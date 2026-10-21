namespace XFEEliteDangerous.Client;

/// <summary>Reject network paths before file operations. Runtime data stays on local drives.</summary>
public static class LocalPaths
{
    public static string Directory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\0'))
            throw new ArgumentException(AppText.Get("LocalPaths.Text034"));
        var full = Path.GetFullPath(path); var root = Path.GetPathRoot(full)!;
        if (OperatingSystem.IsWindows() && (root.StartsWith("\\\\", StringComparison.Ordinal)
            || new DriveInfo(root).DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory || full.AsSpan(root.Length).Contains(':')))
            throw new ArgumentException(AppText.Get("LocalPaths.Text035"));
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            if ((System.IO.Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException(AppText.Get("LocalPaths.Text036"));
        return full;
    }
    public static string FilePath(string path)
    {
        var full = Path.GetFullPath(path); Directory(Path.GetDirectoryName(full)!);
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException(AppText.Get("LocalPaths.Text037")); return full;
    }
}
