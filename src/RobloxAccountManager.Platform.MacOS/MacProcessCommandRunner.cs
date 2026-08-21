using System.Diagnostics;

namespace RobloxAccountManager.Platform.MacOS;

public interface IMacProcessCommandRunner
{
    Task<MacProcessCommandResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs macOS tools without invoking a shell. In particular, launch URLs are supplied as one
/// ArgumentList item and never interpolated into a command string or diagnostic log.
/// </summary>
public sealed class MacProcessCommandRunner : IMacProcessCommandRunner
{
    public async Task<MacProcessCommandResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        if (!OperatingSystem.IsMacOS())
        {
            return new MacProcessCommandResult(-1, string.Empty, "macOS commands are unavailable on this platform.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        // `open` can echo the complete custom-scheme argument on stdout. Never retain that
        // stream because the URI may carry an authentication ticket. Its stderr is still safe
        // to retain for diagnosing a rejected handoff (and is never the ticket-bearing stream).
        var captureOutput = !string.Equals(executable, "/usr/bin/open", StringComparison.Ordinal);
        var outputTask = captureOutput ? process.StandardOutput.ReadToEndAsync(cancellationToken) : null;
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var discardOutput = captureOutput ? null : process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (discardOutput is not null) await discardOutput.ConfigureAwait(false);
        return new MacProcessCommandResult(
            process.ExitCode,
            outputTask is null ? string.Empty : await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }
}
