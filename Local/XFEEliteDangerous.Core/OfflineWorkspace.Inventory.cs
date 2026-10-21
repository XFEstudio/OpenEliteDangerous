using XFEEliteDangerous.Client;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XFEEliteDangerous.Core;

public sealed partial class OfflineWorkspace
{
    private static JsonNode Mutate(DatabaseState state, AccountState account, string operation, JsonObject body)
    {
        var commander = account.Commander;
        if (operation == "credits")
        {
            var balance = checked(commander.Credits + Integer(body, "delta", -9_000_000_000_000, 9_000_000_000_000));
            if (balance is < 0 or > 9_000_000_000_000) throw new InvalidDataException(AppText.Get("OfflineWorkspace_Inventory.Text423"));
            commander.Credits = balance;
        }
        else if (operation is "cargo" or "materials")
        {
            var id = Text(body, "itemId", 160); var quantity = (int)Integer(body, "quantity", 0, 1_000_000);
            var category = operation == "cargo" ? "cargo" : TextOrDefault(body, "category", "materials");
            var list = operation == "cargo" ? commander.Cargo : commander.Materials;
            if (operation == "materials" && !body.ContainsKey("category") && (list.Count(x => x.Id == id) > 1 || !list.Any(x => x.Id == id) && state.Catalog.Count(x => x.Id == id && x.Category != "cargo" && x.Category != "module") > 1)) throw new InvalidDataException(AppText.Get("OfflineWorkspace_Inventory.Text424"));
            var item = list.FirstOrDefault(x => x.Id == id && (operation == "cargo" || x.Category == category || !body.ContainsKey("category")));
            var known = operation == "cargo" && account.NativeSave is { } nativeCargo
                ? NativeCommanderProjection.CargoDefinition(nativeCargo.Snapshot, id)
                : state.Catalog.FirstOrDefault(x => x.Id == id && (operation == "cargo" ? x.Category == "cargo" : x.Category != "cargo" && x.Category != "module" && (!body.ContainsKey("category") || x.Category == category)));
            if (item is null && known is null) throw new InvalidDataException(AppText.Get("OfflineWorkspace_Inventory.Text425"));
            if (item is null && quantity > 0) { item = new() { Id = id, Name = known!.Name, Category = known.Category }; list.Add(item); }
            if (item is not null) { if (quantity == 0) list.Remove(item); else item.Quantity = quantity; }
        }
        else if (operation == "modules")
        {
            var ship = commander.Ships.FirstOrDefault(x => x.Id == Text(body, "shipId", 80)) ?? throw new InvalidDataException("Ship not owned.");
            var module = ship.Modules.FirstOrDefault(x => x.Slot == Text(body, "slot", 100)) ?? throw new InvalidDataException("Module slot not found.");
            if (body.ContainsKey("priority")) module.Priority = (int)Integer(body, "priority", 0, 4);
            if (body.ContainsKey("enabled")) module.Enabled = Boolean(body, "enabled");
            if (body.ContainsKey("health"))
            {
                if (body["health"] is not JsonValue value || !value.TryGetValue<double>(out var health) || !double.IsFinite(health) || health is < 0 or > 1) throw new InvalidDataException("Invalid module health.");
                module.Health = health;
            }
        }
        else throw new InvalidDataException("Unknown commander operation.");
        // Fail the StateStore transaction when a partial edit cannot be represented
        // by the native save, rather than reporting success only in the desktop UI.
        if (account.NativeSave is { } nativeSave) _ = nativeSave.SerializeCurrent(account);
        commander.Revision = checked(commander.Revision + 1);
        return Json(commander.Public());
    }
    private JsonNode Import(DatabaseState state, AccountState account, JsonObject body)
    {
        if (account.NativeSave is not null) throw new InvalidDataException(AppText.Get("OfflineWorkspace_Inventory.Text426"));
        var source = body["commander"] as JsonObject ?? throw new InvalidDataException("Missing commander snapshot.");
        var data = (JsonObject)source.DeepClone();
        data.Remove("source"); data.Remove("id"); data.Remove("revision"); data.Remove("version");
        var candidate = data.Deserialize<CommanderState>(Wire.Json) ?? throw new InvalidDataException("Empty commander.");
        candidate.Id = account.Commander.Id; candidate.Revision = account.Commander.Revision + 1; candidate.Source = "local-journal-partial";
        if (string.IsNullOrWhiteSpace(candidate.Name) || candidate.Name.Length > 64 || candidate.Credits is < 0 or > 9_000_000_000_000) throw new InvalidDataException("Invalid commander identity or balance.");
        if (candidate.Cargo is null || candidate.Materials is null || candidate.Ships is null || candidate.Cargo.Count > 10000 || candidate.Materials.Count > 10000 || candidate.Ships.Count > 1000) throw new InvalidDataException("Invalid inventory size.");
        foreach (var list in new[] { candidate.Cargo, candidate.Materials })
        {
            if (list.Any(x => x is null)) throw new InvalidDataException("Invalid inventory stack.");
            if (list.GroupBy(x => x.Category + ":" + x.Id).Any(x => x.Count() != 1)) throw new InvalidDataException("Duplicate inventory stacks.");
            foreach (var item in list) if (!ValidItemId(item.Id) || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 200 || item.Quantity is < 0 or > 1_000_000 || string.IsNullOrWhiteSpace(item.Category) || item.Category.Length > 40) throw new InvalidDataException("Invalid inventory stack.");
        }
        foreach (var ship in candidate.Ships)
        {
            if (ship is null) throw new InvalidDataException("Invalid ship.");
            if (!ValidItemId(ship.Id) || !ValidItemId(ship.ShipType) || ship.Name is null || ship.Name.Length > 200 || ship.Ident is null || ship.Ident.Length > 64 || ship.Modules is null || ship.Modules.Count > 128 || ship.Modules.Any(x => x is null) || ship.Modules.GroupBy(x => x.Slot).Any(x => x.Count() > 1)) throw new InvalidDataException("Invalid ship.");
            foreach (var module in ship.Modules)
                if (module is null || !ValidItemId(module.ItemId) || !ValidItemId(module.Slot) || module.Priority is < 0 or > 4 || !double.IsFinite(module.Health) || module.Health is < 0 or > 1 || module.Value < 0) throw new InvalidDataException("Invalid module.");
        }
        if (candidate.Ships.GroupBy(x => x.Id).Any(x => x.Count() > 1) || candidate.Ships.Count(x => x.Active) > 1) throw new InvalidDataException("Invalid active ship selection.");
        var backup = store.Backup(account.Commander);
        account.Commander = candidate;
        foreach (var stack in candidate.Cargo.Concat(candidate.Materials))
            if (!state.Catalog.Any(x => x.Id == stack.Id && x.Category == stack.Category)) state.Catalog.Add(new(stack.Id, stack.Name, stack.Category));
        return Json(new { imported = true, backup, commander = candidate.Public(), gameIntegrationReady = false });
    }
}
