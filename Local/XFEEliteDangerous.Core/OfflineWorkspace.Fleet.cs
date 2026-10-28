using XFEEliteDangerous.Client;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace XFEEliteDangerous.Core;

public sealed partial class OfflineWorkspace
{
    public Task<JsonNode> OpenLocalOwnerSessionAsync()=>OpenOwner();
    private async Task<JsonNode> OpenOwner()
    {
        var owner=await store.Update(state=>
        {
            const string email="local-owner@xfe.local";
            var account=state.Accounts.Values.SingleOrDefault(value=>value.Email==email);
            if(account is null)account=Register(state,email,AppText.Get("OfflineWorkspace_Fleet.Text403"),Wire.Secret(),AccountRoles.Administrator);
            if(account.Disabled||account.Role!=AccountRoles.Administrator)throw new UnauthorizedAccessException(AppText.Get("OfflineWorkspace_Fleet.Text404"));
            return (account.Id,Profile:Json(account.Public()));
        });
        var token=Wire.Secret();sessions[token]=(owner.Id,clock.GetUtcNow().AddHours(12));
        return new JsonObject{["token"]=token,["account"]=owner.Profile};
    }
    public Task<JsonNode> FleetAsync(string? session,string? targetId)=>store.Read(state=>
    {
        var account=Target(state,session,targetId);var save=account.NativeSave?.ExportCurrent(account);
        if(save is null)return Json(new{commander=account.Commander.Public(),native=false,ships=account.Commander.Ships,stored=Array.Empty<object>()});
        var live=new LiveRefitMailbox(DirectoryPath,account.Id).ReadStatus();
        var liveReady=LiveRefitMailbox.Fresh(live,clock.GetUtcNow())&&live!.Flow=="InGame"&&live.ShipId==save.Snapshot.CurrentVesselId;
        NativeModuleSnapshot? VisibleModule(uint shipId,NativeSlotSnapshot slot)
        {
            var row=liveReady&&shipId==live!.ShipId?live.Slots.SingleOrDefault(value=>value.Slot==slot.Name):null;
            return row?.Module is {} current&&slot.Module is {} before&&LiveRefitMailbox.Matches(current,before)
                ?before with{On=current.On,Priority=row.PriorityEditable?current.Priority:before.Priority,HealthMillion=row.IntegrityEditable?current.HealthMillion:before.HealthMillion,Ammo=current.Ammo,Engineering=current.Engineering}:slot.Module;
        }
        var ships=save.Snapshot.Vessels.Select(ship=>new
        {
            ship.Id,prototype=ship.Name,label=save.ShipLabels.GetValueOrDefault(ship.Id,new(ship.Name,"")),active=ship.Id==save.Snapshot.CurrentVesselId,
            ship.Fuel,ship.Health,
            slots=ship.Modules.Select(slot=>new{slot.Name,Module=VisibleModule(ship.Id,slot),removable=Removable(slot.Name),
                integrityEditable=!(liveReady&&ship.Id==live!.ShipId&&live.Slots.SingleOrDefault(value=>value.Slot==slot.Name) is {IntegrityEditable:false}),
                priorityEditable=!(liveReady&&ship.Id==live!.ShipId&&live.Slots.SingleOrDefault(value=>value.Slot==slot.Name) is {PriorityEditable:false}),
                ammoEditable=liveReady&&ship.Id==live!.ShipId&&live.Slots.SingleOrDefault(value=>value.Slot==slot.Name) is {AmmoEditable:true},
                maximumHealth=liveReady&&ship.Id==live!.ShipId?live.Slots.SingleOrDefault(value=>value.Slot==slot.Name)?.MaximumHealth:null,
                ammoCapacity=liveReady&&ship.Id==live!.ShipId?live.Slots.SingleOrDefault(value=>value.Slot==slot.Name)?.AmmoCapacity:null,
                engineeringBlueprints=slot.Module is {} fitted?NativeEngineering.Blueprints(fitted.Name):[],
                experimentalEffects=slot.Module is {} installed?NativeEngineering.Effects(installed.Name):[],
                compatible=RefitDefinitions(save).Where(module=>Fits(slot,module)&&(!liveReady||ship.Id!=live!.ShipId||live.AvailableModules is null||live.AvailableModules.Contains(module.Name,StringComparer.Ordinal))).ToArray()}).ToArray()
        }).ToArray();
        return Json(new{commander=account.Commander.Public(),native=true,ships,stored=save.StoredModules,liveRefit=liveReady,liveCargoCapacity=liveReady?live!.CargoCapacity:null});
    });
    public Task<JsonNode> EditFleetAsync(string? session,string? targetId,JsonObject command,string version)
    {
        var body=(JsonObject)command.DeepClone();
        return store.Update(state=>
        {
            var actor=Authenticate(state,session);var account=Target(state,actor,targetId);
            if(actor.Role!=AccountRoles.Administrator&&!state.Permissions.AllowPlayerShipEditing)throw new UnauthorizedAccessException(AppText.Get("FleetPage.Text153"));
            CheckVersion(account.Commander,version);
            var save=account.NativeSave?.ExportCurrent(account)??throw new InvalidOperationException(AppText.Get("OfflineWorkspace_Fleet.Text405"));
            var ships=save.Snapshot.Vessels.ToList();var labels=new Dictionary<uint,NativeShipLabel>(save.ShipLabels);
            uint Id(string key)=>(uint)Integer(body,key,1,uint.MaxValue-1L);
            var action=Text(body,"action",40);var selected=action=="clone"?Id("templateShipId"):Id("shipId");
            var ship=ships.SingleOrDefault(value=>value.Id==selected)??throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text406"));
            if(action=="clone")
            {
                var id=checked(ships.Max(value=>value.Id)+1);if(id==uint.MaxValue)throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text407"));
                NativeSlotSnapshot Copy(NativeSlotSnapshot slot)=>slot with{Module=slot.Module is {} module?module with{Uid=null}:null};
                ships.Add(ship with{Id=id,Cargo=[],ParentShipId=null,Modules=ship.Modules.Select(Copy).ToArray(),FixedModules=ship.FixedModules.Select(Copy).ToArray()});
                labels[id]=new(Text(body,"name",100),Text(body,"ident",24));
            }
            else if(action=="rename")labels[ship.Id]=new(Text(body,"name",100),Text(body,"ident",24));
            else if(action=="remove")
            {
                if(ship.Id==save.Snapshot.CurrentVesselId||ship.Id==save.Snapshot.ParentVesselId||ships.Any(value=>value.ParentShipId==ship.Id))
                    throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text408"));
                ships.Remove(ship);labels.Remove(ship.Id);
                if(save.UploadState is {} upload)save=save with{UploadState=upload with{VesselExtras=upload.VesselExtras.Where(pair=>pair.Key!=ship.Id).ToDictionary()}};
            }
            else if(action=="select")
            {
                if((ship.Location.Address&0x007F_FFFF_FFFF_FFFFUL)!=save.Profile.CurrentSystem)throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text409"));
                save=save with{Snapshot=save.Snapshot with{CurrentVesselId=ship.Id,ParentVesselId=ship.Id,Location=ship.Location}};
            }
            else if(action is "module" or "store" or "retrieve")return CommitManagedNative(account,EditFleetModule(save,ship,body,action));
            else throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text418"));
            save=save with{Snapshot=save.Snapshot with{Vessels=ships},ShipLabels=labels};
            return CommitManagedNative(account,save);
        });
    }

    private static NativeCommanderSave EditFleetModule(NativeCommanderSave save,NativeShipSnapshot ship,JsonObject body,string action)
    {
        save=save with{Catalogue=save.Catalogue with{Modules=RefitDefinitions(save)}};
        uint Id(string key)=>(uint)Integer(body,key,1,uint.MaxValue-1L);
        var slotName=Text(body,"slot",100);var slot=ship.Modules.SingleOrDefault(value=>value.Name==slotName)??throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text410"));
        var storage=save.StoredModules.ToList();NativeModuleSnapshot? replacement;
        if(action=="store")
        {
            if(!Removable(slot.Name))throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text411"));
            replacement=null;
        }
        else if(action=="retrieve")
        {
            var entry=storage.SingleOrDefault(value=>value.SlotId==Id("storageId"))??throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text412"));
            if(entry.InTransit)throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text413"));
            if(!Fits(slot,new(entry.Module.Id,entry.Module.Name)))throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text414"));
            replacement=entry.Module;storage.Remove(entry);
        }
        else
        {
            var name=Text(body,"module",160);var definition=save.Catalogue.Modules.SingleOrDefault(value=>value.Name==name)
                ??throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text415"));
            if(!Fits(slot,definition))throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text416"));
            var source=save.Snapshot.Vessels.SelectMany(value=>value.Modules.Concat(value.FixedModules)).Select(value=>value.Module)
                .FirstOrDefault(value=>value?.Name==name)??save.StoredModules.Select(value=>value.Module).FirstOrDefault(value=>value.Name==name);
            replacement=source is {} template?template with{Uid=null}:new NativeModuleSnapshot{Id=definition.Id,Name=definition.Name,Value=0,Excess=0,On=true,Priority=0,HealthMillion=1_000_000,Free=true};
        }
        if(slot.Module is {} old)
        {
            if(old.Id==0)throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text417"));
            var storageId=storage.Count==0?1:checked(storage.Max(value=>value.SlotId)+1);
            storage.Add(new(){SlotId=storageId,SystemName=save.SystemMetadata?.Name??"Local",MarketId=0,InTransit=false,TransferTime=0,Value=checked((long)old.Value),Module=old});
        }
        ship=ship with{Modules=ship.Modules.Select(value=>value.Name==slotName?value with{Module=replacement}:value).ToArray()};
        save=save with{StoredModules=storage,Catalogue=save.Catalogue with{Ships=save.Catalogue.Ships.Select(definition=>definition.Name==ship.Name
            ?definition with{Modules=definition.Modules.Select(value=>Removable(value.Name)?value with{RequiredModule=false}:value).ToArray()}:definition).ToArray()}};
        return save with{Snapshot=save.Snapshot with{Vessels=save.Snapshot.Vessels.Select(value=>value.Id==ship.Id?ship:value).ToArray()}};
    }
    private static NativeModuleDefinition[] RefitDefinitions(NativeCommanderSave save)=>save.Catalogue.Modules
        .Concat(NativeEngineering.Catalogue.Modules.Where(value=>!save.Catalogue.Modules.Any(original=>original.Name==value.Name||original.Id!=0&&original.Id==value.Id))).ToArray();
    private JsonNode CommitManagedNative(AccountState account,NativeCommanderSave save)
    {
        var inventory=save.CreateInventory(account,checked(account.Commander.Revision+1));
        save.ValidateCurrent(new(){NativeUserId=account.NativeUserId,NativeSave=save,Commander=inventory});
        var backup=store.BackupNative(account);account.NativeSave=save;account.Commander=inventory;
        return Json(new{saved=true,backup,commander=inventory.Public(),requiresReload=true});
    }
    public Task<JsonNode> PrepareMaterialsAsync(string? session,string? targetId,string version)=>store.Update(state=>
    {
        var actor=Authenticate(state,session);RequireAdministrator(actor);var account=Target(state,actor,targetId);CheckVersion(account.Commander,version);
        var save=account.NativeSave?.ExportCurrent(account)??throw new InvalidOperationException(AppText.Get("OfflineWorkspace_Fleet.Text419"));
        var rows=save.Materials.Concat(BundledData.NativeMaterials.Where(def=>!save.Materials.Any(value=>value.Id==def.Id))).ToArray();
        foreach(var row in rows)
            if(!state.Catalog.Any(value=>value.Id==row.Id.ToString(CultureInfo.InvariantCulture)&&value.Category==row.Type))state.Catalog.Add(new(row.Id.ToString(CultureInfo.InvariantCulture),row.Name,row.Type));
        return CommitManagedNative(account,save with{Materials=rows});
    });
    private static bool Removable(string slot)=>slot.Contains("Hardpoint",StringComparison.Ordinal)||slot.StartsWith("Slot",StringComparison.Ordinal);
    public Task<JsonNode> InitializeNativeAsync(string? session,string? targetId,string version)=>store.Update(state=>
    {
        var actor=Authenticate(state,session);RequireAdministrator(actor);var account=Target(state,actor,targetId);CheckVersion(account.Commander,version);
        if(account.NativeSave is not null)throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text420"));
        if(account.Commander.Ships.Count>0||account.Commander.Cargo.Count>0||account.Commander.Materials.Count>0)
            throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text421"));
        var seed=state.Accounts.Values.Select(value=>value.NativeSave).FirstOrDefault(value=>value?.SystemMetadata is not null)
            ??throw new InvalidDataException(AppText.Get("OfflineWorkspace_Fleet.Text422"));
        var profile=seed.Profile with{Name=account.DisplayName,LoanCeiling=0,Debt=0,StartupState=0,ProvingGround=new(0,0,0,0),BattleRating=0,
            Avatar="",AvatarXml="",VehicleDistanceTravelled=0,AtrLevel=0,Notoriety=0,MarkedWanted=false};
        var save=NativeSidewinderSaveFactory.Create(account.NativeUserId,1,account.Commander.Credits,profile,seed.Snapshot.Location)
            with{SystemMetadata=seed.SystemMetadata,Materials=BundledData.NativeMaterials,ShipLabels=new Dictionary<uint,NativeShipLabel>{{1,new("Sidewinder","XFE-01")}}};
        foreach(var row in save.Materials)
            if(!state.Catalog.Any(value=>value.Id==row.Id.ToString(CultureInfo.InvariantCulture)&&value.Category==row.Type))state.Catalog.Add(new(row.Id.ToString(CultureInfo.InvariantCulture),row.Name,row.Type));
        return CommitManagedNative(account,save);
    });
    private static bool Fits(NativeSlotSnapshot slot,NativeModuleDefinition module)
    {
        if(slot.Module?.Name==module.Name)return module.Id>0;
        if(module.Id==0)return false;
        var name=module.Name.ToLowerInvariant();var current=slot.Module?.Name.ToLowerInvariant();
        int Size(string value){var match=Regex.Match(value,"_size([0-9]+)",RegexOptions.IgnoreCase);return match.Success?int.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture):0;}
        if(slot.Name.Contains("Hardpoint",StringComparison.Ordinal))
        {
            if(!name.StartsWith("hpt_",StringComparison.Ordinal))return false;
            var sizes=new[]{"Tiny","Small","Medium","Large","Huge"};var capacity=Array.FindIndex(sizes,value=>slot.Name.StartsWith(value,StringComparison.OrdinalIgnoreCase));
            var size=Array.FindIndex(sizes,value=>name.EndsWith("_"+value,StringComparison.OrdinalIgnoreCase));return size>=0&&capacity>=size;
        }
        if(slot.Name.StartsWith("Slot",StringComparison.Ordinal))return name.StartsWith("int_",StringComparison.Ordinal)&&Size(name)<=Size(slot.Name)
            &&!new[]{"int_powerplant_","int_engine_","int_hyperdrive_","int_lifesupport_","int_powerdistributor_","int_sensors_","int_fueltank_"}.Any(name.StartsWith);
        if(current is null)return false;
        var prefix=current.Split("_size",StringSplitOptions.None)[0];return name.StartsWith(prefix+"_size",StringComparison.Ordinal)&&Size(name)<=Size(current);
    }
}
