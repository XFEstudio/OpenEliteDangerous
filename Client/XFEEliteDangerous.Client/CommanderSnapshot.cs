using System.Text.Json;
using System.Text.Json.Nodes;

namespace XFEEliteDangerous.Client;

/// <summary>A deliberately limited, credential-free copy of locally observed commander state.</summary>
public sealed record CommanderSnapshot(string Name, long Credits, IReadOnlyList<InventoryItem> Cargo,
    IReadOnlyList<MaterialItem> Materials, IReadOnlyList<ShipSnapshot> Ships, SnapshotSource Source);

public sealed record InventoryItem(string Id, string Name, long Quantity);
public sealed record MaterialItem(string Id, string Name, long Quantity, string Category);
public sealed record ModuleSnapshot(string Slot, string ItemId, int Priority, bool Enabled, double Health, long Value,
    JsonObject? Engineering = null);
public sealed record ShipSnapshot(string Id, string ShipType, string Name, string Ident, bool Active,
    IReadOnlyList<ModuleSnapshot> Modules);
public sealed record SourceFile(string Name, string Sha256, long Length);
public sealed record SnapshotSource(string Kind, DateTimeOffset ExportedAt, DateTimeOffset? LastEventAt,
    string? GameVersion, IReadOnlyList<SourceFile> Files, IReadOnlyList<string> Warnings,
    string Completeness = "local-journal-partial");

public static class ClientJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
