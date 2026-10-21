using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using XFEEliteDangerous.Client.Storage;
using XFEEliteDangerous.Core;
using Xunit;

namespace XFEEliteDangerous.Tests;

// Synthetic inputs demonstrate exact field/type contracts only, never a world or native acceptance fixture.
public sealed class NativeFullSavegameDocumentTests
{
    private static readonly JsonSerializerOptions StrictImportJson = new(Wire.Json)
    {
        NumberHandling = JsonNumberHandling.Strict, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static NativeSavegameCatalogue Catalogue => new()
    {
        Source = "explicit pure serializer fixture", PinnedVersion = "2026.09.03.332841", ValidationPending = true,
        Ships = [new("SideWinder", [], [])], Modules = []
    };
    private static NativeSavegameSnapshot Snapshot => new()
    {
        CommanderNativeId = 4_294_967_297, Credits = long.MaxValue, Alive = false,
        Location = new(42, new(1, 2, 3), new(0, 0, 0), false), CurrentVesselId = 17, ParentVesselId = 17,
        Vessels = [new NativeShipSnapshot
        {
            Id = 17, Name = "SideWinder", Alive = false, CockpitBreached = true, Excess = 0,
            OxygenRemainingMilliseconds = 300_000, RadarRangeSelection = 0, WeaponMode = 3,
            Fuel = new(2, 1.5f, .3f), Value = new(0, 0, 0), Health = new(0, 1_000_000, false, 900_000, 800_000),
            Location = new(42, new(1, 2, 3), new(0, 0, 0), false), LastLocation = new(43, new(4, 5, 6), new(0, 0, 0), false), Modules = [], FixedModules = [],
            Firegroups = [], Capacitors = [], Cargo = []
        }]
    };
    private static NativeFullSavegameProfile Profile => new()
    {
        Name = "Private Commander", LoanCeiling = long.MinValue, Debt = long.MaxValue, StartupState = 0x00FF_FFFF,
        Gender = "F", ProvingGround = new(1, 2, 3, uint.MaxValue), BattleRating = uint.MaxValue,
        Avatar = "", AvatarXml = "", VehicleDistanceTravelled = int.MinValue, AtrLevel = uint.MaxValue,
        Notoriety = int.MaxValue, MarkedWanted = false, CurrentSystemKey = "1", CurrentSystem = 2,
        GameVersion = new("private", "4.4.1.1", 332_841, 0),
        EmptyCollections = new()
        {
            Rank = new(), Constants = new(), Pinned = new(), CurrentCrew = new(), Suits = new(), WeaponRack = new(),
            SystemHyperspacePermits = new(), RegionHyperspacePermits = new(), LandingPermits = new(), LegacyFines = new(),
            LegalStates = new(), TaxiBookings = new(), ShipLocker = new(),
            Microresources = new() { Inventory = new(), Discovered = new() },
            OnboardingObjectives = new() { Objectives = new(), ExtraData = new() }
        }
    };

    [Fact]
    public void Full_fleet_retains_previous_location_and_rejects_missing_previous_state()
    {
        using var document=JsonDocument.Parse(NativeFullSavegameDocument.SerializeComplete(Snapshot,Catalogue,Profile));
        var vessel=document.RootElement.GetProperty("vessels")[0];
        Assert.Equal("ua",vessel.GetProperty("lastLocation").GetProperty("type").GetString());
        Assert.Equal("43,4,5,6,0,0,0,0",vessel.GetProperty("lastLocation").GetProperty("ua").GetString());
        Assert.Equal("42,1,2,3,0,0,0,0",vessel.GetProperty("location").GetProperty("ua").GetString());
        Assert.False(vessel.GetProperty("lastLocation").TryGetProperty("market",out _));
        Assert.Throws<ArgumentException>(()=>NativeFullSavegameDocument.SerializeComplete(Snapshot with {Vessels=[Snapshot.Vessels[0] with {LastLocation=null}]},Catalogue,Profile));
        Assert.Throws<ArgumentException>(()=>NativeFullSavegameDocument.SerializeComplete(Snapshot with {Vessels=[Snapshot.Vessels[0] with {LastLocation=new(0,new(0,0,0),new(0,0,0),false)}]},Catalogue,Profile));
    }

    [Fact]
    public void Full_profile_adds_exact_required_fields_and_preserves_the_startup_ship_document()
    {
        using var startup = JsonDocument.Parse(NativeSavegameDocument.Serialize(Snapshot, Catalogue));
        using var document = JsonDocument.Parse(NativeFullSavegameDocument.SerializeComplete(Snapshot, Catalogue, Profile));
        var root = document.RootElement;
        var commander = root.GetProperty("commander");
        Assert.Equal(Snapshot.CommanderNativeId, commander.GetProperty("id").GetUInt64());
        Assert.Equal(Profile.Name, commander.GetProperty("name").GetString());
        Assert.Equal(Snapshot.Credits, commander.GetProperty("credits").GetInt64());
        Assert.Equal(Snapshot.Alive, commander.GetProperty("alive").GetBoolean());
        Assert.Equal(long.MinValue, commander.GetProperty("loanCeiling").GetInt64());
        Assert.Equal(long.MaxValue, commander.GetProperty("debt").GetInt64());
        Assert.Equal(0x00FF_FFFFu, commander.GetProperty("startupState").GetUInt32());
        Assert.Equal("F", commander.GetProperty("gender").GetString());
        Assert.Equal(uint.MaxValue, commander.GetProperty("battleRating").GetUInt32());
        Assert.Equal(uint.MaxValue, commander.GetProperty("atrLevel").GetUInt32());
        Assert.Equal(int.MinValue, commander.GetProperty("vehicleDistanceTravelled").GetInt32());
        Assert.Equal(int.MaxValue, commander.GetProperty("notoriety").GetInt32());
        Assert.False(commander.GetProperty("markedWanted").GetBoolean());
        Assert.Equal(1, commander.EnumerateObject().Count(field => string.Equals(field.Name, "markedwanted", StringComparison.OrdinalIgnoreCase)));
        var ground = commander.GetProperty("provingGround");
        Assert.Equal(1u, ground.GetProperty("eliteRank").GetUInt32());
        Assert.Equal(2u, ground.GetProperty("rank").GetUInt32());
        Assert.Equal(3u, ground.GetProperty("rankXP").GetUInt32());
        Assert.Equal(uint.MaxValue, ground.GetProperty("totalXP").GetUInt32());
        Assert.Equal("1", commander.GetProperty("currentSystemKey").GetString());
        Assert.Equal(2ul, commander.GetProperty("currentSystem").GetUInt64());
        Assert.Equal(17u, commander.GetProperty("currentVesselId").GetUInt32());
        Assert.Equal(17u, commander.GetProperty("parentVesselId").GetUInt32());
        Assert.Equal(startup.RootElement.GetProperty("commander").GetProperty("location").GetString(), commander.GetProperty("location").GetString());
        Assert.Equal(startup.RootElement.GetProperty("vessels").GetRawText(), root.GetProperty("vessels").GetRawText());
        var version = root.GetProperty("gameVersion");
        Assert.Equal("private", version.GetProperty("name").GetString());
        Assert.Equal("4.4.1.1", version.GetProperty("semver").GetString());
        Assert.Equal(332_841u, version.GetProperty("version").GetUInt32());
        Assert.Equal(0u, version.GetProperty("dev").GetUInt32());
        Assert.False(root.TryGetProperty("gameIntegrationReady", out _));
        Assert.False(root.TryGetProperty("multicrew", out _));
    }

    [Fact]
    public void Genuine_empty_collections_have_each_verified_nested_shape_instead_of_omission()
    {
        using var document = JsonDocument.Parse(NativeFullSavegameDocument.SerializeComplete(Snapshot, Catalogue, Profile));
        var root = document.RootElement;
        foreach (var name in new[] { "suits", "weaponRack", "systemHyperspacePermits", "regionHyperspacePermits",
                     "landingPermits", "legacyFines", "legalStates", "taxiBookings" })
        {
            Assert.Equal(JsonValueKind.Array, root.GetProperty(name).ValueKind);
            Assert.Empty(root.GetProperty(name).EnumerateArray());
        }
        Assert.Empty(root.GetProperty("commander").GetProperty("currentCrew").EnumerateArray());
        Assert.Empty(root.GetProperty("commander").GetProperty("rank").EnumerateObject());
        foreach (var name in new[] { "constants", "pinned", "shipLocker" }) Assert.Empty(root.GetProperty(name).EnumerateObject());
        Assert.Equal(new[] { "inventory", "discovered" }, root.GetProperty("microresources").EnumerateObject().Select(field => field.Name));
        Assert.Empty(root.GetProperty("microresources").GetProperty("inventory").EnumerateObject());
        Assert.Empty(root.GetProperty("microresources").GetProperty("discovered").EnumerateObject());
        Assert.Equal(new[] { "objectives", "extraData" }, root.GetProperty("onboardingObjectives").EnumerateObject().Select(field => field.Name));
        Assert.Empty(root.GetProperty("onboardingObjectives").GetProperty("objectives").EnumerateObject());
        Assert.Empty(root.GetProperty("onboardingObjectives").GetProperty("extraData").EnumerateObject());
        Assert.Single(root.GetProperty("vessels").EnumerateArray()); // Never substitute an empty fleet to satisfy a top-level array gate.
    }

    [Fact]
    public void Both_explicit_empty_avatar_strings_are_emitted_without_an_invented_descriptor()
    {
        using var document = JsonDocument.Parse(NativeFullSavegameDocument.SerializeComplete(Snapshot, Catalogue, Profile));
        var commander = document.RootElement.GetProperty("commander");
        Assert.Equal("", commander.GetProperty("avatar").GetString());
        Assert.Equal("", commander.GetProperty("avatarXML").GetString());
        var opaque = Profile with { Avatar = "caller supplied opaque descriptor", AvatarXml = "<caller-data />", StartupState = 123 };
        using var explicitDocument = JsonDocument.Parse(NativeFullSavegameDocument.SerializeComplete(Snapshot, Catalogue, opaque));
        Assert.Equal(opaque.Avatar, explicitDocument.RootElement.GetProperty("commander").GetProperty("avatar").GetString());
        Assert.Equal(opaque.AvatarXml, explicitDocument.RootElement.GetProperty("commander").GetProperty("avatarXML").GetString());
        Assert.Equal(123u, explicitDocument.RootElement.GetProperty("commander").GetProperty("startupState").GetUInt32());
    }

    [Fact]
    public void Identity_and_selected_vessel_still_have_one_validated_snapshot_source()
    {
        var changed = Snapshot with { CommanderNativeId = ulong.MaxValue - 1, Credits = -12, Alive = true };
        using var document = JsonDocument.Parse(NativeFullSavegameDocument.SerializeComplete(changed, Catalogue, Profile));
        Assert.Equal(ulong.MaxValue - 1, document.RootElement.GetProperty("commander").GetProperty("id").GetUInt64());
        Assert.Equal(-12, document.RootElement.GetProperty("commander").GetProperty("credits").GetInt64());
        Assert.True(document.RootElement.GetProperty("commander").GetProperty("alive").GetBoolean());
        Assert.Throws<ArgumentException>(() => NativeFullSavegameDocument.SerializeComplete(Snapshot with { CurrentVesselId = 999 }, Catalogue, Profile));
        Assert.Throws<ArgumentException>(() => NativeFullSavegameDocument.SerializeComplete(Snapshot with { Vessels = [] }, Catalogue, Profile));
        Assert.Throws<ArgumentException>(() => NativeFullSavegameDocument.SerializeComplete(Snapshot with { CommanderNativeId = 0 }, Catalogue, Profile));
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("LoanCeiling")]
    [InlineData("Debt")]
    [InlineData("StartupState")]
    [InlineData("Gender")]
    [InlineData("ProvingGround")]
    [InlineData("ProvingGround.EliteRank")]
    [InlineData("ProvingGround.TotalXp")]
    [InlineData("BattleRating")]
    [InlineData("Avatar")]
    [InlineData("AvatarXml")]
    [InlineData("VehicleDistanceTravelled")]
    [InlineData("AtrLevel")]
    [InlineData("Notoriety")]
    [InlineData("MarkedWanted")]
    [InlineData("CurrentSystemKey")]
    [InlineData("CurrentSystem")]
    [InlineData("GameVersion")]
    [InlineData("GameVersion.Name")]
    [InlineData("GameVersion.Semver")]
    [InlineData("GameVersion.Version")]
    [InlineData("GameVersion.Dev")]
    [InlineData("EmptyCollections")]
    [InlineData("EmptyCollections.Rank")]
    [InlineData("EmptyCollections.Constants")]
    [InlineData("EmptyCollections.Pinned")]
    [InlineData("EmptyCollections.CurrentCrew")]
    [InlineData("EmptyCollections.Suits")]
    [InlineData("EmptyCollections.WeaponRack")]
    [InlineData("EmptyCollections.SystemHyperspacePermits")]
    [InlineData("EmptyCollections.RegionHyperspacePermits")]
    [InlineData("EmptyCollections.LandingPermits")]
    [InlineData("EmptyCollections.LegacyFines")]
    [InlineData("EmptyCollections.LegalStates")]
    [InlineData("EmptyCollections.TaxiBookings")]
    [InlineData("EmptyCollections.ShipLocker")]
    [InlineData("EmptyCollections.Microresources")]
    [InlineData("EmptyCollections.Microresources.Inventory")]
    [InlineData("EmptyCollections.Microresources.Discovered")]
    [InlineData("EmptyCollections.OnboardingObjectives")]
    [InlineData("EmptyCollections.OnboardingObjectives.Objectives")]
    [InlineData("EmptyCollections.OnboardingObjectives.ExtraData")]
    public void Every_profile_value_and_empty_declaration_must_be_explicitly_present(string path)
    {
        var node = JsonSerializer.SerializeToNode(Profile)!;
        var parts = path.Split('.');
        var parent = Parent(node, parts);
        Assert.True(parent.Remove(parts[^1]));
        Assert.Throws<JsonException>(() => node.Deserialize<NativeFullSavegameProfile>(StrictImportJson));
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("Gender")]
    [InlineData("ProvingGround")]
    [InlineData("Avatar")]
    [InlineData("AvatarXml")]
    [InlineData("GameVersion")]
    [InlineData("EmptyCollections")]
    [InlineData("EmptyCollections.Rank")]
    [InlineData("EmptyCollections.Microresources.Inventory")]
    [InlineData("EmptyCollections.OnboardingObjectives.ExtraData")]
    public void Explicit_null_cannot_turn_missing_profile_state_into_empty_output(string path)
    {
        var node = JsonSerializer.SerializeToNode(Profile)!;
        var parts = path.Split('.');
        Parent(node, parts)[parts[^1]] = null;
        var profile = node.Deserialize<NativeFullSavegameProfile>(StrictImportJson)!;
        Assert.Throws<ArgumentException>(() => NativeFullSavegameDocument.SerializeComplete(Snapshot, Catalogue, profile));
    }

    [Theory]
    [InlineData("MarkedWanted", "0")]
    [InlineData("StartupState", "-1")]
    [InlineData("StartupState", "4294967296")]
    [InlineData("VehicleDistanceTravelled", "2147483648")]
    [InlineData("LoanCeiling", "9223372036854775808")]
    [InlineData("CurrentSystem", "-1")]
    [InlineData("CurrentSystemKey", "1")]
    [InlineData("ProvingGround.EliteRank", "1.5")]
    [InlineData("Avatar", "{}")]
    [InlineData("GameVersion.Version", "\"332841\"")]
    public void Wrong_json_types_or_numeric_widths_are_rejected_on_typed_import(string path, string replacement)
    {
        var node = JsonSerializer.SerializeToNode(Profile)!;
        var parts = path.Split('.');
        Parent(node, parts)[parts[^1]] = JsonNode.Parse(replacement);
        Assert.Throws<JsonException>(() => node.Deserialize<NativeFullSavegameProfile>(StrictImportJson));
    }

    [Theory]
    [InlineData("Rank")]
    [InlineData("Constants")]
    [InlineData("Pinned")]
    [InlineData("CurrentCrew")]
    [InlineData("Suits")]
    [InlineData("ShipLocker")]
    [InlineData("Microresources.Inventory")]
    [InlineData("OnboardingObjectives.Objectives")]
    public void Unknown_nonempty_state_is_not_silently_erased_by_an_empty_marker(string relativePath)
    {
        var node = JsonSerializer.SerializeToNode(Profile)!;
        var parts = ("EmptyCollections." + relativePath).Split('.');
        Parent(node, parts)[parts[^1]] = JsonNode.Parse("{\"existing-owned-state\":7}");
        Assert.Throws<JsonException>(() => node.Deserialize<NativeFullSavegameProfile>(StrictImportJson));
    }

    [Fact]
    public void Explicit_full_profile_roundtrips_for_storage_without_becoming_wire_json()
    {
        var stored = JsonSerializer.Serialize(Profile);
        var restored = JsonSerializer.Deserialize<NativeFullSavegameProfile>(stored)!;
        Assert.Equal(NativeFullSavegameDocument.SerializeComplete(Snapshot, Catalogue, Profile),
            NativeFullSavegameDocument.SerializeComplete(Snapshot, Catalogue, restored));
        Assert.Contains("EmptyCollections", stored, StringComparison.Ordinal);
        using var wire = JsonDocument.Parse(NativeFullSavegameDocument.SerializeComplete(Snapshot, Catalogue, restored));
        Assert.False(wire.RootElement.TryGetProperty("EmptyCollections", out _));
    }

    [Theory]
    [InlineData("1", 2ul)]
    [InlineData("0", 1ul)]
    [InlineData("18446744073709551615", 2ul)]
    public void Canonical_system_key_is_a_string_and_only_the_exact_pair_guard_is_claimed(string key, ulong address)
    {
        using var document = JsonDocument.Parse(NativeFullSavegameDocument.SerializeComplete(Snapshot, Catalogue,
            Profile with { CurrentSystemKey = key, CurrentSystem = address }));
        Assert.Equal(key, document.RootElement.GetProperty("commander").GetProperty("currentSystemKey").GetString());
        Assert.Equal(address, document.RootElement.GetProperty("commander").GetProperty("currentSystem").GetUInt64());
    }

    [Theory]
    [InlineData("2", 2ul)]
    [InlineData("1", 17ul)]
    [InlineData("01", 2ul)]
    [InlineData(" 1", 2ul)]
    [InlineData("+1", 2ul)]
    [InlineData("1tail", 2ul)]
    [InlineData("0x1", 2ul)]
    [InlineData("", 2ul)]
    [InlineData("18446744073709551616", 2ul)]
    public void Invalid_or_address_copied_system_keys_are_rejected_before_native_consumption(string key, ulong address)
    {
        Assert.Throws<ArgumentException>(() => NativeFullSavegameDocument.SerializeComplete(Snapshot, Catalogue,
            Profile with { CurrentSystemKey = key, CurrentSystem = address }));
    }

    private static JsonObject Parent(JsonNode root, string[] parts)
    {
        var parent = root;
        foreach (var part in parts[..^1]) parent = parent[part]!;
        return (JsonObject)parent;
    }
}
