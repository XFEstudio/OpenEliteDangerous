namespace XFEEliteDangerous.Bootstrapper;

internal static class SafePaths
{
    internal static string LocalAbsolute(string path)
    {
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||path.StartsWith('\\')||path.Length<3||path[1]!=':'||path.AsSpan(2).Contains(':'))
            throw new ArgumentException("An explicit local drive path is required; UNC, device paths and alternate streams are unsupported.");
        var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if(new DriveInfo(Path.GetPathRoot(full)!).DriveType==DriveType.Network)
            throw new ArgumentException("Mapped network drives are unsupported for native diagnostics.");
        foreach(var segment in full[Path.GetPathRoot(full)!.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries))
        {
            var stem=segment.Split('.')[0];
            if(segment.EndsWith('.')||segment.EndsWith(' ')||stem.Equals("CON",StringComparison.OrdinalIgnoreCase)||stem.Equals("PRN",StringComparison.OrdinalIgnoreCase)||stem.Equals("AUX",StringComparison.OrdinalIgnoreCase)||stem.Equals("NUL",StringComparison.OrdinalIgnoreCase)||stem.Equals("CONIN$",StringComparison.OrdinalIgnoreCase)||stem.Equals("CONOUT$",StringComparison.OrdinalIgnoreCase)||(stem.Length==4&&(stem.StartsWith("COM",StringComparison.OrdinalIgnoreCase)||stem.StartsWith("LPT",StringComparison.OrdinalIgnoreCase))&&stem[3] is >= '1' and <= '9'))
                throw new ArgumentException("DOS device names and ambiguous trailing dots/spaces are unsupported diagnostic paths.");
        }
        return full;
    }
    internal static bool Within(string root,string target)=>string.Equals(root,target,StringComparison.OrdinalIgnoreCase)||target.StartsWith(Path.EndsInDirectorySeparator(root)?root:root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    internal static void NoReparseAncestors(string path)
    {
        path=LocalAbsolute(path);
        var root=Path.GetPathRoot(path)!; var cursor=root;
        // Inspect top-down: inspecting a leaf before its junction parent could already contact a share.
        foreach(var part in path[root.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries).Prepend(string.Empty))
        {
            if(part.Length!=0) cursor=Path.Combine(cursor,part);
            try { if((File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0) throw new IOException("Reparse points are not accepted in diagnostic paths."); }
            catch(FileNotFoundException) { break; }
            catch(DirectoryNotFoundException) { break; }
        }
    }
    internal static string ArtifactsRoot()
    {
        // Status/help never call this. Development follows the executable location, never caller CWD.
        var baseDirectory=LocalAbsolute(AppContext.BaseDirectory); NoReparseAncestors(baseDirectory);
        for(var directory=new DirectoryInfo(baseDirectory);directory is not null;directory=directory.Parent)
            if(File.Exists(Path.Combine(directory.FullName,"XFEEliteDangerous.slnx")))
                return Path.Combine(directory.FullName,"artifacts");
        return Path.Combine(baseDirectory,"diagnostics");
    }
    internal static string ValidateArtifactRoot(string? requested)
    {
        var expected=LocalAbsolute(ArtifactsRoot()); var actual=LocalAbsolute(requested??expected);
        if(!string.Equals(expected,actual,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Diagnostics may write only to this build's project artifacts directory or packaged diagnostics directory.");
        NoReparseAncestors(actual); return actual;
    }
    internal static string NewJsonReportPath(string report)
    {
        report=LocalAbsolute(report); var root=ValidateArtifactRoot(null);
        if(!Within(root,report)||string.Equals(root,report,StringComparison.OrdinalIgnoreCase)||!string.Equals(Path.GetExtension(report),".json",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Reports must be new JSON files inside this build's owned diagnostic directory.");
        NoReparseAncestors(report);
        if(File.Exists(report)||Directory.Exists(report)) throw new IOException("An existing report is never overwritten.");
        return report;
    }
    internal static FileStream CreateNewReport(string report)
    {
        report=NewJsonReportPath(report);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!); NoReparseAncestors(report);
        return new FileStream(report,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
    }
}
