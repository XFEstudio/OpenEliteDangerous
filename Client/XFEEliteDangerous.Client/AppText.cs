using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace XFEEliteDangerous.Client;

/// <summary>UI and operator messages only. Never use translated text as a protocol identifier.</summary>
public static class AppText
{
    private static readonly string SystemLanguage = Resolve(CultureInfo.CurrentUICulture.Name);
    private static string processLanguage = SystemLanguage;
    private static readonly AsyncLocal<string?> RequestLanguage = new();
    private static readonly IReadOnlyDictionary<string, string> English = Load("en-US");
    private static readonly IReadOnlyDictionary<string, string> Chinese = Load("zh-CN");
    public static string Language => RequestLanguage.Value ?? Volatile.Read(ref processLanguage);
    public static CultureInfo DisplayCulture => CultureInfo.GetCultureInfo(Language);
    public static IReadOnlyDictionary<string, string> Catalog(string language) => Resolve(language) == "zh-CN" ? Chinese : English;

    public static string NormalizePreference(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        null or "" or "system" or "auto" => "system",
        "en" or "en-us" or "en-gb" => "en-US",
        "zh" or "zh-cn" or "zh-hans" or "zh-hans-cn" => "zh-CN",
        _ => throw new ArgumentException("Supported languages: system, en-US, zh-CN.", nameof(language))
    };

    public static void Apply(string? preference)
    {
        var normalized = NormalizePreference(preference);
        var culture = CultureInfo.GetCultureInfo(normalized == "system" ? SystemLanguage : normalized);
        Volatile.Write(ref processLanguage, culture.Name);
        // Wire serializers and native parsers keep their existing numeric culture.
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static string[] ConfigureArguments(string[] arguments, string? defaultLanguage = "system")
    {
        var remaining = new List<string>();
        var language = Environment.GetEnvironmentVariable("XFE_LANGUAGE") ?? defaultLanguage;
        var found = false;
        for (var i = 0; i < arguments.Length; i++)
        {
            var argument = arguments[i];
            if (argument != "--language" && !argument.StartsWith("--language=", StringComparison.Ordinal)) { remaining.Add(argument); continue; }
            if (found) throw new ArgumentException("Duplicate --language option.");
            found = true;
            language = argument == "--language"
                ? ++i < arguments.Length ? arguments[i] : throw new ArgumentException("Missing --language value.")
                : argument["--language=".Length..];
            _ = NormalizePreference(language);
        }
        Apply(language);
        return remaining.ToArray();
    }

    public static string Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var catalog = Language == "zh-CN" ? Chinese : English;
        return catalog.TryGetValue(key, out var text) || English.TryGetValue(key, out text)
            ? text : throw new KeyNotFoundException("Missing localization key: " + key);
    }
    public static string Format(string key, params object?[] values) => string.Format(DisplayCulture, Get(key), values);

    /// <summary>Request-local culture; disposing restores the caller without changing process defaults.</summary>
    public static IDisposable UseLanguage(string? acceptLanguage)
    {
        var prior = CultureInfo.CurrentUICulture;
        var priorRequest = RequestLanguage.Value;
        var language = SelectRequestLanguage(acceptLanguage);
        RequestLanguage.Value = language;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        return new CultureScope(prior, priorRequest);
    }

    private static string SelectRequestLanguage(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return Language;
        // Bound untrusted HTTP headers and honor quality values, including exclusions (q=0).
        if (header.Length > 1024) return "en-US";
        var candidates = new List<(string language, double weight, int order)>();
        foreach (var part in header.Split(',').Take(20))
        {
            var fields = part.Trim().Split(';'); var tag = fields[0].Trim(); double weight = 1;
            if (fields.Length > 1 && (!fields[1].Trim().StartsWith("q=", StringComparison.OrdinalIgnoreCase) ||
                !double.TryParse(fields[1].Trim()[2..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out weight))) continue;
            if (weight <= 0 || weight > 1 || !double.IsFinite(weight)) continue;
            var language = tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" :
                tag == "*" || tag.Equals("en", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ? "en-US" : null;
            if (language is not null) candidates.Add((language, weight, candidates.Count));
        }
        return candidates.OrderByDescending(x => x.weight).ThenBy(x => x.order).Select(x => x.language).FirstOrDefault() ?? "en-US";
    }

    private static string Resolve(string name) => name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        using var stream = typeof(AppText).Assembly.GetManifestResourceStream("Localization." + language + ".json")
            ?? throw new InvalidOperationException("Missing embedded language: " + language);
        using var document = JsonDocument.Parse(stream);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject()) entries.Add(property.Name, property.Value.GetString()!);
        return new ReadOnlyDictionary<string, string>(entries);
    }
    private sealed class CultureScope(CultureInfo prior, string? priorRequest) : IDisposable
    {
        public void Dispose() { RequestLanguage.Value = priorRequest; CultureInfo.CurrentUICulture = prior; }
    }
}
