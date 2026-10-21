using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XFEEliteDangerous.Core;

public static class BundledData
{
    private static readonly Lazy<CatalogEntry[]> catalog = new(() => Read("catalog.json")?.Deserialize<CatalogEntry[]>(Wire.Json) ?? []);
    public static IReadOnlyList<CatalogEntry> Catalog => catalog.Value;
    private static readonly Lazy<NativeMaterialStack[]> materials=new(()=>Read("native-materials.json")?.Deserialize<NativeMaterialStack[]>(Wire.Json)??[]);
    public static IReadOnlyList<NativeMaterialStack> NativeMaterials=>materials.Value;
    private static JsonNode? Read(string name)
    {
        var assembly = typeof(BundledData).Assembly;
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(x => x.EndsWith("." + name, StringComparison.Ordinal));
        if (resource is null) return null;
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonNode.Parse(stream);
    }
}
