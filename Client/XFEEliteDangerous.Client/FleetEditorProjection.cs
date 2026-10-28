using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace XFEEliteDangerous.Client;

/// <summary>Display projections only. Original slot names and module symbols remain wire identifiers.</summary>
public static partial class FleetEditorProjection
{
    public static string Category(string slot) => slot.Contains("Hardpoint", StringComparison.OrdinalIgnoreCase)
        ? slot.StartsWith("Tiny", StringComparison.OrdinalIgnoreCase) ? "utility" : "hardpoint"
        : slot.StartsWith("Slot", StringComparison.OrdinalIgnoreCase) || slot.StartsWith("Military", StringComparison.OrdinalIgnoreCase) ? "internal" : "core";

    public static int Capacity(string slot)
    {
        if (slot.Contains("Hardpoint", StringComparison.OrdinalIgnoreCase))
        {
            var sizes = new[] { "Tiny", "Small", "Medium", "Large", "Huge" };
            return Array.FindIndex(sizes, size => slot.StartsWith(size, StringComparison.OrdinalIgnoreCase));
        }
        var match = SlotSize().Match(slot);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    public static string ModuleName(string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return AppText.Get("FleetPage.Text119");
        var family = symbol.Split('_').Skip(1).FirstOrDefault() ?? symbol;
        var key = "Refit.Module." + family.ToLowerInvariant();
        var name = AppText.Catalog(AppText.Language).TryGetValue(key, out var translated) ? translated : family;
        var size = ModuleSize().Match(symbol); var rating = ModuleClass().Match(symbol);
        if (size.Success) name += " " + size.Groups[1].Value;
        if (rating.Success && int.TryParse(rating.Groups[1].Value, out var grade) && grade is >= 1 and <= 5) name += "EDCBA"[grade - 1];
        if (!size.Success)
            foreach (var label in new[] { "tiny", "small", "medium", "large", "huge" })
                if (symbol.EndsWith("_" + label, StringComparison.OrdinalIgnoreCase)) name += " · " + AppText.Get("Refit.Size." + label);
        return name;
    }

    public static IReadOnlyList<FleetEditorSlot> Slots(JsonNode ship)
    {
        var result = new List<FleetEditorSlot>();
        foreach (var source in (ship["slots"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = source["name"]?.GetValue<string>() ?? throw new InvalidDataException("Missing fleet slot name.");
            result.Add(new(name, Category(name), Capacity(name), (JsonObject)source.DeepClone()));
        }
        return result;
    }

    [GeneratedRegex(@"(?:Slot\d+_Size|Military\d+_Size)(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SlotSize();
    [GeneratedRegex(@"_size(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModuleSize();
    [GeneratedRegex(@"_class(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModuleClass();
}

public sealed record FleetEditorSlot(string Name, string Category, int Capacity, JsonObject Data)
{
    public string Module => Data["module"]?["name"]?.GetValue<string>() ?? "";
    public string DisplayName => FleetEditorProjection.ModuleName(Module);
    public string Caption => Name + (Capacity > 0 ? " · " + Capacity.ToString(CultureInfo.InvariantCulture) : "");
    public string State => Module.Length == 0 ? AppText.Get("FleetPage.Text119") : AppText.Format("Refit.SlotState", (Data["module"]?["healthMillion"]?.GetValue<uint>() ?? 0) / 10000d,
        Data["module"]?["on"]?.GetValue<bool>() == true ? AppText.Get("InventoryPage.Text233") : AppText.Get("InventoryPage.Text234"));
}
