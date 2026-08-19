using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using RobloxAccountManager.Core.Capabilities;
using RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Platform.MacOS;

/// <summary>
/// macOS plugin lifecycle host. Plugins are installed from a user-confirmed local directory,
/// filtered to the current osx RID, and started only after capability consent. The process is
/// authenticated over the existing owner-only Unix transport before it is exposed as running.
/// </summary>
public sealed class MacPluginHostFacade : IPluginHostFacade, IAsyncDisposable
{
    private static readonly Regex IdPattern = new("^[a-z0-9]+(\\.[a-z0-9-]+)+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CapabilityPattern = new("^[a-z0-9]+(\\.[a-z0-9-]+)+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SupportedCapabilities =
    [
        "host.accounts.read",
        "host.events.account-lifecycle"
    ];

    private readonly string _root;
    private readonly string _consentPath;
    private readonly MacPluginProcessSupervisor _supervisor = new();
    private readonly ConcurrentDictionary<string, RunningPlugin> _running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private Func<IReadOnlyList<PluginAccountSnapshot>> _accountSnapshotProvider = static () => Array.Empty<PluginAccountSnapshot>();

    public MacPluginHostFacade(string? root = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RobloxAccountManager", "Plugins"));
        _consentPath = Path.Combine(_root, "plugin-consent.json");
    }

    public bool IsAvailable => OperatingSystem.IsMacOS();

    public void SetAccountSnapshotProvider(Func<IReadOnlyList<PluginAccountSnapshot>> provider) =>
        _accountSnapshotProvider = provider ?? throw new ArgumentNullException(nameof(provider));

    public IReadOnlyList<PluginCapabilityResult> Capabilities =>
    [
        new("host.accounts.read", CapabilityStatus.Supported),
        new("host.events.account-lifecycle", CapabilityStatus.Supported),
        new("host.input.foreground.real", CapabilityStatus.Unsupported, "platform-not-supported"),
        new("host.input.background", CapabilityStatus.Unsupported, "platform-not-supported"),
        new("host.input.background.messages", CapabilityStatus.Unsupported, "platform-not-supported"),
        new("system.read-screen", CapabilityStatus.Unsupported, "platform-not-supported"),
        new("system.watch-global-input", CapabilityStatus.Unsupported, "platform-not-supported")
    ];

    public ValueTask<IReadOnlyList<string>> GetInstalledPluginIdsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root)) return ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        var ids = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(directory).StartsWith(".staging-", StringComparison.Ordinal)) continue;
            var descriptor = TryReadDescriptor(directory, out _);
            if (descriptor is not null) ids.Add(descriptor.Id);
        }

        return ValueTask.FromResult<IReadOnlyList<string>>(ids.Order(StringComparer.Ordinal).ToArray());
    }

    public ValueTask<IReadOnlyList<string>> GetRunningPluginIdsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<string>>(_running.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    public ValueTask<IReadOnlyList<string>> GetRequestedCapabilitiesAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IdPattern.IsMatch(pluginId)) return ValueTask.FromResult<IReadOnlyList<string>>([]);
        var directory = PathSafety.RequireContainedPath(_root, Path.Combine(_root, pluginId));
        var descriptor = TryReadDescriptor(directory, out _);
        return ValueTask.FromResult<IReadOnlyList<string>>(descriptor?.RequestedCapabilities ?? []);
    }

    public async ValueTask<PluginInstallResult> InstallFromDirectoryAsync(
        string sourceDirectory,
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return PluginInstallResult.Rejected("platform-not-supported");
        if (!userConfirmed) return PluginInstallResult.Rejected("confirmation-required");
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            return PluginInstallResult.Rejected("plugin-directory-not-found");

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PathSafety.EnsureOwnerOnlyDirectory(_root);
            var fullSource = Path.GetFullPath(sourceDirectory);
            PathSafety.RejectSymlinkDirectory(fullSource);
            var descriptor = TryReadDescriptor(fullSource, out var descriptorError);
            if (descriptor is null) return PluginInstallResult.Rejected(descriptorError ?? "plugin-manifest-invalid");
            if (_running.ContainsKey(descriptor.Id)) return PluginInstallResult.Rejected("plugin-running");

            var destination = PathSafety.RequireContainedPath(_root, Path.Combine(_root, descriptor.Id));
            if (Directory.Exists(destination)) return PluginInstallResult.Rejected("plugin-already-installed");
            var staging = PathSafety.RequireContainedPath(_root, Path.Combine(_root, $".staging-{Guid.NewGuid():N}"));
            try
            {
                PathSafety.EnsureOwnerOnlyDirectory(staging);
                CopyDirectorySafely(fullSource, staging, cancellationToken);
                var copied = TryReadDescriptor(staging, out var copiedError);
                if (copied is null || !string.Equals(copied.Id, descriptor.Id, StringComparison.Ordinal))
                    return PluginInstallResult.Rejected(copiedError ?? "plugin-manifest-changed");
                Directory.Move(staging, destination);
                return PluginInstallResult.Success(descriptor.Id);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    try { Directory.Delete(staging, recursive: true); } catch { }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return PluginInstallResult.Rejected("plugin-install-failed");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask<PluginLifecycleResult> StartAsync(
        string pluginId,
        bool userConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return PluginLifecycleResult.Rejected("platform-not-supported");
        if (!IdPattern.IsMatch(pluginId)) return PluginLifecycleResult.Rejected("plugin-id-invalid");
        if (!userConfirmed) return PluginLifecycleResult.Rejected("capability-consent-required");
        if (_running.ContainsKey(pluginId)) return PluginLifecycleResult.Rejected("plugin-already-running");

        var directory = PathSafety.RequireContainedPath(_root, Path.Combine(_root, pluginId));
        var descriptor = TryReadDescriptor(directory, out var descriptorError);
        if (descriptor is null) return PluginLifecycleResult.Rejected(descriptorError ?? "plugin-manifest-invalid");
        if (descriptor.RequestedCapabilities.Any(capability => !SupportedCapabilities.Contains(capability)))
            return PluginLifecycleResult.Rejected("capability-not-supported-on-macos");

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_running.ContainsKey(pluginId)) return PluginLifecycleResult.Rejected("plugin-already-running");
            await SaveConsentAsync(descriptor, cancellationToken).ConfigureAwait(false);

            var transport = new MacUnixPluginTransport();
            await transport.StartAsync(cancellationToken).ConfigureAwait(false);
            var token = MacUnixPluginTransport.CreateAuthenticationToken();
            MacPluginProcess? process = null;
            try
            {
                process = await _supervisor.StartAsync(
                    descriptor.EntryPointPath,
                    ["--ram-plugin-endpoint", transport.SocketPath, "--ram-plugin-token", token],
                    cancellationToken).ConfigureAwait(false);
                transport.ExpectConnection(
                    descriptor.Id,
                    process.Identity.ProcessId,
                    process.Identity.StartTime,
                    descriptor.ManifestSha256,
                    descriptor.RequestedCapabilities,
                    token);
                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var socket = await transport.AcceptAuthenticatedAsync(handshakeTimeout.Token).ConfigureAwait(false);
                var running = new RunningPlugin(
                    descriptor.Id,
                    process,
                    transport,
                    socket,
                    descriptor.RequestedCapabilities.ToHashSet(StringComparer.Ordinal));
                if (!_running.TryAdd(descriptor.Id, running))
                {
                    socket.Dispose();
                    await transport.DisposeAsync().ConfigureAwait(false);
                    await _supervisor.TerminateAsync(process).ConfigureAwait(false);
                    return PluginLifecycleResult.Rejected("plugin-already-running");
                }
                _ = RunPluginLoopAsync(running);
                _ = WatchExitAsync(running);
                return PluginLifecycleResult.Success();
            }
            catch
            {
                if (process is not null) await _supervisor.TerminateAsync(process, CancellationToken.None).ConfigureAwait(false);
                await transport.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PluginLifecycleResult.Rejected("canceled");
        }
        catch (OperationCanceledException)
        {
            return PluginLifecycleResult.Rejected("plugin-start-timeout");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            return PluginLifecycleResult.Rejected("plugin-start-failed");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask<PluginLifecycleResult> StopAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_running.TryGetValue(pluginId, out var running)) return PluginLifecycleResult.Rejected("plugin-not-running");
        running.Socket.Dispose();
        var terminated = await _supervisor.TerminateAsync(running.Process, cancellationToken).ConfigureAwait(false);
        if (!terminated && !running.Process.Process.HasExited)
            return PluginLifecycleResult.Rejected("plugin-stop-failed");

        _running.TryRemove(new KeyValuePair<string, RunningPlugin>(pluginId, running));
        await running.Transport.DisposeAsync().ConfigureAwait(false);
        return PluginLifecycleResult.Success();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pluginId in _running.Keys.ToArray())
        {
            try { await StopAsync(pluginId).ConfigureAwait(false); } catch { }
        }

        _lifecycleGate.Dispose();
    }

    private async Task WatchExitAsync(RunningPlugin running)
    {
        try { await running.Process.Process.WaitForExitAsync().ConfigureAwait(false); } catch { }
        if (_running.TryRemove(new KeyValuePair<string, RunningPlugin>(running.PluginId, running)))
        {
            running.Socket.Dispose();
            await running.Transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunPluginLoopAsync(RunningPlugin running)
    {
        var subscribed = false;
        var lastSnapshot = string.Empty;
        try
        {
            while (!running.Process.Process.HasExited)
            {
                MacPluginFrame frame;
                try
                {
                    using var receiveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    frame = await MacUnixPluginTransport.ReceiveFrameAsync(running.Socket, receiveTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!running.Process.Process.HasExited)
                {
                    if (subscribed)
                    {
                        var current = GetAccountSnapshots();
                        var serialized = JsonSerializer.Serialize(current);
                        if (!string.Equals(serialized, lastSnapshot, StringComparison.Ordinal))
                        {
                            await SendFrameAsync(
                                running,
                                new MacPluginFrame("account.events.changed", JsonSerializer.SerializeToElement(current))).ConfigureAwait(false);
                            lastSnapshot = serialized;
                        }
                    }

                    continue;
                }

                if (frame.Type is "accounts.list" or "account.snapshot")
                {
                    if (!running.GrantedCapabilities.Contains("host.accounts.read"))
                    {
                        await SendFrameAsync(running, new MacPluginFrame("error", JsonSerializer.SerializeToElement(new { code = "capability-denied" }))).ConfigureAwait(false);
                        continue;
                    }

                    var snapshot = GetAccountSnapshots();
                    lastSnapshot = JsonSerializer.Serialize(snapshot);
                    await SendFrameAsync(
                        running,
                        new MacPluginFrame("accounts.snapshot", JsonSerializer.SerializeToElement(snapshot))).ConfigureAwait(false);
                }
                else if (frame.Type == "account.events.subscribe")
                {
                    if (!running.GrantedCapabilities.Contains("host.events.account-lifecycle"))
                    {
                        await SendFrameAsync(running, new MacPluginFrame("error", JsonSerializer.SerializeToElement(new { code = "capability-denied" }))).ConfigureAwait(false);
                        continue;
                    }

                    subscribed = true;
                    var snapshot = GetAccountSnapshots();
                    lastSnapshot = JsonSerializer.Serialize(snapshot);
                    await SendFrameAsync(
                        running,
                        new MacPluginFrame("account.events.subscribed", JsonSerializer.SerializeToElement(snapshot))).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidDataException or JsonException or System.Net.Sockets.SocketException)
        {
            // A plugin disconnect is handled by the verified process watcher. No exception or
            // authentication material is surfaced to activity logs.
        }
    }

    private static async Task SendFrameAsync(RunningPlugin running, MacPluginFrame frame)
    {
        await running.SendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await MacUnixPluginTransport.SendFrameAsync(running.Socket, frame).ConfigureAwait(false);
        }
        finally
        {
            running.SendGate.Release();
        }
    }

    private IReadOnlyList<PluginAccountSnapshot> GetAccountSnapshots()
    {
        try { return _accountSnapshotProvider().ToArray(); }
        catch { return Array.Empty<PluginAccountSnapshot>(); }
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"plugin-{name}-missing");

    private static int RequiredInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : throw new InvalidDataException($"plugin-{name}-missing");

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.Contains('\\')
            || path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("plugin-entrypoint-path-invalid");
    }

    private sealed record MacPluginDescriptor(
        string Id,
        string EntryPointPath,
        string ManifestSha256,
        IReadOnlyList<string> RequestedCapabilities);

    private sealed record RunningPlugin(
        string PluginId,
        MacPluginProcess Process,
        MacUnixPluginTransport Transport,
        System.Net.Sockets.Socket Socket,
        IReadOnlySet<string> GrantedCapabilities)
    {
        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }
    private async Task SaveConsentAsync(MacPluginDescriptor descriptor, CancellationToken cancellationToken)
    {
        PathSafety.EnsureOwnerOnlyDirectory(_root);
        if (File.Exists(_consentPath)) PathSafety.RejectSymlink(_consentPath);
        var consent = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (File.Exists(_consentPath))
        {
            try
            {
                consent = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
                    await File.ReadAllBytesAsync(_consentPath, cancellationToken).ConfigureAwait(false))
                    ?? consent;
            }
            catch (JsonException)
            {
                // A corrupt consent record cannot grant access; replace it only after the user
                // has explicitly confirmed this plugin's current manifest.
            }
        }

        consent[descriptor.Id] = descriptor.RequestedCapabilities.ToArray();
        var temporary = _consentPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(consent), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _consentPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void CopyDirectorySafely(string source, string destination, CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathSafety.RejectSymlinkDirectory(directory);
            var relative = Path.GetRelativePath(source, directory);
            var target = PathSafety.RequireContainedPath(destination, Path.Combine(destination, relative));
            Directory.CreateDirectory(target);
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PathSafety.RejectSymlink(file);
            var relative = Path.GetRelativePath(source, file);
            var target = PathSafety.RequireContainedPath(destination, Path.Combine(destination, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static MacPluginDescriptor? TryReadDescriptor(string directory, out string? error)
    {
        error = null;
        try
        {
            var manifestPath = Path.Combine(directory, "plugin.json");
            if (!File.Exists(manifestPath)) { error = "plugin-manifest-missing"; return null; }
            PathSafety.RejectSymlinkComponents(manifestPath);
            PathSafety.RejectSymlink(manifestPath);
            var bytes = File.ReadAllBytes(manifestPath);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            var root = document.RootElement;
            var schema = RequiredInt(root, "schemaVersion");
            if (schema is not (1 or 2)) throw new InvalidDataException("plugin-schema-unsupported");
            var id = RequiredString(root, "id");
            if (!IdPattern.IsMatch(id)) throw new InvalidDataException("plugin-id-invalid");
            var capabilities = root.TryGetProperty("capabilities", out var capabilityElement) && capabilityElement.ValueKind == JsonValueKind.Array
                ? capabilityElement.EnumerateArray().Select(element => element.GetString() ?? string.Empty).Distinct(StringComparer.Ordinal).ToArray()
                : throw new InvalidDataException("plugin-capabilities-missing");
            if (capabilities.Any(capability => capability.Length > 128 || !CapabilityPattern.IsMatch(capability)))
                throw new InvalidDataException("plugin-capability-invalid");

            if (schema == 1) throw new InvalidDataException("plugin-rid-not-available");
            var rid = MacPkgUpdateInstaller.GetCurrentRid();
            var relativeEntryPoint = root.TryGetProperty("entryPoints", out var entryPoints)
                    && entryPoints.ValueKind == JsonValueKind.Object
                    && entryPoints.TryGetProperty(rid, out var ridEntry)
                    && ridEntry.ValueKind == JsonValueKind.String
                    ? ridEntry.GetString()!
                    : throw new InvalidDataException("plugin-rid-not-available");
            ValidateRelativePath(relativeEntryPoint);
            var entryPath = PathSafety.RequireContainedPath(directory, Path.Combine(directory, relativeEntryPoint));
            PathSafety.RejectSymlinkComponents(entryPath);
            PathSafety.RejectSymlink(entryPath);
            if (!File.Exists(entryPath)) throw new FileNotFoundException("plugin-entrypoint-missing");
            return new MacPluginDescriptor(id, entryPath, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), capabilities);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            error = exception.Message;
            return null;
        }
    }

}
