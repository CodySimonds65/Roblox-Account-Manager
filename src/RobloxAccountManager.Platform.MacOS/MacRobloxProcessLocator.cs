using System.Diagnostics;
using System.Text.Json;

namespace RobloxAccountManager.Platform.MacOS;

public interface IRobloxProcessLocator
{
    RobloxLaunchSnapshot CaptureSnapshot();

    RobloxProcessInfo? FindProcess(int processId);

    bool IsSameProcess(RobloxProcessIdentity expected, RobloxProcessInfo actual);
}

/// <summary>
/// Process discovery is intentionally conservative. If start time or executable identity cannot
/// be read, the candidate is not considered stable and cannot satisfy a launch verification.
/// </summary>
public sealed class MacRobloxProcessLocator : IRobloxProcessLocator
{
    private static readonly string[] CandidateNames =
    [
        "RobloxPlayer",
        "RobloxPlayerBeta",
        "Roblox"
    ];

    private readonly MacManagedProcessRegistry _registry;

    public MacRobloxProcessLocator(MacManagedProcessRegistry? registry = null)
    {
        _registry = registry ?? new MacManagedProcessRegistry();
    }

    public RobloxLaunchSnapshot CaptureSnapshot()
    {
        var processes = new List<RobloxProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!IsCandidateProcess(process))
                {
                    continue;
                }

                var info = CreateInfo(process);
                if (info is not null)
                {
                    processes.Add(info);
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        return new RobloxLaunchSnapshot(DateTimeOffset.UtcNow, processes);
    }

    public RobloxProcessInfo? FindProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return IsCandidateProcess(process) ? CreateInfo(process) : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public bool IsSameProcess(RobloxProcessIdentity expected, RobloxProcessInfo actual) =>
        expected.Matches(actual.Identity) && actual.IsStable;

    public void RegisterManaged(RobloxProcessIdentity identity, string? accountId = null) =>
        _registry.Register(identity, accountId);

    public string? GetManagedAccountId(RobloxProcessIdentity identity) =>
        _registry.GetAccountId(identity);

    private static bool IsCandidateProcess(Process process)
    {
        var name = process.ProcessName;
        return CandidateNames.Any(candidate => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
            || name.Contains("RobloxPlayer", StringComparison.OrdinalIgnoreCase);
    }

    private RobloxProcessInfo? CreateInfo(Process process)
    {
        try
        {
            var executablePath = process.MainModule?.FileName;
            var startTime = process.StartTime;
            if (string.IsNullOrWhiteSpace(executablePath) || startTime == default)
            {
                return null;
            }

            var fullExecutablePath = Path.GetFullPath(executablePath);
            var bundlePath = FindBundlePath(fullExecutablePath);
            var identity = new RobloxProcessIdentity(
                process.Id,
                new DateTimeOffset(startTime.ToUniversalTime()),
                fullExecutablePath,
                bundlePath);
            return new RobloxProcessInfo(identity, process.ProcessName, _registry.IsRegistered(identity), true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static string FindBundlePath(string executablePath)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(executablePath) ?? string.Empty);
        while (current is not null)
        {
            if (current.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return string.Empty;
    }
}

/// <summary>
/// Only identities explicitly registered after successful launch verification are managed.
/// Discovering a Roblox process is never enough to authorize closing or rebuilding its runtime.
/// </summary>
public sealed class MacManagedProcessRegistry
{
    private readonly string _path;
    private readonly object _gate = new();

    public MacManagedProcessRegistry(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "RobloxAccountManager", "managed-processes.json");
    }

    public IReadOnlyList<RobloxProcessIdentity> Read() =>
        ReadRegistrations().Select(registration => registration.Identity).ToArray();

    public string? GetAccountId(RobloxProcessIdentity identity) =>
        ReadRegistrations()
            .FirstOrDefault(registration => registration.Identity.Matches(identity))
            ?.AccountId;

    private IReadOnlyList<MacManagedProcessRegistration> ReadRegistrations()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    PathSafety.RejectSymlinkComponents(_path);
                    PathSafety.RejectSymlink(_path);
                }

                if (!File.Exists(_path))
                {
                    return Array.Empty<MacManagedProcessRegistration>();
                }

                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_path));
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return Array.Empty<MacManagedProcessRegistration>();

                var registrations = new List<MacManagedProcessRegistration>();
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
                        continue;

                    RobloxProcessIdentity? identity;
                    string? accountId = null;
                    if (element.TryGetProperty(nameof(MacManagedProcessRegistration.Identity), out var identityElement))
                    {
                        identity = identityElement.Deserialize<RobloxProcessIdentity>();
                        if (element.TryGetProperty(nameof(MacManagedProcessRegistration.AccountId), out var accountElement)
                            && accountElement.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            accountId = accountElement.GetString();
                        }
                    }
                    else
                    {
                        // v1 stored a bare identity array. Retain authorization while
                        // leaving the account unbound until the next verified launch.
                        identity = element.Deserialize<RobloxProcessIdentity>();
                    }

                    if (identity is not null && identity.ProcessId > 0 && identity.HasStableStartTime
                        && !string.IsNullOrWhiteSpace(identity.ExecutablePath)
                        && !string.IsNullOrWhiteSpace(identity.BundlePath))
                    {
                        registrations.Add(new MacManagedProcessRegistration(accountId, identity));
                    }
                }

                return registrations;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return Array.Empty<MacManagedProcessRegistration>();
            }
        }
    }

    public bool IsRegistered(RobloxProcessIdentity identity) => Read().Any(existing => existing.Matches(identity));

    public void Register(RobloxProcessIdentity identity, string? accountId = null)
    {
        lock (_gate)
        {
            var values = ReadRegistrations()
                .Where(existing => existing.Identity.ProcessId != identity.ProcessId
                    && (string.IsNullOrWhiteSpace(accountId)
                        || !string.Equals(existing.AccountId, accountId, StringComparison.Ordinal)))
                .Append(new MacManagedProcessRegistration(
                    string.IsNullOrWhiteSpace(accountId) ? null : accountId,
                    identity))
                .ToList();
            Write(values);
        }
    }

    public void Unregister(RobloxProcessIdentity identity)
    {
        lock (_gate)
        {
            var values = ReadRegistrations().Where(existing => !existing.Identity.Matches(identity)).ToList();
            if (File.Exists(_path))
            {
                PathSafety.RejectSymlinkComponents(_path);
                PathSafety.RejectSymlink(_path);
            }
            if (!values.Any())
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }

                return;
            }

            Write(values);
        }
    }

    private void Write(IReadOnlyList<MacManagedProcessRegistration> values)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Registry path has no parent.");
        PathSafety.EnsureOwnerOnlyDirectory(directory);
        PathSafety.RejectSymlinkComponents(_path);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(values));
        PathSafety.RejectSymlinkComponents(_path);
        File.Move(temporary, _path, overwrite: true);
    }

    private sealed record MacManagedProcessRegistration(string? AccountId, RobloxProcessIdentity Identity);
}
