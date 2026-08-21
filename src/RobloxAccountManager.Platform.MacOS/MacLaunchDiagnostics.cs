namespace RobloxAccountManager.Platform.MacOS;

public static class MacLaunchDiagnostics
{
    public static string DescribeOpenFailure(MacProcessCommandResult command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Succeeded)
            return "macos-open-succeeded";

        var error = Compact(command.StandardError);
        return string.IsNullOrWhiteSpace(error)
            ? $"macos-open-failed:exit={command.ExitCode}"
            : $"macos-open-failed:exit={command.ExitCode}:stderr={error}";
    }

    public static string DescribeVerificationFailure(LaunchVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var code = result.Status switch
        {
            LaunchVerificationStatus.TimedOut => "macos-process-verification-timeout",
            LaunchVerificationStatus.ExistingProcessOnly => "macos-process-verification-existing-process",
            LaunchVerificationStatus.InvalidBundle => "macos-process-verification-invalid-bundle",
            _ => "macos-process-verification-failed"
        };
        var warning = result.Warnings.FirstOrDefault();
        return string.IsNullOrWhiteSpace(warning) ? code : $"{code}:{Compact(warning)}";
    }

    private static string Compact(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var redacted = MacRobloxDiagnostics.RedactSensitive(value);
        var compact = string.Join(' ', redacted.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 180 ? compact : compact[..180];
    }
}
