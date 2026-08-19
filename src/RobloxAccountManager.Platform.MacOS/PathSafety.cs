namespace RobloxAccountManager.Platform.MacOS;

internal static class PathSafety
{
    public static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static bool IsContainedBy(string root, string candidate, bool allowRoot = false)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (allowRoot && PathsEqual(root, candidate))
        {
            return true;
        }

        return normalizedCandidate.StartsWith(
            normalizedRoot,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static string RequireContainedPath(string root, string candidate, bool allowRoot = false)
    {
        var full = Path.GetFullPath(candidate);
        if (!IsContainedBy(root, full, allowRoot))
        {
            throw new InvalidOperationException("The path is outside the managed macOS runtime root.");
        }

        return full;
    }

    public static void RejectSymlink(string path)
    {
        RejectSymlinkComponents(path);
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Symlinks and reparse points are not valid managed runtime paths.");
        }
    }

    public static void RejectSymlinkDirectory(string path)
    {
        RejectSymlinkComponents(path);
        var info = new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Symlinks and reparse points are not valid managed runtime paths.");
        }
    }

    /// <summary>
    /// Lexical StartsWith checks are not enough when an attacker can replace a parent with a
    /// symlink between validation and deletion/rename. Walk every existing component and reject
    /// links/reparse points before each sensitive operation.
    /// </summary>
    public static void RejectSymlinkComponents(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new InvalidOperationException("Path has no root.");
        var remainder = full[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        foreach (var component in remainder)
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                continue;
            }

            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("A symlink or reparse point was found in a managed path.");
            }

            var directory = new DirectoryInfo(current);
            if (directory.LinkTarget is not null)
            {
                throw new InvalidOperationException("A symbolic link was found in a managed path.");
            }
        }
    }

    public static void EnsureOwnerOnlyDirectory(string path)
    {
        RejectSymlinkComponents(path);
        Directory.CreateDirectory(path);
        RejectSymlinkComponents(path);
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var mode = File.GetUnixFileMode(path);
        const UnixFileMode groupOrOtherBits = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & groupOrOtherBits) != 0)
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
