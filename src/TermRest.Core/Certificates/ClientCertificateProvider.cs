using System.Security.Cryptography;
using System.Text;
using TermRest.Core.Model;

namespace TermRest.Core.Certificates;

public sealed record ClientCertificateSelection(CertificateEntry Entry, LoadedCertificate Certificate, string Origin)
{
    /// <summary>Captured at selection time, so diagnostics never touch a certificate that was reloaded (disposed) meanwhile.</summary>
    public CertificateSummary Summary { get; } = Certificate.Summary;
}

/// <summary>
/// Chooses and loads the client certificate for a request: a certificate pinned on the request wins, then the most
/// specific registry match. Loaded certificates are cached until their files change.
/// </summary>
public sealed class ClientCertificateProvider(CertificateRegistry registry) : IDisposable
{
    private readonly Dictionary<string, LoadedCertificate> _cache = new(StringComparer.Ordinal);
    private readonly List<LoadedCertificate> _retired = [];
    private readonly Lock _gate = new();

    public CertificateRegistry Registry { get; } = registry;

    public ClientCertificateSelection? Select(
        Uri uri,
        CertificateSpec? requestCertificate,
        string requestBaseDirectory,
        Func<string?, string?> resolveSecret,
        ICollection<string> warnings)
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
                return TrySelect(entry, requestBaseDirectory, "pinned on request", resolveSecret, warnings);
            }
        }

        if (Registry.FindBestMatch(uri) is { } match)
        {
            return TrySelect(match.Entry, Registry.BaseDirectory, $"matched {match.Pattern}", resolveSecret, warnings);
        }

        return null;
    }

    /// <summary>Loads (or returns the cached) certificate for an entry. Throws <see cref="CertificateLoadException"/>.</summary>
    public LoadedCertificate Load(CertificateEntry entry, string baseDirectory, Func<string?, string?>? resolveSecret = null)
    {
        var passphrase = resolveSecret is null ? entry.Passphrase : resolveSecret(entry.Passphrase);
        string Resolve(string src) => Path.IsPathRooted(src) ? src : Path.GetFullPath(Path.Combine(baseDirectory, src));

        var key = BuildCacheKey(entry, Resolve, passphrase);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
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
            foreach (var stale in _cache.Where(kv => kv.Key.StartsWith(entry.Name + "|", StringComparison.Ordinal)).ToList())
            {
                _cache.Remove(stale.Key);
                _retired.Add(stale.Value);
            }

            _cache[key] = loaded;
        }

        return loaded;
    }

    private ClientCertificateSelection? TrySelect(CertificateEntry entry, string baseDirectory, string origin, Func<string?, string?> resolveSecret, ICollection<string> warnings)
    {
        try
        {
            var loaded = Load(entry, baseDirectory, resolveSecret);
            var summary = loaded.Summary;
            if (summary.IsExpired)
            {
                warnings.Add($"Client certificate '{entry.Name}' expired on {summary.NotAfter:yyyy-MM-dd}.");
            }
            else if (summary.IsNotYetValid)
            {
                warnings.Add($"Client certificate '{entry.Name}' is not valid until {summary.NotBefore:yyyy-MM-dd}.");
            }

            return new ClientCertificateSelection(entry, loaded, origin);
        }
        catch (CertificateLoadException ex)
        {
            warnings.Add($"Client certificate '{entry.Name}' not used: {ex.Message}");
            return null;
        }
    }

    private static string BuildCacheKey(CertificateEntry entry, Func<string, string> resolve, string? passphrase)
    {
        var builder = new StringBuilder(entry.Name).Append('|').Append(entry.Kind);
        void AppendFile(string? src)
        {
            if (src is null)
            {
                return;
            }

            var path = resolve(src);
            builder.Append('|').Append(path).Append('@').Append(File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0);
        }

        AppendFile(entry.Cert?.Src);
        AppendFile(entry.Key?.Src);
        AppendFile(entry.Pfx?.Src);
        if (entry.Store is { } store)
        {
            builder.Append('|').Append(store.Location).Append('\\').Append(store.StoreName).Append('|').Append(store.Thumbprint).Append('|').Append(store.Subject);
        }

        if (!string.IsNullOrEmpty(passphrase))
        {
            builder.Append('|').Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passphrase))));
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var certificate in _cache.Values.Concat(_retired))
            {
                certificate.Dispose();
            }

            _cache.Clear();
            _retired.Clear();
        }
    }
}
