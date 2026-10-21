using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace XFEEliteDangerous.Client;

public sealed record FileEvidence(string Name, long Length, string Sha256);
public sealed record PeEvidence(string Machine, string Format, bool Valid);
public sealed record ProductInspection(string Directory, string? Name, string? Version, string? Executable,
    FileEvidence? ExecutableEvidence, PeEvidence? Pe, IReadOnlyList<FileEvidence> Dictionaries,
    IReadOnlyList<string> AppConfigSections, IReadOnlyList<string> ServiceHosts,
    IReadOnlyList<string> Warnings, FileEvidence? VersionInfoEvidence = null, FileEvidence? AppConfigEvidence = null);
public sealed record InstallationInspection(string Kind, DateTimeOffset InspectedAt, string Directory,
    bool LauncherPresent, IReadOnlyList<ProductInspection> Products, IReadOnlyList<string> Warnings);

/// <summary>Reads version, hashes and configuration evidence without launching or modifying the installation.</summary>
public sealed class GameInstallationInspector
{
    public async Task<InstallationInspection> InspectAsync(string gameDirectory, CancellationToken cancellationToken = default)
    {
        var root = LocalPaths.Directory(gameDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(AppText.Get("GameInstallationInspector.Text017"));
        var productsPath = Path.Combine(root, "Products");
        var warnings = new List<string>();
        var products = new List<ProductInspection>();
        if (Directory.Exists(productsPath))
            foreach (var directory in Directory.EnumerateDirectories(productsPath).Order(StringComparer.OrdinalIgnoreCase))
                products.Add(await InspectProductAsync(directory, cancellationToken).ConfigureAwait(false));
        else warnings.Add(AppText.Get("GameInstallationInspector.Text018"));
        return new("elite-installation-evidence", DateTimeOffset.UtcNow, root, File.Exists(Path.Combine(root, "EDLaunch.exe")), products, warnings);
    }

    private static async Task<ProductInspection> InspectProductAsync(string directory, CancellationToken cancel)
    {
        string? name = null, version = null, executable = null;
        FileEvidence? evidence = null, versionEvidence = null, configEvidence = null;
        PeEvidence? pe = null;
        var warnings = new List<string>();
        var sections = new List<string>();
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dictionaries = new List<FileEvidence>();
        var versionPath = Path.Combine(directory, "VersionInfo.txt");
        if (File.Exists(versionPath))
        {
            versionEvidence = await InspectFileAsync(versionPath, cancel).ConfigureAwait(false);
            try
            {
                using var stream = OpenRead(versionPath);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancel).ConfigureAwait(false);
                name = Text(json.RootElement, "name"); version = Text(json.RootElement, "Version");
                executable = Text(json.RootElement, "executable");
            }
            catch (JsonException) { warnings.Add(AppText.Get("GameInstallationInspector.Text019")); }
        }
        else warnings.Add(AppText.Get("GameInstallationInspector.Text020"));
        if (!string.IsNullOrWhiteSpace(executable))
        {
            var fullExecutable = Path.GetFullPath(Path.Combine(directory, executable));
            if (!IsWithin(directory, fullExecutable))
            {
                warnings.Add(AppText.Get("GameInstallationInspector.Text021")); executable = null;
            }
            else if (File.Exists(fullExecutable))
            {
                evidence = await InspectFileAsync(fullExecutable, cancel).ConfigureAwait(false);
                pe = InspectPe(fullExecutable);
            }
            else warnings.Add(AppText.Get("GameInstallationInspector.Text022"));
        }
        foreach (var file in Directory.EnumerateFiles(directory, "OodleDictionary*", SearchOption.TopDirectoryOnly).Order())
            dictionaries.Add(await InspectFileAsync(file, cancel).ConfigureAwait(false));
        var config = Path.Combine(directory, "AppConfig.xml");
        if (File.Exists(config))
        {
            configEvidence = await InspectFileAsync(config, cancel).ConfigureAwait(false);
            try
            {
                using var stream = OpenRead(config);
                var xml = await XDocument.LoadAsync(stream, LoadOptions.None, cancel).ConfigureAwait(false);
                sections.AddRange(xml.Root?.Elements().Select(x => x.Name.LocalName).Distinct() ?? []);
                foreach (var attribute in xml.Descendants().Attributes())
                {
                    // Export hostnames only. Never export credentials, complete URLs, query strings or arbitrary XML values.
                    if (attribute.Name.LocalName.Contains("token", StringComparison.OrdinalIgnoreCase)
                        || attribute.Name.LocalName.Contains("password", StringComparison.OrdinalIgnoreCase)
                        || attribute.Name.LocalName.Contains("key", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Uri.TryCreate(attribute.Value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                        hosts.Add(uri.Host);
                }
            }
            catch (System.Xml.XmlException) { warnings.Add(AppText.Get("GameInstallationInspector.Text023")); }
        }
        return new(Path.GetFileName(directory), name, version, executable, evidence, pe,
            dictionaries, sections, hosts.Order(StringComparer.OrdinalIgnoreCase).ToArray(), warnings, versionEvidence, configEvidence);
    }

    internal static FileStream OpenRead(string file) => new(LocalPaths.FilePath(file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    internal static bool IsWithin(string root, string path) => path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static async Task<FileEvidence> InspectFileAsync(string file, CancellationToken cancel)
    {
        using var stream = OpenRead(file);
        var length = stream.Length;
        var hash = await SHA256.HashDataAsync(stream, cancel).ConfigureAwait(false);
        return new(Path.GetFileName(file), length, Convert.ToHexString(hash).ToLowerInvariant());
    }
    private static string? Text(JsonElement element, string property) => element.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static PeEvidence InspectPe(string file)
    {
        using var stream = OpenRead(file);
        Span<byte> dos = stackalloc byte[64];
        if (stream.Read(dos) != dos.Length || dos[0] != 'M' || dos[1] != 'Z') return new("unknown", "unknown", false);
        var offset = BinaryPrimitives.ReadInt32LittleEndian(dos[60..]);
        if (offset < 64 || offset > stream.Length - 26) return new("unknown", "unknown", false);
        stream.Position = offset;
        Span<byte> header = stackalloc byte[26];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("PE\0\0"u8)) return new("unknown", "unknown", false);
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        var format = BinaryPrimitives.ReadUInt16LittleEndian(header[24..]);
        return new(machine switch { 0x8664 => "AMD64", 0x14c => "I386", 0xaa64 => "ARM64", _ => $"0x{machine:x4}" },
            format switch { 0x20b => "PE32+", 0x10b => "PE32", _ => "unknown" }, true);
    }
}
