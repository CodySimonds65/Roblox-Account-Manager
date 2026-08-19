using System.Collections.Concurrent;
using System.Text.Json;

namespace RobloxAccountManager.Core.Data;

public static class JsonFileStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<T> LoadAsync<T>(string path, T fallback, JsonSerializerOptions options, CancellationToken cancellationToken = default)
    {
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await TryLoadAsync<T>(path, options, cancellationToken).ConfigureAwait(false);
            if (loaded is not null) return loaded;
            var backup = await TryLoadAsync<T>(path + ".bak", options, cancellationToken).ConfigureAwait(false);
            if (backup is not null)
            {
                File.Copy(path + ".bak", path, true);
                return backup;
            }
            return fallback;
        }
        finally { gate.Release(); }
    }

    public static async Task SaveAsync<T>(string path, T value, JsonSerializerOptions options, CancellationToken cancellationToken = default)
    {
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = path + ".tmp";
        var backup = path + ".bak";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (File.Exists(path)) File.Replace(temporary, path, backup, true);
            else { File.Move(temporary, path); File.Copy(path, backup, true); }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            gate.Release();
        }
    }

    private static async Task<T?> TryLoadAsync<T>(string path, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return default;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            return await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException) { return default; }
        catch (IOException) { return default; }
    }
}
