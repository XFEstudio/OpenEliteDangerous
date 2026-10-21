using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using XFEEliteDangerous.Core;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.OfflineRuntime;

public sealed record RuntimeConfiguration(string WorkspaceDirectory, string AccountId, string Executable,
    string ProfileRoot, string LogDirectory, string DriverVersion, bool ProbeSolo = false,string? OnlineBridgeDirectory=null);
[JsonSerializable(typeof(RuntimeConfiguration))]
internal partial class RuntimeJson : JsonSerializerContext;

public static unsafe class RuntimeExports
{
    internal const string SupportedHash = "e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988";
    private static INativeGameData? data;
    private static RuntimeConfiguration? configuration;
    private static int initialized;
    private static string error = "";
    [UnmanagedCallersOnly(EntryPoint = "XfeOfflineInitialize")]
    public static int Initialize(byte* json)
    {
        if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0) return 2;
        try
        {
            var length = 0; while (json != null && length < 65536 && json[length] != 0) length++;
            if (json == null || length is 0 or 65536) throw new InvalidDataException("Invalid owned runtime configuration.");
            configuration = JsonSerializer.Deserialize(new ReadOnlySpan<byte>(json, length), RuntimeJson.Default.RuntimeConfiguration)
                ?? throw new InvalidDataException("Missing owned runtime configuration.");
            OfflineDiagnostics.Current=new(configuration.LogDirectory,"offline-runtime",Path.GetFileName(Path.GetDirectoryName(configuration.LogDirectory))??"owned-runtime");
            OfflineDiagnostics.Current.Write("info","runtime","initialize.begin","Initializing owned offline runtime.");
            AppDomain.CurrentDomain.UnhandledException+=(_,args)=>OfflineDiagnostics.Current?.Write("critical","runtime","unhandled.exception",
                "Unhandled managed exception.",exception:args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException+=(_,args)=>OfflineDiagnostics.Current?.Write("error","runtime","task.unobserved",
                "Unobserved task exception.",exception:args.Exception);
            if (!Path.GetFullPath(configuration.Executable).Equals(@"E:\Elite Dangerous\Products\elite-dangerous-odyssey-64\EliteDangerous64.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Only the authorized E game copy is supported.");
            using var executable = File.OpenRead(configuration.Executable);
            if (executable.Length != 103469056 || Convert.ToHexStringLower(SHA256.HashData(executable)) != SupportedHash)
                throw new InvalidDataException("The owned game image differs from the source analysis.");
            executable.Position = 0x5131E08; var key = new byte[32]; executable.ReadExactly(key);
            data = configuration.OnlineBridgeDirectory is {} bridge?new OnlineNativeGameData(bridge):new OfflineGameData(configuration.WorkspaceDirectory, configuration.AccountId);
            var image = Native.GetModuleHandle(null);
            NativeCodeMap.Initialize(configuration.Executable,image);
            OfflineDiagnostics.Current.RegisterSecret(Convert.ToHexStringLower(key));
            OfflineDiagnostics.Current.RegisterSecret(Convert.ToBase64String(key));
            NativeEngineLog.Install(image);
            NativeLocationProbe.Install(image);
            OfflineProfileHooks.Install(image, configuration);
            OfflineCurl.Install(image, data, key, configuration.LogDirectory);
            InProcessNativeChannels.Install(image, data.World, configuration.LogDirectory);
            NativeGameProbe.Install(image, configuration.LogDirectory, configuration.ProbeSolo,configuration.OnlineBridgeDirectory is not null);
            CryptographicOperations.ZeroMemory(key);
            OfflineDiagnostics.Current.Write("info","runtime","initialize.complete","All pinned local adapters installed.");
            Volatile.Write(ref initialized, 3); return 0;
        }
        catch (Exception exception)
        {
            OfflineDiagnostics.Current?.Write("critical","runtime","initialize.failed","Offline runtime initialization failed.",exception:exception);
            error = exception.GetType().Name + ": " + exception.Message; Volatile.Write(ref initialized, -1);
            if (configuration is not null) { try { File.WriteAllText(Path.Combine(configuration.LogDirectory, "initialization-error.txt"), error); } catch { } }
            return 1;
        }
    }
    [UnmanagedCallersOnly(EntryPoint = "XfeOfflineStatus")]
    public static int Status() => Volatile.Read(ref initialized);
    [UnmanagedCallersOnly(EntryPoint = "XfeOfflineTransfers")]
    public static int Transfers() => OfflineCurl.Completed;
    [UnmanagedCallersOnly(EntryPoint = "XfeOfflineHookSelfTest")]
    public static int HookSelfTest() => NativeHook.CallerObservationSelfTest() && NativeHook.AlignedBlockObservationSelfTest() && NativeHook.FourArgumentCallerObservationSelfTest() ? 0 : 1;
    [UnmanagedCallersOnly(EntryPoint = "XfeOfflineDataSelfTest")]
    public static int DataSelfTest(byte* json)
    {
        RuntimeConfiguration? settings = null;
        try
        {
            var length = 0; while (json != null && length < 65536 && json[length] != 0) length++;
            if (json == null || length is 0 or 65536) return 1;
            settings = JsonSerializer.Deserialize(new ReadOnlySpan<byte>(json, length), RuntimeJson.Default.RuntimeConfiguration)!;
            // Own data only: this export has no game/process entry, profile hooks or image patching.
            var source = new OfflineGameData(settings.WorkspaceDirectory, settings.AccountId);
            try
            {
                var login = source.InvokeAsync("GET", "/2.0/elite/user/login", new Dictionary<string,string>(), []).GetAwaiter().GetResult();
                if (login.Status != 200) return 2;
                var profile = source.InvokeAsync("GET", "/2.0/elite/user", new Dictionary<string,string>(), []).GetAwaiter().GetResult();
                if (profile.Status != 200 || !profile.Payload.StartsWith('<')) return 3;
                var save = source.InvokeAsync("GET", "/2.0/elite/commander/savegame", new Dictionary<string,string>(), []).GetAwaiter().GetResult();
                if (save.Status != 200) return 4;
                using var document = JsonDocument.Parse(save.Payload);
                if (!document.RootElement.TryGetProperty("vessels", out var vessels) || vessels.GetArrayLength() == 0) return 5;
                return OfflineCurl.CallbackSelfTest(source, settings.LogDirectory);
            }
            finally { source.DisposeAsync().GetAwaiter().GetResult(); }
        }
        catch (Exception error)
        { try { if (settings is not null) File.WriteAllText(Path.Combine(settings.LogDirectory, "data-self-test-error.txt"), error.ToString()); } catch { } return 10; }
    }
}
