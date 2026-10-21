using System.Text.Json.Serialization;

namespace XFEEliteDangerous.Client;

// Only UTF-8 JSON and owned native buffers cross the two NativeAOT runtimes.
// No managed references, handles, delegates or mutable world state cross this ABI.
public sealed record NativeReadModuleRequest(int Protocol, string Operation, Dictionary<string,string> Fields,
    ulong CurrentSystem, long LoanCeiling);
public sealed record NativeReadModuleReply(int Protocol, int Status, string Payload,
    string? ErrorType = null, string? Error = null, string? StackTrace = null);

// The fixed host admits the authenticated controller before invoking this pure
// RPC handler. Fresh local identities survive module replacement. Transport,
// cryptographic keys, receive windows and durable state stay in the host.
public sealed record NativeRpcModuleRequest(int Protocol,string Role,byte LetterType,bool Flag,bool AlignedValues,
    int PayloadBits,byte[] Payload,ulong CurrentSystem,ulong ClientMachine,ulong IslandId,ulong WakeId);
public sealed record NativeRpcEmission(byte LetterType,bool Flag,int PayloadBits,byte[] Payload,bool Reliable=true);
public sealed record NativeRpcModuleReply(int Protocol,bool Handled,NativeRpcEmission[] Emissions,
    string? ErrorType=null,string? Error=null,string? StackTrace=null);

// A save handler receives a detached snapshot and returns a candidate. Only the
// fixed host validates protected fields, assigns the revision and commits it.
public sealed record NativeSaveModuleRequest(int Protocol,uint NativeUserId,string CommanderId,long Revision,
    bool Selected,string SaveJson,byte[] Body);
public sealed record NativeSaveModuleReply(int Protocol,int Status,string Payload,string? SaveJson=null,
    string? ErrorType=null,string? Error=null,string? StackTrace=null);

[JsonSerializable(typeof(NativeReadModuleRequest))]
[JsonSerializable(typeof(NativeReadModuleReply))]
[JsonSerializable(typeof(NativeRpcModuleRequest))]
[JsonSerializable(typeof(NativeRpcModuleReply))]
[JsonSerializable(typeof(NativeSaveModuleRequest))]
[JsonSerializable(typeof(NativeSaveModuleReply))]
public partial class NativeReadModuleJson : JsonSerializerContext;
