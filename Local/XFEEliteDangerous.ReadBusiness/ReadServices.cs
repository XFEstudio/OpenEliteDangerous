using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.ReadBusiness;

public static unsafe class ReadServices
{
    [UnmanagedCallersOnly(EntryPoint="XfeReadModuleVersion")]
    public static int Version()=>1;
    [UnmanagedCallersOnly(EntryPoint="XfeReadModuleFree")]
    public static void Free(byte* buffer)=>NativeMemory.Free(buffer);
    [UnmanagedCallersOnly(EntryPoint="XfeReadModuleInvoke")]
    public static int Invoke(byte* input,int length,byte** output,int* outputLength)
    {
        if(output==null||outputLength==null)return 2;
        *output=null;*outputLength=0;
        try
        {
            if(input==null||length is <=0 or >65536)throw new InvalidDataException("Invalid read request buffer.");
            var request=JsonSerializer.Deserialize(new ReadOnlySpan<byte>(input,length),NativeReadModuleJson.Default.NativeReadModuleRequest)
                ??throw new InvalidDataException("Missing read request.");
            if(request.Protocol!=1||request.Fields is null)throw new InvalidDataException("Unsupported read request contract.");
            var reply=Handle(request);if(reply is null)return 1;
            Write(reply,output,outputLength);return 0;
        }
        catch(Exception error)
        {
            try{Write(new(1,500,"",error.GetType().FullName,error.Message,error.ToString()),output,outputLength);}catch{}
            return 2;
        }
    }
    internal static NativeReadModuleReply? Handle(NativeReadModuleRequest request)
    {
        switch(request.Operation)
        {
            case "/2.0/elite/user/friends":
            case "/2.0/elite/user/ignore":
                // Cold-start responses360/361 use data/users. This authored
                // SOLO world has no contact relationships or blocked users;
                // never reuse the online account's captured list. Contact
                // mutations remain unimplemented in the fixed authority.
                return new(1,200,"<data><users /></data>");
            case "/2.0/elite/initiative/commander/list":
                // Cold-start response359 confirms the empty collection shape.
                // There are no community initiatives in this local world.
                return new(1,200,"<data><initiatives /></data>");
            case "/2.0/elite/colonisation/starsystem/info":
            case "/2.0/elite/colonisation/starsystem/constructioneffortmaterials":
                //3410B40 uses lowercase systemaddress for info. The captured
                //materials query uses systemAddress. These are distinct wire
                //selectors, not the older survey API's systemAddr spelling.
                var colonisationInfo=request.Operation.EndsWith("/info",StringComparison.Ordinal);
                var selector=colonisationInfo?"systemaddress":"systemAddress";
                if(!request.Fields.TryGetValue(selector,out var colonisationText)
                    ||!ulong.TryParse(colonisationText,NumberStyles.None,CultureInfo.InvariantCulture,out var colonisationSystem)
                    ||colonisationSystem==0||colonisationSystem>0x007F_FFFF_FFFF_FFFFUL)
                    return new(1,400,"{\"error\":\"Invalid local colonisation system address.\"}");
                if(colonisationSystem!=request.CurrentSystem)return new(1,404,"{\"error\":\"No recorded local colonisation system.\"}");
                // The authored local world has no construction projects.
                // Online process-r3 responses48/199 and cold-start342 return
                //204 for absent info; materials24/119 return this empty array.
                return colonisationInfo?new(1,204,""):new(1,200,"{\"construction_efforts\":[]}");
            case "/2.0/elite/survey/info":
            case "/2.0/elite/fleetcarrier/systemmap":
            case "/2.0/elite/fleetcarrier/system":
                if(!request.Fields.TryGetValue("systemAddr",out var addressText)
                    ||!ulong.TryParse(addressText,NumberStyles.None,CultureInfo.InvariantCulture,out var address)||address==0)
                    return new(1,400,"{\"error\":\"Invalid local survey address.\"}");
                if(address!=request.CurrentSystem)return new(1,404,"{\"error\":\"No local survey record for this system.\"}");
                if(request.Operation=="/2.0/elite/fleetcarrier/system")
                    return new(1,200,"{\"carriers\":{}}");
                if(request.Operation=="/2.0/elite/fleetcarrier/systemmap")
                    //3B3F650/3B41500 test membership in these U64 arrays.
                    //No fleet-carrier destinations are defined in this world.
                    return new(1,200,"{\"systemAddress\":"+address.ToString(CultureInfo.InvariantCulture)
                        +",\"availableLocations\":[],\"viableLocations\":[]}");
                // The authored local Sol start knows its primary star. The
                // isolated native Location journal identifies Sol/BodyID0 as
                // a Star. Body address packing keeps system bits0..54 and
                // body bits55..63, so that star's address equals the system.
                //13A1F60 reads item/bodyAddr and bitmask; this is basic local
                //knowledge (1), with no discoverer or mapping credit. An empty
                //body ledger left the local System Map without visible bodies.
                var bodies=address==10477373803UL
                    ?"<bodies><item><bitmask>1</bitmask><bodyAddr>10477373803</bodyAddr><discoverers /><mapperNames /><mappers /><mapperName /><visitors /></item></bodies>"
                    :"<bodies />";
                return new(1,200,"<data><knownSystems><starSystem><systemAddr>"
                    +address.ToString(CultureInfo.InvariantCulture)
                    +"</systemAddr>"+bodies+"</starSystem></knownSystems></data>");
            case "/2.0/elite/powerplay2/galaxydigest_systemdata_part":
            case "/2.0/elite/powerplay2/galaxydigest_score_part":
                uint number=0;
                if(request.Fields.TryGetValue("pageNumber",out var page)
                    &&(!uint.TryParse(page,NumberStyles.None,CultureInfo.InvariantCulture,out number)||number>1024))
                    return new(1,400,"{\"error\":\"Invalid local digest page.\"}");
                //3464C40 stops the native page task on HTTP 204. Even an empty
                //200 page is a data page, so returning 200 forever never makes
                //Powerplay ready. Initial page 0 is read by 1405930/1405510;
                //their asynchronous continuation starts at page 1 (141E270).
                if(number>0)return new(1,204,"");
                //1417BC0/1417230 accept the systems array, including an empty
                //one. This private world has no Powerplay control or score rows;
                //the separate nonempty global powers catalogue remains intact.
                return new(1,200,"{\"systems\":[],\"lastModified\":"
                    +DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)+"}");
            case "/2.0/elite/codex/discovery/summary":
                //1155F45 looks up the current region in this summary before it
                //accepts a region reply. An empty summary parses but cannot load a world.
                using(var summary=new MemoryStream())
                {
                    using(var writer=new Utf8JsonWriter(summary))
                    {
                        writer.WriteStartObject();writer.WriteStartArray("regions");
                        for(var id=1;id<=42;id++)WriteRegion(writer,id,"categories");
                        writer.WriteEndArray();writer.WriteEndObject();
                    }
                    return new(1,200,Encoding.UTF8.GetString(summary.ToArray()));
                }
            case "/2.0/elite/codex/discovery/region":
                if(!request.Fields.TryGetValue("regionId",out var raw)||!int.TryParse(raw,NumberStyles.None,CultureInfo.InvariantCulture,out var region)
                    ||region is <1 or >42)return new(1,400,"{\"error\":\"Invalid local codex region.\"}");
                // A fresh local discovery ledger has no reports, rumours or commander records.
                // The name is a global localization identifier, not captured player progress.
                using(var stream=new MemoryStream())
                {
                    using(var writer=new Utf8JsonWriter(stream))
                    {
                        WriteRegion(writer,region,"discoveries");
                    }
                    return new(1,200,Encoding.UTF8.GetString(stream.ToArray()));
                }
            default:return null;
        }
    }
    private static void WriteRegion(Utf8JsonWriter writer,int region,string collection)
    {
        writer.WriteStartObject();writer.WriteNumber("id",region);
        writer.WriteString("name","$Codex_RegionName_"+region.ToString(CultureInfo.InvariantCulture)+";");
        writer.WriteNumber("rumoured",0);writer.WriteNumber("reported",0);writer.WriteNumber("discovered",0);
        writer.WriteStartArray(collection);writer.WriteEndArray();writer.WriteNumber("available",0);writer.WriteEndObject();
    }
    private static void Write(NativeReadModuleReply reply,byte** output,int* length)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(reply,NativeReadModuleJson.Default.NativeReadModuleReply);
        if(bytes.Length>8*1024*1024)throw new InvalidDataException("Read reply exceeds its bound.");
        var buffer=(byte*)NativeMemory.Alloc((nuint)bytes.Length);bytes.CopyTo(new Span<byte>(buffer,bytes.Length));
        *length=bytes.Length;*output=buffer;
    }
}
