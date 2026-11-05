using System.Globalization;
using System.Text;

namespace XFEEliteDangerous.Client;

/// <summary>Read Steam's local library and app manifests. Discovery never starts Steam or a game.</summary>
public static class SteamGameDiscovery
{
    private const string AppId = "359320";
    private const int MaxManifestBytes = 4 * 1024 * 1024;

    public static IEnumerable<string> FindOdyssey(IEnumerable<string> steamRoots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in steamRoots)
        {
            if (LocalDirectory(root) is not { } steam) continue;
            foreach (var library in Libraries(steam))
            {
                if (!seen.Add(library)) continue;
                var apps = Path.Combine(library, "steamapps");
                var manifest = ReadObject(Path.Combine(apps, "appmanifest_" + AppId + ".acf"), "AppState");
                if (manifest?.GetValueOrDefault("appid") is not string id || id != AppId
                    || manifest.GetValueOrDefault("installdir") is not string name || !IsFolderName(name)) continue;
                var candidate = FindExecutable(Path.Combine(apps, "common", name));
                if (candidate is not null) yield return candidate;
            }
        }
    }

    private static IEnumerable<string> Libraries(string steam)
    {
        yield return steam;
        foreach (var relative in new[] { Path.Combine("steamapps", "libraryfolders.vdf"), Path.Combine("config", "libraryfolders.vdf") })
        {
            if (ReadObject(Path.Combine(steam, relative), "libraryfolders") is not { } folders) continue;
            foreach (var (key, value) in folders)
            {
                if (!uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                var path = value as string ?? (value as Dictionary<string, object>)?.GetValueOrDefault("path") as string;
                if (path is not null && LocalDirectory(path) is { } library) yield return library;
            }
        }
    }

    private static bool IsFolderName(string name) => name.Length is > 0 and <= 255 && name is not ("." or "..")
        && name == name.Trim() && !name.EndsWith('.') && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.Contains('\\') && !name.Contains('/') && !name.Contains(':');

    private static string? LocalDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(LocalPaths.CheckedDiscoveryPath(path)); }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    private static string? FindExecutable(string directory)
    {
        try
        {
            var executable = OdysseyGame.ExecutableFromSelection(directory);
            return File.Exists(executable) ? executable : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    private static Dictionary<string, object>? ReadObject(string path, string key)
    {
        try
        {
            using var file = new FileStream(LocalPaths.CheckedDiscoveryPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length is <= 0 or > MaxManifestBytes) return null;
            var buffer = new byte[(int)file.Length];
            file.ReadExactly(buffer);
            var document = new KeyValues(new UTF8Encoding(false, true).GetString(buffer).TrimStart('\uFEFF')).Read();
            return document.GetValueOrDefault(key) as Dictionary<string, object>;
        }
        catch (Exception error) when (error is IOException or FormatException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    private sealed class KeyValues(string text)
    {
        private int position;
        public Dictionary<string, object> Read() => Object(0);

        private Dictionary<string, object> Object(int depth)
        {
            if (depth > 24) throw new FormatException("Steam manifest nesting is too deep.");
            var rows = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                Space();
                if (position == text.Length && depth == 0) return rows;
                if (position < text.Length && text[position] == '}' && depth != 0) { position++; return rows; }
                var key = Token();
                Space();
                if (position < text.Length && text[position] == '{') { position++; rows[key] = Object(depth + 1); }
                else rows[key] = Token();
            }
        }

        private string Token()
        {
            Space();
            if (position == text.Length || text[position] is '{' or '}') throw new FormatException("Invalid Steam manifest token.");
            if (text[position] != '"')
            {
                var start = position;
                while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}' or '"')) position++;
                return text[start..position];
            }
            position++;
            var token = new StringBuilder();
            while (position < text.Length)
            {
                var c = text[position++];
                if (c == '"') return token.ToString();
                if (c == '\\' && position < text.Length && text[position] is '\\' or '"') c = text[position++];
                token.Append(c);
            }
            throw new FormatException("Unterminated Steam manifest token.");
        }

        private void Space()
        {
            while (position < text.Length)
            {
                if (char.IsWhiteSpace(text[position])) { position++; continue; }
                if (text[position] != '/' || position + 1 == text.Length || text[position + 1] != '/') return;
                while (position < text.Length && text[position] is not ('\r' or '\n')) position++;
            }
        }
    }
}
