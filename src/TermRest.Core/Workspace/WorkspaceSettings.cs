using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using TermRest.Core.Certificates;

namespace TermRest.Core.Workspace;

/// <summary>Per-workspace settings stored in <c>.termrest/settings.json</c>.</summary>
public sealed class WorkspaceSettings
{
    /// <summary>Smallest <see cref="YamlLineWidth"/> the user interfaces accept (narrower wrapping is not readable).</summary>
    public const int MinYamlLineWidth = 20;

    /// <summary>Largest <see cref="YamlLineWidth"/> the user interfaces accept.</summary>
    public const int MaxYamlLineWidth = 1000;

    /// <summary>Name of the selected environment.</summary>
    public string? ActiveEnvironment { get; set; }

    /// <summary>
    /// File of the selected environment, relative to the workspace folder with "/" separators: it tells environments of
    /// the same name apart. Without it (settings of earlier versions), or when that file is not loaded, the name decides.
    /// </summary>
    public string? ActiveEnvironmentFile { get; set; }

    /// <summary>Verify server certificates (Postman "SSL certificate verification"). Requests can override with strictSSL.</summary>
    public bool StrictSsl { get; set; } = true;

    /// <summary>Extra trusted CA certificates (PEM bundle) in addition to the OS trust store. Relative to the workspace.</summary>
    public string? CaBundle { get; set; }

    /// <summary>Request timeout in milliseconds; 0 means no timeout (cancel with Esc).</summary>
    public int TimeoutMs { get; set; }

    public bool FollowRedirects { get; set; } = true;

    public int MaxRedirects { get; set; } = 10;

    /// <summary>
    /// Null/"system" uses the OS proxy settings, "none" disables proxies, otherwise a proxy URL: used only once the user
    /// allowed it for the workspace folder (<see cref="UserSettings.AllowedProxies"/>), as this file comes with the workspace.
    /// </summary>
    public string? Proxy { get; set; }

    /// <summary>Whether <paramref name="proxy"/> names a proxy (a URL), rather than "system" or "none" (see <see cref="Proxy"/>).</summary>
    public static bool IsProxyUrl([NotNullWhen(true)] string? proxy) => proxy?.Trim().ToLowerInvariant() is not (null or "" or "system" or "none");

    /// <summary>Responses larger than this are truncated in memory.</summary>
    public int MaxResponseSizeMb { get; set; } = 50;

    /// <summary>"1.1" (default, like Postman) or "2" (HTTP/2 when the server supports it).</summary>
    public string HttpVersion { get; set; } = "1.1";

    /// <summary>
    /// Format of new collections and environments: "v3" (Postman v3 YAML folders under postman/), "v2.1" (JSON files),
    /// or null to follow the workspace's existing layout.
    /// </summary>
    public string? CollectionFormat { get; set; }

    /// <summary>
    /// Wrapping of long values in Postman v3 YAML files (collections and environments), as Postman's YAML library does
    /// it: at this column, or never when 0. Null (the default) follows each file: files that Postman wrapped at 80
    /// columns stay wrapped, other files and new ones are not wrapped, as current Postman versions write them. Only new
    /// and changed values are wrapped; lines that do not change keep their layout.
    /// </summary>
    public int? YamlLineWidth { get; set; }

    /// <summary>A short description of a <see cref="YamlLineWidth"/> value, e.g. "wrapped at 80 columns".</summary>
    public static string DescribeYamlLineWidth(int? width) => width switch
    {
        null => "like each file (new files are not wrapped)",
        <= 0 => "never wrapped",
        _ => $"wrapped at {width} columns",
    };

    /// <summary>Record sent requests in the history (kept per user, outside the workspace folder).</summary>
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>Number of history entries kept for this workspace.</summary>
    public int HistoryLimit { get; set; } = 500;

    /// <summary>Response bodies up to this size (KB) are kept with each history entry; 0 keeps none.</summary>
    public int HistoryResponseKb { get; set; } = 256;

    /// <summary>
    /// Color theme of the full-screen UI for this workspace, overriding the user's theme (for example a red one for a
    /// production workspace); null uses the user's theme.
    /// </summary>
    public string? Theme { get; set; }

    /// <summary>
    /// Extra names of headers, URL parameters, body fields and variables whose values recordings redact, in addition
    /// to the built-in credential names (Authorization, Cookie, password, token, api key …). Compared without case
    /// and separators, at the end of names: "Tenant-Key" matches X-Tenant-Key and tenant_key.
    /// </summary>
    public List<string>? RecordingRedact { get; set; }

    public WorkspaceSettings Clone()
    {
        var clone = (WorkspaceSettings)MemberwiseClone();
        clone.RecordingRedact = RecordingRedact is null ? null : [.. RecordingRedact];
        return clone;
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowTrailingCommas = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkspaceSettings))]
[JsonSerializable(typeof(UserSettings))]
[JsonSerializable(typeof(ProxyCredentialFile))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
[JsonSerializable(typeof(List<CertificateEntry>))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;
