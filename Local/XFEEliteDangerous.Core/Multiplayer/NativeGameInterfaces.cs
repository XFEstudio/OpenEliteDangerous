namespace XFEEliteDangerous.Core;

/// <summary>Native adapters depend on this boundary; only the launcher owns network access.</summary>
public interface INativeGameData : IAsyncDisposable
{
    INativeWorldAuthority World { get; }
    Task<NativeDataReply> InvokeAsync(string method,string operation,IReadOnlyDictionary<string,string> fields,byte[] body);
}

public interface INativeWorldAuthority
{
    Action<string>? Diagnostic { get; set; }
    bool CanRoute(ushort port);
    IReadOnlyList<OfflineLocalPacket> Receive(ushort clientPort,ushort targetPort,ReadOnlySpan<byte> packet);
    IReadOnlyList<OfflineLocalPacket> Poll(ushort clientPort);
}
