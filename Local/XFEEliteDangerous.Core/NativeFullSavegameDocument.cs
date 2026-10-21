using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XFEEliteDangerous.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeSavegameGameVersion([property: JsonRequired] string Name, [property: JsonRequired] string Semver,
    [property: JsonRequired] uint Version, [property: JsonRequired] uint Dev);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeProvingGroundSnapshot([property: JsonRequired] uint EliteRank, [property: JsonRequired] uint Rank,
    [property: JsonRequired] uint RankXp, [property: JsonRequired] uint TotalXp);

// These markers deliberately have no extension-data bag. Unknown nonempty input must be rejected, not erased.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeEmptyObject;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeEmptyArray;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeEmptyMicroresources
{
    public required NativeEmptyObject Inventory { get; init; }
    public required NativeEmptyObject Discovered { get; init; }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeEmptyOnboardingObjectives
{
    public required NativeEmptyObject Objectives { get; init; }
    public required NativeEmptyObject ExtraData { get; init; }
}

/// <summary>
/// Explicit declaration of genuinely empty private collections. Do not use this to project or discard an
/// existing nonempty profile. Unknown nonempty schemas are unsupported; there is no implicit empty fallback.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeFullSavegameEmptyCollections
{
    public required NativeEmptyObject Rank { get; init; }
    public required NativeEmptyObject Constants { get; init; }
    public required NativeEmptyObject Pinned { get; init; }
    public required NativeEmptyArray CurrentCrew { get; init; }
    public required NativeEmptyArray Suits { get; init; }
    public required NativeEmptyArray WeaponRack { get; init; }
    public required NativeEmptyArray SystemHyperspacePermits { get; init; }
    public required NativeEmptyArray RegionHyperspacePermits { get; init; }
    public required NativeEmptyArray LandingPermits { get; init; }
    public required NativeEmptyArray LegacyFines { get; init; }
    public required NativeEmptyArray LegalStates { get; init; }
    public required NativeEmptyArray TaxiBookings { get; init; }
    public required NativeEmptyMicroresources Microresources { get; init; }
    public required NativeEmptyObject ShipLocker { get; init; }
    public required NativeEmptyOnboardingObjectives OnboardingObjectives { get; init; }
}

/// <summary>
/// Additional explicit data required by the ordinary non-Cqc/non-multicrew full reader. Identity, credits,
/// alive, location and selected vessel IDs have a single source in NativeSavegameSnapshot.
/// Avatar text is caller-supplied opaque content; empty strings select the verified fresh no-avatar skip
/// branch, without proving that later spawning accepts it. StartupState is never defaulted or interpreted.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeFullSavegameProfile
{
    public required string Name { get; init; }
    public required long LoanCeiling { get; init; }
    public required long Debt { get; init; }
    public required uint StartupState { get; init; }
    public required string Gender { get; init; }
    public required NativeProvingGroundSnapshot ProvingGround { get; init; }
    public required uint BattleRating { get; init; }
    public required string Avatar { get; init; }
    public required string AvatarXml { get; init; }
    public required int VehicleDistanceTravelled { get; init; }
    public required uint AtrLevel { get; init; }
    public required int Notoriety { get; init; }
    public required bool MarkedWanted { get; init; }
    public required string CurrentSystemKey { get; init; }
    public required ulong CurrentSystem { get; init; }
    public required NativeSavegameGameVersion GameVersion { get; init; }
    public required NativeFullSavegameEmptyCollections EmptyCollections { get; init; }
}

/// <summary>
/// Composes the startup ship document with explicit full-reader profile fields and genuine empty inventory
/// containers. This validates the pinned field/type and key-pair contracts only, not world resources,
/// native parser execution, avatar semantics, account ownership or playable readiness.
/// </summary>
public static class NativeFullSavegameDocument
{
    public static string SerializeComplete(NativeSavegameSnapshot snapshot, NativeSavegameCatalogue catalogue,
        NativeFullSavegameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(snapshot);
        if(snapshot.Vessels is null||snapshot.Vessels.Any(ship=>ship is null||ship.LastLocation is null))
            throw new ArgumentException("The full fleet reader requires each vessel's explicit previous UA location.",nameof(snapshot));
        ValidateProfile(profile);
        // Reuse the strict ship/module/location validator. No partial document is returned on failure.
        using var startup = JsonDocument.Parse(NativeSavegameDocument.Serialize(snapshot, catalogue));
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("gameVersion");
            writer.WriteString("name", profile.GameVersion.Name); writer.WriteString("semver", profile.GameVersion.Semver);
            writer.WriteNumber("version", profile.GameVersion.Version); writer.WriteNumber("dev", profile.GameVersion.Dev);
            writer.WriteEndObject();
            writer.WriteStartObject("commander");
            writer.WriteNumber("id", snapshot.CommanderNativeId); writer.WriteString("name", profile.Name);
            foreach (var field in startup.RootElement.GetProperty("commander").EnumerateObject()) field.WriteTo(writer);
            writer.WriteNumber("loanCeiling", profile.LoanCeiling); writer.WriteNumber("debt", profile.Debt);
            EmptyObject(writer, "rank"); writer.WriteNumber("startupState", profile.StartupState);
            writer.WriteString("gender", profile.Gender);
            writer.WriteStartObject("provingGround");
            writer.WriteNumber("eliteRank", profile.ProvingGround.EliteRank); writer.WriteNumber("rank", profile.ProvingGround.Rank);
            writer.WriteNumber("rankXP", profile.ProvingGround.RankXp); writer.WriteNumber("totalXP", profile.ProvingGround.TotalXp);
            writer.WriteEndObject();
            writer.WriteNumber("battleRating", profile.BattleRating); writer.WriteString("avatar", profile.Avatar);
            writer.WriteString("avatarXML", profile.AvatarXml); writer.WriteNumber("vehicleDistanceTravelled", profile.VehicleDistanceTravelled);
            writer.WriteNumber("atrLevel", profile.AtrLevel); writer.WriteNumber("notoriety", profile.Notoriety);
            writer.WriteBoolean("markedWanted", profile.MarkedWanted);
            writer.WriteString("currentSystemKey", profile.CurrentSystemKey); writer.WriteNumber("currentSystem", profile.CurrentSystem);
            EmptyArray(writer, "currentCrew"); writer.WriteEndObject();
            writer.WritePropertyName("vessels"); startup.RootElement.GetProperty("vessels").WriteTo(writer);
            EmptyObject(writer, "constants"); EmptyObject(writer, "pinned");
            foreach (var name in new[] { "suits", "weaponRack", "systemHyperspacePermits", "regionHyperspacePermits",
                         "landingPermits", "legacyFines", "legalStates", "taxiBookings" }) EmptyArray(writer, name);
            writer.WriteStartObject("microresources"); EmptyObject(writer, "inventory"); EmptyObject(writer, "discovered"); writer.WriteEndObject();
            EmptyObject(writer, "shipLocker");
            writer.WriteStartObject("onboardingObjectives"); EmptyObject(writer, "objectives"); EmptyObject(writer, "extraData"); writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static void ValidateProfile(NativeFullSavegameProfile profile)
    {
        Text(profile.Name, nameof(profile.Name), 64, false);
        Text(profile.Gender, nameof(profile.Gender), 16, false);
        if (!string.Equals(profile.Gender, "M", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(profile.Gender, "F", StringComparison.OrdinalIgnoreCase))
            Invalid("Explicit M/F gender is required; unknown text would retain an unset native constructor value.");
        Text(profile.Avatar, nameof(profile.Avatar), 262_144, true);
        Text(profile.AvatarXml, nameof(profile.AvatarXml), 262_144, true);
        Required(profile.GameVersion, nameof(profile.GameVersion));
        Text(profile.GameVersion.Name, "Game version name", 200, true);
        Text(profile.GameVersion.Semver, "Game version semver", 200, true);
        Required(profile.ProvingGround, nameof(profile.ProvingGround));
        if (profile.CurrentSystemKey is null || profile.CurrentSystemKey.Length is < 1 or > 20)
            Invalid("CurrentSystemKey must be a bounded complete UInt64 decimal string.");
        if (!ulong.TryParse(profile.CurrentSystemKey, NumberStyles.None, CultureInfo.InvariantCulture, out var key)
            || key.ToString(CultureInfo.InvariantCulture) != profile.CurrentSystemKey)
            Invalid("CurrentSystemKey must be a canonical complete UInt64 decimal string.");
        if (key == profile.CurrentSystem || ((key >> 51) & 0x1FF0UL) != ((key ^ profile.CurrentSystem) & 0x1FF0UL))
            Invalid("CurrentSystemKey and CurrentSystem fail the pinned native pair check; no world is inferred from that check.");
        Required(profile.EmptyCollections, nameof(profile.EmptyCollections));
        var empty = profile.EmptyCollections;
        Required(empty.Rank, "Empty rank"); Required(empty.Constants, "Empty constants"); Required(empty.Pinned, "Empty pinned");
        Required(empty.CurrentCrew, "Empty current crew"); Required(empty.Suits, "Empty suits"); Required(empty.WeaponRack, "Empty weapon rack");
        Required(empty.SystemHyperspacePermits, "Empty system permits"); Required(empty.RegionHyperspacePermits, "Empty region permits");
        Required(empty.LandingPermits, "Empty landing permits"); Required(empty.LegacyFines, "Empty legacy fines");
        Required(empty.LegalStates, "Empty legal states"); Required(empty.TaxiBookings, "Empty taxi bookings");
        Required(empty.ShipLocker, "Empty ship locker"); Required(empty.Microresources, "Empty microresources");
        Required(empty.Microresources.Inventory, "Empty microresource inventory"); Required(empty.Microresources.Discovered, "Empty discovered microresources");
        Required(empty.OnboardingObjectives, "Empty onboarding objectives");
        Required(empty.OnboardingObjectives.Objectives, "Empty onboarding objective entries");
        Required(empty.OnboardingObjectives.ExtraData, "Empty onboarding extra data");
    }

    private static void Text(string? value, string name, int maximum, bool allowEmpty)
    {
        // Avatar/XML may contain legitimate whitespace; opaque content is not parsed or proclaimed valid.
        if (value is null || value.Length > maximum || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Contains('\0'))
            Invalid($"{name} must be explicitly supplied bounded text without NUL.");
    }
    private static void Required(object? value, string name)
    {
        if (value is null) Invalid($"{name} must be explicitly declared; missing state is not replaced with an empty container.");
    }
    private static void EmptyObject(Utf8JsonWriter writer, string name) { writer.WriteStartObject(name); writer.WriteEndObject(); }
    private static void EmptyArray(Utf8JsonWriter writer, string name) { writer.WriteStartArray(name); writer.WriteEndArray(); }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid(string message) => throw new ArgumentException(message);
}
