using System.Globalization;
using System.Text.RegularExpressions;
using RobloxAltClient.Plugins;

namespace RobloxAltClient.Services;

/// <summary>
/// Best-effort post-mortem of a Roblox client's own session log after it exits.
/// Roblox writes one *_last.log per session into %LOCALAPPDATA%\Roblox\logs with
/// the session start time embedded in the file name. The log is the only
/// authoritative source for self-inflicted shutdowns such as force-updates.
/// Synchronous on purpose: callers must invoke it off the UI thread.
/// </summary>
public static partial class RobloxLogAutopsy
{
    public static IReadOnlyList<string> Autopsy(ManagedAccountSnapshot snapshot)
    {
        var logsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox",
            "logs");
        return Autopsy(snapshot, logsDirectory);
    }

    internal static IReadOnlyList<string> Autopsy(ManagedAccountSnapshot snapshot, string logsDirectory)
    {
        if (!Directory.Exists(logsDirectory))
        {
            return [$"No Roblox session log was found for {snapshot.Label}."];
        }

        var nowUtc = DateTime.UtcNow;
        var processStartUtc = snapshot.ProcessStartTimeUtcTicks > 0
            ? new DateTime(snapshot.ProcessStartTimeUtcTicks, DateTimeKind.Utc)
            : (DateTime?)null;

        var candidate = FindSessionLog(logsDirectory, processStartUtc, nowUtc);
        if (candidate is null)
        {
            return [$"No Roblox session log was found for {snapshot.Label}."];
        }

        IReadOnlyList<string> tail;
        try
        {
            tail = ReadTailWithRetry(candidate.Path, 600);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [$"Roblox log for {snapshot.Label} could not be read: {ex.Message}"];
        }

        var fileName = Path.GetFileName(candidate.Path);
        var version = VersionRegex().Match(fileName).Groups["version"].Value;
        var startText = candidate.StartUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var messages = new List<string>
        {
            $"Roblox log: {fileName} (client {version}, started {startText})."
        };

        foreach (var line in tail)
        {
            if (line.Contains("updateRequired TRUE", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Update mode is chosen as FORCE", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add("Roblox flagged an update as REQUIRED and forced the client to close for its own updater; the launcher did not kill this client.");
                break;
            }
        }

        var channel = tail.Select(line => ChannelRegex().Match(line)).FirstOrDefault(match => match.Success);
        if (channel is not null)
        {
            messages.Add($"Roblox channel: {channel.Groups["channel"].Value} (feature-test channels force-update frequently; opt out of the Roblox test program for stable clients).");
        }

        var disconnect = tail.FirstOrDefault(line => line.Contains("Sending disconnect with reason", StringComparison.Ordinal));
        if (disconnect is not null)
        {
            var reason = DisconnectReasonRegex().Match(disconnect).Groups["reason"].Value;
            if (reason.Length > 0)
            {
                messages.Add($"The client left its game session with disconnect reason {reason} (client-initiated).");
            }
        }

        return messages;
    }

    private static SessionLog? FindSessionLog(string logsDirectory, DateTime? processStartUtc, DateTime nowUtc)
    {
        SessionLog? best = null;
        var bestDelta = TimeSpan.MaxValue;
        foreach (var file in Directory.EnumerateFiles(logsDirectory, "*_last.log", SearchOption.TopDirectoryOnly))
        {
            var nameMatch = LogNameRegex().Match(Path.GetFileName(file));
            if (!nameMatch.Success) continue;
            if (!DateTime.TryParseExact(
                    nameMatch.Groups["time"].Value,
                    "yyyyMMddTHHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var startUtc))
            {
                continue;
            }

            var delta = processStartUtc is not null
                ? (startUtc - processStartUtc.Value).Duration()
                : (nowUtc - startUtc).Duration();
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = new SessionLog(file, startUtc);
            }
        }

        // A one-minute ceiling keeps two accounts launched back-to-back from
        // borrowing each other's session logs.
        return bestDelta <= TimeSpan.FromMinutes(1) ? best : null;
    }

    private static IReadOnlyList<string> ReadTailWithRetry(string path, int maxLines)
    {
        // Roblox holds its session log exclusively while the process tears down,
        // and security software may scan-lock it right after a write. Retry a few
        // times before giving up; the caller runs off the UI thread.
        IOException? lastError = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) Thread.Sleep(250);
            try
            {
                return ReadTail(path, maxLines);
            }
            catch (IOException ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new IOException($"The log file {path} could not be opened.");
    }

    private static IReadOnlyList<string> ReadTail(string path, int maxLines)
    {
        const int blockSize = 32 * 1024;
        var lines = new List<string>();
        var buffer = new byte[blockSize];
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length == 0)
        {
            return lines;
        }

        long position = stream.Length;
        var pending = new List<char>(256);
        while (position > 0 && lines.Count < maxLines)
        {
            var blockLength = (int)Math.Min(blockSize, position);
            position -= blockLength;
            stream.Position = position;
            var read = stream.Read(buffer, 0, blockLength);
            for (var i = read - 1; i >= 0; i--)
            {
                var character = (char)buffer[i];
                if (character is '\n' or '\r')
                {
                    if (pending.Count > 0)
                    {
                        pending.Reverse();
                        lines.Add(new string([.. pending]));
                        pending.Clear();
                        if (lines.Count >= maxLines) break;
                    }
                }
                else
                {
                    pending.Add(character);
                }
            }
        }

        if (pending.Count > 0 && lines.Count < maxLines)
        {
            pending.Reverse();
            lines.Add(new string([.. pending]));
        }

        lines.Reverse();
        return lines;
    }

    private sealed record SessionLog(string Path, DateTime StartUtc);

    [GeneratedRegex(@"^(?<version>[0-9]+(?:\.[0-9]+)*)_(?<time>\d{8}T\d{6})Z_Player_[0-9A-Fa-f]+_last\.log$")]
    private static partial Regex LogNameRegex();

    [GeneratedRegex(@"^(?<version>[0-9]+(?:\.[0-9]+)*)_")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"RobloxChannel has been set to (?<channel>\S+)")]
    private static partial Regex ChannelRegex();

    [GeneratedRegex(@"Sending disconnect with reason:\s*(?<reason>\d+)")]
    private static partial Regex DisconnectReasonRegex();
}
