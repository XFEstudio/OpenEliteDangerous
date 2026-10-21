using System.Globalization;
using System.Text.Json;

namespace XFEEliteDangerous.Core;

internal static class NativeProfileData
{
    internal static NativeDataReply? Handle(AccountState account,string method,string path,IReadOnlyDictionary<string,string> query,byte[] body)
    {
        if(path is not("/2.0/elite/commander/settings" or "/2.0/elite/commander/tutorial" or "/2.0/elite/commander/tutorial/complete"))return null;
        VerifyCommander(account,query);
        if(path=="/2.0/elite/commander/settings")
        {
            if(method=="GET"){Empty(body);return NativeDataReply.Json(account.NativePreferences.Public());}
            Method(method,"PUT");
            using var document=JsonDocument.Parse(body,new(){MaxDepth=8});
            if(document.RootElement.ValueKind!=JsonValueKind.Object)throw new NativeDataException(400,"Native settings require a JSON object.");
            var seen=new HashSet<string>(StringComparer.Ordinal);bool? invites=null;string? privacy=null;
            foreach(var property in document.RootElement.EnumerateObject())
            {
                if(!seen.Add(property.Name))throw new NativeDataException(400,"Duplicate native setting.");
                switch(property.Name)
                {
                    case "accepting_squadron_invites":
                        if(property.Value.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new NativeDataException(400,"Invalid native invitation setting.");
                        invites=property.Value.GetBoolean();break;
                    case "privacy_mode":
                        if(property.Value.ValueKind!=JsonValueKind.String||property.Value.GetString() is not("PRIVATE" or "SQUADRON"))throw new NativeDataException(400,"Invalid native privacy mode.");
                        privacy=property.Value.GetString();break;
                    default:throw new NativeDataException(400,"Unsupported native setting.");
                }
            }
            if(seen.Count==0)throw new NativeDataException(400,"At least one native setting is required.");
            if(invites.HasValue)account.NativePreferences.AcceptingSquadronInvites=invites.Value;
            if(privacy is not null)account.NativePreferences.PrivacyMode=privacy;
            return new(200,"{}");
        }
        Empty(body);
        if(path=="/2.0/elite/commander/tutorial"&&method=="GET")return NativeDataReply.Json(account.NativeTutorial.Public());
        Method(method,"PUT");
        var type=Text(query,"type");
        if(type is not("flight" or "on_foot"))throw new NativeDataException(400,"Unsupported native tutorial type.");
        var progress=type=="flight"?account.NativeTutorial.Flight:account.NativeTutorial.OnFoot;
        if(path.EndsWith("/complete",StringComparison.Ordinal))
        {
            progress.Complete=true;
            if(type=="flight")account.NativeTutorial.FlightActive=false;else account.NativeTutorial.OnFootActive=false;
        }
        else
        {
            var checkpoint=Text(query,"checkpoint");var active=Text(query,"active");
            if(!uint.TryParse(checkpoint,NumberStyles.None,CultureInfo.InvariantCulture,out var number)||active is not("0" or "1"))
                throw new NativeDataException(400,"Invalid native tutorial progress.");
            progress.CurrentCheckpoint=number;
            if(type=="flight")account.NativeTutorial.FlightActive=active=="1";else account.NativeTutorial.OnFootActive=active=="1";
            // active is the independently observed activity state, never a
            // completion or an implicit starter-pack/materialization grant.
        }
        return new(200,"{}");
    }
    internal static void VerifyCommander(AccountState account,IReadOnlyDictionary<string,string> query)
    {
        if(query.TryGetValue("cmdrId",out var text)&&(!ulong.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var value)||value!=account.NativeUserId))
            throw new NativeDataException(401,"Native commander identity mismatch.");
    }
    internal static void Empty(byte[] body){if(body.Length!=0)throw new NativeDataException(400,"Native endpoint requires an empty request body.");}
    internal static void Method(string actual,string expected){if(actual!=expected)throw new NativeDataException(405,"Unsupported native endpoint method.");}
    internal static string Text(IReadOnlyDictionary<string,string> query,string field)=>query.TryGetValue(field,out var value)?value:throw new NativeDataException(400,"Missing native field: "+field);
}
