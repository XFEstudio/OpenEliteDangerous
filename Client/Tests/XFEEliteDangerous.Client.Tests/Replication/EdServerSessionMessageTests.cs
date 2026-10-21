using System.Globalization;
using System.Text.Json;
using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerSessionMessageTests
{
    public static IEnumerable<object[]> Vectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "edserver-session-reference.json")));
        foreach (var item in doc.RootElement.GetProperty("vectors").EnumerateArray())
            yield return new object[] { item.Clone() };
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void SessionBodiesMatchEightIndependentNativeLayoutVectors(JsonElement v)
    {
        var source = Convert.FromHexString(v.GetProperty("hex").GetString()!);
        var kind = v.GetProperty("kind").GetString();
        var p = v.GetProperty("startBit").GetInt32();
        var start = p;
        var sessionId = v.GetProperty("sessionId").GetUInt64();
        var initialBase = ulong.Parse(v.GetProperty("initialObjectIdBase").GetString()!, CultureInfo.InvariantCulture);
        var first = v.GetProperty("firstIds").EnumerateArray().Select(x => ulong.Parse(x.GetString()!, CultureInfo.InvariantCulture)).ToArray();
        var second = v.GetProperty("secondIds").EnumerateArray().Select(x => ulong.Parse(x.GetString()!, CultureInfo.InvariantCulture)).ToArray();
        var target = new byte[source.Length];
        switch (kind)
        {
            case "request":
                var request = EdServerSessionMessageCodec.ReadRequestBody(source, ref p);
                Assert.Equal(new(sessionId, initialBase), request);
                var requestPosition = start;
                EdServerSessionMessageCodec.WriteRequestBody(target, ref requestPosition, request);
                Assert.Equal(p, requestPosition);
                break;
            case "left":
                var left = EdServerSessionMessageCodec.ReadLeftBody(source, ref p);
                Assert.Equal(sessionId, left.SessionId);
                var leftPosition = start;
                EdServerSessionMessageCodec.WriteLeftBody(target, ref leftPosition, left);
                Assert.Equal(p, leftPosition);
                break;
            case "members":
                var members = EdServerSessionMessageCodec.ReadMembersBody(source, ref p);
                Assert.Equal(sessionId, members.SessionId);
                Assert.Equal(initialBase, members.InitialObjectIdBase);
                Assert.Equal(first, members.MachineRunIds);
                var membersPosition = start;
                EdServerSessionMessageCodec.WriteMembersBody(target, ref membersPosition, members);
                Assert.Equal(p, membersPosition);
                break;
            case "joined":
                var joined = EdServerSessionMessageCodec.ReadJoinedBody(source, ref p);
                Assert.Equal(sessionId, joined.SessionId);
                Assert.Equal(first, joined.FirstObjectIds);
                Assert.Equal(second, joined.SecondObjectIds);
                var joinedPosition = start;
                EdServerSessionMessageCodec.WriteJoinedBody(target, ref joinedPosition, joined);
                Assert.Equal(p, joinedPosition);
                break;
            default: throw new InvalidDataException("Unexpected fixture kind.");
        }
        Assert.Equal(v.GetProperty("endBit").GetInt32(), p);
        Assert.Equal(source, target);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(255)]
    [InlineData(4096)]
    public void CollectionExponentBoundariesAreDecodedWithoutInventedExtraFields(int count)
    {
        var ids = Enumerable.Range(0, count).Select(x => (ulong)x).ToArray();
        var bytes = new byte[32 + count * 6];
        var p = 3;
        EdServerSessionMessageCodec.WriteMembersBody(bytes, ref p, new(12, 4096, ids));
        var end = p;
        p = 3;
        var actual = EdServerSessionMessageCodec.ReadMembersBody(bytes, ref p);
        Assert.Equal(ids, actual.MachineRunIds);
        Assert.Equal(end, p);
    }

    [Fact]
    public void OversizeMemberCountIsRejectedBeforeAllocatingOrConsumingCursor()
    {
        var bytes = new byte[32];
        var p = 0;
        EdServerSessionMessageCodec.WriteMembersBody(bytes, ref p, new(1, 4096, new ulong[] { 1, 2 }));
        p = 0;
        Assert.Throws<InvalidDataException>(() => EdServerSessionMessageCodec.ReadMembersBody(bytes, ref p, 1));
        Assert.Equal(0, p);
    }

    [Fact]
    public void JoinedCombinedCountsRespectLocalBound()
    {
        var bytes = new byte[64];
        var p = 0;
        EdServerSessionMessageCodec.WriteJoinedBody(bytes, ref p, new(1, new ulong[] { 2, 3 }, new ulong[] { 4, 5 }));
        p = 0;
        Assert.Throws<InvalidDataException>(() => EdServerSessionMessageCodec.ReadJoinedBody(bytes, ref p, 3));
        Assert.Equal(0, p);
    }

    [Fact]
    public void TruncatedFixedAndCollectionBodiesDoNotAdvanceCursor()
    {
        var p = 1;
        Assert.Throws<InvalidDataException>(() => EdServerSessionMessageCodec.ReadRequestBody(new byte[14], ref p));
        Assert.Equal(1, p);
        p = 0;
        Assert.Throws<InvalidDataException>(() => EdServerSessionMessageCodec.ReadLeftBody(new byte[5], ref p));
        Assert.Equal(0, p);
        var bytes = new byte[64];
        EdServerSessionMessageCodec.WriteMembersBody(bytes, ref p, new(1, 4096, new ulong[] { 1, 2 }));
        p = 0;
        Assert.Throws<InvalidDataException>(() => EdServerSessionMessageCodec.ReadMembersBody(bytes.AsSpan(0, 15), ref p));
        Assert.Equal(0, p);
    }

    [Fact]
    public void Raw48BitIdsKeepReservedBitsWithoutGuessingSpecialObjectMapping()
    {
        var bytes = new byte[64];
        var p = 0;
        EdServerSessionMessageCodec.WriteJoinedBody(bytes, ref p, new(0, new ulong[] { 0xFFFFFFFF0000, 0xFFFFFFFFFFFF }, Array.Empty<ulong>()));
        p = 0;
        var parsed = EdServerSessionMessageCodec.ReadJoinedBody(bytes, ref p);
        Assert.Equal(0UL, parsed.SessionId);
        Assert.Equal(new ulong[] { 0xFFFFFFFF0000, 0xFFFFFFFFFFFF }, parsed.FirstObjectIds);
    }

    [Fact]
    public void WriterRejectsOversizeIdsAndTooSmallBuffersBeforeMutation()
    {
        var bytes = Enumerable.Repeat((byte)0xAA, 20).ToArray();
        var original = bytes.ToArray();
        var p = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => EdServerSessionMessageCodec.WriteRequestBody(bytes, ref p, new(1UL << 48, 0)));
        Assert.Throws<InvalidDataException>(() => EdServerSessionMessageCodec.WriteMembersBody(bytes, ref p,
            new(1, 4096, new ulong[] { 1, 2, 3 })));
        Assert.Equal(original, bytes);
        Assert.Equal(0, p);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65535)]
    public void InvalidCollectionBoundsAreRejected(int maximum)
    {
        var p = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => EdServerSessionMessageCodec.ReadMembersBody(new byte[20], ref p, maximum));
    }
}
