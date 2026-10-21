using XFEEliteDangerous.Client;
using XFEEliteDangerous.Core;

namespace XFEEliteDangerous.OnlineClient;

/// <summary>Owned by one launcher; its isolated game never receives a socket or network capability.</summary>
public sealed class OnlineBridgeHost : IAsyncDisposable
{
    private readonly OnlineConnection connection;
    private readonly NativeGameBridge data,world;
    private readonly CancellationTokenSource stop=new();
    private readonly Task[] workers;
    public OnlineBridgeHost(string directory,OnlineConnection connection)
    {
        this.connection=connection;Directory.CreateDirectory(directory);
        data=NativeGameBridge.Create(Path.Combine(directory,"data.bridge"));
        try{world=NativeGameBridge.Create(Path.Combine(directory,"world.bridge"),true);}catch{data.Dispose();throw;}
        workers=[Task.Run(()=>PumpAsync(data)),Task.Run(()=>PumpAsync(world))];
    }
    private async Task PumpAsync(NativeGameBridge bridge)
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                if(!bridge.TryRead(out var id,out var request)){await Task.Delay(1,stop.Token).ConfigureAwait(false);continue;}
                GameLinkReply reply;
                try{reply=await connection.InvokeAsync(request!,stop.Token).ConfigureAwait(false);}
                catch(Exception error)when(error is IOException or HttpRequestException or OperationCanceledException or InvalidDataException)
                {
                    OfflineDiagnostics.Current?.Write("error","online.launcher","request.failed","Authoritative server request failed; no local or official fallback.",exception:error);
                    reply=new(503,AppText.Get("OnlineBridgeHost.Text042"));
                }
                bridge.Reply(id,reply);
            }
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception error){OfflineDiagnostics.Current?.Write("critical","online.launcher","bridge.failed","Native IPC bridge failed.",exception:error);}
    }
    public async ValueTask DisposeAsync(){stop.Cancel();await Task.WhenAll(workers).ConfigureAwait(false);data.Dispose();world.Dispose();stop.Dispose();}
}
