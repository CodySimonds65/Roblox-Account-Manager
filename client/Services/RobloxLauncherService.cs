using System.Diagnostics;
using System.IO;

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

    public static string? FindStandardRoblox()
    {
        var versions = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox",
            "Versions");
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
