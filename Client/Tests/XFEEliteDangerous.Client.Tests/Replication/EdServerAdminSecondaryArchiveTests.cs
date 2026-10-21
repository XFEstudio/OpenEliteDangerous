using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;
public sealed class EdServerAdminSecondaryArchiveTests
{
    [Fact]
    public void FixedRunAndStringOverrideAreAppliedAndInheritedAcrossADelta()
    {
        using var archive=new EdServerAdminSecondaryArchive(12,64655,new byte[56]);
        using var first=Frame(1,0,(bytes,p)=>
        {
            Put(bytes,ref p,0,9);Put(bytes,ref p,32,9);Put(bytes,ref p,0x12345678,32);
            Put(bytes,ref p,448,9);Put(bytes,ref p,56,32);Put(bytes,ref p,3,32);Put(bytes,ref p,0x636261,24);return p;
        });
        archive.Apply(first);Assert.Equal([0x78,0x56,0x34,0x12],archive.CopyFixedState(1)[..4]);Assert.Equal("abc"u8.ToArray(),archive.CopyString(1,0));
        using var second=Frame(2,1,(bytes,p)=>{Put(bytes,ref p,32,9);Put(bytes,ref p,40,9);Put(bytes,ref p,0xFE,8);return p;});
        archive.Apply(second);Assert.Equal([0x78,0x56,0x34,0x12,0xFE],archive.CopyFixedState(2)[..5]);Assert.Equal("abc"u8.ToArray(),archive.CopyString(2,0));
        Assert.Equal(0,archive.CopyFixedState(1)[4]);archive.Apply(second);Assert.Equal(2,archive.StateCount);
        using var fork=archive.Fork();archive.Dispose();Assert.Equal("abc"u8.ToArray(),fork.CopyString(2,0));
    }
    [Fact]
    public void ChangedDuplicateTruncatedOverrideAndMissingBaselineCannotMutateState()
    {
        using var archive=new EdServerAdminSecondaryArchive(12,64655,new byte[56]);using var initial=Frame(1,0,(_,p)=>p);archive.Apply(initial);
        using var unknownDefaults=new EdServerAdminSecondaryArchive(12,64655);
        Assert.Throws<NotSupportedException>(()=>unknownDefaults.Apply(initial));Assert.Equal(0,unknownDefaults.StateCount);
        using var changed=Frame(1,0,(bytes,p)=>{Put(bytes,ref p,0,9);Put(bytes,ref p,1,9);Put(bytes,ref p,1,1);return p;});
        Assert.Throws<InvalidDataException>(()=>archive.Apply(changed));
        using var missing=Frame(2,99,(_,p)=>p);Assert.Throws<NotSupportedException>(()=>archive.Apply(missing));
        using var truncated=Frame(2,1,(bytes,p)=>{Put(bytes,ref p,448,9);Put(bytes,ref p,900,32);return p;});
        Assert.Throws<InvalidDataException>(()=>archive.Apply(truncated));Assert.Equal(1,archive.StateCount);Assert.Equal(1,archive.LatestSequence);
        Assert.All(archive.CopyFixedState(1),value=>Assert.Equal(0,value));
    }
    private static EdServerObjectUpdate Frame(ushort sequence,ushort baseline,Func<byte[],int,int> body)
    {var bytes=new byte[1024];var p=0;Put(bytes,ref p,baseline,10);p=body(bytes,p);return new(12,true,64655,sequence,p,bytes.AsSpan(0,(p+7)/8));}
    private static void Put(byte[] bytes,ref int p,ulong value,int bits)
    {for(var i=0;i<bits;i++,p++)if((value>>i&1)!=0)bytes[p/8]|=(byte)(1<<(p%8));}

    [Fact]
    public void UnknownNativeStringDefaultsMustBeSuppliedAndSurviveForkedDeltas()
    {
        var fixedDefaults=new byte[56];System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(fixedDefaults,0x42C80000);
        using var archive=new EdServerAdminSecondaryArchive(12,64655,fixedDefaults,39);
        using var missing=Frame(1,0,(_,p)=>p);
        Assert.Throws<NotSupportedException>(()=>archive.Apply(missing));Assert.Equal(0,archive.StateCount);
        using var complete=Frame(1,0,(bytes,p)=>
        {
            foreach(var index in new[]{451,452})
            {Put(bytes,ref p,(ulong)index,9);Put(bytes,ref p,40,32);Put(bytes,ref p,1,32);Put(bytes,ref p,65,8);}
            return p;
        });
        archive.Apply(complete);Assert.Equal(fixedDefaults,archive.CopyFixedState(1));Assert.Equal("A"u8.ToArray(),archive.CopyString(1,3));
        using var fork=archive.Fork();using var delta=Frame(2,1,(_,p)=>p);fork.Apply(delta);
        Assert.Equal("A"u8.ToArray(),fork.CopyString(2,4));Assert.Equal(1,archive.StateCount);
    }
    [Fact]
    public void DuplicateSequenceMayUseDifferentValidEncodingOfTheSameState()
    {
        using var archive=new EdServerAdminSecondaryArchive(12,64655,new byte[56]);
        using var initial=Frame(1,0,(_,p)=>p);archive.Apply(initial);
        using var same=Frame(1,0,(bytes,p)=>{Put(bytes,ref p,0,9);Put(bytes,ref p,32,9);Put(bytes,ref p,0,32);return p;});
        archive.Apply(same);Assert.Equal(1,archive.StateCount);Assert.Equal(1,archive.LatestSequence);
        using var changed=Frame(1,0,(bytes,p)=>{Put(bytes,ref p,0,9);Put(bytes,ref p,32,9);Put(bytes,ref p,1,32);return p;});
        Assert.Throws<InvalidDataException>(()=>archive.Apply(changed));Assert.Equal(new byte[56],archive.CopyFixedState(1));
    }
}
