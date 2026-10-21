using System.Security.Cryptography;
using System.Text.Json;
using XFEEliteDangerous.Client.Storage;

namespace XFEEliteDangerous.Core;

/// <summary>
/// Explicit private starter candidate for the pinned build. Actual resource slot metadata is combined
/// with the sourced private starter fit; this does not assert that a world, spawn or transport is ready.
/// This factory is never called by public registration or a native read.
/// </summary>
public static class NativeSidewinderSaveFactory
{
    private const string MetadataHash = "9e154c97e7a0cd2d924902dd33e936ce3449e8ac025545196191fb2dcfc85732";
    internal sealed record Slot(string Name, bool FixedModule, string ResourceDefault);
    internal sealed record Metadata(string Version, string NativeSha256, string SourceContract, Slot[] Slots);
    private static readonly Lazy<Metadata> metadata = new(ReadMetadata);
    internal static IReadOnlyList<string> NativeSlotNames => metadata.Value.Slots.Select(slot=>slot.Name).ToArray();

    public static NativeCommanderSave Create(uint commanderId, uint vesselId, long credits,
        NativeFullSavegameProfile profile, NativeLocationStringValue location)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (commanderId == 0) throw new ArgumentOutOfRangeException(nameof(commanderId));
        var starter = NativeStarterShipDefinition.Sidewinder(vesselId, profile.Name);
        var slots = metadata.Value.Slots;
        var bindings = starter.Modules.ToDictionary(module => module.Slot, StringComparer.Ordinal);
        if (bindings.Keys.Any(slot => !slots.Any(definition => definition.Name == slot && !definition.FixedModule)))
            throw new InvalidDataException("Private starter fit does not match the pinned native slot table.");
        NativeModuleSnapshot Module(uint id, string name) => new()
        {
            Id = id, Name = name, Value = 0, Excess = 0, On = true, Priority = 0, HealthMillion = 1_000_000,
            Free = true
        };
        //25E2610 initializes every resource slot before overlaying the fit,
        // including ordinary slots with built-in defaults. In particular the
        // VesselVoice slot must retain VoicePack_Verity; setting all unbound
        // ordinary slots to null incorrectly drops this resource default.
        var ordinary = slots.Where(slot => !slot.FixedModule).Select(slot => new NativeSlotSnapshot(slot.Name,
            bindings.TryGetValue(slot.Name, out var binding) ? Module(binding.CatalogueId, binding.Symbol)
                : string.IsNullOrEmpty(slot.ResourceDefault) ? null : Module(0, slot.ResourceDefault))).ToArray();
        var fixedModules = slots.Where(slot => slot.FixedModule).Select(slot =>
            // Native resource-derived defaults themselves use zero catalog identity.
            // Retain exact slot/name, never claim zero is an official market ID.
            new NativeSlotSnapshot(slot.Name, Module(0, slot.ResourceDefault))).ToArray();
        var catalogue = new NativeSavegameCatalogue
        {
            Source = "Pinned native64-slot defaults overlaid by sourced15-module private gift; uncatalogued built-ins id0; gameplay acceptance pending",
            PinnedVersion = metadata.Value.Version, ValidationPending = true,
            Ships = [new("SideWinder", ordinary.Select(slot => new NativeSlotDefinition(slot.Name, slot.Module is not null,
                    slot.Module is {Id:0} ? slot.Module.Name : null)).ToArray(),
                fixedModules.Select(slot => new NativeSlotDefinition(slot.Name, true)).ToArray())],
            Modules = ordinary.Concat(fixedModules).Where(slot=>slot.Module is not null)
                .Select(slot=>new NativeModuleDefinition(slot.Module!.Id,slot.Module.Name))
                .DistinctBy(module => module.Name, StringComparer.Ordinal).ToArray()
        };
        var save = new NativeCommanderSave
        {
            Catalogue = catalogue, Profile = profile,
            Snapshot = new()
            {
                CommanderNativeId = commanderId, Credits = credits, Alive = true, Location = location,
                CurrentVesselId = vesselId, ParentVesselId = vesselId,
                Vessels = [new()
                {
                    Id = vesselId, Name = "SideWinder", Alive = true, CockpitBreached = false, Excess = 0,
                    OxygenRemainingMilliseconds = starter.Physical.OxygenMilliseconds,
                    RadarRangeSelection = 0, WeaponMode = 3,
                    Fuel = new(starter.Physical.FuelCapacity, starter.Physical.FuelLevel, starter.Physical.ReserveLevel),
                    Value = new(0, 0, 0), Health = new(1_000_000, 1_000_000, true, 1_000_000, 1_000_000), Location = location, LastLocation = location,
                    Modules = ordinary, FixedModules = fixedModules,
                    // Explicit initial private configuration. Weapon assignments are not guessed from resource indices.
                    Firegroups = [], Capacitors = [new(0, 4, 1_000_000), new(1, 4, 1_000_000), new(2, 4, 1_000_000)], Cargo = []
                }]
            }
        };
        // Enforce the same account/system/field rules as the actual import endpoint before returning any candidate.
        _ = save.CreateInventory(new AccountState { NativeUserId = commanderId }, 0);
        return save;
    }

    private static Metadata ReadMetadata()
    {
        var assembly = typeof(NativeSidewinderSaveFactory).Assembly;
        var name = assembly.GetManifestResourceNames().Single(resource => resource.EndsWith(".native-sidewinder-slots.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)))) != MetadataHash)
            throw new InvalidDataException("Pinned native Sidewinder slot metadata changed.");
        var value = JsonSerializer.Deserialize<Metadata>(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)), Wire.Json)
            ?? throw new InvalidDataException("Missing native Sidewinder slot metadata.");
        if (value.Version != "2026.09.03.332841" || value.NativeSha256 != "e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988"
            || value.Slots.Length != 64 || value.Slots.Count(slot => slot.FixedModule) != 21
            || value.Slots.Select(slot => slot.Name).Distinct(StringComparer.Ordinal).Count() != 64
            || value.Slots.Any(slot => string.IsNullOrWhiteSpace(slot.Name) || slot.FixedModule && string.IsNullOrWhiteSpace(slot.ResourceDefault)))
            throw new InvalidDataException("Native Sidewinder slot metadata does not match the pinned contract.");
        return value;
    }
}
