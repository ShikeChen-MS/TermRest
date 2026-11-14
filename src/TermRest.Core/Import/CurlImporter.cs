using System.Net;
using System.Text;
using System.Text.Json;
using TermRest.Core.Model;
using TermRest.Core.Postman;

namespace TermRest.Core.Import;

public sealed class CurlImportResult
{
    public required HttpRequestSpec Spec { get; init; }

    /// <summary>A request name derived from the URL path.</summary>
    public required string SuggestedName { get; init; }

    public ShellDialect Dialect { get; init; }

    /// <summary>Parts of the command that could not be imported exactly, for the user to review.</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Turns a curl command line (as found in API docs or produced by "Copy as cURL" in browsers, Postman and TermRest)
/// into an editable request: method, URL and query, headers, body (raw, JSON, urlencoded, multipart, file), auth
/// (-u, --digest, --ntlm, --oauth2-bearer), client certificates (--cert/--key/--pass) and TLS verification (-k).
/// </summary>
public static class CurlImporter
{
    private static readonly Dictionary<char, string> ShortOptions = new()
    {
        ['0'] = "--http1.0", ['4'] = "--ipv4", ['6'] = "--ipv6", ['#'] = "--progress-bar",
        ['a'] = "--append", ['A'] = "--user-agent", ['b'] = "--cookie", ['B'] = "--use-ascii", ['c'] = "--cookie-jar",
        ['C'] = "--continue-at", ['d'] = "--data", ['D'] = "--dump-header", ['e'] = "--referer", ['E'] = "--cert",
        ['f'] = "--fail", ['F'] = "--form", ['g'] = "--globoff", ['G'] = "--get", ['h'] = "--help", ['H'] = "--header",
        ['i'] = "--include", ['I'] = "--head", ['j'] = "--junk-session-cookies", ['J'] = "--remote-header-name",
        ['k'] = "--insecure", ['K'] = "--config", ['l'] = "--list-only", ['L'] = "--location", ['m'] = "--max-time",
        ['M'] = "--manual", ['n'] = "--netrc", ['N'] = "--no-buffer", ['o'] = "--output", ['O'] = "--remote-name",
        ['p'] = "--proxytunnel", ['P'] = "--ftp-port", ['q'] = "--disable", ['Q'] = "--quote", ['r'] = "--range",
        ['R'] = "--remote-time", ['s'] = "--silent", ['S'] = "--show-error", ['t'] = "--telnet-option",
        ['T'] = "--upload-file", ['u'] = "--user", ['U'] = "--proxy-user", ['v'] = "--verbose", ['V'] = "--version",
        ['w'] = "--write-out", ['x'] = "--proxy", ['X'] = "--request", ['y'] = "--speed-time", ['Y'] = "--speed-limit",
        ['z'] = "--time-cond", ['Z'] = "--parallel",
    };

    /// <summary>Long options that take an argument. Everything else is a flag.</summary>
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--abstract-unix-socket", "--alt-svc", "--aws-sigv4", "--cacert", "--capath", "--cert", "--cert-type", "--ciphers",
        "--config", "--connect-timeout", "--connect-to", "--continue-at", "--cookie", "--cookie-jar", "--create-file-mode",
        "--crlfile", "--curves", "--data", "--data-ascii", "--data-binary", "--data-raw", "--data-urlencode", "--delegation",
        "--dns-interface", "--dns-ipv4-addr", "--dns-ipv6-addr", "--dns-servers", "--doh-url", "--dump-header", "--ech",
        "--egd-file", "--engine", "--etag-compare", "--etag-save", "--expect100-timeout", "--form", "--form-string",
        "--ftp-account", "--ftp-alternative-to-user", "--ftp-method", "--ftp-port", "--ftp-ssl-ccc-mode",
        "--happy-eyeballs-timeout-ms", "--header", "--hostpubmd5", "--hostpubsha256", "--hsts", "--interface", "--ip-tos",
        "--json", "--keepalive-cnt", "--keepalive-time", "--key", "--key-type", "--krb", "--limit-rate", "--local-port",
        "--login-options", "--mail-auth", "--mail-from", "--mail-rcpt", "--max-filesize", "--max-redirs", "--max-time",
        "--netrc-file", "--noproxy", "--oauth2-bearer", "--output", "--output-dir", "--parallel-max", "--pass",
        "--pinnedpubkey", "--preproxy", "--proto", "--proto-default", "--proto-redir", "--proxy", "--proxy-cacert",
        "--proxy-capath", "--proxy-cert", "--proxy-cert-type", "--proxy-ciphers", "--proxy-crlfile", "--proxy-header",
        "--proxy-key", "--proxy-key-type", "--proxy-pass", "--proxy-pinnedpubkey", "--proxy-service-name",
        "--proxy-tls13-ciphers", "--proxy-tlsauthtype", "--proxy-tlspassword", "--proxy-tlsuser", "--proxy-user",
        "--pubkey", "--quote", "--random-file", "--range", "--rate", "--referer", "--request", "--request-target",
        "--resolve", "--retry", "--retry-delay", "--retry-max-time", "--sasl-authzid", "--service-name", "--socks4",
        "--socks4a", "--socks5", "--socks5-gssapi-service", "--socks5-hostname", "--speed-limit", "--speed-time",
        "--stderr", "--telnet-option", "--time-cond", "--tls-max", "--tls13-ciphers", "--tlsauthtype", "--tlspassword",
        "--tlsuser", "--trace", "--trace-ascii", "--trace-config", "--unix-socket", "--upload-file", "--url", "--url-query",
        "--user", "--user-agent", "--variable", "--vlan-priority", "--write-out",
    };

    /// <summary>
    /// Options that only affect curl's own output, progress or retries, or that TermRest's own redirect rules cover (it
    /// follows redirects, changes the method as browsers do, and never follows one from https to http): nothing to
    /// import, nothing to report.
    /// </summary>
    private static readonly HashSet<string> QuietlyIgnored = new(StringComparer.Ordinal)
    {
        "--compressed", "--create-dirs", "--disable", "--dump-header", "--fail", "--fail-early", "--fail-with-body",
        "--globoff", "--include", "--ipv4", "--ipv6", "--junk-session-cookies", "--limit-rate", "--location",
        "--location-trusted", "--no-buffer", "--no-keepalive", "--no-progress-meter", "--output", "--output-dir",
        "--parallel", "--parallel-immediate", "--parallel-max", "--path-as-is", "--progress-bar", "--raw", "--remote-header-name",
        "--remote-name", "--remote-name-all", "--remote-time", "--retry", "--retry-all-errors", "--retry-connrefused",
        "--retry-delay", "--retry-max-time", "--show-error", "--show-headers", "--silent", "--ssl", "--ssl-reqd",
        "--sslv2", "--sslv3", "--stderr", "--styled-output", "--no-styled-output", "--tcp-fastopen", "--tcp-nodelay",
        "--tlsv1", "--tlsv1.0", "--tlsv1.1", "--tlsv1.2", "--tlsv1.3", "--trace", "--trace-ascii", "--trace-config",
        "--trace-ids", "--trace-time", "--verbose", "--write-out", "--xattr", "--clobber", "--no-clobber",
        "--remove-on-error", "--skip-existing", "--post301", "--post302", "--post303", "--proto-redir", "--proxy-insecure",
        "--suppress-connect-headers", "--keepalive-time", "--keepalive-cnt", "--rate", "--cookie-jar", "--basic",
    };

    /// <summary>Characters that never appear in application/x-www-form-urlencoded data.</summary>
    private static readonly System.Buffers.SearchValues<char> NotFormEncoded = System.Buffers.SearchValues.Create(" \t\r\n{}[]\"<>");

    private enum DataKind
    {
        Ascii,
        Raw,
        Binary,
        UrlEncode,
        Json,
    }

    private enum ChallengeAuth
    {
        Basic,
        Digest,
        Ntlm,
        Any,
        Negotiate,
    }

    private sealed class ParsedCommand
    {
        public List<string> Urls { get; } = [];

        public string? Method { get; set; }

        public List<string> Headers { get; } = [];

        public List<(DataKind Kind, string Value)> Data { get; } = [];

        public List<(string Value, bool Literal)> Forms { get; } = [];

        public List<string> UrlQueries { get; } = [];

        public string? User { get; set; }

        public ChallengeAuth Auth { get; set; }

        public string? Bearer { get; set; }

        public string? Cert { get; set; }

        public string? CertType { get; set; }

        public string? Key { get; set; }

        public string? Pass { get; set; }

        public string? UploadFile { get; set; }

        public bool Get { get; set; }

        public bool Head { get; set; }

        public bool Insecure { get; set; }

        public int? MaxRedirects { get; set; }
    }

    /// <summary>
    /// True when the text starts like a curl command (optionally after a shell prompt, or after comment lines such as the
    /// notes above a command TermRest exports).
    /// </summary>
    public static bool LooksLikeCurl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var start = SkipCommentLines(text);
        start = StripPrompt(start).TrimStart('&', ' ');
        var end = start.IndexOfAny([' ', '\t', '\r', '\n']);

        // The program, then something to import: a URL whose path ends in /curl is a URL.
        return end > 0 && IsCurlProgram(start[..end]) && start[end..].Trim().Length > 0;
    }

    /// <summary>
    /// Drops a line continuation (<c>\</c>, <c>^</c> or <c>`</c> after a space) that ends the text, as left behind when only
    /// the first lines of a multi-line command were copied; it would otherwise become a stray argument.
    /// </summary>
    private static string TrimDanglingContinuation(string text)
    {
        while (text.Length >= 2 && text[^1] is '\\' or '^' or '`' && char.IsWhiteSpace(text[^2]))
        {
            text = text[..^1].TrimEnd();
        }

        return text;
    }

    /// <summary>Imports a curl command. Throws <see cref="FormatException"/> when it is not one or has no URL.</summary>
    public static CurlImportResult Import(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Terminals and clipboards deliver line breaks as CR, LF or CRLF. Comment lines above the command (the notes TermRest's
        // export writes) come before a prompt to strip, as LooksLikeCurl has them.
        var text = TrimDanglingContinuation(StripPrompt(SkipCommentLines(command.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')).Trim()));
        var warnings = new List<string>();
        var dialect = ShellWords.Detect(text);
        var args = ShellWords.Split(text, dialect, warnings);
        if (args.Count == 0)
        {
            throw new FormatException("Paste a curl command, for example: curl https://api.example.com/items");
        }

        var first = 0;
        if (IsCurlProgram(args[0]))
        {
            first = 1;
        }
        else if (!args[0].StartsWith('-') && !LooksLikeUrl(args[0]))
        {
            throw new FormatException($"This is not a curl command: it starts with '{Shorten(args[0])}'.");
        }

        var parsed = new ParsedCommand();
        for (var i = first; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var name = arg;
                string? inline = null;
                var equals = arg.IndexOf('=', StringComparison.Ordinal);
                if (!ValueOptions.Contains(name) && equals > 2 && ValueOptions.Contains(NormalizeLong(arg[..equals])))
                {
                    name = arg[..equals];
                    inline = arg[(equals + 1)..];
                }

                name = NormalizeLong(name, warnings);
                if (ValueOptions.Contains(name))
                {
                    var value = inline ?? (i + 1 < args.Count ? args[++i] : null);
                    if (value is null)
                    {
                        warnings.Add($"{name} has no value and was ignored.");
                        continue;
                    }

                    HandleOption(parsed, name, value, warnings);
                }
                else
                {
                    HandleFlag(parsed, name, warnings);
                }
            }
            else if (arg.Length > 1 && arg[0] == '-')
            {
                // Short options can be combined (-sSL) and take their value attached (-XPOST) or as the next word.
                for (var j = 1; j < arg.Length; j++)
                {
                    if (!ShortOptions.TryGetValue(arg[j], out var name))
                    {
                        warnings.Add($"Unknown option -{arg[j]} was ignored.");
                        continue;
                    }

                    if (ValueOptions.Contains(name))
                    {
                        var value = j + 1 < arg.Length ? arg[(j + 1)..] : i + 1 < args.Count ? args[++i] : null;
                        if (value is null)
                        {
                            warnings.Add($"-{arg[j]} has no value and was ignored.");
                        }
                        else
                        {
                            HandleOption(parsed, name, value, warnings);
                        }

                        break;
                    }

                    HandleFlag(parsed, name, warnings);
                }
            }
            else
            {
                parsed.Urls.Add(arg);
            }
        }

        return Build(parsed, dialect, warnings);
    }

    // ----- options -----------------------------------------------------------------------------

    private static string NormalizeLong(string name) => NormalizeLong(name, null);

    private static string NormalizeLong(string name, List<string>? warnings)
    {
        // curl 8.3+: --expand-<option> takes the same argument as <option>, with {{variables}} expanded by curl.
        if (name.StartsWith("--expand-", StringComparison.Ordinal))
        {
            warnings?.Add($"{name}: curl variables are not expanded; the value was imported as written.");
            return "--" + name["--expand-".Length..];
        }

        return name;
    }

    private static void HandleOption(ParsedCommand p, string name, string value, List<string> warnings)
    {
        switch (name)
        {
            case "--url":
                p.Urls.Add(value);
                break;
            case "--request":
                p.Method = value;
                break;
            case "--header":
                p.Headers.Add(value);
                break;
            case "--data" or "--data-ascii":
                p.Data.Add((DataKind.Ascii, value));
                break;
            case "--data-raw":
                p.Data.Add((DataKind.Raw, value));
                break;
            case "--data-binary":
                p.Data.Add((DataKind.Binary, value));
                break;
            case "--data-urlencode":
                p.Data.Add((DataKind.UrlEncode, value));
                break;
            case "--json":
                p.Data.Add((DataKind.Json, value));
                break;
            case "--form":
                p.Forms.Add((value, false));
                break;
            case "--form-string":
                p.Forms.Add((value, true));
                break;
            case "--url-query":
                p.UrlQueries.Add(value);
                break;
            case "--user":
                p.User = value;
                break;
            case "--user-agent":
                p.Headers.Add("User-Agent: " + value);
                break;
            case "--referer":
                p.Headers.Add("Referer: " + (value.EndsWith(";auto", StringComparison.Ordinal) ? value[..^5] : value));
                break;
            case "--cookie" when value.Contains('=', StringComparison.Ordinal):
                p.Headers.Add("Cookie: " + value);
                break;
            case "--cookie":
                warnings.Add($"Cookies read from the file '{value}' (-b) were not imported.");
                break;
            case "--range":
                p.Headers.Add("Range: bytes=" + value);
                break;
            case "--oauth2-bearer":
                p.Bearer = value;
                break;
            case "--cert":
                p.Cert = value;
                break;
            case "--cert-type":
                p.CertType = value;
                break;
            case "--key":
                p.Key = value;
                break;
            case "--pass":
                p.Pass = value;
                break;
            case "--upload-file":
                p.UploadFile = value;
                break;
            case "--max-redirs" when int.TryParse(value, out var max) && max < 0:
                // curl's -1: no limit, which TermRest has not. The request then keeps the workspace's limit.
                p.MaxRedirects = null;
                warnings.Add($"{name} {value}: curl follows any number of redirects; TermRest follows up to the workspace's maximum (Tools > Settings).");
                break;
            case "--max-redirs" when int.TryParse(value, out var max):
                p.MaxRedirects = max;
                break;
            case "--key-type":
                break;
            case "--cacert" or "--capath":
                warnings.Add($"{name} {value}: trusted CAs are a workspace setting in TermRest (Tools > Settings > CA bundle).");
                break;
            case "--proxy" or "--proxy-user" or "--noproxy" or "--preproxy" or "--socks4" or "--socks4a" or "--socks5" or "--socks5-hostname":
                warnings.Add($"{name}: proxies are a workspace setting in TermRest (Tools > Settings).");
                break;
            case "--max-time" or "--connect-timeout":
                warnings.Add($"{name} {value}: the timeout is a workspace setting in TermRest (Tools > Settings).");
                break;
            case "--config":
                warnings.Add($"Options in the config file '{value}' (-K) were not read.");
                break;
            default:
                if (!QuietlyIgnored.Contains(name))
                {
                    warnings.Add($"{name} is not supported and was ignored.");
                }

                break;
        }
    }

    private static void HandleFlag(ParsedCommand p, string name, List<string> warnings)
    {
        switch (name)
        {
            case "--get":
                p.Get = true;
                break;
            case "--head":
                p.Head = true;
                break;
            case "--insecure":
                p.Insecure = true;
                break;
            case "--digest":
                p.Auth = ChallengeAuth.Digest;
                break;
            case "--ntlm" or "--ntlm-wb":
                p.Auth = ChallengeAuth.Ntlm;
                break;
            case "--anyauth":
                p.Auth = ChallengeAuth.Any;
                break;
            case "--negotiate":
                p.Auth = ChallengeAuth.Negotiate;
                break;
            case "--http1.0" or "--http1.1" or "--http2" or "--http2-prior-knowledge" or "--http3" or "--http3-only":
                warnings.Add($"{name}: the HTTP version is a workspace setting in TermRest (Tools > Settings).");
                break;
            default:
                if (!QuietlyIgnored.Contains(name))
                {
                    warnings.Add($"{name} is not supported and was ignored.");
                }

                break;
        }
    }

    // ----- building the request ----------------------------------------------------------------

    private static CurlImportResult Build(ParsedCommand p, ShellDialect dialect, List<string> warnings)
    {
        if (p.Urls.Count == 0)
        {
            throw new FormatException("The curl command has no URL.");
        }

        var url = p.Urls.FirstOrDefault(LooksLikeUrl) ?? p.Urls[0];
        if (p.Urls.Count > 1)
        {
            warnings.Add($"Only the URL {url} was imported; curl was given {p.Urls.Count} URLs.");
        }

        // The file of -T is the body only without -F and -d; curl names it in the URL before adding --url-query.
        if (p is { UploadFile: { } uploaded and not ("-" or "."), Forms.Count: 0, Data.Count: 0 })
        {
            url = UploadUrl(url, uploaded);
        }

        foreach (var query in p.UrlQueries)
        {
            if (EncodeUrlencodePart(query, warnings) is { } encoded)
            {
                url = AppendQuery(url, encoded);
            }
        }

        var spec = new HttpRequestSpec();
        foreach (var header in p.Headers)
        {
            AddHeader(spec, header, warnings);
        }

        var hasBody = false;
        if (p.Forms.Count > 0)
        {
            spec.Body = new RequestBody { Mode = BodyMode.FormData, FormData = BuildForm(p.Forms, warnings) };
            hasBody = true;
            if (p.Data.Count > 0)
            {
                warnings.Add("curl cannot combine -F with -d; the -d data was ignored.");
            }
        }
        else if (p.Data.Count > 0 && p.Get)
        {
            url = AppendQuery(url, JoinData(p.Data, warnings));
        }
        else if (p.Data.Count > 0)
        {
            // -H 'Content-Type:' drops curl's default type (TermRest's own cURL export writes it for a body-less POST).
            var noDefaultType = p.Headers.Any(h => h.Split(':', 2) is [var name, var value] && name.Trim().Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && value.Trim().Length == 0);
            BuildDataBody(spec, p.Data, warnings, noDefaultType);
            hasBody = true;
        }
        else if (p.UploadFile is { } upload)
        {
            if (upload is "-" or ".")
            {
                warnings.Add("Uploading from standard input (-T -) cannot be imported.");
            }
            else
            {
                spec.Body = new RequestBody { Mode = BodyMode.File, FileSrc = upload };
            }

            hasBody = true;
        }

        var method = p.Method ?? (p.Head ? "HEAD" : p.UploadFile is not null && p.Forms.Count == 0 && p.Data.Count == 0 ? "PUT" : hasBody ? "POST" : "GET");
        spec.Method = method.Trim().ToUpperInvariant();
        spec.Url = url;
        spec.QueryParams = PostmanUrl.SyncQueryFromRaw(url, []);
        spec.PathVariables = PostmanUrl.SyncPathVariables(url, []);
        spec.Auth = BuildAuth(p, warnings);
        spec.Certificate = BuildCertificate(p, warnings);
        if (p.Insecure)
        {
            spec.Settings.StrictSsl = false;
        }

        if (p.MaxRedirects is { } maxRedirects)
        {
            spec.Settings.MaxRedirects = maxRedirects;
        }

        var result = new CurlImportResult { Spec = spec, SuggestedName = SuggestName(url), Dialect = dialect };
        result.Warnings.AddRange(warnings.Distinct(StringComparer.Ordinal));
        return result;
    }

    private static void AddHeader(HttpRequestSpec spec, string header, List<string> warnings)
    {
        if (header.StartsWith('@'))
        {
            warnings.Add($"Headers read from the file '{header[1..]}' were not imported.");
            return;
        }

        var colon = header.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            var value = header[(colon + 1)..].Trim();
            if (value.Length > 0)
            {
                spec.Headers.Add(new KeyValueEntry { Key = header[..colon].Trim(), Value = value });
            }

            // "Name:" only tells curl to drop one of its own default headers.
            return;
        }

        var semicolon = header.IndexOf(';', StringComparison.Ordinal);
        if (semicolon > 0 && header[(semicolon + 1)..].Trim().Length == 0)
        {
            spec.Headers.Add(new KeyValueEntry { Key = header[..semicolon].Trim(), Value = "" });
            return;
        }

        warnings.Add($"The header '{header}' has no ':' and was ignored.");
    }

    /// <param name="noDefaultType">The command drops curl's default Content-Type: none is added.</param>
    private static void BuildDataBody(HttpRequestSpec spec, List<(DataKind Kind, string Value)> data, List<string> warnings, bool noDefaultType)
    {
        var contentType = spec.Headers.LastOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
        var mediaType = contentType?.Split(';')[0].Trim().ToLowerInvariant();

        if (data is [{ Kind: DataKind.Ascii or DataKind.Binary or DataKind.Json } single] && single.Value.StartsWith('@'))
        {
            var path = single.Value[1..];
            if (path == "-")
            {
                warnings.Add("Data read from standard input (@-) cannot be imported.");
                return;
            }

            spec.Body = new RequestBody { Mode = BodyMode.File, FileSrc = path };
            if (single.Kind == DataKind.Ascii)
            {
                warnings.Add($"curl -d @{path} strips line breaks from the file; TermRest sends it unchanged (like --data-binary).");
            }

            if (contentType is null && !noDefaultType)
            {
                spec.Headers.Add(new KeyValueEntry { Key = "Content-Type", Value = single.Kind == DataKind.Json ? "application/json" : "application/x-www-form-urlencoded" });
            }

            if (single.Kind == DataKind.Json && !spec.Headers.Any(h => h.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)))
            {
                spec.Headers.Add(new KeyValueEntry { Key = "Accept", Value = "application/json" });
            }

            return;
        }

        if (data.Any(d => d.Kind == DataKind.Json))
        {
            foreach (var (_, value) in data.Where(d => d.Kind is DataKind.Ascii or DataKind.Binary or DataKind.Json && d.Value.StartsWith('@')))
            {
                warnings.Add($"The data file '{value[1..]}' cannot be combined with other data; its name was kept as text.");
            }

            spec.Body = new RequestBody { Mode = BodyMode.Raw, RawLanguage = "json", Raw = string.Concat(data.Select(d => d.Value)) };
            if (!spec.Headers.Any(h => h.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)))
            {
                spec.Headers.Add(new KeyValueEntry { Key = "Accept", Value = "application/json" });
            }

            return;
        }

        if (mediaType is null or "application/x-www-form-urlencoded" && TryReadFields(data, out var fields))
        {
            spec.Body = new RequestBody { Mode = BodyMode.UrlEncoded, UrlEncoded = fields };
            return;
        }

        var text = JoinData(data, warnings);
        var language = mediaType switch
        {
            null => LooksLikeJson(text) ? "json" : "text",
            _ when mediaType.Contains("json", StringComparison.Ordinal) => "json",
            _ when mediaType.Contains("xml", StringComparison.Ordinal) => "xml",
            "text/html" => "html",
            _ when mediaType.Contains("javascript", StringComparison.Ordinal) => "javascript",
            _ => "text",
        };
        spec.Body = new RequestBody { Mode = BodyMode.Raw, Raw = text, RawLanguage = language };

        // Empty data is sent as no body (Content-Length: 0 for a POST), without a type: one added would be left out, with a
        // warning, at every send.
        if (contentType is null && !noDefaultType && text.Length > 0)
        {
            if (language == "json")
            {
                warnings.Add("No Content-Type was given, so curl would label this JSON as form data; TermRest sends it as application/json.");
            }
            else
            {
                spec.Headers.Add(new KeyValueEntry { Key = "Content-Type", Value = "application/x-www-form-urlencoded" });
            }
        }
    }

    /// <summary>Reads data that is entirely name=value pairs, with UTF-8 escapes, into urlencoded fields (values decoded, as Postman stores them).</summary>
    private static bool TryReadFields(List<(DataKind Kind, string Value)> data, out List<KeyValueEntry> fields)
    {
        fields = [];
        foreach (var (kind, value) in data)
        {
            if (kind == DataKind.UrlEncode)
            {
                var equals = value.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0)
                {
                    return false;
                }

                fields.Add(new KeyValueEntry { Key = value[..equals], Value = value[(equals + 1)..] });
                continue;
            }

            if (kind == DataKind.Json || (kind != DataKind.Raw && value.StartsWith('@')))
            {
                return false;
            }

            foreach (var pair in value.Split('&'))
            {
                if (pair.Length == 0)
                {
                    continue;
                }

                var equals = pair.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0 || pair.AsSpan().IndexOfAny(NotFormEncoded) >= 0)
                {
                    return false;
                }

                // An escape that isn't UTF-8 (%F6 for ö, from a page in windows-1252) decodes to U+FFFD, which would be sent
                // as %EF%BF%BD: such data is kept as typed.
                var key = WebUtility.UrlDecode(pair[..equals]);
                var decoded = WebUtility.UrlDecode(pair[(equals + 1)..]);
                if (key.AsSpan().Count('\uFFFD') + decoded.AsSpan().Count('\uFFFD') > pair.AsSpan().Count('\uFFFD'))
                {
                    return false;
                }

                fields.Add(new KeyValueEntry { Key = key, Value = decoded });
            }
        }

        return fields.Count > 0;
    }

    /// <summary>The body bytes curl would send for the data options: parts joined with '&amp;'.</summary>
    private static string JoinData(List<(DataKind Kind, string Value)> data, List<string> warnings)
    {
        var parts = new List<string>();
        foreach (var (kind, value) in data)
        {
            if (kind == DataKind.UrlEncode)
            {
                if (EncodeUrlencodePart(value, warnings) is { } encoded)
                {
                    parts.Add(encoded);
                }
            }
            else if (kind is DataKind.Ascii or DataKind.Binary && value.StartsWith('@'))
            {
                warnings.Add($"The data file '{value[1..]}' cannot be combined with other data; its name was kept as text.");
                parts.Add(value);
            }
            else
            {
                parts.Add(value);
            }
        }

        return string.Join('&', parts);
    }

    /// <summary>curl's --data-urlencode / --url-query forms: content, =content, name=content (file forms are not supported).</summary>
    private static string? EncodeUrlencodePart(string value, List<string> warnings)
    {
        var equals = value.IndexOf('=', StringComparison.Ordinal);
        if (equals >= 0)
        {
            var name = value[..equals];
            var content = Uri.EscapeDataString(value[(equals + 1)..]);
            return name.Length == 0 ? content : $"{name}={content}";
        }

        if (value.Contains('@', StringComparison.Ordinal))
        {
            warnings.Add($"--data-urlencode {value}: reading values from files is not supported; it was ignored.");
            return null;
        }

        return Uri.EscapeDataString(value);
    }

    private static List<KeyValueEntry> BuildForm(List<(string Value, bool Literal)> forms, List<string> warnings)
    {
        var entries = new List<KeyValueEntry>();
        foreach (var (value, literal) in forms)
        {
            var equals = value.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                warnings.Add($"The form field '{value}' is not name=value and was ignored.");
                continue;
            }

            var name = value[..equals];
            var content = value[(equals + 1)..];
            if (literal)
            {
                entries.Add(new KeyValueEntry { Key = name, Value = content, Type = "text" });
            }
            else if (content.StartsWith('@') || content.StartsWith('<'))
            {
                var (path, type, fileName) = ParseFormValue(content[1..]);
                entries.Add(new KeyValueEntry { Key = name, Value = null, Type = "file", Src = path, ContentType = type });
                if (content[0] == '<')
                {
                    warnings.Add($"Form field '{name}' takes its value from {path} (curl -F name=<file); it was imported as a file upload.");
                }

                if (fileName is not null)
                {
                    warnings.Add($"Form field '{name}': the custom file name '{fileName}' was not kept.");
                }
            }
            else
            {
                var (text, type, _) = ParseFormValue(content);
                entries.Add(new KeyValueEntry { Key = name, Value = text, Type = "text", ContentType = type });
            }
        }

        return entries;
    }

    /// <summary>Splits a -F value into its content and the ;type= / ;filename= parameters (values may be "quoted").</summary>
    private static (string Value, string? Type, string? FileName) ParseFormValue(string s)
    {
        string value;
        int position;
        if (s.StartsWith('"'))
        {
            var builder = new StringBuilder();
            position = 1;
            for (; position < s.Length; position++)
            {
                if (s[position] == '\\' && position + 1 < s.Length && s[position + 1] is '"' or '\\')
                {
                    builder.Append(s[++position]);
                }
                else if (s[position] == '"')
                {
                    position++;
                    break;
                }
                else
                {
                    builder.Append(s[position]);
                }
            }

            value = builder.ToString();
        }
        else
        {
            position = FindFormParameter(s, 0);
            value = position < 0 ? s : s[..position];
            position = position < 0 ? s.Length : position;
        }

        string? type = null;
        string? fileName = null;
        while (position < s.Length && s[position] == ';')
        {
            var afterType = type is not null || s.AsSpan(position + 1).TrimStart().StartsWith("type=", StringComparison.OrdinalIgnoreCase);
            var next = FindFormParameter(s, position + 1, afterType);
            var part = next < 0 ? s[(position + 1)..] : s[(position + 1)..next];
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            var key = (equals < 0 ? part : part[..equals]).Trim().ToLowerInvariant();

            // Unquoted only when quoted as a whole: a type keeps the quotes of its parameters (charset="utf-8").
            var parameter = equals < 0 ? "" : part[(equals + 1)..].Trim();
            if (parameter is ['"', .., '"'])
            {
                parameter = parameter[1..^1];
            }

            if (key == "type")
            {
                type = parameter;
            }
            else if (key == "filename")
            {
                fileName = parameter;
            }

            position = next < 0 ? s.Length : next;
        }

        return (value, type, fileName);
    }

    /// <param name="afterType">From within the type, where curl reads a type= as part of it (application/atom+xml; type=entry).</param>
    private static int FindFormParameter(string s, int from, bool afterType = false)
    {
        for (var i = s.IndexOf(';', from); i >= 0; i = s.IndexOf(';', i + 1))
        {
            var rest = s.AsSpan(i + 1).TrimStart();
            if ((!afterType && rest.StartsWith("type=", StringComparison.OrdinalIgnoreCase)) || rest.StartsWith("filename=", StringComparison.OrdinalIgnoreCase)
                || rest.StartsWith("headers=", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("encoder=", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static AuthSpec? BuildAuth(ParsedCommand p, List<string> warnings)
    {
        if (p.Bearer is { } token)
        {
            if (p.User is not null)
            {
                warnings.Add("Both -u and --oauth2-bearer were given; the bearer token was imported.");
            }

            var bearer = new AuthSpec { Type = "bearer" };
            bearer.Set("token", token);
            return bearer;
        }

        if (p.User is not { } user)
        {
            return null;
        }

        var colon = user.IndexOf(':', StringComparison.Ordinal);
        var name = colon < 0 ? user : user[..colon];
        var password = colon < 0 ? "" : user[(colon + 1)..];
        if (colon < 0)
        {
            warnings.Add("-u has no password (curl would prompt for it); enter it in the Auth tab.");
        }

        var backslash = name.IndexOf('\\', StringComparison.Ordinal);
        var type = p.Auth switch
        {
            ChallengeAuth.Digest => "digest",
            ChallengeAuth.Ntlm or ChallengeAuth.Negotiate => "ntlm",
            ChallengeAuth.Any => backslash > 0 ? "ntlm" : "digest",
            _ => "basic",
        };
        if (p.Auth == ChallengeAuth.Negotiate)
        {
            warnings.Add("--negotiate was imported as NTLM credentials; they are also offered when the server asks for Negotiate.");
        }

        var auth = new AuthSpec { Type = type };
        if (type == "ntlm" && backslash > 0)
        {
            auth.Set("username", name[(backslash + 1)..]);
            auth.Set("password", password);
            auth.Set("domain", name[..backslash]);
        }
        else
        {
            auth.Set("username", name);
            auth.Set("password", password);
        }

        return auth;
    }

    private static CertificateSpec? BuildCertificate(ParsedCommand p, List<string> warnings)
    {
        if (p.Cert is null)
        {
            if (p.Key is not null)
            {
                warnings.Add("--key without --cert was ignored.");
            }

            return null;
        }

        if (p.Cert.StartsWith("pkcs11:", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("PKCS#11 certificate URIs are not supported; register the certificate from the OS store instead (F8).");
            return null;
        }

        var (path, password) = SplitCertificatePassword(p.Cert);
        var pfx = string.Equals(p.CertType, "P12", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".p12", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase);
        if (p.CertType is { } certType && !certType.Equals("P12", StringComparison.OrdinalIgnoreCase) && !certType.Equals("PEM", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"--cert-type {certType} is not supported; the certificate was imported as {(pfx ? "PKCS#12" : "PEM")}.");
        }

        var passphrase = p.Pass ?? password;
        if (!string.IsNullOrEmpty(passphrase))
        {
            warnings.Add("The certificate passphrase is now stored in the request; consider replacing it with {{variable}} or env:NAME.");
        }

        return new CertificateSpec
        {
            Name = "Imported from cURL",
            CertSrc = pfx ? null : path,
            KeySrc = pfx ? null : p.Key,
            PfxSrc = pfx ? path : null,
            Passphrase = string.IsNullOrEmpty(passphrase) ? null : passphrase,
        };
    }

    /// <summary>curl's "certificate[:password]" (\: is a literal colon; a Windows drive letter is not a separator).</summary>
    private static (string Path, string? Password) SplitCertificatePassword(string value)
    {
        var path = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length && value[i + 1] == ':')
            {
                path.Append(':');
                i++;
            }
            else if (c == ':' && !(i == 1 && char.IsAsciiLetter(value[0]) && i + 1 < value.Length && value[i + 1] is '\\' or '/'))
            {
                return (path.ToString(), value[(i + 1)..]);
            }
            else
            {
                path.Append(c);
            }
        }

        return (path.ToString(), null);
    }

    // ----- helpers -----------------------------------------------------------------------------

    /// <summary>The text from its first line that isn't a <c>#</c> comment (white space before it left out).</summary>
    private static string SkipCommentLines(string text)
    {
        var start = text.TrimStart();
        while (start.StartsWith('#') && start.IndexOfAny(['\r', '\n']) is var lineEnd and > 0)
        {
            start = start[lineEnd..].TrimStart();
        }

        return start;
    }

    private static string StripPrompt(string text)
    {
        if (text.StartsWith("$ ", StringComparison.Ordinal) || text.StartsWith("> ", StringComparison.Ordinal))
        {
            return text[2..].TrimStart();
        }

        if (text.StartsWith("PS ", StringComparison.Ordinal) || text.StartsWith("PS>", StringComparison.Ordinal))
        {
            var prompt = text.IndexOf('>', StringComparison.Ordinal);
            if (prompt > 0 && !text[..prompt].Contains('\n', StringComparison.Ordinal))
            {
                return text[(prompt + 1)..].TrimStart();
            }
        }

        return text;
    }

    private static bool IsCurlProgram(string word)
    {
        if (word.Contains("://", StringComparison.Ordinal) || word.StartsWith("{{", StringComparison.Ordinal))
        {
            return false; // a URL (https://api.github.com/repos/curl/curl), not a path to the program
        }

        var name = word[(word.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.Equals("curl", StringComparison.OrdinalIgnoreCase) || name.Equals("curl.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeUrl(string word) =>
        word.Contains("://", StringComparison.Ordinal) || word.StartsWith("{{", StringComparison.Ordinal)
        || word.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
        || (!word.StartsWith('-') && word.Contains('.', StringComparison.Ordinal) && !word.Contains(' ', StringComparison.Ordinal));

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string AppendQuery(string url, string query)
    {
        if (query.Length == 0)
        {
            return url;
        }

        var hash = url.IndexOf('#', StringComparison.Ordinal);
        var fragment = hash >= 0 ? url[hash..] : "";
        var head = hash >= 0 ? url[..hash] : url;
        var separator = !head.Contains('?', StringComparison.Ordinal) ? "?" : head.EndsWith('?') || head.EndsWith('&') ? "" : "&";
        return head + separator + query + fragment;
    }

    /// <summary>
    /// The URL curl uploads <paramref name="file"/> to (-T): one whose path is empty or ends with '/' gets the file's name
    /// (after its last '/' or '\', percent-encoded), unless it has a query. Without a path, a {{variable}} may hold one:
    /// such a URL is kept as it is.
    /// </summary>
    private static string UploadUrl(string url, string file)
    {
        var name = file[(file.LastIndexOfAny(['/', '\\']) + 1)..];
        var hash = PostmanUrl.IndexOutsideBraces(url, '#');
        var head = hash < 0 ? url : url[..hash];
        var question = PostmanUrl.IndexOutsideBraces(head, '?');
        if (name.Length == 0 || (question >= 0 && question < head.Length - 1))
        {
            return url;
        }

        var path = question < 0 ? head : head[..question]; // curl drops an empty query
        var pathStart = PostmanUrl.PathStart(path);
        if (pathStart < 0 ? path.Contains("{{", StringComparison.Ordinal) : !path.EndsWith('/'))
        {
            return url;
        }

        return path + (pathStart < 0 ? "/" : "") + Uri.EscapeDataString(name) + (hash < 0 ? "" : url[hash..]);
    }

    /// <summary>The URL path without its leading slash (<c>v1/items</c>), so the request is addressable as <c>Folder/v1/items</c>.</summary>
    private static string SuggestName(string url)
    {
        var parsed = PostmanUrl.Parse(url);
        var path = string.Join('/', parsed.Path.Where(s => s.Length > 0));
        var name = path.Length > 0 ? path : string.Join('.', parsed.Host);
        if (name.Length == 0)
        {
            name = "Imported request";
        }

        return Cut(name, 60);
    }

    private static string Shorten(string text) => Cut(text, 40);

    /// <summary><paramref name="text"/> cut to <paramref name="length"/> characters, the last one "…"; a surrogate pair stays whole.</summary>
    private static string Cut(string text, int length)
    {
        if (text.Length <= length)
        {
            return text;
        }

        var keep = char.IsHighSurrogate(text[length - 2]) ? length - 2 : length - 1;
        return text[..keep] + "…";
    }
}
