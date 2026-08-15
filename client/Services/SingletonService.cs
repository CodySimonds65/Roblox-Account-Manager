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
            var toolDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RobloxAltClient",
                "Tools");
            Directory.CreateDirectory(toolDirectory);

            helperPath = await ExtractEmbeddedResourceAsync(
                "RobloxAltClient.Resources.Unlock-Roblox.ps1",
                Path.Combine(toolDirectory, "Unlock-Roblox.ps1"));
            handlePath = await EnsureHandleToolAsync(Path.Combine(toolDirectory, "handle64.exe"));
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

            var json = await File.ReadAllTextAsync(resultPath);
            return JsonSerializer.Deserialize<UnlockResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new UnlockResult(false, 0, ["The unlock helper returned an unreadable result."]);
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
