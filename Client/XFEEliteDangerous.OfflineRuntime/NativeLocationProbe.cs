using System.Runtime.InteropServices;
using System.Text;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.OfflineRuntime;

/// <summary>Reads lifecycle and activation-delay dependencies; never invokes or modifies an entity.</summary>
internal static unsafe class NativeLocationProbe
{
    private static NativeHook? hook;
    private static readonly object gate=new();
    private static readonly Dictionary<(nint Owner,byte Slot),(long Next,string Snapshot)> observations=[];
    [ThreadStatic]private static bool observing;
    internal static void Install(nint image)
    {
        const int entry=0x1C4B620;
        var prefix=Convert.FromHexString("48895c24084889742410574883ec200fb6c2498bf9486bd070498bf0488d9c0a");
        if(!new ReadOnlySpan<byte>((void*)(image+entry),prefix.Length).SequenceEqual(prefix))
            throw new InvalidDataException("The pinned location dependency observer differs.");
        hook=NativeHook.ObserveFourArgumentsAndCaller(image+entry,
            (nint)(delegate* unmanaged<nint,nint,nint,nint,nint,void>)&Observe);
        hook.Enable();
        OfflineDiagnostics.Current?.Write("info","native.location","location.observer.installed",
            "Lifecycle and activation-delay observation installed; no readiness state is changed.",
            new Dictionary<string,string?>{["nativeEntryRva"]="0x1C4B620"});
    }
    [UnmanagedCallersOnly]private static void Observe(nint owner,nint rawSlot,nint request,nint diagnostic,nint caller)
    {
        if(observing)return;observing=true;
        try
        {
            var slot=(byte)rawSlot;var key=(owner,slot);var now=Environment.TickCount64;
            lock(gate)
            {
                if(observations.TryGetValue(key,out var previous)&&now<previous.Next)return;
                if(observations.Count>=128&&!observations.ContainsKey(key))observations.Clear();
                observations[key]=(now+1000,previous.Snapshot??"");
                var fields=NativeCodeMap.Locate(caller);
                fields["nativeEntryRva"]="0x1C4B620";fields["slot"]=slot.ToString();
                if(!Read(owner,out nint table))return;
                fields["ownerVtableRva"]=NativeCodeMap.Rva(table);
                var entry=owner+0x218+slot*0x70;
                Entity(entry+8,"container",fields);Entity(entry+0x10,"location",fields);
                var snapshot=string.Join("|",fields.Where(pair=>pair.Key is not("nativeReturnRva" or "nativeFunctionRva" or "nativeFunctionOffset" or "nativeSymbol"))
                    .Select(pair=>pair.Key+"="+pair.Value));
                if(snapshot==previous.Snapshot)return;
                observations[key]=(now+1000,snapshot);
                OfflineDiagnostics.Current?.Write("debug","native.location","location.dependencies",
                    "Original location check observed these entity dependencies and activation delays.",fields);
            }
        }
        catch(Exception error)
        {OfflineDiagnostics.Current?.Write("warning","native.location","location.observer.failed","Location dependency observation failed.",exception:error);}
        finally{observing=false;}
    }
    private static void Entity(nint handle,string name,Dictionary<string,string?> fields)
    {
        if(!Read(handle,out nint lifecycle)){fields[name+"Present"]="unreadable";return;}
        fields[name+"Present"]=(lifecycle!=0).ToString();if(lifecycle==0)return;
        if(Read(lifecycle+0xC0,out int state))fields[name+"Lifecycle"]=state.ToString();
        if(!Read(lifecycle+0xC8,out nint component)||component==0)return;
        if(Read(component,out nint table))fields[name+"VtableRva"]=NativeCodeMap.Rva(table);
        //646010 obtains the immutable object kind through component+20 -> metadata+10.
        if(Read(component+0x20,out nint metadata)&&Read(metadata+0x10,out nint text))fields[name+"Kind"]=Text(text);
        //8C52B0 reads the activation delay at object-base+4D0;
        //647D90 returns this base's +30 interface from a lifecycle handle.
        if(Read(component-0x30+0x4D0,out nint delay))fields[name+"ActivationDelay"]=Text(delay);
    }
    private static string Text(nint text)
    {
        if(text==0)return "";
        if(!Read(text,out ulong length))return "<unreadable>";
        if(length>4096)return "<invalid-string-bound>";
        var count=(int)Math.Min(length,512);Span<byte> bytes=stackalloc byte[count];
        fixed(byte* destination=bytes)
            if(!ProbeNative.ReadProcessMemory(Native.GetCurrentProcess(),text+0x14,destination,(nuint)count,out var read)||read!=(nuint)count)return "<unreadable>";
        return Encoding.UTF8.GetString(bytes)+(length>512?" [truncated-native-delay]":"");
    }
    private static bool Read<T>(nint pointer,out T value)where T:unmanaged
    {
        T copy=default;value=default;
        if(pointer<0x10000||pointer>nint.MaxValue-sizeof(T)
            ||!ProbeNative.ReadProcessMemory(Native.GetCurrentProcess(),pointer,&copy,(nuint)sizeof(T),out var read)||read!=(nuint)sizeof(T))return false;
        value=copy;return true;
    }
}
