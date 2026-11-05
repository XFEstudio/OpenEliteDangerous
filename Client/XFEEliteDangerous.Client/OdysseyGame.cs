using System.Security.Cryptography;

namespace XFEEliteDangerous.Client;

/// <summary>The source-audited game build. Its local installation directory is not part of its identity.</summary>
public static class OdysseyGame
{
    public const string Build = "2026.09.03.332841";
    public const string Sha256 = "e6be8bbe04e6a7ae226d4318945af7f367de13dc5a007a261964d9ba8144e988";
    public const long ImageLength = 103469056;
    public const string Product = "elite-dangerous-odyssey-64";
    public const string ExecutableName = "EliteDangerous64.exe";

    public static string ExecutableFromSelection(string selection)
    {
        if (!Path.IsPathFullyQualified(selection)) throw new ArgumentException(AppText.Get("Launch.LocalGamePathRequired"));
        var path = Path.TrimEndingDirectorySeparator(LocalPaths.CheckedDiscoveryPath(selection));
        var leaf = Path.GetFileName(path);
        if (!leaf.Equals(ExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            if (Path.GetExtension(leaf).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(AppText.Get("Launch.OdysseyLayoutRequired"));
            path = leaf.Equals(Product, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(path, ExecutableName)
                : Path.Combine(path, "Products", Product, ExecutableName);
        }
        var product = Path.GetDirectoryName(path)!;
        var products = Path.GetDirectoryName(product);
        if (!Path.GetFileName(product).Equals(Product, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(products), "Products", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(AppText.Get("Launch.OdysseyLayoutRequired"));
        return LocalPaths.CheckedDiscoveryPath(path);
    }

    public static bool Matches(Stream image) => image.Length == ImageLength
        && Convert.ToHexStringLower(SHA256.HashData(image)) == Sha256;
}

/// <summary>Holds a read lease from the build check until the owned game exits.</summary>
public sealed class VerifiedOdysseyImage : IDisposable
{
    private readonly FileStream file;
    public string Executable { get; }
    public string ProductDirectory { get; }
    public string InstallDirectory { get; }

    private VerifiedOdysseyImage(string executable, FileStream file)
    {
        Executable = executable;
        ProductDirectory = Path.GetDirectoryName(executable)!;
        InstallDirectory = Path.GetDirectoryName(Path.GetDirectoryName(ProductDirectory)!)!;
        this.file = file;
    }

    public static async Task<VerifiedOdysseyImage> OpenAsync(string selection, CancellationToken cancel = default)
    {
        var executable = OdysseyGame.ExecutableFromSelection(selection);
        var file = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (file.Length != OdysseyGame.ImageLength
                || Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancel).ConfigureAwait(false)) != OdysseyGame.Sha256)
                throw new InvalidDataException(AppText.Get("Launch.UnsupportedOdyssey") + OdysseyGame.Build + "; " + executable);
            return new VerifiedOdysseyImage(executable, file);
        }
        catch { file.Dispose(); throw; }
    }

    public void Dispose() => file.Dispose();
}
