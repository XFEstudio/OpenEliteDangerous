using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace XFEEliteDangerous.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeCommodityDefinition
{
    public required uint Id{get;init;}
    public required string Name{get;init;}
    public required decimal MeanCost{get;init;}
    public uint Display{get;init;}=1;
    public uint CarrierDisplay{get;init;}=1;
    internal object Public()=>new{id=Id,name=Name,cost_mean=MeanCost.ToString(CultureInfo.InvariantCulture),display=Display,carrierdisplay=CarrierDisplay};
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativePermitDefinition
{
    public required uint Id{get;init;}
    public string Description{get;init;}="";
    public required IReadOnlyList<ulong> Systems{get;init;}
    internal object Public()=>new{id=Id,description=Description,systems=Systems};
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeUniverseResources
{
    public IReadOnlyList<NativeCommodityDefinition> Commodities{get;init;}=[];
    public IReadOnlyList<NativePermitDefinition> Permits{get;init;}=[];
    internal void Validate()
    {
        if(Commodities is null||Permits is null||Commodities.Count>16_384||Permits.Count>4096
            ||Commodities.Any(value=>value is null)||Permits.Any(value=>value is null))throw new ArgumentException("Invalid native resource collections.");
        if(Commodities.Select(value=>value.Id).Distinct().Count()!=Commodities.Count||Commodities.Select(value=>value.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=Commodities.Count
            ||Permits.Select(value=>value.Id).Distinct().Count()!=Permits.Count)throw new ArgumentException("Duplicate native resource identity.");
        foreach(var value in Commodities)
            if(value.Id==0||string.IsNullOrEmpty(value.Name)||value.Name.Length>200||value.Name.Any(character=>!char.IsAsciiLetterOrDigit(character)&&character!='_')
                ||value.MeanCost is <0 or >9_000_000_000_000||value.Display>2||value.CarrierDisplay>2)throw new ArgumentException("Invalid native commodity definition.");
        foreach(var value in Permits)
            if(value.Id>ushort.MaxValue||value.Description is null||value.Description.Length>4096
                ||value.Systems is null||value.Systems.Count>65_536||value.Systems.Any(address=>address==0)||value.Systems.Distinct().Count()!=value.Systems.Count)
                throw new ArgumentException("Invalid native permit systems.");
    }
}
