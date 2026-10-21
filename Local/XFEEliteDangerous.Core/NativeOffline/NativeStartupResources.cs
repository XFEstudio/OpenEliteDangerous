using XFEEliteDangerous.Client;
namespace XFEEliteDangerous.Core;

/// <summary>Private startup resources. Empty regions/presets describe this server's actual
/// configuration; they neither create a ship nor implement a native game world.</summary>
internal static class NativeStartupResources
{
    internal static NativeDataReply? Read(string path) => path switch
    {
        "/2.0/elite/resources/dictionary" => NativeDataReply.Json(new
            { blacklist = Array.Empty<string>(), whitelist = Array.Empty<string>() }),
        "/2.0/elite/resources/ussregion/list" => new(200, "<response><regions /></response>"),
        "/2.0/elite/resources/loadouts" => new(200, "{}"),
        //1234E40 reads two object maps. The minimum private world has no
        // engineer definitions; this is distinct from a commander's unlocks.
        "/2.0/elite/crafting/engineer/list" => NativeDataReply.Json(new
            { engineers = new Dictionary<string, object>(), onfootengineers = new Dictionary<string, object>() }),
        // Do not serve a successful empty Powerplay catalogue. After1418580,
        //1420C20 builds its spatial bounds and3C42880 allocates the index.
        // An empty global catalogue reaches an invalid allocation (observed
        // native AV at489A5E9). NativeReferenceLibrary can supply a validated,
        // nonempty captured catalogue. Without that bundle this stays pending.
        // Both native news readers take documentRoot/news/item. No locally
        // published articles exist yet; an empty news node is a real zero-item list.
        "/2.0/elite/news/global" => new(200, "<response><news /></response>"),
        "/2.0/elite/resources/loadingscreentips" => NativeDataReply.Json(new
            { loadingscreentips = new[] { AppText.Get("NativeStartupResources.Text381") } }),
        // The private server has no dynamic BGS or colonisation summary records yet.
        // Its native paginator explicitly terminates on HTTP 204 before decoding a body.
        "/2.0/elite/starsystem/dailydigest_part" => new(204, ""),
        "/2.0/elite/colonisation/galaxydigest_claimed_part" => new(204, ""),
        "/2.0/elite/colonisation/galaxydigest_established_part" => new(204, ""),
        "/2.0/elite/starsystem/thargoiddailydigest" => NativeDataReply.Json(new
            { systems = new Dictionary<string, object>() }),
        // No private Thargoid body-site updates exist. This endpoint reads XML
        // immediate children, unlike the JSON daily system summary above.
        "/2.0/elite/bodysitestatemachine/progressionstatus" => new(200, "<progressionStatus />"),
        // This server currently configures no system-specific colonisation ban.
        // This is a restriction list, not the available construction catalog.
        "/2.0/elite/colonisation/claim/deny_starsystems" => NativeDataReply.Json(new
            { colonisation_deny_starsystems = Array.Empty<ulong>() }),
        _ => null
    };
}
