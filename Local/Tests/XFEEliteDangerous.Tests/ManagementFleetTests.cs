using System.Text.Json;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.Client.Storage;
using Xunit;

namespace XFEEliteDangerous.Tests;

public sealed partial class ManagementFleetTests:IAsyncLifetime
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
    private static JsonObject LiveProperties()=>new(){["action"]="properties",["shipId"]=1,["slot"]="SmallHardpoint1",["enabled"]=false,["priority"]=3,["health"]=.91};
    [Fact]public async Task LiveRefitPreparationIsReadOnlyAndCommitPreservesConcurrentCommanderChanges()
    {
        var before=await File.ReadAllBytesAsync(Path.Combine(root,"state.json"));
        var prepared=await workspace.PrepareLiveRefitAsync(token,account,LiveProperties(),await Version());
        Assert.Equal(before,await File.ReadAllBytesAsync(Path.Combine(root,"state.json")));
        await workspace.EditAsync(token,account,InventoryEdit.Credits,new(){["delta"]=25},await Version());
        var result=await workspace.CommitLiveRefitAsync(token,prepared,new(prepared.Id,true,"applied","fixture readback",new(prepared.Slot,prepared.After)));
        Assert.True(result["appliedInGame"]!.GetValue<bool>());Assert.False(result["requiresReload"]!.GetValue<bool>());
        var saved=(await workspace.ExportNativeSaveAsync(token,account)).Deserialize<NativeCommanderSave>(Wire.Json)!;
        Assert.Equal(125,saved.Snapshot.Credits);
        var actual=saved.Snapshot.Vessels[0].Modules.Single(value=>value.Name==prepared.Slot).Module!;
        Assert.False(actual.On);Assert.Equal(3U,actual.Priority);Assert.Equal(910000U,actual.HealthMillion);
        Assert.Equal(prepared.After!.Ammo,actual.Ammo);Assert.Equal(prepared.After.Default,actual.Default);
    }
    [Fact]public async Task LiveRefitRejectsWrongCommandIdentityAndMismatchingReadbackWithoutWriting()
    {
        var prepared=await workspace.PrepareLiveRefitAsync(token,account,LiveProperties(),await Version());
        var before=await File.ReadAllBytesAsync(Path.Combine(root,"state.json"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>workspace.CommitLiveRefitAsync(token,prepared,new(Guid.NewGuid().ToString("N"),true,"applied","wrong command",new(prepared.Slot,prepared.After))));
        await Assert.ThrowsAsync<InvalidDataException>(()=>workspace.CommitLiveRefitAsync(token,prepared,new(prepared.Id,true,"applied","wrong health",new(prepared.Slot,prepared.After! with{HealthMillion=1000000}))));
        await Assert.ThrowsAsync<InvalidDataException>(()=>workspace.CommitLiveRefitAsync(token,prepared,new(prepared.Id,false,"rejected","not applied",null)));
        Assert.Equal(before,await File.ReadAllBytesAsync(Path.Combine(root,"state.json")));
    }
    [Fact]public async Task LiveRemovalAndRetrievalKeepWarehouseIdentityAndCanBeReopened()
    {
        var prepared=await workspace.PrepareLiveRefitAsync(token,account,new(){["action"]="store",["shipId"]=1,["slot"]="SmallHardpoint1"},await Version());
        Assert.Null(prepared.After);Assert.Single(prepared.Candidate.StoredModules);
        await workspace.CommitLiveRefitAsync(token,prepared,new(prepared.Id,true,"applied","fixture empty slot",new(prepared.Slot,null)));
        var fleet=await workspace.FleetAsync(token,account);var storageId=fleet["stored"]![0]!["slotId"]!.GetValue<uint>();
        var retrieve=await workspace.PrepareLiveRefitAsync(token,account,new(){["action"]="retrieve",["shipId"]=1,["slot"]="SmallHardpoint1",["storageId"]=storageId},await Version());
        Assert.Null(retrieve.Before);Assert.NotNull(retrieve.After);Assert.Empty(retrieve.Candidate.StoredModules);
        await workspace.CommitLiveRefitAsync(token,retrieve,new(retrieve.Id,true,"applied","fixture fitted slot",new(retrieve.Slot,retrieve.After)));
        await workspace.DisposeAsync();workspace=new(root);var owner=await workspace.OpenLocalOwnerSessionAsync();token=owner["token"]!.GetValue<string>();
        var saved=(await workspace.ExportNativeSaveAsync(token,account)).Deserialize<NativeCommanderSave>(Wire.Json)!;
        Assert.Empty(saved.StoredModules);Assert.Equal(retrieve.After,saved.Snapshot.Vessels[0].Modules.Single(value=>value.Name==retrieve.Slot).Module);
    }
    [Fact]public async Task LiveRefitRejectsAChangedWarehouseAndStalePreparation()
    {
        var version=await Version();
        var prepared=await workspace.PrepareLiveRefitAsync(token,account,new(){["action"]="store",["shipId"]=1,["slot"]="SmallHardpoint1"},version);
        await Edit(new(){["action"]="store",["shipId"]=1,["slot"]="SmallHardpoint2"},version);
        var before=await File.ReadAllBytesAsync(Path.Combine(root,"state.json"));
        await Assert.ThrowsAsync<SaveConflictException>(()=>workspace.PrepareLiveRefitAsync(token,account,LiveProperties(),version));
        await Assert.ThrowsAsync<SaveConflictException>(()=>workspace.CommitLiveRefitAsync(token,prepared,new(prepared.Id,true,"applied","fixture",new(prepared.Slot,null))));
        Assert.Equal(before,await File.ReadAllBytesAsync(Path.Combine(root,"state.json")));
    }
    [Fact]public async Task LiveMailboxRoundTripRejectsStaleRuntimeAndPathTraversal()
    {
        var mailbox=new LiveRefitMailbox(root,account);Assert.Null(mailbox.ReadStatus());Assert.Null(mailbox.ReadCommand());
        var prepared=await workspace.PrepareLiveRefitAsync(token,account,LiveProperties(),await Version());
        var runtime=Guid.NewGuid().ToString("N");var now=DateTimeOffset.UtcNow;
        var status=new LiveRefitStatus(1,runtime,"fixture",10,1,100,"InGame",now,[new(prepared.Slot,prepared.Before)]);
        mailbox.Publish(status);Assert.True(LiveRefitMailbox.Fresh(mailbox.ReadStatus(),now));
        Assert.False(LiveRefitMailbox.Fresh(status with{ObservedUtc=now.AddSeconds(-6)},now));
        Assert.False(LiveRefitMailbox.Fresh(status with{RuntimeId="../other-account"},now));
        Assert.Throws<ArgumentException>(()=>new LiveRefitMailbox(root,"../other-account"));
        var command=new LiveRefitCommand(prepared.Id,runtime,now.AddSeconds(8),1,100,prepared.Slot,prepared.Action,prepared.Before,prepared.After);
        mailbox.Submit(command);Assert.Equal(command,mailbox.ReadCommand());
        Assert.True(LiveRefitMailbox.Matches(prepared.Before,prepared.Before! with{Uid=0}));
    }
    [Fact]public async Task LiveReadOnlyFieldsRejectChangesAndCompensationPreservesOtherWarehouseEdits()
    {
        var save=(await workspace.ExportNativeSaveAsync(token,account)).Deserialize<NativeCommanderSave>(Wire.Json)!;
        var original=save.Snapshot.Vessels[0].Modules.Single(value=>value.Name=="SmallHardpoint1").Module!;
        await Assert.ThrowsAsync<InvalidDataException>(()=>workspace.PrepareLiveRefitAsync(token,account,LiveProperties(),Version().GetAwaiter().GetResult(),new("SmallHardpoint1",original,false)));
        await Assert.ThrowsAsync<InvalidDataException>(()=>workspace.PrepareLiveRefitAsync(token,account,LiveProperties(),Version().GetAwaiter().GetResult(),new("SmallHardpoint1",original,true,false)));
        var prepared=await workspace.PrepareLiveRefitAsync(token,account,new(){["action"]="store",["shipId"]=1,["slot"]="SmallHardpoint1"},await Version());
        await Edit(new(){["action"]="store",["shipId"]=1,["slot"]="SmallHardpoint2"},await Version());
        await workspace.RestoreFailedLiveRefitAsync(prepared,new("rollback-fixture",true,"applied","verified native restoration",new(prepared.Slot,prepared.Before)));
        var restored=(await workspace.ExportNativeSaveAsync(token,account)).Deserialize<NativeCommanderSave>(Wire.Json)!;
        Assert.Single(restored.StoredModules);
        Assert.Equal(original,restored.Snapshot.Vessels[0].Modules.Single(value=>value.Name==prepared.Slot).Module);
        Assert.Null(restored.Snapshot.Vessels[0].Modules.Single(value=>value.Name=="SmallHardpoint2").Module);
    }
    [Fact]public async Task LiveRuntimePlaceholderCannotOverwriteAnUnpoweredModuleSaveDefault()
    {
        await workspace.EditAsync(token,account,InventoryEdit.Modules,new(){["shipId"]="1",["slot"]="Slot02_Size2",["priority"]=1},await Version());
        var prepared=await workspace.PrepareLiveRefitAsync(token,account,new(){["action"]="module",["shipId"]=1,["slot"]="Slot01_Size2",["module"]="Int_CargoRack_Size2_Class1"},await Version());
        Assert.Equal(1U,prepared.After!.Priority);
        var native=prepared with{After=prepared.After with{Priority=0}};
        await workspace.CommitLiveRefitAsync(token,native,new(native.Id,true,"applied","no native priority component",new(native.Slot,native.After,false,false)));
        var saved=(await workspace.ExportNativeSaveAsync(token,account)).Deserialize<NativeCommanderSave>(Wire.Json)!;
        Assert.Equal(1U,saved.Snapshot.Vessels[0].Modules.Single(value=>value.Name==native.Slot).Module!.Priority);
    }
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
