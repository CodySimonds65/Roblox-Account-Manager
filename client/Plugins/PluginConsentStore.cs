using System.Text.Json;

namespace RobloxAltClient.Plugins;

public sealed record PluginConsentRecord(
    string PluginId,
    bool Autostart,
    IReadOnlySet<string> GrantedCapabilities,
    DateTime UpdatedUtc);

public sealed class PluginConsentStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, PluginConsentRecord> _records = new(StringComparer.Ordinal);

    public PluginConsentStore(PluginPaths paths)
    {
        _path = paths.ConsentPath;
        Load();
    }

    public PluginConsentRecord Get(string pluginId, bool defaultAutostart = false) =>
        _records.TryGetValue(pluginId, out var record)
            ? record
            : new PluginConsentRecord(pluginId, defaultAutostart, new HashSet<string>(StringComparer.Ordinal), DateTime.UtcNow);

    public void Set(string pluginId, bool autostart, IEnumerable<string> capabilities)
    {
        lock (_gate)
        {
            _records[pluginId] = new PluginConsentRecord(
                pluginId,
                autostart,
                new HashSet<string>(capabilities, StringComparer.Ordinal),
                DateTime.UtcNow);
            SaveLocked();
        }
    }

    /// <summary>
    /// Grants are replaced deliberately. Installation and updates never silently
    /// broaden a plugin's authority; the consent UI must call this with the exact
    /// checked capability set.
    /// </summary>
    public void SetCapabilities(string pluginId, IEnumerable<string> capabilities)
    {
        lock (_gate)
        {
            var existing = Get(pluginId);
            var granted = new HashSet<string>(capabilities, StringComparer.Ordinal);
            _records[pluginId] = existing with { GrantedCapabilities = granted, UpdatedUtc = DateTime.UtcNow };
            SaveLocked();
        }
    }

    public void Remove(string pluginId)
    {
        lock (_gate)
        {
            _records.Remove(pluginId);
            SaveLocked();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var records = JsonSerializer.Deserialize<List<PluginConsentRecord>>(File.ReadAllText(_path), PluginJson.Options);
            if (records is null) return;
            _records = records.ToDictionary(record => record.PluginId, StringComparer.Ordinal);
        }
        catch
        {
            // A corrupt consent file must not prevent the launcher from starting.
            _records = new Dictionary<string, PluginConsentRecord>(StringComparer.Ordinal);
        }
    }

    private void SaveLocked()
    {
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_records.Values.OrderBy(record => record.PluginId), PluginJson.Options));
        File.Move(temporaryPath, _path, overwrite: true);
    }
}
