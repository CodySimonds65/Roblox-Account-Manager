using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace RobloxAltClient.Services;

internal static class JsonFileStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<T> LoadAsync<T>(string path, T fallback, JsonSerializerOptions options)
    {
        var fileLock = FileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync();
        try
        {
            var loaded = await TryLoadAsync<T>(path, options);
            if (loaded.Success)
            {
                return loaded.Value!;
            }

            var backupPath = path + ".bak";
            var backup = await TryLoadAsync<T>(backupPath, options);
            if (backup.Success)
            {
                File.Copy(backupPath, path, overwrite: true);
                return backup.Value!;
            }

            return fallback;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public static async Task SaveAsync<T>(string path, T value, JsonSerializerOptions options)
    {
        var fileLock = FileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync();
        var temporaryPath = path + ".tmp";
        var backupPath = path + ".bak";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, value, options);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
                File.Copy(path, backupPath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            fileLock.Release();
        }
    }

    private static async Task<(bool Success, T? Value)> TryLoadAsync<T>(string path, JsonSerializerOptions options)
    {
        if (!File.Exists(path))
        {
            return (false, default);
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, options);
            return value is null ? (false, default) : (true, value);
        }
        catch (JsonException)
        {
            return (false, default);
        }
        catch (IOException)
        {
            return (false, default);
        }
    }
}
