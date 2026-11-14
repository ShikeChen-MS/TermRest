using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TermRest.Core.IO;
using TermRest.Core.Model;

namespace TermRest.Core.Certificates;

public sealed record ClientCertificateSelection(CertificateEntry Entry, LoadedCertificate Certificate, string Origin)
{
    /// <summary>Captured at selection time, so diagnostics never touch a certificate that was reloaded (disposed) meanwhile.</summary>
    public CertificateSummary Summary { get; } = Certificate.Summary;

    /// <summary>
    /// The folder relative paths of <see cref="Entry"/> are resolved against: the workspace root for a registered
    /// certificate, the collection's folder for one pinned on a request (null: unknown).
    /// </summary>
    public string? BaseDirectory { get; init; }

    /// <summary>The full path of the file a path of <see cref="Entry"/> refers to: the file the certificate was loaded from.</summary>
    public string ResolvePath(string src) => BaseDirectory is null ? src : ClientCertificateProvider.ResolvePath(BaseDirectory, src);
}

/// <summary>
/// Chooses and loads the client certificate for a request: a certificate pinned on the request wins, then the most
/// specific registry match. Loaded certificates, and the workspace CA bundle, are cached until their files change.
/// </summary>
public sealed class ClientCertificateProvider(CertificateRegistry registry) : IDisposable
{
    /// <summary>Why a certificate or CA bundle file on a network share is not opened, and how to allow it.</summary>
    public const string OnNetworkShare =
        "it is on a network share outside the workspace folder, or a link leads there, so it is not opened (Windows would sign in to " +
        "that server with your credentials): allow files from outside the folder for this workspace in the full-screen UI " +
        "(Tools › Settings…), or with --allow-outside-files.";

    // Keyed by source (see BuildCacheKey); a replaced certificate or bundle may still be in use by a request in flight,
    // so it is retired and released with the provider.
    private readonly Dictionary<string, (string Version, LoadedCertificate Certificate)> _cache = new(StringComparer.Ordinal);
    private readonly List<LoadedCertificate> _retired = [];
    private readonly Dictionary<string, ((DateTime, long) Version, X509Certificate2Collection? Bundle, string? Error)> _trustBundles = new(StringComparer.Ordinal);
    private readonly List<X509Certificate2Collection> _retiredBundles = [];
    private readonly Lock _gate = new();

    public CertificateRegistry Registry { get; } = registry;

    /// <param name="filesFolder">
    /// While set, a certificate file on a network share outside it is not opened (see <see cref="RealPath.ReachesNetwork"/>):
    /// the registry and the requests come with the repository.
    /// </param>
    public ClientCertificateSelection? Select(
        Uri uri,
        CertificateSpec? requestCertificate,
        string requestBaseDirectory,
        Func<string?, string?> resolveSecret,
        ICollection<string> warnings,
        string? filesFolder = null)
    {
        if (requestCertificate is { } pinned && (pinned.CertSrc is not null || pinned.PfxSrc is not null))
        {
            var applies = pinned.Matches.Count == 0
                || pinned.Matches.Any(m => UrlMatchPattern.TryParse(m, out var p) && p!.IsMatch(uri));
            if (applies)
            {
                var entry = new CertificateEntry
                {
                    Name = pinned.Name ?? "request certificate",
                    Matches = [.. pinned.Matches],
                    Cert = pinned.CertSrc is null ? null : new FileSource { Src = pinned.CertSrc },
                    Key = pinned.KeySrc is null ? null : new FileSource { Src = pinned.KeySrc },
                    Pfx = pinned.PfxSrc is null ? null : new FileSource { Src = pinned.PfxSrc },
                    Passphrase = pinned.Passphrase,
                };
                return TrySelect(entry, requestBaseDirectory, "pinned on request", resolveSecret, warnings, filesFolder);
            }
        }

        if (Registry.FindBestMatch(uri) is { } match)
        {
            return TrySelect(match.Entry, Registry.BaseDirectory, $"matched {match.Pattern}", resolveSecret, warnings, filesFolder);
        }

        return null;
    }

    /// <summary>Loads (or returns the cached) certificate for an entry. Throws <see cref="CertificateLoadException"/>.</summary>
    /// <param name="filesFolder">While set, a file on a network share outside it is not opened (see <see cref="Select"/>).</param>
    public LoadedCertificate Load(CertificateEntry entry, string baseDirectory, Func<string?, string?>? resolveSecret = null, string? filesFolder = null)
    {
        var passphrase = resolveSecret is null ? entry.Passphrase : resolveSecret(entry.Passphrase);
        string Resolve(string src) => ResolvePath(baseDirectory, src);

        // Before the cache looks at the files.
        if (filesFolder is not null)
        {
            foreach (var src in (string?[])[entry.Cert?.Src, entry.Key?.Src, entry.Pfx?.Src])
            {
                if (src is not null && Resolve(src) is var path && RealPath.ReachesNetwork(path, filesFolder))
                {
                    throw new CertificateLoadException($"'{path}': {OnNetworkShare}");
                }
            }
        }

        var (source, version) = BuildCacheKey(entry, Resolve, passphrase);
        lock (_gate)
        {
            if (_cache.TryGetValue(source, out var cached) && cached.Version == version)
            {
                return cached.Certificate;
            }
        }

        var loaded = entry.Kind switch
        {
            CertificateSourceKind.Store => CertificateLoader.LoadFromStore(entry.Store!),
            CertificateSourceKind.Pfx => CertificateLoader.LoadPfx(Resolve(entry.Pfx!.Src), passphrase),
            _ when entry.Cert is null => throw new CertificateLoadException($"Certificate '{entry.Name}' has no cert, pfx or store source."),
            _ => CertificateLoader.LoadPem(Resolve(entry.Cert.Src), entry.Key is null ? null : Resolve(entry.Key.Src), passphrase),
        };

        lock (_gate)
        {
            if (_cache.TryGetValue(source, out var stale))
            {
                _retired.Add(stale.Certificate);
            }

            _cache[source] = (version, loaded);
        }

        return loaded;
    }

    /// <summary>
    /// Loads (or returns the cached) extra CA bundle, parsed again only when the file changes. Throws
    /// <see cref="CertificateLoadException"/>, also for a failure cached until then.
    /// </summary>
    public X509Certificate2Collection LoadTrustBundle(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        var version = file.Exists ? (file.LastWriteTimeUtc, file.Length) : default;
        lock (_gate)
        {
            if (_trustBundles.TryGetValue(file.FullName, out var cached) && cached.Version == version)
            {
                return cached.Bundle ?? throw new CertificateLoadException(cached.Error!);
            }
        }

        X509Certificate2Collection? bundle = null;
        string? error = null;
        try
        {
            bundle = CertificateLoader.LoadTrustBundle(path);
        }
        catch (CertificateLoadException ex)
        {
            error = ex.Message;
        }

        lock (_gate)
        {
            if (_trustBundles.TryGetValue(file.FullName, out var stale) && stale.Bundle is { } replaced)
            {
                _retiredBundles.Add(replaced);
            }

            _trustBundles[file.FullName] = (version, bundle, error);
        }

        return bundle ?? throw new CertificateLoadException(error!);
    }

    private ClientCertificateSelection? TrySelect(CertificateEntry entry, string baseDirectory, string origin, Func<string?, string?> resolveSecret,
        ICollection<string> warnings, string? filesFolder)
    {
        try
        {
            var loaded = Load(entry, baseDirectory, resolveSecret, filesFolder);
            var summary = loaded.Summary;
            if (summary.IsExpired)
            {
                warnings.Add($"Client certificate '{entry.Name}' expired on {summary.NotAfter:yyyy-MM-dd}.");
            }
            else if (summary.IsNotYetValid)
            {
                warnings.Add($"Client certificate '{entry.Name}' is not valid until {summary.NotBefore:yyyy-MM-dd}.");
            }

            return new ClientCertificateSelection(entry, loaded, origin) { BaseDirectory = baseDirectory };
        }
        catch (CertificateLoadException ex)
        {
            warnings.Add($"Client certificate '{entry.Name}' not used: {ex.Message}");
            return null;
        }
    }

    /// <summary>The full path of a certificate file: a relative <paramref name="src"/> is relative to <paramref name="baseDirectory"/>.</summary>
    internal static string ResolvePath(string baseDirectory, string src) =>
        Path.IsPathRooted(src) ? src : Path.GetFullPath(Path.Combine(baseDirectory, src));

    /// <summary>
    /// Where a certificate comes from (its kind and files, or its store selector) and the version of that source (file
    /// times, passphrase): entries sharing a name do not evict each other, and a changed source replaces its certificate.
    /// </summary>
    private static (string Source, string Version) BuildCacheKey(CertificateEntry entry, Func<string, string> resolve, string? passphrase)
    {
        var source = new StringBuilder().Append(entry.Kind);
        var version = new StringBuilder();
        void AppendFile(string? src)
        {
            if (src is null)
            {
                return;
            }

            var path = resolve(src);
            source.Append('|').Append(path);
            version.Append(File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0).Append('|');
        }

        AppendFile(entry.Cert?.Src);
        AppendFile(entry.Key?.Src);
        AppendFile(entry.Pfx?.Src);
        if (entry.Store is { } store)
        {
            source.Append('|').Append(store.Location).Append('\\').Append(store.StoreName).Append('|').Append(store.Thumbprint).Append('|').Append(store.Subject);
        }

        if (!string.IsNullOrEmpty(passphrase))
        {
            version.Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passphrase))));
        }

        return (source.ToString(), version.ToString());
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var certificate in _cache.Values.Select(c => c.Certificate).Concat(_retired))
            {
                certificate.Dispose();
            }

            foreach (var bundle in _trustBundles.Values.Select(b => b.Bundle).OfType<X509Certificate2Collection>().Concat(_retiredBundles))
            {
                foreach (var ca in bundle)
                {
                    ca.Dispose();
                }
            }

            _cache.Clear();
            _retired.Clear();
            _trustBundles.Clear();
            _retiredBundles.Clear();
        }
    }
}
