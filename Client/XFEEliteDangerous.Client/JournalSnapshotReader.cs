using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XFEEliteDangerous.Client;

/// <summary>
/// Replays only documented state fields from local journals. Catalogues are evidence, never owned inventory.
/// The result is a partial local copy, not a Frontier save or a playable game-server implementation.
/// </summary>
public sealed class JournalSnapshotReader
{
    private static readonly string[] Sidecars = ["Cargo.json", "ShipLocker.json", "Backpack.json", "ModulesInfo.json", "Outfitting.json", "Shipyard.json"];
    private sealed record Entry(DateTimeOffset Timestamp, long Order, string Owner, string File, JsonElement Data);

    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Saved Games", "Frontier Developments", "Elite Dangerous");

    public async Task<CommanderSnapshot> ReadAsync(string journalDirectory, CancellationToken cancellationToken = default)
    {
        var root = LocalPaths.Directory(journalDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(AppText.Get("JournalSnapshotReader.Text024"));
        var entries = new List<Entry>();
        var evidence = new List<SourceFile>();
        var warnings = new List<string>();
        long sequence = 0;
        var currentOwner = "";
        foreach (var file in Directory.EnumerateFiles(root, "Journal.*.log").Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            using var stream = GameInstallationInspector.OpenRead(file);
            using var reader = new StreamReader(stream);
            var lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var data = document.RootElement;
                    if (data.ValueKind != JsonValueKind.Object || !Timestamp(data, out var timestamp))
                    { warnings.Add(AppText.Format("JournalSnapshotReader.Text025", (name), (lineNumber))); continue; }
                    var eventName = Text(data, "event");
                    if (eventName is "Commander" or "NewCommander") currentOwner = Text(data, "Name") ?? currentOwner;
                    if (eventName == "LoadGame") currentOwner = Text(data, "Commander") ?? currentOwner;
                    entries.Add(new(timestamp, sequence++, currentOwner, name, data.Clone()));
                }
                catch (JsonException) { warnings.Add(AppText.Format("JournalSnapshotReader.Text026", (name), (lineNumber))); }
            }
            var hash = await GameInstallationInspector.InspectFileAsync(file, cancellationToken).ConfigureAwait(false);
            evidence.Add(new(name, hash.Sha256, hash.Length));
        }
        entries.Sort((a, b) => a.Timestamp != b.Timestamp ? a.Timestamp.CompareTo(b.Timestamp) : a.Order.CompareTo(b.Order));
        var session = entries.LastOrDefault(e => Text(e.Data, "event") == "LoadGame" && !string.IsNullOrWhiteSpace(e.Owner));
        if (session is null) throw new InvalidDataException(AppText.Get("JournalSnapshotReader.Text027"));
        var selected = entries.Where(e => string.Equals(e.Owner, session.Owner, StringComparison.Ordinal)).ToList();
        foreach (var sidecar in Sidecars)
        {
            var file = Path.Combine(root, sidecar);
            if (!File.Exists(file)) continue;
            try
            {
                using var stream = GameInstallationInspector.OpenRead(file);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var data = document.RootElement;
                var hash = await GameInstallationInspector.InspectFileAsync(file, cancellationToken).ConfigureAwait(false);
                evidence.Add(new(sidecar, hash.Sha256, hash.Length));
                if (data.ValueKind != JsonValueKind.Object || !Timestamp(data, out var timestamp))
                { warnings.Add(AppText.Format("JournalSnapshotReader.Text028", (sidecar))); continue; }
                // Sidecars are shared between accounts. Only the newest commander's current session can claim them.
                if (timestamp < session.Timestamp)
                { warnings.Add(AppText.Format("JournalSnapshotReader.Text029", (sidecar))); continue; }
                if (sidecar is "Outfitting.json" or "Shipyard.json")
                { warnings.Add(AppText.Format("JournalSnapshotReader.Text030", (sidecar))); continue; }
                selected.Add(new(timestamp, sequence++, session.Owner, sidecar, data.Clone()));
            }
            catch (JsonException) { warnings.Add(AppText.Format("JournalSnapshotReader.Text031", (sidecar))); }
        }
        selected.Sort((a, b) => a.Timestamp != b.Timestamp ? a.Timestamp.CompareTo(b.Timestamp) : a.Order.CompareTo(b.Order));
        var replay = new Replay(session.Owner, warnings);
        foreach (var entry in selected) { cancellationToken.ThrowIfCancellationRequested(); replay.Apply(entry.Data, entry.Timestamp); }
        var version = entries.Where(e => Text(e.Data, "event") == "Fileheader").LastOrDefault();
        warnings.Add(AppText.Get("JournalSnapshotReader.Text032"));
        return replay.Snapshot(new("elite-journal", DateTimeOffset.UtcNow, selected.LastOrDefault()?.Timestamp,
            version is null ? null : Text(version.Data, "gameversion"), evidence, warnings.Distinct().ToArray()));
    }

    internal static string? Text(JsonElement data, string property) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Id(JsonElement data, string property) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.Number or JsonValueKind.String ? value.ToString() : "";
    private static long Number(JsonElement data, string property, long fallback = 0) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out var value)
        && value.TryGetInt64Safe(out var number) ? number : fallback;
    private static double Fraction(JsonElement data, string property, double fallback = 1) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : fallback;
    private static bool Bool(JsonElement data, string property, bool fallback = true) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
    private static IEnumerable<JsonElement> Array(JsonElement data, string property) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    private static bool Timestamp(JsonElement data, out DateTimeOffset timestamp)
        => DateTimeOffset.TryParse(Text(data, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp);
    private static string Symbol(string? symbol)
    {
        var result = (symbol ?? "").Trim().TrimStart('$').TrimEnd(';');
        if (result.EndsWith("_name", StringComparison.OrdinalIgnoreCase)) result = result[..^5];
        return result.ToLowerInvariant();
    }

    private sealed class Replay(string name, List<string> warnings)
    {
        private readonly Dictionary<string, InventoryItem> cargo = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MaterialItem> materials = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MutableShip> ships = new(StringComparer.Ordinal);
        private string? activeShip;
        private long credits;
        private DateTimeOffset lastLoadoutAt;
        private DateTimeOffset lastCargoSnapshot;
        private bool creditBaseline;

        public void Apply(JsonElement data, DateTimeOffset timestamp)
        {
            var evt = Text(data, "event");
            switch (evt)
            {
                case "NewCommander":
                    cargo.Clear(); materials.Clear(); ships.Clear(); activeShip = null; credits = 0; creditBaseline = false;
                    break;
                case "LoadGame":
                    if (data.TryGetProperty("Credits", out _)) { credits = Number(data, "Credits"); creditBaseline = true; }
                    SetActive(data, "ShipID", "Ship"); break;
                case "Loadout":
                    var ship = SetActive(data, "ShipID", "Ship");
                    if (ship is null) break;
                    ship.Name = Text(data, "ShipName") ?? ship.Name;
                    ship.Ident = Text(data, "ShipIdent") ?? ship.Ident;
                    ship.Modules.Clear();
                    foreach (var module in Array(data, "Modules")) PutModule(ship, ReadModule(module));
                    lastLoadoutAt = timestamp; break;
                case "StoredShips":
                    var owned = new HashSet<string>(StringComparer.Ordinal);
                    if (activeShip is not null) owned.Add(activeShip);
                    foreach (var stored in Array(data, "ShipsHere").Concat(Array(data, "ShipsRemote")))
                    {
                        var storedShip = GetShip(stored, "ShipID", "ShipType");
                        if (storedShip is null) continue;
                        storedShip.Name = Text(stored, "Name") ?? storedShip.Name;
                        owned.Add(storedShip.Id);
                    }
                    foreach (var id in ships.Keys.Where(id => !owned.Contains(id)).ToArray()) ships.Remove(id);
                    break;
                case "ShipyardNew": SetActive(data, "NewShipID", "ShipType"); break;
                case "ShipyardSwap": SetActive(data, "ShipID", "ShipType"); break;
                case "ShipyardSell": ships.Remove(Id(data, "SellShipID")); break;
                case "ShipyardBuy":
                    var sold = Id(data, "SellShipID"); if (sold.Length > 0) ships.Remove(sold);
                    break;
                case "SellShipOnRebuy": ships.Remove(Id(data, "SellShipId")); break;
                case "SetUserShipName":
                    var named = GetShip(data, "ShipID", "Ship");
                    if (named is not null) { named.Name = Text(data, "UserShipName") ?? named.Name; named.Ident = Text(data, "UserShipId") ?? named.Ident; }
                    break;
                case "ModuleInfo":
                    if (timestamp < lastLoadoutAt || activeShip is null || !ships.TryGetValue(activeShip, out var current)) break;
                    foreach (var module in Array(data, "Modules"))
                    {
                        var slot = Text(module, "Slot") ?? "";
                        var item = Symbol(Text(module, "Item"));
                        // ModuleInfo is a power-priority supplement, not a complete loadout. Do not invent missing equipment.
                        if (current.Modules.TryGetValue(slot, out var old) && old.ItemId == item)
                            current.Modules[slot] = old with { Priority = (int)Number(module, "Priority", old.Priority) };
                    }
                    break;
                case "ModuleBuy": ChangeModule(data, "Slot", "BuyItem"); break;
                case "ModuleRetrieve": ChangeModule(data, "Slot", "RetrievedItem"); break;
                case "ModuleSell":
                case "ModuleStore":
                    var changed = GetShip(data, "ShipID", "Ship");
                    changed?.Modules.Remove(Text(data, "Slot") ?? "");
                    if (!string.IsNullOrEmpty(Text(data, "ReplacementItem"))) ChangeModule(data, "Slot", "ReplacementItem");
                    break;
                case "ModuleSwap":
                    var swapped = GetShip(data, "ShipID", "Ship");
                    if (swapped is null) break;
                    var from = Text(data, "FromSlot") ?? ""; var to = Text(data, "ToSlot") ?? "";
                    swapped.Modules.Remove(from, out var fromModule); swapped.Modules.Remove(to, out var toModule);
                    if (fromModule is not null) PutModule(swapped, fromModule with { Slot = to });
                    if (toModule is not null) PutModule(swapped, toModule with { Slot = from });
                    break;
                case "MassModuleStore":
                    var storedModules = GetShip(data, "ShipID", "Ship");
                    if (storedModules is not null)
                        foreach (var item in Array(data, "Items")) storedModules.Modules.Remove(Text(item, "Slot") ?? "");
                    break;
                case "Cargo":
                    if (data.TryGetProperty("Inventory", out var inventory) && inventory.ValueKind == JsonValueKind.Array)
                    {
                        cargo.Clear();
                        foreach (var item in inventory.EnumerateArray()) CargoDelta(Text(item, "Name"), Text(item, "Name_Localised"), Number(item, "Count"));
                        lastCargoSnapshot = timestamp;
                    }
                    break;
                case "CollectCargo": if (timestamp > lastCargoSnapshot) CargoDelta(Text(data, "Type"), Text(data, "Type_Localised"), Number(data, "Count", 1)); break;
                case "EjectCargo": if (timestamp > lastCargoSnapshot) CargoDelta(Text(data, "Type"), null, -Number(data, "Count")); break;
                case "MarketBuy": if (timestamp > lastCargoSnapshot) CargoDelta(Text(data, "Type"), Text(data, "Type_Localised"), Number(data, "Count")); break;
                case "MarketSell": if (timestamp > lastCargoSnapshot) CargoDelta(Text(data, "Type"), null, -Number(data, "Count")); break;
                case "Materials":
                    foreach (var category in new[] { "Raw", "Manufactured", "Encoded" }) ReplaceMaterials(data, category, category);
                    break;
                case "ShipLocker": case "Backpack":
                    foreach (var category in new[] { "Items", "Components", "Consumables", "Data" }) ReplaceMaterials(data, category, evt + "." + category);
                    break;
                case "MaterialCollected": MaterialDelta(Text(data, "Name"), Text(data, "Name_Localised"), Number(data, "Count"), Text(data, "Category") ?? "Unknown"); break;
                case "MaterialDiscarded": MaterialDelta(Text(data, "Name"), null, -Number(data, "Count"), Text(data, "Category") ?? "Unknown"); break;
                case "MaterialTrade":
                    foreach (var (key, sign) in new[] { ("Paid", -1), ("Received", 1) })
                        if (data.TryGetProperty(key, out var trade) && trade.ValueKind == JsonValueKind.Object)
                            MaterialDelta(Text(trade, "Material"), Text(trade, "Material_Localised"), sign * Number(trade, "Quantity"), Text(trade, "Category") ?? "Unknown");
                    break;
                case "EngineerCraft": case "Synthesis":
                    foreach (var ingredient in Array(data, evt == "EngineerCraft" ? "Ingredients" : "Materials"))
                    {
                        var id = Symbol(Text(ingredient, "Name"));
                        var category = materials.Values.FirstOrDefault(m => m.Id == id && !m.Category.Contains('.'))?.Category;
                        if (category is not null) MaterialDelta(id, null, -Number(ingredient, "Count"), category);
                    }
                    if (evt == "EngineerCraft" && activeShip is not null && ships.TryGetValue(activeShip, out var engineered))
                    {
                        var slot = Text(data, "Slot") ?? "";
                        if (engineered.Modules.TryGetValue(slot, out var old) && old.ItemId == Symbol(Text(data, "Module")))
                        {
                            var engineering = old.Engineering?.DeepClone().AsObject() ?? new JsonObject();
                            if (ReadEngineering(data) is { } update)
                                foreach (var property in update) engineering[property.Key] = property.Value?.DeepClone();
                            if (Text(data, "ApplyExperimentalEffect") is { } effect) engineering["ExperimentalEffect"] = effect;
                            engineered.Modules[slot] = old with { Engineering = engineering };
                        }
                    }
                    break;
            }
            ApplyCreditChange(data, evt);
        }

        private void ApplyCreditChange(JsonElement data, string? evt)
        {
            if (!creditBaseline || evt == "LoadGame") return;
            var delta = evt switch
            {
                "MarketBuy" or "BuyDrones" => -Number(data, "TotalCost"),
                "MarketSell" or "SellDrones" => Number(data, "TotalSale"),
                "ModuleBuy" => -Number(data, "BuyPrice") + Number(data, "SellPrice"),
                "ModuleSell" or "ModuleSellRemote" => Number(data, "SellPrice"),
                "ShipyardBuy" => -Number(data, "ShipPrice") + Number(data, "SellPrice"),
                "ShipyardSell" or "SellShipOnRebuy" => Number(data, "ShipPrice"),
                "BuySuit" or "BuyWeapon" or "BuyMicroResources" => -Number(data, "Price"),
                "SellSuit" or "SellWeapon" or "SellMicroResources" => Number(data, "Price"),
                "MissionCompleted" => Number(data, "Reward") - Number(data, "Donated"),
                "RedeemVoucher" => Number(data, "Amount"),
                "PayFines" or "PayBounties" or "PayLegacyFines" => -Number(data, "Amount"),
                "SellExplorationData" or "MultiSellExplorationData" => Number(data, "TotalEarnings"),
                "Resurrect" or "Repair" or "RepairAll" or "RefuelAll" or "RefuelPartial" or "BuyAmmo"
                    or "BuyTradeData" or "BuyExplorationData" or "FetchRemoteModule" or "ModuleStore" or "ModuleRetrieve" or "MassModuleStore"
                    or "ShipyardTransfer" or "RestockVehicle" or "BookTaxi" or "BookDropship" => -Number(data, "Cost"),
                "CancelTaxi" or "CancelDropship" => Number(data, "Refund"),
                _ => 0
            };
            try { credits = checked(credits + delta); }
            catch (OverflowException) { warnings.Add(AppText.Get("JournalSnapshotReader.Text033")); }
        }

        private MutableShip? SetActive(JsonElement data, string idField, string typeField)
        {
            var ship = GetShip(data, idField, typeField);
            if (ship is not null) activeShip = ship.Id;
            return ship;
        }
        private MutableShip? GetShip(JsonElement data, string idField, string typeField)
        {
            var id = Id(data, idField); var type = Symbol(Text(data, typeField));
            if (id.Length == 0 || type.Length == 0 || type.Contains("buggy", StringComparison.OrdinalIgnoreCase)
                || type.Contains("fighter", StringComparison.OrdinalIgnoreCase) || type.Contains("srv", StringComparison.OrdinalIgnoreCase)
                || type.Contains("suit", StringComparison.OrdinalIgnoreCase) || type.Contains("taxi", StringComparison.OrdinalIgnoreCase)
                || Bool(data, "Taxi", false) || type == "unknown") return null;
            if (!ships.TryGetValue(id, out var ship)) ships[id] = ship = new(id, type);
            ship.Type = type;
            if (Text(data, "ShipName") is { } shipName) ship.Name = shipName;
            if (Text(data, "ShipIdent") is { } ident) ship.Ident = ident;
            return ship;
        }
        private static ModuleSnapshot ReadModule(JsonElement module) => new(Text(module, "Slot") ?? "", Symbol(Text(module, "Item")),
            (int)Number(module, "Priority"), Bool(module, "On"), Fraction(module, "Health"), Number(module, "Value"),
            module.ValueKind == JsonValueKind.Object && module.TryGetProperty("Engineering", out var engineering) ? ReadEngineering(engineering) : null);
        private static JsonObject? ReadEngineering(JsonElement engineering)
        {
            if (engineering.ValueKind != JsonValueKind.Object) return null;
            var result = new JsonObject();
            // All engineering keys present in local 2026 Loadout events; credentials and unrelated event data are excluded.
            foreach (var property in new[] { "Engineer", "EngineerID", "BlueprintID", "BlueprintName", "Level", "Quality", "ExperimentalEffect", "ExperimentalEffect_Localised" })
                if (engineering.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    result[property] = JsonNode.Parse(value.GetRawText());
            if (engineering.TryGetProperty("Modifiers", out var modifiers) && modifiers.ValueKind == JsonValueKind.Array)
            {
                var items = new JsonArray();
                foreach (var modifier in modifiers.EnumerateArray())
                {
                    if (modifier.ValueKind != JsonValueKind.Object) continue;
                    var item = new JsonObject();
                    foreach (var property in new[] { "Label", "Value", "ValueStr", "OriginalValue", "LessIsGood" })
                        if (modifier.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                            item[property] = JsonNode.Parse(value.GetRawText());
                    items.Add(item);
                }
                result["Modifiers"] = items;
            }
            return result.Count == 0 ? null : result;
        }
        private static void PutModule(MutableShip ship, ModuleSnapshot module)
        { if (module.Slot.Length > 0 && module.ItemId.Length > 0 && module.ItemId != "null") ship.Modules[module.Slot] = module; }
        private void ChangeModule(JsonElement data, string slotField, string itemField)
        {
            var ship = GetShip(data, "ShipID", "Ship");
            if (ship is null) return;
            PutModule(ship, new(Text(data, slotField) ?? "", Symbol(Text(data, itemField)), 0, true, 1, Number(data, "BuyPrice")));
        }
        private void CargoDelta(string? raw, string? displayName, long delta)
        {
            var id = Symbol(raw); if (id.Length == 0) return;
            cargo.TryGetValue(id, out var old);
            var count = Math.Max(0, (old?.Quantity ?? 0) + delta);
            if (count == 0) cargo.Remove(id); else cargo[id] = new(id, displayName ?? old?.Name ?? raw ?? id, count);
        }
        private void ReplaceMaterials(JsonElement data, string array, string category)
        {
            if (!data.TryGetProperty(array, out var items) || items.ValueKind != JsonValueKind.Array) return;
            foreach (var key in materials.Where(kv => kv.Value.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToArray()) materials.Remove(key);
            foreach (var item in items.EnumerateArray()) MaterialDelta(Text(item, "Name"), Text(item, "Name_Localised"), Number(item, "Count"), category);
        }
        private void MaterialDelta(string? raw, string? displayName, long delta, string category)
        {
            var id = Symbol(raw); if (id.Length == 0) return;
            var key = category + ":" + id;
            materials.TryGetValue(key, out var old);
            var count = Math.Max(0, (old?.Quantity ?? 0) + delta);
            if (count == 0) materials.Remove(key); else materials[key] = new(id, displayName ?? old?.Name ?? raw ?? id, count, category);
        }
        public CommanderSnapshot Snapshot(SnapshotSource source) => new(name, credits,
            cargo.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray(),
            materials.Values.OrderBy(x => x.Category, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray(),
            ships.Values.OrderBy(x => x.Id, StringComparer.Ordinal).Select(s => new ShipSnapshot(s.Id, s.Type, s.Name, s.Ident, s.Id == activeShip,
                s.Modules.Values.OrderBy(m => m.Slot, StringComparer.Ordinal).ToArray())).ToArray(), source);
        private sealed class MutableShip(string id, string type)
        {
            public string Id { get; } = id;
            public string Type { get; set; } = type;
            public string Name { get; set; } = "";
            public string Ident { get; set; } = "";
            public Dictionary<string, ModuleSnapshot> Modules { get; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}

internal static class JournalNumbers
{
    public static bool TryGetInt64Safe(this JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }
}
