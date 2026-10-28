using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XFEEliteDangerous.Client.Storage;

namespace XFEEliteDangerous.Core;

/// <summary>Caller-owned identity metadata and an explicit catalogue, not evidence of native acceptance.</summary>
public sealed record NativeSavegameCatalogue
{
    public required string Source { get; init; }
    public required string PinnedVersion { get; init; }
    public required bool ValidationPending { get; init; }
    public required IReadOnlyList<NativeShipDefinition> Ships { get; init; }
    public required IReadOnlyList<NativeModuleDefinition> Modules { get; init; }
}

/// <summary>The supplied order is native module-index order. Empty optional slots must still be present.</summary>
public sealed record NativeShipDefinition([property: JsonRequired] string Name,
    [property: JsonRequired] IReadOnlyList<NativeSlotDefinition> Modules,
    [property: JsonRequired] IReadOnlyList<NativeSlotDefinition> FixedModules);
public sealed record NativeSlotDefinition([property: JsonRequired] string Name, [property: JsonRequired] bool RequiredModule,
    string? ResourceDefaultName = null);
/// <summary>Nonzero catalogue identity, or explicit slot-bound resource-default ID zero; zero is not an official catalogue ID.</summary>
public sealed record NativeModuleDefinition([property: JsonRequired] uint Id, [property: JsonRequired] string Name);

/// <summary>A complete, explicitly supplied local state. Its native identity is validated but is not an invented JSON field.</summary>
public sealed record NativeSavegameSnapshot
{
    public required ulong CommanderNativeId { get; init; }
    public required long Credits { get; init; }
    public required bool Alive { get; init; }
    public required NativeLocationStringValue Location { get; init; }
    public required uint CurrentVesselId { get; init; }
    public required uint ParentVesselId { get; init; }
    public required IReadOnlyList<NativeShipSnapshot> Vessels { get; init; }
}

public sealed record NativeShipSnapshot
{
    public required uint Id { get; init; }
    public required string Name { get; init; }
    public required bool Alive { get; init; }
    public required bool CockpitBreached { get; init; }
    public required ulong Excess { get; init; }
    public required uint OxygenRemainingMilliseconds { get; init; }
    public required uint RadarRangeSelection { get; init; }
    public required uint WeaponMode { get; init; }
    public required NativeShipFuel Fuel { get; init; }
    public required NativeShipValues Value { get; init; }
    public required NativeShipHealth Health { get; init; }
    public required NativeLocationStringValue Location { get; init; }
    public required IReadOnlyList<NativeSlotSnapshot> Modules { get; init; }
    public required IReadOnlyList<NativeSlotSnapshot> FixedModules { get; init; }
    public required IReadOnlyList<NativeFiregroupSnapshot> Firegroups { get; init; }
    public required IReadOnlyList<NativeCapacitorSnapshot> Capacitors { get; init; }
    public required IReadOnlyList<NativeCargoSnapshot> Cargo { get; init; }
    public uint? ParentShipId { get; init; }
    /// <summary>Explicit previous UA location used by the full fleet reader; never inferred from current position.</summary>
    public NativeLocationStringValue? LastLocation { get; init; }
}

/// <summary>All fuel values are direct native floats, without inferred ratios, tank capacity or reserve defaults.</summary>
public sealed record NativeShipFuel([property: JsonRequired] float Capacity, [property: JsonRequired] float Level,
    [property: JsonRequired] float ReserveLevel);
public sealed record NativeShipValues([property: JsonRequired] ulong Unloaned, [property: JsonRequired] ulong Hull,
    [property: JsonRequired] ulong Modules);
/// <summary>Million-scale UInt32 wire values. The writer does not invert hull/shield or rescale any field.</summary>
public sealed record NativeShipHealth([property: JsonRequired] uint HullMillion, [property: JsonRequired] uint ShieldMillion,
    [property: JsonRequired] bool ShieldUp, [property: JsonRequired] uint IntegrityMillion, [property: JsonRequired] uint PaintworkMillion);
public sealed record NativeSlotSnapshot([property: JsonRequired] string Name, [property: JsonRequired] NativeModuleSnapshot? Module);
public sealed record NativeModuleDefaultSnapshot([property: JsonRequired] uint Id, [property: JsonRequired] string Name,
    [property: JsonRequired] uint Cost);
public sealed record NativeModuleAmmo([property: JsonRequired] uint Clip, [property: JsonRequired] uint Hopper);

public sealed record NativeModuleSnapshot
{
    public required uint Id { get; init; }
    public required string Name { get; init; }
    public required ulong Value { get; init; }
    public required ulong Excess { get; init; }
    public required bool On { get; init; }
    public required uint Priority { get; init; }
    public required uint HealthMillion { get; init; }
    public NativeModuleDefaultSnapshot? Default { get; init; }
    public ulong? Uid { get; init; }
    public uint? Boost { get; init; }
    public uint? DamageOverride { get; init; }
    public bool? Free { get; init; }
    public NativeModuleAmmo? Ammo { get; init; }
    public uint? Class { get; init; }
    public NativeModuleEngineering? Engineering { get; init; }
}

public sealed record NativeFiregroupSnapshot([property: JsonRequired] uint Index,
    [property: JsonRequired] IReadOnlyList<uint> Primary, [property: JsonRequired] IReadOnlyList<uint> Secondary);
/// <summary>Energy is optional. Absence is kept absent instead of fabricating the native reader's fallback value.</summary>
public sealed record NativeCapacitorSnapshot([property: JsonRequired] uint Index, [property: JsonRequired] uint Pips,
    [property: JsonRequired] uint? EnergyMillion);

/// <summary>Value is a signed total for this stack, not the Journal's unit price.</summary>
public sealed record NativeCargoSnapshot
{
    public required string Commodity { get; init; }
    public required ulong Owner { get; init; }
    public required uint Quantity { get; init; }
    public required long TotalValue { get; init; }
    public ulong? Mission { get; init; }
    public ulong? PowerplayOrigin { get; init; }
    public uint? Marked { get; init; }
    public ulong? Origin { get; init; }
    public ulong? OriginSystem { get; init; }
}

/// <summary>
/// Writes the closed, verified subset of GET commander/savegame JSON. It neither creates a ship/world
/// nor asserts that a catalogue, location or produced document has been accepted by the game.
/// This startup subset does not satisfy the separate ordinary full reader; that composition requires
/// NativeFullSavegameDocument.SerializeComplete and its explicit profile/empty-collection declarations.
/// See docs/protocol-evidence/native-savegame-*.json. No network, native calls or disk writes occur.
/// </summary>
public static class NativeSavegameDocument
{
    // Bounded local input policy, not inferred native gameplay limits.
    private const int MaximumVessels = 128, MaximumSlots = 512, MaximumGroups = 1024,
        MaximumCatalogueEntries = 16_384, MaximumCargoStacks = 1024;

    public static string Serialize(NativeSavegameSnapshot snapshot, NativeSavegameCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalogue);
        Validate(snapshot, catalogue);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("commander");
            writer.WriteNumber("credits", snapshot.Credits);
            writer.WriteString("location", Location(snapshot.Location));
            writer.WriteBoolean("alive", snapshot.Alive);
            writer.WriteNumber("currentVesselId", snapshot.CurrentVesselId);
            writer.WriteNumber("parentVesselId", snapshot.ParentVesselId);
            writer.WriteEndObject();
            writer.WriteStartArray("vessels");
            foreach (var vessel in snapshot.Vessels) WriteShip(writer, vessel);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static void Validate(NativeSavegameSnapshot snapshot, NativeSavegameCatalogue catalogue)
    {
        Text(catalogue.Source, "Catalogue source", 512);
        Text(catalogue.PinnedVersion, "Catalogue pinned version", 128);
        if (snapshot.CommanderNativeId is 0 or ulong.MaxValue)
            throw new ArgumentException("A concrete private commander identity is required.", nameof(snapshot));
        VesselId(snapshot.CurrentVesselId);
        VesselId(snapshot.ParentVesselId);
        _ = Location(snapshot.Location);
        List(catalogue.Ships, MaximumCatalogueEntries, "Catalogue ships", allowEmpty: false);
        List(catalogue.Modules, MaximumCatalogueEntries, "Catalogue modules");
        var ships = new Dictionary<string, NativeShipDefinition>(StringComparer.Ordinal);
        var modules = new Dictionary<string, NativeModuleDefinition>(StringComparer.Ordinal);
        var nonzeroModuleIds = new HashSet<uint>();
        foreach (var definition in catalogue.Ships)
        {
            NotNull(definition, "Ship definition");
            Text(definition.Name, "Ship definition name");
            if (!ships.TryAdd(definition.Name, definition)) Invalid("Duplicate ship definition.");
            SlotDefinitions(definition.Modules);
            SlotDefinitions(definition.FixedModules);
        }
        foreach (var definition in catalogue.Modules)
        {
            NotNull(definition, "Module definition");
            Text(definition.Name, "Module definition name");
            if (!modules.TryAdd(definition.Name, definition) || (definition.Id != 0 && !nonzeroModuleIds.Add(definition.Id)))
                Invalid("Module definitions require unique names and unique nonzero catalogue IDs.");
        }
        List(snapshot.Vessels, MaximumVessels, "Vessels", allowEmpty: false);
        var vesselIds = new HashSet<uint>();
        foreach (var ship in snapshot.Vessels)
        {
            NotNull(ship, "Vessel");
            VesselId(ship.Id);
            if (!vesselIds.Add(ship.Id)) Invalid("Duplicate vessel ID.");
            Text(ship.Name, "Ship name");
            if (!ships.TryGetValue(ship.Name, out var definition)) Invalid("Ship name is absent from the supplied catalogue.");
            NotNull(ship.Fuel, "Fuel"); NotNull(ship.Value, "Value"); NotNull(ship.Health, "Health");
            if (!float.IsFinite(ship.Fuel.Capacity) || !float.IsFinite(ship.Fuel.Level) || !float.IsFinite(ship.Fuel.ReserveLevel))
                Invalid("Fuel values must be finite native floats.");
            _ = Location(ship.Location);
            if (ship.ParentShipId is { } parent) VesselId(parent);
            Slots(ship.Modules, definition!.Modules, modules, fixedModule: false);
            Slots(ship.FixedModules, definition.FixedModules, modules, fixedModule: true);
            List(ship.Firegroups, MaximumGroups, "Firegroups");
            for (var index = 0; index < ship.Firegroups.Count; index++)
            {
                var group = ship.Firegroups[index]; NotNull(group, "Firegroup");
                if (group.Index != (uint)index) Invalid("Firegroups must use contiguous numeric index order starting at zero.");
                FiregroupIndices(group.Primary); FiregroupIndices(group.Secondary);
            }
            List(ship.Capacitors, MaximumGroups, "Capacitors");
            for (var index = 0; index < ship.Capacitors.Count; index++)
            {
                var capacitor = ship.Capacitors[index]; NotNull(capacitor, "Capacitor");
                if (capacitor.Index != (uint)index) Invalid("Capacitors must use contiguous numeric index order starting at zero.");
            }
            List(ship.Cargo, MaximumCargoStacks, "Cargo");
            foreach (var cargo in ship.Cargo)
            {
                NotNull(cargo, "Cargo entry"); Text(cargo.Commodity, "Commodity");
                if (cargo.Quantity > ushort.MaxValue) Invalid("Cargo quantity exceeds the native UInt16 expansion bound.");
                // Native signed division truncates toward zero. Compare the quotient without Math.Abs(Int64.MinValue).
                if (cargo.Quantity != 0)
                {
                    var unitValue = cargo.TotalValue / cargo.Quantity;
                    if (unitValue is < -int.MaxValue or > int.MaxValue)
                        Invalid("Cargo total value divided by quantity exceeds the native signed unit-value bound.");
                }
            }
        }
        if (!vesselIds.Contains(snapshot.CurrentVesselId) || !vesselIds.Contains(snapshot.ParentVesselId))
            Invalid("Current and parent vessel IDs must resolve to supplied ships.");
        if (snapshot.Vessels.Any(ship => ship.ParentShipId is { } parent && !vesselIds.Contains(parent)))
            Invalid("Parent ship ID must resolve to a supplied ship.");
    }

    private static void SlotDefinitions(IReadOnlyList<NativeSlotDefinition> slots)
    {
        List(slots, MaximumSlots, "Slot definitions");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            NotNull(slot, "Slot definition"); Text(slot.Name, "Slot definition name");
            if(slot.ResourceDefaultName is not null) Text(slot.ResourceDefaultName,"Slot resource default name");
            if (!names.Add(slot.Name)) Invalid("Duplicate slot definition.");
        }
    }

    private static void Slots(IReadOnlyList<NativeSlotSnapshot> slots, IReadOnlyList<NativeSlotDefinition> definitions,
        Dictionary<string, NativeModuleDefinition> modules, bool fixedModule)
    {
        List(slots, MaximumSlots, "Slots");
        if (slots.Count != definitions.Count) Invalid("Every canonical slot must be explicitly supplied, including empty slots.");
        for (var index = 0; index < slots.Count; index++)
        {
            var slot = slots[index]; NotNull(slot, "Slot"); var definition = definitions[index];
            if (!string.Equals(slot.Name, definition.Name, StringComparison.Ordinal))
                Invalid("Slots must match the supplied canonical names and order exactly.");
            if (slot.Module is not { } module)
            {
                if (definition.RequiredModule) Invalid("A required canonical slot cannot be empty.");
                continue;
            }
            ModuleIdentity(module.Id, module.Name, modules, fixedModule || definition.ResourceDefaultName == module.Name);
            if (module.Default is { } defaultModule) ModuleIdentity(defaultModule.Id, defaultModule.Name, modules,
                fixedModule || definition.ResourceDefaultName == defaultModule.Name);
            NativeEngineering.Validate(module.Engineering);
        }
    }

    private static void ModuleIdentity(uint id, string name, Dictionary<string, NativeModuleDefinition> modules, bool resourceDefault)
    {
        Text(name, "Module name");
        if (id == 0 && !resourceDefault) Invalid("Resource-default ID zero requires an explicitly bound native slot default.");
        if (!modules.TryGetValue(name, out var definition) || id != definition.Id)
            Invalid("Module ID and name must match one explicit supplied catalogue definition.");
    }

    private static void FiregroupIndices(IReadOnlyList<uint> indices)
    {
        List(indices, 1024, "Firegroup indices");
        if (indices.Any(index => index >= 1024)) Invalid("Firegroup weapon indices must be below 1024.");
    }

    private static string Location(NativeLocationStringValue value)
    {
        if (value.Address == 0) Invalid("Native location address must be nonzero; its world existence is not asserted.");
        var text = NativeLocationStringCodec.Format(value);
        if (!NativeLocationStringCodec.TryParse(text, out var parsed) || parsed != value)
            Invalid("Native location must round-trip through the supported eight-field codec.");
        return text;
    }

    private static void WriteShip(Utf8JsonWriter writer, NativeShipSnapshot ship)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "ship"); writer.WriteNumber("id", ship.Id); writer.WriteString("name", ship.Name);
        if (ship.ParentShipId is { } parent) writer.WriteNumber("parentShipId", parent);
        writer.WriteBoolean("alive", ship.Alive); writer.WriteBoolean("cockpitBreached", ship.CockpitBreached);
        writer.WriteNumber("excess", ship.Excess); writer.WriteNumber("oxygenRemaining", ship.OxygenRemainingMilliseconds);
        writer.WriteNumber("radarRangeSelection", ship.RadarRangeSelection); writer.WriteNumber("weaponMode", ship.WeaponMode);
        writer.WriteStartObject("fuel"); writer.WriteNumber("capacity", ship.Fuel.Capacity); writer.WriteNumber("lvl", ship.Fuel.Level); writer.WriteEndObject();
        writer.WriteStartObject("reserve"); writer.WriteNumber("lvl", ship.Fuel.ReserveLevel); writer.WriteEndObject();
        writer.WriteStartObject("value"); writer.WriteNumber("unloaned", ship.Value.Unloaned); writer.WriteNumber("hull", ship.Value.Hull);
        writer.WriteNumber("modules", ship.Value.Modules); writer.WriteEndObject();
        writer.WriteStartObject("health"); writer.WriteNumber("hull", ship.Health.HullMillion); writer.WriteNumber("shield", ship.Health.ShieldMillion);
        writer.WriteBoolean("shieldup", ship.Health.ShieldUp); writer.WriteNumber("integrity", ship.Health.IntegrityMillion);
        writer.WriteNumber("paintwork", ship.Health.PaintworkMillion); writer.WriteEndObject();
        writer.WriteStartObject("location"); writer.WriteString("type", "ua"); writer.WriteString("ua", Location(ship.Location)); writer.WriteEndObject();
        if(ship.LastLocation is {} previous)
        { writer.WriteStartObject("lastLocation"); writer.WriteString("type","ua"); writer.WriteString("ua",Location(previous)); writer.WriteEndObject(); }
        WriteSlots(writer, "modules", ship.Modules); WriteSlots(writer, "fixedModules", ship.FixedModules);
        writer.WriteStartObject("firegroups");
        foreach (var group in ship.Firegroups)
        {
            writer.WriteStartObject(group.Index.ToString(CultureInfo.InvariantCulture));
            WriteIndices(writer, "primary", group.Primary); WriteIndices(writer, "secondary", group.Secondary); writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.WriteStartObject("capacitors");
        foreach (var capacitor in ship.Capacitors)
        {
            writer.WriteStartObject(capacitor.Index.ToString(CultureInfo.InvariantCulture));
            writer.WriteNumber("pips", capacitor.Pips);
            if (capacitor.EnergyMillion is { } energy) writer.WriteNumber("energy", energy);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.WriteStartArray("cargo");
        foreach (var cargo in ship.Cargo)
        {
            writer.WriteStartObject(); writer.WriteString("commodity", cargo.Commodity); writer.WriteNumber("owner", cargo.Owner);
            writer.WriteNumber("qty", cargo.Quantity); writer.WriteNumber("value", cargo.TotalValue);
            if (cargo.Mission is { } mission) writer.WriteNumber("mission", mission);
            if (cargo.PowerplayOrigin is { } powerplay) writer.WriteNumber("powerplayOrigin", powerplay);
            if (cargo.Marked is { } marked) writer.WriteNumber("marked", marked);
            if (cargo.Origin is { } origin) writer.WriteNumber("origin", origin);
            if (cargo.OriginSystem is { } originSystem) writer.WriteNumber("originSystem", originSystem);
            writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject();
    }

    private static void WriteSlots(Utf8JsonWriter writer, string name, IReadOnlyList<NativeSlotSnapshot> slots)
    {
        writer.WriteStartObject(name);
        foreach (var slot in slots)
        {
            writer.WriteStartObject(slot.Name);
            if (slot.Module is { } module)
            {
                writer.WritePropertyName("module");WriteModule(writer,module);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    internal static JsonElement ModuleDocument(NativeModuleSnapshot module)
    {
        using var output=new MemoryStream();using(var writer=new Utf8JsonWriter(output))WriteModule(writer,module);
        using var document=JsonDocument.Parse(output.GetBuffer().AsMemory(0,(int)output.Length));return document.RootElement.Clone();
    }
    private static void WriteModule(Utf8JsonWriter writer,NativeModuleSnapshot module)
    {
        writer.WriteStartObject();
        writer.WriteString("name",module.Name);writer.WriteNumber("id",module.Id);writer.WriteNumber("value",module.Value);
        writer.WriteNumber("excess",module.Excess);writer.WriteBoolean("on",module.On);
        writer.WriteNumber("priority",module.Priority);writer.WriteNumber("health",module.HealthMillion);
        if(module.Default is {} defaults)
        {writer.WriteStartObject("default");writer.WriteNumber("id",defaults.Id);writer.WriteString("name",defaults.Name);writer.WriteNumber("cost",defaults.Cost);writer.WriteEndObject();}
        if(module.Uid is {} uid)writer.WriteNumber("uid",uid);
        if(module.Boost is {} boost)writer.WriteNumber("boost",boost);
        if(module.DamageOverride is {} damage)writer.WriteNumber("damageOverride",damage);
        if(module.Free is {} free)writer.WriteBoolean("free",free);
        if(module.Class is {} moduleClass)writer.WriteNumber("class",moduleClass);
        if(module.Ammo is {} ammo)
        {writer.WriteStartObject("ammo");writer.WriteNumber("clip",ammo.Clip);writer.WriteNumber("hopper",ammo.Hopper);writer.WriteEndObject();}
        if(module.Engineering is {} engineering)
        {
            writer.WriteString("recipeName",engineering.RecipeName);writer.WriteNumber("recipeLevel",engineering.Level);writer.WriteNumber("craftCount",engineering.CraftCount);
            writer.WriteStartObject("modifiers");writer.WriteNumber("engineerID",engineering.EngineerId);writer.WriteNumber("recipeID",engineering.RecipeId);writer.WriteNumber("progress",engineering.Progress);
            writer.WriteStartArray("modifiers");foreach(var item in engineering.Modifiers){writer.WriteStartObject();writer.WriteString("name",item.Name);writer.WriteNumber("value",item.Value);writer.WriteNumber("type",item.Type);writer.WriteEndObject();}
            writer.WriteEndArray();writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteIndices(Utf8JsonWriter writer, string name, IReadOnlyList<uint> indices)
    {
        writer.WriteStartArray(name); foreach (var index in indices) writer.WriteNumberValue(index); writer.WriteEndArray();
    }

    private static void VesselId(uint id)
    {
        if (id == uint.MaxValue) Invalid("UInt32.MaxValue is the native absent-vessel sentinel.");
    }
    private static void Text(string? value, string name, int maximum = 200)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)) Invalid($"{name} must be bounded nonempty text without control characters.");
    }
    private static void List<T>(IReadOnlyList<T>? list, int maximum, string name, bool allowEmpty = true)
    {
        if (list is null || list.Count > maximum || (!allowEmpty && list.Count == 0)) Invalid($"{name} must be explicitly supplied within its local size bound.");
    }
    private static void NotNull(object? value, string name)
    {
        if (value is null) Invalid($"{name} is required.");
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid(string message) => throw new ArgumentException(message);
}
