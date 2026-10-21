namespace XFEEliteDangerous.Core;

/// <summary>
/// Read services for the explicitly fresh local profile subset. This model has
/// no imported reputation, vouchers, engineer progression or trading history.
/// It does not project a captured nonempty commander into empty collections.
/// </summary>
internal static class NativeCommanderServices
{
    private static readonly string[] ranks=["combat","trade","explore","crime","service","empire","federation","power","cqc","soldier","exobiologist","builder","learner"];
    internal static NativeDataReply? Handle(AccountState account,string method,string path,IReadOnlyDictionary<string,string> fields,byte[] body)
    {
        if(path is not("/2.0/elite/commander/reputation" or "/2.0/elite/voucher/list"
            or "/2.0/elite/crafting/engineer/progression" or "/2.0/elite/commander/tradesystems" or "/2.0/elite/inbox" or "/2.0/elite/user/arcs/redeem"
            or "/2.0/elite/codex/discovery/summary" or "/2.0/elite/crafting/engineer/pin" or "/2.0/elite/crafting/specials"
            or "/2.0/elite/fss/signals" or "/2.0/elite/starsystem/factionconflicts"))return null;
        NativeProfileData.VerifyCommander(account,fields);NativeProfileData.Empty(body);
        var save=account.NativeSave??throw new NativeDataException(409,"A complete fresh local commander is required.");
        if(path=="/2.0/elite/user/arcs/redeem")
        {
            NativeProfileData.Method(method,"PUT");
            // This local ledger has no automatic reward rules configured.
            // Redeeming its zero pending earnings cannot grant official value.
            return NativeDataReply.Json(new{balance=new{total_balance=account.NativeArx.TotalBalance,current_week_earnings=0,
                earned_delta=0,earned_delta_combat=0,earned_delta_trade=0,earned_delta_exploration=0,earned_delta_other=0,week_cap=400}});
        }
        NativeProfileData.Method(method,"GET");
        if(path is "/2.0/elite/fss/signals" or "/2.0/elite/starsystem/factionconflicts")
            foreach(var name in new[]{"systemAddr","systemAddress"})
                if(fields.TryGetValue(name,out var address)&&(!ulong.TryParse(address,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var parsed)
                    ||parsed!=save.Profile.CurrentSystem))throw new NativeDataException(404,"This system is outside the selected local world.");
        return path switch
        {
            //11343C0 accepts an empty regions array and leaves the actual fresh
            //local discovery collection empty. Never copy an online commander's
            //discovered totals into this account.
            "/2.0/elite/codex/discovery/summary"=>NativeDataReply.Json(new{regions=Array.Empty<object>()}),
            "/2.0/elite/crafting/engineer/pin"=>NativeDataReply.Json(new{recipes=Array.Empty<object>()}),
            //No local special recipes/engineers are configured in this subset.
            "/2.0/elite/crafting/specials"=>NativeDataReply.Json(new{recipes=Array.Empty<object>(),progressPercent=0,
                reputationThresholds=new Dictionary<string,int>(),reputationRewards=new Dictionary<string,int>()}),
            "/2.0/elite/fss/signals"=>NativeDataReply.Json(new{settings=new{fssSignalMode=3},signals=Array.Empty<object>()}),
            "/2.0/elite/starsystem/factionconflicts"=>NativeDataReply.Json(new{conflicts=Array.Empty<object>(),battlesiteRewards=Array.Empty<object>()}),
            "/2.0/elite/commander/reputation"=>NativeDataReply.Json(new
            {
                powerId=0,powerJoinTime=0,totalMerits=0,meritsLastTurn=0,meritsThisTurn=0,rankPower=0,scoreThisDay=0,dailyScoreLimit=0,
                power_id=0,powerTs=0,votesRemaining=0,powerRankStats=Array.Empty<object>(),reputations=Array.Empty<object>(),
                rank=ranks.ToDictionary(value=>value,_=>0),rankProgress=ranks.ToDictionary(value=>value,_=>0),
                loanCeiling=save.Profile.LoanCeiling,alliedCount=0
            }),
            "/2.0/elite/voucher/list"=>new(200,"<data><vouchers /></data>"),
            "/2.0/elite/crafting/engineer/progression"=>NativeDataReply.Json(new
                {engineers=new Dictionary<string,object>(),onfootengineers=new Dictionary<string,object>()}),
            "/2.0/elite/inbox"=>new(200,"<data><messages /><refresh>"+(DateTimeOffset.UtcNow.ToUnixTimeSeconds()+300).ToString(System.Globalization.CultureInfo.InvariantCulture)+"</refresh></data>"),
            _=>NativeDataReply.Json(new{starsystems=Array.Empty<object>()})
        };
    }
}
