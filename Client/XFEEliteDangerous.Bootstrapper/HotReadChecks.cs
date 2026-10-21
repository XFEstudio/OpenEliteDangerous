using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using XFEEliteDangerous.Client;
using XFEEliteDangerous.Core;

namespace XFEEliteDangerous.Bootstrapper;

internal static class HotReadChecks
{
    internal static async Task RunAsync(string workspace,string account,string reports)
    {
        Directory.CreateDirectory(reports);
        using var diagnostics=new OfflineDiagnosticLog(reports,"hot-read-check","same-process-read-check");
        OfflineDiagnostics.Current=diagnostics;
        var root=Path.Combine(workspace,"native-business");var manifest=Path.Combine(root,"active.json");
        SafePaths.NoReparseAncestors(manifest);
        var original=await File.ReadAllBytesAsync(manifest);
        var metadata=JsonNode.Parse(original)?.AsObject()??throw new InvalidDataException("A published read module is required.");
        try
        {
            await using var data=new OfflineGameData(workspace,account);
            if((await data.InvokeAsync("GET","/2.0/elite/user/login",new Dictionary<string,string>(),[])).Status!=200)
                throw new InvalidDataException("Local check identity failed.");
            var runId=data.RunId;var world=data.World;
            var before=await data.InvokeAsync("GET","/2.0/elite/commander/savegame",new Dictionary<string,string>(),[]);
            async Task<NativeDataReply> Region()=>await data.InvokeAsync("GET","/2.0/elite/codex/discovery/region",new Dictionary<string,string>{["regionId"]="18"},[]);
            var first=await Region();if(first.Status!=200)throw new InvalidDataException("The first native read module failed.");
            foreach(var (operation,collection) in new[]{
                ("/2.0/elite/user/friends","users"),
                ("/2.0/elite/user/ignore","users"),
                ("/2.0/elite/initiative/commander/list","initiatives")})
            {
                var document=await data.InvokeAsync("GET",operation,new Dictionary<string,string>(),[]);
                var listRoot=System.Xml.Linq.XDocument.Parse(document.Payload).Root;
                if(document.Status!=200||listRoot?.Name.LocalName!="data"||listRoot.Elements().Count()!=1
                    ||listRoot.Element(collection) is not {} rows||rows.HasElements||rows.Value!="")
                    throw new InvalidDataException("The empty local relationship/initiative read contract failed: "+operation);
                if((await data.InvokeAsync("GET",operation,new Dictionary<string,string>{["cmdrId"]="0"},[])).Status!=401
                    ||(await data.InvokeAsync("PUT",operation,new Dictionary<string,string>(),[])).Status!=501)
                    throw new InvalidDataException("A local list read bypassed identity or accepted a mutation: "+operation);
            }
            VerifyRpc(Path.Combine(root,metadata["file"]!.GetValue<string>()));
            NativeSaveModuleRequest saveRequest;
            await using(var store=new StateStore(workspace))saveRequest=await store.Read(state=>
            {
                var owner=state.Accounts[account];var saved=owner.NativeSave!.ExportCurrent(owner);
                var upload=Encoding.UTF8.GetBytes("{\"commander\":{\"vehicleDistanceTravelled\":0,\"onFootDistanceTravelled\":0,\"startupState\":0,\"nearplanet\":false,\"atrLevel\":0,\"markedWanted\":false},"
                    +"\"vessels\":[{\"ownedshipid\":"+saved.Snapshot.CurrentVesselId+",\"prototype\":\"SideWinder\",\"location\":{},\"state\":{\"capacitors.0.energy\":18000000}}]}");
                return new NativeSaveModuleRequest(1,owner.NativeUserId,owner.Commander.Id,owner.Commander.Revision,true,
                    JsonSerializer.Serialize(saved,Wire.Json),upload);
            });
            VerifySave(Path.Combine(root,metadata["file"]!.GetValue<string>()),saveRequest);
            var system=JsonSerializer.Deserialize<NativeCommanderSave>(saveRequest.SaveJson,Wire.Json)!.Profile.CurrentSystem;
            var survey=await data.InvokeAsync("GET","/2.0/elite/survey/info",new Dictionary<string,string>{["systemAddr"]=system.ToString(System.Globalization.CultureInfo.InvariantCulture)},[]);
            var surveyRow=System.Xml.Linq.XDocument.Parse(survey.Payload).Root?.Element("knownSystems")?.Element("starSystem");
            if(survey.Status!=200||surveyRow?.Element("systemAddr")?.Value!=system.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ||surveyRow.Element("bodies") is not {} bodies)
                throw new InvalidDataException("Native local survey did not identify the requested system.");
            if(system==10477373803)
            {
                var body=surveyRow.Element("bodies")!.Elements().Single();
                if(body.Name.LocalName!="item"||body.Element("bodyAddr")?.Value!="10477373803"||body.Element("bitmask")?.Value!="1"
                    ||new[]{"discoverers","mapperNames","mappers","mapperName","visitors"}.Any(name=>body.Element(name) is not {} value||value.HasElements||value.Value!=""))
                    throw new InvalidDataException("Local Sol survey did not preserve its primary star without player discovery credit.");
            }
            else if(surveyRow.Element("bodies")!.HasElements)throw new InvalidDataException("An unseeded system received invented survey knowledge.");
            foreach(var (operation,selector,status) in new[]{
                ("/2.0/elite/colonisation/starsystem/info","systemaddress",204),
                ("/2.0/elite/colonisation/starsystem/constructioneffortmaterials","systemAddress",200)})
            {
                var document=await data.InvokeAsync("GET",operation,new Dictionary<string,string>{[selector]=system.ToString(System.Globalization.CultureInfo.InvariantCulture)},[]);
                if(document.Status!=status)throw new InvalidDataException("Local no-project colonisation contract failed.");
                if(status==204){if(document.Payload!="")throw new InvalidDataException("No-content colonisation response contains a body.");}
                else using(var materials=JsonDocument.Parse(document.Payload))
                    if(materials.RootElement.GetProperty("construction_efforts").GetArrayLength()!=0)
                        throw new InvalidDataException("Fresh private world received invented construction materials.");
                if((await data.InvokeAsync("GET",operation,new Dictionary<string,string>{[selector]="0"},[])).Status!=400
                    ||(await data.InvokeAsync("GET",operation,new Dictionary<string,string>{[selector]=(system+1).ToString(System.Globalization.CultureInfo.InvariantCulture)},[])).Status!=404
                    ||(await data.InvokeAsync("GET",operation,new Dictionary<string,string>{["systemAddr"]=system.ToString(System.Globalization.CultureInfo.InvariantCulture)},[])).Status!=400)
                    throw new InvalidDataException("Colonisation query accepted an invalid address or the wrong selector spelling.");
            }
            if((await data.InvokeAsync("GET","/2.0/elite/survey/info",new Dictionary<string,string>{["systemAddr"]="0"},[])).Status!=400
                ||(await data.InvokeAsync("GET","/2.0/elite/survey/info",new Dictionary<string,string>{["systemAddr"]=(system+1).ToString(System.Globalization.CultureInfo.InvariantCulture)},[])).Status!=404)
                throw new InvalidDataException("Native local survey accepted an invalid or unrecorded system.");
            var carrierMap=await data.InvokeAsync("GET","/2.0/elite/fleetcarrier/systemmap",new Dictionary<string,string>{["systemAddr"]=system.ToString(System.Globalization.CultureInfo.InvariantCulture)},[]);
            using(var map=JsonDocument.Parse(carrierMap.Payload))
                if(carrierMap.Status!=200||map.RootElement.GetProperty("systemAddress").GetUInt64()!=system
                    ||map.RootElement.GetProperty("availableLocations").GetArrayLength()!=0||map.RootElement.GetProperty("viableLocations").GetArrayLength()!=0)
                    throw new InvalidDataException("Native carrier map did not identify its empty local destination set.");
            foreach(var operation in new[]{"/2.0/elite/powerplay2/galaxydigest_systemdata_part","/2.0/elite/powerplay2/galaxydigest_score_part"})
            {
                var initial=await data.InvokeAsync("GET",operation,new Dictionary<string,string>(),[]);
                using(var digest=JsonDocument.Parse(initial.Payload))
                    if(initial.Status!=200||digest.RootElement.GetProperty("systems").GetArrayLength()!=0)
                        throw new InvalidDataException("A fresh private Powerplay digest did not provide its initial page.");
                foreach(var page in new[]{"1","2","1024"})
                {
                    var end=await data.InvokeAsync("GET",operation,new Dictionary<string,string>{["pageNumber"]=page},[]);
                    if(end.Status!=204||end.Payload!="")throw new InvalidDataException("The private Powerplay digest did not terminate pagination.");
                }
                if((await data.InvokeAsync("GET",operation,new Dictionary<string,string>{["pageNumber"]="-1"},[])).Status!=400)
                    throw new InvalidDataException("The private Powerplay digest accepted a malformed page.");
            }
            using(var reply=JsonDocument.Parse(first.Payload))
                if(reply.RootElement.GetProperty("id").GetInt32()!=18||reply.RootElement.GetProperty("discoveries").GetArrayLength()!=0)
                    throw new InvalidDataException("Fresh local codex region contract failed.");
            var generation="verify-"+Guid.NewGuid().ToString("N")[..12];var filename=generation+".dll";
            File.Copy(Path.Combine(root,metadata["file"]!.GetValue<string>()),Path.Combine(root,filename),false);
            metadata["generation"]=generation;metadata["file"]=filename;Publish(metadata);
            var second=await Region();if(second!=first)throw new InvalidDataException("The replacement native generation changed the read contract.");
            VerifyRpc(Path.Combine(root,filename));
            VerifySave(Path.Combine(root,filename),saveRequest);
            var goodHash=metadata["sha256"]!.GetValue<string>();metadata["sha256"]=new string('0',64);Publish(metadata);
            if(await Region()!=second)throw new InvalidDataException("An invalid module did not retain the last good generation.");
            metadata["sha256"]=goodHash;Publish(metadata);
            if((await data.InvokeAsync("GET","/2.0/elite/codex/discovery/region",new Dictionary<string,string>{["regionId"]="18",["cmdrId"]="0"},[])).Status!=401)
                throw new InvalidDataException("A hot read bypassed the stable identity check.");
            if((await data.InvokeAsync("PUT","/2.0/elite/codex/discovery/region",new Dictionary<string,string>{["regionId"]="18"},[])).Status!=501)
                throw new InvalidDataException("A read-only module accepted a mutation.");
            var after=await data.InvokeAsync("GET","/2.0/elite/commander/savegame",new Dictionary<string,string>(),[]);
            if(runId!=data.RunId||!ReferenceEquals(world,data.World)||after!=before||data.SaveCommits!=0)
                throw new InvalidDataException("Hot reads replaced session authority or modified the save.");
            diagnostics.Dispose();
            var records=Directory.GetFiles(reports,"hot-read-check.*.jsonl").SelectMany(File.ReadLines)
                .Select(line=>JsonDocument.Parse(line)).ToArray();
            try
            {
                if(records.Count(record=>record.RootElement.GetProperty("event").GetString()=="module.activated")!=2
                    ||records.Count(record=>record.RootElement.GetProperty("event").GetString()=="module.reload.rejected")!=1)
                    throw new InvalidDataException("Native generation activation/rejection evidence is incomplete.");
            }
            finally{foreach(var record in records)record.Dispose();}
            Console.WriteLine("PASS same process: two native C# generations executed, invalid update rolled back, identity/save/session retained.");
        }
        finally
        {
            var restore=Path.Combine(root,"restore-"+Guid.NewGuid().ToString("N")+".tmp");
            await File.WriteAllBytesAsync(restore,original);File.Move(restore,manifest,true);OfflineDiagnostics.Current=null;
        }
        void Publish(JsonObject value)
        {
            var temporary=Path.Combine(root,"check-"+Guid.NewGuid().ToString("N")+".tmp");
            File.WriteAllText(temporary,value.ToJsonString(),new UTF8Encoding(false));File.Move(temporary,manifest,true);
        }
    }
    private static unsafe void VerifyRpc(string moduleFile)
    {
        // NativeLibrary is deliberately retained until this check process exits.
        var module=NativeLibrary.Load(moduleFile);
        var invoke=(delegate* unmanaged<byte*,int,byte**,int*,int>)NativeLibrary.GetExport(module,"XfeRpcModuleInvoke");
        var free=(delegate* unmanaged<byte*,void>)NativeLibrary.GetExport(module,"XfeReadModuleFree");
        var island=new byte[153];island[0]=2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(island.AsSpan(1),7);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(island.AsSpan(5),10477373803);
        var definition=new byte[18];definition[0]=17;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(definition.AsSpan(1),103);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(definition.AsSpan(9),10477373803);
        var named=new byte[14];named[0]=89;named[1]=9;named[5]=5;"local"u8.CopyTo(named.AsSpan(9));
        foreach(var (role,type,payload,bits,replyMethod,replyBits) in new[]{
            ("primary",(byte)22,island,1224,(byte)3,343),
            ("primary",(byte)22,new byte[]{9,0,0,0,0},40,(byte)0,0),
            ("primary",(byte)22,new byte[]{9,255,255,255,255},40,(byte)0,0),
            ("primary",(byte)23,definition,144,(byte)18,77),
            ("primary",(byte)22,new byte[]{32,0,0,0,0},40,(byte)0,0),
            ("primary",(byte)23,named,112,(byte)90,128),
            ("missions",(byte)23,named,112,(byte)90,128)})
        {
            var request=new NativeRpcModuleRequest(1,role,type,payload[0]==89,true,bits,payload,10477373803,91,101,102);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(request,NativeReadModuleJson.Default.NativeRpcModuleRequest);
            byte* output=null;var length=0;
            try
            {
                int result;fixed(byte* input=bytes)result=invoke(input,bytes.Length,&output,&length);
                if(result!=0||output==null||length is <=0 or >65536)throw new InvalidDataException("Native RPC business invocation failed for method "+payload[0]+" role "+role);
                var reply=JsonSerializer.Deserialize(new ReadOnlySpan<byte>(output,length),NativeReadModuleJson.Default.NativeRpcModuleReply)!;
                if(reply.Protocol!=1||!reply.Handled||reply.ErrorType is not null)throw new InvalidDataException("Native RPC business rejected its bounded request.");
                if(replyBits==0){if(reply.Emissions.Length!=0)throw new InvalidDataException("A one-way native notification produced a fake response.");continue;}
                if(reply.Emissions.Length!=1||reply.Emissions[0].LetterType!=23||reply.Emissions[0].PayloadBits!=replyBits
                    ||reply.Emissions[0].Payload[0]!=replyMethod||reply.Emissions[0].Flag||reply.ErrorType is not null)
                    throw new InvalidDataException("Native RPC business did not return its bounded reply for method "+payload[0]+" role "+role);
            }
            finally{if(output!=null)free(output);}
        }
        var notification=new NativeRpcModuleRequest(1,"primary",22,false,true,40,[9,1,0,0,0],10477373803,91,101,102);
        foreach(var (request,expected) in new[]{
            (notification with{Flag=true},2),
            (notification with{AlignedValues=false},2),
            (notification with{PayloadBits=39},2),
            (notification with{Payload=new byte[]{9,1,0,0,0,0}},2),
            (notification with{Role="missions"},1),
            (notification with{LetterType=23},1)})
        {
            var bytes=JsonSerializer.SerializeToUtf8Bytes(request,NativeReadModuleJson.Default.NativeRpcModuleRequest);
            byte* output=null;var length=0;
            try
            {
                int result;fixed(byte* input=bytes)result=invoke(input,bytes.Length,&output,&length);
                if(result!=expected)throw new InvalidDataException("Native scalar notification accepted an invalid envelope or role.");
                if(output!=null)
                {
                    var reply=JsonSerializer.Deserialize(new ReadOnlySpan<byte>(output,length),NativeReadModuleJson.Default.NativeRpcModuleReply)!;
                    if(reply.Handled||reply.Emissions.Length!=0)throw new InvalidDataException("Rejected scalar notification produced an application success.");
                }
            }
            finally{if(output!=null)free(output);}
        }
    }
    private static unsafe void VerifySave(string moduleFile,NativeSaveModuleRequest request)
    {
        var module=NativeLibrary.Load(moduleFile);
        var invoke=(delegate* unmanaged<byte*,int,byte**,int*,int>)NativeLibrary.GetExport(module,"XfeSaveModuleInvoke");
        var free=(delegate* unmanaged<byte*,void>)NativeLibrary.GetExport(module,"XfeReadModuleFree");
        foreach(var reject in new[]{false,true})
        {
            var vector=reject?request with{Body=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(request.Body)
                .Replace("\"capacitors.0.energy\":18000000","\"health.hull\":1000001",StringComparison.Ordinal))}:request;
            var bytes=JsonSerializer.SerializeToUtf8Bytes(vector,NativeReadModuleJson.Default.NativeSaveModuleRequest);
            byte* output=null;var length=0;
            try
            {
                int result;fixed(byte* input=bytes)result=invoke(input,bytes.Length,&output,&length);
                if(result!=0||output==null||length is <=0 or >8*1024*1024)throw new InvalidDataException("Native save module invocation failed.");
                var reply=JsonSerializer.Deserialize(new ReadOnlySpan<byte>(output,length),NativeReadModuleJson.Default.NativeSaveModuleReply)!;
                if(reject)
                {if(reply.Status!=400||reply.SaveJson is not null||reply.Error?.Contains("health.hull",StringComparison.Ordinal)!=true)
                    throw new InvalidDataException("The native save module did not retain its precise rejection diagnostic.");}
                else
                {
                    if(reply.Status!=200||reply.SaveJson is null||reply.ErrorType is not null)throw new InvalidDataException("Native save module did not produce a candidate.");
                    if(reply.Payload!="<data />")throw new InvalidDataException("Native save module changed the XML upload acknowledgement.");
                    var save=JsonSerializer.Deserialize<NativeCommanderSave>(reply.SaveJson,Wire.Json)!;
                    if(save.Snapshot.Vessels.Single(ship=>ship.Id==save.Snapshot.CurrentVesselId).Capacitors[0].EnergyMillion!=18_000_000)
                        throw new InvalidDataException("Native save module changed absolute capacitor energy.");
                }
            }
            finally{if(output!=null)free(output);}
        }
    }
}
