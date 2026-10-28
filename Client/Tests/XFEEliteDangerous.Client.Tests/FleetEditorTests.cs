using System.Text.Json.Nodes;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class FleetEditorTests
{
    [Theory]
    [InlineData("SmallHardpoint1", "hardpoint", 1)]
    [InlineData("HugeHardpoint2", "hardpoint", 4)]
    [InlineData("TinyHardpoint1", "utility", 0)]
    [InlineData("PowerPlant", "core", 0)]
    [InlineData("Slot02_Size4", "internal", 4)]
    [InlineData("Military01_Size5", "internal", 5)]
    public void SlotTaxonomyPreservesOriginalIdentifiers(string slot, string category, int capacity)
    {
        Assert.Equal(category, FleetEditorProjection.Category(slot));
        Assert.Equal(capacity, FleetEditorProjection.Capacity(slot));
    }

    [Fact]
    public void DisplayFormattingDoesNotAlterTheServerPayload()
    {
        var original = JsonNode.Parse("""{"slots":[{"name":"PowerPlant","module":{"name":"int_powerplant_size2_class1","id":128064234,"on":true,"healthMillion":875000,"priority":2},"compatible":[{"name":"int_powerplant_size2_class1","id":128064234}]}]}""")!;
        var before = original.ToJsonString();
        var slot = Assert.Single(FleetEditorProjection.Slots(original));
        using (AppText.UseLanguage("en-US"))
        { Assert.Equal("Power plant 2E", slot.DisplayName); Assert.Contains("87.5%", slot.State); }
        using (AppText.UseLanguage("zh-CN"))
        { Assert.Equal("发电厂 2E", slot.DisplayName); Assert.Contains("87.5%", slot.State); }
        slot.Data["module"]!["on"] = false;
        Assert.Equal(before, original.ToJsonString());
        Assert.Equal("PowerPlant", slot.Name); Assert.Equal("int_powerplant_size2_class1", slot.Module);
    }

    [Fact]
    public void EmptySlotsAndUnknownSymbolsRemainExplicit()
    {
        using var language = AppText.UseLanguage("en-US");
        var slot = Assert.Single(FleetEditorProjection.Slots(JsonNode.Parse("""{"slots":[{"name":"SmallHardpoint1","module":null,"compatible":[]}]}""")!));
        Assert.Equal("Empty slot", slot.DisplayName); Assert.Equal("Empty slot", slot.State);
        Assert.Equal("newfamily", FleetEditorProjection.ModuleName("int_newfamily"));
    }
}
