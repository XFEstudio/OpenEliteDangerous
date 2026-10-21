using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Bootstrapper;

/// <summary>Loads the owned NativeAOT module into the suspended child created by this launcher.</summary>
internal sealed class OfflineNativeModule(nint process, nint module, IReadOnlyDictionary<string,uint> exports)
{
    internal Task<ulong> ReadStatusAsync() => CallAsync(process, module + (int)exports["XfeOfflineStatus"]);
    internal Task<ulong> TransfersAsync() => CallAsync(process, module + (int)exports["XfeOfflineTransfers"]);
    internal static async Task<OfflineNativeModule> LoadAsync(nint process, string library, byte[] configuration)
    {
        var exports = ReadExports(library);
        var loader = NativeLibrary.GetExport(NativeLibrary.Load("ntdll.dll"), "LdrLoadDll");
        var local = new byte[32]; Marshal.Copy(loader, local, 0, 32);
        if (!local.AsSpan().SequenceEqual(Read(process, loader, 32))) throw new InvalidDataException("The child system-loader mapping differs.");
        var path = Encoding.Unicode.GetBytes(library + '\0'); var argument = Allocate(process, new byte[32 + path.Length]);
        var input = Allocate(process, configuration);
        try
        {
            var buffer = new byte[32 + path.Length];
            BitConverter.TryWriteBytes(buffer, checked((ushort)(path.Length - 2)));
            BitConverter.TryWriteBytes(buffer.AsSpan(2), checked((ushort)path.Length));
            BitConverter.TryWriteBytes(buffer.AsSpan(8), (long)(argument + 32)); path.CopyTo(buffer, 32); Write(process, argument, buffer);
            var result = await CallAsync(process, loader, 0, 0, argument, argument + 16);
            if (unchecked((int)result) < 0) throw new InvalidOperationException("Owned LdrLoadDll failed: " + unchecked((uint)result).ToString("X8"));
            EnsureAlive(process,"LdrLoadDll output");
            var loaded = (nint)BitConverter.ToInt64(Read(process, argument + 16, 8));
            if (loaded == 0) throw new InvalidDataException("Owned native module was not loaded.");
            result = await CallAsync(process, loaded + (int)exports["XfeOfflineInitialize"], input);
            if (result != 0) throw new InvalidOperationException("Offline runtime initialization failed; inspect this launch's initialization-error.txt.");
            return new(process, loaded, exports);
        }
        finally { Free(process, input); Free(process, argument); }
    }
    internal static Dictionary<string,uint> ReadExports(string path)
    {
        using var file = File.OpenRead(path); using var pe = new PEReader(file);
        if (pe.PEHeaders.CoffHeader.Machine != System.Reflection.PortableExecutable.Machine.Amd64) throw new InvalidDataException("A native x64 module is required.");
        var directory = pe.PEHeaders.PEHeader!.ExportTableDirectory;
        var header = pe.GetSectionData(directory.RelativeVirtualAddress).GetContent(0,40).ToArray();
        var functionsCount = BitConverter.ToInt32(header,20); var namesCount = BitConverter.ToInt32(header,24);
        if (functionsCount is <1 or >4096 || namesCount is <1 or >4096) throw new InvalidDataException("Invalid export count.");
        var functions = pe.GetSectionData(BitConverter.ToInt32(header,28)).GetContent(0,functionsCount*4).ToArray();
        var names = pe.GetSectionData(BitConverter.ToInt32(header,32)).GetContent(0,namesCount*4).ToArray();
        var ordinals = pe.GetSectionData(BitConverter.ToInt32(header,36)).GetContent(0,namesCount*2).ToArray();
        var result = new Dictionary<string,uint>(StringComparer.Ordinal);
        for (var index=0;index<namesCount;index++)
        {
            var bytes=pe.GetSectionData(BitConverter.ToInt32(names,index*4)).GetContent(0,256).ToArray();
            var end=Array.IndexOf(bytes,(byte)0); if(end<1) throw new InvalidDataException("Invalid export name.");
            var ordinal=BitConverter.ToUInt16(ordinals,index*2); if(ordinal>=functionsCount) throw new InvalidDataException("Invalid export ordinal.");
            result.Add(Encoding.ASCII.GetString(bytes[..end]),BitConverter.ToUInt32(functions,ordinal*4));
        }
        foreach(var name in new[]{"XfeOfflineInitialize","XfeOfflineStatus","XfeOfflineTransfers"})
            if(!result.TryGetValue(name,out var rva)||rva==0||(rva>=directory.RelativeVirtualAddress&&rva<directory.RelativeVirtualAddress+directory.Size))
                throw new InvalidDataException("An offline module export is missing or forwarded.");
        return result;
    }
    internal static async Task<ulong> CallAsync(nint process,nint function,nint arg1=0,nint arg2=0,nint arg3=0,nint arg4=0)
    {
        var allocation=VirtualAllocEx(process,0,8192,0x3000,0x04);if(allocation==0)throw new Win32Exception();
        nint thread=0;var completed=false;
        try
        {
            var code=new List<byte>{0x48,0x83,0xEC,0x28,0x48,0xB9};code.AddRange(BitConverter.GetBytes((long)arg1));
            code.AddRange([0x48,0xBA]);code.AddRange(BitConverter.GetBytes((long)arg2));
            code.AddRange([0x49,0xB8]);code.AddRange(BitConverter.GetBytes((long)arg3));
            code.AddRange([0x49,0xB9]);code.AddRange(BitConverter.GetBytes((long)arg4));
            code.AddRange([0x48,0xB8]);code.AddRange(BitConverter.GetBytes((long)function));code.AddRange([0xFF,0xD0,0x49,0xBB]);
            // A zero-filled return cell is not proof that the callback returned.
            // The completion marker is written only after its actual return.
            code.AddRange(BitConverter.GetBytes((long)(allocation+4096)));code.AddRange([0x49,0x89,0x03,0x41,0xC7,0x43,0x08,0x58,0x46,0x45,0x31,0x31,0xC0,0x48,0x83,0xC4,0x28,0xC3]);
            Write(process,allocation,code.ToArray());
            if(!Native.VirtualProtectEx(process,allocation,4096,0x20,out _)||!Native.FlushInstructionCache(process,allocation,(nuint)code.Count))throw new Win32Exception();
            thread=CreateRemoteThread(process,0,0,allocation,0,0,out _);if(thread==0)throw new Win32Exception();
            var started=Stopwatch.GetTimestamp();
            while(true)
            {
                var wait=Native.WaitForSingleObject(thread,50);if(wait==0){completed=true;break;}
                if(wait!=258)throw new Win32Exception();
                if(Stopwatch.GetElapsedTime(started)>TimeSpan.FromSeconds(25))throw new TimeoutException("Owned native initialization timed out.");
                await Task.Delay(10).ConfigureAwait(false);
            }
            if(!GetExitCodeThread(thread,out var exit))throw new Win32Exception();
            EnsureAlive(process,"native callback",exit);
            if(exit!=0)throw new InvalidOperationException("Owned native callback thread failed: 0x"+exit.ToString("X8"));
            var output=Read(process,allocation+4096,12);
            if(BitConverter.ToUInt32(output,8)!=0x31454658)throw new InvalidOperationException("Owned native callback thread exited without its completion marker; return value is unavailable.");
            var result=BitConverter.ToUInt64(output);
            OfflineDiagnostics.Current?.Write("debug","bootstrapper.loader","callback.returned","Native callback completed with a verified marker.",
                new Dictionary<string,string?>{["threadExit"]="0x"+exit.ToString("X8"),["elapsedMs"]=Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F1",System.Globalization.CultureInfo.InvariantCulture)});
            return result;
        }
        finally{if(thread!=0)Native.CloseHandle(thread);if(completed||thread==0)Free(process,allocation);}
    }
    private static nint Allocate(nint process,byte[] bytes)
    {var pointer=VirtualAllocEx(process,0,(nuint)bytes.Length,0x3000,0x04);if(pointer==0)throw new Win32Exception();try{Write(process,pointer,bytes);return pointer;}catch{Free(process,pointer);throw;}}
    private static void Write(nint process,nint pointer,byte[] bytes)
    {if(!Native.WriteProcessMemory(process,pointer,bytes,(nuint)bytes.Length,out var written)||written!=(nuint)bytes.Length)throw new Win32Exception();}
    private static byte[] Read(nint process,nint pointer,int count)
    {
        var bytes=new byte[count];
        if(!Native.ReadProcessMemory(process,pointer,bytes,(nuint)count,out var read)||read!=(nuint)count)
        {
            var code=Marshal.GetLastWin32Error();var exit=Native.GetExitCodeProcess(process,out var status)?"0x"+status.ToString("X8"):"unavailable";
            throw new InvalidOperationException($"Owned child memory read failed: Win32={code}, bytes={read}/{count}, processExit={exit}.",new Win32Exception(code));
        }
        return bytes;
    }
    private static void Free(nint process,nint pointer){if(pointer!=0)VirtualFreeEx(process,pointer,0,0x8000);}
    private static void EnsureAlive(nint process,string stage,uint? threadExit=null)
    {
        if(!Native.GetExitCodeProcess(process,out var exit))throw new Win32Exception();
        var wait=Native.WaitForSingleObject(process,0);
        if(wait==0)
            throw new InvalidOperationException($"Owned game exited during {stage}: processExit=0x{exit:X8}, threadExit={(threadExit is {} value?"0x"+value.ToString("X8"):"unavailable")}. No further child-memory read was attempted.");
        if(wait!=258)throw new Win32Exception();
    }
    internal static async Task RunCallbackChecksAsync()
    {
        // Exercise the same remote-thread transport in this diagnostic host;
        // neither a game nor a desktop is opened by these regression checks.
        var process=Native.GetCurrentProcess();
        var code=VirtualAllocEx(process,0,4096,0x3000,0x04);if(code==0)throw new Win32Exception();
        try
        {
            const ulong expected=0xFEDCBA98C0000005;
            var body=new List<byte>{0x48,0xB8};body.AddRange(BitConverter.GetBytes(expected));body.Add(0xC3);
            Write(process,code,body.ToArray());
            if(!Native.VirtualProtectEx(process,code,4096,0x20,out _)||!Native.FlushInstructionCache(process,code,(nuint)body.Count))throw new Win32Exception();
            if(await CallAsync(process,code)!=expected)throw new InvalidOperationException("Native callback regression: full 64-bit return value changed.");
            var exitThread=NativeLibrary.GetExport(NativeLibrary.Load("kernel32.dll"),"ExitThread");
            foreach(var status in new[]{0,42})
            {
                var rejected=false;
                try{await CallAsync(process,exitThread,status);}
                catch(InvalidOperationException error)when(status==0?error.Message.Contains("completion marker",StringComparison.Ordinal):error.Message.Contains("thread failed: 0x0000002A",StringComparison.Ordinal)){rejected=true;}
                if(!rejected)throw new InvalidOperationException("Native callback regression: an early thread exit was accepted.");
            }
        }
        finally{Free(process,code);}
        var childInfo=new ProcessStartInfo(Environment.ProcessPath??throw new InvalidOperationException("Diagnostic host path is unavailable."))
        {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true};
        childInfo.ArgumentList.Add("--loader-check-child");
        using var child=Process.Start(childInfo)??throw new IOException("Diagnostic child did not start.");
        try
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if(await child.StandardOutput.ReadLineAsync(deadline.Token)!="loader-check-child-ready")throw new IOException("Diagnostic child was not ready.");
            var exitProcess=NativeLibrary.GetExport(NativeLibrary.Load("kernel32.dll"),"ExitProcess");
            var rejected=false;
            try{await CallAsync(child.Handle,exitProcess,299);}
            catch(InvalidOperationException error)when(error.Message.Contains("processExit=0x0000012B",StringComparison.Ordinal)
                &&error.Message.Contains("No further child-memory read",StringComparison.Ordinal)){rejected=true;}
            if(!rejected)throw new InvalidOperationException("Native callback regression: an exited child was read as a successful callback.");
        }
        finally{if(!child.HasExited){child.Kill(entireProcessTree:true);await child.WaitForExitAsync();}}
    }
    [DllImport("kernel32",SetLastError=true)]private static extern nint VirtualAllocEx(nint process,nint address,nuint size,uint type,uint protect);
    [DllImport("kernel32",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool VirtualFreeEx(nint process,nint address,nuint size,uint type);
    [DllImport("kernel32",SetLastError=true)]private static extern nint CreateRemoteThread(nint process,nint attributes,nuint size,nint function,nint argument,uint flags,out uint thread);
    [DllImport("kernel32",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetExitCodeThread(nint thread,out uint code);
}
