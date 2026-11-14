using System.Text.Json;
using TermRest.Core.IO;
using TermRest.Core.Workspace;

namespace TermRest.Core.Certificates;

/// <summary>
/// Workspace certificate registry (<c>.termrest/certificates.json</c>), compatible with Newman's
/// <c>--ssl-client-cert-list</c> file. Relative paths are resolved against <see cref="BaseDirectory"/> (the workspace root).
/// </summary>
public sealed class CertificateRegistry
{
    /// <summary>What <see cref="FilePath"/> held when it was last read or written; null when it did not exist then.</summary>
    private FileStamp? _stamp;

    public CertificateRegistry(string baseDirectory, string? filePath = null)
    {
        BaseDirectory = baseDirectory;
        FilePath = filePath;
    }

    public List<CertificateEntry> Entries { get; set; } = [];

    public string BaseDirectory { get; }

    public string? FilePath { get; set; }

    public static CertificateRegistry Load(string path, string baseDirectory)
    {
        var (text, _) = TextFile.Read(path, out var stamp);
        var entries = JsonSerializer.Deserialize(text, CoreJsonContext.Default.ListCertificateEntry)
            ?? throw new InvalidDataException($"'{path}' does not contain a certificate list.");
        return new CertificateRegistry(baseDirectory, path) { Entries = entries, _stamp = stamp };
    }

    public static CertificateRegistry LoadOrEmpty(string path, string baseDirectory) =>
        File.Exists(path) ? Load(path, baseDirectory) : new CertificateRegistry(baseDirectory, path);

    /// <summary>
    /// Writes the entries to <paramref name="path"/> (default <see cref="FilePath"/>). Saving over <see cref="FilePath"/>
    /// is refused with a <see cref="FileChangedException"/> when another program (a git pull, another TermRest) changed
    /// or created it since it was read: that would undo its changes.
    /// </summary>
    /// <param name="overwriteChanged">Write the file even when it changed on disk.</param>
    public void Save(string? path = null, bool overwriteChanged = false)
    {
        path ??= FilePath ?? throw new InvalidOperationException("The registry has no file path.");
        var fullPath = Path.GetFullPath(path);
        if (!overwriteChanged && FilePath is not null && string.Equals(fullPath, Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase)
            && File.Exists(fullPath) && (_stamp is null || _stamp.HasChanged(fullPath)))
        {
            throw new FileChangedException([fullPath]);
        }

        var json = JsonSerializer.Serialize(Entries, CoreJsonContext.Default.ListCertificateEntry);
        TextFile.WriteAtomic(path, json.Replace("\r\n", "\n", StringComparison.Ordinal), new TextFileInfo(false, true, false), out var stamp);
        _stamp = stamp;
        FilePath = path;
    }

    public string ResolvePath(string source) =>
        Path.IsPathRooted(source) ? source : Path.GetFullPath(Path.Combine(BaseDirectory, source));

    /// <summary>The enabled entry whose most specific pattern matches <paramref name="uri"/>.</summary>
    public (CertificateEntry Entry, UrlMatchPattern Pattern)? FindBestMatch(Uri uri)
    {
        (CertificateEntry Entry, UrlMatchPattern Pattern)? best = null;
        foreach (var entry in Entries.Where(e => !e.Disabled))
        {
            foreach (var text in entry.Matches)
            {
                if (UrlMatchPattern.TryParse(text, out var pattern) && pattern!.IsMatch(uri)
                    && (best is null || pattern.Specificity > best.Value.Pattern.Specificity))
                {
                    best = (entry, pattern);
                }
            }
        }

        return best;
    }
}
