using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TermRest.Core.Certificates;

namespace TermRest.Core.Http;

public enum PreparedBodyKind
{
    None,
    Text,
    FormUrlEncoded,
    Multipart,
    File,
}

public sealed class MultipartPart
{
    public required string Name { get; init; }

    public string? Value { get; init; }

    public string? FilePath { get; init; }

    public string? ContentType { get; init; }
}

/// <summary>A fully resolved request body. Content objects are created on demand so the same request can be re-sent.</summary>
public sealed class PreparedBody
{
    public PreparedBodyKind Kind { get; init; }

    public string? Text { get; init; }

    public string? ContentType { get; init; }

    public List<KeyValuePair<string, string>> Fields { get; init; } = [];

    public List<MultipartPart> Parts { get; init; } = [];

    public string? FilePath { get; init; }

    /// <summary>Type of a file body (json, xml…), checked when it is sent; null for a binary file.</summary>
    public string? FileLanguage { get; init; }

    /// <summary>Whether a file is sent: the body file, or a form-data file field.</summary>
    public bool HasFiles => Kind == PreparedBodyKind.File || (Kind == PreparedBodyKind.Multipart && Parts.Any(p => p.FilePath is not null));

    public static PreparedBody None { get; } = new();

    public HttpContent? CreateContent()
    {
        HttpContent? content = Kind switch
        {
            PreparedBodyKind.Text => new ByteArrayContent(Encoding.UTF8.GetBytes(Text ?? "")),
            PreparedBodyKind.FormUrlEncoded => new ByteArrayContent(Encoding.ASCII.GetBytes(EncodeForm(Fields))),
            PreparedBodyKind.File => new StreamContent(File.OpenRead(FilePath!)),
            PreparedBodyKind.Multipart => CreateMultipart(),
            _ => null,
        };

        if (content is not null && Kind != PreparedBodyKind.Multipart && ContentType is not null
            && MediaTypeHeaderValue.TryParse(ContentType, out var mediaType))
        {
            content.Headers.ContentType = mediaType;
        }

        return content;
    }

    /// <summary>Short human-readable description for the request timeline.</summary>
    public string Describe() => Kind switch
    {
        PreparedBodyKind.None => "(no body)",
        PreparedBodyKind.Text => Text ?? "",
        PreparedBodyKind.FormUrlEncoded => EncodeForm(Fields),
        PreparedBodyKind.File => FileLanguage is null ? $"<file {FilePath}>" : $"<{FileLanguage} file {FilePath}>",
        _ => string.Join('\n', Parts.Select(p => p.FilePath is null ? $"{p.Name}={p.Value}" : $"{p.Name}=@{p.FilePath}")),
    };

    public static string EncodeForm(IEnumerable<KeyValuePair<string, string>> fields) =>
        string.Join('&', fields.Select(f => $"{WebUtility.UrlEncode(f.Key)}={WebUtility.UrlEncode(f.Value)}"));

    private MultipartFormDataContent CreateMultipart()
    {
        var multipart = new MultipartFormDataContent();
        foreach (var part in Parts)
        {
            HttpContent partContent;
            string? fileName = null;
            if (part.FilePath is not null)
            {
                partContent = new StreamContent(File.OpenRead(part.FilePath));
                fileName = Path.GetFileName(part.FilePath);
                partContent.Headers.ContentType = MediaTypeHeaderValue.Parse(part.ContentType ?? "application/octet-stream");
            }
            else
            {
                partContent = new ByteArrayContent(Encoding.UTF8.GetBytes(part.Value ?? ""));
                if (part.ContentType is not null && MediaTypeHeaderValue.TryParse(part.ContentType, out var partType))
                {
                    partContent.Headers.ContentType = partType;
                }
            }

            if (fileName is null)
            {
                multipart.Add(partContent, part.Name);
            }
            else
            {
                multipart.Add(partContent, part.Name, fileName);
            }
        }

        return multipart;
    }
}

/// <summary>Where a prepared request came from; recorded with the history entry.</summary>
/// <param name="Spec">The request as edited when it was sent (variables unresolved).</param>
/// <param name="CollectionName">The saved collection it belongs to (null for scratch requests).</param>
/// <param name="RequestPath">Its path in the collection (null when not saved).</param>
/// <param name="RequestName">Its name (null when not saved).</param>
public sealed record RequestSource(Model.HttpRequestSpec Spec, string? CollectionName, string? RequestPath, string? RequestName);

/// <summary>A request with all variables, auth, body and TLS choices resolved, ready to send.</summary>
public sealed class PreparedRequest
{
    public string Method { get; set; } = "GET";

    /// <summary>The resolved URL text (what the user would see), even if it is not a valid URI.</summary>
    public string Url { get; set; } = "";

    public Uri? Uri { get; set; }

    public List<KeyValuePair<string, string>> Headers { get; } = [];

    public PreparedBody Body { get; set; } = PreparedBody.None;

    public NetworkCredential? Credential { get; set; }

    public string AuthDescription { get; set; } = "No auth";

    public ClientCertificateSelection? ClientCertificate { get; set; }

    public bool StrictSsl { get; set; } = true;

    public X509Certificate2Collection? ExtraTrustedCertificates { get; set; }

    public bool FollowRedirects { get; set; } = true;

    public int MaxRedirects { get; set; } = 10;

    public bool UseCookies { get; set; } = true;

    public TimeSpan? Timeout { get; set; }

    public string HttpVersion { get; set; } = "1.1";

    /// <summary>Null/"system" = OS proxy, "none" = direct, otherwise a proxy URL.</summary>
    public string? Proxy { get; set; }

    public long MaxResponseBytes { get; set; } = 50L * 1024 * 1024;

    public List<string> Warnings { get; } = [];

    /// <summary>Problems that prevent sending (invalid URL, missing body file, ...).</summary>
    public List<string> Errors { get; } = [];

    public SortedSet<string> UnresolvedVariables { get; } = new(StringComparer.Ordinal);

    /// <summary>Set by <see cref="Workspace.RequestRunner.Prepare"/>; requests built elsewhere are not recorded in the history.</summary>
    public RequestSource? Source { get; set; }

    /// <summary>Resolved values of secret variables and auth credentials, masked by <see cref="Redact"/>.</summary>
    public HashSet<string> SecretValues { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolved values of variables named like credentials (password, token, apiKey …) that are not typed "secret".
    /// Recordings redact them as well (see <see cref="Recording.Redactor"/>).
    /// </summary>
    public HashSet<string> SensitiveValues { get; } = new(StringComparer.Ordinal);

    public string? GetHeader(string name) =>
        Headers.LastOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Replaces every known secret value (4+ characters) in <paramref name="text"/> with <c>***</c>.</summary>
    public string Redact(string text)
    {
        foreach (var secret in SecretValues.Where(s => s.Length >= 4).OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        }

        return text;
    }
}
