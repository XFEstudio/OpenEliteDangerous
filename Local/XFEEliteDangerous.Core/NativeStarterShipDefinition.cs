using System.Globalization;

namespace XFEEliteDangerous.Core;

public sealed record NativeStarterModuleBinding(string Slot, string Symbol, uint CatalogueId);
public sealed record NativeStarterPhysicalSeed(float FuelCapacity, float FuelLevel, float ReserveLevel, uint OxygenMilliseconds);
public sealed record NativeStarterShipCandidate(ShipState Inventory, IReadOnlyList<NativeStarterModuleBinding> Modules,
    NativeStarterPhysicalSeed Physical, IReadOnlyList<string> UnresolvedFixedModules)
{
    public bool NativeAcceptanceVerified => false;
}

/// <summary>A private grant candidate, never an official purchase or an automatically accepted native save.</summary>
public static class NativeStarterShipDefinition
{
    // Identity pin: EDCD/FDevIDs c35612952dd6a547d1a7ac4cffab9c7051e86579.
    // Canonical Journal slots cross-checked against the independent, pinned starter investigation.
    // These are catalogue identities, not instance uid values. No market prices are inferred.
    private static readonly NativeStarterModuleBinding[] sidewinder =
    [
        new("SmallHardpoint1", "Hpt_PulseLaser_Gimbal_Small", 128049385),
        new("SmallHardpoint2", "Hpt_PulseLaser_Gimbal_Small", 128049385),
        new("Armour", "SideWinder_Armour_Grade1", 128049250),
        new("PowerPlant", "Int_Powerplant_Size2_Class1", 128064033),
        new("MainEngines", "Int_Engine_Size2_Class1", 128064068),
        new("FrameShiftDrive", "Int_Hyperdrive_Size2_Class1", 128064103),
        new("LifeSupport", "Int_LifeSupport_Size1_Class1", 128064138),
        new("PowerDistributor", "Int_PowerDistributor_Size1_Class1", 128064178),
        new("Radar", "Int_Sensors_Size1_Class1", 128064218),
        new("FuelTank", "Int_FuelTank_Size1_Class3", 128064346),
        new("Slot01_Size2", "Int_ShieldGenerator_Size2_Class1", 128064263),
        new("Slot02_Size2", "Int_CargoRack_Size2_Class1", 128064339),
        new("Slot05_Size1", "Int_SuperCruiseAssist", 128932273),
        new("Slot06_Size1", "Int_DockingComputer_Advanced", 128935155),
        new("PlanetaryApproachSuite", "Int_PlanetApproachSuite_Advanced", 128975719)
    ];

    public static NativeStarterShipCandidate Sidewinder(ulong privateVesselId, string suppliedName)
    {
        if (privateVesselId is 0 or >= uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(privateVesselId));
        if (string.IsNullOrWhiteSpace(suppliedName) || suppliedName.Length > 200 || suppliedName.Any(char.IsControl))
            throw new ArgumentException("A bounded private ship name is required.", nameof(suppliedName));
        var inventory = new ShipState
        {
            Id = privateVesselId.ToString(CultureInfo.InvariantCulture), ShipType = "SideWinder", Name = suppliedName,
            Ident = "XFE-01", Active = true,
            Modules = sidewinder.Select(module => new ModuleState
            {
                Slot = module.Slot, ItemId = module.Symbol, Enabled = true, Priority = 0, Health = 1,
                // Explicit private gift policy: no loan or invented official market value.
                Value = 0
            }).ToList()
        };
        // Coriolis pin0db9234b...: tank1C capacity2, hull reservoir0.3, lifeSupport1E time300s.
        // Native oxygen wire is UInt32 /1000; tank fields are direct numeric values.
        return new(inventory, Array.AsReadOnly(sidewinder.ToArray()), new(2, 2, .3f, 300_000),
            Array.AsReadOnly(new[] { "CargoHatch/ModularCargoBayDoor" }));
    }
}
