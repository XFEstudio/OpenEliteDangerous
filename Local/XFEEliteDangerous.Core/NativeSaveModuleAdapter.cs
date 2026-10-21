using System.Text.Json;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Core;

/// <summary>Detached native save business and the fixed host's bounded commit boundary.</summary>
public static class NativeSaveModuleAdapter
{
    public static NativeSaveModuleReply Evaluate(NativeSaveModuleRequest request)
    {
        try
        {
            if(request.Protocol!=1||request.NativeUserId==0||request.CommanderId.Length is 0 or >128
                ||request.Revision<0||request.SaveJson.Length is 0 or >4*1024*1024||request.Body.Length>1024*1024)
                throw new InvalidDataException("Invalid detached native save request.");
            var save=JsonSerializer.Deserialize<NativeCommanderSave>(request.SaveJson,Wire.Json)
                ??throw new InvalidDataException("Missing detached native save.");
            var account=new AccountState{NativeUserId=request.NativeUserId,NativeSave=save,
                Commander=new(){Id=request.CommanderId,Revision=request.Revision}};
            account.Commander=save.CreateInventory(account,request.Revision);
            var result=NativeSavegameUpload.Handle(account,request.Selected,request.Body);
            return new(1,result.Status,result.Payload,JsonSerializer.Serialize(account.NativeSave!.ExportCurrent(account),Wire.Json));
        }
        catch(NativeDataException error)
        {return new(1,error.Status,JsonSerializer.Serialize(new{error=error.Message},Wire.Json),null,
            error.GetType().FullName,error.Message,error.ToString());}
    }

    internal static NativeSaveModuleRequest Request(AccountState account,bool selected,byte[] body)
    {
        if(!selected)throw new NativeDataException(409,"Select a private commander before saving.");
        var save=account.NativeSave??throw new NativeDataException(409,"Native save is not initialized.");
        return new(1,account.NativeUserId,account.Commander.Id,account.Commander.Revision,selected,
            JsonSerializer.Serialize(save.ExportCurrent(account),Wire.Json),body);
    }

    internal static NativeDataReply Commit(AccountState account,NativeSaveModuleReply reply)
    {
        if(reply.Protocol!=1||reply.Status is <200 or >599||reply.Payload is null||reply.Payload.Length>65536)
            throw new InvalidDataException("Invalid native save module reply contract.");
        if(reply.Status>=400)
        {
            if(reply.SaveJson is not null)throw new InvalidDataException("A rejected save returned a candidate.");
            return new(reply.Status,reply.Payload);
        }
        if(reply.Status!=200||reply.ErrorType is not null||reply.SaveJson is null||reply.SaveJson.Length is 0 or >4*1024*1024)
            throw new InvalidDataException("Missing successful native save candidate.");
        var before=account.NativeSave!.ExportCurrent(account);
        var candidate=JsonSerializer.Deserialize<NativeCommanderSave>(reply.SaveJson,Wire.Json)
            ??throw new InvalidDataException("Missing native save candidate.");
        if(!JsonNode.DeepEquals(Protected(before),Protected(candidate)))
            throw new InvalidDataException("The native save module changed protected inventory or world fields.");
        var oldJson=JsonSerializer.Serialize(before,Wire.Json);
        var newJson=JsonSerializer.Serialize(candidate,Wire.Json);
        if(oldJson==newJson)return new(reply.Status,reply.Payload);
        if(account.Commander.Revision==long.MaxValue)throw new NativeDataException(409,"Native inventory revision exhausted.");
        var inventory=candidate.CreateInventory(account,account.Commander.Revision+1);
        var temporary=new AccountState{NativeUserId=account.NativeUserId,NativeSave=candidate,Commander=inventory};
        candidate.ValidateCurrent(temporary);
        // StateStore's enclosing transaction persists this pair atomically.
        account.NativeSave=candidate;account.Commander=inventory;
        return new(reply.Status,reply.Payload);
    }

    private static JsonObject Protected(NativeCommanderSave save)
    {
        var root=JsonSerializer.SerializeToNode(save,Wire.Json)!.AsObject();
        root.Remove("uploadState");
        var profile=root["profile"]!.AsObject();
        foreach(var name in new[]{"vehicleDistanceTravelled","startupState","atrLevel","markedWanted","gender"})profile.Remove(name);
        var snapshot=root["snapshot"]!.AsObject();snapshot.Remove("location");
        foreach(var item in snapshot["vessels"]!.AsArray())
        {
            var ship=item!.AsObject();
            foreach(var name in new[]{"cockpitBreached","oxygenRemainingMilliseconds","radarRangeSelection","weaponMode",
                "health","location","lastLocation","firegroups","capacitors"})ship.Remove(name);
            var fuel=ship["fuel"]!.AsObject();fuel.Remove("level");fuel.Remove("reserveLevel");
            foreach(var list in new[]{"modules","fixedModules"})
                foreach(var slot in ship[list]!.AsArray())
                    if(slot!["module"] is JsonObject module)
                        foreach(var name in new[]{"uid","healthMillion","on","priority","boost","damageOverride","ammo"})module.Remove(name);
        }
        return root;
    }
}
