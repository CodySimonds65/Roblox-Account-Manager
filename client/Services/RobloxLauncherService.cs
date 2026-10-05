using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;

namespace RobloxAltClient.Services;

public sealed class RobloxLauncherService
{
    private static readonly HttpClient VersionClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    internal static bool ShouldUseNativePlayer(string preference, bool nativeProtocolHandler) =>
        string.Equals(preference, "Standard", StringComparison.OrdinalIgnoreCase) ||
        (string.Equals(preference, "Auto", StringComparison.OrdinalIgnoreCase) && nativeProtocolHandler);

    internal static async Task<string?> ResolveNativePlayerAsync(
        string channel,
        Func<string, CancellationToken, Task<string>> queryVersion,
        Func<string, string?> findVersion,
        Func<string?> findFallback,
        Func<bool> hasRunningClients,
        Action<string>? diagnostic,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? upload = null;
        try
        {
            upload = await queryVersion(channel, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hasRunningClients())
                throw new InvalidOperationException("Could not verify this account's Roblox build. Launch paused to avoid starting an updater while other clients are running. Retry this account when the version check is available.");
            diagnostic?.Invoke("Could not verify Roblox's current build; the first client may run its normal updater.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (upload is not null)
        {
            var exactPlayer = findVersion(upload);
            if (exactPlayer is not null) return exactPlayer;
            if (hasRunningClients())
                throw new InvalidOperationException($"Roblox build {upload} for channel {DisplayChannel(channel)} is not installed. Launch paused to avoid interrupting running clients. Let those games finish, then launch this account first so Roblox can update.");
            diagnostic?.Invoke($"Roblox build {upload} is not installed; the first client may run its normal update.");
        }
        return findFallback();
    }

    public async Task StartAsync(string launchUri, string preference, Action<string>? diagnostic = null,
        CancellationToken cancellationToken = default, Func<string, Task>? prepareNativeClient = null)
    {
        var bloxstrap = string.Equals(preference, "Bloxstrap", StringComparison.OrdinalIgnoreCase);
        var scheme = new Uri(launchUri).Scheme;
        var nativeHandler = GetRegisteredPlayerPath(ReadProtocolCommand(scheme)) is not null;
        string? executable = bloxstrap ? FindBloxstrap() : null;
        var nativeSelection = ShouldUseNativePlayer(preference, nativeHandler);
        if (nativeSelection)
        {
            var channel = GetLaunchChannel(launchUri, ReadRegisteredChannel());
            diagnostic?.Invoke($"Checking Roblox's required build for channel {DisplayChannel(channel)} (launcher preference: {preference}).");
            executable = await ResolveNativePlayerAsync(channel, QueryVersionUploadAsync,
                FindInstalledVersion, FindStandardRoblox, HasRunningClients, diagnostic, cancellationToken);
            if (executable is null && HasRunningClients())
                throw new InvalidOperationException("No installed Roblox player could be selected safely while other clients are running.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (nativeSelection && executable is not null && prepareNativeClient is not null)
        {
            await prepareNativeClient(executable);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (executable is null)
        {
            diagnostic?.Invoke("Starting Roblox through the registered Windows protocol handler.");
            Process.Start(new ProcessStartInfo { FileName = launchUri, UseShellExecute = true });
            return;
        }

        diagnostic?.Invoke($"Roblox launch executable: {executable}");
        var startInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = false };
        if (bloxstrap)
        {
            startInfo.ArgumentList.Add("-player");
        }

        startInfo.ArgumentList.Add(launchUri);
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(startInfo);
    }

    internal static string GetLaunchChannel(string launchUri, string? registeredChannel = null)
    {
        IEnumerable<string> values;
        if (launchUri.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase))
        {
            values = launchUri.Split('+')
                .Where(part => part.StartsWith("channel:", StringComparison.OrdinalIgnoreCase))
                .Select(part => part[8..]);
        }
        else
        {
            values = new Uri(launchUri).Query.TrimStart('?').Split('&')
                .Where(part => part.StartsWith("channel=", StringComparison.OrdinalIgnoreCase))
                .Select(part => part[8..]);
        }
        var channels = values.Select(Uri.UnescapeDataString).ToArray();
        var selected = channels.Length == 0 ? registeredChannel ?? "" : channels[0];
        if (channels.Length > 1 || selected.Length > 100 ||
            selected.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidOperationException("Roblox supplied an invalid or ambiguous channel. Retry this account to request a fresh launch.");
        return string.Equals(selected, "production", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(selected, "LIVE", StringComparison.OrdinalIgnoreCase) ? "" : selected.ToLowerInvariant();
    }

    private static string? ReadRegisteredChannel()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\ROBLOX Corporation\Environments\RobloxPlayer\Channel");
            return key?.GetValue("www.roblox.com") as string;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string DisplayChannel(string channel) => channel.Length == 0 ? "production" : channel;

    internal static async Task<string> QueryVersionUploadAsync(string channel, CancellationToken cancellationToken)
    {
        var url = "https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer";
        if (channel.Length > 0) url += "/channel/" + Uri.EscapeDataString(channel);
        var json = await VersionClient.GetStringAsync(url, cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("clientVersionUpload", out var value) ||
            value.ValueKind != JsonValueKind.String || !IsVersionUpload(value.GetString()))
            throw new InvalidDataException("Roblox's version response did not contain a valid installation ID.");
        return value.GetString()!;
    }

    private static bool IsVersionUpload(string? upload) => upload is not null &&
        upload.StartsWith("version-", StringComparison.Ordinal) && upload.Length is > 8 and <= 64 &&
        upload.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    internal static string? FindInstalledVersion(string upload) => FindInstalledVersion(upload, GetStandardVersionsDirectories());

    internal static string? FindInstalledVersion(string upload, IEnumerable<string> roots)
    {
        if (!IsVersionUpload(upload)) throw new InvalidDataException("Invalid Roblox installation ID.");
        return roots.Select(root => Path.Combine(root, upload, "RobloxPlayerBeta.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static bool HasRunningClients()
    {
        var clients = Process.GetProcessesByName("RobloxPlayerBeta");
        try { return clients.Length > 0; }
        finally { foreach (var client in clients) client.Dispose(); }
    }

    public static string? FindBloxstrap()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bloxstrap",
            "Bloxstrap.exe");
        return File.Exists(path) ? path : null;
    }

    public static string? FindBloxstrapRoblox()
    {
        var versions = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bloxstrap",
            "Versions");
        return FindLatestRobloxInVersionsDirectory(versions);
    }

    public static string GetBloxstrapClientSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bloxstrap",
        "Modifications",
        "ClientSettings",
        "ClientAppSettings.json");

    public static bool UsesBloxstrap(string preference)
    {
        if (string.Equals(preference, "Standard", StringComparison.OrdinalIgnoreCase))
        {
            return FindStandardRoblox() is null && IsBloxstrapRegisteredHandler();
        }

        if (string.Equals(preference, "Bloxstrap", StringComparison.OrdinalIgnoreCase))
        {
            return FindBloxstrap() is not null || IsBloxstrapRegisteredHandler();
        }

        return IsBloxstrapRegisteredHandler();
    }

    public static bool IsBloxstrapRegisteredHandler()
    {
        try
        {
            return new[] { "roblox-player", "roblox" }
                .Select(ReadProtocolCommand)
                .Any(command => command?.Contains("Bloxstrap", StringComparison.OrdinalIgnoreCase) == true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string? ReadProtocolCommand(string scheme)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"{scheme}\shell\open\command");
        return key?.GetValue(null) as string;
    }

    public static string? FindStandardRoblox()
    {
        var roots = GetStandardVersionsDirectories();

        var commands = new List<string?>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"Software\Classes\roblox-player\shell\open\command");
                commands.Add(key?.GetValue(null) as string);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // An inaccessible registration does not hide the other installation.
            }
        }

        return FindStandardRoblox(roots, commands);
    }

    private static string[] GetStandardVersionsDirectories() => new[]
        {
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86
        }.Select(Environment.GetFolderPath)
         .Where(path => !string.IsNullOrWhiteSpace(path))
         .Select(path => Path.Combine(path, "Roblox", "Versions"))
         .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static string? FindStandardRoblox(
        IReadOnlyList<string> versionsDirectories,
        IReadOnlyList<string?> registeredCommands)
    {
        var candidates = new List<string>();
        foreach (var root in versionsDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                candidates.AddRange(Directory.EnumerateDirectories(root)
                    .Select(directory => Path.GetFullPath(Path.Combine(directory, "RobloxPlayerBeta.exe")))
                    .Where(File.Exists));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Continue checking the other supported installation locations.
            }
        }

        var installed = candidates.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var registered = registeredCommands.Select(GetRegisteredPlayerPath)
            .Where(path => path is not null && installed.Contains(path))
            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // A machine-wide updater can leave an older per-user installation and
        // registration behind. Prefer the newest registered installed player;
        // unregistered leftovers only participate when neither registration works.
        IEnumerable<string> eligible = registered.Length > 0 ? registered : candidates;
        return eligible
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? GetRegisteredPlayerPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        var end = command.StartsWith('"')
            ? command.IndexOf('"', 1)
            : command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;
        var path = command.StartsWith('"') ? command[1..end] : command[..(end + 4)];
        try
        {
            return Path.IsPathFullyQualified(path) &&
                string.Equals(Path.GetFileName(path), "RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(path) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? FindLatestRobloxInVersionsDirectory(string versions)
    {
        if (!Directory.Exists(versions))
        {
            return null;
        }

        return Directory.EnumerateDirectories(versions)
            .Select(directory => Path.Combine(directory, "RobloxPlayerBeta.exe"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
