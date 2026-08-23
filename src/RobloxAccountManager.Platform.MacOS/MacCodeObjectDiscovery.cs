using System.Buffers.Binary;

namespace RobloxAccountManager.Platform.MacOS;

internal enum MacMachOFileType : uint
{
    Unknown = 0,
    Object = 1,
    Executable = 2,
    FixedVmLibrary = 3,
    Core = 4,
    Preload = 5,
    DynamicLibrary = 6,
    DynamicLinker = 7,
    Bundle = 8,
    DynamicLibraryStub = 9,
    DebugSymbols = 10,
    KernelCollection = 11
}

/// <summary>
/// Finds signed code by inspecting Mach-O headers instead of relying on file extensions.
/// Roblox ships extensionless helper executables beside RobloxPlayer, so suffix-only discovery
/// can leave a vendor-signed helper loading locally re-signed libraries at runtime.
/// </summary>
internal static class MacCodeObjectDiscovery
{
    private const uint MachOMagic32 = 0xFEEDFACE;
    private const uint MachOMagic64 = 0xFEEDFACF;
    private const uint FatMagic32 = 0xCAFEBABE;
    private const uint FatMagic64 = 0xCAFEBABF;

    private static readonly string[] CodeBundleSuffixes =
    [
        ".app",
        ".appex",
        ".bundle",
        ".framework",
        ".plugin",
        ".xpc"
    ];

    public static IReadOnlyList<MacCodeObject> Enumerate(string bundlePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        var root = Path.GetFullPath(bundlePath);
        if (!Directory.Exists(root))
            return Array.Empty<MacCodeObject>();

        var objects = new List<MacCodeObject>();
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if (!PathSafety.IsContainedBy(root, path) || IsSymlink(path))
                continue;

            if (Directory.Exists(path))
            {
                if (CodeBundleSuffixes.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                    objects.Add(new MacCodeObject(path, MacMachOFileType.Bundle, IsBundle: true));
                continue;
            }

            if (TryReadFileType(path, out var fileType))
                objects.Add(new MacCodeObject(path, fileType, IsBundle: false));
        }

        return objects
            .DistinctBy(item => item.Path, StringComparer.Ordinal)
            .OrderByDescending(item => PathDepth(item.Path))
            .ThenBy(item => item.IsBundle)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool TryReadFileType(string path, out MacMachOFileType fileType)
    {
        fileType = MacMachOFileType.Unknown;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return TryReadFileType(stream, out fileType);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TryReadFileType(Stream stream, out MacMachOFileType fileType)
    {
        ArgumentNullException.ThrowIfNull(stream);
        fileType = MacMachOFileType.Unknown;
        Span<byte> header = stackalloc byte[20];
        if (!ReadExactly(stream, header[..4]))
            return false;

        var magicBigEndian = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        var magicLittleEndian = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        var isBigEndianFat = magicBigEndian is FatMagic32 or FatMagic64;
        var isLittleEndianFat = magicLittleEndian is FatMagic32 or FatMagic64;
        if (isBigEndianFat || isLittleEndianFat)
        {
            if (!ReadExactly(stream, header[4..8]))
                return false;
            var architectureCount = isBigEndianFat
                ? BinaryPrimitives.ReadUInt32BigEndian(header[4..8])
                : BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            if (architectureCount is 0 or > 128)
                return false;

            var isFat64 = isBigEndianFat ? magicBigEndian == FatMagic64 : magicLittleEndian == FatMagic64;
            var architectureHeaderSize = isFat64 ? 32 : 20;
            var architectureHeader = new byte[architectureHeaderSize];
            if (!ReadExactly(stream, architectureHeader))
                return false;
            long sliceOffset;
            if (isFat64)
            {
                var rawSliceOffset = isBigEndianFat
                    ? BinaryPrimitives.ReadUInt64BigEndian(architectureHeader.AsSpan(8, 8))
                    : BinaryPrimitives.ReadUInt64LittleEndian(architectureHeader.AsSpan(8, 8));
                if (rawSliceOffset > long.MaxValue)
                    return false;
                sliceOffset = (long)rawSliceOffset;
            }
            else
            {
                sliceOffset = isBigEndianFat
                    ? BinaryPrimitives.ReadUInt32BigEndian(architectureHeader.AsSpan(8, 4))
                    : BinaryPrimitives.ReadUInt32LittleEndian(architectureHeader.AsSpan(8, 4));
            }
            if (!stream.CanSeek || sliceOffset < 0 || sliceOffset > stream.Length - 16)
                return false;

            stream.Position = sliceOffset;
            if (!ReadExactly(stream, header[..16]))
                return false;
            return TryReadThinFileType(header[..16], out fileType);
        }

        if (!ReadExactly(stream, header[4..16]))
            return false;
        return TryReadThinFileType(header[..16], out fileType);
    }

    private static bool TryReadThinFileType(ReadOnlySpan<byte> header, out MacMachOFileType fileType)
    {
        fileType = MacMachOFileType.Unknown;
        if (header.Length < 16)
            return false;

        var magicBigEndian = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        var magicLittleEndian = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        uint rawFileType;
        if (magicBigEndian is MachOMagic32 or MachOMagic64)
            rawFileType = BinaryPrimitives.ReadUInt32BigEndian(header[12..16]);
        else if (magicLittleEndian is MachOMagic32 or MachOMagic64)
            rawFileType = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
        else
            return false;

        fileType = Enum.IsDefined(typeof(MacMachOFileType), rawFileType)
            ? (MacMachOFileType)rawFileType
            : MacMachOFileType.Unknown;
        return true;
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count == 0)
                return false;
            read += count;
        }
        return true;
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static int PathDepth(string path) =>
        path.Count(character => character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar);
}

internal sealed record MacCodeObject(string Path, MacMachOFileType FileType, bool IsBundle)
{
    public bool IsExecutable => !IsBundle && FileType == MacMachOFileType.Executable;
}
