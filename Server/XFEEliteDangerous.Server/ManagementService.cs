using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.Client;
using XFEExtension.NetCore.ServerInteractive.Attributes;
using XFEExtension.NetCore.ServerInteractive.Implements.CoreService;

namespace XFEEliteDangerous.Server;

public partial class ManagementService : ServerCoreStandardServiceBase
{
    private static OfflineWorkspace workspace=null!;
    private static ManagementCommands commands=null!;
    private static ServerConfiguration configuration=null!;
    private static string localKey="";
    private static Action stop=()=>{};
    internal static Func<string,Func<Task<JsonNode>>,Task<JsonNode>> EditOnlineIdle=(_,edit)=>edit();
    internal static Func<string?,JsonObject,Func<Task<JsonNode>>,Task<JsonNode>> Refit=(token,input,idle)=>idle();
    public static void Configure(OfflineWorkspace data,ServerConfiguration options,string key,Action shutdown)
    {workspace=data;commands=new(data);configuration=options;localKey=key;stop=shutdown;OfflineDiagnostics.Current?.RegisterSecret(key);}
    [EntryPoint("health"),NoLog]
    public async Task Health(){await Reply(new JsonObject{["status"]="ready",["framework"]="XFEExtension.NetCore.ServerInteractive",["version"]=1,["workspace"]=configuration.Workspace,["pid"]=Environment.ProcessId},200);}
    [EntryPoint("shutdown"),NoLog]
    public async Task Shutdown()
    {
        using var language = AppText.UseLanguage(Args.RequestHeaders["Accept-Language"]);
        var header=Args.RequestHeaders["Authorization"];
        if(Args.RequestMethod!="POST"||!Wire.Equal(header,"Bearer "+localKey)){await Reply(new JsonObject{["error"]=AppText.Get("ManagementService.Text427")},401);return;}
        await Reply(new JsonObject{["stopping"]=true},200);stop();
    }
    [EntryPoint("control"),NoLog]
    public async Task Control()
    {
        using var language = AppText.UseLanguage(Args.RequestHeaders["Accept-Language"]);
        var operation="unparsed";var status=200;
        using var diagnostic=OfflineDiagnostics.Current?.Begin("management","request",new Dictionary<string,string?>{["correlationId"]=Args.CorrelationId});
        try
        {
            if(Args.RequestMethod!="POST"){await Reply(new JsonObject{["error"]=AppText.Get("ManagementService.Text428")},405);return;}
            var body=JsonNode.Parse(Args.RequestBody??"",documentOptions:new(){MaxDepth=64})?.AsObject()??throw new InvalidDataException(AppText.Get("ManagementService.Text429"));
            if(body.Count!=2||body["arguments"] is not JsonObject input)throw new InvalidDataException(AppText.Get("ManagementService.Text430"));
            operation=body["operation"]?.GetValue<string>()??throw new InvalidDataException(AppText.Get("ManagementService.Text431"));
            var header=Args.RequestHeaders["Authorization"];
            var token=header?.StartsWith("Bearer ",StringComparison.Ordinal)==true?header[7..]:null;
            if(token is not null)OfflineDiagnostics.Current?.RegisterSecret(token);
            JsonNode result;
            if(operation=="auth/local-owner")
            {
                if(!Wire.Equal(token,localKey))throw new LocalSessionException(AppText.Get("ManagementService.Text427"));
                result=await workspace.OpenLocalOwnerSessionAsync();
            }
            else
            {
                if(operation=="fleet/refit")
                {
                    result=await Refit(token,input,async()=>
                    {
                        var actor=await workspace.ProfileAsync(token);
                        var account=input["accountId"]?.GetValue<string>()??actor["id"]!.GetValue<string>();
                        CheckNativeGameIdle();
                        return await EditOnlineIdle(account,async()=>
                        {
                            if(input["action"]?.GetValue<string>()=="properties")
                            {
                                var values=(JsonObject)input.DeepClone();values["shipId"]=input["shipId"]!.ToString();
                                return await workspace.EditAsync(token,account,InventoryEdit.Modules,values,input["version"]!.GetValue<string>());
                            }
                            if(input["action"]?.GetValue<string>() is "engineering" or "ammunition")
                                return await workspace.SaveIdleRefitAsync(token,account,input,input["version"]!.GetValue<string>());
                            return await commands.ExecuteAsync("fleet/edit",token,input);
                        });
                    });
                }
                else if(operation is "inventory/edit" or "fleet/edit" or "materials/prepare" or "native/import" or "native/initialize" or "journal/import")
                {
                    var actor=await workspace.ProfileAsync(token);
                    var account=input["accountId"]?.GetValue<string>()??actor["id"]!.GetValue<string>();
                    CheckNativeGameIdle();
                    result=await EditOnlineIdle(account,()=>commands.ExecuteAsync(operation,token,input));
                }
                else result=await commands.ExecuteAsync(operation,token,input);
                if(operation=="status")
                {
                    result["networkTransport"]=true;result["managementTransport"]="loopback-http";
                    result["gameTransport"]=configuration.Online is {Enabled:true}?"launcher-https":"in-process";
                    result["nativeMultiplayerValidated"]=OnlineGameService.NativeMultiplayerValidated;
                    result["nativeMultiplayerValidationScope"]=OnlineGameService.NativeValidationScope;
                }
            }
            await Reply(result,200);diagnostic?.Complete("completed");
        }
        catch(Exception error)
        {
            status=error switch{LocalSessionException=>401,UnauthorizedAccessException=>403,SaveConflictException or ManagementBusyException=>409,
                JsonException or InvalidDataException or ArgumentException or OverflowException or InvalidOperationException=>400,_=>500};
            OfflineDiagnostics.Current?.Write(status==500?"error":"warning","management","request.rejected",error.Message,new Dictionary<string,string?>{["operation"]=operation,["status"]=status.ToString()},error);
            await Reply(new JsonObject{["error"]=status==500?AppText.Get("ManagementService.Text432"):error.Message,
                ["code"]=error is SaveConflictException?"save_conflict":"request_rejected",["correlationId"]=Args.CorrelationId},status);
            diagnostic?.Complete("rejected");
        }
    }
    private async Task Reply(JsonNode data,int status)
    {
        Handled=true;await Args.ReplyAndClose(data.ToJsonString(),(HttpStatusCode)status,"application/json; charset=utf-8");
    }
    private static void CheckNativeGameIdle()
    {
        if(configuration.ArtifactsRoot is not {} root||!Directory.Exists(root))return;
        foreach(var directory in Directory.EnumerateDirectories(root,"offline-game-*"))
        {
            var receipt=Path.Combine(directory,"launch.json");if(!File.Exists(receipt))continue;
            JsonObject launch;try{launch=JsonNode.Parse(File.ReadAllText(receipt))!.AsObject();}catch(IOException){continue;}
            if(!string.Equals(Path.GetFullPath(launch["workspace"]!.GetValue<string>()),configuration.Workspace,StringComparison.OrdinalIgnoreCase))continue;
            try
            {
                using var process=System.Diagnostics.Process.GetProcessById(launch["ProcessId"]!.GetValue<int>());
                if(process.HasExited||Math.Abs((process.StartTime.ToUniversalTime()-File.GetLastWriteTimeUtc(receipt)).TotalSeconds)>15
                    ||!string.Equals(process.MainModule?.FileName,@"E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe",StringComparison.OrdinalIgnoreCase))continue;
                if(launch["nativeManagementSchema"]?.GetValue<int>()!=1)throw new ManagementBusyException(AppText.Get("ManagementService.Text433"));
                var flow=Path.Combine(directory,"logs","native-game-flow.tsv");
                if(!File.Exists(flow))throw new ManagementBusyException(AppText.Get("ManagementService.Text434"));
                using var stream=new FileStream(flow,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                using var reader=new StreamReader(stream);var last=reader.ReadToEnd().Split('\n',StringSplitOptions.RemoveEmptyEntries).LastOrDefault(value=>value.Contains("\tFLOW\t",StringComparison.Ordinal));
                if(last is null||!last.Trim().EndsWith("\tMainMenu\tMainMenu",StringComparison.Ordinal))throw new ManagementBusyException(AppText.Get("ManagementService.Text435"));
            }
            catch(ArgumentException){continue;}
        }
    }
}
internal sealed class ManagementBusyException(string message):InvalidOperationException(message);
