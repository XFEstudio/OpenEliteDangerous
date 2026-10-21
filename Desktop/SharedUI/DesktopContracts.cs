using XFEEliteDangerous.Client;
using System.Globalization;
using System.Text.Json.Nodes;

namespace XFEEliteDangerous.Desktop;

/// <summary>Local data projections shared by pages and the desktop construction checks.</summary>
public static class DesktopContracts
{
    public static InventoryRow[] Inventory(JsonNode data, string category) => data[category].Rows().Select(x => new InventoryRow(
        x.Text("id"), x.Text("name", x.Text("id")), category == "ships" ? 1 : x.Number("quantity"),
        category == "ships" ? x.Text("shipType") + " · " + (x["modules"] as JsonArray)?.Count + AppText.Get("DesktopContracts.Text111") : category == "cargo" ? AppText.Get("DesktopContracts.Text112") : x.Text("category", "materials"),
        x.Text("category", category))).ToArray();

    public static CatalogChoice[] Catalog(JsonNode data, string category) => data.Rows()
        .Select(x => new CatalogChoice(x.Text("id"), x.Text("name"), x.Text("category")))
        .Where(x => category == "cargo" ? x.Category.Equals("cargo", StringComparison.OrdinalIgnoreCase) : IsMaterialCategory(x.Category)).ToArray();

    public static bool IsMaterialCategory(string value) =>
        value.Equals("Raw", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Manufactured", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Encoded", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("materials", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("material", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("Backpack.", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("ShipLocker.", StringComparison.OrdinalIgnoreCase);

    public static JsonObject Quantity(string id, string category, long quantity, string? version)
    {
        var body = new JsonObject { ["itemId"] = id, ["category"] = category, ["quantity"] = quantity };
        return Version(body, version);
    }

    public static JsonObject Version(JsonObject body, string? version)
    {
        if (!string.IsNullOrEmpty(version)) body["version"] = version;
        return body;
    }

    public static void ApplyPlayerAccount(JsonNode account)
    {
        var id = account.Text("id");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException(AppText.Get("DesktopContracts.Text113"));
        var role = account.Text("role", "player");
        if (role is not ("player" or "administrator")) throw new InvalidDataException(AppText.Get("DesktopContracts.Text114"));
        DesktopContext.State.AccountId = id;
        DesktopContext.State.DisplayName = account.Text("displayName", AppText.Get("DesktopContracts.Text115"));
        DesktopContext.State.AccountRole = role;
    }

    public static double Real(this JsonNode? node, string name) =>
        double.TryParse(node?[name]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
}
