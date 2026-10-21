using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XFEEliteDangerous.Core;

/// <summary>
/// Explicit private economic/political metadata for one persisted system. The
/// native 346ED60/346F0C0 reader requires two starsystem object levels. This is
/// separate from the StellarForge key, physical hierarchy and world entities.
/// Nonempty polity/settlement schemas are deliberately outside this subset.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeSystemMetadata
{
    public required ulong Address {get;init;}
    public required ulong Id {get;init;}
    public required string Name {get;init;}
    public required ulong LastChanged {get;init;}
    public required ulong CacheUntil {get;init;}
    public required ulong Population {get;init;}
    public required float Wealth {get;init;}
    public required float Security {get;init;}
    public required float StandardOfLiving {get;init;}
    public required float DevelopmentLevel {get;init;}
    public required NativeEmptyObject Distances {get;init;}
    public required NativeEmptyObject Polities {get;init;}
    public required NativeEmptyObject UnownedSettlements {get;init;}

    internal void Validate(ulong currentSystem)
    {
        if(Address!=currentSystem||Address>0x007F_FFFF_FFFF_FFFFUL||Id==0)
            throw new ArgumentException("Private system metadata must match the recorded current system and have an explicit local identity.");
        if(string.IsNullOrWhiteSpace(Name)||Name.Length>128||Name.Any(char.IsControl)
            ||new[]{Wealth,Security,StandardOfLiving,DevelopmentLevel}.Any(value=>!float.IsFinite(value)||value<0)
            ||Distances is null||Polities is null||UnownedSettlements is null)
            throw new ArgumentException("Invalid private system metadata subset.");
    }
    internal string Serialize(ulong currentSystem)
    {
        Validate(currentSystem);
        using var output=new MemoryStream();
        using(var writer=new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();writer.WriteStartObject("starsystem");writer.WriteStartObject("starsystem");
            writer.WriteNumber("lastChanged",LastChanged);writer.WriteNumber("population",Population);
            writer.WriteNumber("wealth",Wealth);writer.WriteNumber("security",Security);
            writer.WriteNumber("standardofliving",StandardOfLiving);writer.WriteNumber("developmentlevel",DevelopmentLevel);
            writer.WriteString("name",Name);writer.WriteNumber("address",Address);writer.WriteNumber("id",Id);
            Empty(writer,"distances");writer.WriteEndObject();
            Empty(writer,"polities");Empty(writer,"unownedSettlements");writer.WriteNumber("cacheuntil",CacheUntil);
            writer.WriteEndObject();writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    private static void Empty(Utf8JsonWriter writer,string name){writer.WriteStartObject(name);writer.WriteEndObject();}
}
