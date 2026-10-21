using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace XFEEliteDangerous.Core;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(
            CoreJsonContext.Default, new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver())
    };
    public static string Id() => Guid.NewGuid().ToString("N");
    public static string Secret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    public static bool Equal(string? a, string? b) => a is not null && b is not null && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}

public sealed class Permissions
{
    public bool AllowPlayerCredits { get; set; }
    public bool AllowPlayerCargo { get; set; }
    public bool AllowPlayerMaterials { get; set; }
    public bool AllowPlayerShipEditing { get; set; }
}

public static class AccountRoles
{
    public const string Player = "player";
    public const string Administrator = "administrator";
    public static bool IsValid(string? role) => role is Player or Administrator;
}

public sealed class DatabaseState
{
    public int Schema { get; set; } = 1;
    public string? ServerName { get; set; }
    public string? GameDirectory { get; set; }
    public bool? AllowPlayerRegistration { get; set; }
    public Permissions Permissions { get; set; } = new();
    public Dictionary<string, AccountState> Accounts { get; set; } = [];

    public List<CatalogEntry> Catalog { get; set; } = BundledData.Catalog.ToList();
    public NativeUniverseResources NativeResources{get;set;}=new();
    public long NativeResourceRevision{get;set;}
}

public sealed class AccountState
{
    public string Id { get; set; } = Wire.Id();
    /// <summary>Independent private-server numeric identity used by the native XML API. Zero is unallocated.</summary>
    public uint NativeUserId { get; set; }
    public NativeTutorialProfile NativeTutorial { get; set; } = new();
    public NativeCommanderPreferences NativePreferences {get;set;}=new();
    public List<NativeBookmark> NativeBookmarks{get;set;}=[];
    /// <summary>Private-server wallet only. New and older schema-1 accounts start at zero;
    /// it grants no official wallet value, purchase or entitlement.</summary>
    public NativeArxLedger NativeArx { get; set; } = new();
    public NativeCommanderSave? NativeSave { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = AccountRoles.Player;
    public string PasswordSalt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool Disabled { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public CommanderState Commander { get; set; } = new();
    public object Public() => new { Id, Email, DisplayName, Role, Credits = Commander.Credits, Disabled };
}
public sealed class NativeArxLedger
{
    public int TotalBalance {get;set;}
}
public sealed class NativeTutorialProfile
{
    public bool HasChosenStarterPack { get; set; }
    public bool FlightActive{get;set;}
    public bool OnFootActive{get;set;}
    public NativeTutorialProgress Flight { get; set; } = new();
    public NativeTutorialProgress OnFoot { get; set; } = new();
    public object Public() => new { hasChosenStarterPack = HasChosenStarterPack,
        tutorials = new Dictionary<string, NativeTutorialProgress> { ["flight"] = Flight, ["on_foot"] = OnFoot } };
}
public sealed class NativeCommanderPreferences
{
    public bool AcceptingSquadronInvites{get;set;}=true;
    public string PrivacyMode{get;set;}="PRIVATE";
    public object Public()=>new{settings=new{accepting_squadron_invites=AcceptingSquadronInvites,privacy_mode=PrivacyMode}};
}
public sealed class NativeTutorialProgress
{
    public uint CurrentCheckpoint { get; set; }
    public bool Complete { get; set; }
}
public sealed class CommanderState
{
    public string Id { get; set; } = Wire.Id();
    public string Name { get; set; } = "";
    public long Credits { get; set; }
    public long Revision { get; set; }
    public List<InventoryStack> Cargo { get; set; } = [];
    public List<InventoryStack> Materials { get; set; } = [];
    public List<ShipState> Ships { get; set; } = [];
    public string Source { get; set; } = "private-account";
    public object Public() => new { Id, Name, Credits, Cargo, Materials, Ships, Source, Version = Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) };
}
public sealed class InventoryStack
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
    public string Category { get; set; } = "cargo";
}
public sealed class ShipState
{
    public string Id { get; set; } = "";
    public string ShipType { get; set; } = "";
    public string Name { get; set; } = "";
    public string Ident { get; set; } = "";
    public bool Active { get; set; }
    public List<ModuleState> Modules { get; set; } = [];
}
public sealed class ModuleState
{
    public string Slot { get; set; } = "";
    public string ItemId { get; set; } = "";
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;
    public double Health { get; set; } = 1;
    public long Value { get; set; }
    public JsonObject? Engineering { get; set; }
}
public sealed record CatalogEntry(string Id, string Name, string Category);
