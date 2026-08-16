using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace RobloxAltClient.Services;

public sealed class SingletonService
{
    private static readonly Uri HandleDownloadUri = new("https://download.sysinternals.com/files/Handle.zip");
    private static readonly HttpClient HttpClient = new();

    public async Task<UnlockResult> ReleaseAsync()
    {
        string helperPath;
        string handlePath;
        try
        {
            (helperPath, handlePath) = await PrepareToolsAsync();
        }
        catch (Exception exception)
        {
            return new UnlockResult(false, 0, [$"Could not prepare the unlock tools: {exception.Message}"]);
        }

        var resultPath = Path.Combine(Path.GetTempPath(), $"roblox-alt-unlock-{Guid.NewGuid():N}.json");
        var arguments = $"-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File {Quote(helperPath)} -HandleTool {Quote(handlePath)} -ResultPath {Quote(resultPath)}";

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process is null)
            {
                return new UnlockResult(false, 0, ["Windows could not start the administrator unlock helper."]);
            }

            await process.WaitForExitAsync();
            if (!File.Exists(resultPath))
            {
                return new UnlockResult(false, 0, [$"The unlock helper exited with code {process.ExitCode} without producing a result."]);
            }

            return await ReadResultAsync(resultPath);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new UnlockResult(false, 0, ["Administrator approval was cancelled."]);
        }
        catch (Exception exception)
        {
            return new UnlockResult(false, 0, [exception.Message]);
        }
        finally
        {
            if (File.Exists(resultPath))
            {
                File.Delete(resultPath);
            }
        }
    }

    public async Task<SingletonSessionStartResult> StartSessionAsync(CancellationToken cancellationToken = default)
    {
        string sessionDirectory = string.Empty;
        Process? process = null;
        try
        {
            var (helperPath, handlePath) = await PrepareToolsAsync();
            sessionDirectory = Path.Combine(Path.GetTempPath(), $"roblox-alt-unlock-session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(sessionDirectory);
            var arguments = $"-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File {Quote(helperPath)} -HandleTool {Quote(handlePath)} -SessionDirectory {Quote(sessionDirectory)}";
            process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process is null)
            {
                DeleteSessionDirectory(sessionDirectory);
                return new SingletonSessionStartResult(false, null, ["Windows could not start the administrator unlock helper."]);
            }

            var readyPath = Path.Combine(sessionDirectory, "ready");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!File.Exists(readyPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    var exitCode = process.ExitCode;
                    process.Dispose();
                    DeleteSessionDirectory(sessionDirectory);
                    return new SingletonSessionStartResult(
                        false,
                        null,
                        [$"The administrator unlock helper exited with code {exitCode} before it was ready."]);
                }

                if (DateTime.UtcNow >= deadline)
                {
                    StopSessionProcess(process, sessionDirectory);
                    process.Dispose();
                    DeleteSessionDirectory(sessionDirectory);
                    return new SingletonSessionStartResult(false, null, ["The administrator unlock helper did not become ready in time."]);
                }

                await Task.Delay(100, cancellationToken);
            }

            return new SingletonSessionStartResult(
                true,
                new SingletonUnlockSession(process, sessionDirectory),
                ["Administrator approval will be reused for the rest of this launch queue."]);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            StopSessionProcess(process, sessionDirectory);
            process?.Dispose();
            DeleteSessionDirectory(sessionDirectory);
            return new SingletonSessionStartResult(false, null, ["Administrator approval was cancelled."]);
        }
        catch (OperationCanceledException)
        {
            StopSessionProcess(process, sessionDirectory);
            process?.Dispose();
            DeleteSessionDirectory(sessionDirectory);
            throw;
        }
        catch (Exception exception)
        {
            StopSessionProcess(process, sessionDirectory);
            process?.Dispose();
            DeleteSessionDirectory(sessionDirectory);
            return new SingletonSessionStartResult(false, null, [$"Could not start the administrator unlock helper: {exception.Message}"]);
        }
    }

    private static async Task<(string HelperPath, string HandlePath)> PrepareToolsAsync()
    {
        var toolDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobloxAltClient",
            "Tools");
        Directory.CreateDirectory(toolDirectory);

        var helperPath = await ExtractEmbeddedResourceAsync(
            "RobloxAltClient.Resources.Unlock-Roblox.ps1",
            Path.Combine(toolDirectory, "Unlock-Roblox.ps1"));
        var handlePath = await EnsureHandleToolAsync(Path.Combine(toolDirectory, "handle64.exe"));
        return (helperPath, handlePath);
    }

    internal static async Task<UnlockResult> ReadResultAsync(string resultPath)
    {
        var json = await File.ReadAllTextAsync(resultPath);
        return JsonSerializer.Deserialize<UnlockResult>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new UnlockResult(false, 0, ["The unlock helper returned an unreadable result."]);
    }

    internal static void DeleteSessionDirectory(string sessionDirectory)
    {
        if (!string.IsNullOrWhiteSpace(sessionDirectory) && Directory.Exists(sessionDirectory))
        {
            try
            {
                Directory.Delete(sessionDirectory, recursive: true);
            }
            catch
            {
                // The helper may still be releasing its final file handle. The
                // temporary directory is harmless and will be cleaned by Windows.
            }
        }
    }

    internal static void StopSessionProcess(Process? process, string sessionDirectory)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(sessionDirectory) && Directory.Exists(sessionDirectory))
            {
                File.WriteAllText(Path.Combine(sessionDirectory, "stop"), string.Empty);
            }

            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
            }
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
                // Elevated processes can deny termination to a standard token.
            }
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "")}\"";

    private static async Task<string> EnsureHandleToolAsync(string destinationPath)
    {
        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
        {
            return destinationPath;
        }

        using var response = await HttpClient.GetAsync(HandleDownloadUri);
        response.EnsureSuccessStatusCode();

        await using var download = await response.Content.ReadAsStreamAsync();
        using var archiveBytes = new MemoryStream();
        await download.CopyToAsync(archiveBytes);
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
                await source.CopyToAsync(destination);
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

    private static async Task<string> ExtractEmbeddedResourceAsync(string resourceName, string destinationPath)
    {
        await using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' is missing.");

        using var memory = new MemoryStream();
        await resource.CopyToAsync(memory);
        var expectedBytes = memory.ToArray();
        var expectedHash = SHA256.HashData(expectedBytes);

        if (File.Exists(destinationPath))
        {
            await using var existing = File.OpenRead(destinationPath);
            var existingHash = await SHA256.HashDataAsync(existing);
            if (CryptographicOperations.FixedTimeEquals(expectedHash, existingHash))
            {
                return destinationPath;
            }
        }

        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporaryPath, expectedBytes);
        try
        {
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return destinationPath;
    }
}

public sealed record UnlockResult(bool Success, int ClosedCount, string[] Messages);

public sealed record SingletonSessionStartResult(
    bool Success,
    SingletonUnlockSession? Session,
    string[] Messages);

public sealed class SingletonUnlockSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _sessionDirectory;
    private bool _disposed;

    internal SingletonUnlockSession(Process process, string sessionDirectory)
    {
        _process = process;
        _sessionDirectory = sessionDirectory;
    }

    public async Task<UnlockResult> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process.HasExited)
        {
            return new UnlockResult(false, 0, ["The queue's administrator unlock helper is no longer running."]);
        }

        var requestId = Guid.NewGuid().ToString("N");
        var requestPath = Path.Combine(_sessionDirectory, $"request-{requestId}");
        var temporaryRequestPath = $"{requestPath}.tmp";
        var resultPath = Path.Combine(_sessionDirectory, $"result-{requestId}.json");
        try
        {
            await File.WriteAllTextAsync(temporaryRequestPath, requestId, cancellationToken);
            File.Move(temporaryRequestPath, requestPath);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (!File.Exists(resultPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_process.HasExited)
                {
                    return new UnlockResult(false, 0, ["The queue's administrator unlock helper exited unexpectedly."]);
                }

                if (DateTime.UtcNow >= deadline)
                {
                    return new UnlockResult(false, 0, ["The queue's administrator unlock helper did not respond in time."]);
                }

                await Task.Delay(100, cancellationToken);
            }

            return await SingletonService.ReadResultAsync(resultPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new UnlockResult(false, 0, [$"Could not communicate with the administrator unlock helper: {exception.Message}"]);
        }
        finally
        {
            foreach (var path in new[] { temporaryRequestPath, requestPath, resultPath })
            {
                if (File.Exists(path))
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch
                    {
                        // The session shutdown path will retry directory cleanup.
                    }
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                await File.WriteAllTextAsync(Path.Combine(_sessionDirectory, "stop"), string.Empty);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await _process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Elevated processes can deny termination to a standard token.
                    }
                }
            }
        }
        catch
        {
            SingletonService.StopSessionProcess(_process, _sessionDirectory);
        }
        finally
        {
            _process.Dispose();
            SingletonService.DeleteSessionDirectory(_sessionDirectory);
        }
    }
}
