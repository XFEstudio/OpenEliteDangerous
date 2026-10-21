using System.Runtime.InteropServices;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.OfflineRuntime;

/// <summary>Settings and generated caches stay in the same owned profile across every launch.</summary>
internal static unsafe partial class OfflineProfileHooks
{
    private static ImportPointerHooks? imports;
    private static delegate* unmanaged<Guid*,uint,nint,nint*,int> originalKnownFolder;
    private static delegate* unmanaged<nint,int,nint,uint,char*,int> originalFolder;
    private static string local = "", roaming = "", saved = "";
    private static readonly Guid LocalId = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    private static readonly Guid RoamingId = new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D");
    private static readonly Guid SavedId = new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4");
    internal static void Install(nint image, RuntimeConfiguration configuration)
    {
        local = Path.Combine(configuration.ProfileRoot, "LocalAppData");
        roaming = Path.Combine(configuration.ProfileRoot, "RoamingAppData"); saved = Path.Combine(configuration.ProfileRoot, "SavedGames");
        if (new[] {local,roaming,saved}.Any(path => !Directory.Exists(path) || path.Length >= 260)) throw new InvalidDataException("Persistent profile folders are absent or too long.");
        ConfigureRenderCache(Path.GetDirectoryName(configuration.Executable)!, configuration.ProfileRoot);
        var entry = image + 0x1F7730;
        if (!new ReadOnlySpan<byte>((void*)entry,32).SequenceEqual(Convert.FromHexString("48895c24085556574154415541564157488bec4883ec20b916000000e82f9b34")))
            throw new InvalidDataException("The pinned startup-only thread probe differs.");
        // The source-audited startup probe deliberately uses invalid handle 0xFFFF. Its
        // ordinary enum21 is only stored for diagnostics. No global NT API is changed.
        NativeHook.Write(entry, [0xB8,21,0,0,0,0xC3]);
        InstallDisplayDriverVersion(image, configuration.DriverVersion);
        imports = new ImportPointerHooks();
        var originals = imports.Install(image, new Dictionary<string,nint>
        {
            ["SHGetKnownFolderPath"] = (nint)(delegate* unmanaged<Guid*,uint,nint,nint*,int>)&KnownFolder,
            ["SHGetFolderPathW"] = (nint)(delegate* unmanaged<nint,int,nint,uint,char*,int>)&Folder,
            ["CreateFileW"] = (nint)(delegate* unmanaged<char*,uint,uint,nint,uint,uint,nint,nint>)&OpenWithRenderCache
        });
        originalKnownFolder = (delegate* unmanaged<Guid*,uint,nint,nint*,int>)originals["SHGetKnownFolderPath"];
        originalFolder = (delegate* unmanaged<nint,int,nint,uint,char*,int>)originals["SHGetFolderPathW"];
        originalCreateFile = (delegate* unmanaged<char*,uint,uint,nint,uint,uint,nint,nint>)originals["CreateFileW"];
    }
    [UnmanagedCallersOnly] private static int KnownFolder(Guid* id, uint flags, nint token, nint* output)
    {
        if (id == null || output == null) return unchecked((int)0x80070057);
        var path = *id == LocalId ? local : *id == RoamingId ? roaming : *id == SavedId ? saved : null;
        if (path is null) return originalKnownFolder(id, flags, token, output);
        try { *output = Marshal.StringToCoTaskMemUni(path); return 0; }
        catch(Exception error) { OfflineDiagnostics.Current?.Write("error","native.profile","folder.redirect.failed","Known-folder redirect failed.",exception:error);*output = 0; return unchecked((int)0x8007000e); }
    }
    [UnmanagedCallersOnly] private static int Folder(nint window, int csidl, nint token, uint flags, char* output)
    {
        var path = (csidl & 0xff) == 0x1c ? local : (csidl & 0xff) == 0x1a ? roaming : null;
        if (path is null) return originalFolder(window, csidl, token, flags, output);
        if (output == null) return unchecked((int)0x80070057);
        path.AsSpan().CopyTo(new Span<char>(output, 260)); output[path.Length] = '\0'; return 0;
    }
}
