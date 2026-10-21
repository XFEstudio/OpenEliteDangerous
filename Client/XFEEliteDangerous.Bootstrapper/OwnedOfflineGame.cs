using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.Client;
using XFEEliteDangerous.OnlineClient;

namespace XFEEliteDangerous.Bootstrapper;

/// <summary>Owns only the exact E-copy child. Its AppContainer has zero network capabilities.</summary>
internal sealed class OwnedOfflineGame : IAsyncDisposable
{
    internal const string Executable = @"E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe";
    private const string ImageHash = "e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988";
    private readonly List<IDisposable> leases = [];
    private FileStream? executableLock;
    private Native.ProcessInfo process;
    private nint sid,job;
    private OfflineNativeModule? module;
    private bool stopped;
    private OfflineDiagnosticLog? diagnostics;
    private OnlineBridgeHost? onlineBridge;
    private OnlineConnection? onlineConnection;
    internal string RuntimeDirectory { get; private set; } = "";
    internal uint ProcessId => process.ProcessId;
    internal static async Task<OwnedOfflineGame> CreateAsync(string workspace, string accountId, string library, bool visible,bool probeSolo,OnlineConnection? online=null)
    {
        var game = new OwnedOfflineGame();
        try { game.onlineConnection=online;await game.InitializeAsync(workspace, accountId, library, visible,probeSolo); return game; }
        catch(Exception error) { game.diagnostics?.Write("error","bootstrapper","launch.failed","Owned offline launch failed.",exception:error);await game.DisposeAsync(); throw; }
    }
    private async Task InitializeAsync(string workspace, string accountId, string library, bool visible,bool probeSolo)
    {
        RuntimeDirectory=Path.Combine(SafePaths.ValidateArtifactRoot(null),"offline-game-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RuntimeDirectory);
        var logs=Path.Combine(RuntimeDirectory,"logs");Directory.CreateDirectory(logs);
        diagnostics=new(logs,"bootstrapper",Path.GetFileName(RuntimeDirectory));
        OfflineDiagnostics.Current=diagnostics;
        diagnostics.Write("info","bootstrapper","launch.begin","Preparing owned offline process.",
            new Dictionary<string,string?>{["visible"]=visible.ToString(),["build"]="2026.09.03.332841",["requestedContentMode"]="Odyssey"});
        SafePaths.NoReparseAncestors(Executable); SafePaths.NoReparseAncestors(library); SafePaths.NoReparseAncestors(workspace);
        if(onlineConnection is null){await using var store = new StateStore(workspace);await store.Read(state =>
        {
            if (!state.Accounts.TryGetValue(accountId,out var account)||account.Disabled||account.NativeSave is null)
                throw new InvalidDataException("Select an enabled local account with a complete native save.");
            account.NativeSave.ValidateCurrent(account);return true;
        });}
        if (onlineConnection is null&&System.Diagnostics.Process.GetProcessesByName("EliteDangerous64").Any(value =>
        {
            using (value) { try { return string.Equals(value.MainModule?.FileName, Executable, StringComparison.OrdinalIgnoreCase); } catch { return true; } }
        })) throw new InvalidOperationException("The E-copy game is already running; it was left untouched.");
        executableLock = new(Executable,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(executableLock.Length!=103469056||Convert.ToHexStringLower(await SHA256.HashDataAsync(executableLock))!=ImageHash)
            throw new InvalidDataException("The E-copy version differs from the source-audited build.");
        diagnostics.Write("info","bootstrapper","image.verified","Game image length and pinned hash verified.");
        var payload=Path.Combine(RuntimeDirectory,"XFEEliteDangerous.OfflineRuntime.dll");File.Copy(library,payload,false);
        var symbols=Path.ChangeExtension(library,"pdb");
        if(File.Exists(symbols))File.Copy(symbols,Path.ChangeExtension(payload,"pdb"),false);
        diagnostics.Write("info","bootstrapper","runtime.copied","Native runtime and available symbols copied.");
        var profile=onlineConnection is null?PersistentGameProfile.Open(@"E:\Elite Dangerous"):PersistentGameProfile.OpenOnline(@"E:\Elite Dangerous",onlineConnection.Server,accountId);leases.Add(profile);
        profile.ImportPreviousVisibleSettings();
        foreach(var name in new[]{"LocalAppData","RoamingAppData","SavedGames\\Frontier Developments\\Elite Dangerous","Temp","RenderCache"})
            Directory.CreateDirectory(Path.Combine(profile.DirectoryPath,name));
        _=await OwnedProfileSettings.InitializePersistentAsync(profile);
        sid=profile.CreateOrReuseAppContainer();var sidText=new SecurityIdentifier(sid).Value;
        Grant(RuntimeDirectory,sidText,FileSystemRights.ReadAndExecute);Grant(logs,sidText,FileSystemRights.Modify);
        Grant(profile.DirectoryPath,sidText,FileSystemRights.Modify);
        // The selected workspace is owned local data. Native commits and the desktop use short file leases.
        // ReplaceFileW merges the old file's DACL onto the new file. WRITE_DAC
        // is needed as well as ordinary writes for StateStore's atomic replace.
        if(onlineConnection is null)Grant(workspace,sidText,FileSystemRights.Modify|FileSystemRights.ChangePermissions);
        leases.Add(new DirectoryAccessLease(@"E:\Elite Dangerous",sidText,Path.Combine(RuntimeDirectory,"game-access.json")));
        var desktop=new PrivateDesktopLease(sidText,Guid.NewGuid().ToString("N"),visible);leases.Add(desktop);
        job=Native.CreateJobObjectW(0,null);if(job==0)throw new Win32Exception();
        var limits=new Native.ExtendedLimit{Basic=new Native.BasicLimit{Flags=0x2000|0x400|8,ActiveProcesses=1}};
        if(!Native.SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<Native.ExtendedLimit>()))throw new Win32Exception();
        nint attributes=0,capabilities=0,environment=0;
        try
        {
            nuint size=0;Native.InitializeProcThreadAttributeList(0,1,0,ref size);
            attributes=Marshal.AllocHGlobal((int)size);if(!Native.InitializeProcThreadAttributeList(attributes,1,0,ref size))throw new Win32Exception();
            capabilities=Marshal.AllocHGlobal(Marshal.SizeOf<Native.SecurityCapabilities>());
            Marshal.StructureToPtr(new Native.SecurityCapabilities{AppContainerSid=sid,Capabilities=0,CapabilityCount=0},capabilities,false);
            if(!Native.UpdateProcThreadAttribute(attributes,0,0x00020009,capabilities,(nuint)Marshal.SizeOf<Native.SecurityCapabilities>(),0,0))throw new Win32Exception();
            var startup=new Native.StartupEx{Startup=new Native.Startup{Size=Marshal.SizeOf<Native.StartupEx>(),Desktop=desktop.StartupName},Attributes=attributes};
            var zeros=new string('0',64);
            var command=new StringBuilder('"'+Executable+"\" \"ServerToken "+zeros+" "+zeros+" \" /novr");
            environment=Marshal.StringToHGlobalUni(EnvironmentBlock(profile.DirectoryPath));
            if(!Native.CreateProcessW(Executable,command,0,0,false,Native.Suspended|Native.ExtendedStartup|0x400,environment,Path.GetDirectoryName(Executable)!,ref startup,out process))throw new Win32Exception();
            if(!Native.AssignProcessToJobObject(job,process.Process))throw new Win32Exception();
        }
        finally
        {
            if(environment!=0)Marshal.FreeHGlobal(environment);
            if(attributes!=0){Native.DeleteProcThreadAttributeList(attributes);Marshal.FreeHGlobal(attributes);}
            if(capabilities!=0)Marshal.FreeHGlobal(capabilities);
        }
        VerifyNoNetworkCapabilities();
        diagnostics.Write("info","bootstrapper","process.created","Owned suspended process has zero network capabilities.",
            new Dictionary<string,string?>{["gamePid"]=ProcessId.ToString(),["zeroNetworkCapabilities"]="true"});
        string? bridgeDirectory=null;
        if(onlineConnection is not null)
        {
            bridgeDirectory=Path.Combine(logs,"online-bridge");onlineBridge=new(bridgeDirectory,onlineConnection);
            diagnostics.Write("info","bootstrapper","online.connected","Launcher owns the pinned private server connection; native game retains zero network capabilities.",
                new Dictionary<string,string?>{["server"]=onlineConnection.Server.Address,["account"]=accountId});
        }
        var configuration=JsonSerializer.SerializeToUtf8Bytes(new { WorkspaceDirectory=workspace,AccountId=accountId,Executable,
            ProfileRoot=profile.DirectoryPath,LogDirectory=logs,DriverVersion=await DisplayDriverVersionReader.ReadAsync()??"",ProbeSolo=probeSolo,OnlineBridgeDirectory=bridgeDirectory });
        module=await OfflineNativeModule.LoadAsync(process.Process,payload,configuration.Concat(new byte[]{0}).ToArray());
        if(await module.ReadStatusAsync()!=3)throw new InvalidDataException("The offline runtime is not ready.");
        diagnostics.Write("info","bootstrapper","runtime.initialized","Injected local runtime reported initialized.");
        File.WriteAllText(Path.Combine(RuntimeDirectory,"launch.json"),JsonSerializer.Serialize(new { ProcessId,mode="in-process-offline",
            nativeSoloValidated=false,nativeManagementSchema=1,requestedContentMode="Odyssey",zeroNetworkCapabilities=true,visible,profile=profile.DirectoryPath,workspace,accountId,
            imageSha256=ImageHash,ownedNativeRuntime=payload,online=onlineConnection is not null,server=onlineConnection?.Server.Address },new JsonSerializerOptions{WriteIndented=true}));
    }
    internal void Resume()
    {
        if(module is null||stopped)throw new InvalidOperationException("The owned runtime is not initialized.");
        if(Native.ResumeThread(process.Thread)!=1)throw new Win32Exception();
        diagnostics?.Write("info","bootstrapper","process.resumed","Owned game thread resumed.",new Dictionary<string,string?>{["gamePid"]=ProcessId.ToString()});
    }
    internal bool HasExited => process.Process==0||Native.WaitForSingleObject(process.Process,0)==0;
    internal Task<ulong> TransfersAsync()=>module?.TransfersAsync()??Task.FromResult(0UL);
    internal async Task WaitAsync(CancellationToken cancellation)
    {
        while(!HasExited){cancellation.ThrowIfCancellationRequested();await Task.Delay(500,cancellation);}
        if(process.Process!=0&&Native.GetExitCodeProcess(process.Process,out var code))
            diagnostics?.Write(code==0?"info":"error","bootstrapper","process.exited","Owned game process exited.",
                new Dictionary<string,string?>{["gamePid"]=ProcessId.ToString(),["exitCode"]="0x"+code.ToString("X8")});
    }
    private void VerifyNoNetworkCapabilities()
    {
        if(!Native.OpenProcessToken(process.Process,8,out var token))throw new Win32Exception();
        try
        {
            if(TokenInt(29)!=1||TokenInt(30)!=0)throw new InvalidDataException("The owned process is not a zero-capability AppContainer.");
            Native.GetTokenInformation(token,31,0,0,out var size);var buffer=Marshal.AllocHGlobal((int)size);
            try{if(!Native.GetTokenInformation(token,31,buffer,size,out _))throw new Win32Exception();
                if(new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value!=new SecurityIdentifier(sid).Value)throw new InvalidDataException("The owned container identity differs.");}
            finally{Marshal.FreeHGlobal(buffer);}
            int TokenInt(int kind)
            {
                Native.GetTokenInformation(token,kind,0,0,out var needed);var data=Marshal.AllocHGlobal((int)needed);
                try{if(!Native.GetTokenInformation(token,kind,data,needed,out _))throw new Win32Exception();return Marshal.ReadInt32(data);}
                finally{Marshal.FreeHGlobal(data);}
            }
        }
        finally{Native.CloseHandle(token);}
    }
    private static void Grant(string path,string sid,FileSystemRights rights)
    {
        SafePaths.NoReparseAncestors(path);var info=new DirectoryInfo(path);var access=info.GetAccessControl();
        access.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),rights,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));info.SetAccessControl(access);
    }
    private static string EnvironmentBlock(string profile)
    {
        var variables=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var name in new[]{"SystemRoot","WINDIR","SystemDrive","ComSpec","PROCESSOR_ARCHITECTURE","PROCESSOR_IDENTIFIER","NUMBER_OF_PROCESSORS","OS","PATHEXT"})
            if(Environment.GetEnvironmentVariable(name) is {} value)variables[name]=value;
        var windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);variables["PATH"]=windows+"\\System32;"+windows;
        variables["USERPROFILE"]=profile;variables["APPDATA"]=Path.Combine(profile,"RoamingAppData");variables["LOCALAPPDATA"]=Path.Combine(profile,"LocalAppData");
        variables["TEMP"]=variables["TMP"]=Path.Combine(profile,"Temp");return string.Join('\0',variables.Select(value=>value.Key+"="+value.Value))+"\0\0";
    }
    public async ValueTask DisposeAsync()
    {
        if(stopped)return;stopped=true;
        if(process.Process!=0&&!HasExited){diagnostics?.Write("info","bootstrapper","process.owner.stop","Diagnostic owner is stopping its game process.",
            new Dictionary<string,string?>{["gamePid"]=ProcessId.ToString()});Native.TerminateProcess(process.Process,0);while(Native.WaitForSingleObject(process.Process,50)==258)await Task.Delay(10);}
        if(process.Thread!=0)Native.CloseHandle(process.Thread);if(process.Process!=0)Native.CloseHandle(process.Process);if(job!=0)Native.CloseHandle(job);
        if(onlineBridge is not null)await onlineBridge.DisposeAsync();
        if(onlineConnection is not null)await onlineConnection.DisposeAsync();
        foreach(var lease in leases.AsEnumerable().Reverse())lease.Dispose();
        if(sid!=0)Native.FreeSid(sid);executableLock?.Dispose();
        diagnostics?.Write("info","bootstrapper","launch.disposed","Owned launch resources released.");diagnostics?.Dispose();
        if(ReferenceEquals(OfflineDiagnostics.Current,diagnostics))OfflineDiagnostics.Current=null;
    }
}
