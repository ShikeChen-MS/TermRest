using System.Text.Json.Serialization;
using TermRest.Core.Certificates;

namespace TermRest.Core.Workspace;

/// <summary>Per-workspace settings stored in <c>.termrest/settings.json</c>.</summary>
public sealed class WorkspaceSettings
{
    /// <summary>Name of the selected environment.</summary>
    public string? ActiveEnvironment { get; set; }

    /// <summary>Verify server certificates (Postman "SSL certificate verification"). Requests can override with strictSSL.</summary>
    public bool StrictSsl { get; set; } = true;

    /// <summary>Extra trusted CA certificates (PEM bundle) in addition to the OS trust store. Relative to the workspace.</summary>
    public string? CaBundle { get; set; }

    /// <summary>Request timeout in milliseconds; 0 means no timeout (cancel with Esc).</summary>
    public int TimeoutMs { get; set; }

    public bool FollowRedirects { get; set; } = true;

    public int MaxRedirects { get; set; } = 10;

    /// <summary>Null/"system" uses the OS proxy settings, "none" disables proxies, otherwise a proxy URL.</summary>
    public string? Proxy { get; set; }

    /// <summary>Responses larger than this are truncated in memory.</summary>
    public int MaxResponseSizeMb { get; set; } = 50;

    /// <summary>"1.1" (default, like Postman) or "2" (HTTP/2 when the server supports it).</summary>
    public string HttpVersion { get; set; } = "1.1";

    public WorkspaceSettings Clone() => (WorkspaceSettings)MemberwiseClone();
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowTrailingCommas = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkspaceSettings))]
[JsonSerializable(typeof(List<CertificateEntry>))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;
