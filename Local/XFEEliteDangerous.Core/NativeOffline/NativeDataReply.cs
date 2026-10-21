using System.Text.Json;

namespace XFEEliteDangerous.Core;

/// <summary>Completion understood by the original native parser, delivered in-process without a transport.</summary>
public sealed record NativeDataReply(int Status, string Payload)
{
    public static NativeDataReply Json(object value, int status = 200) => new(status, NativeDataJson.Serialize(value));
}
internal sealed class NativeDataException(int status, string message) : InvalidOperationException(message)
{
    internal int Status { get; } = status;
}
