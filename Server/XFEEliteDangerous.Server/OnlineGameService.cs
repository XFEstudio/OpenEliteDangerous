using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Client;
using XFEEliteDangerous.Core;
using XFEExtension.NetCore.ServerInteractive.Attributes;
using XFEExtension.NetCore.ServerInteractive.Implements.CoreService;

namespace XFEEliteDangerous.Server;

public partial class OnlineGameService : ServerCoreStandardServiceBase
{
    internal const bool NativeMultiplayerValidated=true;
    internal const string NativeValidationScope="pinned-odyssey-two-clients-loopback";
    internal static OnlineGameHost Host=null!;
    private string? Bearer=>Args.RequestHeaders["Authorization"] is {} header&&header.StartsWith("Bearer ",StringComparison.Ordinal)?header[7..]:null;
    [EntryPoint("health"),NoLog]
    public Task Health()=>Send(new JsonObject{["service"]="XFEEliteDangerous.Game",["protocol"]=1,["onlinePlayers"]=Host.Members.Length,
        ["nativeMultiplayerValidated"]=NativeMultiplayerValidated,["nativeMultiplayerValidationScope"]=NativeValidationScope},200);
    [EntryPoint("connect"),NoLog]
    public async Task Connect()=>await Guard(async()=>
    {
        RequirePost();var input=JsonNode.Parse(Args.RequestBody??"",documentOptions:new(){MaxDepth=8})!.AsObject();
        if(input.Any(pair=>pair.Key is not("email" or "password" or "accountId")))throw new InvalidDataException(AppText.Get("OnlineGameService.Text452"));
        var local=IPAddress.TryParse(Args.ClientIP,out var ip)&&IPAddress.IsLoopback(ip);
        var lease=await Host.OpenAsync(Bearer,input["email"]?.GetValue<string>(),input["password"]?.GetValue<string>(),input["accountId"]?.GetValue<string>(),local);
        await Send(JsonSerializer.SerializeToNode(lease,GameLinkJson.Default.OnlineGameLease)!,200);
    });
    [EntryPoint("invoke"),NoLog]
    public async Task Invoke()=>await Guard(async()=>
    {
        RequirePost();var request=JsonSerializer.Deserialize(Args.RequestBody??"",GameLinkJson.Default.GameLinkRequest)??throw new InvalidDataException(AppText.Get("OnlineGameService.Text453"));
        var reply=await Host.ExecuteAsync(Bearer,request);await Send(JsonSerializer.SerializeToNode(reply,GameLinkJson.Default.GameLinkReply)!,200);
    });
    [EntryPoint("disconnect"),NoLog]
    public async Task Disconnect()=>await Guard(async()=>{RequirePost();await Host.CloseAsync(Bearer);await Send(new JsonObject{["closed"]=true},200);});
    private void RequirePost(){if(Args.RequestMethod!="POST")throw new InvalidDataException(AppText.Get("OnlineGameService.Text454"));}
    private async Task Guard(Func<Task> action)
    {
        using var language = AppText.UseLanguage(Args.RequestHeaders["Accept-Language"]);
        try{if(Bearer is {} token)OfflineDiagnostics.Current?.RegisterSecret(token);await action();}
        catch(Exception error)
        {
            var status=error switch{LocalSessionException=>401,UnauthorizedAccessException=>403,SaveConflictException=>409,
                JsonException or ArgumentException or InvalidDataException or OverflowException=>400,InvalidOperationException=>409,_=>500};
            OfflineDiagnostics.Current?.Write(status==500?"error":"warning","online.api","request.rejected","Online game request rejected.",new Dictionary<string,string?>{["correlationId"]=Args.CorrelationId,["status"]=status.ToString()},error);
            await Send(new JsonObject{["error"]=status==500?AppText.Get("OnlineGameService.Text455"):error.Message,
                ["code"]=error is SaveConflictException?"save_conflict":"request_rejected",["correlationId"]=Args.CorrelationId},status);
        }
    }
    private async Task Send(JsonNode value,int status){Handled=true;await Args.ReplyAndClose(value.ToJsonString(),(HttpStatusCode)status,"application/json; charset=utf-8");}
}
