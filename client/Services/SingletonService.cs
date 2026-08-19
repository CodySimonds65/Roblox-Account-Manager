using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace RobloxAltClient.Services;

public sealed partial class SingletonService
{
    private static readonly Uri HandleDownloadUri = new("https://download.sysinternals.com/files/Handle.zip");
    private static readonly HttpClient HttpClient = new();

    public async Task<UnlockResult> ReleaseAsync()
    {
        if (!IsAdministrator())
        {
            return new UnlockResult(
                false,
                0,
                ["Roblox Account Manager is not running as administrator. Close it and start it again."]);
        }

        try
        {
            var handlePath = await PrepareHandleToolAsync();
            return await ReleaseHandlesAsync(handlePath, CancellationToken.None);
        }
        catch (Exception exception)
        {
            return new UnlockResult(false, 0, [exception.Message]);
        }
    }

    public async Task<SingletonSessionStartResult> StartSessionAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAdministrator())
            {
                return new SingletonSessionStartResult(
                    false,
                    null,
                    ["Roblox Account Manager is not running as administrator. Close it and start it again."]);
            }

            var handlePath = await PrepareHandleToolAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new SingletonSessionStartResult(
                true,
                new SingletonUnlockSession(handlePath),
                ["Using the client's existing elevated security context; no additional UAC prompt will be requested."]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SingletonSessionStartResult(
                false,
                null,
                [$"Could not prepare the native singleton unlock service: {exception.Message}"]);
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static async Task<string> PrepareHandleToolAsync(CancellationToken cancellationToken = default)
    {
        var toolDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxAltClient",
            "Tools");
        Directory.CreateDirectory(toolDirectory);
        return await EnsureHandleToolAsync(Path.Combine(toolDirectory, "handle64.exe"), cancellationToken);
    }

    internal static async Task<UnlockResult> ReleaseHandlesAsync(
        string handlePath,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? protectedProcessIds = null)
    {
        var protectedSet = protectedProcessIds is null ? null : new HashSet<int>(protectedProcessIds);
        var processes = Process.GetProcessesByName("RobloxPlayerBeta");
        if (processes.Length == 0)
        {
            return new UnlockResult(
                false,
                0,
                ["No running Roblox client was found. Launch the first account into a game before preparing another account."]);
        }

        var messages = new List<string>();
        var closedCount = 0;
        try
        {
            try
            {
                foreach (var process in processes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Closing the singleton handles inside a RUNNING managed client
                    // makes its watchdog shut the game down. Managed accounts are
                    // protected; their handles are never closed or verified.
                    if (protectedSet is not null && protectedSet.Contains(process.Id))
                    {
                        messages.Add($"Skipped singleton release for PID {process.Id} (managed account in use).");
                        continue;
                    }
                    var queryResult = await RunHandleAsync(
                        handlePath,
                        cancellationToken,
                        "-accepteula", "-nobanner", "-a", "-p", process.Id.ToString());
                    EnsureHandleSucceeded(queryResult, $"inspect Roblox PID {process.Id}");

                    foreach (var handle in ParseSingletonHandles(queryResult.Output))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var closeResult = await RunHandleAsync(
                            handlePath,
                            cancellationToken,
                            "-accepteula", "-nobanner", "-c", handle.Id, "-p", process.Id.ToString(), "-y");
                        EnsureHandleSucceeded(closeResult, $"release {handle.Name} in PID {process.Id}");
                        closedCount++;
                        messages.Add($"Released {handle.Name} in PID {process.Id}.");
                    }
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }

            if (closedCount == 0)
            {
                messages.Add("No singleton handles are currently present; Roblox is already unlocked.");
            }

            var verificationProcesses = Process.GetProcessesByName("RobloxPlayerBeta");
            try
            {
                foreach (var process in verificationProcesses)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (protectedSet is not null && protectedSet.Contains(process.Id))
                    {
                        continue;
                    }
                    var verificationResult = await RunHandleAsync(
                        handlePath,
                        cancellationToken,
                        "-accepteula", "-nobanner", "-a", "-p", process.Id.ToString());
                    EnsureHandleSucceeded(verificationResult, $"verify Roblox PID {process.Id}");

                    if (ParseSingletonHandles(verificationResult.Output).Count > 0)
                    {
                        throw new InvalidOperationException($"Roblox still owns a singleton object in PID {process.Id}.");
                    }
                }
            }
            finally
            {
                foreach (var process in verificationProcesses)
                {
                    process.Dispose();
                }
            }

            return new UnlockResult(true, closedCount, [.. messages]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            messages.Add(exception.Message);
            return new UnlockResult(false, closedCount, [.. messages]);
        }
    }

    private static List<SingletonHandle> ParseSingletonHandles(string output)
    {
        var handles = new List<SingletonHandle>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = HandleLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var nameMatch = SingletonNameRegex().Match(match.Groups["name"].Value.Trim());
            if (nameMatch.Success)
            {
                handles.Add(new SingletonHandle(
                    match.Groups["id"].Value,
                    nameMatch.Value.TrimStart('\\')));
            }
        }

        return handles;
    }

    private static async Task<HandleResult> RunHandleAsync(
        string handlePath,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = handlePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start the Sysinternals Handle tool.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort cleanup when cancellation races process exit.
            }

            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        return new HandleResult(process.ExitCode, string.Join(Environment.NewLine, new[] { output, error }
            .Where(value => !string.IsNullOrWhiteSpace(value))));
    }

    private static void EnsureHandleSucceeded(HandleResult result, string operation)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.Output)
            ? "No diagnostic output was produced."
            : result.Output.Trim();
        throw new InvalidOperationException(
            $"Sysinternals Handle could not {operation} (exit code {result.ExitCode}): {detail}");
    }

    private static async Task<string> EnsureHandleToolAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
        {
            return destinationPath;
        }

        using var response = await HttpClient.GetAsync(HandleDownloadUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var download = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var archiveBytes = new MemoryStream();
        await download.CopyToAsync(archiveBytes, cancellationToken);
        archiveBytes.Position = 0;

        using var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Read);
        var handleEntry = archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.Name, "handle64.exe", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Microsoft's Handle archive did not contain handle64.exe.");

        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var source = handleEntry.Open())
            await using (var destination = File.Create(temporaryPath))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            if (new FileInfo(temporaryPath).Length == 0)
            {
                throw new InvalidOperationException("The downloaded Handle executable was empty.");
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            return destinationPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    [GeneratedRegex(@"^\s*(?<id>[0-9A-Fa-f]+):\s+\S+\s+(?<name>.+)$")]
    private static partial Regex HandleLineRegex();

    [GeneratedRegex(@"\\ROBLOX_singleton(?:Event|Mutex)$", RegexOptions.IgnoreCase)]
    private static partial Regex SingletonNameRegex();

    private sealed record SingletonHandle(string Id, string Name);
    private sealed record HandleResult(int ExitCode, string Output);
}

public sealed record UnlockResult(bool Success, int ClosedCount, string[] Messages);

public sealed record SingletonSessionStartResult(
    bool Success,
    SingletonUnlockSession? Session,
    string[] Messages);

public sealed class SingletonUnlockSession : IAsyncDisposable
{
    private readonly string _handlePath;
    private bool _disposed;

    internal SingletonUnlockSession(string handlePath)
    {
        _handlePath = handlePath;
    }

    public Task<UnlockResult> ReleaseAsync(
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<int>? protectedProcessIds = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SingletonService.ReleaseHandlesAsync(_handlePath, cancellationToken, protectedProcessIds);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
