using XFEEliteDangerous.Client;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XFEEliteDangerous.Core;

namespace XFEEliteDangerous.Bootstrapper;

/// <summary>
/// One installation's private settings and rendering caches. A host-only lock
/// prevents concurrent games from writing the same profile. Per-launch tokens,
/// bridge binaries and certificates stay in the separate temporary runtime.
/// </summary>
internal sealed class PersistentGameProfile : IDisposable
{
    private sealed record Marker(int Version,string InstallationKey,string AppContainerName);
    private readonly FileStream lease;
    internal string DirectoryPath { get; }
    internal string AppContainerName { get; }
    internal int ImportedSettingsFiles { get; private set; }

    private PersistentGameProfile(string directory,string appContainerName,FileStream lease)
    {DirectoryPath=directory;AppContainerName=appContainerName;this.lease=lease;}

    internal static PersistentGameProfile Open(string gameDirectory)
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XFEEliteDangerous","GameProfiles");
        return OpenCore(root,gameDirectory);
    }
    internal static PersistentGameProfile OpenOnline(string gameDirectory,OnlineServerProfile server,string account)
    {
        if(account.Length!=32||!account.All(Uri.IsHexDigit))throw new InvalidDataException("Invalid private online account.");
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XFEEliteDangerous","GameProfiles");
        return OpenCore(root,Path.Combine(gameDirectory,"XFE-online-"+server.CertificateSha256+"-"+account));
    }
    internal static PersistentGameProfile OpenDiagnostics(string gameDirectory)
    {
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XFEEliteDangerous","GameProfiles");
        return OpenCore(root,Path.Combine(SafePaths.LocalAbsolute(gameDirectory),"XFE-controller-diagnostic-profile"));
    }

    // The no-game fixture has its own owned root and never touches a player's profile.
    internal static PersistentGameProfile OpenFixture(string fixtureRoot,string installationIdentity)
    {
        var root=SafePaths.LocalAbsolute(fixtureRoot);var artifacts=SafePaths.ValidateArtifactRoot(null);
        var name=Path.GetFileName(root);
        if(!string.Equals(Path.GetDirectoryName(root),artifacts,StringComparison.OrdinalIgnoreCase)
            ||!name.StartsWith("profile-persistence-check-",StringComparison.Ordinal)
            ||!Guid.TryParseExact(name["profile-persistence-check-".Length..],"N",out _))
            throw new ArgumentException("An owned profile-persistence fixture root is required.");
        return OpenCore(Path.Combine(root,"profiles"),installationIdentity);
    }

    private static PersistentGameProfile OpenCore(string storeRoot,string gameDirectory)
    {
        var game=SafePaths.LocalAbsolute(gameDirectory).ToUpperInvariant();
        var key=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(game)))[..32];
        var root=SafePaths.LocalAbsolute(storeRoot);SafePaths.NoReparseAncestors(root);
        Directory.CreateDirectory(root);SafePaths.NoReparseAncestors(root);
        var directory=Path.Combine(root,key);SafePaths.NoReparseAncestors(directory);
        var lockPath=Path.Combine(root,key+".lock");SafePaths.NoReparseAncestors(lockPath);
        FileStream handle;
        try{handle=new(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException failure){throw new IOException(AppText.Get("PersistentGameProfile.Text002"),failure);}
        try
        {
            Directory.CreateDirectory(directory);SafePaths.NoReparseAncestors(directory);
            var expected=new Marker(1,key,"XFEEliteDangerous.Play."+key);
            var markerPath=Path.Combine(directory,"profile.json");SafePaths.NoReparseAncestors(markerPath);
            if(File.Exists(markerPath))
            {
                if(new FileInfo(markerPath).Length>4096||JsonSerializer.Deserialize<Marker>(File.ReadAllText(markerPath))!=expected)
                    throw new InvalidDataException("The persistent private profile marker differs; existing settings were preserved.");
            }
            else
            {
                if(Directory.EnumerateFileSystemEntries(directory).Any())
                    throw new IOException("An unrecognized nonempty profile will not be overwritten.");
                using var output=new FileStream(markerPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
                JsonSerializer.Serialize(output,expected);output.Flush(true);
            }
            return new(directory,expected.AppContainerName,handle);
        }
        catch{handle.Dispose();throw;}
    }

    internal nint CreateOrReuseAppContainer()
    {
        var hr=Native.CreateAppContainerProfile(AppContainerName,"XFE Elite private game","Persistent private settings and GPU cache; no network capabilities.",0,0,out var sid);
        if(hr==unchecked((int)0x800700B7))
        {
            if(sid!=0)Native.FreeSid(sid);
            hr=Native.DeriveAppContainerSidFromAppContainerName(AppContainerName,out sid);
        }
        Marshal.ThrowExceptionForHR(hr);
        if(sid==0)throw new InvalidDataException("The persistent private AppContainer SID is absent.");
        return sid;
    }

    // One-time migration from this launcher's own stopped visible session only.
    // No Windows/Steam profile, journal, credentials or telemetry cache is copied.
    internal void ImportPreviousVisibleSettings()
    {
        var options=Path.Combine(DirectoryPath,"LocalAppData","Frontier Developments","Elite Dangerous","Options");
        if(Directory.Exists(options))return;
        var artifacts=SafePaths.ValidateArtifactRoot(null);
        if(!Directory.Exists(artifacts))return;
        foreach(var runtime in new DirectoryInfo(artifacts).EnumerateDirectories("native-session-*").OrderByDescending(value=>value.CreationTimeUtc))
        {
            if(!Guid.TryParseExact(runtime.Name["native-session-".Length..],"N",out _))continue;
            SafePaths.NoReparseAncestors(runtime.FullName);
            var reports=runtime.EnumerateFiles("session-closed-*.json").Take(2).ToArray();
            if(reports.Length!=1||reports[0].Length>262144)continue;
            using var document=JsonDocument.Parse(File.ReadAllText(reports[0].FullName));var data=document.RootElement;
            if(!data.TryGetProperty("schema",out var schema)||schema.GetString()!="xfeelite.private-native-session.v1"
                ||!data.TryGetProperty("desktopName",out var desktop)||desktop.GetString()!="Default")continue;
            var source=Path.Combine(runtime.FullName,"profile","LocalAppData","Frontier Developments","Elite Dangerous","Options");
            if(!Directory.Exists(source))continue;
            ImportOptions(source,options);return;
        }
    }

    private void ImportOptions(string source,string destination)
    {
        // Native settings only. Development scripts and identity-specific Player
        // data are deliberately excluded from migration, but future preferences
        // written to this persistent profile remain there normally.
        string[] groups=["Audio","Graphics","Controls","Bindings","Startup","GUI"];
        var copies=new List<(string Source,string Target)>();long bytes=0;
        foreach(var group in groups)
        {
            var directory=Path.Combine(source,group);SafePaths.NoReparseAncestors(directory);
            if(!Directory.Exists(directory))continue;
            foreach(var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                SafePaths.NoReparseAncestors(entry.FullName);
                if(entry is not FileInfo file||file.Length>4*1024*1024||file.Length==0
                    ||!(new[]{".xml",".start",".binds",".gui",".fov"}.Contains(file.Extension,StringComparer.OrdinalIgnoreCase)
                        ||file.Name.EndsWith(".binds.backup",StringComparison.OrdinalIgnoreCase)))continue;
                if(copies.Count>=256||(bytes+=file.Length)>16*1024*1024)throw new InvalidDataException("Previous settings exceed the fixed migration bound.");
                copies.Add((file.FullName,Path.Combine(destination,group,file.Name)));
            }
        }
        if(copies.Count==0)return;
        var stage=destination+".import-"+Guid.NewGuid().ToString("N");
        SafePaths.NoReparseAncestors(stage);Directory.CreateDirectory(stage);
        foreach(var copy in copies)
        {
            var target=Path.Combine(stage,Path.GetRelativePath(destination,copy.Target));
            SafePaths.NoReparseAncestors(target);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input=new FileStream(copy.Source,FileMode.Open,FileAccess.Read,FileShare.Read);
            using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.Read);input.CopyTo(output);
        }
        SafePaths.NoReparseAncestors(destination);Directory.Move(stage,destination);
        ImportedSettingsFiles=copies.Count;
    }

    public void Dispose()=>lease.Dispose(); // Ordinary exit keeps settings and the OS GPU-cache profile.
}
