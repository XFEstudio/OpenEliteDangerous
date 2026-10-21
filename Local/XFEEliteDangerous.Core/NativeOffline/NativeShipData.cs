using System.Globalization;
using System.Text.Json;

namespace XFEEliteDangerous.Core;

internal static class NativeShipData
{
    internal static NativeDataReply? Handle(AccountState account,string method,string path,IReadOnlyDictionary<string,string> query,byte[] body)
    {
        if(path is not("/2.0/elite/fuel/set" or "/2.0/elite/commander/ship" or "/2.0/elite/commander/storedmodules"))return null;
        NativeProfileData.VerifyCommander(account,query);
        if(path=="/2.0/elite/commander/storedmodules")
        {
            NativeProfileData.Method(method,"GET");NativeProfileData.Empty(body);
            return NativeDataReply.Json(new{storage=account.NativeSave?.StoredModules.Select(value=>value.Public()).ToArray()??[]});
        }
        if(path=="/2.0/elite/commander/ship")
        {
            NativeProfileData.Method(method,"GET");NativeProfileData.Empty(body);
            var shipText=NativeProfileData.Text(query,"shipId");
            if(shipText!="Invalid"&&(!uint.TryParse(shipText,NumberStyles.None,CultureInfo.InvariantCulture,out var parsed)||parsed==uint.MaxValue))
                throw new NativeDataException(400,"Invalid native vessel identity.");
            if(query.TryGetValue("includeLaunchbay",out var include)&&include is not("0" or "1"))throw new NativeDataException(400,"Invalid launchbay selection.");
            var save=account.NativeSave?.ExportCurrent(account)??throw new NativeDataException(409,"Native vessel is not initialized.");
            var target=shipText=="Invalid"?save.Snapshot.CurrentVesselId:uint.Parse(shipText,CultureInfo.InvariantCulture);
            if(!save.Snapshot.Vessels.Any(value=>value.Id==target))throw new NativeDataException(404,"Private vessel not found.");
            if(include=="1"&&save.Snapshot.Vessels.Any(value=>value.ParentShipId==target))
                throw new NativeDataException(501,"Native launchable vessel composition is not implemented.");
            using var document=JsonDocument.Parse(account.NativeSave!.SerializeCurrent(account));
            return NativeDataReply.Json(new{ship=document.RootElement.GetProperty("vessels").EnumerateArray().Single(value=>value.GetProperty("id").GetUInt32()==target)});
        }
        NativeProfileData.Method(method,"PUT");NativeProfileData.Empty(body);
        var level=Quantity(query,"level");var reserve=Quantity(query,"reserve");
        if(!long.TryParse(NativeProfileData.Text(query,"finalCredits"),NumberStyles.None,CultureInfo.InvariantCulture,out var credits))
            throw new NativeDataException(400,"Invalid native refuel balance.");
        var baseline=account.NativeSave??throw new NativeDataException(409,"Native vessel is not initialized.");
        var current=baseline.ExportCurrent(account);
        var ship=current.Snapshot.Vessels.Single(value=>value.Id==current.Snapshot.CurrentVesselId);
        if(level>ship.Fuel.Capacity||reserve>ship.Fuel.Capacity)throw new NativeDataException(409,"Native fuel exceeds the recorded tank bound.");
        // This endpoint reports an absolute post-refuel balance. Never implement
        // it as a repeated subtraction or let it create credits on this server.
        if(credits>account.Commander.Credits)throw new NativeDataException(409,"Native refuel cannot increase the account balance.");
        if(level==ship.Fuel.Level&&reserve==ship.Fuel.ReserveLevel&&credits==account.Commander.Credits)return new(200,"{}");
        // Keep the inventory's original cargo row keys. Replacing the baseline
        // with an already compacted projection would renumber retained rows.
        var vessels=baseline.Snapshot.Vessels.Select(value=>value.Id==ship.Id?value with{Fuel=value.Fuel with{Level=level,ReserveLevel=reserve}}:value).ToArray();
        var candidate=baseline with{Snapshot=baseline.Snapshot with{Credits=credits,Vessels=vessels}};
        _=NativeFullSavegameDocument.SerializeComplete(candidate.Snapshot,candidate.Catalogue,candidate.Profile);
        if(account.Commander.Revision==long.MaxValue)throw new NativeDataException(409,"Native inventory revision exhausted.");
        var revision=checked(account.Commander.Revision+1);
        account.NativeSave=candidate;account.Commander.Credits=credits;account.Commander.Revision=revision;
        return new(200,"{}");
    }
    private static float Quantity(IReadOnlyDictionary<string,string> query,string field)
    {
        var text=NativeProfileData.Text(query,field);
        return float.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&float.IsFinite(value)&&value>=0
            ?value:throw new NativeDataException(400,"Invalid native quantity: "+field);
    }
}
