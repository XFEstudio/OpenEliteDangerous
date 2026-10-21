using System.Text;
using System.Text.Json;
namespace XFEEliteDangerous.Core;
internal static class NativeSystemData
{
    internal static NativeDataReply? Handle(AccountState account,string method,string path,IReadOnlyDictionary<string,string> query,byte[] body)
    {
        if(path!="/2.0/elite/starsystem")return null;
        NativeProfileData.VerifyCommander(account,query);NativeProfileData.Method(method,"GET");NativeProfileData.Empty(body);
        if(!query.TryGetValue("systemAddr",out var text)||!ulong.TryParse(text,System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,out var address))throw new NativeDataException(400,"Native system address required.");
        if(query.TryGetValue("cachedTimeStamp",out var cached)&&!ulong.TryParse(cached,System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,out _))throw new NativeDataException(400,"Invalid native system cache timestamp.");
        var saved=account.NativeSave;
        if(saved is null||saved.SystemMetadata is null)return NativeDataReply.Json(new{error="NativeSystemMetadataNotInitialized",gameIntegrationReady=false},501);
        if(address!=saved.Profile.CurrentSystem)throw new NativeDataException(404,"Private system metadata is not recorded for that address.");
        // Always return the current explicit document. Timestamp units and native
        // cache-status semantics are not inferred from the query spelling.
        return new(200,saved.SystemMetadata.Serialize(saved.Profile.CurrentSystem));
    }
}
