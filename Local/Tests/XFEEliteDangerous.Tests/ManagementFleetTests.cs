using System.Text.Json;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.Client.Storage;
using Xunit;

namespace XFEEliteDangerous.Tests;

public sealed class ManagementFleetTests:IAsyncLifetime
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"xfe-management-"+Guid.NewGuid().ToString("N"));
    private OfflineWorkspace workspace=null!;private string token="",account="";
    public async Task InitializeAsync()
    {
        workspace=new(root);var owner=await workspace.OpenLocalOwnerSessionAsync();token=owner["token"]!.GetValue<string>();account=owner["account"]!["id"]!.GetValue<string>();
        uint nativeId;await using(var store=new StateStore(root))nativeId=await store.Read(state=>state.Accounts[account].NativeUserId);
        var profile=new NativeFullSavegameProfile
        {
            Name="Private fixture",LoanCeiling=0,Debt=0,StartupState=0,Gender="M",ProvingGround=new(0,0,0,0),BattleRating=0,Avatar="",AvatarXml="",
            VehicleDistanceTravelled=0,AtrLevel=0,Notoriety=0,MarkedWanted=false,CurrentSystem=42,CurrentSystemKey="43",GameVersion=new("fixture","1",332841,0),
            EmptyCollections=new(){Rank=new(),Constants=new(),Pinned=new(),CurrentCrew=new(),Suits=new(),WeaponRack=new(),SystemHyperspacePermits=new(),RegionHyperspacePermits=new(),
                LandingPermits=new(),LegacyFines=new(),LegalStates=new(),TaxiBookings=new(),Microresources=new(){Inventory=new(),Discovered=new()},ShipLocker=new(),OnboardingObjectives=new(){Objectives=new(),ExtraData=new()}}
        };
        var save=NativeSidewinderSaveFactory.Create(nativeId,1,100,profile,new(42,new(1,2,3),new(0,0,0),false));
        await workspace.ImportNativeSaveAsync(token,account,save,"0");
    }
    public async Task DisposeAsync(){await workspace.DisposeAsync();Directory.Delete(root,true);}
    private async Task<string> Version()=> (await workspace.CommanderAsync(token,account))["version"]!.GetValue<string>();
    private Task<JsonNode> Edit(JsonObject operation,string version)=>workspace.EditFleetAsync(token,account,operation,version);
    [Fact]public async Task CloneSwitchRemoveAndLabelsSurviveReopen()
    {
        await Edit(new(){["action"]="clone",["templateShipId"]=1,["name"]="Custom ship",["ident"]="XFE-02"},await Version());
        var save=(await workspace.ExportNativeSaveAsync(token,account)).Deserialize<NativeCommanderSave>(Wire.Json)!;
        Assert.Equal(2,save.Snapshot.Vessels.Count);Assert.Equal("Custom ship",save.ShipLabels[2].Name);
        Assert.All(save.Snapshot.Vessels[1].Modules.Where(slot=>slot.Module is not null),slot=>Assert.Null(slot.Module!.Uid));
        await Edit(new(){["action"]="select",["shipId"]=2},await Version());
        var before=await File.ReadAllBytesAsync(Path.Combine(root,"state.json"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>Edit(new(){["action"]="remove",["shipId"]=2},Version().GetAwaiter().GetResult()));
        Assert.Equal(before,await File.ReadAllBytesAsync(Path.Combine(root,"state.json")));
        await workspace.DisposeAsync();workspace=new(root);var owner=await workspace.OpenLocalOwnerSessionAsync();token=owner["token"]!.GetValue<string>();
        var fleet=await workspace.FleetAsync(token,account);Assert.Equal(2,fleet["ships"]!.AsArray().Count);Assert.True(fleet["ships"]![1]!["active"]!.GetValue<bool>());
        await Edit(new(){["action"]="select",["shipId"]=1},await Version());await Edit(new(){["action"]="remove",["shipId"]=2},await Version());
        Assert.Single((await workspace.FleetAsync(token,account))["ships"]!.AsArray());
    }
    [Fact]public async Task StoredWeaponCanBeReequippedAndIncompatibleReplacementRollsBack()
    {
        await Edit(new(){["action"]="store",["shipId"]=1,["slot"]="SmallHardpoint1"},await Version());
        var save=(await workspace.ExportNativeSaveAsync(token,account)).Deserialize<NativeCommanderSave>(Wire.Json)!;
        Assert.Null(save.Snapshot.Vessels[0].Modules.Single(slot=>slot.Name=="SmallHardpoint1").Module);Assert.Single(save.StoredModules);
        await Edit(new(){["action"]="retrieve",["shipId"]=1,["slot"]="SmallHardpoint1",["storageId"]=save.StoredModules[0].SlotId},await Version());
        var before=await File.ReadAllBytesAsync(Path.Combine(root,"state.json"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>Edit(new(){["action"]="module",["shipId"]=1,["slot"]="PowerPlant",["module"]="Hpt_PulseLaser_Gimbal_Small"},Version().GetAwaiter().GetResult()));
        Assert.Equal(before,await File.ReadAllBytesAsync(Path.Combine(root,"state.json")));
    }
    [Fact]public async Task MaterialsUseCapturedNativeShapeAndRejectOverflowWithoutChangingRevision()
    {
        await workspace.PrepareMaterialsAsync(token,account,await Version());var material=BundledData.NativeMaterials[0];
        var value=new JsonObject{["itemId"]=material.Id.ToString(),["category"]=material.Type,["quantity"]=material.Capacity};
        await workspace.EditAsync(token,account,InventoryEdit.Materials,value,await Version());
        var saved=(await workspace.ExportNativeSaveAsync(token,account)).Deserialize<NativeCommanderSave>(Wire.Json)!;
        await using var store=new StateStore(root);
        var document=await store.Read(state=>state.Accounts[account].NativeSave!.SerializeCurrent(state.Accounts[account]));
        var row=JsonNode.Parse(document)!["microresources"]!["inventory"]![material.Id.ToString()]!;
        Assert.Equal(material.Capacity,row["qty"]!.GetValue<int>());Assert.Equal(material.Type,row["type"]!.GetValue<string>());Assert.Equal(material.Id,row["id"]!.GetValue<uint>());
        var before=await File.ReadAllBytesAsync(Path.Combine(root,"state.json"));value["quantity"]=material.Capacity+1;
        await Assert.ThrowsAsync<ArgumentException>(()=>workspace.EditAsync(token,account,InventoryEdit.Materials,value,Version().GetAwaiter().GetResult()));
        Assert.Equal(before,await File.ReadAllBytesAsync(Path.Combine(root,"state.json")));Assert.Equal(material.Capacity,saved.Materials[0].Quantity);
    }
    [Fact]public async Task AddedNativeCargoAndCreditsReachTheNativeWriter()
    {
        await workspace.EditAsync(token,account,InventoryEdit.Credits,new(){["delta"]=500},await Version());
        await workspace.EditAsync(token,account,InventoryEdit.Cargo,new(){["itemId"]="platinum",["quantity"]=3},await Version());
        var save=(await workspace.ExportNativeSaveAsync(token,account)).Deserialize<NativeCommanderSave>(Wire.Json)!;
        Assert.Equal(600,save.Snapshot.Credits);Assert.Equal("Platinum",save.Snapshot.Vessels[0].Cargo.Single().Commodity);Assert.Equal(3U,save.Snapshot.Vessels[0].Cargo.Single().Quantity);
    }
    [Fact]public async Task LegacyNativeSaveWithoutNewOptionalPropertiesRemainsReadable()
    {
        var node=await workspace.ExportNativeSaveAsync(token,account);node.AsObject().Remove("materials");node.AsObject().Remove("shipLabels");
        var save=node.Deserialize<NativeCommanderSave>(Wire.Json)!;Assert.Empty(save.Materials);Assert.Empty(save.ShipLabels);
        await workspace.ImportNativeSaveAsync(token,account,save,await Version());
    }
    [Fact]public async Task StaleFleetEditAndUnprivilegedEditsAreRejected()
    {
        var stale=await Version();await Edit(new(){["action"]="rename",["shipId"]=1,["name"]="Name",["ident"]="XFE"},stale);
        await Assert.ThrowsAsync<SaveConflictException>(()=>Edit(new(){["action"]="clone",["templateShipId"]=1,["name"]="Stale",["ident"]="NO"},stale));
        await workspace.RegisterAsync("player@example.invalid","Player","FixturePassword2026");var player=await workspace.LoginAsync("player@example.invalid","FixturePassword2026");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>workspace.EditFleetAsync(player["token"]!.GetValue<string>(),account,new(){["action"]="select",["shipId"]=1},"0"));
    }
    [Fact]public async Task NewCommanderReceivesAnIndependentStarterWithoutCopyingProgressOrCredits()
    {
        await using(var store=new StateStore(root))await store.Update(state=>
        {
            var original=state.Accounts[account];original.NativeSave=original.NativeSave! with{SystemMetadata=new()
            {Address=42,Id=1,Name="Fixture",LastChanged=0,CacheUntil=0,Population=0,Wealth=0,Security=0,StandardOfLiving=0,DevelopmentLevel=0,Distances=new(),Polities=new(),UnownedSettlements=new()}};return true;
        });
        var created=await workspace.CreateAccountAsync(token,"new@example.invalid","New commander","FixturePassword2026","player");var id=created["id"]!.GetValue<string>();
        await workspace.InitializeNativeAsync(token,id,"0");
        var saved=(await workspace.ExportNativeSaveAsync(token,id)).Deserialize<NativeCommanderSave>(Wire.Json)!;
        Assert.Equal("New commander",saved.Profile.Name);Assert.Equal(0,saved.Snapshot.Credits);Assert.Single(saved.Snapshot.Vessels);Assert.All(saved.Materials,value=>Assert.Equal(0,value.Quantity));
        var originalSave=(await workspace.ExportNativeSaveAsync(token,account)).Deserialize<NativeCommanderSave>(Wire.Json)!;Assert.NotEqual(originalSave.Snapshot.CommanderNativeId,saved.Snapshot.CommanderNativeId);
        await Assert.ThrowsAsync<InvalidDataException>(()=>workspace.InitializeNativeAsync(token,id,"1"));
    }
}
