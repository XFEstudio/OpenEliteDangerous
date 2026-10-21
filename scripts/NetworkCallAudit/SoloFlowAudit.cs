using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Iced.Intel;
using Decoder=Iced.Intel.Decoder;

internal static partial class PeNetworkAudit
{
    private sealed record SoloPhase(string Name,uint[] Roots,string Meaning);
    private sealed record SoloEdge(uint From,uint To,uint Site,string Kind);
    private sealed record IndirectSite(uint Owner,uint Site,string Kind,long? Slot);
    private sealed record CallbackTable(uint Owner,uint Site,uint Table,uint[] Targets);
    private sealed record ActivityClass(string Name,uint Getter,uint[] Vtables,uint[] Methods);
    private sealed record PhaseWalk(SoloPhase Phase,Dictionary<uint,int> Reachable,bool Truncated);
    private sealed record MvpRule(string Role,string Reason,string[] RequiredMethods);
    private static readonly SoloPhase[] SoloPhases=[
        new("solo-menu",[0x4836060,0x1D62CA0,0x1D68A40,0x1D21FE0],"Original button dispatch and native selectable conditions; shared conditions also serve other modes."),
        new("pre-multiplayer-warnings",[0x1D014A0,0x1D82AE0,0x1D647D0,0x1D653E0,0x1D99DA0,0x1D7D2A0,0x1D5F7A0,0x1D1F170],"Optional Powerplay notice, first-show preference, warning callbacks and native no-notice branch; not a world-readiness predicate."),
        new("startup-commander",[0x1D18000,0x2572160],"Menu/brief savegame consumer, distinct from full-state parsing."),
        new("full-commander-load",[0x25EF7A0,0x25EF950,0x26160A0,0x2404620,0x2407760,0x2403660,0x23FE9C0,0x25E64F0],"Full request polling, fleet/selected-vessel readers and loadout validation."),
        new("load-game-activity",[0x1D82470,0x1D6FCA0,0x1D6FF60,0x1D74440,0x1D74950],"LoadGame activity plus discovered session-owner callbacks; virtual dependencies remain explicit gaps."),
        new("native-control-transport",[0x4C4D700,0x4C4DE50,0x4C4E790,0x650060,0x651F20,0x651C60,0x653AB0,0x6426B0,0x981950,0x981980,0x986130],"Native UDP/replication and currently unresolved registry RPC70/71, not HTTP."),
        new("ship-entity-application",[0x240DC70,0x24DBB10,0x8BEEA0],"Known entity/module applier and numeric ship validity; linkage to world readiness must be reviewed separately."),
        new("world-location-service",[0x1C10DA0,0x1C52330,0x1C4B620,0x1C3F9B0,0x1C4BA40,0x1C4AFE0,0x1C5C740,0x1C57EA0],"Exact typeId5F02B5C getter1C39580/concrete52234C0 virtual entries and their readiness helpers. Actual local references/resources and completion predicates are retained."),
        new("connection-service",[0x8ABFE0,0x8A9040,0x8B0E00,0x8A9200,0x8AA760,0x8AE6C0],"Exact typeId5F02AAC getter8AE5C0/concrete5125F38 dispatch. Per-request state/queue functions are not automatically classified as HTTP or world business."),
        new("save-and-normal-exit",[0x26498F0,0x23F8220,0x3445710,0x25955A0,0x3433CE0],"Native save composition/upload and commander exit builders. PUT payload is not assumed identical to GET response.")
    ];

    // Fixed pinned input and fresh ignored report only. No process, native DLL,
    // network, player profile or game-resource mutation is used by this command.
    internal static async Task<int> AuditSoloAsync()
    {
        const string main=@"E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe";
        const string hash="e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988";
        var bytes=await File.ReadAllBytesAsync(main);
        if(Convert.ToHexStringLower(SHA256.HashData(bytes))!=hash)throw new InvalidDataException("Pinned image differs.");
        var repo=new DirectoryInfo(AppContext.BaseDirectory);
        while(repo is not null&&!File.Exists(Path.Combine(repo.FullName,"XFEEliteDangerous.slnx")))repo=repo.Parent;
        if(repo is null)throw new DirectoryNotFoundException("Use the repository build.");
        var output=Path.Combine(repo.FullName,"artifacts","solo-static-dependency-audit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
        using var image=new Image(bytes);
        var activities=DiscoverActivities(image);
        var ranges=FunctionRanges(image).ToArray();var families=FunctionFamilies(image,ranges.ToList()).ToArray();
        var entries=families.SelectMany(f=>f.Ranges.Select(range=>(range.Begin,f.Entry))).ToDictionary(x=>x.Begin,x=>x.Entry);
        uint Canonical(uint address)=>FindFunction(ranges,address) is {} range?entries[range.Begin]:address;
        var imports=ReadImports(image).GroupBy(x=>x.Iat).ToDictionary(x=>x.Key,x=>x.First());
        var edges=new List<SoloEdge>();var indirect=new List<IndirectSite>();var tables=new List<CallbackTable>();
        var nativeCalls=new List<NativeNetworkCall>();long decoded=0;var invalid=0;
        foreach(var family in families)
        foreach(var range in family.Ranges)
        {
            var code=Decoder.Create(64,new ByteArrayCodeReader(image.Data(range.Begin,(int)(range.End-range.Begin)).ToArray()));
            code.IP=image.Base+range.Begin;var end=image.Base+range.End;
            var recent=new Queue<Instruction>();
            while(code.IP<end)
            {
                code.Decode(out var instruction);decoded++;
                if(instruction.IsInvalid){invalid++;recent.Clear();continue;}
                var site=(uint)(instruction.IP-image.Base);
                if(instruction.Mnemonic==Mnemonic.Mov&&instruction.Op0Kind==OpKind.Memory&&instruction.Op1Kind==OpKind.Register)
                {
                    // A readonly code-pointer array address must actually be
                    // stored by this function. It is still a potential callback
                    // edge, not a proved dynamic dispatch/required request.
                    var sourceRegister=instruction.Op1Register;
                    foreach(var previous in recent.Reverse())
                    {
                        if(previous.FlowControl is FlowControl.Call or FlowControl.IndirectCall)break;
                        if(previous.Op0Kind!=OpKind.Register||previous.Op0Register!=sourceRegister)continue;
                        if(previous.Mnemonic==Mnemonic.Lea&&previous.IsIPRelativeMemoryOperand
                            &&previous.IPRelativeMemoryAddress>=image.Base&&previous.IPRelativeMemoryAddress-image.Base<=uint.MaxValue)
                        {
                            var table=(uint)(previous.IPRelativeMemoryAddress-image.Base);
                            var targets=CodePointerTable(image,table);
                            if(targets.Length>=2)
                            {
                                tables.Add(new(family.Entry,(uint)(previous.IP-image.Base),table,targets));
                                foreach(var target in targets)edges.Add(new(family.Entry,Canonical(target),site,"potential-readonly-callback-table"));
                            }
                        }
                        break;
                    }
                }
                if(instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall or FlowControl.UnconditionalBranch or FlowControl.IndirectBranch)
                {
                    var symbol=ResolveImport(image,imports,in instruction,out var binding);
                    if(symbol is not null&&ApiClassification.Native(symbol.Library,symbol.Api) is {} category)
                        nativeCalls.Add(new(symbol.Library+"!"+symbol.Api,category,Hex(site),Hex(family.Entry),binding));
                    if(instruction.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32 or OpKind.NearBranch16)
                    {
                        var target=instruction.NearBranchTarget;
                        if(target>=image.Base&&target-image.Base<=uint.MaxValue&&image.IsExecutable((uint)(target-image.Base)))
                        {var to=Canonical((uint)(target-image.Base));if(to!=family.Entry)edges.Add(new(family.Entry,to,site,"direct"));}
                    }
                    else if(symbol is null)
                        indirect.Add(new(family.Entry,site,instruction.Op0Kind==OpKind.Register?"register":"memory",instruction.Op0Kind==OpKind.Memory?unchecked((long)instruction.MemoryDisplacement64):null));
                }
                recent.Enqueue(instruction);while(recent.Count>8)recent.Dequeue();
            }
        }
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true};
        var activityRows=activities.Select(value=>new{value.Name,getter=Hex(value.Getter),vtables=value.Vtables.Select(Hex),
            vtableStores=tables.Where(table=>value.Vtables.Contains(table.Table)).Select(table=>Hex(table.Owner)).Distinct().ToArray(),
            methods=value.Methods.Select(Hex)}).ToArray();
        await File.WriteAllTextAsync(Path.Combine(output,"activity-classes.json"),JsonSerializer.Serialize(activityRows,json));
        string[] selectedNames=["ConnectToServerActivity","EnsureGameOwnerActivity","LoadGameActivity","SaveGameActivity","GotoLocationActivity","PrepareLocationActivity",
            "StartLocationStartupActivity","WaitForLocationReadyActivity","StartEndGameplayActivity","DelayLocationStartActivity","SetStartFlowStageActivity","PreMultiplayerWarningsActivity"];
        var selectedActivities=activities.Where(value=>selectedNames.Contains(value.Name)).ToArray();
        var phaseList=SoloPhases.Concat(new[]{new SoloPhase("location-and-save-activities",selectedActivities.SelectMany(value=>value.Methods)
            .Concat(tables.Where(table=>selectedActivities.Any(value=>value.Vtables.Contains(table.Table))).Select(table=>table.Owner)).Distinct().ToArray(),
            "Exact named activity vtables and functions that store them; their inclusion is a static candidate boundary, not proof that every mode executes each callback.")}).ToArray();
        var decompileTargets=selectedActivities.SelectMany(value=>value.Methods).Concat(tables.Where(table=>selectedActivities.Any(value=>value.Vtables.Contains(table.Table))).Select(table=>table.Owner))
            .Concat(SoloPhases.SelectMany(phase=>phase.Roots)).Select(Canonical).Distinct().Where(value=>value>=0x900000&&value<0x4C00000).Order().ToArray();
        await File.WriteAllLinesAsync(Path.Combine(output,"decompile-targets.txt"),decompileTargets.Select(Hex));
        var distinctEdges=edges.Distinct().ToArray();
        var adjacency=distinctEdges.GroupBy(edge=>edge.From).ToDictionary(group=>group.Key,group=>group.Select(edge=>edge.To).Distinct().ToArray());
        var walks=phaseList.Select(phase=>Walk(phase,phase.Roots.Select(Canonical),adjacency)).ToArray();
        var directAdjacency=distinctEdges.Where(edge=>edge.Kind=="direct").GroupBy(edge=>edge.From)
            .ToDictionary(group=>group.Key,group=>group.Select(edge=>edge.To).Distinct().ToArray());
        var directWalks=phaseList.Select(phase=>Walk(phase,phase.Roots.Select(Canonical),directAdjacency)).ToArray();
        var source=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(repo.FullName,"Server","data","native-api-contracts.json")))!.AsObject();
        if(source["executableSha256"]!.GetValue<string>()!=hash)throw new InvalidDataException("API inventory pin differs.");
        var endpoints=source["endpoints"]!.AsArray();if(endpoints.Count!=406)throw new InvalidDataException("API inventory count differs.");
        var adapterText=await File.ReadAllTextAsync(Path.Combine(repo.FullName,"Server","XFEEliteDangerous.Core","NativeApiCatalog.cs"));
        var adapters=Regex.Matches(adapterText,"\\[\"(?<path>/[^\"]+)\"\\]\\s*=\\s*new\\(\\[(?<methods>[^\\]]*)\\]")
            .ToDictionary(match=>match.Groups["path"].Value,match=>Regex.Matches(match.Groups["methods"].Value,"\"(?<method>[A-Z]+)\"")
                .Select(method=>method.Groups["method"].Value).ToArray(),StringComparer.Ordinal);
        var routes=endpoints.Select(node=>
        {
            var row=node!.AsObject();var owners=row["references"]!.AsArray()
                .Where(value=>value!["image"]!.GetValue<string>().EndsWith("\\EliteDangerous64.exe",StringComparison.OrdinalIgnoreCase))
                .Select(value=>value!["FunctionRva"]!.GetValue<string>())
                .Where(value=>value.Length>0).Select(ParseRva).Select(Canonical).Distinct().ToArray();
            var path=row["path"]!.GetValue<string>();var mvp=ClassifyMvpPath(path);var implemented=adapters.GetValueOrDefault(path)??[];
            return new{path,methods=row["methods"]!.DeepClone(),methodEvidence=row["methodEvidence"]!.DeepClone(),references=row["references"]!.DeepClone(),
                constructorFamilies=owners.Select(Hex),directPhases=directWalks.Where(walk=>owners.Any(walk.Reachable.ContainsKey)).Select(walk=>walk.Phase.Name).ToArray(),
                potentialPhases=walks.Where(walk=>owners.Any(walk.Reachable.ContainsKey)).Select(walk=>walk.Phase.Name).ToArray(),
                mvpRole=mvp.Role,mvpReason=mvp.Reason,mvpRequiredMethods=mvp.RequiredMethods,implementedMethods=implemented,
                missingMvpMethods=mvp.RequiredMethods.Except(implemented,StringComparer.Ordinal).ToArray(),
                soloRequired="requires-reader-and-condition-review",runtimeInvoked=false};
        }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(output,"api-phase-matrix.json"),JsonSerializer.Serialize(routes,json));
        var optionalCuts=new[]{new{activity="PreMultiplayerWarningsActivity",feature="Powerplay pre-load notice",entry="0x1D82AE0",branch="0x1D82B1C",continuation="0x1D82D84",
            imageBytesVerified=image.Data(0x1D82B14,14).SequenceEqual(Convert.FromHexString("488b01ff503884c00f8562020000"))
                &&image.Data(0x1D82D84,17).SequenceEqual(Convert.FromHexString("837b7c01750b488b4320488d4b20ff5008"))
                &&BinaryPrimitives.ReadUInt64LittleEndian(image.Data(0x52309D8+0x60,8))==image.Base+0x1D82AE0,
            implementedInSource=true,configuration="MinimumSoloFlow",gameAppliedInThisAudit=false,nativeGameAcceptanceVerified=false,
            policy="Use the existing no-notice branch; keep normal activity registration, completion and exit. No auth, licence, save validation or world-ready flag changed."}};
        if(optionalCuts.Any(cut=>!cut.imageBytesVerified))throw new InvalidDataException("Pinned optional-activity cut evidence differs.");
        var typedServices=new[]{
            new{typeIdGlobal="0x5F02B5C",getter="0x1C39580",concreteVtable="0x52234C0",constructor="0x1C10DA0",
                selectedSlots=new[]{"+18=0x1C52330","+30=0x1C4B620","+38=0x1C3F9B0"},
                pointersVerified=BinaryPrimitives.ReadUInt64LittleEndian(image.Data(0x52234C0,8))==image.Base+0x1C39580
                    &&BinaryPrimitives.ReadUInt64LittleEndian(image.Data(0x52234C0+0x18,8))==image.Base+0x1C52330
                    &&BinaryPrimitives.ReadUInt64LittleEndian(image.Data(0x52234C0+0x30,8))==image.Base+0x1C4B620
                    &&BinaryPrimitives.ReadUInt64LittleEndian(image.Data(0x52234C0+0x38,8))==image.Base+0x1C3F9B0},
            new{typeIdGlobal="0x5F02AAC",getter="0x8AE5C0",concreteVtable="0x5125F38",constructor="0x8ABFE0",
                selectedSlots=new[]{"+20=0x8A9040","+28=0x8B0E00"},
                pointersVerified=BinaryPrimitives.ReadUInt64LittleEndian(image.Data(0x5125F38,8))==image.Base+0x8AE5C0
                    &&BinaryPrimitives.ReadUInt64LittleEndian(image.Data(0x5125F38+0x20,8))==image.Base+0x8A9040
                    &&BinaryPrimitives.ReadUInt64LittleEndian(image.Data(0x5125F38+0x28,8))==image.Base+0x8B0E00}};
        if(typedServices.Any(service=>!service.pointersVerified))throw new InvalidDataException("Pinned typed service dispatch differs.");
        var mvpPlan=new{schema="xfeelite.minimum-solo-network-plan.v1",imageSha256=hash,analysisOnly=true,gameStarted=false,coreListIsExhaustive=false,
            scope="First private commander/ship, load one local star system, enter flight and save/reload. Trading, missions, social modes and paid stores are excluded from the initial milestone.",
            counts=routes.GroupBy(row=>row.mvpRole).ToDictionary(group=>group.Key,group=>group.Count()),
            requiredHttp=routes.Where(row=>row.mvpRole=="retain-core").Select(row=>new{row.path,requiredMethods=row.mvpRequiredMethods,row.implementedMethods,row.missingMvpMethods}),
            cutPolicy="Omit optional feature scheduling and entry points in the private minimal client flow. Do not fabricate successful transactions/entitlements or empty responses to unknown required calls.",
            optionalCuts,
            typedServices,
            cutCountBoundary="One exact optional activity has a source implementation. Path classifications are review proposals, not applied client cuts or proof of phase reachability.",
            requiredNonHttp=new[]{"Native management SYN/SYNC, MTU and ID allocation","Owned ServerAdmin construction and binding","Application RPC and secondary replication","World/island membership and player ship entity state"},
            unresolved=new[]{"Resource-defined CSM actions and remaining virtual service/coroutine dispatch","Full world/island and ship-spawn contract","Native PUT savegame body and transactional persistence","Native starsystem response/resource contract"}};
        await File.WriteAllTextAsync(Path.Combine(output,"minimum-solo-plan.json"),JsonSerializer.Serialize(mvpPlan,json));
        var routeCsv=new StringBuilder("path,mvp_role,required_methods,implemented_methods,missing_methods,reason\r\n");
        foreach(var route in routes)routeCsv.AppendLine(string.Join(',',new[]{route.path,route.mvpRole,string.Join('|',route.mvpRequiredMethods),string.Join('|',route.implementedMethods),string.Join('|',route.missingMvpMethods),route.mvpReason}
            .Select(value=>'"'+value.Replace("\"","\"\"")+'"')));
        await File.WriteAllTextAsync(Path.Combine(output,"minimum-api-matrix.csv"),routeCsv.ToString(),new UTF8Encoding(true));
        var graphText=new StringBuilder("from_rva,to_rva,site_rva,edge_kind\r\n");
        foreach(var edge in distinctEdges.OrderBy(x=>x.From).ThenBy(x=>x.Site).ThenBy(x=>x.To))graphText.AppendLine($"{Hex(edge.From)},{Hex(edge.To)},{Hex(edge.Site)},{edge.Kind}");
        await File.WriteAllTextAsync(Path.Combine(output,"call-graph.csv"),graphText.ToString());
        foreach(var walk in directWalks)
        {
            var reachable=walk.Reachable;var selected=distinctEdges.Where(edge=>reachable.ContainsKey(edge.From)).ToArray();
            var unknown=indirect.Where(site=>reachable.ContainsKey(site.Owner)).Distinct().ToArray();
            var localApis=nativeCalls.Where(call=>reachable.ContainsKey(ParseRva(call.FunctionRva))).ToArray();
            await File.WriteAllTextAsync(Path.Combine(output,walk.Phase.Name+".json"),JsonSerializer.Serialize(new{
                phase=walk.Phase.Name,meaning=walk.Phase.Meaning,roots=walk.Phase.Roots.Select(Hex),reachableFunctions=reachable.Count,
                boundedDepth=12,walk.Truncated,functions=reachable.OrderBy(x=>x.Value).ThenBy(x=>x.Key).Select(x=>new{rva=Hex(x.Key),depth=x.Value}),
                directHttpPaths=routes.Where(route=>route.directPhases.Contains(walk.Phase.Name)).Select(x=>x.path),
                broaderPotentialFunctionCount=walks.Single(value=>value.Phase.Name==walk.Phase.Name).Reachable.Count,
                nativeNetworkCalls=localApis,unresolvedIndirectCount=unknown.Length,
                unresolvedIndirect=unknown.Select(x=>new{owner=Hex(x.Owner),site=Hex(x.Site),kind=x.Kind,staticDisplacement=x.Slot}),
                callbackTables=tables.Where(table=>reachable.ContainsKey(table.Owner)).Distinct().Select(table=>new{owner=Hex(table.Owner),site=Hex(table.Site),table=Hex(table.Table),targets=table.Targets.Select(Hex)}),
                boundary="Potential callback-table reachability is conservative and not proof that a mode executes each branch. Unknown indirect dispatch and functions beyond the fixed depth are unresolved, never silently marked offline."},json));
        }
        var summary=new{schema="xfeelite.solo-static-dependency-audit.v1",createdAt=DateTimeOffset.UtcNow,imageSha256=hash,gameStarted=false,gameControlled=false,networkUsed=false,
            counts=new{indexedFragments=ranges.Length,canonicalFunctions=families.Length,decodedInstructions=decoded,invalidInstructions=invalid,edges=distinctEdges.Length,
                directEdges=distinctEdges.Count(edge=>edge.Kind=="direct"),potentialCallbackEdges=distinctEdges.Count(edge=>edge.Kind!="direct"),unresolvedIndirectSites=indirect.Count,
                activityClasses=activities.Length,apiPaths=routes.Length,apiPathsWithDirectReachability=routes.Count(route=>route.directPhases.Length!=0),apiPathsPotentiallyReached=routes.Count(route=>route.potentialPhases.Length!=0)},
            phases=directWalks.Select(walk=>new{walk.Phase.Name,roots=walk.Phase.Roots.Select(Hex),functions=walk.Reachable.Count,walk.Truncated,
                directHttpPaths=routes.Count(route=>route.directPhases.Contains(walk.Phase.Name)),
                broaderPotentialFunctions=walks.Single(value=>value.Phase.Name==walk.Phase.Name).Reachable.Count}),
            boundary="Static whole-image CALL/JUMP graph, chained-unwind families, exact API constructor inventory and potential stored readonly callback tables. Indirect dispatch and branch predicates require explicit review; potential paths are not a Solo requirement list or gameplay proof."};
        await File.WriteAllTextAsync(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(summary,json));
        Console.WriteLine(JsonSerializer.Serialize(new{report=Path.Combine(output,"summary.json"),summary.counts,gameStarted=false}));return 0;
    }
    private static uint ParseRva(string value)=>Convert.ToUInt32(value.StartsWith("0x",StringComparison.OrdinalIgnoreCase)?value[2..]:value,16);
    private static MvpRule ClassifyMvpPath(string path)
    {
        var core=path switch{
            "/2.0/server/time"=>new[]{"GET"},"/2.0/elite/user/login"=>["GET"],"/2.0/elite/user"=>["GET"],
            "/2.0/elite/commander/login"=>["PUT"],"/2.0/elite/server/list"=>["GET"],
            "/2.0/elite/commander/savegame"=>["GET","PUT"],"/2.0/elite/commander/ship"=>["GET"],
            "/2.0/elite/starsystem/key"=>["GET"],"/2.0/elite/starsystem"=>["GET"],
            "/2.0/elite/commander/logout"=>["PUT"],_=>Array.Empty<string>()};
        if(core.Length!=0)return new("retain-core","Private identity/session, native ship/system state or normal save/exit boundary; retained pending complete response review.",core);
        if(new[]{"/2.0/elite/user/arcs/","/2.0/elite/powerplay/","/2.0/elite/powerplay2/","/2.0/elite/squadron/","/2.0/elite/news/","/2.0/elite/crafting/","/2.0/elite/communitygoal/","/2.0/elite/multicrew/"}.Any(prefix=>path.StartsWith(prefix,StringComparison.Ordinal))
            ||path is "/2.0/elite/user/friends" or "/2.0/elite/user/ignore" or "/2.0/elite/user/privategroup" or "/2.0/elite/server/news" or "/2.0/elite/provingGround")
            return new("omit-optional-feature","Outside the first solo flight/save milestone; remove its scheduling/UI dependency rather than claiming server business success.",[]);
        if(path.StartsWith("/2.0/elite/resources/",StringComparison.Ordinal)
            ||path is "/2.0/elite/commander/tutorial" or "/2.0/elite/commander/tutorial/complete" or "/2.0/elite/commander/settings" or "/2.0/elite/bookmarks" or "/2.0/elite/event")
            return new("review-bootstrap-or-localize","Some native startup jobs may wait for these readers; retain proven local adapters or remove the corresponding optional job after code review.",[]);
        return new("deferred-or-unresolved","Not asserted necessary or safe to remove from a path literal alone; review actual caller/reader when inside the retained milestone.",[]);
    }
    private static ActivityClass[] DiscoverActivities(Image image)
    {
        var getters=new Dictionary<uint,string>();
        foreach(var section in image.Pe.PEHeaders.SectionHeaders.Where(value=>(value.SectionCharacteristics&SectionCharacteristics.MemExecute)!=0))
        {
            var code=image.Bytes.AsSpan(section.PointerToRawData,section.SizeOfRawData);var cursor=0;
            while(cursor+8<=code.Length)
            {
                var found=code[cursor..].IndexOf(new byte[]{0x48,0x8D,0x05});if(found<0)break;cursor+=found;
                if(cursor+8<=code.Length&&code[cursor+7]==0xC3)
                {
                    var getter=(uint)(section.VirtualAddress+cursor);var address=(long)getter+7+BinaryPrimitives.ReadInt32LittleEndian(code[(cursor+3)..]);
                    if(address>0&&address<uint.MaxValue&&image.Offset((uint)address,96) is {} at)
                    {
                        var span=image.Bytes.AsSpan(at,96);var end=span.IndexOf((byte)0);
                        if(end is >8 and <96&&span[..end].ToArray().All(value=>char.IsAsciiLetterOrDigit((char)value)||value==(byte)'_'))
                        {
                            var name=Encoding.ASCII.GetString(span[..end]);
                            if(name.EndsWith("Activity",StringComparison.Ordinal))getters[getter]=name;
                        }
                    }
                }
                cursor++;
            }
        }
        var locations=new Dictionary<uint,List<uint>>();
        foreach(var section in image.Pe.PEHeaders.SectionHeaders.Where(value=>(value.SectionCharacteristics&(SectionCharacteristics.MemRead|SectionCharacteristics.MemWrite|SectionCharacteristics.MemExecute))==SectionCharacteristics.MemRead))
        {
            var data=image.Bytes.AsSpan(section.PointerToRawData,section.SizeOfRawData);
            for(var offset=0;offset+8<=data.Length;offset+=8)
            {
                var address=BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
                if(address<image.Base||address-image.Base>uint.MaxValue||!getters.ContainsKey((uint)(address-image.Base)))continue;
                var getter=(uint)(address-image.Base);var table=(uint)(section.VirtualAddress+offset-8);
                if(CodePointerTable(image,table).Length<2)continue;
                if(!locations.TryGetValue(getter,out var list))locations[getter]=list=[];list.Add(table);
            }
        }
        return getters.Where(value=>locations.ContainsKey(value.Key)).Select(value=>
        {
            var tables=locations[value.Key].ToArray();
            return new ActivityClass(value.Value,value.Key,tables,tables.SelectMany(table=>CodePointerTable(image,table)).Distinct().ToArray());
        }).OrderBy(value=>value.Name,StringComparer.Ordinal).ToArray();
    }
    private static PhaseWalk Walk(SoloPhase phase,IEnumerable<uint> roots,Dictionary<uint,uint[]> edges)
    {
        var found=new Dictionary<uint,int>();var queue=new Queue<uint>();var truncated=false;
        foreach(var root in roots)if(found.TryAdd(root,0))queue.Enqueue(root);
        while(queue.TryDequeue(out var node))
        {
            if(!edges.TryGetValue(node,out var children))continue;
            var depth=found[node];if(depth>=12){if(children.Any(child=>!found.ContainsKey(child)))truncated=true;continue;}
            foreach(var child in children)
                if(found.TryAdd(child,depth+1))queue.Enqueue(child);
            if(found.Count>100000)throw new InvalidDataException("Static phase closure exceeds its explicit bound.");
        }
        return new(phase,found,truncated);
    }
    private static uint[] CodePointerTable(Image image,uint table)
    {
        if((table&7)!=0)return [];
        var section=image.Pe.PEHeaders.SectionHeaders.FirstOrDefault(value=>table>=value.VirtualAddress&&(long)table-value.VirtualAddress<value.SizeOfRawData);
        if((section.SectionCharacteristics&(SectionCharacteristics.MemRead|SectionCharacteristics.MemWrite|SectionCharacteristics.MemExecute))!=SectionCharacteristics.MemRead)return [];
        var result=new List<uint>();
        for(var index=0;index<64;index++)
        {
            var at=table+(uint)index*8;if(image.Offset(at,8) is not {} offset)break;
            var pointer=BinaryPrimitives.ReadUInt64LittleEndian(image.Bytes.AsSpan(offset,8));
            if(pointer<image.Base||pointer-image.Base>uint.MaxValue||!image.IsExecutable((uint)(pointer-image.Base)))break;
            result.Add((uint)(pointer-image.Base));
        }
        return result.ToArray();
    }
}
