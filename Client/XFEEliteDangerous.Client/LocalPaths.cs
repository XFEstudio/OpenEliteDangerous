namespace XFEEliteDangerous.Client;

/// <summary>Reject network paths before file operations. Runtime data stays on local drives.</summary>
public static class LocalPaths
{
    public static string Directory(string path)
    {
        var full = Normalize(path);
        // The restricted runtime can open its workspace without READ_ATTRIBUTES on every parent.
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((System.IO.Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException(AppText.Get("LocalPaths.Text036"));
        return full;
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\0'))
            throw new ArgumentException(AppText.Get("LocalPaths.Text034"));
        var full = Path.GetFullPath(path); var root = Path.GetPathRoot(full)!;
        if (OperatingSystem.IsWindows() && (root.StartsWith("\\\\", StringComparison.Ordinal)
            || new DriveInfo(root).DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory || full.AsSpan(root.Length).Contains(':')))
            throw new ArgumentException(AppText.Get("LocalPaths.Text035"));
        return full;
    }

    internal static string CheckedDiscoveryPath(string path)
    {
        var full = Normalize(path);
        var root = Path.GetPathRoot(full)!;
        // Inspect the parent before its children so a junction cannot send a probe to a remote share.
        var current = root;
        foreach (var segment in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Prepend(""))
        {
            if (segment.Length != 0) current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException(AppText.Get("LocalPaths.Text036"));
            }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
        }
        return full;
    }
    public static string FilePath(string path)
    {
        var full = Path.GetFullPath(path); Directory(Path.GetDirectoryName(full)!);
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException(AppText.Get("LocalPaths.Text037")); return full;
    }
}
