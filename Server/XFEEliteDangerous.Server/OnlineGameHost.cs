using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using XFEEliteDangerous.Client;
using XFEEliteDangerous.Core;

namespace XFEEliteDangerous.Server;

/// <summary>The existing server owns all online data and native sessions.</summary>
public sealed class OnlineGameHost : IAsyncDisposable
{
    private sealed class Session(OnlineGameLease lease,OfflineGameData data)
    {
        internal readonly OnlineGameLease Lease=lease;
        internal readonly OfflineGameData Data=data;
        internal readonly SemaphoreSlim Gate=new(1,1);
        internal readonly HashSet<ushort> Channels=[];
        internal DateTimeOffset LastSeen=DateTimeOffset.UtcNow;
        internal readonly DateTimeOffset Expires=DateTimeOffset.UtcNow.AddHours(12);
        internal bool Closed;
        internal volatile bool Playing;
    }
    private readonly OfflineWorkspace workspace;
    private readonly string localKey;
    private readonly string? artifactsRoot,legacyOfflineAccount;
    private readonly SharedWorldDirectory world=new();
    private readonly ConcurrentDictionary<string,Session> sessions=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,SemaphoreSlim> accountGates=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim opening=new(1,1);
    private readonly CancellationTokenSource stop=new();
    private readonly Task cleanup;
    public OnlineGameHost(OfflineWorkspace workspace,string localKey,string? artifactsRoot=null)
    {
        this.workspace=workspace;this.localKey=localKey;this.artifactsRoot=artifactsRoot;
        // Older receipts did not contain the account ID. Infer it only when
        // there is exactly one runnable native commander, and otherwise fail
        // conservatively for a live matching legacy offline owner.
        using var snapshot=new StateStoreLease(workspace.DirectoryPath);legacyOfflineAccount=snapshot.NativeAccount;
        cleanup=Task.Run(CleanupAsync);
    }
    public SharedWorldMember[] Members=>world.Snapshot();
    public bool AccountActive(string? account)=>sessions.Values.Any(value=>!value.Closed&&value.Playing&&value.Lease.AccountId==account);
    public async Task<JsonNode> EditIdleAsync(string account,Func<Task<JsonNode>> edit)
    {
        var gate=accountGates.GetOrAdd(account,_=>new(1,1));await gate.WaitAsync();
        try{if(AccountActive(account))throw new ManagementBusyException(AppText.Get("OnlineGameHost.Text436"));return await edit();}
        finally{gate.Release();}
    }
    public async Task<OnlineGameLease> OpenAsync(string? bearer,string? email,string? password,string? target,bool local)
    {
        string account;string? loginToken=null;
        try
        {
            if(local&&Wire.Equal(bearer,localKey))account=target??throw new InvalidDataException(AppText.Get("OnlineGameHost.Text437"));
            else
            {
                if(bearer is not null||email is null||password is null)throw new LocalSessionException(AppText.Get("OnlineGameHost.Text438"));
                var login=await workspace.LoginAsync(email,password);loginToken=login["token"]!.GetValue<string>();account=login["account"]!["id"]!.GetValue<string>();
                if(target is not null&&target!=account)throw new UnauthorizedAccessException(AppText.Get("OnlineGameHost.Text439"));
            }
            await opening.WaitAsync();
            try
            {
                EnsureOfflineOwnerReleased(account);
                if(sessions.Values.Any(value=>!value.Closed&&value.Lease.AccountId==account))throw new SaveConflictException();
                if(sessions.Count>=32)throw new InvalidOperationException(AppText.Get("OnlineGameHost.Text440"));
                await using var store=new StateStore(workspace.DirectoryPath);
                var identity=await store.Read(state=>
                {
                    if(!state.Accounts.TryGetValue(account,out var row)||row.Disabled||row.NativeSave is null)throw new InvalidDataException(AppText.Get("OnlineGameHost.Text441"));
                    row.NativeSave.ValidateCurrent(row);return(row.NativeUserId,row.DisplayName);
                });
                var data=new OfflineGameData(workspace.DirectoryPath,account,world);
                try
                {
                    world.Register(data.RunId,identity.NativeUserId);
                    var lease=new OnlineGameLease(Wire.Secret(),account,data.RunId,identity.DisplayName,identity.NativeUserId);
                    OfflineDiagnostics.Current?.RegisterSecret(lease.Token);
                    if(!sessions.TryAdd(lease.Token,new(lease,data)))throw new InvalidOperationException(AppText.Get("OnlineGameHost.Text442"));
                    OfflineDiagnostics.Current?.Write("info","online.session","session.opened","Authenticated private game session created.",new Dictionary<string,string?>{["account"]=account});
                    return lease;
                }
                catch{world.Remove(data.RunId);await data.DisposeAsync();throw;}
            }
            finally{opening.Release();}
        }
        finally{if(loginToken is not null)workspace.Logout(loginToken);}
    }
    private void EnsureOfflineOwnerReleased(string account)
    {
        if(artifactsRoot is null||!Directory.Exists(artifactsRoot))return;
        foreach(var directory in Directory.EnumerateDirectories(artifactsRoot,"offline-game-*"))
        {
            var receipt=Path.Combine(directory,"launch.json");if(!File.Exists(receipt))continue;
            JsonObject launch;try{launch=JsonNode.Parse(File.ReadAllText(receipt))!.AsObject();}catch(Exception error)when(error is IOException or System.Text.Json.JsonException){continue;}
            if(launch["online"]?.GetValue<bool>()==true||!string.Equals(launch["workspace"]?.GetValue<string>(),workspace.DirectoryPath,StringComparison.OrdinalIgnoreCase))continue;
            var owner=launch["accountId"]?.GetValue<string>()??legacyOfflineAccount;if(owner is not null&&owner!=account)continue;
            try
            {
                using var process=System.Diagnostics.Process.GetProcessById(launch["ProcessId"]!.GetValue<int>());
                if(process.HasExited||Math.Abs((process.StartTime.ToUniversalTime()-File.GetLastWriteTimeUtc(receipt)).TotalSeconds)>15
                    ||!string.Equals(process.MainModule?.FileName,@"E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe",StringComparison.OrdinalIgnoreCase))continue;
                throw new ManagementBusyException(AppText.Get("OnlineGameHost.Text443"));
            }
            catch(ArgumentException){continue;}
        }
    }
    private sealed class StateStoreLease : IDisposable
    {
        private readonly StateStore store;
        internal string? NativeAccount{get;}
        internal StateStoreLease(string root)
        {
            store=new(root);var accounts=store.Read(state=>state.Accounts.Values.Where(row=>!row.Disabled&&row.NativeSave is not null).Select(row=>row.Id).Take(2).ToArray()).GetAwaiter().GetResult();
            NativeAccount=accounts.Length==1?accounts[0]:null;
        }
        public void Dispose()=>store.DisposeAsync().GetAwaiter().GetResult();
    }
    public async Task<GameLinkReply> ExecuteAsync(string? token,GameLinkRequest request)
    {
        if(token is null||!sessions.TryGetValue(token,out var session))throw new LocalSessionException(AppText.Get("OnlineGameHost.Text444"));
        await session.Gate.WaitAsync();
        try
        {
            if(session.Closed||session.Expires<=DateTimeOffset.UtcNow)throw new LocalSessionException(AppText.Get("OnlineGameHost.Text445"));
            session.LastSeen=DateTimeOffset.UtcNow;
            if(request.Kind=="data")
            {
                if(request.Method is null||request.Path is null||request.Fields is null||request.Body is null||request.Datagrams is not null)throw new InvalidDataException(AppText.Get("OnlineGameHost.Text446"));
                var accountGate=accountGates.GetOrAdd(session.Lease.AccountId,_=>new(1,1));await accountGate.WaitAsync();
                try
                {
                    var result=await session.Data.InvokeAsync(request.Method,request.Path,request.Fields,request.Body);
                    if(result.Status<300)
                    {
                        if(request.Path=="/2.0/elite/commander/login")session.Playing=true;
                        if(request.Path is "/2.0/elite/commander/logout" or "/2.0/elite/user/logout"){session.Playing=false;world.Leave(session.Lease.RunId);}
                    }
                    return new(result.Status,result.Payload);
                }
                finally{accountGate.Release();}
            }
            if(request.Kind=="exchange")
            {
                if(request.Datagrams is not {} packets||packets.Length>64||request.Body is not null||request.Method is not null||request.Path is not null||request.Fields is not null)throw new InvalidDataException(AppText.Format("OnlineGameHost.Text447", (request.Datagrams?.Length), (request.Body is not null), (request.Method is not null), (request.Path is not null), (request.Fields is not null)));
                if(packets.Any(packet=>packet is null||packet.ClientPort==0||packet.Payload is null||packet.Payload.Length is <8 or >1472||packet.Port is not(46001 or 46002 or >=47000 and <=57000)))throw new InvalidDataException(AppText.Get("OnlineGameHost.Text448"));
                var replies=new List<GameDatagram>();
                foreach(var packet in packets)
                {
                    if(session.Channels.Count>=4&&!session.Channels.Contains(packet.ClientPort))throw new InvalidDataException(AppText.Get("OnlineGameHost.Text449"));
                    session.Channels.Add(packet.ClientPort);
                    if(packet.Port>=47000)world.Route(session.Lease.RunId,packet);
                    else
                    {
                        if(packet.Port==46001)world.BindChannel(session.Lease.RunId,packet.ClientPort);
                        replies.AddRange(session.Data.World.Receive(packet.ClientPort,packet.Port,packet.Payload).Select(reply=>new GameDatagram(packet.ClientPort,reply.SourcePort,reply.Payload)));
                    }
                }
                foreach(var port in session.Channels)replies.AddRange(session.Data.World.Poll(port).Select(reply=>new GameDatagram(port,reply.SourcePort,reply.Payload)));
                replies.AddRange(world.Drain(session.Lease.RunId));
                if(replies.Count>256)throw new InvalidDataException(AppText.Get("OnlineGameHost.Text450"));
                return new(200,Datagrams:replies.ToArray());
            }
            if(request.Kind=="members")return new(200,System.Text.Json.JsonSerializer.Serialize(Members,Wire.Json));
            throw new InvalidDataException(AppText.Get("OnlineGameHost.Text451"));
        }
        finally{session.Gate.Release();}
    }
    public async Task CloseAsync(string? token)
    {
        if(token is null||!sessions.TryRemove(token,out var session))return;
        await session.Gate.WaitAsync();
        try{session.Closed=true;world.Remove(session.Lease.RunId);await session.Data.DisposeAsync();OfflineDiagnostics.Current?.Write("info","online.session","session.closed","Private game session released.",new Dictionary<string,string?>{["account"]=session.Lease.AccountId});}
        finally{session.Gate.Release();}
    }
    private async Task CleanupAsync()
    {
        try{while(!stop.IsCancellationRequested){await Task.Delay(5000,stop.Token);foreach(var entry in sessions.Where(pair=>pair.Value.LastSeen<DateTimeOffset.UtcNow.AddSeconds(-30)||pair.Value.Expires<=DateTimeOffset.UtcNow))await CloseAsync(entry.Key);}}
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        catch(Exception error){OfflineDiagnostics.Current?.Write("error","online.session","cleanup.failed","Online session cleanup failed.",exception:error);}
    }
    public async ValueTask DisposeAsync(){stop.Cancel();await cleanup;foreach(var token in sessions.Keys)await CloseAsync(token);world.Dispose();stop.Dispose();opening.Dispose();}
}
