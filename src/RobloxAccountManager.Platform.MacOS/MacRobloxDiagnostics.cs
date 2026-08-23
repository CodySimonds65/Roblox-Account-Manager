using System.Globalization;
using System.Text.RegularExpressions;

namespace RobloxAccountManager.Platform.MacOS;

public sealed record MacRobloxLaunchDiagnostics(
    string StatusCode,
    string? LogFileName,
    string? LogVersion,
    IReadOnlyList<string> Summary,
    IReadOnlyList<string> RedactedTail,
    string? ArtifactPath);

public static partial class MacRobloxDiagnostics
{
    private static readonly TimeSpan SessionMatchWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CrashReportEarlyTolerance = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CrashReportWriteWindow = TimeSpan.FromSeconds(90);
    private const int MaxTailLines = 240;
    private const int MaxTailBytes = 256 * 1024;

    public static string DescribeClient(MacBundleInfo? bundle) =>
        bundle is null
            ? "Roblox client: not detected."
            : $"Roblox client: {FormatVersion(bundle.Version)}{FormatBuild(bundle.Build)}.";

    public static MacRobloxLaunchDiagnostics Collect(
        DateTimeOffset processStartUtc,
        IEnumerable<string>? logDirectories = null,
        string? artifactDirectory = null)
    {
        var directories = (logDirectories ?? GetDefaultLogDirectories())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var crashReports = FindRecentCrashReports(processStartUtc, directories);
        var candidate = FindSessionLog(processStartUtc, directories);
        if (candidate is null)
        {
            var crashTail = ReadCrashReportTail(crashReports);
            if (crashReports.Count > 0)
            {
                var redactedCrashTail = crashTail.Select(RedactSensitive).ToArray();
                var crashSummary = SummarizeCrashReports(crashReports);
                var crashArtifactPath = WriteArtifact(redactedCrashTail, artifactDirectory);
                return new MacRobloxLaunchDiagnostics(
                    "crash-report-found",
                    null,
                    null,
                    crashSummary,
                    redactedCrashTail,
                    crashArtifactPath);
            }

            return new MacRobloxLaunchDiagnostics(
                "session-log-not-found",
                null,
                null,
                ["No matching Roblox session log was found."],
                [],
                null);
        }

        IReadOnlyList<string> tail;
        try
        {
            tail = ReadTail(candidate.Value.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new MacRobloxLaunchDiagnostics(
                "session-log-unreadable",
                Path.GetFileName(candidate.Value.Path),
                candidate.Value.Version,
                ["The matching Roblox session log could not be read."],
                [],
                null);
        }

        var redactedTail = tail.Select(RedactSensitive).ToArray();
        var summary = Summarize(candidate.Value, tail);
        if (crashReports.Count > 0)
        {
            summary = summary.Concat(SummarizeCrashReports(crashReports)).ToArray();
            redactedTail = redactedTail
                .Concat(ReadCrashReportTail(crashReports).Select(RedactSensitive))
                .ToArray();
        }
        var artifactPath = WriteArtifact(redactedTail, artifactDirectory);
        return new MacRobloxLaunchDiagnostics(
            "matched-session-log",
            Path.GetFileName(candidate.Value.Path),
            candidate.Value.Version,
            summary,
            redactedTail,
            artifactPath);
    }

    public static string RedactSensitive(string line)
    {
        var redacted = SensitiveParameterRegex().Replace(line, "$1[REDACTED]");
        redacted = HttpUrlRegex().Replace(redacted, "https://[REDACTED]");
        redacted = RobloxSchemeRegex().Replace(redacted, "roblox://[REDACTED]");
        return redacted;
    }

    public static IReadOnlyList<string> GetDefaultLogDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            Path.Combine(home, "Library", "Logs", "Roblox"),
            Path.Combine(home, "Library", "Logs", "RobloxPlayer"),
            Path.Combine(home, "Library", "Application Support", "Roblox", "logs"),
            Path.Combine(home, "Library", "Logs", "DiagnosticReports")
        ];
    }

    private static SessionLog? FindSessionLog(DateTimeOffset processStartUtc, IEnumerable<string> logDirectories)
    {
        SessionLog? best = null;
        var bestDelta = TimeSpan.MaxValue;
        foreach (var directory in logDirectories.Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(directory)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*_last.log", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in files)
            {
                var match = SessionLogRegex().Match(Path.GetFileName(path));
                if (!match.Success || !DateTimeOffset.TryParseExact(
                        match.Groups["time"].Value,
                        "yyyyMMddTHHmmss'Z'",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var logStartUtc))
                {
                    continue;
                }

                var delta = (logStartUtc - processStartUtc).Duration();
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = new SessionLog(path, match.Groups["version"].Value, logStartUtc);
                }
            }
        }

        return bestDelta <= SessionMatchWindow ? best : null;
    }

    private static IReadOnlyList<string> ReadTail(string path)
    {
        var lines = new Queue<string>(MaxTailLines);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - MaxTailBytes);
        stream.Position = start;
        using var reader = new StreamReader(stream);
        if (start > 0) _ = reader.ReadLine();
        while (reader.ReadLine() is { } line)
        {
            if (lines.Count == MaxTailLines) lines.Dequeue();
            lines.Enqueue(line);
        }

        return lines.ToArray();
    }

    private static IReadOnlyList<string> ReadCrashReportTail(IReadOnlyList<string> paths)
    {
        var lines = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                lines.Add($"--- {Path.GetFileName(path)} ---");
                lines.AddRange(ReadTail(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lines.Add($"--- {Path.GetFileName(path)} could not be read ---");
            }
        }

        return lines;
    }

    private static IReadOnlyList<string> SummarizeCrashReports(IReadOnlyList<string> paths) =>
        [
            $"Roblox crash report(s) found: {string.Join(", ", paths.Select(Path.GetFileName))}.",
            "Roblox crash-report evidence was collected for this launch attempt."
        ];

    private static IReadOnlyList<string> FindRecentCrashReports(
        DateTimeOffset processStartUtc,
        IEnumerable<string> directories)
    {
        var latestAllowed = processStartUtc.Add(CrashReportWriteWindow);
        var earliestAllowed = processStartUtc.Subtract(CrashReportEarlyTolerance);
        var paths = new List<string>();
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in files)
            {
                var fileName = Path.GetFileName(path);
                if (!fileName.Contains("Roblox", StringComparison.OrdinalIgnoreCase)
                    || (!path.EndsWith(".crash", StringComparison.OrdinalIgnoreCase)
                        && !path.EndsWith(".ips", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                DateTimeOffset lastWriteUtc;
                try
                {
                    lastWriteUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (lastWriteUtc >= earliestAllowed && lastWriteUtc <= latestAllowed)
                {
                    paths.Add(path);
                }
            }
        }

        return paths
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> Summarize(SessionLog session, IReadOnlyList<string> tail)
    {
        var summary = new List<string>
        {
            $"Roblox session log: {Path.GetFileName(session.Path)} (client {session.Version})."
        };

        var channel = tail.Select(line => ChannelRegex().Match(line)).FirstOrDefault(match => match.Success);
        if (channel is not null)
        {
            summary.Add($"Roblox channel: {channel.Groups["channel"].Value}.");
        }

        if (tail.Any(line => line.Contains("updateRequired TRUE", StringComparison.OrdinalIgnoreCase)
                             || line.Contains("Update mode is chosen as FORCE", StringComparison.OrdinalIgnoreCase)))
        {
            summary.Add("Roblox reported a required update or force-close condition.");
        }

        if (tail.Any(ContainsSeriousFailureMarker))
        {
            summary.Add("Roblox reported an error, fatal, or crash marker.");
        }

        var disconnect = tail.Select(line => DisconnectReasonRegex().Match(line)).FirstOrDefault(match => match.Success);
        if (disconnect is not null)
        {
            summary.Add($"Roblox reported disconnect reason {disconnect.Groups["reason"].Value}.");
        }

        return summary;
    }

    private static bool ContainsSeriousFailureMarker(string line)
    {
        if (line.Contains("fatal", StringComparison.OrdinalIgnoreCase)
            || line.Contains("crash", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!line.Contains("[FLog::Error]", StringComparison.OrdinalIgnoreCase))
            return false;

        return line.Contains("terminate", StringComparison.OrdinalIgnoreCase)
            || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
            || line.Contains("launch failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("render view is null", StringComparison.OrdinalIgnoreCase);
    }

    private static string? WriteArtifact(IReadOnlyList<string> lines, string? artifactDirectory)
    {
        if (string.IsNullOrWhiteSpace(artifactDirectory) || lines.Count == 0) return null;
        try
        {
            Directory.CreateDirectory(artifactDirectory);
            var path = Path.Combine(artifactDirectory, $"macos-roblox-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.log");
            File.WriteAllLines(path, lines);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string FormatVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "unknown version" : version.Trim();

    private static string FormatBuild(string? build) =>
        string.IsNullOrWhiteSpace(build) ? string.Empty : $" build {build.Trim()}";

    private readonly record struct SessionLog(string Path, string Version, DateTimeOffset StartUtc);

    [GeneratedRegex(@"^(?<version>[0-9]+(?:\.[0-9]+)*)_(?<time>\d{8}T\d{6}Z)_Player_[^_]+_last\.log$", RegexOptions.IgnoreCase)]
    private static partial Regex SessionLogRegex();

    [GeneratedRegex(@"RobloxChannel has been set to (?<channel>\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelRegex();

    [GeneratedRegex(@"Sending disconnect with reason:\s*(?<reason>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DisconnectReasonRegex();

    [GeneratedRegex(@"(?<prefix>(?:code|privateServerLinkCode|token|ticket|auth|authTicket|authorization|cookie|access_token|accessToken|secret|password)(?:=|""?\s*:\s*""?))[^\r\n&"",}]+", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveParameterRegex();

    [GeneratedRegex(@"https?://[^\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex HttpUrlRegex();

    [GeneratedRegex(@"roblox(?:-player)?:(?:/{0,2})[^\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex RobloxSchemeRegex();
}
