using System.Text.Json;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.Client;
using XFEEliteDangerous.Server;
using XFEExtension.NetCore.ServerInteractive.Utilities.Extensions;
using XFEExtension.NetCore.ServerInteractive.Utilities.Server;
using XFEExtension.NetCore.CyberComm;

var originalError=Console.Error;
string? failureLog=null;
try
{
    args = AppText.ConfigureArguments(args);
    var options=ServerConfiguration.Load(args);
    Directory.CreateDirectory(options.DataRoot);
    Directory.SetCurrentDirectory(options.DataRoot);
    var runId="management-"+Guid.NewGuid().ToString("N");
    Directory.CreateDirectory(Path.Combine(options.DataRoot,"logs"));
    failureLog=Path.Combine(options.DataRoot,"logs","startup-errors.log");
    using var consoleLog=options.Background?TextWriter.Synchronized(File.AppendText(Path.Combine(options.DataRoot,"logs",runId+".console.log"))):null;
    if(consoleLog is not null){Console.SetOut(consoleLog);Console.SetError(consoleLog);}
    using var diagnostics=new OfflineDiagnosticLog(Path.Combine(options.DataRoot,"logs",runId),"management",runId);
    OfflineDiagnostics.Current=diagnostics;
    await using var workspace=new OfflineWorkspace(options.Workspace);
    var access=LocalAdministratorAccess.Open(options.DataRoot,options.Address);
    await using var online=new OnlineGameHost(workspace,access.Key,options.ArtifactsRoot);
    OnlineGameService.Host=online;
    ManagementService.EditOnlineIdle=online.EditIdleAsync;
    using var onlineCertificate=options.Online is {Enabled:true} onlineOptions?onlineOptions.OpenCertificate(options.DataRoot):null;
    if(options.Online is {Enabled:true})await workspace.InitializeSharedGalaxyAsync();
    using var shutdown=new CancellationTokenSource();
    ManagementService.Configure(workspace,options,access.Key,shutdown.Cancel);
    var builder=XFEServerBuilder.CreateBuilder().UseXFEServer()
        .AddServerCore(XFEServerCoreBuilder.CreateBuilder().AddService<ManagementService>().Build(core=>
        {
            core.ServerCoreName="XFEEliteDangerous.Management";core.MainEntryPoint="api";
            core.AcceptGet=true;core.AcceptPost=true;core.AcceptNonStandardJson=false;
            core.BindIP($"http://127.0.0.1:{options.Port}/");
            core.TransportLimits=new(){MaxHttpBodyBytes=8*1024*1024};
        }));
    if(options.Online is {Enabled:true} gameOptions)
    {
        if(gameOptions.Port==options.Port)throw new InvalidDataException(AppText.Get("Program.Text457"));
        builder.AddServerCore(XFEServerCoreBuilder.CreateBuilder().AddService<OnlineGameService>().Build(core=>
        {
            core.ServerCoreName="XFEEliteDangerous.Game";core.MainEntryPoint="game";core.AcceptGet=true;core.AcceptPost=true;core.AcceptNonStandardJson=false;
            core.BindIP($"https://{gameOptions.BindAddress}:{gameOptions.Port}/");core.Tls=new(){Certificate=onlineCertificate};core.TransportLimits=new(){MaxHttpBodyBytes=16*1024*1024};
        }));
    }
    await using var server=builder.Build();
    ConsoleCancelEventHandler cancelHandler=(_,eventArgs)=>{eventArgs.Cancel=true;shutdown.Cancel();};
    EventHandler exitHandler=(_,_)=>shutdown.Cancel();
    Console.CancelKeyPress+=cancelHandler;
    AppDomain.CurrentDomain.ProcessExit+=exitHandler;
    Console.WriteLine(AppText.Get("Program.Text458")+options.Address);
    Console.WriteLine(AppText.Get("Program.Text459")+options.Workspace);
    Console.WriteLine(AppText.Get("Program.Text460")+Path.Combine(options.DataRoot,"logs"));
    diagnostics.Write("info","management","server.start","Local ServerInteractive management host starting.",new Dictionary<string,string?>{["address"]=options.Address,["workspace"]=options.Workspace});
    try{await server.RunAsync(shutdown.Token);}catch(OperationCanceledException)when(shutdown.IsCancellationRequested){}
    finally{Console.CancelKeyPress-=cancelHandler;AppDomain.CurrentDomain.ProcessExit-=exitHandler;OfflineDiagnostics.Current=null;}
    return 0;
}
catch(Exception error)
{
    if(failureLog is not null)try{File.AppendAllText(failureLog,DateTimeOffset.UtcNow.ToString("O")+" "+error+Environment.NewLine);}catch(IOException){}
    try{originalError.WriteLine(error.ToString());}catch(IOException){}
    return 1;
}
