using XFEEliteDangerous.NativeFormat.Replication;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class EdServerObjectIdAllocatorTests
{
    [Fact]
    public void DifferentSessionsShareOneNonoverlappingProcessRange()
    {
        var allocator = new NativeObjectIdAllocator();
        var a = allocator.AllocateBlock("launch-a");
        var b = allocator.AllocateBlock("launch-b");
        var c = allocator.AllocateBlock("launch-a");
        Assert.Equal(4096UL, a.Base);
        Assert.Equal(8192UL, b.Base);
        Assert.Equal(12288UL, c.Base);
        Assert.Equal(4096UL, a.Count);
        Assert.Equal(a.Base + 4095, a.Last);
        Assert.True(a.Last < b.Base && b.Last < c.Base);
        Assert.Equal(0UL, a.SessionBlockIndex);
        Assert.Equal(0UL, b.SessionBlockIndex);
        Assert.Equal(1UL, c.SessionBlockIndex);
        Assert.Equal(3UL, allocator.AllocatedBlockCount);
    }

    [Fact]
    public void LastOrdinaryBlockCannotSpillIntoReservedIds()
    {
        var allocator = new NativeObjectIdAllocator(NativeObjectIdAllocator.ReservedStart - 4096);
        var block = allocator.AllocateBlock("launch");
        Assert.Equal(NativeObjectIdAllocator.ReservedStart - 1, block.Last);
        Assert.Throws<InvalidOperationException>(() => allocator.AllocateBlock("another-launch"));
        Assert.Equal(1UL, allocator.AllocatedBlockCount);
    }

    [Fact]
    public void ExplicitSmallRangeHasExact4096Boundary()
    {
        var allocator = new NativeObjectIdAllocator(4096, 12288);
        Assert.Equal(4096UL, allocator.AllocateBlock("launch").Base);
        Assert.Equal(8192UL, allocator.AllocateBlock("launch").Base);
        Assert.Throws<InvalidOperationException>(() => allocator.AllocateBlock("launch"));
    }

    [Fact]
    public async Task ConcurrentRequestsAcrossSessionsCannotReuseIds()
    {
        var allocator = new NativeObjectIdAllocator();
        var values = await Task.WhenAll(Enumerable.Range(0, 256)
            .Select(index => Task.Run(() => allocator.AllocateBlock("launch-" + (index % 8)))));
        Assert.Equal(256, values.Select(value => value.Base).Distinct().Count());
        var sorted = values.OrderBy(value => value.Base).ToArray();
        Assert.All(sorted, value => Assert.Equal(0UL, value.Base % 4096));
        for (var index = 1; index < sorted.Length; index++)
            Assert.True(sorted[index - 1].Last < sorted[index].Base);
        Assert.Equal(256UL, allocator.AllocatedBlockCount);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(4095UL)]
    [InlineData(4097UL)]
    [InlineData(NativeObjectIdAllocator.ReservedStart)]
    [InlineData(ulong.MaxValue)]
    public void InvalidBasesCannotWrapOrEnterReservedSpace(ulong start) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeObjectIdAllocator(start));

    [Theory]
    [InlineData(0UL)]
    [InlineData(4096UL)]
    [InlineData(4097UL)]
    [InlineData(ulong.MaxValue)]
    public void InvalidLimitsCannotWrapOrEnterReservedSpace(ulong limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeObjectIdAllocator(4096, limit));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("launch\ncredential")]
    public void InvalidLocalSessionKeysCannotConsumeIds(string key)
    {
        var allocator = new NativeObjectIdAllocator();
        Assert.Throws<ArgumentException>(() => allocator.AllocateBlock(key));
        Assert.Equal(0UL, allocator.AllocatedBlockCount);
    }
}
