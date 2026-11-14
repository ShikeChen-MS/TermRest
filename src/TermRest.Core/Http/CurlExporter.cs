using System.Buffers;
using System.Text;
using TermRest.Core.Certificates;

namespace TermRest.Core.Http;

/// <summary>Renders a prepared request as a POSIX-shell cURL command.</summary>
public static class CurlExporter
{
    /// <summary>What ends a form-data part's content type for curl before its first ';'.</summary>
    private static readonly SearchValues<char> TypeEnds = SearchValues.Create("()<>@,:\\\"[]?= ");

    /// <exception cref="NotSupportedException">
    /// curl cannot send the request as TermRest does (a HEAD request with a body; a form-data field whose name has an '=',
    /// or whose content type curl would cut short or read in part as a setting of its own); the message says why.
    /// </exception>
    public static string Export(PreparedRequest request, bool multiline = true)
    {
        var body = request.Body;

        // Whether options send a body: curl then sends POST unless the method is named. A HEAD request goes without an
        // empty one, as --head allows none and curl would wait for a response body after -X HEAD.
        var data = body.Kind switch
        {
            PreparedBodyKind.Text => request.Method != "HEAD" || !string.IsNullOrEmpty(body.Text),
            PreparedBodyKind.FormUrlEncoded => body.Fields.Count > 0,
            PreparedBodyKind.Multipart => body.Parts.Count > 0,
            PreparedBodyKind.File => true,
            _ => false,
        };
        if (request.Method == "HEAD" && data)
        {
            throw new NotSupportedException("A HEAD request with a body cannot be sent with curl: --head sends no body, and after -X HEAD curl waits for a response body that never comes.");
        }

        // Without a body, .NET still sends Content-Length: 0 unless the method is GET, HEAD, DELETE, OPTIONS or CONNECT, and
        // some servers refuse a POST without one (411 Length Required). curl sends none without data: it gets empty data,
        // without the type curl would label it with.
        var empty = !data && request.Method is not ("GET" or "HEAD" or "DELETE" or "OPTIONS" or "CONNECT");
        data |= empty;

        var parts = new List<string> { "curl" };
        if (request.Method == "HEAD")
        {
            parts.Add("--head");
        }
        else if (request.Method != (data ? "POST" : "GET"))
        {
            // Not for a POST, which curl sends anyway for its data: after -X POST, curl would also POST to the URL a 301, 302
            // or 303 leads to, where TermRest sends a GET without the body (as curl does otherwise).
            // Invalid methods are refused only when sent: one such as "GET;calc" must reach the shell quoted.
            var method = request.Method.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? request.Method : Quote(request.Method);
            parts.Add($"-X {method}");
        }

        // The URL as TermRest sends it: encoded by System.Uri (curl refuses one with a space), the host name in ASCII (punycode).
        var url = request.Uri is not { } uri ? request.Url
            : uri.HostNameType == UriHostNameType.Dns && uri.IdnHost != uri.Host ? new UriBuilder(uri) { Host = uri.IdnHost }.Uri.AbsoluteUri
            : uri.AbsoluteUri;

        // curl would expand [1-9] and {a,b} in the URL into several requests; TermRest sends it as it is.
        if (url.AsSpan().IndexOfAny("[]{}") >= 0)
        {
            parts.Add("--globoff");
        }

        parts.Add(Quote(url));
        foreach (var (key, value) in request.Headers)
        {
            // Left out as TermRest leaves them out: Content-Length, which it computes from the body, and on a request without
            // a body the headers that would describe one.
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || (body.Kind == PreparedBodyKind.None && (HttpExecutor.IsContentHeader(key) || key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            // curl would leave out a header without a value; written 'Name;', it is sent empty.
            parts.Add($"-H {Quote(value.AsSpan().Trim(" \t").IsEmpty ? key + ";" : $"{key}: {value}")}");
        }

        switch (body.Kind)
        {
            case PreparedBodyKind.Text:
                if (data)
                {
                    parts.Add($"--data-raw {Quote(body.Text ?? "")}");
                }

                if (request.GetHeader("Content-Type") is null && body.ContentType is { } contentType)
                {
                    parts.Add($"-H {Quote($"Content-Type: {contentType}")}");
                }

                break;
            case PreparedBodyKind.FormUrlEncoded:
                parts.AddRange(body.Fields.Select(f => $"--data-urlencode {Quote($"{f.Key}={f.Value}")}"));
                break;
            case PreparedBodyKind.Multipart:
                // curl ends the name at the first '=': the rest would be the value, a file to read if it starts with @ or <.
                if (body.Parts.FirstOrDefault(p => p.Name.Contains('=', StringComparison.Ordinal)) is { } part)
                {
                    throw new NotSupportedException($"The form-data field '{part.Name}' cannot be sent with curl, which ends a field's name at the first '='.");
                }

                // Text goes as it is: with --form-string, or "quoted" with -F to give it a type (-F reads a file for a value that
                // starts with @ or <, but not in quotes). A file's path is quoted for curl, which would take a ';' in it for a
                // parameter and a ',' for a list of files, and its type is named: curl knows the types of fewer extensions.
                foreach (var p in body.Parts)
                {
                    var type = PartType(p);
                    parts.Add(p.FilePath is not null ? $"-F {Quote($"{p.Name}=@{FormQuote(p.FilePath)};type={type}")}"
                        : type is null ? $"--form-string {Quote($"{p.Name}={p.Value}")}"
                        : $"-F {Quote($"{p.Name}={FormQuote(p.Value ?? "")};type={type}")}");
                }

                break;
            case PreparedBodyKind.File:
                parts.Add($"--data-binary {Quote("@" + body.FilePath)}");

                // curl would label the file application/x-www-form-urlencoded: name the type TermRest sends.
                if (request.GetHeader("Content-Type") is null && body.ContentType is { } fileType)
                {
                    parts.Add($"-H {Quote($"Content-Type: {fileType}")}");
                }

                break;
        }

        if (empty)
        {
            parts.Add("--data-raw '' -H 'Content-Type:'");
        }

        if (!request.StrictSsl)
        {
            parts.Add("--insecure");
        }

        if (request.Credential is { } credential)
        {
            // Only the scheme the auth helper uses: --anyauth would also let curl send the password with Basic.
            var scheme = request.CredentialSchemes.Contains("Digest") ? "--digest" : request.CredentialSchemes.Contains("NTLM") ? "--ntlm" : "--anyauth";
            parts.Add($"{scheme} -u {Quote($"{(string.IsNullOrEmpty(credential.Domain) ? "" : credential.Domain + "\\")}{credential.UserName}:{credential.Password}")}");
        }

        var notes = "";
        var certificateFile = false;
        if (request.ClientCertificate is { } selection)
        {
            var entry = selection.Entry;
            switch (entry.Kind)
            {
                case CertificateSourceKind.Pem when entry.Cert is not null:
                    // Full paths: relative ones are relative to the workspace (or the collection), not to curl's folder.
                    parts.Add($"--cert {Quote(selection.ResolvePath(entry.Cert.Src))}");
                    if (entry.Key is not null)
                    {
                        parts.Add($"--key {Quote(selection.ResolvePath(entry.Key.Src))}");
                    }

                    certificateFile = true;
                    break;
                case CertificateSourceKind.Pfx:
                    parts.Add($"--cert-type P12 --cert {Quote(selection.ResolvePath(entry.Pfx!.Src))}");
                    certificateFile = true;
                    break;
                default:
                    notes += $"# client certificate from OS store: {QuoteInNote(selection.Summary.Subject)} ({selection.Summary.Thumbprint})\n";
                    break;
            }
        }

        // TermRest follows no redirect when following is off or its limit is 0.
        if (request.FollowRedirects && request.MaxRedirects > 0)
        {
            // TermRest sends the auth helper's headers (an API key …) and Proxy-Authorization only to the request's own
            // origin; curl withholds only Authorization and Cookie (and Host) from other hosts it is redirected to. And
            // TermRest selects the client certificate for each URL a redirect leads to, where curl presents the one it is
            // given on every connection.
            var forwarded = request.Headers.Select(h => h.Key)
                .Where(k => (request.AuthHeaderNames.Contains(k) || k.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
                    && k.ToLowerInvariant() is not ("authorization" or "cookie" or "host"))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> withheld = forwarded.Count > 0 ? [$"send {string.Join(", ", forwarded.Select(QuoteInNote))}"] : [];
            if (certificateFile)
            {
                withheld.Add("present the client certificate");
            }

            if (withheld.Count > 0)
            {
                notes += $"# redirects are not followed: curl would {string.Join(" and ", withheld)} to every host it is redirected to\n";
            }
            else
            {
                // TermRest's limit (curl's is 50), and from an https URL only to https: TermRest never follows a redirect from
                // https to http. Where TermRest stops and shows the redirect, curl ends with an error. From an http URL, curl
                // also follows one to ftp, or back to http after https, which TermRest doesn't. '=https' is quoted for zsh,
                // which would replace it with the path of a command named https.
                var follow = $"--location --max-redirs {request.MaxRedirects}";
                if (request.Uri?.Scheme == Uri.UriSchemeHttps)
                {
                    follow += $" --proto-redir {Quote("=https")}";
                }

                // TermRest resends any method but POST with its body after a 301 or 302, and a GET also after a 303. Still
                // different: after a 303, curl keeps any other method (without the body) where TermRest sends GET; a POST
                // stays a POST in curl after a 300; and without the body, curl still sends the headers given with -H that
                // describe it (Content-Type).
                if (data && request.Method != "POST")
                {
                    follow += request.Method == "GET" ? " --post301 --post302 --post303" : " --post301 --post302";
                }

                parts.Add(follow);
            }
        }

        return notes + Join(parts, multiline);
    }

    /// <summary>
    /// The content type TermRest sends a form-data part with (see <see cref="MultipartPart.SentContentType"/>), for -F's
    /// ;type=; null for text without one.
    /// </summary>
    private static string? PartType(MultipartPart part)
    {
        if (part.SentContentType is not { } type)
        {
            return null;
        }

        // curl sends the rest of the value as the type, but ends it at a space or a special character such as '=' or '('
        // before its first ';', reads a filename=, headers= (headers=@file reads a file) or encoder= after a ';' as a setting
        // of its own and, after a file, a ',' as the start of another file to send.
        var parameters = type.Split(';');
        if (parameters[0].AsSpan().IndexOfAny(TypeEnds) >= 0 || type.AsSpan().IndexOfAny('\r', '\n') >= 0
            || (part.FilePath is not null && type.Contains(',', StringComparison.Ordinal))
            || parameters.Skip(1).Select(p => p.TrimStart(' ', '\t')).Any(p => p.StartsWith("filename=", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("headers=", StringComparison.OrdinalIgnoreCase) || p.StartsWith("encoder=", StringComparison.OrdinalIgnoreCase)))
        {
            throw new NotSupportedException($"The form-data field '{part.Name}' cannot be sent with curl, which would send only part of its content type '{type}', or read part of it as a setting of its own or as another file.");
        }

        return type;
    }

    private static string Join(List<string> parts, bool multiline)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(multiline ? " \\\n  " : " ");
            }

            builder.Append(parts[i]);
        }

        return builder.ToString();
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// A value in a note: on a line of its own above the command (inside it, the comment would swallow the line
    /// continuation), and quoted for zsh, where '#' starts no comment at the prompt.
    /// </summary>
    private static string QuoteInNote(string value) => Quote(new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray()));

    /// <summary>Quotes a -F value or file name for curl, which takes it literally between the quotes (\ and " escaped).</summary>
    private static string FormQuote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
