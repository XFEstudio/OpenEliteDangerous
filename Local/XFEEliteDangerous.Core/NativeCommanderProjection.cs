using System.Globalization;

namespace XFEEliteDangerous.Core;

/// <summary>
/// Bridges the editable private inventory and an explicitly supplied native save.
/// Native cargo row keys retain separate owner/mission/value metadata; two stacks
/// of the same commodity are not silently collapsed. No ships or module IDs are minted.
/// </summary>
public static class NativeCommanderProjection
{
    public static CommanderState CreateInventory(NativeSavegameSnapshot snapshot, NativeSavegameCatalogue catalogue,
        uint accountNativeId, string commanderId, string commanderName, long revision)
    {
        _ = NativeSavegameDocument.Serialize(snapshot, catalogue);
        if (accountNativeId == 0 || snapshot.CommanderNativeId != accountNativeId)
            throw new ArgumentException("Native save belongs to a different private commander.");
        if (snapshot.Credits is < 0 or > 9_000_000_000_000 || string.IsNullOrWhiteSpace(commanderName)
            || commanderName.Length > 64 || commanderName.Any(char.IsControl))
            throw new ArgumentException("Native save cannot be represented by the editable private inventory.");
        var current = snapshot.Vessels.Single(ship => ship.Id == snapshot.CurrentVesselId);
        return new CommanderState
        {
            Id = commanderId, Name = commanderName, Revision = revision, Credits = snapshot.Credits,
            Source = "private-native-save",
            Ships = snapshot.Vessels.Select(ship => new ShipState
            {
                Id = Number(ship.Id), ShipType = ship.Name, Name = ship.Name, Active = ship.Id == snapshot.CurrentVesselId,
                Modules = ship.Modules.Where(slot => slot.Module is not null).Select(slot => InventoryModule(slot.Name, slot.Module!))
                    .Concat(ship.FixedModules.Where(slot => slot.Module is not null)
                        .Select(slot => InventoryModule("fixed:" + slot.Name, slot.Module!))).ToList()
            }).ToList(),
            Cargo = current.Cargo.Select((cargo, index) => new InventoryStack
            {
                Id = CargoKey(index), Name = cargo.Commodity, Quantity = checked((int)cargo.Quantity), Category = "cargo"
            }).ToList()
        };
    }

    public static NativeSavegameSnapshot ApplyInventory(NativeSavegameSnapshot baseline, NativeSavegameCatalogue catalogue,
        uint accountNativeId, CommanderState inventory,bool materialsHandled=false)
    {
        if (accountNativeId == 0 || baseline.CommanderNativeId != accountNativeId)
            throw new ArgumentException("Native save belongs to a different private commander.");
        if (inventory.Ships.Count != baseline.Vessels.Count || inventory.Ships.Select(ship => ship.Id).Distinct().Count() != inventory.Ships.Count
            || inventory.Ships.Count(ship => ship.Active) != 1
            || !inventory.Ships.Any(ship => ship.Active && ship.Id == Number(baseline.CurrentVesselId)))
            throw new ArgumentException("Native vessel membership or active selection changed; import a complete native save instead.");
        if (inventory.Materials.Count != 0&&!materialsHandled)
            throw new ArgumentException("Native material editing requires its typed resource mapping; partial Journal materials cannot replace it.");
        var current = baseline.Vessels.Single(ship => ship.Id == baseline.CurrentVesselId);
        var cargoByKey = inventory.Cargo.ToDictionary(stack => stack.Id, StringComparer.Ordinal);
        var expectedKeys = current.Cargo.Select((_, index) => CargoKey(index)).ToHashSet(StringComparer.Ordinal);
        if (cargoByKey.Keys.Any(key => !expectedKeys.Contains(key)))
            throw new ArgumentException("New native cargo requires an explicit owner and value in a complete native save.");
        var cargo = new List<NativeCargoSnapshot>();
        for (var index = 0; index < current.Cargo.Count; index++)
        {
            var original = current.Cargo[index];
            if (!cargoByKey.TryGetValue(CargoKey(index), out var editable)) continue;
            if (editable.Name != original.Commodity || editable.Category != "cargo" || editable.Quantity is < 0 or > ushort.MaxValue)
                throw new ArgumentException("Native cargo row identity or quantity is invalid.");
            if (editable.Quantity == 0) continue;
            var quantity = (uint)editable.Quantity;
            // Retain the exact total on unchanged rows, including division remainder.
            var total = quantity == original.Quantity ? original.TotalValue
                : original.Quantity == 0 ? 0 : checked(original.TotalValue / original.Quantity * quantity);
            cargo.Add(original with { Quantity = quantity, TotalValue = total });
        }
        var vessels = baseline.Vessels.Select(ship =>
        {
            var editable = inventory.Ships.SingleOrDefault(candidate => candidate.Id == Number(ship.Id))
                ?? throw new ArgumentException("Native vessel was removed from editable inventory.");
            if (editable.ShipType != ship.Name) throw new ArgumentException("Native vessel resource identity changed.");
            var expectedSlots = ship.Modules.Where(slot => slot.Module is not null).Select(slot => slot.Name)
                .Concat(ship.FixedModules.Where(slot => slot.Module is not null).Select(slot => "fixed:" + slot.Name)).ToHashSet(StringComparer.Ordinal);
            if (editable.Modules.Count != expectedSlots.Count || editable.Modules.Select(module => module.Slot).Distinct().Count() != expectedSlots.Count
                || editable.Modules.Any(module => !expectedSlots.Contains(module.Slot)))
                throw new ArgumentException("Native module membership changed; import a complete native save instead.");
            NativeSlotSnapshot MapSlot(NativeSlotSnapshot slot, bool fixedModule)
            {
                if (slot.Module is not { } module) return slot;
                var changed = editable.Modules.Single(candidate => candidate.Slot == (fixedModule ? "fixed:" : "") + slot.Name);
                if (changed.ItemId != module.Name || changed.Priority is < 0 or > 4 || !double.IsFinite(changed.Health)
                    || changed.Health is < 0 or > 1)
                    throw new ArgumentException("Native editable module identity, health or priority is invalid.");
                return slot with { Module = module with { On = changed.Enabled, Priority = (uint)changed.Priority,
                    HealthMillion = checked((uint)Math.Round(changed.Health * 1_000_000, MidpointRounding.AwayFromZero)) } };
            }
            return ship with { Modules = ship.Modules.Select(slot => MapSlot(slot, false)).ToArray(),
                FixedModules = ship.FixedModules.Select(slot => MapSlot(slot, true)).ToArray(),
                Cargo = ship.Id == baseline.CurrentVesselId ? cargo : ship.Cargo };
        }).ToArray();
        var projected = baseline with { Credits = inventory.Credits, Vessels = vessels };
        if (projected.Credits is < 0 or > 9_000_000_000_000) throw new ArgumentException("Native credits exceed private inventory limits.");
        _ = NativeSavegameDocument.Serialize(projected, catalogue);
        return projected;
    }

    private static ModuleState InventoryModule(string slot, NativeModuleSnapshot module)
    {
        if (module.Value > long.MaxValue || module.Priority > 4 || module.HealthMillion > 1_000_000)
            throw new ArgumentException("Native module exceeds editable private inventory limits.");
        return new() { Slot = slot, ItemId = module.Name, Priority = (int)module.Priority, Enabled = module.On,
            Health = module.HealthMillion / 1_000_000d, Value = (long)module.Value };
    }
    private static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);
    private static string CargoKey(int index) => "native-cargo-" + index.ToString(CultureInfo.InvariantCulture);

    internal static CatalogEntry? CargoDefinition(NativeSavegameSnapshot snapshot, string rowId)
    {
        var current = snapshot.Vessels.Single(ship => ship.Id == snapshot.CurrentVesselId);
        for (var index = 0; index < current.Cargo.Count; index++)
            if (rowId == CargoKey(index)) return new(rowId, current.Cargo[index].Commodity, "cargo");
        return null;
    }
}
