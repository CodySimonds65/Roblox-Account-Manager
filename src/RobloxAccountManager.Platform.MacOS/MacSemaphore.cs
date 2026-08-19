using System.Runtime.InteropServices;

namespace RobloxAccountManager.Platform.MacOS;

/// <summary>Maps sem_unlink's return value and errno without losing the native error.</summary>
public sealed partial class MacSemaphore
{
    public SingletonReleaseResult Unlink(string name = "/RobloxPlayerUniq")
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new SingletonReleaseResult(SingletonReleaseStatus.NotMacOS, 0, null);
        }

        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith('/'))
        {
            throw new ArgumentException("A POSIX semaphore name must start with '/'.", nameof(name));
        }

        var returnCode = NativeMethods.SemUnlink(name);
        // This must happen immediately after the P/Invoke. Any managed call before this can
        // overwrite errno and turn a harmless ENOENT into an opaque failure.
        var nativeError = returnCode == 0 ? 0 : Marshal.GetLastPInvokeError();
        return MacSemaphoreMapping.Map(returnCode, nativeError);
    }

    internal static SingletonReleaseResult MapResultForTests(int returnCode, int nativeError) =>
        MacSemaphoreMapping.Map(returnCode, nativeError);

    private static partial class NativeMethods
    {
        [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "sem_unlink", SetLastError = true,
            StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int SemUnlink(string name);
    }
}

public static class MacSemaphoreMapping
{
    private const int ErrnoNoEntry = 2;
    public static SingletonReleaseResult Map(int returnCode, int nativeError)
    {
        if (returnCode == 0)
        {
            return new SingletonReleaseResult(SingletonReleaseStatus.Removed, 0, null);
        }

        if (nativeError == ErrnoNoEntry)
        {
            return new SingletonReleaseResult(SingletonReleaseStatus.AlreadyAbsent, nativeError, "ENOENT");
        }

        return new SingletonReleaseResult(SingletonReleaseStatus.Failed, nativeError, DescribeErrno(nativeError));
    }

    public static string DescribeErrno(int errno) => errno switch
    {
        1 => "EPERM",
        2 => "ENOENT",
        13 => "EACCES",
        22 => "EINVAL",
        _ => $"errno-{errno}"
    };

}
