using System.Collections.Concurrent;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Core;

/// <summary>Runs inside the isolated game. The launcher owns the actual network connection.</summary>
public sealed class OnlineNativeGameData : INativeGameData,INativeWorldAuthority
{
    private readonly NativeGameBridge data,world;
    private readonly CancellationTokenSource stop=new();
    private readonly ConcurrentQueue<GameDatagram> outgoing=new();
    private readonly ConcurrentDictionary<ushort,ConcurrentQueue<OfflineLocalPacket>> incoming=new();
    private readonly Task worker;
    private int pending;
    private Exception? failure;
    public INativeWorldAuthority World=>this;
    public Action<string>? Diagnostic{get;set;}
    public OnlineNativeGameData(string bridgeDirectory)
    {
        data=NativeGameBridge.Open(Path.Combine(bridgeDirectory,"data.bridge"));
        try{world=NativeGameBridge.Open(Path.Combine(bridgeDirectory,"world.bridge"),true);}catch{data.Dispose();throw;}
        worker=Task.Run(PumpAsync);
    }
    public Task<NativeDataReply> InvokeAsync(string method,string operation,IReadOnlyDictionary<string,string> fields,byte[] body)
    {
        if(failure is not null)return Task.FromResult(new NativeDataReply(503,AppText.Get("OnlineNativeGameData.Text360")));
        var response=data.Call(new("data",method,operation,new(fields),body));
        return Task.FromResult(new NativeDataReply(response.Status,response.Payload));
    }
    public bool CanRoute(ushort port)=>port is 46001 or 46002 or >=47000 and <=57000;
    public IReadOnlyList<OfflineLocalPacket> Receive(ushort clientPort,ushort targetPort,ReadOnlySpan<byte> packet)
    {
        if(failure is not null||packet.Length is <8 or >1472||!CanRoute(targetPort))return [];
        incoming.GetOrAdd(clientPort,_=>new());
        if(Interlocked.Increment(ref pending)>256){Interlocked.Decrement(ref pending);throw new IOException(AppText.Get("OnlineNativeGameData.Text361"));}
        outgoing.Enqueue(new(clientPort,targetPort,packet.ToArray()));return [];
    }
    public IReadOnlyList<OfflineLocalPacket> Poll(ushort clientPort)
    {
        if(!incoming.TryGetValue(clientPort,out var queue))return [];
        var result=new List<OfflineLocalPacket>();while(result.Count<128&&queue.TryDequeue(out var item))result.Add(item);return result;
    }
    private async Task PumpAsync()
    {
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var batch=new List<GameDatagram>();while(batch.Count<64&&outgoing.TryDequeue(out var item)){Interlocked.Decrement(ref pending);batch.Add(item);}
                var response=world.Call(new("exchange",Datagrams:batch.ToArray()),TimeSpan.FromSeconds(10));
                if(response.Status!=200)throw new IOException(AppText.Get("OnlineNativeGameData.Text362")+response.Status);
                foreach(var packet in response.Datagrams??[])
                {
                    if(packet.Payload.Length>1472||!CanRoute(packet.Port))throw new InvalidDataException(AppText.Get("OnlineNativeGameData.Text363"));
                    var queue=incoming.GetOrAdd(packet.ClientPort,_=>new());if(queue.Count>=256)throw new IOException(AppText.Get("OnlineNativeGameData.Text364"));
                    queue.Enqueue(new(packet.Port,packet.Payload));
                }
                await Task.Delay(10,stop.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception error){failure=error;OfflineDiagnostics.Current?.Write("error","online.bridge","connection.failed","Authoritative game connection failed; no fallback.",exception:error);Diagnostic?.Invoke("ONLINE_DISCONNECTED");}
    }
    public async ValueTask DisposeAsync(){stop.Cancel();await worker.ConfigureAwait(false);data.Dispose();world.Dispose();stop.Dispose();}
}
