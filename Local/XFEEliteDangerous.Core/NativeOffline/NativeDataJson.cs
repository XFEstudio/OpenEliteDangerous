using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace XFEEliteDangerous.Core;

/// <summary>
/// Write-only native DTO encoding. NativeAOT cannot synthesize constructor converters for
/// anonymous response projections. These objects are never deserialized; write their rooted
/// public getters directly and keep persisted models on the generated CoreJsonContext.
/// </summary>
internal static class NativeDataJson
{
    internal static string Serialize(object? value)
    {
        using var output=new MemoryStream();using(var writer=new Utf8JsonWriter(output))Write(writer,value,0);
        if(output.Length>8*1024*1024)throw new InvalidDataException("Native completion exceeds its bound.");
        return Encoding.UTF8.GetString(output.GetBuffer().AsSpan(0,checked((int)output.Length)));
    }
    private static void Write(Utf8JsonWriter writer,object? value,int depth)
    {
        if(depth>32)throw new InvalidDataException("Native completion is recursive or too deeply nested.");
        switch(value)
        {
            case null:writer.WriteNullValue();return;
            case JsonElement element:element.WriteTo(writer);return;
            case JsonNode node:node.WriteTo(writer);return;
            case string text:writer.WriteStringValue(text);return;
            case bool boolean:writer.WriteBooleanValue(boolean);return;
            case byte number:writer.WriteNumberValue(number);return;
            case sbyte number:writer.WriteNumberValue(number);return;
            case short number:writer.WriteNumberValue(number);return;
            case ushort number:writer.WriteNumberValue(number);return;
            case int number:writer.WriteNumberValue(number);return;
            case uint number:writer.WriteNumberValue(number);return;
            case long number:writer.WriteNumberValue(number);return;
            case ulong number:writer.WriteNumberValue(number);return;
            case float number:writer.WriteNumberValue(number);return;
            case double number:writer.WriteNumberValue(number);return;
            case decimal number:writer.WriteNumberValue(number);return;
            case DateTimeOffset date:writer.WriteStringValue(date);return;
            case DateTime date:writer.WriteStringValue(date);return;
            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach(DictionaryEntry pair in dictionary)
                {writer.WritePropertyName(Convert.ToString(pair.Key,CultureInfo.InvariantCulture)??throw new InvalidDataException("Null native key."));Write(writer,pair.Value,depth+1);}
                writer.WriteEndObject();return;
            case IEnumerable sequence:
                writer.WriteStartArray();var count=0;
                foreach(var item in sequence){if(++count>200000)throw new InvalidDataException("Native sequence exceeds its bound.");Write(writer,item,depth+1);}
                writer.WriteEndArray();return;
        }
        var type=value.GetType();
        if(type.Assembly!=typeof(NativeDataJson).Assembly)throw new InvalidDataException("Unrecognized native completion type.");
        writer.WriteStartObject();
        foreach(var property in type.GetProperties(BindingFlags.Public|BindingFlags.Instance))
        {
            if(property.GetMethod is null||property.GetIndexParameters().Length!=0||property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition==JsonIgnoreCondition.Always)continue;
            writer.WritePropertyName(property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name??JsonNamingPolicy.CamelCase.ConvertName(property.Name));
            Write(writer,property.GetValue(value),depth+1);
        }
        writer.WriteEndObject();
    }
}
