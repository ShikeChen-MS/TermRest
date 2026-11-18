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

    /// <summary>
    /// The content type the part is sent with (recordings and the cURL export give the same): as typed, as curl and Postman
    /// send it; for a file without one, the type its extension names, as they choose it (for a few common ones), else
    /// application/octet-stream. Null for text without one.
    /// </summary>
    public string? SentContentType => ContentType ?? (FilePath is null ? null : TypeOfFile(FilePath));

    /// <summary>curl's types for file extensions, and JSON's.</summary>
    private static string TypeOfFile(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".gif" => "image/gif",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        ".txt" => "text/plain",
        ".htm" or ".html" => "text/html",
        ".pdf" => "application/pdf",
        ".xml" => "application/xml",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };
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
            PreparedBodyKind.File => new StreamContent(OpenFile(FilePath!)),
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

    /// <summary>
    /// A file to send, opened as <see cref="BodyFile.Check"/> opens it: a file another program has open for writing (Excel,
    /// a log writer) is sent too, as curl and Postman send it. Sharing it for reading only, Windows would refuse to open it.
    /// It is sent as long as it is now (see <see cref="BoundedFile"/>); a pipe, which has no length, in chunks.
    /// </summary>
    private static Stream OpenFile(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            return file.CanSeek ? new BoundedFile(file, file.Length) : file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The parts as browsers, curl and Postman send them: names and file names in UTF-8, in quotes, with only '"', CR and LF
    /// escaped (as %22, %0D and %0A); MultipartFormDataContent would write non-ASCII ones as RFC 2047 encoded-words, which
    /// form parsers don't decode, add a filename* (which RFC 7578 rules out), and refuse a '"'.
    /// </summary>
    private MultipartContent CreateMultipart()
    {
        var multipart = new MultipartContent("form-data") { HeaderEncodingSelector = (_, _) => Encoding.UTF8 };
        HttpContent? partContent = null;
        try
        {
            foreach (var part in Parts)
            {
                var disposition = $"form-data; name={QuoteName(part.Name)}";
                if (part.FilePath is not null)
                {
                    partContent = new StreamContent(OpenFile(part.FilePath));
                    disposition += $"; filename={QuoteName(Path.GetFileName(part.FilePath))}";
                }
                else
                {
                    partContent = new ByteArrayContent(Encoding.UTF8.GetBytes(part.Value ?? ""));
                }

                partContent.Headers.TryAddWithoutValidation("Content-Disposition", disposition);

                // As typed (MediaTypeHeaderValue would refuse 'jpeg'); RequestPreparer refuses a line break.
                if (part.SentContentType is { } type)
                {
                    partContent.Headers.TryAddWithoutValidation("Content-Type", type);
                }

                multipart.Add(partContent);
                partContent = null;
            }
        }
        catch
        {
            // Not sent: the files opened so far are closed (Windows would keep them locked until a garbage collection).
            partContent?.Dispose();
            multipart.Dispose();
            throw;
        }

        return multipart;
    }

    private static string QuoteName(string name) =>
        "\"" + name.Replace("\"", "%22", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// A file read up to the length it had when it was opened, which Content-Length announces: what another program
    /// appends while it is sent (a log) stays out, where .NET would fail the request for a body longer than announced. A
    /// file that gets shorter still fails it.
    /// </summary>
    private sealed class BoundedFile(FileStream file, long length) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => file.Position;
            set => file.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => file.Read(buffer[..Available(buffer.Length)]);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            file.ReadAsync(buffer[..Available(buffer.Length)], cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            origin == SeekOrigin.End ? file.Seek(length + offset, SeekOrigin.Begin) : file.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                file.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Available(int count) => (int)Math.Clamp(length - file.Position, 0, count);
    }
}

/// <summary>Where a prepared request came from; recorded with the history entry.</summary>
/// <param name="Spec">The request as edited when it was sent (variables unresolved).</param>
/// <param name="CollectionName">The saved collection it belongs to (null for scratch requests).</param>
/// <param name="RequestPath">Its path in the collection (null when not saved).</param>
/// <param name="RequestName">Its name (null when not saved).</param>
/// <param name="AuthInherited">The request inherits its auth (from its folders or collection); <see cref="Spec"/> holds a copy of it.</param>
/// <param name="CollectionFile">
/// The file of that collection (a v3 collection's folder) in the workspace, relative with "/": it tells collections of
/// the same name apart.
/// </param>
public sealed record RequestSource(Model.HttpRequestSpec Spec, string? CollectionName, string? RequestPath, string? RequestName, bool AuthInherited = false,
    string? CollectionFile = null);

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

    /// <summary>The challenge schemes <see cref="Credential"/> answers, on the request's own origin only (see <see cref="Auth.AppliedAuth"/>).</summary>
    public string[] CredentialSchemes { get; set; } = [];

    /// <summary>
    /// Headers the auth helper added (an API key …). Like Authorization and Cookie, they are not sent when a redirect
    /// leads to another origin.
    /// </summary>
    public HashSet<string> AuthHeaderNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string AuthDescription { get; set; } = "No auth";

    /// <summary>
    /// The auth the request was prepared with: its own or the one it inherits, and of credential sets the one its URL
    /// selected (see <see cref="Model.AuthSpec.CredentialSets"/>); null for none.
    /// </summary>
    public Model.AuthSpec? AuthUsed { get; set; }

    /// <summary>
    /// OAuth 2.0 settings the access token is fetched with before the request is sent (see
    /// <see cref="Workspace.RequestRunner"/>); null when its auth needs no token fetched.
    /// </summary>
    public Auth.OAuth2Settings? OAuth2 { get; set; }

    /// <summary>The token added for <see cref="OAuth2"/> (see <see cref="Auth.OAuth2Tokens.Apply"/>); null until it is.</summary>
    public Auth.OAuth2Result? OAuth2Token { get; set; }

    public ClientCertificateSelection? ClientCertificate { get; set; }

    /// <summary>
    /// Selects the client certificate for a URL a redirect leads to, as <see cref="ClientCertificate"/> was for
    /// <see cref="Uri"/>; null when there is no certificate registry.
    /// </summary>
    public Func<Uri, ClientCertificateSelection?>? ClientCertificateSelector { get; set; }

    public bool StrictSsl { get; set; } = true;

    public X509Certificate2Collection? ExtraTrustedCertificates { get; set; }

    public bool FollowRedirects { get; set; } = true;

    public int MaxRedirects { get; set; } = 10;

    public bool UseCookies { get; set; } = true;

    public TimeSpan? Timeout { get; set; }

    public string HttpVersion { get; set; } = "1.1";

    /// <summary>Null/"system" = OS proxy, "none" = direct, otherwise a proxy URL.</summary>
    public string? Proxy { get; set; }

    /// <summary>The proxy's user name and password (see <see cref="ExecutionDefaults.ProxyCredential"/>); never recorded.</summary>
    public System.Net.NetworkCredential? ProxyCredential { get; set; }

    public long MaxResponseBytes { get; set; } = 50L * 1024 * 1024;

    public List<string> Warnings { get; } = [];

    /// <summary>Problems that prevent sending (invalid URL, missing body file, ...).</summary>
    public List<string> Errors { get; } = [];

    public SortedSet<string> UnresolvedVariables { get; } = new(StringComparer.Ordinal);

    /// <summary>Set by <see cref="Workspace.RequestRunner.Prepare"/>; requests built elsewhere are not recorded in the history.</summary>
    public RequestSource? Source { get; set; }

    /// <summary>Resolved values of secret variables and auth credentials, masked by <see cref="Redact"/>.</summary>
    /// <remarks>Add to it with <see cref="AddSecretValues"/>, which also adds the form a value is sent in.</remarks>
    public HashSet<string> SecretValues { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolved values of variables named like credentials (password, token, apiKey …) that are not typed "secret".
    /// Recordings redact them as well (see <see cref="Recording.Redactor"/>).
    /// </summary>
    /// <remarks>Add to it with <see cref="AddSensitiveValues"/>, which also adds the form a value is sent in.</remarks>
    public HashSet<string> SensitiveValues { get; } = new(StringComparer.Ordinal);

    /// <summary>Adds secret values (see <see cref="SecretValues"/>) as they are and as they are sent (see <see cref="AddForms"/>).</summary>
    public void AddSecretValues(IEnumerable<string> values) => AddForms(SecretValues, values);

    /// <summary>Adds values of variables named like credentials (see <see cref="SensitiveValues"/>), as they are and as they are sent.</summary>
    public void AddSensitiveValues(IEnumerable<string> values) => AddForms(SensitiveValues, values);

    /// <summary>
    /// Adds each value, and also without the white space around it: a header value goes without its trailing line break (a
    /// token pasted or read with one), and servers that echo a value drop the spaces around it. Only that form may come back.
    /// A value with a '#' or a '?' is also added as a URL holds it (see <see cref="RequestPreparer"/>): with its '#' as %23
    /// (in the path or the query), and also its '?' as %3F (in a path variable); and one with a character the user info
    /// of a URL encodes, as it holds it (<see cref="RequestPreparer.InUserInfo"/>).
    /// </summary>
    private static void AddForms(HashSet<string> set, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (value.Length == 0)
            {
                continue;
            }

            set.Add(value);
            var trimmed = value.Trim();
            if (trimmed.Length > 0 && trimmed.Length < value.Length)
            {
                set.Add(trimmed);
            }

            if (value.AsSpan().IndexOfAny('#', '?') >= 0)
            {
                var inUrl = value.Replace("#", "%23", StringComparison.Ordinal);
                set.Add(inUrl);
                set.Add(inUrl.Replace("?", "%3F", StringComparison.Ordinal));
            }

            set.Add(RequestPreparer.InUserInfo(value));
        }
    }

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
