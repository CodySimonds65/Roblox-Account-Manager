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

    public PluginRuntime()
    {
        _paths = new PluginPaths();
        Accounts = new RunningAccountRegistry(_paths.Root);
        _consent = new PluginConsentStore(_paths);
        _host = new PluginHostService();
        _actions = new PluginActionRouter(_host);
        _supervisor = new PluginProcessSupervisor();
        _installer = new PluginInstaller(_paths, _consent, stopPluginAsync: _supervisor.StopAsync,
            signatureVerifier: TryLoadPinnedSignatureVerifier());
        _supervisor.Exited += Supervisor_Exited;
        _host.InputDispatcher = DispatchInputAsync;
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

    private Task<BackgroundInputResult> DispatchInputAsync(string accountId, IReadOnlyList<PluginInputEvent> events, CancellationToken cancellationToken)
    {
        var account = Accounts.Snapshot().FirstOrDefault(snapshot => string.Equals(snapshot.AccountId, accountId, StringComparison.Ordinal));
        if (account is null) return Task.FromResult(BackgroundInputResult.Failure("unknown-account", "The managed account is not running.", nint.Zero, nint.Zero));
        return Task.FromResult(_inputBroker.Post(account, events));
    }

    public bool IsOfficialUrl(string url) => OfficialCatalog.Any(entry =>
        string.Equals(NormalizeBaseUrl(entry.InstallUrl), NormalizeBaseUrl(url), StringComparison.OrdinalIgnoreCase));

    public async Task<InstalledPlugin> InstallAsync(string url, bool allowUnsignedSideload = false, CancellationToken cancellationToken = default)
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

    public async Task<bool> LaunchAsync(string pluginId)
    {
        var launchGate = _launchGates.GetOrAdd(pluginId, _ => new SemaphoreSlim(1, 1));
        await launchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await LaunchCoreAsync(pluginId).ConfigureAwait(false);
        }
        finally { launchGate.Release(); }
    }

    private async Task<bool> LaunchCoreAsync(string pluginId)
    {
        InstalledPlugin installed;
        lock (_gate)
        {
            if (!_installed.TryGetValue(pluginId, out installed!)) return false;
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
        var result = await _installer.RollbackAsync(pluginId, cancellationToken).ConfigureAwait(false);
        if (result) { RefreshInstalled(); Changed?.Invoke(this, EventArgs.Empty); }
        return result;
    }

    public async Task RemoveAsync(string pluginId)
    {
        await StopAsync(pluginId).ConfigureAwait(false);
        if (!_installed.TryGetValue(pluginId, out var installed)) return;
        var directory = Path.GetFullPath(installed.InstallDirectory);
        var root = Path.GetFullPath(_paths.InstallRoot) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid plugin path.");
        Directory.Delete(directory, recursive: true);
        _consent.Remove(pluginId);
        lock (_gate) _installed.Remove(pluginId);
        Changed?.Invoke(this, EventArgs.Empty);
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
        const string pinnedPublicKey = "nHHGpVihLHHzE/R2MgWKb8YO291k+M7VMxKsLSVghGE=";
        return new PinnedEd25519PackageSignatureVerifier(Convert.FromBase64String(pinnedPublicKey));
    }

    public async ValueTask DisposeAsync()
    {
        await _supervisor.StopAllAsync().ConfigureAwait(false);
        _supervisor.Exited -= Supervisor_Exited;
        _supervisor.Dispose();
        await _actions.DisposeAsync().ConfigureAwait(false);
        await _host.DisposeAsync().ConfigureAwait(false);
        Accounts.Dispose();
    }
}

public sealed record PluginCatalogEntry(string Id, string Name, string Description, string InstallUrl);
