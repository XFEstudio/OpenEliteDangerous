using System.Runtime.InteropServices;
using System.Text;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.OfflineRuntime;

/// <summary>Observes every call to the pinned formatted engine log sink, before its own level filter.</summary>
internal static unsafe class NativeEngineLog
{
    private static NativeHook? hook;
    [ThreadStatic]private static bool recording;
    private static long unavailable,reentrant;
    internal static void Install(nint image)
    {
        const string prefix="48895c241048897424205557415441564157488d6c24c94881ecc0000000488b";
        if(!new ReadOnlySpan<byte>((void*)(image+0x560DE0),prefix.Length/2).SequenceEqual(Convert.FromHexString(prefix)))
            throw new InvalidDataException("The pinned engine log sink differs.");
        hook=NativeHook.ObserveFourArgumentsAndCaller(image+0x560DE0,(nint)(delegate* unmanaged<nint,uint,nint,nint,nint,void>)&Observe);
        hook.Enable();
        OfflineDiagnostics.Current?.Write("info","native.engine","log.sink.installed","Capturing formatted native engine logs before their level filter.",
            new Dictionary<string,string?>{["nativeEntryRva"]="0x560DE0",["build"]="2026.09.03.332841"});
    }
    [UnmanagedCallersOnly]private static void Observe(nint logger,uint severity,nint message,nint unused,nint caller)
    {
        if(recording){Interlocked.Increment(ref reentrant);return;}
        recording=true;
        try
        {
            var fields=NativeCodeMap.Locate(caller);
            if(fields.GetValueOrDefault("nativeFunctionRva")=="0x99E190"&&OfflineCurl.NativeCompletionContext is {} completion)
                foreach(var entry in completion)fields[entry.Key]=entry.Value;
            fields["nativeSinkRva"]="0x560DE0";fields["nativeSeverity"]=severity.ToString();
            fields["nativeThreadId"]=ProbeNative.GetCurrentThreadId().ToString();
            fields["unavailableMessages"]=Interlocked.Read(ref unavailable).ToString();
            fields["reentrantMessages"]=Interlocked.Read(ref reentrant).ToString();
            var text=Text(message);
            if(text is null){Interlocked.Increment(ref unavailable);OfflineDiagnostics.Current?.Write("warning","native.engine","message.unreadable","Native text was unreadable.",fields);return;}
            var level=new[]{"error","failed","exception","disconnect","assert","invalid"}.Any(value=>text.Contains(value,StringComparison.OrdinalIgnoreCase))?"warning":"debug";
            OfflineDiagnostics.Current?.Write(level,"native.engine","engine.message",text,fields);
        }
        catch(Exception error){OfflineDiagnostics.Current?.Write("error","native.engine","observer.failed","Native logging observation failed.",exception:error);}
        finally{recording=false;}
    }
    private static string? Text(nint pointer)
    {
        if(pointer<0x10000)return null;
        Span<byte> bytes=stackalloc byte[4096];
        for(var offset=0;offset<bytes.Length;offset+=64)
        {
            fixed(byte* destination=bytes)
                if(!ProbeNative.ReadProcessMemory(Native.GetCurrentProcess(),pointer+offset,destination+offset,64,out var read)||read!=64)
                    return offset==0?null:Encoding.UTF8.GetString(bytes[..offset])+" [unreadable suffix]";
            var end=bytes.Slice(offset,64).IndexOf((byte)0);
            if(end>=0)return Encoding.UTF8.GetString(bytes[..(offset+end)]);
        }
        return Encoding.UTF8.GetString(bytes)+" [truncated]";
    }
}
