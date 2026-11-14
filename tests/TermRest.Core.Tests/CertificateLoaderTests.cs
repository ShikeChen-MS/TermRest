using TermRest.Core.Certificates;
using TermRest.Core.Http;
using TermRest.Core.Model;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Variables;
using Xunit;

namespace TermRest.Core.Tests;

public sealed class CertificateLoaderTests : IDisposable
{
    private readonly TestPki _pki = TestPki.Create();

    [Fact]
    public void Loads_pem_certificate_with_separate_key()
    {
        using var loaded = CertificateLoader.LoadPem(_pki.ClientCertPemPath, _pki.ClientKeyPemPath, null);

        Assert.True(loaded.Certificate.HasPrivateKey);
        Assert.Equal(_pki.Client.Thumbprint, loaded.Certificate.Thumbprint);
    }

    [Fact]
    public void Loads_pem_with_key_in_the_same_file()
    {
        using var loaded = CertificateLoader.LoadPem(_pki.ClientCombinedPemPath, null, null);

        Assert.True(loaded.Certificate.HasPrivateKey);
    }

    [Fact]
    public void Loads_encrypted_pkcs8_key_with_passphrase()
    {
        using var loaded = CertificateLoader.LoadPem(_pki.ClientCertPemPath, _pki.ClientEncryptedKeyPemPath, TestPki.Passphrase);

        Assert.True(loaded.Certificate.HasPrivateKey);
    }

    [Fact]
    public void Encrypted_key_without_passphrase_explains_the_problem()
    {
        var ex = Assert.Throws<CertificateLoadException>(() => CertificateLoader.LoadPem(_pki.ClientCertPemPath, _pki.ClientEncryptedKeyPemPath, null));

        Assert.Contains("passphrase", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pem_chain_certificates_become_intermediates()
    {
        using var loaded = CertificateLoader.LoadPem(_pki.ClientChainPemPath, _pki.ClientKeyPemPath, null);

        Assert.Equal(_pki.Client.Thumbprint, loaded.Certificate.Thumbprint);
        Assert.Equal(_pki.Ca.Thumbprint, Assert.Single(loaded.Chain).Thumbprint);
    }

    [Fact]
    public void Loads_pfx_and_rejects_wrong_passphrase()
    {
        using var loaded = CertificateLoader.LoadPfx(_pki.ClientPfxPath, TestPki.Passphrase);
        Assert.True(loaded.Certificate.HasPrivateKey);

        var ex = Assert.Throws<CertificateLoadException>(() => CertificateLoader.LoadPfx(_pki.ClientPfxPath, "wrong"));
        Assert.Contains("passphrase", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_files_and_store_entries_produce_clear_errors()
    {
        Assert.Contains("does not exist", Assert.Throws<CertificateLoadException>(() => CertificateLoader.LoadPfx("nope.pfx", null)).Message, StringComparison.Ordinal);
        var storeError = Assert.Throws<CertificateLoadException>(() => CertificateLoader.LoadFromStore(new StoreSource { Thumbprint = "00 11 22 33" }));
        Assert.Contains("0011", storeError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_that_cannot_be_read_produce_certificate_errors()
    {
        // The file exists, but another program holds it locked (or the user may not read it).
        static CertificateLoadException WhileLocked(string path, Action load)
        {
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return Assert.Throws<CertificateLoadException>(load);
        }

        var pfx = WhileLocked(_pki.ClientPfxPath, () => CertificateLoader.LoadPfx(_pki.ClientPfxPath, TestPki.Passphrase)).Message;
        Assert.Contains("client.pfx", pfx, StringComparison.Ordinal);
        Assert.DoesNotContain("passphrase", pfx, StringComparison.Ordinal); // .NET reports the file error as a cryptographic one
        Assert.Contains("client.key", WhileLocked(_pki.ClientKeyPemPath, () => CertificateLoader.LoadPem(_pki.ClientCertPemPath, _pki.ClientKeyPemPath, null)).Message, StringComparison.Ordinal);
        Assert.Contains("client.pem", WhileLocked(_pki.ClientCertPemPath, () => CertificateLoader.LoadPem(_pki.ClientCertPemPath, _pki.ClientKeyPemPath, null)).Message, StringComparison.Ordinal);
        Assert.Contains("ca.pem", WhileLocked(_pki.CaPemPath, () => CertificateLoader.LoadTrustBundle(_pki.CaPemPath)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_prefers_pinned_request_certificate_then_registry_and_resolves_passphrase()
    {
        var registry = new CertificateRegistry(_pki.Directory)
        {
            Entries =
            [
                new CertificateEntry
                {
                    Name = "registry-pfx",
                    Matches = ["https://localhost/*"],
                    Pfx = new FileSource { Src = Path.GetFileName(_pki.ClientPfxPath) },
                    Passphrase = "env:TERMREST_TEST_PFX_PASS",
                },
            ],
        };
        Environment.SetEnvironmentVariable("TERMREST_TEST_PFX_PASS", TestPki.Passphrase);
        using var provider = new ClientCertificateProvider(registry);
        var warnings = new List<string>();
        static string? Secret(string? value) =>
            value?.StartsWith("env:", StringComparison.Ordinal) == true ? Environment.GetEnvironmentVariable(value[4..]) : value;

        var fromRegistry = provider.Select(new Uri("https://localhost:5001/x"), null, _pki.Directory, Secret, warnings);
        Assert.NotNull(fromRegistry);
        Assert.Equal("registry-pfx", fromRegistry.Entry.Name);
        Assert.Empty(warnings);
        Assert.Same(fromRegistry.Certificate, provider.Select(new Uri("https://localhost/y"), null, _pki.Directory, Secret, warnings)!.Certificate);

        var pinned = new CertificateSpec { Name = "pinned", CertSrc = "client.pem", KeySrc = "client.key" };
        var fromRequest = provider.Select(new Uri("https://localhost/x"), pinned, _pki.Directory, Secret, warnings);
        Assert.Equal("pinned", fromRequest!.Entry.Name);
        Assert.Equal("pinned on request", fromRequest.Origin);

        Assert.Null(provider.Select(new Uri("https://other.example/"), null, _pki.Directory, Secret, warnings));
    }

    [Fact]
    public void Provider_reports_unusable_certificates_as_warnings()
    {
        var registry = new CertificateRegistry(_pki.Directory)
        {
            Entries = [new CertificateEntry { Name = "broken", Matches = ["<all_urls>"], Pfx = new FileSource { Src = "client.pfx" }, Passphrase = "wrong" }],
        };
        using var provider = new ClientCertificateProvider(registry);
        var warnings = new List<string>();

        Assert.Null(provider.Select(new Uri("https://localhost/"), null, _pki.Directory, s => s, warnings));
        Assert.Contains(warnings, w => w.Contains("broken", StringComparison.Ordinal));
    }

    [Fact]
    public void Certificates_that_share_a_name_are_cached_by_their_source()
    {
        using var provider = new ClientCertificateProvider(new CertificateRegistry(_pki.Directory));
        var warnings = new List<string>();
        LoadedCertificate Select(CertificateSpec spec) => provider.Select(new Uri("https://localhost/"), spec, _pki.Directory, s => s, warnings)!.Certificate;
        var pem = new CertificateSpec { Name = "Imported from cURL", CertSrc = "client.pem", KeySrc = "client.key" };
        var pfx = new CertificateSpec { Name = "Imported from cURL", PfxSrc = "client.pfx", Passphrase = TestPki.Passphrase };

        var first = Select(pem);
        var other = Select(pfx);
        Assert.Same(first, Select(pem));
        Assert.Same(other, Select(pfx));

        File.SetLastWriteTimeUtc(_pki.ClientCertPemPath, DateTime.UtcNow.AddMinutes(1));
        var reloaded = Select(pem);
        Assert.NotSame(first, reloaded);
        Assert.Equal(reloaded.Certificate.Thumbprint, first.Certificate.Thumbprint); // replaced, but still usable by requests in flight
        Assert.Empty(warnings);
    }

    [Fact]
    public void Trust_bundle_loads_pem_certificates()
    {
        var bundle = CertificateLoader.LoadTrustBundle(_pki.CaPemPath);

        Assert.Equal(_pki.Ca.Thumbprint, Assert.Single(bundle).Thumbprint);
    }

    [Fact]
    public void The_ca_bundle_is_parsed_once_until_its_file_changes()
    {
        var path = Path.Combine(_pki.Directory, "bundle.pem");
        File.Copy(_pki.CaPemPath, path);
        using var provider = new ClientCertificateProvider(new CertificateRegistry(_pki.Directory));
        var defaults = new ExecutionDefaults { CaBundlePath = path };
        PreparedRequest Prepare() => new RequestPreparer(VariableResolver.Empty, provider, defaults).Prepare(new HttpRequestSpec { Url = "https://localhost/" }, null, _pki.Directory);

        var first = Prepare().ExtraTrustedCertificates!;
        Assert.Same(first, Prepare().ExtraTrustedCertificates);

        File.AppendAllText(path, "\n" + _pki.Server.ExportCertificatePem());
        var changed = Prepare().ExtraTrustedCertificates!;
        Assert.NotSame(first, changed);
        Assert.Equal(2, changed.Count);
        Assert.Equal(_pki.Ca.Thumbprint, first[0].Thumbprint); // replaced, but still usable by requests in flight

        File.WriteAllText(path, "not a certificate");
        var broken = Prepare();
        Assert.StartsWith($"CA bundle '{path}' not loaded: ", Assert.Single(broken.Warnings), StringComparison.Ordinal);
        Assert.Empty(broken.ExtraTrustedCertificates!);
    }

    public void Dispose() => _pki.Dispose();
}
