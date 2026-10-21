using XFEEliteDangerous.Client;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;

namespace XFEEliteDangerous.Core;

public sealed class SharedGalaxyState
{
    public int Schema{get;set;}=1;
    public long Revision{get;set;}
    public Dictionary<string,string> Definitions{get;set;}=new(StringComparer.Ordinal);
    public Dictionary<ulong,string> Systems{get;set;}=[];
}

/// <summary>Server-wide public world data lives outside commander state.json, so
/// a legacy offline runtime cannot erase it when committing its own saved game.</summary>
public sealed class SharedGalaxyStore
{
    public static IReadOnlyList<string> DefinitionOperations{get;}=Array.AsReadOnly(new[]{
        "/2.0/elite/resources/dictionary","/2.0/elite/resources/finance","/2.0/elite/resources/loadingscreentips",
        "/2.0/elite/resources/microresources","/2.0/elite/resources/permits","/2.0/elite/resources/commodities",
        "/2.0/elite/resources/ussregion/list","/2.0/elite/synthesis/recipes","/2.0/elite/powerplay2/powers/list",
        "/2.0/elite/colonisation/resources/optionsfulllist","/2.0/elite/colonisation/resources/marketlink/weighting"});
    private readonly string root,path;
    public SharedGalaxyStore(string workspace){root=Path.GetFullPath(workspace);path=Path.Combine(root,"galaxy.json");}
    public SharedGalaxyState Snapshot()=>WithLease(Load);
    public NativeDataReply? Definition(string operation)
    {
        if(!DefinitionOperations.Contains(operation,StringComparer.Ordinal))return null;
        return WithLease(()=>Load().Definitions.TryGetValue(operation,out var payload)?new NativeDataReply(200,payload):null);
    }
    public NativeDataReply System(ulong address,NativeSystemMetadata? seed)
    {
        return WithLease(()=>
        {
            var state=Load();if(state.Systems.TryGetValue(address,out var payload))return new NativeDataReply(200,payload);
            if(seed is null||seed.Address!=address)return new(404,"{\"error\":\"UnknownSharedSystem\"}");
            payload=seed.Serialize(address);ValidateSystem(address,payload);state.Systems.Add(address,payload);state.Revision=checked(state.Revision+1);Save(state,false);
            return new(200,payload);
        });
    }
    public SharedGalaxyState Update(string version,string kind,string key,string payload)
    {
        return WithLease(()=>
        {
            var state=Load();if(version!=state.Revision.ToString(CultureInfo.InvariantCulture))throw new SaveConflictException();
            try
            {
                if(kind=="definition"){ValidateDefinition(key,payload);state.Definitions[key]=payload;}
                else if(kind=="system")
                {
                    if(!ulong.TryParse(key,NumberStyles.None,CultureInfo.InvariantCulture,out var address))throw new ArgumentException(AppText.Get("SharedGalaxyStore.Text365"));
                    ValidateSystem(address,payload);if(!state.Systems.ContainsKey(address)&&state.Systems.Count>=4096)throw new ArgumentException(AppText.Get("SharedGalaxyStore.Text366"));state.Systems[address]=payload;
                }
                else throw new ArgumentException(AppText.Get("SharedGalaxyStore.Text367"));
            }
            catch(Exception error)when(error is JsonException or XmlException or KeyNotFoundException or InvalidOperationException or FormatException)
            {throw new InvalidDataException(AppText.Get("SharedGalaxyStore.Text368"),error);}
            state.Revision=checked(state.Revision+1);Save(state,true);return state;
        });
    }
    private SharedGalaxyState Load()
    {
        if(!File.Exists(path))return new();
        if(new FileInfo(path).Length>64*1024*1024)throw new InvalidDataException(AppText.Get("SharedGalaxyStore.Text369"));
        var state=JsonSerializer.Deserialize(File.ReadAllBytes(path),CoreJsonContext.Default.SharedGalaxyState)??throw new InvalidDataException(AppText.Get("SharedGalaxyStore.Text370"));
        if(state.Schema!=1||state.Revision<0||state.Definitions is null||state.Systems is null||state.Definitions.Count>DefinitionOperations.Count||state.Systems.Count>4096)throw new InvalidDataException(AppText.Get("SharedGalaxyStore.Text371"));
        foreach(var item in state.Definitions)ValidateDefinition(item.Key,item.Value);foreach(var item in state.Systems)ValidateSystem(item.Key,item.Value);
        return state;
    }
    private void Save(SharedGalaxyState state,bool backup)
    {
        Directory.CreateDirectory(root);
        if(backup&&File.Exists(path))
        {
            var directory=Path.Combine(root,"galaxy-backups");Directory.CreateDirectory(directory);File.Copy(path,Path.Combine(directory,DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")+".json"));
        }
        var temporary=Path.Combine(root,"galaxy-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(file,state,CoreJsonContext.Default.SharedGalaxyState);file.Flush(true);}
            if(File.Exists(path))File.Replace(temporary,path,path+".bak");else File.Move(temporary,path);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private T WithLease<T>(Func<T> action)
    {
        Directory.CreateDirectory(root);var deadline=DateTimeOffset.UtcNow.AddSeconds(10);FileStream? lease=null;
        while(lease is null)
        {
            try{lease=new FileStream(Path.Combine(root,"galaxy.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException){if(DateTimeOffset.UtcNow>deadline)throw;Thread.Sleep(5);}
        }
        using(lease)return action();
    }
    internal static void ValidateDefinition(string operation,string payload)
    {
        Bound(payload);if(!DefinitionOperations.Contains(operation,StringComparer.Ordinal))throw new ArgumentException(AppText.Get("SharedGalaxyStore.Text372"));
        if(operation is "/2.0/elite/synthesis/recipes" or "/2.0/elite/resources/ussregion/list")
        {
            using var reader=XmlReader.Create(new StringReader(payload),new(){DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=2*1024*1024});while(reader.Read()){}
        }
        else
        {
            using var document=JsonDocument.Parse(payload,new(){MaxDepth=64});var value=document.RootElement;
            if(value.ValueKind!=JsonValueKind.Object)throw new ArgumentException(AppText.Get("SharedGalaxyStore.Text373"));
            if(operation=="/2.0/elite/powerplay2/powers/list"&&(!value.TryGetProperty("powers",out var powers)||powers.ValueKind!=JsonValueKind.Array||powers.GetArrayLength() is 0 or >1024||powers.EnumerateArray().Any(item=>item.ValueKind!=JsonValueKind.Object||!item.EnumerateObject().Any())))throw new ArgumentException(AppText.Get("SharedGalaxyStore.Text374"));
            if(operation=="/2.0/elite/resources/commodities"&&(!value.TryGetProperty("commodities",out var commodities)||commodities.ValueKind is not(JsonValueKind.Array or JsonValueKind.Object)))throw new ArgumentException(AppText.Get("SharedGalaxyStore.Text375"));
            if(operation=="/2.0/elite/resources/permits")NativeReferenceLibrary.ValidatePermits(value);
        }
    }
    internal static void ValidateSystem(ulong address,string payload)
    {
        Bound(payload);if(address==0||address>0x007F_FFFF_FFFF_FFFFUL)throw new ArgumentException(AppText.Get("SharedGalaxyStore.Text376"));
        using var document=JsonDocument.Parse(payload,new(){MaxDepth=64});var outer=document.RootElement.GetProperty("starsystem");var system=outer.GetProperty("starsystem");
        if(system.GetProperty("address").GetUInt64()!=address||system.GetProperty("id").GetUInt64()==0||string.IsNullOrWhiteSpace(system.GetProperty("name").GetString())
            ||outer.GetProperty("polities").ValueKind!=JsonValueKind.Object||outer.GetProperty("unownedSettlements").ValueKind!=JsonValueKind.Object)throw new ArgumentException(AppText.Get("SharedGalaxyStore.Text377"));
        foreach(var name in new[]{"wealth","security","standardofliving","developmentlevel"})
        {var value=system.GetProperty(name).GetDouble();if(!double.IsFinite(value)||value<0||value>float.MaxValue)throw new ArgumentException(AppText.Get("SharedGalaxyStore.Text378"));}
        // Preserve nonempty polity documents exactly. Their detailed native
        // schema is not guessed or projected onto a smaller model.
    }
    private static void Bound(string payload){if(string.IsNullOrWhiteSpace(payload)||Encoding.UTF8.GetByteCount(payload)>2*1024*1024)throw new ArgumentException(AppText.Get("SharedGalaxyStore.Text379"));}
}
