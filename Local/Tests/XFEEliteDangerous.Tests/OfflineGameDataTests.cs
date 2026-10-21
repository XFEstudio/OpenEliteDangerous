using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using XFEEliteDangerous.Core;
using Xunit;

namespace XFEEliteDangerous.Tests;

public sealed class OfflineGameDataTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "XFEElite-native-local-" + Guid.NewGuid().ToString("N"));
    private OfflineGameData data = null!;
    private string accountId = "";
    public async Task InitializeAsync()
    {
        await using var workspace = new OfflineWorkspace(directory);
        await workspace.RegisterAsync("native@example.invalid", "Local Commander", "LocalNativeCheck!2026");
        var login = await workspace.LoginAsync("native@example.invalid", "LocalNativeCheck!2026");
        accountId = login["account"]!["id"]!.GetValue<string>();
        await using var store = new StateStore(directory);
        await store.Update(state =>
        {
            var account = state.Accounts[accountId];
            var save = new NativeCommanderSave
            {
                Snapshot = NativeCommanderProjectionTests.Snapshot(account.NativeUserId), Catalogue = NativeCommanderProjectionTests.Catalogue,
                Profile = new()
                {
                    Name="Local Commander",LoanCeiling=0,Debt=0,StartupState=0,Gender="M",ProvingGround=new(0,0,0,0),BattleRating=0,
                    Avatar="",AvatarXml="",VehicleDistanceTravelled=0,AtrLevel=0,Notoriety=0,MarkedWanted=false,CurrentSystemKey="43",CurrentSystem=42,
                    GameVersion=new("local","4.4.1.1",332841,0),EmptyCollections=new()
                    {
                        Rank=new(),Constants=new(),Pinned=new(),CurrentCrew=new(),Suits=new(),WeaponRack=new(),
                        SystemHyperspacePermits=new(),RegionHyperspacePermits=new(),LandingPermits=new(),LegacyFines=new(),LegalStates=new(),TaxiBookings=new(),
                        ShipLocker=new(),Microresources=new(){Inventory=new(),Discovered=new()},OnboardingObjectives=new(){Objectives=new(),ExtraData=new()}
                    }
                }
            };
            account.Commander=save.CreateInventory(account,0);account.NativeSave=save;return true;
        });
        data = new(directory, accountId);
    }
    public async Task DisposeAsync() { await data.DisposeAsync(); Directory.Delete(directory,true); }
    private Task<NativeDataReply> Call(string method,string path,string body="",Dictionary<string,string>? query=null)
        => data.InvokeAsync(method,"/2.0/elite/"+path,query??[],Encoding.UTF8.GetBytes(body));
    private async Task Login() => Assert.Equal(200,(await Call("GET","user/login")).Status);
    [Fact] public async Task BootstrapTimeIsLocalPlainJsonBeforeLogin()
    {
        var reply=await data.InvokeAsync("GET","/2.0/server/time",new Dictionary<string,string>(),[]);
        Assert.Equal(200,reply.Status);using var time=JsonDocument.Parse(reply.Payload);
        Assert.InRange(time.RootElement.GetProperty("unixTimestamp").GetInt64(),DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds(),DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
    [Fact] public async Task FreshCommanderReadServicesUseCapturedContractsWithoutEditingTheSave()
    {
        await Login();var before=(await Call("GET","commander/savegame")).Payload;
        using(var reputation=JsonDocument.Parse((await Call("GET","commander/reputation")).Payload))
        {
            Assert.Empty(reputation.RootElement.GetProperty("reputations").EnumerateArray());
            Assert.Equal(0,reputation.RootElement.GetProperty("rank").GetProperty("combat").GetInt32());
            Assert.Equal(0,reputation.RootElement.GetProperty("powerId").GetInt32());
        }
        var voucher=System.Xml.Linq.XDocument.Parse((await Call("GET","voucher/list")).Payload);
        Assert.Empty(voucher.Root!.Element("vouchers")!.Elements());
        Assert.Equal(200,(await Call("GET","crafting/engineer/progression")).Status);
        var inbox=System.Xml.Linq.XDocument.Parse((await Call("GET","inbox")).Payload);
        Assert.Empty(inbox.Root!.Element("messages")!.Elements());
        Assert.InRange(long.Parse(inbox.Root.Element("refresh")!.Value),DateTimeOffset.UtcNow.ToUnixTimeSeconds()+290,DateTimeOffset.UtcNow.ToUnixTimeSeconds()+305);
        using(var history=JsonDocument.Parse((await Call("GET","commander/tradesystems")).Payload))
            Assert.Empty(history.RootElement.GetProperty("starsystems").EnumerateArray());
        using(var codex=JsonDocument.Parse((await Call("GET","codex/discovery/summary")).Payload))
            Assert.Empty(codex.RootElement.GetProperty("regions").EnumerateArray());
        using(var pinned=JsonDocument.Parse((await Call("GET","crafting/engineer/pin")).Payload))
            Assert.Empty(pinned.RootElement.GetProperty("recipes").EnumerateArray());
        using(var signals=JsonDocument.Parse((await Call("GET","fss/signals",query:new(){["systemAddr"]="42"})).Payload))
        {Assert.Equal(3,signals.RootElement.GetProperty("settings").GetProperty("fssSignalMode").GetInt32());Assert.Empty(signals.RootElement.GetProperty("signals").EnumerateArray());}
        Assert.Equal(404,(await Call("GET","fss/signals",query:new(){["systemAddr"]="43"})).Status);
        using(var conflicts=JsonDocument.Parse((await Call("GET","starsystem/factionconflicts")).Payload))
            Assert.Empty(conflicts.RootElement.GetProperty("conflicts").EnumerateArray());
        Assert.Equal(200,(await Call("GET","crafting/specials")).Status);
        Assert.Equal(401,(await Call("GET","commander/reputation",query:new(){["cmdrId"]="0"})).Status);
        Assert.Equal(405,(await Call("PUT","voucher/list")).Status);
        using(var wallet=JsonDocument.Parse((await Call("PUT","user/arcs/redeem")).Payload))
        {Assert.Equal(0,wallet.RootElement.GetProperty("balance").GetProperty("total_balance").GetInt32());Assert.Equal(0,wallet.RootElement.GetProperty("balance").GetProperty("earned_delta").GetInt32());}
        Assert.Equal(before,(await Call("GET","commander/savegame")).Payload);
    }
    [Fact]public async Task JournalUploadsAreDurableAndRetriesDoNotDuplicateOrReplaceTheCommanderSave()
    {
        await Login();const string journal="[{\"event\":\"SyntheticLocal\",\"sequence\":1}]";
        var save=(await Call("GET","commander/savegame")).Payload;
        Assert.Equal(200,(await Call("PUT","journal",journal)).Status);
        Assert.Equal(200,(await Call("PUT","journal",journal)).Status);Assert.Equal(1,data.JournalCommits);
        var entry=Assert.Single(Directory.GetFiles(Path.Combine(directory,"native-journal",accountId),"*.bin"));
        Assert.Equal(Encoding.UTF8.GetBytes(journal),File.ReadAllBytes(entry));
        await data.DisposeAsync();data=new(directory,accountId);await Login();
        Assert.Equal(200,(await Call("PUT","journal",journal)).Status);Assert.Equal(0,data.JournalCommits);
        Assert.Equal(save,(await Call("GET","commander/savegame")).Payload);
        Assert.Equal(401,(await Call("PUT","journal",journal,new(){["cmdrId"]="0"})).Status);
    }
    [Fact] public async Task NativeIdentityIsPrivateAndUnknownOperationsStayExplicitFailures()
    {
        Assert.Equal(401,(await Call("GET","commander/savegame")).Status);await Login();
        Assert.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes("Local Commander")),(await Call("GET","user")).Payload);
        Assert.Equal(501,(await Call("GET","not-implemented")).Status);
        Assert.Equal(401,(await Call("GET","user",query:new(){["runID"]="0"})).Status);
        Assert.Equal(501,(await Call("PUT","commander/login",query:new(){["gameMode"]="open"})).Status);
    }
    [Fact] public async Task PrivateUserDeclaresTheOdysseyContentModeWithoutProductGrants()
    {
        await Login();var document=System.Xml.Linq.XDocument.Parse((await Call("GET","user")).Payload);
        var root=document.Root!;Assert.Equal("data",root.Name.LocalName);
        Assert.Empty(root.Element("sku")!.Elements());Assert.Empty(root.Element("sku")!.Value);
        var capabilities=root.Element("capabilities")!.Elements().Select(value=>value.Value).ToArray();
        Assert.Contains("SPACE_LEGS",capabilities);Assert.Contains("LANDING_ATMOS_TENUOUS",capabilities);
        Assert.Contains("SHIP",capabilities);Assert.Equal(capabilities.Length,capabilities.Distinct().Count());
        Assert.All(root.Element("capabilities")!.Elements(),value=>Assert.Equal("item",value.Name.LocalName));
    }
    [Fact] public async Task NativeSaveDeltaPersistsPositionFuelAndReloadsAfterSessionRecreation()
    {
        await Login();Assert.Equal(200,(await Call("PUT","commander/login",query:new(){["gameMode"]="solo"})).Status);
        const string delta="""
        {"commander":{"vehicleDistanceTravelled":4,"onFootDistanceTravelled":0,"startupState":0,"nearplanet":false,"atrLevel":0,"markedWanted":false},
         "vessels":[{"ownedshipid":1,"prototype":"SideWinder","location":{"ua":"42,100,200,300,0,0,0,0","landed":false},"state":{"fuel.lvl":1.25,"health.hull":700000}}]}
        """;
        var acknowledgement=await Call("PUT","commander/savegame",delta);
        Assert.Equal(200,acknowledgement.Status);
        Assert.Equal("data",System.Xml.Linq.XDocument.Parse(acknowledgement.Payload).Root!.Name.LocalName);
        await data.DisposeAsync();data=new(directory,accountId);await Login();
        using var save=JsonDocument.Parse((await Call("GET","commander/savegame")).Payload);
        var ship=save.RootElement.GetProperty("vessels")[0];
        Assert.Equal("42,100,200,300,0,0,0,0",ship.GetProperty("location").GetProperty("ua").GetString());
        Assert.Equal(1.25f,ship.GetProperty("fuel").GetProperty("lvl").GetSingle());
        Assert.Equal(700000u,ship.GetProperty("health").GetProperty("hull").GetUInt32());
    }
    [Fact] public async Task InvalidDeltaRollsBackAndDesktopChangesAreVisibleWithoutRestart()
    {
        await Login();await Call("PUT","commander/login",query:new(){["gameMode"]="solo"});
        var before=(await Call("GET","commander/savegame")).Payload;
        Assert.Equal(400,(await Call("PUT","commander/savegame","{\"commander\":{\"notAField\":1}}")).Status);
        Assert.Equal(before,(await Call("GET","commander/savegame")).Payload);
        await using(var store=new StateStore(directory))await store.Update(state=>state.Accounts[accountId].Commander.Credits=321);
        using var changed=JsonDocument.Parse((await Call("GET","commander/savegame")).Payload);
        Assert.Equal(321,changed.RootElement.GetProperty("commander").GetProperty("credits").GetInt64());
    }
    private const string EnergyDelta="""
        {"commander":{"vehicleDistanceTravelled":4,"onFootDistanceTravelled":0,"startupState":0,"nearplanet":false,"atrLevel":0,"markedWanted":false},
         "vessels":[{"ownedshipid":1,"prototype":"SideWinder","location":{},"state":{"capacitors.0.energy":18000000}}]}
        """;
    [Fact]public async Task DetachedSaveHandlerPreservesAbsoluteEnergyAndOnlyTheFixedCommitChangesState()
    {
        await using var store=new StateStore(directory);
        var account=await store.Read(state=>state.Accounts[accountId]);
        var before=account.NativeSave!.SerializeCurrent(account);var revision=account.Commander.Revision;
        var reply=NativeSaveModuleAdapter.Evaluate(NativeSaveModuleAdapter.Request(account,true,Encoding.UTF8.GetBytes(EnergyDelta)));
        Assert.Equal(200,reply.Status);Assert.Equal("<data />",reply.Payload);Assert.Equal(before,account.NativeSave.SerializeCurrent(account));
        Assert.Equal(200,NativeSaveModuleAdapter.Commit(account,reply).Status);
        Assert.Equal(revision+1,account.Commander.Revision);
        Assert.Equal(18_000_000u,account.NativeSave.Snapshot.Vessels[0].Capacitors[0].EnergyMillion);
        NativeSaveModuleAdapter.Commit(account,reply);Assert.Equal(revision+1,account.Commander.Revision);
        await store.Update(state=>state.Accounts[accountId]=account);
        var reloaded=await store.Read(state=>state.Accounts[accountId]);
        Assert.Equal(18_000_000u,reloaded.NativeSave!.Snapshot.Vessels[0].Capacitors[0].EnergyMillion);
    }
    [Theory]
    [InlineData("credits")][InlineData("system")][InlineData("fuel-capacity")][InlineData("module-definition")]
    public async Task FixedSaveHostRejectsProtectedChangesAndKeepsTheOriginalState(string change)
    {
        await using var store=new StateStore(directory);var account=await store.Read(state=>state.Accounts[accountId]);
        var before=account.NativeSave!.SerializeCurrent(account);var revision=account.Commander.Revision;
        var reply=NativeSaveModuleAdapter.Evaluate(NativeSaveModuleAdapter.Request(account,true,Encoding.UTF8.GetBytes(EnergyDelta)));
        var candidate=JsonSerializer.Deserialize<NativeCommanderSave>(reply.SaveJson!,Wire.Json)!;
        candidate=change switch
        {
            "credits"=>candidate with{Snapshot=candidate.Snapshot with{Credits=candidate.Snapshot.Credits+1}},
            "system"=>candidate with{Profile=candidate.Profile with{CurrentSystem=43}},
            "fuel-capacity"=>candidate with{Snapshot=candidate.Snapshot with{Vessels=[candidate.Snapshot.Vessels[0] with{Fuel=new(20,1,0)}]}},
            _=>candidate with{Catalogue=candidate.Catalogue with{Modules=[]}}
        };
        Assert.Throws<InvalidDataException>(()=>NativeSaveModuleAdapter.Commit(account,reply with{SaveJson=JsonSerializer.Serialize(candidate,Wire.Json)}));
        Assert.Equal(before,account.NativeSave.SerializeCurrent(account));Assert.Equal(revision,account.Commander.Revision);
    }
    [Fact]public async Task RejectedHotSaveReturnsExactFieldDiagnosticAndNoCandidateOrMutation()
    {
        await using var store=new StateStore(directory);var account=await store.Read(state=>state.Accounts[accountId]);
        var before=account.NativeSave!.SerializeCurrent(account);
        var invalid=EnergyDelta.Replace("\"capacitors.0.energy\":18000000","\"health.hull\":1000001",StringComparison.Ordinal);
        var reply=NativeSaveModuleAdapter.Evaluate(NativeSaveModuleAdapter.Request(account,true,Encoding.UTF8.GetBytes(invalid)));
        Assert.Equal(400,reply.Status);Assert.Null(reply.SaveJson);Assert.Contains("health.hull",reply.Error);Assert.Contains("1000001",reply.Error);
        Assert.Equal(400,NativeSaveModuleAdapter.Commit(account,reply).Status);Assert.Equal(before,account.NativeSave.SerializeCurrent(account));
    }
    [Fact] public async Task NativeSettingsSurviveLogoutAndRejectForeignIdentity()
    {
        await Login();Assert.Equal(200,(await Call("PUT","commander/settings","{\"privacy_mode\":\"PRIVATE\",\"accepting_squadron_invites\":false}")).Status);
        Assert.Equal(401,(await Call("GET","commander/settings",query:new(){["cmdrId"]="0"})).Status);
        await Call("PUT","user/logout");Assert.Equal(401,(await Call("GET","commander/settings")).Status);await Login();
        using var settings=JsonDocument.Parse((await Call("GET","commander/settings")).Payload);
        Assert.False(settings.RootElement.GetProperty("settings").GetProperty("accepting_squadron_invites").GetBoolean());
    }
    [Fact] public async Task VersionedReferenceDefinitionsLoadLocallyAndNeverReplaceSessionOperations()
    {
        await Login();Assert.Equal(501,(await Call("GET","powerplay2/powers/list")).Status);
        const string catalogue="""{"powers":[{"id":1,"name":"Synthetic local power"}],"powerplay":{}}""";
        WriteReferences("/2.0/elite/powerplay2/powers/list","powers.json",catalogue);
        await data.DisposeAsync();data=new(directory,accountId);
        Assert.Equal(401,(await Call("GET","powerplay2/powers/list")).Status);
        await Login();Assert.Equal(catalogue,(await Call("GET","powerplay2/powers/list")).Payload);
        Assert.Equal(405,(await Call("PUT","powerplay2/powers/list")).Status);
        Assert.Equal(400,(await Call("GET","powerplay2/powers/list","{}")).Status);
        Assert.Equal(401,(await Call("GET","powerplay2/powers/list",query:new(){["cmdrId"]="0"})).Status);
        // Editing content without publishing its manifest cannot replace a validated snapshot.
        File.AppendAllText(Path.Combine(directory,"native-resources","powers.json")," ");
        Assert.Equal(catalogue,(await Call("GET","powerplay2/powers/list")).Payload);
        await data.DisposeAsync();Assert.Throws<InvalidDataException>(()=>new OfflineGameData(directory,accountId));
    }
    [Fact]public async Task PublishedReferencesReloadWithinTheSameAuthorityAndRejectIncompleteUpdates()
    {
        await Login();var runId=data.RunId;var world=data.World;
        var save=(await Call("GET","commander/savegame")).Payload;
        const string first="""{"powers":[{"id":1,"name":"First local definition"}]}""";
        const string second="""{"powers":[{"id":1,"name":"Revised local definition"}]}""";
        WriteReferences("/2.0/elite/powerplay2/powers/list","first.json",first);
        Assert.Equal(first,(await Call("GET","powerplay2/powers/list")).Payload);
        WriteReferences("/2.0/elite/powerplay2/powers/list","second.json",second);
        File.AppendAllText(Path.Combine(directory,"native-resources","second.json"),"incomplete update");
        Assert.Equal(first,(await Call("GET","powerplay2/powers/list")).Payload);
        File.WriteAllText(Path.Combine(directory,"native-resources","second.json"),second,new UTF8Encoding(false));
        Assert.Equal(second,(await Call("GET","powerplay2/powers/list")).Payload);
        Assert.Equal(runId,data.RunId);Assert.Same(world,data.World);
        Assert.Equal(save,(await Call("GET","commander/savegame")).Payload);
    }
    [Fact]public async Task PermitDefinitionsUseAnIdKeyedMapWithoutGrantingCommanderPermits()
    {
        await Login();
        using(var empty=JsonDocument.Parse((await Call("GET","resources/permits")).Payload))
            Assert.Equal(JsonValueKind.Object,empty.RootElement.GetProperty("permits").ValueKind);
        await using(var store=new StateStore(directory))
            await store.Update(state=>{state.NativeResources=state.NativeResources with
                {Permits=[new(){Id=5,Description="Synthetic local permit",Systems=[42]}]};return true;});
        using(var definitions=JsonDocument.Parse((await Call("GET","resources/permits")).Payload))
            Assert.Equal("Synthetic local permit",definitions.RootElement.GetProperty("permits").GetProperty("5").GetProperty("description").GetString());
        const string reference="""{"permits":{"5":{"id":5,"description":"Synthetic reference permit","systems":[42]}}}""";
        WriteReferences("/2.0/elite/resources/permits","permits.json",reference);
        await data.DisposeAsync();data=new(directory,accountId);await Login();
        Assert.Equal(reference,(await Call("GET","resources/permits")).Payload);
        using var save=JsonDocument.Parse((await Call("GET","commander/savegame")).Payload);
        Assert.Empty(save.RootElement.GetProperty("systemHyperspacePermits").EnumerateArray());
    }
    [Theory]
    [InlineData("/2.0/elite/user/login","powers.json","{}")]
    [InlineData("/2.0/elite/powerplay2/powers/list","../powers.json","{\"powers\":[{\"id\":1}]}")]
    [InlineData("/2.0/elite/powerplay2/powers/list","powers.json","{\"powers\":[]}")]
    [InlineData("/2.0/elite/powerplay2/powers/list","powers.xml","<response />")]
    [InlineData("/2.0/elite/resources/permits","permits.json","{\"permits\":[]}")]
    [InlineData("/2.0/elite/resources/permits","permits.json","{\"permits\":{\"5\":{\"id\":6,\"description\":\"bad identity\",\"systems\":[42]}}}")]
    [InlineData("/2.0/elite/resources/permits","permits.json","{\"permits\":{},\"sessionToken\":\"not a global definition\"}")]
    public async Task InvalidOrUnsafeReferenceBundlesRejectTheWholeLaunch(string operation,string filename,string content)
    {
        WriteReferences(operation,filename,content);
        await data.DisposeAsync();Assert.Throws<InvalidDataException>(()=>new OfflineGameData(directory,accountId));
    }
    private void WriteReferences(string operation,string filename,string content)
    {
        var root=Path.Combine(directory,"native-resources");Directory.CreateDirectory(root);
        if(Path.GetFileName(filename)==filename)File.WriteAllText(Path.Combine(root,filename),content,new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root,"manifest.json"),JsonSerializer.Serialize(new
        {
            schema=1,build="2026.09.03.332841",gameSha256="e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988",
            documents=new[]{new{operation,file=filename,sha256=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))}}
        }));
    }
}
