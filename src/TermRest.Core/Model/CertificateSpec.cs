using System.Text.Json.Nodes;

namespace TermRest.Core.Model;

/// <summary>Postman <c>request.certificate</c>: a client certificate pinned to the request.</summary>
public sealed class CertificateSpec
{
    public string? Name { get; set; }

    public List<string> Matches { get; set; } = [];

    public string? CertSrc { get; set; }

    public string? KeySrc { get; set; }

    /// <summary>Not in the Postman schema; accepted for convenience.</summary>
    public string? PfxSrc { get; set; }

    public string? Passphrase { get; set; }

    public JsonObject? Source { get; set; }

    public CertificateSpec Clone() => new()
    {
        Name = Name,
        Matches = [.. Matches],
        CertSrc = CertSrc,
        KeySrc = KeySrc,
        PfxSrc = PfxSrc,
        Passphrase = Passphrase,
        Source = Source,
    };

    public bool ContentEquals(CertificateSpec? other) =>
        other is not null && Name == other.Name && Matches.SequenceEqual(other.Matches) && CertSrc == other.CertSrc
        && KeySrc == other.KeySrc && PfxSrc == other.PfxSrc && Passphrase == other.Passphrase;

    public static bool AreEqual(CertificateSpec? a, CertificateSpec? b) => a is null ? b is null : a.ContentEquals(b);
}

/// <summary>Item-level <c>protocolProfileBehavior</c> values TermRest understands. Other keys are preserved.</summary>
public sealed class RequestSettings
{
    public bool? StrictSsl { get; set; }

    public bool? FollowRedirects { get; set; }

    public int? MaxRedirects { get; set; }

    public bool? DisableCookies { get; set; }

    public JsonObject? Source { get; set; }

    public RequestSettings Clone() => new()
    {
        StrictSsl = StrictSsl,
        FollowRedirects = FollowRedirects,
        MaxRedirects = MaxRedirects,
        DisableCookies = DisableCookies,
        Source = Source,
    };

    public bool ContentEquals(RequestSettings other) =>
        StrictSsl == other.StrictSsl && FollowRedirects == other.FollowRedirects && MaxRedirects == other.MaxRedirects
        && DisableCookies == other.DisableCookies;
}
