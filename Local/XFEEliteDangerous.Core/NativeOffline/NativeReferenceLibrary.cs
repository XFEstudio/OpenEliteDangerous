using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Core;

/// <summary>Versioned local definitions only; player state and session responses cannot be installed here.</summary>
internal sealed class NativeReferenceLibrary
{
    private const string GameHash = "e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988";
    private const int MaximumDocument = 16 * 1024 * 1024;
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
    {
        "/2.0/elite/resources/dictionary", "/2.0/elite/resources/finance",
        "/2.0/elite/resources/loadingscreentips", "/2.0/elite/resources/microresources",
        "/2.0/elite/resources/permits",
        "/2.0/elite/resources/commodities",
        "/2.0/elite/resources/ussregion/list", "/2.0/elite/synthesis/recipes",
        "/2.0/elite/powerplay2/powers/list", "/2.0/elite/colonisation/resources/optionsfulllist",
        "/2.0/elite/colonisation/resources/marketlink/weighting"
    };
    private readonly string root;
    private Dictionary<string, NativeDataReply> documents = new(StringComparer.Ordinal);
    private string acceptedManifest="",lastFailure="";

    internal NativeReferenceLibrary(string workspace)
    {
        root = Path.Combine(Path.GetFullPath(workspace), "native-resources");
        if (!Directory.Exists(root)) return;
        var manifest = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifest)) throw new InvalidDataException("The local native resource manifest is missing.");
        var content=Read(manifest,64*1024);
        documents=Load(root,content);acceptedManifest=Convert.ToHexStringLower(SHA256.HashData(content));
    }
    private static Dictionary<string,NativeDataReply> Load(string root,byte[] manifest)
    {
        var documents=new Dictionary<string,NativeDataReply>(StringComparer.Ordinal);
        try
        {
            NoLink(root);
            using var parsed = JsonDocument.Parse(manifest);
            var metadata = parsed.RootElement;
            if (metadata.GetProperty("schema").GetInt32() != 1 || metadata.GetProperty("gameSha256").GetString() != GameHash
                || metadata.GetProperty("build").GetString() != "2026.09.03.332841")
                throw new InvalidDataException("The local native resource version differs from the supported game.");
            var entries = metadata.GetProperty("documents");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > Operations.Count)
                throw new InvalidDataException("Invalid local native resource collection.");
            var bytes = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                var operation = entry.GetProperty("operation").GetString() ?? "";
                var filename = entry.GetProperty("file").GetString() ?? "";
                var hash = entry.GetProperty("sha256").GetString() ?? "";
                if (!Operations.Contains(operation) || documents.ContainsKey(operation)
                    || filename.Length is 0 or >160 || filename.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('-' or '_' or '.'))
                    || Path.GetFileName(filename) != filename || Path.GetExtension(filename) !=
                        (operation is "/2.0/elite/synthesis/recipes" or "/2.0/elite/resources/ussregion/list" ? ".xml" : ".json"))
                    throw new InvalidDataException("Unsupported or duplicate local native resource.");
                var content = Read(Path.Combine(root, filename), MaximumDocument);
                bytes = checked(bytes + content.Length);
                if (bytes > 64 * 1024 * 1024 || hash != Convert.ToHexStringLower(SHA256.HashData(content)))
                    throw new InvalidDataException("Local native resource integrity verification failed.");
                if (filename.EndsWith(".json", StringComparison.Ordinal))
                {
                    using var document = JsonDocument.Parse(content);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("A native definition document must be an object.");
                    if (operation == "/2.0/elite/powerplay2/powers/list")
                    {
                        var powers = document.RootElement.GetProperty("powers");
                        if (powers.ValueKind != JsonValueKind.Array || powers.GetArrayLength() is 0 or >1024
                            || powers.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Any()))
                            throw new InvalidDataException("An empty Powerplay catalogue cannot initialize the native spatial index.");
                    }
                    if(operation=="/2.0/elite/resources/permits")ValidatePermits(document.RootElement);
                }
                else
                {
                    using var reader = XmlReader.Create(new MemoryStream(content, false), new XmlReaderSettings
                        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumDocument });
                    while (reader.Read()) { }
                }
                documents.Add(operation, new(200, new UTF8Encoding(false, true).GetString(content)));
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or XmlException or DecoderFallbackException or OverflowException)
        {
            throw new InvalidDataException("The local native reference library is invalid.", error);
        }
        return documents;
    }

    internal NativeDataReply? Read(string operation)
    {
        if(!Operations.Contains(operation))return null;
        var manifest=Path.Combine(root,"manifest.json");var candidate="unreadable";
        try
        {
            if(File.Exists(manifest))
            {
                NoLink(root);var content=Read(manifest,64*1024);
                candidate=Convert.ToHexStringLower(SHA256.HashData(content));
                if(candidate!=acceptedManifest)
                {
                    var replacement=Load(root,content); // All documents validate before the snapshot is replaced.
                    documents=replacement;acceptedManifest=candidate;lastFailure="";
                    OfflineDiagnostics.Current?.Write("info","resources.hotload","resources.activated","Validated local resource snapshot activated on this request.",
                        new Dictionary<string,string?>{["manifestRevision"]=candidate[..12],["documents"]=replacement.Count.ToString()});
                }
            }
        }
        catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if(candidate!=lastFailure)
            {
                lastFailure=candidate;
                OfflineDiagnostics.Current?.Write("error","resources.hotload","resources.reload.rejected","Resource reload rejected; the last complete snapshot remains active.",exception:error);
            }
        }
        return documents.GetValueOrDefault(operation);
    }
    internal static void ValidatePermits(JsonElement root)
    {
        var permits=root.GetProperty("permits");
        if(root.EnumerateObject().Count()!=1||permits.ValueKind!=JsonValueKind.Object||permits.EnumerateObject().Count()>4096)
            throw new InvalidDataException("A native permit catalogue must contain an ID-keyed object map.");
        foreach(var entry in permits.EnumerateObject())
        {
            var value=entry.Value;
            if(!uint.TryParse(entry.Name,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var id)
                ||id>ushort.MaxValue||value.ValueKind!=JsonValueKind.Object||value.EnumerateObject().Count()!=3
                ||value.GetProperty("id").GetUInt32()!=id||value.GetProperty("description").ValueKind!=JsonValueKind.String
                ||value.GetProperty("description").GetString()!.Length>4096)
                throw new InvalidDataException("Invalid native permit definition.");
            var systems=value.GetProperty("systems");
            if(systems.ValueKind!=JsonValueKind.Array||systems.GetArrayLength()>65_536
                ||systems.EnumerateArray().Any(system=>!system.TryGetUInt64(out var address)||address==0))
                throw new InvalidDataException("Invalid native permit system definitions.");
        }
    }

    private static byte[] Read(string path, int maximum)
    {
        NoLink(path);
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length is 0 || source.Length > maximum) throw new InvalidDataException("Invalid local native resource size.");
        var content = new byte[checked((int)source.Length)];
        source.ReadExactly(content);
        return content;
    }
    private static void NoLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A local native resource cannot use a filesystem link.");
    }
}
