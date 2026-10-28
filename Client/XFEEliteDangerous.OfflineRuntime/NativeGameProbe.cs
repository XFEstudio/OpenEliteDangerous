using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.OfflineRuntime;

/// <summary>
/// Pumps local replies and observes the pinned native flow. Diagnostic launches
/// also select real Continue/SOLO buttons when native eligibility allows.
/// It never changes eligibility, advances a state machine, or declares the world ready.
/// </summary>
internal static unsafe class NativeGameProbe
{
    private static readonly List<NativeHook> hooks = [];
    private static delegate* unmanaged<nint,nint,float,byte> originalUpdate;
    private static delegate* unmanaged<nint,float,byte> originalMenu;
    private static delegate* unmanaged<nint,byte> originalOdysseyMode;
    private static int lastOdysseyMode=-1;
    private static nint image;
    private static string log = "", lastFlow = "", lastMenu = "", lastMenuSkip = "";
    private static long nextFlow, nextMenu;
    private static int stage, menuInFlight, entries, menuObserved;
    private static uint menuThread;
    private static bool online;
    private static int activeSaveReads;
    private static string lastSaveContext="";
    private static string lastConnectionContext="";
    private static readonly object logGate = new();
    private static readonly NativeSoloAutomation automation=new();
    private static string automationFile="",lastAutomationHash="",observedCurrent="",observedTarget="";
    private static nint disconnectActivity;
    private static long disconnectShown;
    private static readonly byte[] menuCallbackPrefix = Convert.FromHexString("488b51908b81880100004d63d03b421073314c6bc8584c034a087427410fb601");

    internal static void Install(nint module, string directory, bool navigateMenus,bool onlineMode=false)
    {
        image = module; log = Path.Combine(directory, "native-game-flow.tsv");
        online=onlineMode;
        automationFile=Path.Combine(directory,"solo-control.json");
        Check(0x2154C40, "40534883ec3033c00f297424204885d2488d5a900f28f2480f44d8488bcbe86d");
        var update = new NativeHook(image + 0x2154C40, (nint)(delegate* unmanaged<nint,nint,float,byte>)&Update);
        originalUpdate = (delegate* unmanaged<nint,nint,float,byte>)update.Trampoline; hooks.Add(update);
        Check(0x2609980,"4c8bdc5356574883ec60488bf141c6430800488b89100100004c8d0590397d02");
        var contentMode=new NativeHook(image+0x2609980,(nint)(delegate* unmanaged<nint,byte>)&OdysseyMode);
        originalOdysseyMode=(delegate* unmanaged<nint,byte>)contentMode.Trampoline;hooks.Add(contentMode);
        Check(0x2404620,"48895c24084c894c242048895424105556574154415541564157488dac2430fa");
        Check(0x2406534,"488d8d68010000e8603b8002488b9c2410070000418bc74881c4d0060000415f");
        Check(0x4C0D566,"48895c2478b92400000048897424384c897c2430e8013d93fb488bd849c7c7ff");
        hooks.Add(NativeHook.Observe(image+0x2404620,(nint)(delegate* unmanaged<void>)&SaveEnter));
        hooks.Add(NativeHook.ObserveNativeSaveResultBlock(image+0x2406534,(nint)(delegate* unmanaged<uint,void>)&SaveResult));
        hooks.Add(NativeHook.ObserveJsonMissingMemberBlock(image+0x4C0D566,(nint)(delegate* unmanaged<nint,nint,void>)&MissingMember));
        Check(0x25EF7A0,"48895c2410565741564883ec20488bd9498bf84881c1880300004c8bf2e8ceb2");
        Check(0xA236C0,"40534883ec20488bd9483991080100000f848600000048899108010000488b89");
        Check(0xA23630,"4883ec284c8bca4885d2744d4c8b91f80000004c8bc248b86766666666666666");
        hooks.Add(NativeHook.Observe(image+0x25EF7A0,(nint)(delegate* unmanaged<nint,void>)&SaveRequestContext));
        hooks.Add(NativeHook.ObserveCaller(image+0xA236C0,(nint)(delegate* unmanaged<nint,nint,nint,void>)&TransitionRequest));
        hooks.Add(NativeHook.ObserveCaller(image+0xA23630,(nint)(delegate* unmanaged<nint,nint,nint,void>)&TransitionRequest));
        Check(0x1D136B0,"405355574883ec50488be9488b4930488b01ff506084c00f8473010000488b4d");
        Check(0x1D3BE50,"83f90777284863c1488d15a1412cfe8b8c8284bed3014803caffe1b801000000");
        hooks.Add(NativeHook.Observe(image+0x1D136B0,(nint)(delegate* unmanaged<nint,void>)&ConnectionContext));
        hooks.Add(NativeHook.Observe(image+0x1D3BE50,(nint)(delegate* unmanaged<uint,void>)&DisconnectReason));
        if (navigateMenus)
        {
            Check(0x4817860, "405641544883ec5841bcffffffff0f297424300f28f1488bf14439a108020000");
            Check(0x4836060, Convert.ToHexString(menuCallbackPrefix));
            var menu = new NativeHook(image + 0x4817860, (nint)(delegate* unmanaged<nint,float,byte>)&Menu);
            originalMenu = (delegate* unmanaged<nint,float,byte>)menu.Trampoline; hooks.Add(menu);
            Check(0x4828190,"405356574883ec40488b7110488bf9488b4970b201e8f67719fc488987e00000");
            Check(0x481B9E0,"40534883ec20488b81e0000000488d9180000000488bd9488b4808488b01ff50");
            Check(0x4810C10,"488b4128488b5190488bc84c8b00488b521849ff6020");
            // Return from the managed observer before native UI/Lua code runs.
            hooks.Add(NativeHook.Observe(image+0x4828190,(nint)(delegate* unmanaged<nint,void>)&DisconnectStart));
            hooks.Add(NativeHook.Observe(image+0x481B9E0,(nint)(delegate* unmanaged<nint,void>)&DisconnectEnd));
        }
        foreach (var hook in hooks) hook.Enable();
        if(navigateMenus)File.WriteAllText(Path.Combine(directory,"native-automation.json"),
            "{\"schema\":1,\"command\":\"enter-solo\",\"nativeEligibilityRequired\":true,\"disconnectAcknowledgement\":\"original-configured-OK-callback\"}");
        Record("PROBE\tnative-eligibility-only\tworld-not-validated");
    }
    [UnmanagedCallersOnly]private static byte OdysseyMode(nint user)
    {
        // This original predicate requires GameSeason>=4, SPACE_LEGS and normal
        // native user readiness. Observe its result without replacing any check.
        var result=originalOdysseyMode(user);
        if(Interlocked.Exchange(ref lastOdysseyMode,result)!=result)
            Record("NATIVE_CONTENT_MODE\tOdyssey\t"+result+"\t0x2609980");
        return result;
    }
    [UnmanagedCallersOnly]private static void SaveEnter(){Interlocked.Increment(ref activeSaveReads);Record("NATIVE_SAVE_ENTER");}
    [UnmanagedCallersOnly]private static void SaveResult(uint result)
    {Interlocked.Decrement(ref activeSaveReads);Record("NATIVE_SAVE_RESULT\t"+result);}
    [UnmanagedCallersOnly]private static void MissingMember(nint name,nint document)
    {
        try
        {
            if(Identifier(name) is {} field)
            {
                Record((Volatile.Read(ref activeSaveReads)>0?"NATIVE_SAVE_MISSING":"NATIVE_JSON_MISSING")+"\t"+field);
                OfflineDiagnostics.Current?.Write("debug","native.json","json.member.missing","Native JSON reader recorded a missing member.",
                    new Dictionary<string,string?>{["field"]=field,["saveReadActive"]=(Volatile.Read(ref activeSaveReads)>0).ToString(),
                        ["nativeEntryRva"]="0x4C0D566",["httpPath"]=OfflineCurl.NativeCompletionContext?.GetValueOrDefault("httpPath"),
                        ["httpTraceId"]=OfflineCurl.NativeCompletionContext?.GetValueOrDefault("httpTraceId")});
            }
        }catch(Exception error){OfflineDiagnostics.Current?.Write("warning","native.json","observer.failed","Missing-member observation failed.",exception:error);}
    }
    [UnmanagedCallersOnly]private static void SaveRequestContext(nint request)
    {
        try
        {
            if(!Read(request+0x330,out nint connection)||!Read(connection,out nint table)||!Read(table+0x20,out nint getter))return;
            var text="NATIVE_SAVE_CONNECTION\t"+Rva(table)+"\t"+Rva(getter);
            if(Read(request+0x340,out nint reporter)&&Read(reporter,out nint reporterTable))
            {
                text+="\t"+Rva(reporterTable);
                if(Read(reporterTable+0x18,out nint error))text+="\t"+Rva(error);
            }
            if(text!=lastSaveContext){lastSaveContext=text;Record(text);}
        }catch{}
    }
    [UnmanagedCallersOnly]private static void TransitionRequest(nint csm,nint record,nint caller)
    {
        try
        {
            if(!Read(csm,out nint table)||table!=image+0x5138DC0||!Read(record,out nint name))return;
            var state=Identifier(name);
            if(state is "LoadFailed_Disconnect" or "MultiplayerLoad" or "LoadFailed")Record("STATE_REQUEST\t"+state+"\t"+Rva(caller));
        }catch{}
    }
    private static string Rva(nint pointer)=>pointer>=image&&pointer<image+0x6500000?"0x"+(pointer-image).ToString("X"):"external";
    [UnmanagedCallersOnly]private static void DisconnectReason(uint reason)=>Record("NATIVE_DISCONNECT_REASON\t"+reason);
    [UnmanagedCallersOnly]private static void ConnectionContext(nint activity)
    {
        if(lastConnectionContext.Length>0)return;
        try
        {
            var row="NATIVE_DISCONNECT_CONTEXT";
            foreach(var field in new[]{0x30,0x38})
            {
                if(!Read(activity+field,out nint service)||!Read(service,out nint table))return;
                row+="\t"+Rva(table);
                foreach(var slot in field==0x30?new[]{0x60,0x230}:new[]{0x128,0x130})
                    if(Read(table+slot,out nint method))row+="\t"+Rva(method);
            }
            if(row!=lastConnectionContext){lastConnectionContext=row;Record(row);}
        }catch{}
    }
    private static void Check(int rva, string hex)
    {
        if (!new ReadOnlySpan<byte>((void*)(image+rva), hex.Length/2).SequenceEqual(Convert.FromHexString(hex)))
            throw new InvalidDataException("The pinned native probe entry differs: " + rva.ToString("X"));
    }
    [UnmanagedCallersOnly] private static byte Update(nint first, nint context, float delta)
    {
        var result = originalUpdate(first, context, delta);
        InProcessNativeChannels.Pump();
        NativeLiveRefit.Pump(observedCurrent,observedTarget);
        var now = Environment.TickCount64;
        if (now < Volatile.Read(ref nextFlow)) return result;
        Volatile.Write(ref nextFlow, now + 1000);
        try
        {
            if (context > 0x10070 && Read(context-0x70+0xA0, out nint flow) && Flow(flow, out var current, out var target))
            {
                var value = current + "\t" + target;
                observedCurrent=current;observedTarget=target;
                if (value != lastFlow) { lastFlow = value; Record("FLOW\t" + value); }
                PollAutomation();
                AcknowledgeDisconnect(current,target,now);
            }
        }
        catch (Exception error) { Record("PROBE_ERROR\t" + error.GetType().Name); }
        return result;
    }
    [UnmanagedCallersOnly]private static void DisconnectStart(nint activity)
    {
        if(Read(activity,out nint table)&&table==image+0x560CD40)
        {disconnectActivity=activity;disconnectShown=Environment.TickCount64;}
    }
    [UnmanagedCallersOnly]private static void DisconnectEnd(nint activity)
    {
        if(disconnectActivity==activity)disconnectActivity=0;
    }
    private static void PollAutomation()
    {
        if(originalMenu==null||!File.Exists(automationFile))return;
        string candidate="unreadable";
        try
        {
            if((File.GetAttributes(automationFile)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Native SOLO command cannot be a filesystem link.");
            using var input=new FileStream(automationFile,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(input.Length is <=0 or >2048)throw new InvalidDataException("Native SOLO command exceeds its bound.");
            var document=new byte[(int)input.Length];input.ReadExactly(document);
            candidate=Convert.ToHexStringLower(SHA256.HashData(document));
            if(candidate==lastAutomationHash)return;
            if(automation.Arm(document,DateTimeOffset.UtcNow))Record("AUTOMATION_ARMED\t"+automation.CommandId[..12]);
            lastAutomationHash=candidate;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            if(candidate==lastAutomationHash)return;lastAutomationHash=candidate;
            OfflineDiagnostics.Current?.Write("error","native.automation","command.rejected","Native SOLO command rejected.",exception:error);
        }
    }
    private static void AcknowledgeDisconnect(string current,string target,long now)
    {
        var activity=disconnectActivity;
        if(activity==0||now-disconnectShown<1000||!automation.CanAcknowledgeDisconnect(current,target,DateTimeOffset.UtcNow))return;
        //477CF80 installs these exact two interfaces.4810C10 forwards the dialog's
        //normal OK action from config+18 to the original flow service; no state
        //record, ready flag or error result is overwritten.
        if(!Read(activity,out nint table)||table!=image+0x560CD40
            ||!Read(activity+0x80,out nint callback)||callback!=image+0x560CE00
            ||!Read(activity+0x10,out nint config)||!Read(config+0x18,out nint action)||action==0
            ||!Read(activity+0xA8,out nint flow)||flow==0)return;
        automation.AcknowledgedDisconnect();Record("AUTOMATION_ACK_DISCONNECT\t"+automation.CommandId[..12]);
        ((delegate* unmanaged<nint,void>)(image+0x4810C10))(activity+0x80);
    }
    [UnmanagedCallersOnly] private static byte Menu(nint activity, float delta)
    {
        var inFlight = Interlocked.Increment(ref menuInFlight);
        try
        {
            if (Interlocked.CompareExchange(ref menuObserved,1,0)==0) Record("MENU_CALLBACK\tfirst-natural-update");
            var result = originalMenu(activity, delta);
            var now = Environment.TickCount64;
            if (inFlight != 1 || now < nextMenu) return result;
            nextMenu = now + 1000;
            var thread = ProbeNative.GetCurrentThreadId();
            if (menuThread == 0) menuThread = thread;
            if (menuThread != thread) { Record("PROBE_ERROR\tmenu-thread-changed"); return result; }
            try { InspectMenu(activity); }
            catch (Exception error) { Record("PROBE_ERROR\t" + error.GetType().Name); }
            return result;
        }
        finally { Interlocked.Decrement(ref menuInFlight); }
    }
    private static void InspectMenu(nint activity)
    {
        if (!Read(activity,out nint vtable) || vtable != image+0x5611E60
            || !Read(activity+0x80,out nint callbackVtable) || callbackVtable != image+0x5611F20
            || !Read(activity+0x118,out nint flow) || !Flow(flow,out var current,out var target)
            || !Read(activity+0x10,out nint config) || !Read(config+8,out nint categories)
            || !Read(config+0x10,out uint count) || count is 0 or >128
            || !Read(activity+0x208,out uint selected) || selected >= count) { Skip("header-or-flow"); return; }
        var category = categories + checked((int)selected*0x58);
        if (!Read(category,out byte type) || type is <1 or >3
            || !Read(category+0x40,out nint buttons) || !Read(category+0x48,out uint buttonCount) || buttonCount >128
            || !Read(activity+0x240,out ulong cacheCount) || cacheCount != buttonCount
            || !Read(activity+0x248,out nint cache)) { Skip("category-or-cache-layout"); return; }
        var wanted=automation.CommandId.Length>0
            ?automation.WantedAction(current,target,DateTimeOffset.UtcNow)??""
            :stage==0?"ContinueMenu":stage==1?(online?"SetupPlayWithEveryone":"SetupPlaySolo"):"";
        var candidate = -1; var matches = 0;
        var rows = new StringBuilder();
        Span<byte> definition = stackalloc byte[0x78]; Span<byte> copy = stackalloc byte[0x78];
        for (var index=0; index<buttonCount; index++)
        {
            var button = buttons+index*0x78; var cached = cache+index*0xC8;
            if (!Bytes(button,definition) || !Bytes(cached+0x30,copy) || !definition.SequenceEqual(copy)
                || !Read(cached,out byte condition) || !Read(cached+1,out byte selectable)
                || condition>1 || selectable>1) { Skip("button-definition-or-flags"); return; }
            // Separators and unrelated actions need not have an identifier in
            // this small diagnostic alphabet. Only an exact readable wanted
            // action can be selected; all definition/cache/eligibility checks remain.
            var name = Read(button,out nint wrapper) && Read(wrapper,out nint namePointer)
                ? Identifier(namePointer) ?? "<opaque>" : "<unreadable>";
            rows.Append(index).Append(':').Append(name).Append(':').Append(condition).Append(selectable).Append(',');
            if (string.Equals(name,wanted,StringComparison.OrdinalIgnoreCase) && condition==1 && selectable==1)
            { matches++;candidate=index; }
        }
        var menu = selected + "\t" + current + "\t" + target + "\t" + rows;
        if (menu != lastMenu) { lastMenu=menu; Record("MENU\t"+menu); }
        if (wanted.Length==0 || matches!=1 || current!=target) return;
        // Same-thread fresh reads and exact definition/cache identity are required.
        if (!Read(activity+0x208,out uint reselected) || selected!=reselected
            || !Read(activity+0x248,out nint recache) || recache!=cache
            || !Read(cache+candidate*0xC8,out ushort flags) || flags!=0x0101
            || !new ReadOnlySpan<byte>((void*)(image+0x4836060),menuCallbackPrefix.Length).SequenceEqual(menuCallbackPrefix)) return;
        stage++;
        if(automation.CommandId.Length>0)
        {automation.Issued(wanted);Record("AUTOMATION_ACTION\t"+automation.CommandId[..12]+"\t"+wanted+"\t"+automation.AttemptsIssued);}
        Record("ACTION\t"+wanted);
        ((delegate* unmanaged<nint,nint,int,void>)(image+0x4836060))(activity+0x80,0,candidate);
    }
    private static void Skip(string reason)
    {
        if(reason!=lastMenuSkip){lastMenuSkip=reason;Record("MENU_SKIP\t"+reason);}
    }
    private static bool Flow(nint flow,out string current,out string target)
    {
        current=target="";
        return Read(flow,out nint vtable) && vtable==image+0x5236DB0
            && Read(flow+0x310,out nint csm) && Read(csm,out nint csmVtable) && csmVtable==image+0x5138DC0
            && Read(csm+0xF8,out nint definition) && Read(definition+0x10,out nint records)
            && Read(definition+0x18,out uint count) && count is >0 and <=4096
            && Read(csm+0x100,out nint from) && Read(csm+0x108,out nint to)
            && State(from,records,count,out current) && State(to,records,count,out target);
    }
    private static bool State(nint record,nint records,uint count,out string value)
    {
        value=""; if(record==0) { value="Null"; return true; }
        if(records<0x10000 || record<records || (ulong)(record-records)>=(ulong)count*0x28 || (record-records)%0x28!=0
            || !Read(record,out nint name) || Identifier(name) is not {} identifier) return false;
        value=identifier;return true;
    }
    private static string? Identifier(nint pointer)
    {
        // Native flow names include spaces (for example the configured quit
        // actions). Excluding them hid those transitions from the error log.
        Span<byte> bytes=stackalloc byte[128];
        for(var index=0;index<bytes.Length;index++)
        {
            if(!Read(pointer+index,out byte value))return null;
            if(value==0)return index>0?Encoding.ASCII.GetString(bytes[..index]):null;
            if(value is not(>= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'_' or (byte)' '))return null;
            bytes[index]=value;
        }
        return null;
    }
    private static bool Read<T>(nint pointer,out T value) where T:unmanaged
    {
        T copy=default;value=default;
        if(pointer<0x10000 || pointer>nint.MaxValue-sizeof(T)
            || !ProbeNative.ReadProcessMemory(Native.GetCurrentProcess(),pointer,&copy,(nuint)sizeof(T),out var read) || read!=(nuint)sizeof(T))return false;
        value=copy;return true;
    }
    private static bool Bytes(nint pointer,Span<byte> bytes)
    {
        fixed(byte* destination=bytes)return pointer>=0x10000 && pointer<=nint.MaxValue-bytes.Length
            && ProbeNative.ReadProcessMemory(Native.GetCurrentProcess(),pointer,destination,(nuint)bytes.Length,out var read) && read==(nuint)bytes.Length;
    }
    private static void Record(string value,[CallerFilePath]string file="",[CallerLineNumber]int line=0,[CallerMemberName]string member="")
    {
        var fields=new Dictionary<string,string?>{["nativeThreadId"]=ProbeNative.GetCurrentThreadId().ToString()};
        var parts=value.Split('\t');
        for(var index=1;index<parts.Length;index++)fields["argument"+index]=parts[index];
        if(parts is ["STATE_REQUEST",_,var nativeCaller]&&uint.TryParse(nativeCaller.AsSpan(2),System.Globalization.NumberStyles.HexNumber,null,out var callerRva))
            foreach(var entry in NativeCodeMap.Locate(image+(nint)callerRva))fields[entry.Key]=entry.Value;
        OfflineDiagnostics.Current?.Write(value.Contains("ERROR",StringComparison.Ordinal)||value.Contains("LoadFailed",StringComparison.Ordinal)||parts[0]=="NATIVE_DISCONNECT_REASON"?"warning":"debug",
            "native.flow",parts[0],value,fields,file:file,line:line,member:member);
        try { lock(logGate) if(entries++<2048)File.AppendAllText(log,Environment.TickCount64+"\t"+value+Environment.NewLine); } catch { }
    }
}
internal static unsafe partial class ProbeNative
{
    [LibraryImport("kernel32")] internal static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32")] [return:MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadProcessMemory(nint process,nint source,void* destination,nuint length,out nuint read);
}
