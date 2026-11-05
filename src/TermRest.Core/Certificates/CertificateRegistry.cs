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
        var (text, _) = TextFile.Read(path);
        var entries = JsonSerializer.Deserialize(text, CoreJsonContext.Default.ListCertificateEntry)
            ?? throw new InvalidDataException($"'{path}' does not contain a certificate list.");
        return new CertificateRegistry(baseDirectory, path) { Entries = entries };
    }

    public static CertificateRegistry LoadOrEmpty(string path, string baseDirectory) =>
        File.Exists(path) ? Load(path, baseDirectory) : new CertificateRegistry(baseDirectory, path);

    public void Save(string? path = null)
    {
        path ??= FilePath ?? throw new InvalidOperationException("The registry has no file path.");
        var json = JsonSerializer.Serialize(Entries, CoreJsonContext.Default.ListCertificateEntry);
        TextFile.WriteAtomic(path, json.Replace("\r\n", "\n", StringComparison.Ordinal), new TextFileInfo(false, true, false));
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
