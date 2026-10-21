using System.Text;
using System.Text.Json;
namespace XFEEliteDangerous.Core;
internal static class NativeBookmarkOperations
{
    internal const int Capacity=500; // Explicit private-server quota, advertised in every response.
    internal static NativeDataReply? Handle(AccountState account,string method,string path,IReadOnlyDictionary<string,string> query,byte[] body)
    {
        if(path!="/2.0/elite/bookmarks")return null;
        NativeProfileData.VerifyCommander(account,query);
        if(method=="GET"){NativeProfileData.Empty(body);return Response(account,[]);}
        NativeProfileData.Method(method,"PUT");using var document=NativeJson.Parse(body);
        var root=document.RootElement;NativeJson.Fields(root,"edit","delete");
        var edits=new List<NativeBookmark>();var deleted=new HashSet<ulong>();
        if(root.TryGetProperty("edit",out var editing))
        {
            if(editing.ValueKind!=JsonValueKind.Array||editing.GetArrayLength()>Capacity)throw new NativeDataException(400,"Invalid native bookmark edits.");
            foreach(var entry in editing.EnumerateArray())
            {
                NativeJson.Fields(entry,"id","systemAddr","bodyAddr","marketId","bookmarkName","iconName","bodyType","isCluster","hasRings","favourite");
                edits.Add(new(){Id=NativeJson.U64(entry,"id"),SystemAddr=NativeJson.U64(entry,"systemAddr"),BodyAddr=NativeJson.U64(entry,"bodyAddr"),
                    MarketId=NativeJson.U64(entry,"marketId"),BookmarkName=NativeJson.Text(entry,"bookmarkName",256),IconName=NativeJson.U32(entry,"iconName"),
                    BodyType=NativeJson.U32(entry,"bodyType"),IsCluster=NativeJson.Boolean(entry,"isCluster"),HasRings=NativeJson.Boolean(entry,"hasRings"),Favourite=NativeJson.Boolean(entry,"favourite")});
            }
            if(edits.Select(value=>value.Id).Distinct().Count()!=edits.Count)throw new NativeDataException(400,"Duplicate native bookmark edit identity.");
        }
        if(root.TryGetProperty("delete",out var deleting))
        {
            if(deleting.ValueKind!=JsonValueKind.Array||deleting.GetArrayLength()>Capacity)throw new NativeDataException(400,"Invalid native bookmark deletions.");
            foreach(var entry in deleting.EnumerateArray())
                if(entry.ValueKind!=JsonValueKind.Number||!entry.TryGetUInt64(out var id)||!deleted.Add(id))throw new NativeDataException(400,"Invalid native bookmark deletion identity.");
        }
        if(!root.EnumerateObject().Any()||edits.Any(value=>deleted.Contains(value.Id)))throw new NativeDataException(400,"Conflicting native bookmark operations.");
        var next=account.NativeBookmarks.ToDictionary(value=>value.Id);
        foreach(var id in deleted)next.Remove(id);foreach(var entry in edits)next[entry.Id]=entry;
        if(next.Count>Capacity)throw new NativeDataException(409,"Private bookmark quota exceeded.");
        account.NativeBookmarks=next.Values.OrderBy(value=>value.Id).ToList();
        return Response(account,deleted.Order().ToArray());
    }
    private static NativeDataReply Response(AccountState account,ulong[] deleted)=>NativeDataReply.Json(new
    {bookmarks=account.NativeBookmarks.Select(value=>value.Public()).ToArray(),bookmarksDeleted=deleted,bookmarksAvailable=Capacity-account.NativeBookmarks.Count,
        squadron_bookmarks=Array.Empty<object>()});
    internal static bool Valid(IReadOnlyList<NativeBookmark>? entries)=>entries is not null&&entries.Count<=Capacity
        &&entries.All(value=>value is not null&&value.BookmarkName is {} name&&name.Length<=256&&!name.Any(char.IsControl))
        &&entries.Select(value=>value.Id).Distinct().Count()==entries.Count;
}
