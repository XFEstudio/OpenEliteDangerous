using XFEEliteDangerous.Client.Storage;
using XFEEliteDangerous.Core;
using Xunit;

namespace XFEEliteDangerous.Tests;

public sealed class NativeCommanderProjectionTests
{
    private static readonly NativeLocationStringValue Position = new(42, new(1, 2, 3), new(0, 0, 0), false);
    internal static NativeSavegameCatalogue Catalogue => new()
    {
        Source = "synthetic private projection fixture", PinnedVersion = "2026.09.03.332841", ValidationPending = true,
        Ships = [new("SideWinder", [new("PowerPlant", true), new("OptionalSlot", false)], [new("Cockpit", true)])],
        Modules = [new(100, "TestPowerPlant"), new(200, "TestCockpit")]
    };
    private static NativeModuleSnapshot Module(uint id, string name) => new()
    {
        Id = id, Name = name, Value = 1200, Excess = 400, On = true, Priority = 2, HealthMillion = 765_432,
        Uid = 333, Ammo = new(10, 20), Default = new(id, name, 12)
    };
    internal static NativeSavegameSnapshot Snapshot(uint commanderId = 7) => new()
    {
        CommanderNativeId = commanderId, Credits = 100, Alive = true, Location = Position,
        CurrentVesselId = 1, ParentVesselId = 1,
        Vessels = [new()
        {
            Id = 1, Name = "SideWinder", Alive = true, CockpitBreached = false, Excess = 123,
            OxygenRemainingMilliseconds = 300_000, RadarRangeSelection = 0, WeaponMode = 3,
            Fuel = new(2, 1.5f, .3f), Value = new(10, 20, 30),
            Health = new(123, 456, true, 1_000_000, 999_999), Location = Position, LastLocation = Position,
            Modules = [new("PowerPlant", Module(100, "TestPowerPlant")), new("OptionalSlot", null)],
            FixedModules = [new("Cockpit", Module(200, "TestCockpit"))],
            Firegroups = [new(0, [0], [1])], Capacitors = [new(0, 4, 1_000_000)],
            Cargo = [new() { Commodity = "same_commodity", Owner = 12, Quantity = 3, TotalValue = 10, Mission = 123, Marked = 1 },
                new() { Commodity = "same_commodity", Owner = 22, Quantity = 2, TotalValue = -4, OriginSystem = 42 }]
        }]
    };
    private static CommanderState Inventory(NativeSavegameSnapshot baseline) =>
        NativeCommanderProjection.CreateInventory(baseline, Catalogue, 7, "private-id", "Private Commander", 3);
    private static NativeSavegameSnapshot Apply(NativeSavegameSnapshot baseline, CommanderState inventory) =>
        NativeCommanderProjection.ApplyInventory(baseline, Catalogue, 7, inventory);

    [Fact] public void RoundTripPreservesNativeOnlyMetadataAndSeparateCargoOwners()
    {
        var baseline = Snapshot(); var inventory = Inventory(baseline);
        Assert.Equal(2, inventory.Cargo.Count);
        Assert.NotEqual(inventory.Cargo[0].Id, inventory.Cargo[1].Id);
        Assert.Equal(inventory.Cargo[0].Name, inventory.Cargo[1].Name);
        Assert.Equal("fixed:Cockpit", inventory.Ships[0].Modules[1].Slot);
        Assert.Equal(NativeSavegameDocument.Serialize(baseline, Catalogue), NativeSavegameDocument.Serialize(Apply(baseline, inventory), Catalogue));
    }

    [Fact] public void EditsReachCreditsNormalAndFixedModulesWithoutDiscardingAmmoUidOrDefault()
    {
        var baseline = Snapshot(); var inventory = Inventory(baseline);
        inventory.Credits = 123456;
        inventory.Ships[0].Modules[0].Enabled = false;
        inventory.Ships[0].Modules[1].Health = .234567;
        inventory.Ships[0].Modules[1].Priority = 4;
        var projected = Apply(baseline, inventory);
        Assert.Equal(123456, projected.Credits);
        var normal = projected.Vessels[0].Modules[0].Module!;
        var fixedModule = projected.Vessels[0].FixedModules[0].Module!;
        Assert.False(normal.On); Assert.Equal(234_567u, fixedModule.HealthMillion); Assert.Equal(4u, fixedModule.Priority);
        Assert.Equal(baseline.Vessels[0].Modules[0].Module!.Ammo, normal.Ammo);
        Assert.Equal(333ul, normal.Uid); Assert.Equal(new NativeModuleDefaultSnapshot(100, "TestPowerPlant", 12), normal.Default);
        Assert.True(baseline.Vessels[0].Modules[0].Module!.On); // Baseline remains immutable.
    }

    [Fact] public void CargoQuantityAndRemovalPreserveTheSelectedRowsIdentityAndUnitValue()
    {
        var baseline = Snapshot(); var inventory = Inventory(baseline);
        inventory.Cargo[0].Quantity = 6; inventory.Cargo.RemoveAt(1);
        var cargo = Assert.Single(Apply(baseline, inventory).Vessels[0].Cargo);
        Assert.Equal(12ul, cargo.Owner); Assert.Equal(6u, cargo.Quantity); Assert.Equal(18, cargo.TotalValue);
        Assert.Equal(123ul, cargo.Mission); Assert.Equal(1u, cargo.Marked);
        Assert.Equal(2, baseline.Vessels[0].Cargo.Count);
    }

    [Fact] public void AccountIdentityIsBoundBeforeProjectingAnySave()
    {
        Assert.Throws<ArgumentException>(() => NativeCommanderProjection.CreateInventory(Snapshot(8), Catalogue, 7, "id", "Name", 0));
        Assert.Throws<ArgumentException>(() => NativeCommanderProjection.ApplyInventory(Snapshot(8), Catalogue, 7, Inventory(Snapshot())));
    }

    [Theory]
    [InlineData("new-cargo")][InlineData("oversized-cargo")][InlineData("rename-cargo")]
    [InlineData("materials")][InlineData("new-ship")][InlineData("active-ship")]
    [InlineData("change-resource")][InlineData("remove-module")][InlineData("change-module")]
    [InlineData("invalid-health")][InlineData("negative-credits")]
    public void IncompatiblePartialEditsAreRejectedInsteadOfSilentlyDivergingFromTheNativeSave(string mutation)
    {
        var baseline = Snapshot(); var inventory = Inventory(baseline);
        switch (mutation)
        {
            case "new-cargo": inventory.Cargo.Add(new() { Id = "new", Name = "New", Quantity = 1 }); break;
            case "oversized-cargo": inventory.Cargo[0].Quantity = 65536; break;
            case "rename-cargo": inventory.Cargo[0].Name = "different"; break;
            case "materials": inventory.Materials.Add(new() { Id = "material", Quantity = 1 }); break;
            case "new-ship": inventory.Ships.Add(new() { Id = "2" }); break;
            case "active-ship": inventory.Ships[0].Active = false; break;
            case "change-resource": inventory.Ships[0].ShipType = "DifferentShip"; break;
            case "remove-module": inventory.Ships[0].Modules.RemoveAt(0); break;
            case "change-module": inventory.Ships[0].Modules[0].ItemId = "different"; break;
            case "invalid-health": inventory.Ships[0].Modules[0].Health = double.NaN; break;
            case "negative-credits": inventory.Credits = -1; break;
        }
        Assert.Throws<ArgumentException>(() => Apply(baseline, inventory));
    }
}
