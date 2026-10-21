using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XFEEliteDangerous.Bootstrapper;

public sealed record PreparedProfileSetting(string RelativePath,int Bytes,string Sha256);
public sealed record OwnedProfileSettingsReceipt(DateTimeOffset PreparedAt,IReadOnlyList<PreparedProfileSetting> Files,
    bool ReadBackVerified,bool ExistingFilesOverwritten)
{
    public bool Persistent { get; init; }
    public int ExistingFilesPreserved { get; init; }
    public int MigratedSettingsFiles { get; init; }
}

/// <summary>Only two native, non-identity defaults inside one new owned diagnostic profile.
/// This is not a general profile copier, arbitrary settings writer or completion-flag editor.</summary>
internal static class OwnedProfileSettings
{
    internal const string StartupRelativePath=@"LocalAppData\Frontier Developments\Elite Dangerous\Options\Startup\Settings.xml";
    internal const string GraphicsRelativePath=@"LocalAppData\Frontier Developments\Elite Dangerous\Options\Graphics\StartPreset.start";
    private const string StartupXml="<?xml version=\"1.0\" encoding=\"UTF-8\"?><Startup><RenderVersion>2</RenderVersion></Startup>";

    internal static async Task<OwnedProfileSettingsReceipt> InitializePersistentAsync(PersistentGameProfile profile,CancellationToken cancellation=default)
    {
        var prepared=new List<PreparedProfileSetting>(2);var preserved=0;
        foreach(var definition in new[]{(Relative:StartupRelativePath,Bytes:Encoding.UTF8.GetBytes(StartupXml)),
                    (Relative:GraphicsRelativePath,Bytes:Encoding.ASCII.GetBytes("High"))})
        {
            cancellation.ThrowIfCancellationRequested();
            var path=Path.Combine(profile.DirectoryPath,definition.Relative);SafePaths.NoReparseAncestors(path);
            if(!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);SafePaths.NoReparseAncestors(path);
                await using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,4096,FileOptions.Asynchronous);
                await output.WriteAsync(definition.Bytes,cancellation).ConfigureAwait(false);await output.FlushAsync(cancellation).ConfigureAwait(false);
            }
            else preserved++;
            SafePaths.NoReparseAncestors(path);
            if(new FileInfo(path).Length is <1 or >1048576)throw new InvalidDataException("Existing game preferences are invalid; they were preserved for repair.");
            var actual=await File.ReadAllBytesAsync(path,cancellation).ConfigureAwait(false);
            if(definition.Relative==StartupRelativePath)
            {
                using var reader=XmlReader.Create(new MemoryStream(actual),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null});
                if(XDocument.Load(reader).Root?.Name!="Startup")throw new InvalidDataException("Existing startup settings have an unexpected root; no settings were overwritten.");
            }
            prepared.Add(new(definition.Relative,actual.Length,Convert.ToHexStringLower(SHA256.HashData(actual))));
        }
        return new(DateTimeOffset.UtcNow,prepared.AsReadOnly(),true,false)
        {Persistent=true,ExistingFilesPreserved=preserved,MigratedSettingsFiles=profile.ImportedSettingsFiles};
    }

    internal static async Task<OwnedProfileSettingsReceipt> InitializeAsync(string runtimeDirectory,CancellationToken cancellation=default)
    {
        var runtime=SafePaths.LocalAbsolute(runtimeDirectory);
        var artifacts=SafePaths.ValidateArtifactRoot(null);
        var leaf=Path.GetFileName(runtime);
        if(!string.Equals(Path.GetDirectoryName(runtime),artifacts,StringComparison.OrdinalIgnoreCase)
            ||!leaf.StartsWith("native-session-",StringComparison.Ordinal)
            ||!Guid.TryParseExact(leaf["native-session-".Length..],"N",out _))
            throw new ArgumentException("Fixed defaults may be prepared only in a new owned diagnostic runtime.");
        SafePaths.NoReparseAncestors(runtime);
        var profile=Path.Combine(runtime,"profile");
        SafePaths.NoReparseAncestors(profile);
        if(!Directory.Exists(runtime)||!Directory.Exists(profile)) throw new DirectoryNotFoundException("The owned profile must exist before preparing settings.");
        var definitions=new[]{(Relative:StartupRelativePath,Bytes:Encoding.UTF8.GetBytes(StartupXml)),
            (Relative:GraphicsRelativePath,Bytes:Encoding.ASCII.GetBytes("High"))};
        // Preflight both fixed paths before creating either file. FileMode.CreateNew also closes an
        // overwrite race; any failure aborts session creation rather than reusing a partial profile.
        foreach(var definition in definitions)
        {
            var path=Path.Combine(profile,definition.Relative);
            SafePaths.NoReparseAncestors(path);
            if(!SafePaths.Within(profile,path)||File.Exists(path)||Directory.Exists(path))
                throw new IOException("Existing profile settings are never overwritten.");
        }
        var prepared=new List<PreparedProfileSetting>(2);
        foreach(var definition in definitions)
        {
            cancellation.ThrowIfCancellationRequested();
            var path=Path.Combine(profile,definition.Relative);
            var parent=Path.GetDirectoryName(path)!;
            SafePaths.NoReparseAncestors(parent); Directory.CreateDirectory(parent); SafePaths.NoReparseAncestors(path);
            await using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,4096,FileOptions.Asynchronous))
            {
                await output.WriteAsync(definition.Bytes,cancellation).ConfigureAwait(false);
                await output.FlushAsync(cancellation).ConfigureAwait(false);
            }
            SafePaths.NoReparseAncestors(path);
            var actual=await File.ReadAllBytesAsync(path,cancellation).ConfigureAwait(false);
            if(!actual.AsSpan().SequenceEqual(definition.Bytes)) throw new InvalidDataException("A fixed profile default failed read-back validation.");
            if(definition.Relative==StartupRelativePath)
            {
                using var reader=XmlReader.Create(new MemoryStream(actual),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null});
                var document=XDocument.Load(reader);
                if(document.Root is not {Name:var name}||name!="Startup"||document.Root.HasAttributes
                    ||document.Root.Elements().Count()!=1||document.Root.Element("RenderVersion") is not {Value:"2",HasAttributes:false} render||render.HasElements)
                    throw new InvalidDataException("The fixed startup default has an unexpected schema.");
            }
            prepared.Add(new(definition.Relative,actual.Length,Convert.ToHexString(SHA256.HashData(actual)).ToLowerInvariant()));
        }
        return new(DateTimeOffset.UtcNow,prepared.AsReadOnly(),true,false);
    }
}
