using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TermRest.Core.Tests.Infrastructure;

/// <summary>
/// Generates a throw-away PKI for tests: a root CA, a localhost server certificate and a client certificate,
/// written to disk as PEM (plain and encrypted key) and PKCS#12.
/// </summary>
public sealed class TestPki : IDisposable
{
    public const string Passphrase = "test-passphrase";

    private TestPki(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }

    public X509Certificate2 Ca { get; private set; } = null!;

    public X509Certificate2 Server { get; private set; } = null!;

    public X509Certificate2 Client { get; private set; } = null!;

    public string CaPemPath => Path.Combine(Directory, "ca.pem");

    public string ClientCertPemPath => Path.Combine(Directory, "client.pem");

    public string ClientKeyPemPath => Path.Combine(Directory, "client.key");

    public string ClientEncryptedKeyPemPath => Path.Combine(Directory, "client.enc.key");

    public string ClientCombinedPemPath => Path.Combine(Directory, "client-combined.pem");

    public string ClientChainPemPath => Path.Combine(Directory, "client-chain.pem");

    public string ClientPfxPath => Path.Combine(Directory, "client.pfx");

    public static TestPki Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "termrest-tests", Guid.NewGuid().ToString("N")[..12]);
        System.IO.Directory.CreateDirectory(directory);
        var pki = new TestPki(directory);
        pki.Generate();
        return pki;
    }

    private void Generate()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);

        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=TermRest Test Root CA, O=TermRest Tests", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        using var caWithKey = caRequest.CreateSelfSigned(now, now.AddDays(30));
        Ca = Reimport(caWithKey);

        Server = Issue("CN=localhost", isServer: true, now, caWithKey, out var serverKey);
        serverKey.Dispose();
        Client = Issue("CN=TermRest Test Client, O=TermRest Tests", isServer: false, now, caWithKey, out var clientKey);
        using (clientKey)
        {
            File.WriteAllText(CaPemPath, Ca.ExportCertificatePem());
            File.WriteAllText(ClientCertPemPath, Client.ExportCertificatePem());
            File.WriteAllText(ClientKeyPemPath, clientKey.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(
                ClientEncryptedKeyPemPath,
                clientKey.ExportEncryptedPkcs8PrivateKeyPem(Passphrase, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000)));
            File.WriteAllText(ClientCombinedPemPath, Client.ExportCertificatePem() + "\n" + clientKey.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(ClientChainPemPath, Client.ExportCertificatePem() + "\n" + Ca.ExportCertificatePem());
            File.WriteAllBytes(ClientPfxPath, Client.Export(X509ContentType.Pkcs12, Passphrase));
        }
    }

    private static X509Certificate2 Issue(string subject, bool isServer, DateTimeOffset now, X509Certificate2 issuer, out RSA key)
    {
        key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(isServer ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));
        if (isServer)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(System.Net.IPAddress.Loopback);
            san.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(san.Build());
        }

        var serial = RandomNumberGenerator.GetBytes(12);
        using var issued = request.Create(issuer, now, now.AddDays(10), serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        return Reimport(withKey);
    }

    /// <summary>Round-trips through PKCS#12 so SChannel on Windows can use the private key.</summary>
    private static X509Certificate2 Reimport(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);

    public void Dispose()
    {
        Ca.Dispose();
        Server.Dispose();
        Client.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
