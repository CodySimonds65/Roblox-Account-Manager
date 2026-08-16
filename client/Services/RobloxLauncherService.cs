using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace RobloxAltClient.Services;

public sealed class RobloxLauncherService
{
    public void Start(string launchUri, string preference)
    {
        var executable = preference switch
        {
            "Bloxstrap" => FindBloxstrap(),
            "Standard" => FindStandardRoblox(),
            _ => null
        };

        if (executable is null)
        {
            Process.Start(new ProcessStartInfo { FileName = launchUri, UseShellExecute = true });
            return;
        }

        var startInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = false };
        if (string.Equals(preference, "Bloxstrap", StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("-player");
        }

        startInfo.ArgumentList.Add(launchUri);
        Process.Start(startInfo);
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
        var versions = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox",
            "Versions");
        return FindLatestRobloxInVersionsDirectory(versions);
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
