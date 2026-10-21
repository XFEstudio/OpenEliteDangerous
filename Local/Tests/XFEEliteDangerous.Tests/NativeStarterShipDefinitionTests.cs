using XFEEliteDangerous.Core;
using Xunit;

namespace XFEEliteDangerous.Tests;

public sealed class NativeStarterShipDefinitionTests
{
    [Fact] public void PrivateGrantKeepsUnknownFixedBindingsExplicitAndDoesNotClaimNativeAcceptance()
    {
        var candidate = NativeStarterShipDefinition.Sidewinder(1, "Private starter");
        Assert.False(candidate.NativeAcceptanceVerified);
        Assert.Contains("CargoHatch/ModularCargoBayDoor", candidate.UnresolvedFixedModules);
        Assert.DoesNotContain(candidate.Modules, module => module.Slot == "CargoHatch" || module.CatalogueId == 0);
        Assert.All(candidate.Inventory.Modules, module => Assert.Equal(0, module.Value));
        Assert.Equal(2, candidate.Physical.FuelCapacity);
        Assert.Equal(2, candidate.Physical.FuelLevel);
        Assert.Equal(.3f, candidate.Physical.ReserveLevel);
        Assert.Equal(300_000U, candidate.Physical.OxygenMilliseconds);
    }

    [Fact] public void EachCandidateOwnsItsEditableInventoryWhileTheCatalogueBindingRemainsImmutable()
    {
        var first = NativeStarterShipDefinition.Sidewinder(1, "First");
        var second = NativeStarterShipDefinition.Sidewinder(2, "Second");
        var original = second.Inventory.Modules[0].ItemId;
        first.Inventory.Modules[0].ItemId = "changed-in-first-only";
        first.Inventory.Modules.Clear();
        Assert.NotEmpty(second.Inventory.Modules);
        Assert.Equal(original, second.Inventory.Modules[0].ItemId);
        Assert.Equal("1", first.Inventory.Id);
        Assert.Equal("2", second.Inventory.Id);
        Assert.Equal("Second", second.Inventory.Name);
        var catalogue = Assert.IsAssignableFrom<IList<NativeStarterModuleBinding>>(first.Modules);
        Assert.Throws<NotSupportedException>(() => catalogue[0] = new("Invalid", "Invalid", 1));
    }

    [Theory]
    [InlineData(0UL, "Ship")]
    [InlineData(ulong.MaxValue, "Ship")]
    [InlineData(4294967295UL, "Ship")]
    [InlineData(4294967296UL, "Ship")]
    [InlineData(1UL, "")]
    [InlineData(1UL, "  ")]
    [InlineData(1UL, "Ship\n")]
    public void PrivateCandidateRejectsSentinelIdentityAndUnusableName(ulong id, string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => NativeStarterShipDefinition.Sidewinder(id, name));
    }
}
