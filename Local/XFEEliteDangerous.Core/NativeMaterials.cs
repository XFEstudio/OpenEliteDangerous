using System.Globalization;
using System.Text.Json.Nodes;

namespace XFEEliteDangerous.Core;

public sealed record NativeMaterialStack(uint Id,string Name,string Type,int Capacity,int Quantity);

internal static class NativeMaterials
{
    internal static void Validate(IReadOnlyList<NativeMaterialStack> rows)
    {
        if(rows is null||rows.Count>1024||rows.Any(row=>row is null)||rows.Select(row=>row.Id).Distinct().Count()!=rows.Count)
            throw new ArgumentException("Invalid native material collection.");
        foreach(var row in rows)
            if(row.Id==0||string.IsNullOrWhiteSpace(row.Name)||row.Name.Length>160||row.Name.Any(char.IsControl)
                ||row.Type is not("Raw" or "Encoded" or "Manufactured")||row.Capacity is <1 or >1000||row.Quantity<0||row.Quantity>row.Capacity)
                throw new ArgumentException("Invalid native material definition or quantity.");
    }
    internal static IReadOnlyList<NativeMaterialStack> Apply(IReadOnlyList<NativeMaterialStack> baseline,CommanderState commander)
    {
        Validate(baseline);
        if(commander.Materials.GroupBy(row=>row.Id).Any(group=>group.Count()!=1)
            ||commander.Materials.Any(row=>!baseline.Any(def=>def.Id.ToString(CultureInfo.InvariantCulture)==row.Id&&def.Type==row.Category)))
            throw new ArgumentException("Native material identity is absent from the recorded catalogue.");
        var result=baseline.Select(def=>def with{Quantity=commander.Materials.FirstOrDefault(row=>row.Id==def.Id.ToString(CultureInfo.InvariantCulture))?.Quantity??0}).ToArray();
        Validate(result);return result;
    }
    internal static string Compose(string document,IReadOnlyList<NativeMaterialStack> rows)
    {
        if(rows.Count==0)return document;
        Validate(rows);var root=JsonNode.Parse(document)!.AsObject();var inventory=new JsonObject();
        foreach(var row in rows)
            inventory[row.Id.ToString(CultureInfo.InvariantCulture)]=new JsonObject{["id"]=row.Id,["type"]=row.Type,["capacity"]=row.Capacity,["qty"]=row.Quantity};
        root["microresources"]!["inventory"]=inventory;
        return root.ToJsonString();
    }
}
