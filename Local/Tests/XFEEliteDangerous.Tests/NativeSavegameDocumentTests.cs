using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Client.Storage;
using XFEEliteDangerous.Core;
using Xunit;

namespace XFEEliteDangerous.Tests;

// Explicit synthetic catalogue/state for pure serialization checks only. Nothing is sent to the game or an API.
public sealed class NativeSavegameDocumentTests
{
    private static NativeLocationStringValue Position => new(42, new(1.25, -2.5, 3.75), new(.1, .2, -.3), true);
    private static NativeModuleSnapshot Module => new()
    {
        Id = 128064033, Name = "Int_Powerplant_Size2_Class1", Value = ulong.MaxValue,
        Excess = 17, On = true, Priority = 2, HealthMillion = 900_001
    };
    private static NativeSavegameCatalogue Catalogue => new()
    {
        Source = "explicit synthetic serialization fixture", PinnedVersion = "2026.09.03.332841", ValidationPending = true,
        Ships = [new("SideWinder", [new("PowerPlant", true), new("Slot01_Size2", false)], [new("FixedTestSlot", false)])],
        Modules = [new(Module.Id, Module.Name)]
    };
    private static NativeShipSnapshot Ship => new()
    {
        Id = 17, Name = "SideWinder", Alive = true, CockpitBreached = false, Excess = 123,
        OxygenRemainingMilliseconds = 300_000, RadarRangeSelection = 0, WeaponMode = 3,
        Fuel = new(2, 1.5f, .3f), Value = new(ulong.MaxValue, 100, 200),
        Health = new(123_456, 765_432, true, 1_000_000, 876_543), Location = Position,
        Modules = [new("PowerPlant", Module), new("Slot01_Size2", null)], FixedModules = [new("FixedTestSlot", null)],
        Firegroups = [new(0, [0, 1023], [1])], Capacitors = [new(0, 4, 500_000), new(1, 2, null)], Cargo = []
    };
    private static NativeSavegameSnapshot Snapshot => new()
    {
        CommanderNativeId = 4097, Credits = long.MaxValue, Alive = true, Location = Position,
        CurrentVesselId = 17, ParentVesselId = 17, Vessels = [Ship]
    };
    private static string Serialize(NativeShipSnapshot ship) => NativeSavegameDocument.Serialize(Snapshot with { Vessels = [ship] }, Catalogue);

    [Fact]
    public void Complete_state_has_exact_known_shapes_types_and_unscaled_wire_units()
    {
        using var document = JsonDocument.Parse(NativeSavegameDocument.Serialize(Snapshot, Catalogue));
        var root = document.RootElement;
        Assert.Equal(new[] { "commander", "vessels" }, root.EnumerateObject().Select(item => item.Name));
        var commander = root.GetProperty("commander");
        Assert.Equal(long.MaxValue, commander.GetProperty("credits").GetInt64());
        Assert.Equal(17u, commander.GetProperty("currentVesselId").GetUInt32());
        Assert.Equal(17u, commander.GetProperty("parentVesselId").GetUInt32());
        Assert.True(commander.GetProperty("alive").GetBoolean());
        Assert.Equal(Position, NativeLocationStringCodec.Parse(commander.GetProperty("location").GetString()!));
        Assert.False(commander.TryGetProperty("id", out _)); // Binding metadata has no proven commander.id GET-reader field.
        var ship = root.GetProperty("vessels")[0];
        Assert.Equal("ship", ship.GetProperty("type").GetString());
        foreach (var field in new[] { "alive", "cockpitBreached", "excess", "id", "name", "oxygenRemaining", "radarRangeSelection", "weaponMode",
                     "fuel", "reserve", "value", "health", "firegroups", "capacitors", "location", "modules", "fixedModules", "cargo" })
            Assert.NotEqual(JsonValueKind.Null, ship.GetProperty(field).ValueKind);
        Assert.Equal(300_000u, ship.GetProperty("oxygenRemaining").GetUInt32());
        Assert.Equal(123_456u, ship.GetProperty("health").GetProperty("hull").GetUInt32());
        Assert.Equal(765_432u, ship.GetProperty("health").GetProperty("shield").GetUInt32());
        Assert.Equal(876_543u, ship.GetProperty("health").GetProperty("paintwork").GetUInt32());
        Assert.True(ship.GetProperty("health").GetProperty("shieldup").GetBoolean());
        Assert.Equal(2f, ship.GetProperty("fuel").GetProperty("capacity").GetSingle());
        Assert.Equal(1.5f, ship.GetProperty("fuel").GetProperty("lvl").GetSingle());
        Assert.Equal(.3f, ship.GetProperty("reserve").GetProperty("lvl").GetSingle());
        Assert.Equal(ulong.MaxValue, ship.GetProperty("value").GetProperty("unloaned").GetUInt64());
        Assert.Equal(0u, ship.GetProperty("radarRangeSelection").GetUInt32());
        Assert.Equal(3u, ship.GetProperty("weaponMode").GetUInt32());
        Assert.Equal("ua", ship.GetProperty("location").GetProperty("type").GetString());
        Assert.Equal(Position, NativeLocationStringCodec.Parse(ship.GetProperty("location").GetProperty("ua").GetString()!));
        Assert.Empty(ship.GetProperty("cargo").EnumerateArray());
        Assert.DoesNotContain("ValidationPending", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PinnedVersion", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthetic", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Module_containers_are_separate_and_empty_slots_have_no_fabricated_module()
    {
        var suppliedModule = Module with { Default = new(Module.Id, Module.Name, uint.MaxValue), Uid = 0,
            Boost = 1, DamageOverride = 8, Free = false, Ammo = new(7, 42), Class = 1 };
        using var document = JsonDocument.Parse(Serialize(Ship with
        { Modules = [new("PowerPlant", suppliedModule), new("Slot01_Size2", null)], FixedModules = [new("FixedTestSlot", suppliedModule)] }));
        var ship = document.RootElement.GetProperty("vessels")[0];
        var modules = ship.GetProperty("modules");
        Assert.Equal(new[] { "PowerPlant", "Slot01_Size2" }, modules.EnumerateObject().Select(item => item.Name));
        Assert.Empty(modules.GetProperty("Slot01_Size2").EnumerateObject());
        var module = modules.GetProperty("PowerPlant").GetProperty("module");
        Assert.Equal(Module.Id, module.GetProperty("id").GetUInt32());
        Assert.Equal(ulong.MaxValue, module.GetProperty("value").GetUInt64());
        Assert.Equal(900_001u, module.GetProperty("health").GetUInt32());
        Assert.Equal(17ul, module.GetProperty("excess").GetUInt64());
        Assert.Equal(uint.MaxValue, module.GetProperty("default").GetProperty("cost").GetUInt32());
        Assert.Equal(0ul, module.GetProperty("uid").GetUInt64()); // Preserve caller wire; no silent sentinel normalization.
        Assert.False(module.GetProperty("free").GetBoolean());
        Assert.Equal(42u, module.GetProperty("ammo").GetProperty("hopper").GetUInt32());
        Assert.Equal(Module.Name, ship.GetProperty("fixedModules").GetProperty("FixedTestSlot").GetProperty("module").GetProperty("name").GetString());
    }

    [Fact]
    public void Numeric_container_keys_keep_numeric_insertion_order_and_get_arrays()
    {
        var ship = Ship with
        {
            Firegroups = Enumerable.Range(0, 12).Select(index => new NativeFiregroupSnapshot((uint)index, [(uint)index], [])).ToArray(),
            Capacitors = Enumerable.Range(0, 12).Select(index => new NativeCapacitorSnapshot((uint)index, (uint)index, index == 1 ? null : (uint)index)).ToArray()
        };
        using var document = JsonDocument.Parse(Serialize(ship));
        var output = document.RootElement.GetProperty("vessels")[0];
        var numericOrder = Enumerable.Range(0, 12).Select(index => index.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(numericOrder, output.GetProperty("firegroups").EnumerateObject().Select(item => item.Name));
        Assert.Equal(numericOrder, output.GetProperty("capacitors").EnumerateObject().Select(item => item.Name));
        Assert.Equal(JsonValueKind.Array, output.GetProperty("firegroups").GetProperty("10").GetProperty("primary").ValueKind);
        Assert.Equal(10u, output.GetProperty("firegroups").GetProperty("10").GetProperty("primary")[0].GetUInt32());
        Assert.False(output.GetProperty("capacitors").GetProperty("1").TryGetProperty("energy", out _));
        Assert.Equal(0u, output.GetProperty("capacitors").GetProperty("0").GetProperty("energy").GetUInt32());
    }

    [Theory]
    [InlineData("missing-slot")]
    [InlineData("wrong-order")]
    [InlineData("wrong-case")]
    [InlineData("required-empty")]
    [InlineData("unknown-id")]
    [InlineData("mismatched-name")]
    [InlineData("unknown-default")]
    [InlineData("fixed-unknown")]
    public void Catalogue_is_explicit_and_does_not_repair_or_invent_equipment(string invalid)
    {
        var ship = invalid switch
        {
            "missing-slot" => Ship with { Modules = [new("PowerPlant", Module)] },
            "wrong-order" => Ship with { Modules = [new("Slot01_Size2", null), new("PowerPlant", Module)] },
            "wrong-case" => Ship with { Modules = [new("powerplant", Module), new("Slot01_Size2", null)] },
            "required-empty" => Ship with { Modules = [new("PowerPlant", null), new("Slot01_Size2", null)] },
            "unknown-id" => Ship with { Modules = [new("PowerPlant", Module with { Id = 999 }), new("Slot01_Size2", null)] },
            "mismatched-name" => Ship with { Modules = [new("PowerPlant", Module with { Name = "Other" }), new("Slot01_Size2", null)] },
            "unknown-default" => Ship with { Modules = [new("PowerPlant", Module with { Default = new(999, "Other", 0) }), new("Slot01_Size2", null)] },
            "fixed-unknown" => Ship with { FixedModules = [new("CargoHatch", Module)] },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<ArgumentException>(() => Serialize(ship));
    }

    [Theory]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-name")]
    [InlineData("duplicate-ship")]
    [InlineData("duplicate-slot")]
    [InlineData("no-source")]
    [InlineData("no-version")]
    public void Catalogue_ambiguity_or_missing_provenance_is_rejected(string invalid)
    {
        var catalogue = invalid switch
        {
            "duplicate-id" => Catalogue with { Modules = [new(Module.Id, Module.Name), new(Module.Id, "Other")] },
            "duplicate-name" => Catalogue with { Modules = [new(Module.Id, Module.Name), new(123, Module.Name)] },
            "duplicate-ship" => Catalogue with { Ships = [Catalogue.Ships[0], Catalogue.Ships[0]] },
            "duplicate-slot" => Catalogue with { Ships = [new("SideWinder", [new("PowerPlant", true), new("PowerPlant", false)], [])] },
            "no-source" => Catalogue with { Source = "" },
            "no-version" => Catalogue with { PinnedVersion = "" },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<ArgumentException>(() => NativeSavegameDocument.Serialize(Snapshot, catalogue));
    }

    [Fact]
    public void Distinct_explicit_fixed_resource_defaults_can_share_zero_without_becoming_catalogue_identities()
    {
        var catalogue = Catalogue with
        {
            Modules = [new(Module.Id, Module.Name), new(0, "FixedDefaultA"), new(0, "FixedDefaultB")],
            Ships = [Catalogue.Ships[0] with { FixedModules = [new("FixedA", true), new("FixedB", true)] }]
        };
        var ship = Ship with
        {
            FixedModules = [new("FixedA", Module with { Id = 0, Name = "FixedDefaultA", Value = 0 }),
                new("FixedB", Module with { Id = 0, Name = "FixedDefaultB", Value = 0, Default = new(0, "FixedDefaultA", 0) })]
        };
        using var document = JsonDocument.Parse(NativeSavegameDocument.Serialize(Snapshot with { Vessels = [ship] }, catalogue));
        var fixedModules = document.RootElement.GetProperty("vessels")[0].GetProperty("fixedModules");
        Assert.Equal(0u, fixedModules.GetProperty("FixedA").GetProperty("module").GetProperty("id").GetUInt32());
        Assert.Equal("FixedDefaultB", fixedModules.GetProperty("FixedB").GetProperty("module").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("ordinary-zero")]
    [InlineData("ordinary-default-zero")]
    [InlineData("wrong-fixed-zero-name")]
    [InlineData("wrong-fixed-nonzero-id")]
    public void Zero_resource_default_ids_require_explicit_slot_and_catalogue_name_bindings(string invalid)
    {
        var catalogue = Catalogue with { Modules = [new(Module.Id, Module.Name), new(0, "FixedDefaultA")] };
        var ship = invalid switch
        {
            "ordinary-zero" => Ship with { Modules = [new("PowerPlant", Module with { Id = 0, Name = "FixedDefaultA" }), new("Slot01_Size2", null)] },
            "ordinary-default-zero" => Ship with { Modules = [new("PowerPlant", Module with { Default = new(0, "FixedDefaultA", 0) }), new("Slot01_Size2", null)] },
            "wrong-fixed-zero-name" => Ship with { FixedModules = [new("FixedTestSlot", Module with { Id = 0, Name = "OtherFixedDefault" })] },
            "wrong-fixed-nonzero-id" => Ship with { FixedModules = [new("FixedTestSlot", Module with { Name = "FixedDefaultA" })] },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<ArgumentException>(() => NativeSavegameDocument.Serialize(Snapshot with { Vessels = [ship] }, catalogue));
    }

    [Fact] public void Ordinary_native_resource_default_must_match_its_declared_slot_and_catalogue()
    {
        var definition = new NativeSlotDefinition("VesselVoice",true,"VoicePack_Verity");
        var catalogue = Catalogue with
        {
            Modules = [.. Catalogue.Modules,new(0,"VoicePack_Verity")],
            Ships = [Catalogue.Ships[0] with { Modules = [.. Catalogue.Ships[0].Modules,definition] }]
        };
        var ship = Ship with { Modules = [.. Ship.Modules,new("VesselVoice",Module with {Id=0,Name="VoicePack_Verity"})] };
        var snapshot = Snapshot with {Vessels=[ship]};
        using var document = JsonDocument.Parse(NativeSavegameDocument.Serialize(snapshot,catalogue));
        Assert.Equal("VoicePack_Verity",document.RootElement.GetProperty("vessels")[0].GetProperty("modules").GetProperty("VesselVoice").GetProperty("module").GetProperty("name").GetString());
        foreach(var invalid in new[]{definition with{ResourceDefaultName=null},definition with{ResourceDefaultName="OtherResource"}})
            Assert.Throws<ArgumentException>(()=>NativeSavegameDocument.Serialize(snapshot,catalogue with
                {Ships=[catalogue.Ships[0] with{Modules=[..Catalogue.Ships[0].Modules,invalid]}]}));
        Assert.Throws<ArgumentException>(()=>NativeSavegameDocument.Serialize(snapshot,catalogue with{Modules=Catalogue.Modules}));
        var reloaded=JsonSerializer.Deserialize<NativeSavegameCatalogue>(JsonSerializer.Serialize(catalogue,Wire.Json),Wire.Json)!;
        Assert.Equal("VoicePack_Verity",reloaded.Ships[0].Modules[^1].ResourceDefaultName);
    }

    [Theory]
    [InlineData("current-missing")]
    [InlineData("parent-missing")]
    [InlineData("sentinel-current")]
    [InlineData("sentinel-ship")]
    [InlineData("duplicate-ship")]
    [InlineData("empty-fleet")]
    [InlineData("unknown-type")]
    [InlineData("parent-ship-missing")]
    public void Vessel_identity_selection_must_resolve_without_fabricated_fallback(string invalid)
    {
        var snapshot = invalid switch
        {
            "current-missing" => Snapshot with { CurrentVesselId = 18 },
            "parent-missing" => Snapshot with { ParentVesselId = 18 },
            "sentinel-current" => Snapshot with { CurrentVesselId = uint.MaxValue },
            "sentinel-ship" => Snapshot with { Vessels = [Ship with { Id = uint.MaxValue }] },
            "duplicate-ship" => Snapshot with { Vessels = [Ship, Ship] },
            "empty-fleet" => Snapshot with { Vessels = [] },
            "unknown-type" => Snapshot with { Vessels = [Ship with { Name = "other" }] },
            "parent-ship-missing" => Snapshot with { Vessels = [Ship with { ParentShipId = 18 }] },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<ArgumentException>(() => NativeSavegameDocument.Serialize(snapshot, Catalogue));
    }

    [Fact]
    public void Zero_vessel_id_is_not_confused_with_the_minus_one_native_sentinel()
    {
        var snapshot = Snapshot with { CurrentVesselId = 0, ParentVesselId = 0, Vessels = [Ship with { Id = 0 }] };
        using var document = JsonDocument.Parse(NativeSavegameDocument.Serialize(snapshot, Catalogue));
        Assert.Equal(0u, document.RootElement.GetProperty("vessels")[0].GetProperty("id").GetUInt32());
    }

    [Fact]
    public void Explicit_empty_optional_containers_are_distinct_from_missing_required_state()
    {
        var definition = new NativeShipDefinition("SideWinder", [], []);
        var ship = Ship with { Modules = [], FixedModules = [], Firegroups = [], Capacitors = [], Cargo = [] };
        using var document = JsonDocument.Parse(NativeSavegameDocument.Serialize(Snapshot with { Vessels = [ship] },
            Catalogue with { Ships = [definition], Modules = [] }));
        var output = document.RootElement.GetProperty("vessels")[0];
        Assert.Empty(output.GetProperty("modules").EnumerateObject());
        Assert.Empty(output.GetProperty("capacitors").EnumerateObject());
        Assert.Throws<ArgumentException>(() => Serialize(Ship with { Fuel = null! }));
        Assert.Throws<ArgumentException>(() => Serialize(Ship with { Health = null! }));
        Assert.Throws<ArgumentException>(() => Serialize(Ship with { Value = null! }));
        Assert.Throws<ArgumentException>(() => Serialize(Ship with { Modules = null! }));
        Assert.Throws<ArgumentException>(() => Serialize(Ship with { FixedModules = null! }));
        Assert.Throws<ArgumentException>(() => Serialize(Ship with { Firegroups = null! }));
        Assert.Throws<ArgumentException>(() => Serialize(Ship with { Capacitors = null! }));
        Assert.Throws<ArgumentException>(() => Serialize(Ship with { Cargo = null! }));
    }

    [Theory]
    [InlineData("CommanderNativeId")]
    [InlineData("Credits")]
    [InlineData("Alive")]
    [InlineData("Location")]
    [InlineData("CurrentVesselId")]
    [InlineData("ParentVesselId")]
    [InlineData("Vessels")]
    [InlineData("Vessels.0.Fuel")]
    [InlineData("Vessels.0.Fuel.Capacity")]
    [InlineData("Vessels.0.Fuel.ReserveLevel")]
    [InlineData("Vessels.0.Health.ShieldUp")]
    [InlineData("Vessels.0.OxygenRemainingMilliseconds")]
    [InlineData("Vessels.0.Modules.0.Module.HealthMillion")]
    [InlineData("Vessels.0.Modules.1.Module")]
    [InlineData("Vessels.0.Capacitors.1.EnergyMillion")]
    public void Missing_required_input_is_not_silently_deserialized_as_zero_or_empty(string path)
    {
        var node = JsonSerializer.SerializeToNode(Snapshot)!;
        var parts = path.Split('.');
        JsonNode parent = node;
        foreach (var part in parts[..^1]) parent = parent is JsonArray array ? array[int.Parse(part, CultureInfo.InvariantCulture)]! : parent[part]!;
        Assert.True(((JsonObject)parent).Remove(parts[^1]));
        Assert.Throws<JsonException>(() => node.Deserialize<NativeSavegameSnapshot>());
    }

    [Theory]
    [InlineData("fuel-capacity")]
    [InlineData("fuel-level")]
    [InlineData("reserve")]
    [InlineData("location-position")]
    [InlineData("location-rotation")]
    [InlineData("zero-address")]
    public void Nonfinite_or_invalid_native_location_inputs_fail_before_output(string invalid)
    {
        var ship = invalid switch
        {
            "fuel-capacity" => Ship with { Fuel = new(float.NaN, 1, 1) },
            "fuel-level" => Ship with { Fuel = new(1, float.PositiveInfinity, 1) },
            "reserve" => Ship with { Fuel = new(1, 1, float.NegativeInfinity) },
            "location-position" => Ship with { Location = Position with { Position = new(double.NaN, 0, 0) } },
            "location-rotation" => Ship with { Location = Position with { RotationVector = new(0, double.PositiveInfinity, 0) } },
            "zero-address" => Ship with { Location = Position with { Address = 0 } },
            _ => throw new InvalidOperationException()
        };
        Assert.ThrowsAny<ArgumentException>(() => Serialize(ship));
    }

    [Theory]
    [InlineData("firegroup-gap")]
    [InlineData("firegroup-index")]
    [InlineData("capacitor-gap")]
    public void Index_order_and_native_firegroup_bitset_bound_are_validated(string invalid)
    {
        var ship = invalid switch
        {
            "firegroup-gap" => Ship with { Firegroups = [new(1, [], [])] },
            "firegroup-index" => Ship with { Firegroups = [new(0, [1024], [])] },
            "capacitor-gap" => Ship with { Capacitors = [new(0, 4, null), new(2, 4, null)] },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<ArgumentException>(() => Serialize(ship));
    }

    [Theory]
    [InlineData(3u, 100L)]
    [InlineData(3u, -100L)]
    [InlineData(1u, 2147483647L)]
    [InlineData(1u, -2147483647L)]
    [InlineData(0u, -9223372036854775808L)]
    public void Cargo_preserves_explicit_owner_total_value_and_optional_fields(uint quantity, long totalValue)
    {
        var cargo = new NativeCargoSnapshot { Commodity = "gold", Owner = ulong.MaxValue, Quantity = quantity, TotalValue = totalValue,
            Mission = 0, PowerplayOrigin = 42, Marked = 0, Origin = 2, OriginSystem = ulong.MaxValue };
        using var document = JsonDocument.Parse(Serialize(Ship with { Cargo = [cargo] }));
        var output = document.RootElement.GetProperty("vessels")[0].GetProperty("cargo")[0];
        Assert.Equal(new[] { "commodity", "owner", "qty", "value", "mission", "powerplayOrigin", "marked", "origin", "originSystem" },
            output.EnumerateObject().Select(item => item.Name));
        Assert.Equal(ulong.MaxValue, output.GetProperty("owner").GetUInt64());
        Assert.Equal(quantity, output.GetProperty("qty").GetUInt32());
        Assert.Equal(totalValue, output.GetProperty("value").GetInt64());
        Assert.False(output.TryGetProperty("unitValue", out _));
    }

    [Theory]
    [InlineData(65536u, 0L)]
    [InlineData(1u, 2147483648L)]
    [InlineData(1u, -2147483648L)]
    [InlineData(1u, -9223372036854775808L)]
    [InlineData(65535u, 9223372036854775807L)]
    public void Cargo_rejects_quantity_and_signed_expansion_overflow(uint quantity, long value)
    {
        var cargo = new NativeCargoSnapshot { Commodity = "gold", Owner = 42, Quantity = quantity, TotalValue = value };
        Assert.Throws<ArgumentException>(() => Serialize(Ship with { Cargo = [cargo] }));
    }

    [Fact]
    public void Culture_does_not_change_native_location_or_numeric_container_order()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var first = NativeSavegameDocument.Serialize(Snapshot, Catalogue);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal(first, NativeSavegameDocument.Serialize(Snapshot, Catalogue));
            Assert.Contains("42,1.25,-2.5,3.75,0.1,0.2,-0.3,1", first, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
