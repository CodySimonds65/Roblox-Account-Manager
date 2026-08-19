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
        // `open` can echo the complete custom-scheme argument. Never retain that output because
        // the URI may carry an authentication ticket. Other callers that need structured output
        // (plutil/codesign entitlement extraction) still receive it.
        var captureOutput = !string.Equals(executable, "/usr/bin/open", StringComparison.Ordinal);
        var outputTask = captureOutput ? process.StandardOutput.ReadToEndAsync(cancellationToken) : null;
        var errorTask = captureOutput ? process.StandardError.ReadToEndAsync(cancellationToken) : null;
        var discardOutput = captureOutput ? null : process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
        var discardError = captureOutput ? null : process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (discardOutput is not null) await discardOutput.ConfigureAwait(false);
        if (discardError is not null) await discardError.ConfigureAwait(false);
        return new MacProcessCommandResult(
            process.ExitCode,
            outputTask is null ? string.Empty : await outputTask.ConfigureAwait(false),
            errorTask is null ? string.Empty : await errorTask.ConfigureAwait(false));
    }
}
