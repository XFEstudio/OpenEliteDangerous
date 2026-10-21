using System.Text.Json;
using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerObjectCreateTests
{
    public static IEnumerable<object[]> Vectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "edserver-object-create-reference.json")));
        foreach (var vector in document.RootElement.GetProperty("vectors").EnumerateArray()) yield return [vector.Clone()];
    }
    [Theory, MemberData(nameof(Vectors))]
    public void LeafCreationMatchesIndependentBitVectors(JsonElement vector)
    {
        var expected = new EdServerLeafObjectCreate(vector.GetProperty("objectReference").GetUInt64(),
            vector.GetProperty("parentObjectReference").GetUInt64(), vector.GetProperty("goLiveWhenReady").GetBoolean(),
            new(vector.GetProperty("factoryIdentifier").GetUInt16(),vector.GetProperty("factoryName").GetString()),
            vector.GetProperty("version").GetUInt16(),vector.GetProperty("dependencies").EnumerateArray().Select(value=>value.GetUInt64()).ToArray(),
            vector.GetProperty("ownerMachineReference").ValueKind == JsonValueKind.Null ? null : vector.GetProperty("ownerMachineReference").GetUInt64());
        var bytes = Convert.FromHexString(vector.GetProperty("hex").GetString()!);
        var start = vector.GetProperty("startBit").GetInt32(); var end = vector.GetProperty("endBit").GetInt32();
        var p = start; var actual = EdServerObjectCreateCodec.ReadLeafBody(bytes,ref p);
        Assert.Equal(end,p); Assert.Equal(expected with {Dependencies=actual.Dependencies},actual);
        Assert.Equal(expected.Dependencies,actual.Dependencies);
        Assert.Equal(end-start,EdServerObjectCreateCodec.GetLeafBodyBitLength(expected));
        var output = new byte[bytes.Length]; p=start;
        EdServerObjectCreateCodec.WriteLeafBody(output,ref p,expected);
        Assert.Equal(end,p); Assert.Equal(bytes,output);
        for (var size=0;size<(end+7)/8-1;size++)
        {
            var truncated=bytes[..size]; var cursor=start;
            Assert.Throws<InvalidDataException>(()=>EdServerObjectCreateCodec.ReadLeafBody(truncated,ref cursor));
            Assert.Equal(start,cursor);
        }
    }
    [Fact]
    public void NativeClientRootAliasIsPreservedAsU48AndNeverTreatedAsASession()
    {
        // Independent bit-list assembly of sender638180 and mapper7FBD30/7FBCE0.
        var expected=Convert.FromHexString("02000000020001ffffffffff212c0000004c95c9d995c90591b5a5b901000000");var bytes=new byte[expected.Length];var p=0;
        var message=new EdServerLeafObjectCreate(0x200000002,EdServerObjectCreateCodec.ClientRootReference,true,new(1,"ServerAdmin"),0,[]);
        EdServerObjectCreateCodec.WriteLeafBody(bytes,ref p,message);Assert.Equal(254,p);Assert.Equal(expected,bytes);
        p=0;Assert.Equal(0xFFFFFFFFFF01UL,EdServerObjectCreateCodec.ReadLeafBody(expected,ref p).ParentObjectReference);
    }
    [Fact]
    public void UnsupportedCloneDoesNotConsumeCursor()
    {
        var message = Sample(); var bytes=new byte[100]; var p=0;
        EdServerObjectCreateCodec.WriteLeafBody(bytes,ref p,message);
        var cloneBit=48+48+1+5+16; bytes[cloneBit/8]|=(byte)(1<<(cloneBit%8)); p=0;
        Assert.Throws<NotSupportedException>(()=>EdServerObjectCreateCodec.ReadLeafBody(bytes,ref p)); Assert.Equal(0,p);
    }
    [Fact]
    public void InvalidNamesReferencesCountsAndShortBuffersNeverMutateOutput()
    {
        var message=Sample();
        EdServerLeafObjectCreate[] invalid=[message with{ObjectReference=1UL<<48},message with{Factory=new(0)},
            message with{Factory=new(1,"bad\0factory")},message with{Factory=new(1,new string('x',129))},
            message with{Dependencies=new ulong[65]},message with{Dependencies=[1UL<<48]}];
        foreach (var item in invalid)
        {
            var bytes=Enumerable.Repeat((byte)0xA5,100).ToArray(); var before=bytes.ToArray(); var p=3;
            Assert.ThrowsAny<ArgumentException>(()=>EdServerObjectCreateCodec.WriteLeafBody(bytes,ref p,item));
            Assert.Equal(3,p);Assert.Equal(before,bytes);
        }
        var small=new byte[1]; var cursor=0;
        Assert.Throws<InvalidDataException>(()=>EdServerObjectCreateCodec.WriteLeafBody(small,ref cursor,message));
        Assert.Equal(new byte[1],small);Assert.Equal(0,cursor);
    }
    [Fact]
    public void ReplicatedAllocationPolicyAvoidsBothNativeSpecialRanges()
    {
        var allocator=new NativeObjectIdAllocator(NativeObjectIdAllocator.FirstReplicatedObjectBase);
        var first=allocator.AllocateBlock("object-fixture"); var second=allocator.AllocateBlock("object-fixture");
        Assert.True(first.Base>0x1FFFFFFFF);Assert.Equal(0UL,first.Base%4096);
        Assert.Equal(first.Last+1,second.Base);
    }
    private static EdServerLeafObjectCreate Sample()=>new(0x200000002,0x200000001,true,new(1),0,[]);
}
