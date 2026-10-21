using System.Globalization;
using System.Text.Json;

namespace XFEEliteDangerous.Core;

internal static class NativeJson
{
    internal static JsonDocument Parse(byte[] body)
    {
        var document=JsonDocument.Parse(body,new(){MaxDepth=24});
        try {Unique(document.RootElement);return document;}catch{document.Dispose();throw;}
    }
    private static void Unique(JsonElement node)
    {
        if(node.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in node.EnumerateObject())
            {if(!names.Add(property.Name))throw new InvalidDataException("Duplicate native JSON field.");Unique(property.Value);}
        }
        else if(node.ValueKind==JsonValueKind.Array)foreach(var item in node.EnumerateArray())Unique(item);
    }
    internal static void Fields(JsonElement value,params string[] allowed)
    {
        if(value.ValueKind!=JsonValueKind.Object||value.EnumerateObject().Any(property=>!allowed.Contains(property.Name,StringComparer.Ordinal)))
            throw new InvalidDataException("Unsupported native JSON fields.");
    }
    internal static JsonElement Field(JsonElement value,string name)=>value.TryGetProperty(name,out var found)?found:throw new InvalidDataException("Missing native JSON field.");
    internal static ulong U64(JsonElement value,string name)
    {var field=Field(value,name);return field.ValueKind==JsonValueKind.Number&&field.TryGetUInt64(out var number)?number:throw new InvalidDataException("Expected native unsigned integer.");}
    internal static uint U32(JsonElement value,string name)
    {var number=U64(value,name);return number<=uint.MaxValue?(uint)number:throw new InvalidDataException("Native integer exceeds UInt32.");}
    internal static bool Boolean(JsonElement value,string name)
    {var field=Field(value,name);return field.ValueKind is JsonValueKind.True or JsonValueKind.False?field.GetBoolean():throw new InvalidDataException("Expected native Boolean.");}
    internal static string Text(JsonElement value,string name,int maximum)
    {
        var field=Field(value,name);
        if(field.ValueKind!=JsonValueKind.String||field.GetString() is not {} text||text.Length>maximum||text.Any(char.IsControl))
            throw new InvalidDataException("Expected bounded native text.");return text;
    }
}
