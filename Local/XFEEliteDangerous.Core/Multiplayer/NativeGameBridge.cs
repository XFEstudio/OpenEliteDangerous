using XFEEliteDangerous.Client;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace XFEEliteDangerous.Core;

/// <summary>One producer/consumer in each direction. No sockets or named global objects.
/// Payload/length precede the release-store sequence; the peer uses an acquire load.
/// A timed-out exchange poisons this channel so a late reply cannot be reused.</summary>
public sealed unsafe class NativeGameBridge : IDisposable
{
    private const int Magic=0x31474658,Header=64;
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly object gate=new();
    private readonly int capacity;
    private byte* pointer;
    private long sequence,lastRequest;
    private bool failed,disposed;
    private NativeGameBridge(string path,int capacity,bool create)
    {
        this.capacity=capacity;
        using var file=new FileStream(path,create?FileMode.CreateNew:FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite);
        var length=checked(Header+capacity*2L);
        if(create)file.SetLength(length);else if(file.Length!=length)throw new InvalidDataException("Game bridge size differs.");
        mapping=MemoryMappedFile.CreateFromFile(file,null,length,MemoryMappedFileAccess.ReadWrite,HandleInheritability.None,true);
        view=mapping.CreateViewAccessor(0,length,MemoryMappedFileAccess.ReadWrite);
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        if(create){new Span<byte>(pointer,Header).Clear();Volatile.Write(ref Int32(0),Magic);}
        else if(Volatile.Read(ref Int32(0))!=Magic){Dispose();throw new InvalidDataException("Game bridge version differs.");}
    }
    public static NativeGameBridge Create(string path,bool world=false)=>new(path,world?512*1024:16*1024*1024,true);
    public static NativeGameBridge Open(string path,bool world=false)=>new(path,world?512*1024:16*1024*1024,false);
    private ref long Int64(int offset)=>ref Unsafe.AsRef<long>(pointer+offset);
    private ref int Int32(int offset)=>ref Unsafe.AsRef<int>(pointer+offset);
    public GameLinkReply Call(GameLinkRequest request,TimeSpan? timeout=null)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);if(failed)throw new IOException("Game bridge is disconnected.");
            var bytes=JsonSerializer.SerializeToUtf8Bytes(request,GameLinkJson.Default.GameLinkRequest);
            if(bytes.Length>capacity)throw new InvalidDataException("Game bridge request exceeds its bound.");
            bytes.CopyTo(new Span<byte>(pointer+Header,capacity));Int32(16)=bytes.Length;
            var next=checked(++sequence);Volatile.Write(ref Int64(8),next);
            var started=Stopwatch.GetTimestamp();var maximum=timeout??TimeSpan.FromSeconds(30);
            while(Volatile.Read(ref Int64(24))!=next)
            {
                if(Stopwatch.GetElapsedTime(started)>maximum){failed=true;throw new TimeoutException(AppText.Get("NativeGameBridge.Text359"));}
                Thread.Sleep(1);
            }
            var count=Int32(32);if(count is <1||count>capacity){failed=true;throw new InvalidDataException("Game bridge reply exceeds its bound.");}
            return JsonSerializer.Deserialize(new ReadOnlySpan<byte>(pointer+Header+capacity,count),GameLinkJson.Default.GameLinkReply)
                ??throw new InvalidDataException("Game bridge reply is empty.");
        }
    }
    public bool TryRead(out long id,out GameLinkRequest? request)
    {
        ObjectDisposedException.ThrowIf(disposed,this);id=Volatile.Read(ref Int64(8));request=null;
        if(id==lastRequest)return false;
        if(id!=lastRequest+1)throw new InvalidDataException("Game bridge sequence differs.");
        var count=Int32(16);if(count is <1||count>capacity)throw new InvalidDataException("Game bridge request exceeds its bound.");
        request=JsonSerializer.Deserialize(new ReadOnlySpan<byte>(pointer+Header,count),GameLinkJson.Default.GameLinkRequest)
            ??throw new InvalidDataException("Game bridge request is empty.");
        lastRequest=id;return true;
    }
    public void Reply(long id,GameLinkReply response)
    {
        ObjectDisposedException.ThrowIf(disposed,this);if(id!=lastRequest)throw new InvalidDataException("Game bridge reply correlation differs.");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(response,GameLinkJson.Default.GameLinkReply);
        if(bytes.Length>capacity)throw new InvalidDataException("Game bridge response exceeds its bound.");
        bytes.CopyTo(new Span<byte>(pointer+Header+capacity,capacity));Int32(32)=bytes.Length;Volatile.Write(ref Int64(24),id);
    }
    public void Dispose()
    {
        lock(gate){if(disposed)return;disposed=true;view.SafeMemoryMappedViewHandle.ReleasePointer();pointer=null;view.Dispose();mapping.Dispose();}
    }
}
