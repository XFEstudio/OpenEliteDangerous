using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using XFEEliteDangerous.Client.Storage;

namespace XFEEliteDangerous.Core;

/// <summary>Confirmed upload-only fields, retained separately from the editable inventory.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeSavegameUploadState
{
    public required int OnFootDistanceTravelled {get;init;}
    public required bool NearPlanet {get;init;}
    public JsonElement? OnboardingObjectives {get;init;}
    public bool? OnboardingObjectivesComplete {get;init;}
    public IReadOnlyDictionary<uint,JsonElement> VesselExtras {get;init;}=new Dictionary<uint,JsonElement>();
}

/// <summary>
/// PUT writer 23F8220/23F8FB0 sends a runtime delta, not the GET document.
/// Validate and construct the whole candidate before changing the account.
/// No credits, cargo, ship membership, market transactions or world creation are inferred.
/// </summary>
internal static class NativeSavegameUpload
{
    private static readonly string[] Objectives=["ShipVendorAndTutorial","SuitWeaponsVendor","CreateLoadoutWithTerminal",
        "CompleteFirstMission","CommanderFirstShip","VisitShipVendor","FinishShipTutorial","VisitSuitWeaponsVendor",
        "PurchaseUtilitySuit","AccessTerminal","ApplyLoadout","GoToTurboLift","SelectShipHangarFloor","BoardShip",
        "AccessMissionBoardTerminal","AcceptMission"];

    internal static NativeDataReply Handle(AccountState account,bool selected,byte[] body)
    {
        if(!selected)throw new NativeDataException(409,"Select a private commander before saving.");
        if(body.Length>1024*1024)throw new NativeDataException(413,"Native save upload exceeds the private limit.");
        var original=account.NativeSave??throw new NativeDataException(409,"Native save is not initialized.");
        var baseline=original.ExportCurrent(account);
        using var document=NativeJson.Parse(body);var root=document.RootElement;
        NativeJson.Fields(root,"commander","vessels","onboardingObjectives","onboardingObjectivesComplete");
        var commander=NativeJson.Field(root,"commander");
        NativeJson.Fields(commander,"vehicleDistanceTravelled","onFootDistanceTravelled","startupState","nearplanet","gender","atrLevel","markedWanted");
        var profile=baseline.Profile with
        {
            VehicleDistanceTravelled=I32(commander,"vehicleDistanceTravelled"),StartupState=NativeJson.U32(commander,"startupState"),
            AtrLevel=NativeJson.U32(commander,"atrLevel"),MarkedWanted=NativeJson.Boolean(commander,"markedWanted"),
            Gender=commander.TryGetProperty("gender",out _)?NativeJson.Text(commander,"gender",1):baseline.Profile.Gender
        };
        var extras=new NativeSavegameUploadState
        {
            OnFootDistanceTravelled=I32(commander,"onFootDistanceTravelled"),NearPlanet=NativeJson.Boolean(commander,"nearplanet"),
            OnboardingObjectives=root.TryGetProperty("onboardingObjectives",out var onboarding)?Onboarding(onboarding):baseline.UploadState?.OnboardingObjectives,
            OnboardingObjectivesComplete=root.TryGetProperty("onboardingObjectivesComplete",out _)
                ?NativeJson.Boolean(root,"onboardingObjectivesComplete"):baseline.UploadState?.OnboardingObjectivesComplete,
            VesselExtras=new Dictionary<uint,JsonElement>(baseline.UploadState?.VesselExtras??new Dictionary<uint,JsonElement>())
        };
        var entries=NativeJson.Field(root,"vessels");
        if(entries.ValueKind!=JsonValueKind.Array||entries.GetArrayLength()>baseline.Snapshot.Vessels.Count)
            throw new NativeDataException(400,"Invalid native vessel delta collection.");
        var updates=new Dictionary<uint,NativeShipSnapshot>();
        foreach(var entry in entries.EnumerateArray())
        {
            NativeJson.Fields(entry,"ownedshipid","parentshipid","prototype","location","state");
            var id=NativeJson.U32(entry,"ownedshipid");
            var ship=baseline.Snapshot.Vessels.SingleOrDefault(value=>value.Id==id)
                ??throw new NativeDataException(404,"Native upload references an unowned vessel.");
            if(updates.ContainsKey(id))throw new NativeDataException(400,"Duplicate native vessel delta.");
            if(entry.TryGetProperty("parentshipid",out _)&&NativeJson.U32(entry,"parentshipid")!=ship.ParentShipId)
                throw new NativeDataException(409,"Native vessel parent changes require a separate transaction.");
            // This resource label describes the already identified vessel. It is
            // not a catalogue ID and must not replace the server's ship definition.
            _=NativeJson.Text(entry,"prototype",256);
            var location=NativeJson.Field(entry,"location");NativeJson.Fields(location,"ua","landed");
            NativeLocationStringValue position=ship.Location;
            if(location.TryGetProperty("ua",out _))
            {
                if(!NativeLocationStringCodec.TryParse(NativeJson.Text(location,"ua",512),out position))
                    throw new NativeDataException(400,"Invalid native uploaded location.");
                if((position.Address&0x007f_ffff_ffff_ffffUL)!=profile.CurrentSystem)
                    throw new NativeDataException(409,"A system transition requires its own confirmed system state.");
            }
            var extra=new JsonObject();
            if(extras.VesselExtras.TryGetValue(id,out var previous))extra=JsonNode.Parse(previous.GetRawText())!.AsObject();
            if(location.TryGetProperty("landed",out _))extra["landed"]=NativeJson.Boolean(location,"landed");
            ship=ApplyState(ship,NativeJson.Field(entry,"state"),extra) with {Location=position,
                LastLocation=position==ship.Location?ship.LastLocation:ship.Location};
            updates.Add(id,ship);
            ((Dictionary<uint,JsonElement>)extras.VesselExtras)[id]=JsonSerializer.SerializeToElement(extra,Wire.Json);
        }
        var vessels=baseline.Snapshot.Vessels.Select(ship=>updates.GetValueOrDefault(ship.Id,ship)).ToArray();
        var active=vessels.Single(ship=>ship.Id==baseline.Snapshot.CurrentVesselId);
        var candidate=baseline with{Profile=profile,UploadState=extras,
            Snapshot=baseline.Snapshot with{Vessels=vessels,Location=active.Location}};
        try
        {
            var inventory=candidate.CreateInventory(account,account.Commander.Revision);
            _=candidate.SerializeCurrent(withCommander(inventory));
            if(JsonSerializer.Serialize(candidate,Wire.Json)==JsonSerializer.Serialize(baseline,Wire.Json))return new(200,"<data />");
            if(account.Commander.Revision==long.MaxValue)throw new NativeDataException(409,"Native inventory revision exhausted.");
            inventory.Revision=account.Commander.Revision+1;
            account.NativeSave=candidate;account.Commander=inventory;
            // The native upload completion consumes the captured XML acknowledgement,
            // even though the upload itself and the GET save are JSON documents.
            return new(200,"<data />");
        }
        catch(Exception error)when(error is ArgumentException or InvalidOperationException or OverflowException)
        {throw new NativeDataException(400,"Native save delta is inconsistent with the private state.");}

        AccountState withCommander(CommanderState value)=>new(){NativeUserId=account.NativeUserId,Commander=value};
    }

    private static NativeShipSnapshot ApplyState(NativeShipSnapshot ship,JsonElement state,JsonObject extra)
    {
        if(state.ValueKind!=JsonValueKind.Object)throw new NativeDataException(400,"Expected native vessel state.");
        var groups=ship.Firegroups.ToDictionary(value=>value.Index);var capacitors=ship.Capacitors.ToDictionary(value=>value.Index);
        foreach(var field in state.EnumerateObject())
        {
            var key=field.Name;
            switch(key)
            {
                case "cockpitBreached":ship=ship with{CockpitBreached=NativeJson.Boolean(state,key)};break;
                case "fuel.lvl":ship=ship with{Fuel=ship.Fuel with{Level=Quantity(state,key,ship.Fuel.Capacity)}};break;
                case "reserve.lvl":ship=ship with{Fuel=ship.Fuel with{ReserveLevel=Quantity(state,key,ship.Fuel.Capacity)}};break;
                case "health.hull":ship=ship with{Health=ship.Health with{HullMillion=Million(state,key)}};break;
                case "health.shield":ship=ship with{Health=ship.Health with{ShieldMillion=Million(state,key)}};break;
                case "health.integrity":ship=ship with{Health=ship.Health with{IntegrityMillion=Million(state,key)}};break;
                case "health.paintwork":ship=ship with{Health=ship.Health with{PaintworkMillion=Million(state,key)}};break;
                case "health.shieldup":ship=ship with{Health=ship.Health with{ShieldUp=NativeJson.Boolean(state,key)}};break;
                case "oxygenRemaining":ship=ship with{OxygenRemainingMilliseconds=NativeJson.U32(state,key)};break;
                case "radarRangeSelection":ship=ship with{RadarRangeSelection=NativeJson.U32(state,key)};break;
                case "weaponMode":ship=ship with{WeaponMode=NativeJson.U32(state,key)};break;
                case "firegroupSelected":case "health.caustic":case "health.scorch":case "fuel.superchargedFSD":
                    extra[key]=NativeJson.U32(state,key);break;
                case "health.shielddamagetimer":extra[key]=Quantity(state,key,float.MaxValue);break;
                case "purchaseData":extra[key]=NativeJson.Text(state,key,32_768);break;
                default:
                    var path=key.Split('.');
                    if(path.Length==3&&path[0] is "firegroups" or "capacitors"&&uint.TryParse(path[1],NumberStyles.None,CultureInfo.InvariantCulture,out var index))
                    {
                        if(path[0]=="firegroups"&&index<8&&path[2] is "primary" or "secondary")
                        {
                            var group=groups.GetValueOrDefault(index,new(index,[],[]));
                            var indices=Indices(NativeJson.Text(state,key,8192));
                            groups[index]=path[2]=="primary"?group with{Primary=indices}:group with{Secondary=indices};break;
                        }
                        if(path[0]=="capacitors"&&index<16&&path[2] is "energy" or "pips")
                        {
                            var capacitor=capacitors.GetValueOrDefault(index,new(index,0,null));
                            //23FA714 scales the absolute capacitor energy by
                            //one million. It is not a normalized health ratio.
                            capacitors[index]=path[2]=="energy"?capacitor with{EnergyMillion=NativeJson.U32(state,key)}
                                :capacitor with{Pips=Byte(state,key)};break;
                        }
                    }
                    if(key.StartsWith("modules.",StringComparison.Ordinal))
                    {ship=Module(ship,state,key);break;}
                    throw new NativeDataException(501,"Native save contains an unsupported runtime branch: "+key);
            }
        }
        return ship with{Firegroups=groups.Values.OrderBy(value=>value.Index).ToArray(),Capacitors=capacitors.Values.OrderBy(value=>value.Index).ToArray()};
    }

    private static NativeShipSnapshot Module(NativeShipSnapshot ship,JsonElement state,string key)
    {
        var slot=ship.Modules.Concat(ship.FixedModules).SingleOrDefault(value=>key.StartsWith("modules."+value.Name+".",StringComparison.Ordinal));
        if(slot?.Module is not{} module)throw new NativeDataException(409,"Native save module is absent from the recorded fit.");
        var member=key[(9+slot.Name.Length)..];
        module=member switch
        {
            "id"=>module with{Uid=NativeJson.U64(state,key)},
            "health"=>module with{HealthMillion=Million(state,key)},"on"=>module with{On=NativeJson.Boolean(state,key)},
            "priority"=>module with{Priority=NativeJson.U32(state,key)},"boost"=>module with{Boost=NativeJson.U32(state,key)},
            "damageOverride"=>module with{DamageOverride=NativeJson.U32(state,key)},
            "ammo.clip"=>module with{Ammo=new(NativeJson.U32(state,key),module.Ammo?.Hopper??0)},
            "ammo.hopper"=>module with{Ammo=new(module.Ammo?.Clip??0,NativeJson.U32(state,key))},
            _=>throw new NativeDataException(501,"Native save module field is not implemented: "+key)
        };
        NativeSlotSnapshot Replace(NativeSlotSnapshot value)=>ReferenceEquals(value,slot)?value with{Module=module}:value;
        return ship with{Modules=ship.Modules.Select(Replace).ToArray(),FixedModules=ship.FixedModules.Select(Replace).ToArray()};
    }

    internal static void Validate(NativeCommanderSave save)
    {
        if(save.UploadState is not{} upload)return;
        if(upload.OnboardingObjectives is{} objectives)_=Onboarding(objectives);
        if(upload.VesselExtras is null||upload.VesselExtras.Count>save.Snapshot.Vessels.Count)throw new ArgumentException("Invalid uploaded native runtime state.");
        foreach(var pair in upload.VesselExtras)
        {
            if(!save.Snapshot.Vessels.Any(ship=>ship.Id==pair.Key))throw new ArgumentException("Unowned uploaded native runtime state.");
            NativeJson.Fields(pair.Value,"landed","firegroupSelected","health.caustic","health.scorch","health.shielddamagetimer","fuel.superchargedFSD","purchaseData");
            foreach(var field in pair.Value.EnumerateObject())
                if(field.Name=="landed")_=NativeJson.Boolean(pair.Value,field.Name);
                else if(field.Name=="purchaseData")_=NativeJson.Text(pair.Value,field.Name,32_768);
                else if(field.Name=="health.shielddamagetimer")_=Quantity(pair.Value,field.Name,float.MaxValue);
                else _=NativeJson.U32(pair.Value,field.Name);
        }
    }

    internal static string Compose(string json,NativeCommanderSave save)
    {
        if(save.UploadState is not{} upload)return json;
        Validate(save);var root=JsonNode.Parse(json)!.AsObject();var commander=root["commander"]!.AsObject();
        commander["onFootDistanceTravelled"]=upload.OnFootDistanceTravelled;commander["nearplanet"]=upload.NearPlanet;
        if(upload.OnboardingObjectives is{} objectives)root["onboardingObjectives"]=JsonNode.Parse(objectives.GetRawText());
        if(upload.OnboardingObjectivesComplete is{} complete)root["onboardingObjectivesComplete"]=complete;
        foreach(var node in root["vessels"]!.AsArray())
        {
            var ship=node!.AsObject();if(!upload.VesselExtras.TryGetValue(ship["id"]!.GetValue<uint>(),out var fields))continue;
            foreach(var field in fields.EnumerateObject())
            {
                var target=ship;var path=field.Name=="landed"?new[]{"location","landed"}:field.Name.Split('.');
                for(var i=0;i<path.Length-1;i++)
                {if(target[path[i]] is null)target[path[i]]=new JsonObject();target=target[path[i]]!.AsObject();}
                target[path[^1]]=JsonNode.Parse(field.Value.GetRawText());
            }
        }
        return root.ToJsonString(Wire.Json);
    }

    private static JsonElement Onboarding(JsonElement node)
    {
        NativeJson.Fields(node,"objectives","extraData");var objectives=NativeJson.Field(node,"objectives");
        NativeJson.Fields(objectives,Objectives);foreach(var field in objectives.EnumerateObject())_=NativeJson.Boolean(objectives,field.Name);
        var data=NativeJson.Field(node,"extraData");NativeJson.Fields(data,"firstPurchasedSuit");
        if(data.TryGetProperty("firstPurchasedSuit",out _))_=NativeJson.Text(data,"firstPurchasedSuit",256);
        return node.Clone();
    }
    private static uint[] Indices(string text)
    {
        if(text.Length==0)return[];
        var values=text.Split(',');if(values.Length>1024)throw new NativeDataException(400,"Native firegroup exceeds its bound.");
        var result=new uint[values.Length];for(var i=0;i<result.Length;i++)
            if(!uint.TryParse(values[i].AsSpan().Trim(' '),NumberStyles.None,CultureInfo.InvariantCulture,out result[i])||result[i]>=1024)
                throw new NativeDataException(400,"Invalid native firegroup index.");
        if(result.Distinct().Count()!=result.Length)throw new NativeDataException(400,"Duplicate native firegroup index.");return result;
    }
    private static int I32(JsonElement node,string key)
    {var value=NativeJson.Field(node,key);return value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out var number)?number:throw new NativeDataException(400,"Invalid native signed distance.");}
    private static uint Million(JsonElement node,string key)
    {var value=NativeJson.U32(node,key);return value<=1_000_000?value:throw new NativeDataException(400,
        "Native normalized state '"+key+"'="+value.ToString(CultureInfo.InvariantCulture)+" exceeds 1000000.");}
    private static uint Byte(JsonElement node,string key)
    {var value=NativeJson.U32(node,key);return value<=255?value:throw new NativeDataException(400,"Native capacitor pips exceed the stored byte.");}
    private static float Quantity(JsonElement node,string key,float maximum)
    {
        var field=NativeJson.Field(node,key);
        return field.ValueKind==JsonValueKind.Number&&field.TryGetSingle(out var value)&&float.IsFinite(value)&&value>=0&&value<=maximum
            ?value:throw new NativeDataException(400,"Invalid native runtime quantity.");
    }
}
