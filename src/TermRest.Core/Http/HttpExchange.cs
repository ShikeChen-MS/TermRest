using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using TermRest.Core.Certificates;

namespace TermRest.Core.Http;

/// <summary>What happened when a request was sent: the response (if any), timing, TLS details and errors.</summary>
public sealed class HttpExchange(PreparedRequest request)
{
    public PreparedRequest Request { get; } = request;

    public ResponseSnapshot? Response { get; set; }

    public string? ErrorMessage { get; set; }

    public Exception? Exception { get; set; }

    public bool Cancelled { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;

    public TimingBreakdown Timing { get; set; } = new();

    public List<TlsConnectionInfo> Tls { get; } = [];

    /// <summary>Request headers as sent (including generated ones such as User-Agent and Cookie).</summary>
    public List<KeyValuePair<string, string>> SentHeaders { get; } = [];

    public bool IsSuccess => Response is not null && ErrorMessage is null;

    public string Summary
    {
        get
        {
            if (Cancelled)
            {
                return "Cancelled";
            }

            if (Response is null)
            {
                return $"Error: {ErrorMessage}";
            }

            return $"{Response.StatusCode} {Response.ReasonPhrase} · {Format(Timing.Total)} · {FormatSize(Response.Body.LongLength)}";
        }
    }

    public static string Format(TimeSpan? value) =>
        value is not { } v ? "-" : v.TotalMilliseconds < 1000 ? $"{v.TotalMilliseconds:0} ms" : $"{v.TotalSeconds:0.00} s";

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB",
    };
}

public sealed class ResponseSnapshot
{
    public int StatusCode { get; init; }

    public string ReasonPhrase { get; init; } = "";

    public Version Version { get; init; } = HttpVersion.Version11;

    public List<KeyValuePair<string, string>> Headers { get; init; } = [];

    public byte[] Body { get; init; } = [];

    public bool BodyTruncated { get; init; }

    public string? ContentType { get; init; }

    public Uri? FinalUri { get; init; }

    public string? GetHeader(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

public sealed class TimingBreakdown
{
    public TimeSpan? Dns { get; set; }

    public TimeSpan? Connect { get; set; }

    /// <summary>Approximate: from TCP connect until the server certificate was validated.</summary>
    public TimeSpan? Tls { get; set; }

    /// <summary>Time to first byte after the connection was ready (server processing + transfer).</summary>
    public TimeSpan? Waiting { get; set; }

    public TimeSpan? Download { get; set; }

    public TimeSpan? Total { get; set; }

    public bool ReusedConnection { get; set; }

    public string? RemoteEndPoint { get; set; }

    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"DNS lookup      {HttpExchange.Format(Dns)}\n");
        builder.Append(CultureInfo.InvariantCulture, $"TCP connect     {HttpExchange.Format(Connect)}\n");
        builder.Append(CultureInfo.InvariantCulture, $"TLS handshake   {HttpExchange.Format(Tls)}\n");
        builder.Append(CultureInfo.InvariantCulture, $"Waiting (TTFB)  {HttpExchange.Format(Waiting)}\n");
        builder.Append(CultureInfo.InvariantCulture, $"Download        {HttpExchange.Format(Download)}\n");
        builder.Append(CultureInfo.InvariantCulture, $"Total           {HttpExchange.Format(Total)}");
        if (RemoteEndPoint is not null)
        {
            builder.Append(CultureInfo.InvariantCulture, $"\nRemote address  {RemoteEndPoint}");
        }

        return builder.ToString();
    }
}

public sealed class TlsConnectionInfo
{
    public string Host { get; set; } = "";

    public SslProtocols Protocol { get; set; }

    public TlsCipherSuite? CipherSuite { get; set; }

    public string? ApplicationProtocol { get; set; }

    public List<CertificateSummary> ServerChain { get; } = [];

    public List<string> ChainStatus { get; } = [];

    public SslPolicyErrors PolicyErrors { get; set; }

    /// <summary>Whether TermRest accepted the server certificate, and why.</summary>
    public bool Accepted { get; set; }

    public string AcceptReason { get; set; } = "";

    public CertificateSummary? ClientCertificate { get; set; }

    public string? ClientCertificateOrigin { get; set; }

    /// <summary>Whether the server asked for a client certificate during the handshake.</summary>
    public bool ClientCertificateRequested { get; set; }

    /// <summary>Issuer names the server said it accepts (may be empty even when a certificate is requested).</summary>
    public List<string> AcceptableIssuers { get; } = [];

    public string Describe()
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"Host              {Host}\n");
        builder.Append(CultureInfo.InvariantCulture,
            $"Protocol          {(Protocol == SslProtocols.None ? "(handshake not completed)" : Protocol.ToString())} · {CipherSuite?.ToString() ?? "-"} · ALPN {ApplicationProtocol ?? "-"}\n");
        builder.Append(CultureInfo.InvariantCulture, $"Client cert sent  {(ClientCertificate is null ? "none" : $"{ClientCertificate.Subject} ({ClientCertificateOrigin})")}\n");
        if (ClientCertificate is not null)
        {
            builder.Append(CultureInfo.InvariantCulture, $"                  thumbprint {ClientCertificate.Thumbprint}, expires {ClientCertificate.NotAfter:yyyy-MM-dd}\n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"Client cert asked {(ClientCertificateRequested ? "yes" : "no / not observed")}\n");
        foreach (var issuer in AcceptableIssuers.Take(10))
        {
            builder.Append(CultureInfo.InvariantCulture, $"  accepts issuer  {issuer}\n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"Server validation {(Accepted ? "accepted" : "REJECTED")} — {AcceptReason}\n");
        if (PolicyErrors != SslPolicyErrors.None)
        {
            builder.Append(CultureInfo.InvariantCulture, $"Policy errors     {PolicyErrors}\n");
        }

        foreach (var status in ChainStatus)
        {
            builder.Append(CultureInfo.InvariantCulture, $"Chain status      {status}\n");
        }

        builder.Append("\nServer certificate chain:\n");
        for (var i = 0; i < ServerChain.Count; i++)
        {
            var c = ServerChain[i];
            builder.Append(CultureInfo.InvariantCulture, $"  [{i}] {c.Subject}\n");
            builder.Append(CultureInfo.InvariantCulture, $"      issuer {c.Issuer}\n");
            builder.Append(CultureInfo.InvariantCulture, $"      valid {c.NotBefore:yyyy-MM-dd} → {c.NotAfter:yyyy-MM-dd}{(c.IsExpired ? " (EXPIRED)" : "")}, thumbprint {c.Thumbprint}\n");
        }

        return builder.ToString().TrimEnd();
    }
}
