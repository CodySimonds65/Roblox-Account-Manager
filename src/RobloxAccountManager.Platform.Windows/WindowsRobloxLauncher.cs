using System.Diagnostics;
using RobloxAccountManager.Core.Contracts;

namespace RobloxAccountManager.Platform.Windows;

public sealed class WindowsRobloxLauncher(Func<string?> executableResolver) : IRobloxPlatformLauncher
{
    public RobloxPlatform Platform => RobloxPlatform.Windows;

    public ValueTask<PlatformLaunchResult> LaunchAsync(
        RobloxLaunchRequest request,
        Uri freshLaunchUri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (freshLaunchUri.Scheme is not ("roblox" or "roblox-player"))
            return ValueTask.FromResult(new PlatformLaunchResult(false, LaunchFailureKind.LauncherRejected, "invalid-launch-uri"));

        try
        {
            var executable = executableResolver();
            var start = string.IsNullOrWhiteSpace(executable)
                ? new ProcessStartInfo { FileName = freshLaunchUri.AbsoluteUri, UseShellExecute = true }
                : new ProcessStartInfo { FileName = executable, UseShellExecute = false };
            if (!string.IsNullOrWhiteSpace(executable)) start.ArgumentList.Add(freshLaunchUri.AbsoluteUri);
            _ = Process.Start(start) ?? throw new InvalidOperationException("Windows did not create the Roblox launcher process.");
            return ValueTask.FromResult(PlatformLaunchResult.Success());
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Never include the URI or exception text: a shell error can echo the ticket.
            return ValueTask.FromResult(new PlatformLaunchResult(false, LaunchFailureKind.LauncherRejected, "windows-launch-failed"));
        }
    }
}
