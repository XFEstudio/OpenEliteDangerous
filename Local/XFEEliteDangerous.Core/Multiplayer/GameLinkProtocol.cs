using System.Text.Json.Serialization;

namespace XFEEliteDangerous.Core;

public sealed record GameDatagram(ushort ClientPort,ushort Port,byte[] Payload);
public sealed record GameLinkRequest(string Kind,string? Method=null,string? Path=null,Dictionary<string,string>? Fields=null,byte[]? Body=null,GameDatagram[]? Datagrams=null);
public sealed record GameLinkReply(int Status,string Payload="",GameDatagram[]? Datagrams=null);
public sealed record OnlineServerProfile(string Address,string CertificateSha256);
public sealed record OnlineGameLease(string Token,string AccountId,ulong RunId,string Commander,uint NativeUserId);
public sealed record OnlineLaunch(OnlineServerProfile Server,OnlineGameLease Lease);
[JsonSourceGenerationOptions(PropertyNamingPolicy=JsonKnownNamingPolicy.CamelCase,GenerationMode=JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(GameLinkRequest))]
[JsonSerializable(typeof(GameLinkReply))]
[JsonSerializable(typeof(OnlineServerProfile))]
[JsonSerializable(typeof(OnlineGameLease))]
[JsonSerializable(typeof(OnlineLaunch))]
public partial class GameLinkJson : JsonSerializerContext;
