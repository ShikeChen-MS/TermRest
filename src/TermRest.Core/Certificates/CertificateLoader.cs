using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TermRest.Core.Certificates;

public sealed class CertificateLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A certificate with its private key plus any intermediate certificates to send with it.</summary>
public sealed class LoadedCertificate : IDisposable
{
    public required X509Certificate2 Certificate { get; init; }

    public X509Certificate2Collection Chain { get; init; } = [];

    public required string Description { get; init; }

    public CertificateSummary Summary => CertificateSummary.From(Certificate, Description);

    public void Dispose()
    {
        Certificate.Dispose();
        foreach (var c in Chain)
        {
            c.Dispose();
        }
    }
}

public sealed record CertificateSummary(
    string Subject,
    string Issuer,
    DateTime NotBefore,
    DateTime NotAfter,
    string Thumbprint,
    bool HasPrivateKey,
    string? Source)
{
    public bool IsExpired => DateTime.Now > NotAfter;

    public bool IsNotYetValid => DateTime.Now < NotBefore;

    public string CommonName
    {
        get
        {
            var cn = ExtractCommonName(Subject);
            return cn.Length > 0 ? cn : Subject.Length > 0 ? Subject : $"(no subject) {Thumbprint[..Math.Min(8, Thumbprint.Length)]}";
        }
    }

    public string IssuerCommonName => ExtractCommonName(Issuer) is { Length: > 0 } cn ? cn : Issuer;

    public static CertificateSummary From(X509Certificate2 certificate, string? source = null) =>
        new(certificate.Subject, certificate.Issuer, certificate.NotBefore, certificate.NotAfter, certificate.Thumbprint, certificate.HasPrivateKey, source);

    public static string ExtractCommonName(string distinguishedName)
    {
        foreach (var part in distinguishedName.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[3..];
            }
        }

        return distinguishedName;
    }

    public override string ToString() => $"{CommonName} (expires {NotAfter:yyyy-MM-dd}, {Thumbprint[..Math.Min(8, Thumbprint.Length)]}…)";
}

/// <summary>Loads client certificates from PEM, PKCS#12 and OS certificate stores.</summary>
public static class CertificateLoader
{
    public static LoadedCertificate LoadPem(string certPath, string? keyPath, string? passphrase)
    {
        EnsureExists(certPath, "certificate");
        if (keyPath is not null)
        {
            EnsureExists(keyPath, "private key");
        }

        var keyText = File.ReadAllText(keyPath ?? certPath);
        X509Certificate2 leaf;
        try
        {
            if (keyText.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal))
            {
                throw new CertificateLoadException(
                    "Legacy OpenSSL-encrypted private keys are not supported. Convert the key to PKCS#8: " +
                    "openssl pkcs8 -topk8 -in key.pem -out key.p8.pem");
            }

            if (keyText.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(passphrase))
                {
                    throw new CertificateLoadException("The private key is encrypted; set a passphrase for this certificate.");
                }

                leaf = X509Certificate2.CreateFromEncryptedPemFile(certPath, passphrase, keyPath);
            }
            else if (!keyText.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
            {
                throw new CertificateLoadException(keyPath is null
                    ? $"'{certPath}' contains no private key. Set the key file as well."
                    : $"'{keyPath}' contains no PEM private key.");
            }
            else
            {
                leaf = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            }
        }
        catch (CryptographicException ex)
        {
            throw new CertificateLoadException($"Could not load PEM certificate/key: {ex.Message}", ex);
        }

        var chain = new X509Certificate2Collection();
        var all = new X509Certificate2Collection();
        all.ImportFromPemFile(certPath);
        foreach (var certificate in all)
        {
            if (certificate.Thumbprint == leaf.Thumbprint)
            {
                certificate.Dispose();
            }
            else
            {
                chain.Add(certificate);
            }
        }

        return new LoadedCertificate
        {
            Certificate = MakeUsableForTls(leaf),
            Chain = chain,
            Description = $"PEM {Path.GetFileName(certPath)}",
        };
    }

    public static LoadedCertificate LoadPfx(string pfxPath, string? passphrase)
    {
        EnsureExists(pfxPath, "PKCS#12");
        X509Certificate2Collection collection;
        try
        {
            collection = X509CertificateLoader.LoadPkcs12CollectionFromFile(pfxPath, passphrase, KeyStorageFlags);
        }
        catch (CryptographicException ex)
        {
            throw new CertificateLoadException($"Could not open '{Path.GetFileName(pfxPath)}' (wrong passphrase?): {ex.Message}", ex);
        }

        var leaf = collection.FirstOrDefault(c => c.HasPrivateKey);
        if (leaf is null)
        {
            foreach (var c in collection)
            {
                c.Dispose();
            }

            throw new CertificateLoadException($"'{Path.GetFileName(pfxPath)}' contains no certificate with a private key.");
        }

        var chain = new X509Certificate2Collection();
        foreach (var c in collection)
        {
            if (!ReferenceEquals(c, leaf))
            {
                chain.Add(c);
            }
        }

        return new LoadedCertificate { Certificate = leaf, Chain = chain, Description = $"PFX {Path.GetFileName(pfxPath)}" };
    }

    /// <summary>
    /// Loads an identity from an OS store (Windows certificate store, macOS keychain, .NET store on Linux).
    /// Works with non-exportable keys, including smart-card/TPM-backed keys on Windows.
    /// </summary>
    public static LoadedCertificate LoadFromStore(StoreSource source)
    {
        if (!Enum.TryParse<StoreLocation>(source.Location, ignoreCase: true, out var location))
        {
            throw new CertificateLoadException($"Unknown store location '{source.Location}'. Use CurrentUser or LocalMachine.");
        }

        using var store = new X509Store(source.StoreName, location);
        try
        {
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        }
        catch (CryptographicException ex)
        {
            throw new CertificateLoadException($"Cannot open certificate store {source.Location}\\{source.StoreName}: {ex.Message}", ex);
        }

        var all = store.Certificates;
        X509Certificate2Collection found;
        if (!string.IsNullOrWhiteSpace(source.Thumbprint))
        {
            found = all.Find(X509FindType.FindByThumbprint, NormalizeThumbprint(source.Thumbprint), validOnly: false);
        }
        else if (!string.IsNullOrWhiteSpace(source.Subject))
        {
            found = source.Subject.Contains('=', StringComparison.Ordinal)
                ? all.Find(X509FindType.FindBySubjectDistinguishedName, source.Subject, validOnly: false)
                : all.Find(X509FindType.FindBySubjectName, source.Subject, validOnly: false);
        }
        else
        {
            DisposeAll(all);
            throw new CertificateLoadException("A store certificate needs a thumbprint or a subject.");
        }

        var chosen = found.Where(c => c.HasPrivateKey).OrderByDescending(c => c.NotAfter).FirstOrDefault();
        foreach (var c in all)
        {
            if (!ReferenceEquals(c, chosen))
            {
                c.Dispose();
            }
        }

        foreach (var c in found)
        {
            if (!ReferenceEquals(c, chosen))
            {
                c.Dispose();
            }
        }

        if (chosen is null)
        {
            throw new CertificateLoadException(found.Count > 0
                ? $"The certificate in {source.Location}\\{source.StoreName} has no accessible private key."
                : $"No certificate matching {(string.IsNullOrWhiteSpace(source.Thumbprint) ? source.Subject : NormalizeThumbprint(source.Thumbprint))} in {source.Location}\\{source.StoreName}.");
        }

        return new LoadedCertificate { Certificate = chosen, Description = $"store {source.Location}\\{source.StoreName}" };
    }

    /// <summary>Lists certificates in an OS store, by default only those with a private key (usable as client certificates).</summary>
    public static IReadOnlyList<CertificateSummary> ListStore(string location = "CurrentUser", string storeName = "My", bool requirePrivateKey = true)
    {
        if (!Enum.TryParse<StoreLocation>(location, ignoreCase: true, out var storeLocation))
        {
            return [];
        }

        using var store = new X509Store(storeName, storeLocation);
        try
        {
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        }
        catch (CryptographicException)
        {
            return [];
        }

        var certificates = store.Certificates;
        try
        {
            return certificates
                .Where(c => !requirePrivateKey || c.HasPrivateKey)
                .Select(c => CertificateSummary.From(c, $"{location}\\{storeName}"))
                .OrderBy(s => s.CommonName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            DisposeAll(certificates);
        }
    }

    /// <summary>Loads extra trusted CA certificates from a PEM bundle or a single DER/PEM file.</summary>
    public static X509Certificate2Collection LoadTrustBundle(string path)
    {
        EnsureExists(path, "CA bundle");
        var collection = new X509Certificate2Collection();
        var text = File.ReadAllText(path);
        if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
        {
            collection.ImportFromPemFile(path);
        }
        else
        {
            collection.Add(X509CertificateLoader.LoadCertificateFromFile(path));
        }

        return collection;
    }

    public static string NormalizeThumbprint(string thumbprint) =>
        new string(thumbprint.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    private static X509KeyStorageFlags KeyStorageFlags =>
        OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.DefaultKeySet;

    /// <summary>
    /// On Windows, SChannel cannot use ephemeral (in-memory) keys such as those created from PEM files.
    /// Re-importing through PKCS#12 gives the key a (temporary) key container SChannel can access.
    /// </summary>
    private static X509Certificate2 MakeUsableForTls(X509Certificate2 certificate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return certificate;
        }

        var pkcs12 = certificate.Export(X509ContentType.Pkcs12);
        try
        {
            return X509CertificateLoader.LoadPkcs12(pkcs12, password: null, KeyStorageFlags);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
            certificate.Dispose();
        }
    }

    private static void EnsureExists(string path, string what)
    {
        if (!File.Exists(path))
        {
            throw new CertificateLoadException($"The {what} file '{path}' does not exist.");
        }
    }

    private static void DisposeAll(X509Certificate2Collection collection)
    {
        foreach (var c in collection)
        {
            c.Dispose();
        }
    }
}
