using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace XFEEliteDangerous.Core;

// NativeAOT must have constructors and property metadata for every persisted shape.
// Keeping an assembly alone does not instantiate JsonPropertyInfo<Nullable<T>>.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(DatabaseState))]
[JsonSerializable(typeof(SharedGalaxyState))]
[JsonSerializable(typeof(NativeCommanderSave))]
[JsonSerializable(typeof(NativeMaterialStack[]))]
[JsonSerializable(typeof(CatalogEntry[]))]
[JsonSerializable(typeof(Permissions))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(NativeSidewinderSaveFactory.Metadata))]
internal partial class CoreJsonContext : JsonSerializerContext;
