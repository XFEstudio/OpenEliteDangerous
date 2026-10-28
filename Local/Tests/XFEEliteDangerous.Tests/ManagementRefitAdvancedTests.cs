using System.Text.Json;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.Client.Storage;
using Xunit;

namespace XFEEliteDangerous.Tests;

public sealed partial class ManagementFleetTests
{
    private static NativeEngineeringBlueprint FsdBlueprint()=>NativeEngineering.Blueprints("Int_Hyperdrive_Size2_Class1")
        .First(value=>value.State.RecipeName=="FSD_LongRange"&&value.State.Level==5);
    private static JsonObject EngineerFsd()=>new(){["action"]="engineering",["shipId"]=1,["slot"]="FrameShiftDrive",["blueprintKey"]=FsdBlueprint().Key,["experimental"]="special_fsd_heavy"};
    private async Task<NativeCommanderSave> ReadNative()=>(await workspace.ExportNativeSaveAsync(token,account)).Deserialize<NativeCommanderSave>(Wire.Json)!;
    [Fact]public void PublicEngineeringCatalogueKeepsModuleIdentityAndExperimentalCompatibility()
    {
        Assert.Contains(NativeEngineering.Catalogue.Modules,value=>value.Id==128049455&&value.Name=="Hpt_MultiCannon_Fixed_Small");
        Assert.Equal(NativeEngineering.Family("Int_Hyperdrive_Size2_Class1"),NativeEngineering.Family("Int_Hyperdrive_Overcharge_Size5_Class5"));
        Assert.Contains(NativeEngineering.Effects("Hpt_MultiCannon_Fixed_Small"),value=>value.Name=="special_corrosive_shell");
        Assert.DoesNotContain(NativeEngineering.Effects("Int_Hyperdrive_Size2_Class1"),value=>value.Name=="special_corrosive_shell");
        Assert.All(NativeEngineering.Catalogue.Blueprints,value=>NativeEngineering.Validate(value.State));
    }
    [Fact]public async Task UnseenRecordedModuleCanBeFittedWithoutInventingAnAmmoDefault()
    {
        var prepared=await workspace.PrepareLiveRefitAsync(token,account,new(){["action"]="module",["shipId"]=1,["slot"]="SmallHardpoint1",["module"]="Hpt_MultiCannon_Fixed_Small"},await Version());
        Assert.Null(prepared.After!.Ammo);Assert.Contains(prepared.Candidate.Catalogue.Modules,value=>value.Id==128049455);
        var actual=prepared.After with{Ammo=new(23,321)};
        await workspace.CommitLiveRefitAsync(token,prepared,new(prepared.Id,true,"applied","boundary fixture",new(prepared.Slot,actual,AmmoEditable:true)));
        var saved=await ReadNative();Assert.Equal(actual.Ammo,saved.Snapshot.Vessels[0].Modules.Single(value=>value.Name==prepared.Slot).Module!.Ammo);
        saved.ValidateCurrent(new(){NativeUserId=checked((uint)saved.Snapshot.CommanderNativeId),NativeSave=saved,Commander=saved.CreateInventory(new(){NativeUserId=checked((uint)saved.Snapshot.CommanderNativeId)},0)});
    }
    [Fact]public async Task EngineeringRequiresPhysicalReadbackBeforePersistingAndWritesTheNativeEnvelope()
    {
        var bytes=await File.ReadAllBytesAsync(Path.Combine(root,"state.json"));
        var prepared=await workspace.PrepareLiveRefitAsync(token,account,EngineerFsd(),await Version());
        Assert.Equal(bytes,await File.ReadAllBytesAsync(Path.Combine(root,"state.json")));
        var incorrect=prepared.After! with{Engineering=prepared.After!.Engineering! with{RecipeId=300100}};
        await Assert.ThrowsAsync<InvalidDataException>(()=>workspace.CommitLiveRefitAsync(token,prepared,new(prepared.Id,true,"applied","wrong native metadata",new(prepared.Slot,incorrect))));
        Assert.Equal(bytes,await File.ReadAllBytesAsync(Path.Combine(root,"state.json")));
        await workspace.CommitLiveRefitAsync(token,prepared,new(prepared.Id,true,"applied","fixture native data",new(prepared.Slot,prepared.After)));
        await using var store=new StateStore(root);var json=await store.Read(state=>state.Accounts[account].NativeSave!.SerializeCurrent(state.Accounts[account]));
        var module=JsonNode.Parse(json)!["vessels"]![0]!["modules"]!["FrameShiftDrive"]!["module"]!;
        Assert.Equal("FSD_LongRange",module["recipeName"]!.GetValue<string>());Assert.Equal(5,module["recipeLevel"]!.GetValue<int>());
        Assert.Equal(prepared.After!.Engineering!.RecipeId,module["modifiers"]!["recipeID"]!.GetValue<ulong>());
        Assert.Contains(module["modifiers"]!["modifiers"]!.AsArray(),value=>value!["name"]!.GetValue<string>()=="special_fsd_heavy"&&value["type"]!.GetValue<uint>()==3);
    }
    [Fact]public async Task EngineeringRejectsAnUnrelatedBlueprintEffectAndUnknownParameter()
    {
        var wrong=EngineerFsd();wrong["slot"]="SmallHardpoint1";
        await Assert.ThrowsAsync<InvalidDataException>(()=>workspace.PrepareLiveRefitAsync(token,account,wrong,Version().GetAwaiter().GetResult()));
        var effect=EngineerFsd();effect["experimental"]="special_corrosive_shell";
        await Assert.ThrowsAsync<InvalidDataException>(()=>workspace.PrepareLiveRefitAsync(token,account,effect,Version().GetAwaiter().GetResult()));
        var parameter=EngineerFsd();parameter["values"]=new JsonObject{["mod_invented"]=1};
        await Assert.ThrowsAsync<InvalidDataException>(()=>workspace.PrepareLiveRefitAsync(token,account,parameter,Version().GetAwaiter().GetResult()));
    }
    [Fact]public async Task AmmunitionReadbackAndLastObservedWarehouseCountsCannotBeFabricated()
    {
        await Edit(new(){["action"]="module",["shipId"]=1,["slot"]="SmallHardpoint1",["module"]="Hpt_MultiCannon_Fixed_Small"},await Version());
        var request=new JsonObject{["action"]="ammunition",["shipId"]=1,["slot"]="SmallHardpoint1",["clip"]=23,["hopper"]=321};
        var prepared=await workspace.PrepareLiveRefitAsync(token,account,request,await Version());
        await Assert.ThrowsAsync<InvalidDataException>(()=>workspace.CommitLiveRefitAsync(token,prepared,new(prepared.Id,true,"applied","capacity is not current ammo",new(prepared.Slot,prepared.After! with{Ammo=new(100,2100)}))));
        await workspace.CommitLiveRefitAsync(token,prepared,new(prepared.Id,true,"applied","current ammo fixture",new(prepared.Slot,prepared.After)));
        var store=await workspace.PrepareLiveRefitAsync(token,account,new(){["action"]="store",["shipId"]=1,["slot"]="SmallHardpoint1"},await Version());
        await workspace.CommitLiveRefitAsync(token,store,new(store.Id,true,"applied","empty slot fixture",new(store.Slot,null),store.Before! with{Ammo=new(19,300)}));
        Assert.Equal(new NativeModuleAmmo(19,300),(await ReadNative()).StoredModules.Last().Module.Ammo);
    }
    [Fact]public async Task ReadOnlyAmmunitionCannotBeEditedAndIdleEngineeringCanBeRemoved()
    {
        var save=await ReadNative();var original=save.Snapshot.Vessels[0].Modules.Single(value=>value.Name=="SmallHardpoint1").Module;
        await Assert.ThrowsAsync<InvalidDataException>(()=>workspace.PrepareLiveRefitAsync(token,account,new(){["action"]="ammunition",["shipId"]=1,["slot"]="SmallHardpoint1",["clip"]=23,["hopper"]=321},
            Version().GetAwaiter().GetResult(),new("SmallHardpoint1",original,AmmoEditable:false)));
        var result=await workspace.SaveIdleRefitAsync(token,account,EngineerFsd(),await Version());Assert.True(result["requiresReload"]!.GetValue<bool>());Assert.Null(result["appliedInGame"]);
        await workspace.SaveIdleRefitAsync(token,account,new(){["action"]="engineering",["shipId"]=1,["slot"]="FrameShiftDrive",["remove"]=true},await Version());
        Assert.Null((await ReadNative()).Snapshot.Vessels[0].Modules.Single(value=>value.Name=="FrameShiftDrive").Module!.Engineering);
    }
    [Fact]public async Task VerifiedNativeRejectionReconcilesOnlyTheRecoveredModule()
    {
        var prepared=await workspace.PrepareLiveRefitAsync(token,account,LiveProperties(),await Version());
        await workspace.EditAsync(token,account,InventoryEdit.Credits,new(){["delta"]=17},await Version());
        var before=prepared.Before! with{Ammo=new(1,1),HealthMillion=950000};
        var result=new LiveRefitResult(prepared.Id,false,"native_apply_failed_restored","boundary fixture",new(prepared.Slot,before),before,true);
        await workspace.RestoreFailedLiveRefitAsync(prepared,result);
        var save=await ReadNative();Assert.Equal(117,save.Snapshot.Credits);
        Assert.Equal(950000U,save.Snapshot.Vessels[0].Modules.Single(value=>value.Name==prepared.Slot).Module!.HealthMillion);
        var bytes=await File.ReadAllBytesAsync(Path.Combine(root,"state.json"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>workspace.RestoreFailedLiveRefitAsync(prepared,result with{Restored=false}));
        Assert.Equal(bytes,await File.ReadAllBytesAsync(Path.Combine(root,"state.json")));
    }
}
