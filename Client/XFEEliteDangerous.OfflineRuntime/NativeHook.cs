using System.ComponentModel;
using System.Runtime.InteropServices;
using Iced.Intel;

namespace XFEEliteDangerous.OfflineRuntime;

internal sealed unsafe class NativeHook
{
    private readonly nint address;
    private readonly byte[] original;
    private readonly byte[] replacement;
    public nint Trampoline { get; }
    private readonly nint allocation;
    private bool enabled;
    public NativeHook(nint address, nint destination) : this(address, destination, false, false) { }
    // Observe arguments, then tail-call the original native function. Its Lua error
    // handler may longjmp: the managed observer must have returned before it runs.
    public static NativeHook Observe(nint address, nint observer) => new(address, observer, true, false);
    // For an audited two-argument diagnostic entry only: the observer's third
    // argument is the caller's original return address. Native arguments are
    // restored before the unchanged original function resumes.
    internal static NativeHook ObserveCaller(nint address,nint observer)=>new(address,observer,true,false,true);
    // Preserve four native arguments and pass the original return address as
    // the observer's fifth argument. Used for the engine's formatted log sink.
    internal static NativeHook ObserveFourArgumentsAndCaller(nint address,nint observer)=>new(address,observer,true,false,observeFourCaller:true);
    // Audited internal basic-block boundary after the native prologue: RSP is
    //0 mod16, unlike a function entry. No arguments/return values are changed.
    internal static NativeHook ObserveAlignedBlock(nint address,nint observer)=>new(address,observer,true,false,false,true);
    // The pinned JSON first-missing-member block stores its name/document in
    // R12/R13. Expose only this fixed pair after saving the original arguments.
    internal static NativeHook ObserveJsonMissingMemberBlock(nint address,nint observer)=>new(address,observer,true,false,false,true,true);
    // The pinned full-save reader's common exit keeps its result enum in R15D.
    internal static NativeHook ObserveNativeSaveResultBlock(nint address,nint observer)=>new(address,observer,true,false,false,true,false,true);
    // A false filter result passes a null second argument to an audited native
    // function with a null fast-return path. Native execution remains a tail call.
    public static NativeHook FilterSecondArgument(nint address, nint predicate) => new(address, predicate, true, true);
    private NativeHook(nint address, nint destination, bool observer, bool filter,bool observeCaller=false,bool alignedBlock=false,bool jsonMissingMember=false,bool saveResultR15=false,bool observeFourCaller=false)
    {
        this.address = address;
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(new ReadOnlySpan<byte>((void*)address, 64).ToArray()));
        decoder.IP = (ulong)address;
        var instructions = new List<Instruction>();
        var length = 0;
        while (length < 14)
        {
            decoder.Decode(out var instruction);
            if (instruction.IsInvalid) throw new InvalidDataException("Invalid x64 instruction at hook boundary.");
            instructions.Add(instruction);
            length += instruction.Length;
        }
        original = new ReadOnlySpan<byte>((void*)address, length).ToArray();
        replacement = Enumerable.Repeat((byte)0x90, length).ToArray();
        Trampoline = allocation = AllocateNear(address);
        try
        {
            // Include the return jump in BlockEncoder: it may place branch pointer data after the instructions.
            instructions.Add(Instruction.CreateBranch(Code.Jmp_rel32_64, (ulong)(address + length)));
            var writer = new BufferWriter();
            if (!BlockEncoder.TryEncode(64, new InstructionBlock(writer, instructions, (ulong)Trampoline), out var error, out _))
                throw new InvalidDataException("Cannot relocate x64 prologue: " + error);
            if (writer.Bytes.Count > 2048) throw new InvalidDataException("Trampoline is too large.");
            Marshal.Copy(writer.Bytes.ToArray(), 0, Trampoline, writer.Bytes.Count);
            var target = destination;
            if (observer)
            {
                target = allocation + 2048;
                var stub = ObserverStub(destination, Trampoline, filter,observeCaller,alignedBlock,jsonMissingMember,saveResultR15,observeFourCaller);
                Marshal.Copy(stub, 0, target, stub.Length);
            }
            Jump(target).CopyTo(replacement, 0);
            Protect(Trampoline, 4096, 0x20);
            Native.FlushInstructionCache(Native.GetCurrentProcess(), Trampoline, 4096);
        }
        catch { Native.VirtualFree(Trampoline, 0, 0x8000); throw; }
    }
    private static byte[] ObserverStub(nint callback, nint continuation, bool filter,bool observeCaller,bool alignedBlock,bool jsonMissingMember,bool saveResultR15,bool observeFourCaller)
    {
        // Entry RSP is 8 mod 16. Save flags and all volatile registers, reserve
        // shadow space plus XMM0..5, and align the callback's stack to 16 bytes.
        var bytes = new List<byte> { 0x9C,0x50,0x51,0x52,0x41,0x50,0x41,0x51,0x41,0x52,0x41,0x53,
            0x48,0x81,0xEC,0x88,0,0,0 };
        var reservation=alignedBlock?0x80:observeFourCaller?0x98:0x88;
        var xmmOffset=observeFourCaller?0x30:0x20;
        bytes[15]=(byte)reservation;
        for (var i = 0; i < 6; i++) Xmm(0x7F,i,xmmOffset+i*16);
        // Eight pushes and0x88 bytes of local space leave the original native
        // return address at+0xC8. R8 is saved already and restored below.
        if(observeCaller)bytes.AddRange([0x4C,0x8B,0x84,0x24,0xC8,0,0,0]);
        if(observeFourCaller)bytes.AddRange([0x48,0x8B,0x84,0x24,0xD8,0,0,0,0x48,0x89,0x44,0x24,0x20]);
        if(jsonMissingMember)bytes.AddRange([0x4C,0x89,0xE1,0x4C,0x89,0xEA]);
        if(saveResultR15)bytes.AddRange([0x44,0x89,0xF9]); // ECX = original R15D
        bytes.AddRange([0x48,0xB8]); bytes.AddRange(BitConverter.GetBytes((long)callback)); bytes.AddRange([0xFF,0xD0]);
        // Saved RDX is at RSP+0xA8 after the eight pushes and 0x88 reservation.
        if (filter) bytes.AddRange([0x85,0xC0,0x75,0x0C,0x48,0xC7,0x84,0x24,0xA8,0,0,0,0,0,0,0]);
        for (var i = 0; i < 6; i++) Xmm(0x6F,i,xmmOffset+i*16);
        bytes.AddRange([0x48,0x81,0xC4,(byte)reservation,0,0,0,0x41,0x5B,0x41,0x5A,0x41,0x59,0x41,0x58,0x5A,0x59,0x58,0x9D]);
        bytes.AddRange(Jump(continuation));
        return bytes.ToArray();
        void Xmm(byte opcode,int register,int offset)
        {
            bytes.AddRange([0xF3,0x0F,opcode,(byte)((offset<128?0x44:0x84)+register*8),0x24]);
            if(offset<128)bytes.Add((byte)offset);else bytes.AddRange(BitConverter.GetBytes(offset));
        }
    }
    private static nint fixtureThird,fixtureFourth;
    [UnmanagedCallersOnly]private static void ObserveFixtureFour(nint first,nint second,nint third,nint fourth,nint caller)
    {fixtureFirst=first;fixtureSecond=second;fixtureThird=third;fixtureFourth=fourth;fixtureCaller=caller;}
    internal static bool FourArgumentCallerObservationSelfTest()
    {
        var memory=Native.VirtualAlloc(0,4096,0x3000,0x04);if(memory==0)return false;NativeHook? hook=null;
        try
        {
            byte[] code=[0x48,0x89,0xC8,0x48,0x31,0xD0,0x4C,0x31,0xC0,0x4C,0x31,0xC8,0x90,0x90,0xC3];
            Marshal.Copy(code,0,memory,code.Length);Protect(memory,4096,0x20);
            hook=ObserveFourArgumentsAndCaller(memory,(nint)(delegate* unmanaged<nint,nint,nint,nint,nint,void>)&ObserveFixtureFour);hook.Enable();
            fixtureFirst=fixtureSecond=fixtureThird=fixtureFourth=fixtureCaller=0;
            if(((delegate* unmanaged<nint,nint,nint,nint,nint>)memory)(0x123,0x456,0x789,0xABC)!=(0x123^0x456^0x789^0xABC)
                ||fixtureFirst!=0x123||fixtureSecond!=0x456||fixtureThird!=0x789||fixtureFourth!=0xABC||fixtureCaller<0x10000)return false;
            hook.Rollback();hook=null;
            Write(memory+256,Enumerable.Repeat((byte)0x90,14).Concat(new byte[]{0xC3}).ToArray());
            // A separate own x64 observer deliberately destroys volatile XMM0..5.
            var clobber=new List<byte>();
            for(var index=0;index<6;index++)clobber.AddRange([0x0F,0x57,(byte)(0xC0+index*9)]);
            clobber.Add(0xC3);Write(memory+512,clobber.ToArray());
            hook=ObserveFourArgumentsAndCaller(memory+256,memory+512);hook.Enable();
            return ((delegate* unmanaged<float,float>)(memory+256))(1.25f)==1.25f;
        }
        catch{return false;}
        finally{hook?.Rollback();Native.VirtualFree(memory,0,0x8000);}
    }
    private static nint fixtureFirst,fixtureSecond,fixtureCaller;
    private static int fixtureBlockCalls;
    private static uint fixtureSaveCode;
    [UnmanagedCallersOnly]private static void ObserveFixtureSaveResult(uint result)=>fixtureSaveCode=result;
    [UnmanagedCallersOnly] private static void ObserveFixtureBlock()=>fixtureBlockCalls++;
    internal static bool AlignedBlockObservationSelfTest()
    {
        var memory=Native.VirtualAlloc(0,4096,0x3000,0x04);if(memory==0)return false;NativeHook? hook=null;
        try
        {
            byte[] code=[0x48,0x83,0xEC,0x28,0xB8,0xEF,0xBE,0,0,0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x48,0x83,0xC4,0x28,0xC3];
            Marshal.Copy(code,0,memory,code.Length);Protect(memory,4096,0x20);
            hook=ObserveAlignedBlock(memory+4,(nint)(delegate* unmanaged<void>)&ObserveFixtureBlock);hook.Enable();fixtureBlockCalls=0;
            if(((delegate* unmanaged<uint>)memory)()!=0xBEEF||fixtureBlockCalls!=1)return false;
            hook.Rollback();hook=null;
            // Full-savegame's precise shape: a nested CALL followed by a
            // conditional branch outside the relocated block. Both outcomes and
            // the incoming argument must survive the aligned observer.
            byte[] branch=[0x48,0x83,0xEC,0x28,0xE8,0x77,0,0,0,0x84,0xC0,0x74,7,0xB8,0x34,0x12,0,0,0xEB,2,0x31,0xC0,0x48,0x83,0xC4,0x28,0xC3];
            Write(memory+64,branch);Write(memory+192,[0x8B,0xC1,0xC3]);
            hook=ObserveAlignedBlock(memory+68,(nint)(delegate* unmanaged<void>)&ObserveFixtureBlock);hook.Enable();fixtureBlockCalls=0;
            var function=(delegate* unmanaged<uint,uint>)(memory+64);
            if(function(1)!=0x1234||function(0)!=0||fixtureBlockCalls!=2)return false;
            hook.Rollback();hook=null;
            byte[] pair=[0x41,0x54,0x41,0x55,0x48,0x83,0xEC,0x28,0x49,0x89,0xCC,0x49,0x89,0xD5,
                0xB8,0xEF,0xBE,0,0,0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90,0x90,
                0x48,0x83,0xC4,0x28,0x41,0x5D,0x41,0x5C,0xC3];
            Write(memory+256,pair);fixtureFirst=fixtureSecond=0;
            hook=ObserveJsonMissingMemberBlock(memory+270,(nint)(delegate* unmanaged<nint,nint,void>)&ObserveFixturePair);hook.Enable();
            if(((delegate* unmanaged<nint,nint,uint>)(memory+256))(0x123,0x456)!=0xBEEF||fixtureFirst!=0x123||fixtureSecond!=0x456)return false;
            hook.Rollback();hook=null;
            byte[] savePrefix=[0x41,0x57,0x48,0x83,0xEC,0x20,0x41,0xBF,2,0,0,0];
            byte[] saveTail=[0x44,0x89,0xF8,0x48,0x83,0xC4,0x20,0x41,0x5F,0xC3];
            Write(memory+512,savePrefix.Concat(Enumerable.Repeat((byte)0x90,14)).Concat(saveTail).ToArray());fixtureSaveCode=uint.MaxValue;
            hook=ObserveNativeSaveResultBlock(memory+524,(nint)(delegate* unmanaged<uint,void>)&ObserveFixtureSaveResult);hook.Enable();
            return ((delegate* unmanaged<uint>)(memory+512))()==2&&fixtureSaveCode==2;
        }
        catch{return false;}
        finally{hook?.Rollback();Native.VirtualFree(memory,0,0x8000);fixtureBlockCalls=0;}
    }
    [UnmanagedCallersOnly]private static void ObserveFixturePair(nint first,nint second){fixtureFirst=first;fixtureSecond=second;}
    [UnmanagedCallersOnly] private static void ObserveFixtureCaller(nint first,nint second,nint caller)
    {fixtureFirst=first;fixtureSecond=second;fixtureCaller=caller;}
    // Execute only a tiny own allocation. This checks the observer ABI and that
    // the original third register remains intact; no game/process/network access.
    internal static bool CallerObservationSelfTest()
    {
        var memory=Native.VirtualAlloc(0,4096,0x3000,0x04);if(memory==0)return false;
        NativeHook? hook=null;
        try
        {
            byte[] code=[0x48,0x89,0xC8,0x48,0x31,0xD0,0x4C,0x31,0xC0,0x90,0x90,0x90,0x90,0x90,0xC3];
            Marshal.Copy(code,0,memory,code.Length);Protect(memory,4096,0x20);
            hook=ObserveCaller(memory,(nint)(delegate* unmanaged<nint,nint,nint,void>)&ObserveFixtureCaller);hook.Enable();
            fixtureFirst=fixtureSecond=fixtureCaller=0;
            var result=((delegate* unmanaged<nint,nint,nint,nint>)memory)(0x123,0x456,0x789);
            return result==(0x123^0x456^0x789)&&fixtureFirst==0x123&&fixtureSecond==0x456&&fixtureCaller>0x10000;
        }
        catch{return false;}
        finally{hook?.Rollback();Native.VirtualFree(memory,0,0x8000);fixtureFirst=fixtureSecond=fixtureCaller=0;}
    }
    // Redirect one audited CALL, retaining the call site's existing Windows x64 ABI.
    // This runs the callback at a precise initialization phase without detouring a
    // shared getter that other ability constructors can call earlier.
    public static NativeHook RedirectCall(nint address, nint destination) => new(address, destination, true);
    private NativeHook(nint address, nint destination, bool callSite)
    {
        this.address = address;
        original = new ReadOnlySpan<byte>((void*)address, 5).ToArray();
        if (original[0] != 0xE8) throw new InvalidDataException("Expected a direct x64 call.");
        Trampoline = address + 5 + BitConverter.ToInt32(original, 1);
        allocation = AllocateNear(address);
        try
        {
            Marshal.Copy(Jump(destination), 0, allocation, 14);
            Protect(allocation, 4096, 0x20);
            Native.FlushInstructionCache(Native.GetCurrentProcess(), allocation, 4096);
            replacement = new byte[5]; replacement[0] = 0xE8;
            BitConverter.TryWriteBytes(replacement.AsSpan(1), checked((int)(allocation - address - 5)));
        }
        catch { Native.VirtualFree(allocation, 0, 0x8000); throw; }
    }
    public void Enable() { Write(address, replacement); enabled = true; }
    public void Rollback()
    {
        if (enabled) Write(address, original);
        Native.VirtualFree(allocation, 0, 0x8000);
    }
    public static void Write(nint address, ReadOnlySpan<byte> bytes)
    {
        var old = Protect(address, (nuint)bytes.Length, 0x40);
        try { bytes.CopyTo(new Span<byte>((void*)address, bytes.Length)); }
        finally { Protect(address, (nuint)bytes.Length, old); }
        if (!Native.FlushInstructionCache(Native.GetCurrentProcess(), address, (nuint)bytes.Length)) throw new Win32Exception();
    }
    private static byte[] Jump(nint target)
    {
        byte[] bytes = [0xFF, 0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        BitConverter.TryWriteBytes(bytes.AsSpan(6), (long)target);
        return bytes;
    }
    private static uint Protect(nint address, nuint size, uint flags)
    {
        if (!Native.VirtualProtect(address, size, flags, out var old)) throw new Win32Exception();
        return old;
    }
    private static nint AllocateNear(nint address)
    {
        var aligned = (long)address & ~0xFFFFL;
        for (long distance = 0x10000; distance < 0x70000000; distance += 0x10000)
        {
            foreach (var candidate in new[] { aligned + distance, aligned - distance })
            {
                if (candidate < 0x10000) continue;
                var result = Native.VirtualAlloc((nint)candidate, 4096, 0x3000, 0x04);
                if (result != 0) return result;
            }
        }
        throw new InvalidOperationException("Cannot allocate a nearby x64 trampoline.");
    }
    private sealed class BufferWriter : CodeWriter
    {
        public List<byte> Bytes { get; } = [];
        public override void WriteByte(byte value) => Bytes.Add(value);
    }
}

internal static partial class Native
{
    [LibraryImport("kernel32", SetLastError = true)] internal static partial nint VirtualAlloc(nint address, nuint size, uint type, uint protect);
    [LibraryImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool VirtualFree(nint address, nuint size, uint type);
    [LibraryImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool VirtualProtect(nint address, nuint size, uint protect, out uint old);
    [LibraryImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool FlushInstructionCache(nint process, nint address, nuint size);
    [LibraryImport("kernel32")] internal static partial nint GetCurrentProcess();
    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)] internal static partial nint GetModuleHandle(string? name);
    [LibraryImport("kernel32")] internal static partial void ExitProcess(uint exitCode);
}
