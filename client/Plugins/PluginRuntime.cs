using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;

namespace RobloxAltClient.Plugins;

public sealed class PluginRuntime : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly PluginPaths _paths;
    private readonly PluginConsentStore _consent;
    private readonly PluginHostService _host;
    private readonly PluginProcessSupervisor _supervisor;
    private readonly PluginInstaller _installer;
    private readonly FocusSafeInputBroker _inputBroker = new();
    private readonly PluginActionRouter _actions;
    private readonly Dictionary<string, InstalledPlugin> _installed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _launchGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Token, int ProcessId, long StartTicks)> _launchTokens = new(StringComparer.Ordinal);
    private readonly object _diagnosticGate = new();
    private readonly Dictionary<string, DiagnosticRateLimit> _diagnosticLimits = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<string, (PluginConnection Connection, string? AccountId)> _accountEventSubscribers = new(StringComparer.Ordinal);
    private readonly GlobalHotkeyMonitor _hotkeyMonitor = new();
    private readonly InputSendInjector _sendInjector = new();
    public ClientEmbeddingService ClientEmbeddings { get; } = new();
    private int _queuedAccountUpdates;
    private const int MaxQueuedAccountUpdates = 64;

    public PluginRuntime(string? appDataDirectory = null)
    {
        _paths = new PluginPaths(appDataDirectory);
        Accounts = new RunningAccountRegistry(_paths.Root);
        _consent = new PluginConsentStore(_paths);
        _host = new PluginHostService();
        _actions = new PluginActionRouter(_host);
        _supervisor = new PluginProcessSupervisor();
        _installer = new PluginInstaller(_paths, _consent, stopPluginAsync: _supervisor.StopAsync,
            signatureVerifier: TryLoadPinnedSignatureVerifier());
        _supervisor.Exited += Supervisor_Exited;
        _host.MessageReceived += Host_MessageReceived;
        _host.Disconnected += Host_Disconnected;
        _host.InputDispatcher = DispatchInputAsync;
        _hotkeyMonitor.KeyDown += (_, vk) => _host.BroadcastHotkey("hotkey.pressed", vk);
        _hotkeyMonitor.KeyUp += (_, vk) => _host.BroadcastHotkey("hotkey.released", vk);
        _hotkeyMonitor.Start();
        Accounts.Diagnostic += Accounts_Diagnostic;
        Accounts.AccountChanged += Accounts_AccountChanged;
        Accounts.AccountExited += Accounts_AccountExited;
        RefreshInstalled();
    }

    public PluginHostService Host => _host;
    public PluginActionRouter Actions => _actions;
    public RunningAccountRegistry Accounts { get; }
    public IReadOnlyList<InstalledPlugin> Installed
    {
        get { lock (_gate) return _installed.Values.OrderBy(plugin => plugin.Manifest.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    public static IReadOnlyList<PluginCatalogEntry> OfficialCatalog { get; } =
    [
        new("io.github.codysimonds65.ram.macros", "RAM Macros", "Portable, background-safe macro recording and playback.", "https://github.com/CodySimonds65/ram-macros/releases/latest/download/"),
        new("io.github.codysimonds65.ram.ocr", "RAM OCR", "Window-relative OCR and color triggers.", "https://github.com/CodySimonds65/ram-ocr/releases/latest/download/"),
        new("io.github.codysimonds65.ram.afk", "RAM AFK", "Staggered background keep-alive for enabled accounts.", "https://github.com/CodySimonds65/ram-afk/releases/latest/download/")
    ];

    public event EventHandler? Changed;
    public event EventHandler<PluginDiagnostic>? Diagnostic;

    public sealed record PluginDiagnostic(string PluginId, string Level, string Message, DateTime Utc);

    internal enum InputDeliveryMode { GuardedReal, BackgroundMessage }

    internal static InputDeliveryMode SelectInputDeliveryMode(bool embedded, bool selectedVisible, bool hostForeground) =>
        embedded && selectedVisible && hostForeground ? InputDeliveryMode.GuardedReal : InputDeliveryMode.BackgroundMessage;

    internal static bool MatchesInputTarget(ManagedAccountSnapshot expected, ManagedAccountSnapshot? current, nint expectedRoot) =>
        current is not null && current.IsRunning &&
        current.ProcessId == expected.ProcessId &&
        current.ProcessStartTimeUtcTicks == expected.ProcessStartTimeUtcTicks &&
        current.RootWindowHandle == expectedRoot;

    private async Task<BackgroundInputResult> DispatchInputAsync(string accountId, IReadOnlyList<PluginInputEvent> events, CancellationToken cancellationToken)
    {
        var account = Accounts.Snapshot().FirstOrDefault(snapshot => string.Equals(snapshot.AccountId, accountId, StringComparison.Ordinal));
        if (account is null) return BackgroundInputResult.Failure("unknown-account", "The managed account is not running.", nint.Zero, nint.Zero);
        var embeddedRoot = ClientEmbeddings.RootFor(accountId);
        var deliveryMode = SelectInputDeliveryMode(
            embeddedRoot is not null && embeddedRoot != nint.Zero,
            ClientEmbeddings.IsVisible(accountId),
            ClientEmbeddings.HostOwnsForeground());
        if (deliveryMode == InputDeliveryMode.GuardedReal)
        {
            var expectedRoot = embeddedRoot!.Value;
            return await _sendInjector.PostAsync(expectedRoot, events, cancellationToken, () =>
            {
                var current = Accounts.Snapshot().FirstOrDefault(snapshot =>
                    string.Equals(snapshot.AccountId, accountId, StringComparison.Ordinal));
                return ClientEmbeddings.IsVisible(accountId) &&
                       ClientEmbeddings.HostOwnsForeground() &&
                       ClientEmbeddings.RootFor(accountId) == expectedRoot &&
                       MatchesInputTarget(account, current, expectedRoot);
            }).ConfigureAwait(false);
        }

        var result = await _inputBroker.PostAsync(account, events, cancellationToken).ConfigureAwait(false);
        if (result.Accepted)
        {
            return result with { Message = result.Message + " Delivered as best-effort background input without changing the foreground window or selected tab." };
        }
        return result;
    }

    public bool IsOfficialUrl(string url) => OfficialCatalog.Any(entry =>
        string.Equals(NormalizeBaseUrl(entry.InstallUrl), NormalizeBaseUrl(url), StringComparison.OrdinalIgnoreCase));

    public async Task<InstalledPlugin> InstallAsync(string url, bool allowUnsignedSideload = false, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var official = IsOfficialUrl(url);
            if (!official && !allowUnsignedSideload)
                throw new InvalidOperationException("This URL is not in the official catalog. Confirm the unsigned sideload warning before continuing.");
            var catalog = official ? OfficialCatalog.First(entry => string.Equals(NormalizeBaseUrl(entry.InstallUrl), NormalizeBaseUrl(url), StringComparison.OrdinalIgnoreCase)) : null;
            var installed = await _installer.InstallFromUrlAsync(url, requireTrustedSignature: official,
                allowUnsignedSideload: !official && allowUnsignedSideload,
                expectedPluginId: catalog?.Id, expectedPublisher: official ? "CodySimonds65" : null, cancellationToken).ConfigureAwait(false);
            if (official)
            {
                if (catalog is null || !string.Equals(installed.Manifest.Id, catalog.Id, StringComparison.Ordinal))
                    throw new InvalidDataException("The official URL returned an unexpected plugin identity.");
            }
            lock (_gate) _installed[installed.Manifest.Id] = installed;
            Changed?.Invoke(this, EventArgs.Empty);
            return installed;
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task<bool> LaunchAsync(string pluginId)
    {
        var launchGate = _launchGates.GetOrAdd(pluginId, _ => new SemaphoreSlim(1, 1));
        await launchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try { return await LaunchCoreAsync(pluginId).ConfigureAwait(false); }
            finally { _lifecycleGate.Release(); }
        }
        finally { launchGate.Release(); }
    }

    private async Task<bool> LaunchCoreAsync(string pluginId)
    {
        InstalledPlugin installed;
        lock (_gate)
        {
            if (!_installed.TryGetValue(pluginId, out installed!)) return false;
            if (installed.IsRunning || _launchTokens.ContainsKey(pluginId))
                throw new InvalidOperationException("This plugin is already running.");
        }

        var consent = _consent.Get(pluginId, installed.Manifest.AutostartDefault);
        var effectiveCapabilities = consent.GrantedCapabilities.Intersect(installed.Manifest.Capabilities, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (!effectiveCapabilities.SetEquals(installed.Manifest.Capabilities))
            throw new InvalidOperationException("This plugin has ungranted capabilities. Review and accept its capability consent first.");

        var manifestPath = Path.Combine(installed.InstallDirectory, "plugin.json");
        var manifestHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath))).ToLowerInvariant();
        var token = _host.CreateLaunchToken(installed.Manifest.Id, manifestHash, effectiveCapabilities);
        int? pid = null;
        long startTicks = 0;
        try
        {
            pid = _supervisor.Start(installed.Manifest, installed.EntryPointPath, _host.PipeName, token, _paths.GetDataDirectory(pluginId));
            using (var process = Process.GetProcessById(pid.Value))
            {
                startTicks = process.StartTime.ToUniversalTime().Ticks;
                _host.BindLaunchProcess(token, pid.Value, startTicks);
            }
            _launchTokens[pluginId] = (token, pid.Value, startTicks);
            _supervisor.Resume(pluginId);
        }
        catch
        {
            _host.RevokeLaunchToken(token);
            _launchTokens.TryRemove(pluginId, out _);
            if (pid is not null) await _supervisor.StopAsync(pluginId).ConfigureAwait(false);
            throw;
        }
        lock (_gate)
        {
            _installed[pluginId] = installed with { IsRunning = true, ProcessId = pid, Autostart = consent.Autostart, GrantedCapabilities = effectiveCapabilities };
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task StopAsync(string pluginId)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync(pluginId).ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StopCoreAsync(string pluginId)
    {
        await _supervisor.StopAsync(pluginId).ConfigureAwait(false);
        lock (_gate)
        {
            if (_installed.TryGetValue(pluginId, out var installed))
                _installed[pluginId] = installed with { IsRunning = false, ProcessId = null };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetAutostart(string pluginId, bool enabled)
    {
        var installed = _installed.TryGetValue(pluginId, out var value) ? value : null;
        if (installed is null) return;
        var existing = _consent.Get(pluginId, installed.Manifest.AutostartDefault);
        _consent.Set(pluginId, enabled, existing.GrantedCapabilities);
        lock (_gate) _installed[pluginId] = installed with { Autostart = enabled };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetCapabilities(string pluginId, IEnumerable<string> capabilities)
    {
        if (!_installed.TryGetValue(pluginId, out var installed)) return;
        var allowed = new HashSet<string>(capabilities, StringComparer.Ordinal);
        if (!allowed.IsSubsetOf(installed.Manifest.Capabilities))
            throw new InvalidOperationException("A capability not declared by the plugin was requested.");
        var existing = _consent.Get(pluginId, installed.Manifest.AutostartDefault);
        _consent.Set(pluginId, existing.Autostart, allowed);
        lock (_gate) _installed[pluginId] = installed with { GrantedCapabilities = allowed };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> RollbackAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _installer.RollbackAsync(pluginId, cancellationToken).ConfigureAwait(false);
            if (result) { RefreshInstalled(); Changed?.Invoke(this, EventArgs.Empty); }
            return result;
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task RemoveAsync(string pluginId)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(pluginId).ConfigureAwait(false);
            if (!_installed.TryGetValue(pluginId, out var installed)) return;
            var directory = Path.GetFullPath(installed.InstallDirectory);
            var root = Path.GetFullPath(_paths.InstallRoot) + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid plugin path.");
            Directory.Delete(directory, recursive: true);
            _consent.Remove(pluginId);
            lock (_gate) _installed.Remove(pluginId);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _lifecycleGate.Release(); }
    }

    private void RefreshInstalled()
    {
        foreach (var directory in Directory.EnumerateDirectories(_paths.InstallRoot))
        {
            var manifestPath = Path.Combine(directory, "plugin.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var manifest = PluginManifestReader.Parse(File.ReadAllText(manifestPath));
                var consent = _consent.Get(manifest.Id, manifest.AutostartDefault);
                var effective = consent.GrantedCapabilities.Intersect(manifest.Capabilities, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
                _installed[manifest.Id] = new InstalledPlugin(manifest, directory, consent.Autostart,
                    effective, false, null, null);
            }
            catch
            {
                // Invalid installed plugins remain visible through diagnostics only; they
                // are never launched automatically.
            }
        }
    }

    private void Supervisor_Exited(object? sender, (string PluginId, int ProcessId, long ProcessStartTimeUtcTicks) e)
    {
        PublishDiagnostic(new PluginDiagnostic(e.PluginId, "error",
            $"Plugin process exited (PID {e.ProcessId}, start {e.ProcessStartTimeUtcTicks}).", DateTime.UtcNow));
        if (_launchTokens.TryGetValue(e.PluginId, out var launch) && launch.ProcessId == e.ProcessId &&
            (e.ProcessStartTimeUtcTicks == 0 || launch.StartTicks == e.ProcessStartTimeUtcTicks) &&
            _launchTokens.TryRemove(new KeyValuePair<string, (string Token, int ProcessId, long StartTicks)>(e.PluginId, launch)))
            _host.RevokeLaunchToken(launch.Token);
        lock (_gate)
        {
            if (_installed.TryGetValue(e.PluginId, out var installed) && installed.ProcessId == e.ProcessId)
                _installed[e.PluginId] = installed with { IsRunning = false, ProcessId = null, LastError = "Plugin process exited." };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Host_MessageReceived(object? sender, (PluginConnection Connection, PluginEnvelope Envelope) message)
    {
        if (message.Envelope.Type is "accounts.list" or "account.snapshot")
            _ = RespondToAccountQueryAsync(message.Connection, message.Envelope);
        else if (message.Envelope.Type == "account.events.subscribe")
            HandleAccountEventsSubscribe(message.Connection, message.Envelope);
        else if (message.Envelope.Type == "diagnostic.log")
            HandlePluginDiagnostic(message.Connection, message.Envelope);
    }

    private void HandleAccountEventsSubscribe(PluginConnection connection, PluginEnvelope envelope)
    {
        string? accountId = null;
        try
        {
            if (envelope.Payload.TryGetProperty("accountId", out var accountIdElement))
            {
                if (accountIdElement.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("account.events.subscribe account id must be a string.");
                accountId = accountIdElement.GetString();
            }
            if (accountId is not null && accountId.Length > 200)
                throw new InvalidDataException("account.events.subscribe account id is invalid.");
            _accountEventSubscribers[connection.PluginId] = (connection, accountId);
            _ = SendSubscribeReplyAsync(connection, "account.events.subscribed", new { }, envelope.RequestId);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            _ = SendSubscribeReplyAsync(connection, "host.reject",
                new { reason = "invalid-request", messageType = "account.events.subscribe", detail = ex.Message },
                envelope.RequestId);
        }
    }

    private async Task SendSubscribeReplyAsync(PluginConnection connection, string type, object payload, string requestId)
    {
        try
        {
            await connection.SendAsync(type, payload, requestId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or JsonException)
        {
            // Disconnects are expected during shutdown; there is no response to send.
        }
    }

    private void Host_Disconnected(object? sender, PluginConnection connection) =>
        _accountEventSubscribers.TryRemove(connection.PluginId, out _);

    private void Accounts_AccountChanged(object? sender, ManagedAccountSnapshot snapshot) =>
        BroadcastAccountEvent("account.updated", snapshot);

    private void Accounts_AccountExited(object? sender, ManagedAccountSnapshot snapshot) =>
        BroadcastAccountEvent("account.exited", snapshot);

    private void BroadcastAccountEvent(string type, ManagedAccountSnapshot snapshot)
    {
        if (_accountEventSubscribers.IsEmpty) return;
        bool isUpdate = type == "account.updated";
        if (isUpdate && Volatile.Read(ref _queuedAccountUpdates) > MaxQueuedAccountUpdates)
            return;
        foreach (var subscriber in _accountEventSubscribers.Values.ToArray())
        {
            if (subscriber.AccountId is not null &&
                !string.Equals(subscriber.AccountId, snapshot.AccountId, StringComparison.Ordinal))
                continue;
            if (isUpdate)
                Interlocked.Increment(ref _queuedAccountUpdates);
            _ = SendAccountEventAsync(subscriber.Connection, type, snapshot, isUpdate);
        }
    }

    private async Task SendAccountEventAsync(PluginConnection connection, string type, ManagedAccountSnapshot snapshot, bool isUpdate)
    {
        try
        {
            await connection.SendAsync(type, new { account = snapshot }, "", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            if (_accountEventSubscribers.TryGetValue(connection.PluginId, out var current) &&
                ReferenceEquals(current.Connection, connection))
                _accountEventSubscribers.TryRemove(connection.PluginId, out _);
        }
        finally
        {
            if (isUpdate)
                Interlocked.Decrement(ref _queuedAccountUpdates);
        }
    }

    private void HandlePluginDiagnostic(PluginConnection connection, PluginEnvelope envelope)
    {
        try
        {
            var request = envelope.Payload.Deserialize<DiagnosticLogRequest>(PluginJson.Options)
                          ?? throw new InvalidDataException("Diagnostic payload is invalid.");
            if (request.Message is null || request.Message.Length is < 1 or > 2_000)
                throw new InvalidDataException("Diagnostic message length is invalid.");
            var level = request.Level?.Trim().ToLowerInvariant() ?? "info";
            if (level is not ("trace" or "info" or "warning" or "error")) level = "info";
            if (!TryAcceptDiagnostic(connection.PluginId, out var emitRateLimitWarning))
            {
                if (emitRateLimitWarning)
                    PublishDiagnostic(new PluginDiagnostic(connection.PluginId, "warning",
                        "Diagnostic messages are being rate-limited (maximum 30 messages per 10 seconds).", DateTime.UtcNow));
                return;
            }
            PublishDiagnostic(new PluginDiagnostic(connection.PluginId, level, request.Message, request.Utc ?? DateTime.UtcNow));
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            if (TryAcceptDiagnostic(connection.PluginId, out var emitRateLimitWarning))
                PublishDiagnostic(new PluginDiagnostic(connection.PluginId, "warning", $"Rejected diagnostic message: {ex.Message}", DateTime.UtcNow));
            else if (emitRateLimitWarning)
                PublishDiagnostic(new PluginDiagnostic(connection.PluginId, "warning",
                    "Diagnostic messages are being rate-limited (maximum 30 messages per 10 seconds).", DateTime.UtcNow));
        }
    }

    private void PublishDiagnostic(PluginDiagnostic diagnostic)
    {
        var handlers = Diagnostic?.GetInvocationList();
        if (handlers is null) return;
        foreach (EventHandler<PluginDiagnostic> handler in handlers)
        {
            try { handler(this, diagnostic); }
            catch { /* A diagnostic subscriber must never disrupt plugin lifecycle work. */ }
        }
    }

    private bool TryAcceptDiagnostic(string pluginId, out bool emitRateLimitWarning)
    {
        var now = DateTime.UtcNow;
        lock (_diagnosticGate)
        {
            if (!_diagnosticLimits.TryGetValue(pluginId, out var limit) || now - limit.WindowStart >= TimeSpan.FromSeconds(10))
            {
                limit = new DiagnosticRateLimit(now);
                _diagnosticLimits[pluginId] = limit;
            }

            if (limit.Count >= 30)
            {
                emitRateLimitWarning = !limit.WarningEmitted;
                limit.WarningEmitted = true;
                return false;
            }

            limit.Count++;
            emitRateLimitWarning = false;
            return true;
        }
    }

    private sealed class DiagnosticRateLimit(DateTime windowStart)
    {
        public DateTime WindowStart { get; } = windowStart;
        public int Count { get; set; }
        public bool WarningEmitted { get; set; }
    }

    private void Accounts_Diagnostic(object? sender, string message) =>
        PublishDiagnostic(new PluginDiagnostic("host.accounts", "error", message, DateTime.UtcNow));

    private async Task RespondToAccountQueryAsync(PluginConnection connection, PluginEnvelope envelope)
    {
        try
        {
            IReadOnlyList<ManagedAccountSnapshot> accounts;
            if (envelope.Type == "accounts.list")
            {
                accounts = Accounts.Snapshot();
            }
            else
            {
                var accountId = envelope.Payload.TryGetProperty("accountId", out var id) ? id.GetString() : null;
                accounts = string.IsNullOrWhiteSpace(accountId)
                    ? []
                    : Accounts.Snapshot().Where(account => string.Equals(account.AccountId, accountId, StringComparison.Ordinal)).ToArray();
            }
            await connection.SendAsync("accounts.result", new { accounts }, envelope.RequestId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Disconnects are expected during shutdown; there is no response to send.
        }
    }

    private static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value.TrimEnd('/');
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static IPluginPackageSignatureVerifier? TryLoadPinnedSignatureVerifier()
    {
        // This value is the release trust anchor. Rotate it only as part of a
        // signed launcher release; user/environment configuration is intentionally
        // not consulted for official package verification.
        const string pinnedPublicKey = "kHdvM/oqWovIr54z9a8xLitNemH9J+zIMwUalm0cTmw=";
        return new PinnedEd25519PackageSignatureVerifier(Convert.FromBase64String(pinnedPublicKey));
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { await _supervisor.StopAllAsync().ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
        _supervisor.Exited -= Supervisor_Exited;
        _host.MessageReceived -= Host_MessageReceived;
        _host.Disconnected -= Host_Disconnected;
        Accounts.Diagnostic -= Accounts_Diagnostic;
        Accounts.AccountChanged -= Accounts_AccountChanged;
        Accounts.AccountExited -= Accounts_AccountExited;
        _supervisor.Dispose();
        _hotkeyMonitor.Dispose();
        await _actions.DisposeAsync().ConfigureAwait(false);
        await _host.DisposeAsync().ConfigureAwait(false);
        Accounts.Dispose();
        _lifecycleGate.Dispose();
    }
}

internal sealed record DiagnosticLogRequest(string? Level, string? Message, DateTime? Utc);

public sealed record PluginCatalogEntry(string Id, string Name, string Description, string InstallUrl);
