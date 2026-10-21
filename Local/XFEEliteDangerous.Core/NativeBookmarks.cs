using System.Text.Json;

namespace XFEEliteDangerous.Core;

public sealed record NativeBookmark
{
    public required ulong Id{get;init;}
    public required ulong SystemAddr{get;init;}
    public required ulong BodyAddr{get;init;}
    public required ulong MarketId{get;init;}
    public required string BookmarkName{get;init;}
    public required uint IconName{get;init;}
    public required uint BodyType{get;init;}
    public required bool IsCluster{get;init;}
    public required bool HasRings{get;init;}
    public required bool Favourite{get;init;}
    internal object Public()=>new{id=Id,systemAddr=SystemAddr,bodyAddr=BodyAddr,marketID=MarketId,bookmarkName=BookmarkName,
        iconName=IconName,bodyType=BodyType,isCluster=IsCluster,hasRings=HasRings,favourite=Favourite};
}

internal static class NativeBookmarkData
{
    internal const int Capacity=500;
    internal static bool Valid(IReadOnlyList<NativeBookmark>? entries)=>entries is not null&&entries.Count<=Capacity
        &&entries.All(value=>value is not null&&value.BookmarkName is {} name&&name.Length<=256&&!name.Any(char.IsControl))
        &&entries.Select(value=>value.Id).Distinct().Count()==entries.Count;
}
