using Microsoft.Win32;
using System.Security;
using XFEEliteDangerous.Client;

namespace XFEEliteDangerous.Bootstrapper;

internal sealed record GameSelection(VerifiedOdysseyImage Image, string Source) : IDisposable
{
    public void Dispose() => Image.Dispose();

    internal static async Task<GameSelection?> ResolveAsync(string? path, string? steamFixture, bool interactive)
    {
        if (path is not null) return new(await VerifiedOdysseyImage.OpenAsync(path), "argument");
        string? rejection = null;
        async Task<GameSelection?> TryAsync(string candidate, string source)
        {
            try { return new(await VerifiedOdysseyImage.OpenAsync(candidate), source); }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
            { rejection = error.Message; return null; }
        }

        var candidates = SteamGameDiscovery.FindOdyssey(steamFixture is null ? SteamRoots() : [steamFixture]);
        foreach (var candidate in candidates)
        {
            // Fixture overrides are diagnostic-only and must never start a real Steam installation on this host.
            if (steamFixture is not null && !SafePaths.Within(SafePaths.ArtifactsRoot(), candidate)) continue;
            if (await TryAsync(candidate, "steam") is { } detected) return detected;
        }
        if (!interactive) throw new InvalidDataException(rejection ?? AppText.Get("Launch.NoCompatibleSteam"));

        var remembered = steamFixture is null ? ReadPreference() : null;
        if (remembered is not null && await TryAsync(remembered, "remembered") is { } saved) return saved;
        if (rejection is not null) Console.Error.WriteLine(rejection);
        Console.WriteLine(AppText.Get("Launch.NoCompatibleSteam"));
        while (await GameFilePicker.PickAsync() is { } selected)
        {
            if (await TryAsync(selected, "manual") is { } manual)
            {
                if (steamFixture is null) WritePreference(manual.Image.Executable);
                return manual;
            }
            Console.Error.WriteLine(rejection);
        }
        return null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        if (ReadRegistry(RegistryHive.CurrentUser, RegistryView.Default, "SteamPath") is { } user) yield return user;
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            if (ReadRegistry(RegistryHive.LocalMachine, view, "InstallPath") is { } machine) yield return machine;
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            if (Environment.GetFolderPath(folder) is { Length: > 0 } programFiles) yield return Path.Combine(programFiles, "Steam");
    }

    private static string? ReadRegistry(RegistryHive hive, RegistryView view, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var steam = root.OpenSubKey(@"Software\Valve\Steam");
            return steam?.GetValue(name) as string;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { return null; }
    }

    private static string Preference => LocalPaths.FilePath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XFEEliteDangerous", "solo-game-path.txt"));

    private static string? ReadPreference()
    {
        try
        {
            var path = Preference;
            return File.Exists(path) && new FileInfo(path).Length <= 32 * 1024 ? File.ReadAllText(path) : null;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    private static void WritePreference(string executable)
    {
        try
        {
            var path = Preference;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, executable);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { Console.Error.WriteLine(AppText.Get("Launch.GamePathNotRemembered")); }
    }
}
