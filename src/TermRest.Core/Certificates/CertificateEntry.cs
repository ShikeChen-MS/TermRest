using System.Text.Json.Serialization;

namespace TermRest.Core.Certificates;

/// <summary>
/// One client-certificate registration. The file format is Newman's <c>--ssl-client-cert-list</c> JSON
/// (name, matches, cert/key/pfx src, passphrase) plus a TermRest extension (<c>x-store</c>) for OS certificate stores.
/// </summary>
public sealed class CertificateEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>URL match patterns, e.g. <c>https://*.contoso.com/*</c> or <c>https://api.contoso.com:8443/*</c>.</summary>
    [JsonPropertyName("matches")]
    public List<string> Matches { get; set; } = [];

    [JsonPropertyName("cert")]
    public FileSource? Cert { get; set; }

    [JsonPropertyName("key")]
    public FileSource? Key { get; set; }

    [JsonPropertyName("pfx")]
    public FileSource? Pfx { get; set; }

    /// <summary>Literal text, <c>{{variable}}</c>, or <c>env:NAME</c> to read an environment variable.</summary>
    [JsonPropertyName("passphrase")]
    public string? Passphrase { get; set; }

    [JsonPropertyName("x-store")]
    public StoreSource? Store { get; set; }

    [JsonPropertyName("x-disabled")]
    public bool Disabled { get; set; }

    [JsonIgnore]
    public CertificateSourceKind Kind => Store is not null ? CertificateSourceKind.Store
        : Pfx is not null ? CertificateSourceKind.Pfx
        : CertificateSourceKind.Pem;

    public string DescribeSource() => Kind switch
    {
        CertificateSourceKind.Store => $"store {Store!.Location}\\{Store.StoreName} {(Store.Thumbprint is { Length: > 0 } t ? "thumbprint " + t : "subject " + Store.Subject)}",
        CertificateSourceKind.Pfx => $"pfx {Pfx!.Src}",
        _ => $"pem {Cert?.Src}{(Key is null ? "" : " + key " + Key.Src)}",
    };

    public CertificateEntry Clone() => new()
    {
        Name = Name,
        Matches = [.. Matches],
        Cert = Cert is null ? null : new FileSource { Src = Cert.Src },
        Key = Key is null ? null : new FileSource { Src = Key.Src },
        Pfx = Pfx is null ? null : new FileSource { Src = Pfx.Src },
        Passphrase = Passphrase,
        Store = Store is null ? null : new StoreSource { Location = Store.Location, StoreName = Store.StoreName, Thumbprint = Store.Thumbprint, Subject = Store.Subject },
        Disabled = Disabled,
    };
}

public enum CertificateSourceKind
{
    Pem,
    Pfx,
    Store,
}

public sealed class FileSource
{
    [JsonPropertyName("src")]
    public string Src { get; set; } = "";
}

public sealed class StoreSource
{
    /// <summary><c>CurrentUser</c> or <c>LocalMachine</c>.</summary>
    [JsonPropertyName("location")]
    public string Location { get; set; } = "CurrentUser";

    /// <summary>Store name, usually <c>My</c> (personal certificates; the login keychain on macOS).</summary>
    [JsonPropertyName("store")]
    public string StoreName { get; set; } = "My";

    [JsonPropertyName("thumbprint")]
    public string? Thumbprint { get; set; }

    [JsonPropertyName("subject")]
    public string? Subject { get; set; }
}
