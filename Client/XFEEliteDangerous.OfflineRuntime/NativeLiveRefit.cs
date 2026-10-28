using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using XFEEliteDangerous.Client;
using XFEEliteDangerous.Core;

namespace XFEEliteDangerous.OfflineRuntime;

/// <summary>Original module setters and subsequent readback, exclusively on the native game thread.</summary>
internal static partial class NativeLiveRefit
{
    private sealed record Module(nint Life, ulong EntityId, string Slot, NativeModuleSnapshot Data);
    private sealed record Discovered(nint ShipLife,ulong ShipEntity,uint ShipId,Module Module,nint Library,nint ModifierContext);
    private sealed record Pending(LiveRefitCommand Command, Module? Module, NativeModuleSnapshot? Previous, long VerifyAfter, bool Initialize = false,nint ExpectedRecipe=0,
        LiveRefitCommand? RejectedCommand=null,string? FailureDetail=null);
    private static nint image, shipLife;
    private static ulong shipEntity;
    private static uint shipId;
    private static readonly List<NativeHook> Hooks = [];
    private static readonly Dictionary<string, Module> Modules = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<LiveRefitCommand> Commands = new();
    private static readonly ConcurrentQueue<Discovered> DiscoveredModules = new();
    private static readonly HashSet<string> SeenCommands = [];
    private static LiveRefitStatus? observation;
    private static LiveRefitResult? result;
    private static Pending? pending;
    private static string runtimeId = "";
    private static long nextObservation;
    private static int exchangeStopping;

    internal static unsafe void Install(nint module, RuntimeConfiguration configuration, INativeGameData data)
    {
        image = module; runtimeId = Guid.NewGuid().ToString("N");
        Check(0x24F0B80, "4c8bdc55498dab68fdffff4881ec90030000488b05cf5d9b034833c448898540");
        Check(0x43B1500, "48895c2408574883ec20488bd90fb6fa488b49e04883c128e8639229fc84c074");
        Check(0x43B3780, "40534883ec2080b9c100000000488bd9754380b9c0000000018b81b800000075");
        Check(0x43B2D70, "48895c2408574883ec2033d2410fb6f833c00f28c1f0480fb19110040000f30f");
        Check(0x4398630, "4533c033c0f04c0fb18110040000488bd048c1ea2033d08954241033c0f04c0f");
        InstallOutfitting();InstallAssets();
        var hook = NativeHook.ObserveFourArgumentsAndCaller(image + 0x24F0B80,
            (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&ObserveModule);
        Hooks.Add(hook); hook.Enable();
        _ = Task.Run(() => ExchangeAsync(configuration, data));
    }

    private static async Task ExchangeAsync(RuntimeConfiguration configuration, INativeGameData data)
    {
        var mailbox = data is OnlineNativeGameData ? null : new LiveRefitMailbox(configuration.WorkspaceDirectory, configuration.AccountId);
        string? lastCommand = null; long nextError = 0;
        while (Volatile.Read(ref exchangeStopping)==0)
        {
            try
            {
                var status = Volatile.Read(ref observation);
                LiveRefitCommand? command;
                if (data is OnlineNativeGameData online)
                { if (status is not null) online.PublishRefit(status); command = online.TakeRefit(); }
                else
                { if (status is not null) mailbox!.Publish(status); command = mailbox!.ReadCommand(); }
                if (command is not null && command.RuntimeId == runtimeId && command.Id != lastCommand && Commands.IsEmpty)
                { lastCommand = command.Id; Commands.Enqueue(command); }
            }
            catch (Exception error)
            {
                if (Environment.TickCount64 >= nextError)
                { nextError = Environment.TickCount64 + 5000; OfflineDiagnostics.Current?.Write("warning", "native.refit", "mailbox.failed", "Refit exchange failed; no command was acknowledged.", exception: error); }
            }
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    [UnmanagedCallersOnly]
    private static void ObserveModule(nint owner, nint entityHandle, nint state, nint auxiliary, nint caller)
    {
        try
        {
            if (!Read(state, out nint slotRef) || !Read(state + 8, out nint moduleRef) || !Read(state + 0x14, out uint id)
                || !Read(entityHandle, out nint life) || !Read(life + 0xB0, out ulong entityId)
                || !Read(owner + 0xB0, out nint parentLife) || !Read(parentLife + 0xB0, out ulong parentEntity)
                || !Read(owner + 0x238, out nint shipState) || !Read(shipState + 0xE0, out uint vesselId) || vesselId is 0 or uint.MaxValue) return;
            var slot = RcText(slotRef); var name = RcText(moduleRef); if (slot is null || name is null) return;
            if(DiscoveredModules.Count>=256)return;
            Read(state + 0x18, out ulong uid); Read(state + 0x20, out ulong value); Read(state + 0x30, out ulong excess);
            Read(state + 0x44, out byte enabled); Read(state + 0x48, out uint priority); Read(state + 0x40, out float health); Read(state + 0x4C, out byte free);
            var snapshot = new NativeModuleSnapshot { Id = id, Name = name, Value = value, Excess = excess, Uid = NormalizeUid(uid),
                On = enabled != 0, Priority = priority, HealthMillion = Million(health), Free = free != 0 };
            Read(state+0x38,out uint clip);Read(state+0x3C,out uint hopper);
            snapshot=snapshot with{Ammo=clip==uint.MaxValue&&hopper==uint.MaxValue?null:new(clip,hopper)};
            Read(owner+0x48,out nint library);Read(owner+0x78,out nint context);
            DiscoveredModules.Enqueue(new(parentLife,parentEntity,vesselId,new(life,entityId,slot,snapshot),library,context));
            OfflineDiagnostics.Current?.Write("debug", "native.refit", "module.observed", "Original native module and parent ship identities observed.",
                new Dictionary<string, string?> { ["slot"] = slot, ["module"] = name, ["shipId"] = vesselId.ToString(), ["entityId"] = entityId.ToString(), ["nativeCallerRva"] = NativeCodeMap.Rva(caller) });
        }
        catch (Exception error) { OfflineDiagnostics.Current?.Write("warning", "native.refit", "observe.failed", "Native module discovery failed.", exception: error); }
    }

    internal static void Pump(string current, string target)
    {
        while(DiscoveredModules.TryDequeue(out var discovered))
        {
            if(discovered.ShipLife!=shipLife||discovered.ShipEntity!=shipEntity||discovered.ShipId!=shipId)
            {ClearRecipes();Modules.Clear();shipLife=discovered.ShipLife;shipEntity=discovered.ShipEntity;shipId=discovered.ShipId;}
            resourceLibrary=discovered.Library;modifierContext=discovered.ModifierContext;
            if(Modules.Count<128||Modules.ContainsKey(discovered.Module.Slot))Modules[discovered.Module.Slot]=discovered.Module;
        }
        var inGame = current == "InGame" && target == "InGame" && Live(shipLife, shipEntity);
        if(current is "SaveAndExit" or "StopGame" or "PrepareForQuit" or "QuitApplication" or "MainMenu")ClearRecipes();
        if(current=="QuitApplication")Volatile.Write(ref exchangeStopping,1);
        LiveRefitCommand? executing=null;
        try
        {
            if(inGame)CacheRecipes();
            if (pending is { } waiting && Environment.TickCount64 >= waiting.VerifyAfter)
            {
                if (!inGame) throw new InvalidOperationException("world_changed");
                VerifyPending(waiting);
            }
            if (pending is null && Commands.TryDequeue(out var command))
            {
                executing=command;
                if (!Guid.TryParseExact(command.Id, "N", out _) || !SeenCommands.Add(command.Id)) return;
                if (SeenCommands.Count > 256) SeenCommands.Clear();
                if (!inGame || command.RuntimeId != runtimeId || command.ShipId != shipId || command.ShipEntityId != shipEntity || command.ExpiresUtc <= DateTimeOffset.UtcNow)
                { Complete(command, false, "world_changed", "Command expired or the active native ship changed.", null); return; }
                BeginCommand(command); nextObservation = 0;
            }
        }
        catch (Exception error)
        {
            if (pending is { } failed)
            {
                if(inGame&&failed.RejectedCommand is null)
                {
                    try{BeginRecovery(failed,error);}
                    catch(Exception recoveryError){Complete(failed.Command,false,"native_recovery_failed",error.Message+"; "+recoveryError.Message,null);pending=null;}
                }
                else{Complete(failed.RejectedCommand??failed.Command,false,"native_recovery_failed",error.Message,null);pending=null;}
            }
            else if(executing is not null)Complete(executing,false,"native_request_rejected",error.Message,null);
            OfflineDiagnostics.Current?.Write("error", "native.refit", "apply.failed", "Native refit failed; no success acknowledgement was issued.", exception: error);
        }
        if (Environment.TickCount64 < nextObservation) return;
        nextObservation = Environment.TickCount64 + 500;
        var slots = new List<LiveRefitSlot>();
        if (inGame) foreach (var module in Modules.Values) { try { slots.Add(ObservedSlot(module)); } catch { } }
        try{if(inGame)foreach(var slot in NativeSlots())if(!slots.Any(value=>value.Slot==slot.Name)&&CurrentLife(slot.Index)==0)slots.Add(new(slot.Name,null));}catch{}
        uint? capacity=null;
        try{if(inGame&&Read(Component(shipLife,0x5F2C21C,0x53086A0)+0x288,out uint cargoCapacity))capacity=cargoCapacity;}catch{}
        Volatile.Write(ref observation, new(1, runtimeId, RuntimeExports.SupportedHash, Environment.ProcessId, shipId, shipEntity,
            current == target ? current : "Transition", DateTimeOffset.UtcNow, slots.ToArray(), result,CanLoadRecipes()?null:Recipes.Values.Select(value=>value.Template.Name).Distinct(StringComparer.Ordinal).ToArray(),capacity));
    }

    private static unsafe NativeModuleSnapshot Snapshot(Module module)
    {
        if (!Live(module.Life, module.EntityId)) throw new InvalidOperationException("module_not_live");
        var power = Component(module.Life, 0x5F2C228, 0x5597F60);
        var health = HealthComponent(module.Life);
        var identity=Component(module.Life,0x5F2C224,0x5597298);
        var prioritySettings=PrioritySettings(module);uint priority=0;
        if (!Read(power + 0x108, out byte enabled) || prioritySettings!=0&&!Read(prioritySettings+0xB8, out priority)) throw new InvalidDataException("Unreadable module properties.");
        if(!Read(identity+0x148,out ulong uid)||!Read(identity+0x160,out ulong excess))throw new InvalidDataException("Unreadable module identity.");
        // The original module builder only applies integrity when this optional
        // damage component exists. Indestructible modules retain full integrity.
        var value = health==0?1f:((delegate* unmanaged<nint, float>)(image + 0x4398630))(health);
        var recipe=RecipeReference(NativeSlots().Single(value=>value.Name==module.Slot).Index);
        return module.Data with { On = enabled != 0, Priority = priority, HealthMillion = Million(value),Uid=NormalizeUid(uid),Excess=excess,
            Ammo=ReadAmmunition(module),Engineering=ReadEngineering(recipe) };
    }
    private static unsafe void SetProperties(Module module, NativeModuleSnapshot values)
    {
        var before = Snapshot(module); var power = Component(module.Life, 0x5F2C228, 0x5597F60); var health = HealthComponent(module.Life);
        if(health==0&&values.HealthMillion!=1_000_000)throw new InvalidOperationException("integrity_not_editable");
        var prioritySettings=PrioritySettings(module);
        if(prioritySettings==0&&values.Priority!=0)throw new InvalidOperationException("priority_not_editable");
        if (before.On != values.On) ((delegate* unmanaged<nint, byte, void>)(image + 0x43B1500))(power, values.On ? (byte)1 : (byte)0);
        if (before.Priority != values.Priority) ((delegate* unmanaged<nint, uint, void>)(image + 0x43B3780))(prioritySettings, values.Priority);
        if (health!=0&&Math.Abs((long)before.HealthMillion - values.HealthMillion) > 1) ((delegate* unmanaged<nint, float, byte, void>)(image + 0x43B2D70))(health, values.HealthMillion / 1_000_000f, 0);
    }
    private static bool SameProperties(NativeModuleSnapshot? actual, NativeModuleSnapshot? expected,bool compareAmmo=true) => actual is not null && expected is not null
        && LiveRefitMailbox.Matches(actual, expected) && actual.On == expected.On && actual.Priority == expected.Priority && Math.Abs((long)actual.HealthMillion - expected.HealthMillion) <= 2
        &&NativeEngineering.Equivalent(actual.Engineering,expected.Engineering)&&(!compareAmmo||expected.Ammo is null||actual.Ammo==expected.Ammo);
    private static ulong? NormalizeUid(ulong value)=>value is 0 or ulong.MaxValue?null:value;
    private static uint Million(float value) => float.IsFinite(value) && value is >= 0 and <= 1 ? (uint)Math.Round(value * 1_000_000d, MidpointRounding.AwayFromZero) : throw new InvalidDataException("Invalid native module integrity.");
    private static void Complete(LiveRefitCommand command, bool applied, string code, string detail, LiveRefitSlot? actual,bool restored=false)
    {
        result = new(command.Id, applied, code, detail, actual,pending?.Command.Id==command.Id?pending.Previous:null,restored); nextObservation = 0;
        OfflineDiagnostics.Current?.Write(applied ? "info" : "warning", "native.refit", applied ? "apply.verified" : "apply.rejected", detail,
            new Dictionary<string, string?> { ["commandId"] = command.Id, ["slot"] = command.Slot, ["code"] = code });
    }
    private static bool Live(nint life, ulong entity) => life != 0 && entity != 0 && Read(life + 0xB0, out ulong id) && id == entity && Read(life + 0xC0, out int state) && state == 8;
    private static nint Component(nint life, int typeRva, int vtableRva)
    {
        var value=FindComponent(life,typeRva);
        if(value==0||!Read(value,out nint table)||table!=image+vtableRva)throw new InvalidOperationException("Pinned native module component is unavailable.");
        return value;
    }
    private static nint HealthComponent(nint life)
    {
        var value=FindComponent(life,0x5F41D5C);
        if(value!=0&&(!Read(value,out nint table)||table!=image+0x5598F88))throw new InvalidOperationException("Native module damage component differs.");
        return value;
    }
    private static nint FindComponent(nint life,int typeRva)
    {
        if (!Read(life + 0xC8, out nint component) || component == 0 || !Read(component + 8, out nint queryTable)
            || !Read(queryTable, out nint query) || query != image + 0x8C6EF0
            || !Read(component - 0x30 + 0x10, out nint metadata) || !Read(metadata + 0x30, out nint slots)
            || !Read(component - 0x30 + 0x18, out nint values) || !Read(image + typeRva, out uint type) || type is 0 or > 100000
            || !Read(slots + checked((nint)type * 16), out ulong index)) throw new InvalidOperationException("Pinned native component query is unavailable.");
        if(index>512)return 0;
        if(!Read(values+(nint)index*8,out nint value))throw new InvalidOperationException("Unreadable native component slot.");
        return value;
    }
    private static unsafe void Check(int rva, string hex)
    {
        if (!new ReadOnlySpan<byte>((void*)(image + rva), hex.Length / 2).SequenceEqual(Convert.FromHexString(hex))) throw new InvalidDataException("Pinned live refit entry differs: " + rva.ToString("X"));
    }
    private static unsafe string? RcText(nint pointer)
    {
        if (!Read(pointer, out ulong length) || length is 0 or > 160) return null;
        var bytes = new byte[(int)length];
        fixed (byte* destination = bytes) if (!ProbeNative.ReadProcessMemory(Native.GetCurrentProcess(), pointer + 0x14, destination, (nuint)bytes.Length, out var read) || read != (nuint)bytes.Length) return null;
        var value = Encoding.ASCII.GetString(bytes);
        return value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? value : null;
    }
    private static unsafe bool Read<T>(nint pointer, out T value) where T : unmanaged
    {
        T copy = default; value = default;
        if (pointer < 0x10000 || pointer > nint.MaxValue - sizeof(T) || !ProbeNative.ReadProcessMemory(Native.GetCurrentProcess(), pointer, &copy, (nuint)sizeof(T), out var read) || read != (nuint)sizeof(T)) return false;
        value = copy; return true;
    }
}
