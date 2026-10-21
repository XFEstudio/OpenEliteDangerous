using System.Text;
using System.Xml.Linq;

namespace XFEEliteDangerous.Core;

internal static class NativeUserXml
{
    // Public content-mode names from this build's Odyssey user document. The
    // native season-four predicate 2609980 checks SPACE_LEGS separately from SKU.
    // These describe the private mode; the SKU table still carries no grants.
    private static readonly string[] OdysseyCapabilities=["SHIP","MULTICREW","PLANETARY_LANDINGS",
        "LANDING_AIRLESS","ALLOW_COMMS","GATED_CONTENT","LANDING_ATMOS_TENUOUS","SPACE_LEGS","OPERATIONS"];
    internal static string Write(AccountState account)
    {
        if (account.NativeUserId == 0) throw new InvalidOperationException("A private native identity must be allocated first.");
        var commander = account.Commander;
        var name = string.IsNullOrEmpty(commander.Name) ? account.DisplayName : commander.Name;
        var entry = new XElement("commander",
            new XElement("id", (ulong)account.NativeUserId), new XElement("name", Hex(name)),
            new XElement("credits", commander.Credits));
        var activeShip = commander.Ships.FirstOrDefault(ship => ship.Active);
        if (activeShip is not null)
        {
            // This private model currently controls the active owned hull directly;
            // it has no separately persisted SRV/on-foot child vessel. Both real
            // references therefore identify that same hull. Empty fleets stay empty.
            entry.Add(new XElement("currentShip", activeShip.Id), new XElement("parentShip", activeShip.Id));
        }
        // This is private-server metadata for the single audited client build, not
        // an official entitlement/version response. dev=0 is an explicit local
        // candidate; its relationship to a native build getter remains unverified.
        // Empty SKU advertises no product grants. No ship is invented for a new account.
        var root = new XElement("data", new XElement("sku"),
            // A local mode declaration, parsed independently from the SKU table.
            // It neither grants an official product nor asserts a loaded native world.
            new XElement("capabilities", OdysseyCapabilities.Select(value=>new XElement("item",value))),
            new XElement("gameVersion", new XElement("name", "Elite Dangerous: Odyssey"),
                new XElement("semver", "4.4.1.1"), new XElement("version", 332841U), new XElement("dev", 0U)),
            new XElement("user", new XElement("id", account.NativeUserId),
                new XElement("commsID", (ulong)account.NativeUserId), new XElement("displayName", Hex(account.DisplayName))),
            new XElement("commanders", entry), new XElement("privategroups"), new XElement("starterpacks"),
            new XElement("lastCommanderName", Hex(name)));
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static string Hex(string text) => Convert.ToHexString(Encoding.UTF8.GetBytes(text));
}
