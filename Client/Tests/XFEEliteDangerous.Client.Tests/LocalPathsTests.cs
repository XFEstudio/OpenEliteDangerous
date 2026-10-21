using XFEEliteDangerous.Client;
using Xunit;

namespace XFEEliteDangerous.Client.Tests;

public sealed class LocalPathsTests
{
    [Theory]
    [InlineData(@"\\server\share\save")]
    [InlineData("//server/share/save")]
    [InlineData(@"\\?\C:\save")]
    [InlineData(@"\\.\pipe\save")]
    [InlineData("")]
    public void NetworkAndDevicePathsAreRejectedWithoutOpeningThem(string path) => Assert.Throws<ArgumentException>(() => LocalPaths.Directory(path));
    [Fact] public void LocalDirectoryIsNormalized() => Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), LocalPaths.Directory(Path.Combine(Path.GetTempPath(), ".")).TrimEnd(Path.DirectorySeparatorChar));
}
