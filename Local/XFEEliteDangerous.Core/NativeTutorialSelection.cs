using XFEEliteDangerous.Client;
using System.Globalization;
using System.Text.Json.Serialization;

namespace XFEEliteDangerous.Core;

/// <summary>
/// Explicit administrator selection for the private tutorial state. This is not
/// evidence that a player completed a tutorial, or that native gameplay works.
/// The closed input types deliberately do not reuse the defaulted persisted model.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeTutorialUpdate
{
    public required string Version { get; init; }
    public required NativeTutorialSelection NativeTutorial { get; init; }

    public NativeTutorialProfile ValidateFor(AccountState account)
    {
        if (Version is null || !long.TryParse(Version, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            || revision < 0 || Version != revision.ToString(CultureInfo.InvariantCulture) || NativeTutorial is null)
            throw new InvalidDataException("A current inventory version string and explicit tutorial state are required.");
        var profile = NativeTutorial.ToProfile();
        if (revision != account.Commander.Revision)
            throw new InvalidDataException(AppText.Get("NativeTutorialSelection.Text382"));
        if (account.NativeSave is not null && !profile.HasChosenStarterPack)
            throw new InvalidDataException("An imported native commander cannot return to new-commander selection.");
        if (profile.HasChosenStarterPack)
        {
            if (account.NativeSave is null)
                throw new InvalidDataException("Import a complete native save with a selected vessel first.");
            try
            {
                // This validates the complete typed document, private commander
                // identity and the editable projection's actual active vessel.
                // It does not certify the candidate's world or spawn semantics.
                account.NativeSave.ValidateCurrent(account);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
            { throw new InvalidDataException("The native commander or its selected vessel is inconsistent."); }
        }
        return profile;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeTutorialSelection
{
    public required bool HasChosenStarterPack { get; init; }
    public required NativeTutorialSelectionProgress Flight { get; init; }
    public required NativeTutorialSelectionProgress OnFoot { get; init; }

    public static NativeTutorialSelection FromProfile(NativeTutorialProfile profile) => new()
    {
        HasChosenStarterPack = profile.HasChosenStarterPack,
        Flight = new() { CurrentCheckpoint = profile.Flight.CurrentCheckpoint, Complete = profile.Flight.Complete },
        OnFoot = new() { CurrentCheckpoint = profile.OnFoot.CurrentCheckpoint, Complete = profile.OnFoot.Complete }
    };

    internal NativeTutorialProfile ToProfile()
    {
        if (Flight is null || OnFoot is null) throw new InvalidDataException("Both tutorial progress records are required.");
        return new()
        {
            HasChosenStarterPack = HasChosenStarterPack,
            Flight = new() { CurrentCheckpoint = Flight.CurrentCheckpoint, Complete = Flight.Complete },
            OnFoot = new() { CurrentCheckpoint = OnFoot.CurrentCheckpoint, Complete = OnFoot.Complete }
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeTutorialSelectionProgress
{
    public required uint CurrentCheckpoint { get; init; }
    public required bool Complete { get; init; }
}
