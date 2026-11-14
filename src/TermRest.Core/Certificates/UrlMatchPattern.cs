using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace TermRest.Core.Certificates;

/// <summary>
/// URL match pattern used to bind client certificates to hosts, following Postman/Chrome semantics:
/// <c>&lt;scheme&gt;://&lt;host&gt;[:&lt;port&gt;][/&lt;path&gt;]</c> where scheme may be <c>*</c>, host may be <c>*</c> or
/// start with <c>*.</c> (matches the domain and all subdomains), and path is a glob. <c>&lt;all_urls&gt;</c> matches anything.
/// A pattern without a port matches any port.
/// </summary>
public sealed class UrlMatchPattern
{
    private static readonly IdnMapping Idn = new();

    private readonly string? _scheme;
    private readonly string _host;
    private readonly int? _port;
    private readonly Regex? _path;

    private UrlMatchPattern(string pattern, string? scheme, string host, int? port, Regex? path, int specificity)
    {
        Pattern = pattern;
        _scheme = scheme;
        _host = host;
        _port = port;
        _path = path;
        Specificity = specificity;
    }

    public string Pattern { get; }

    /// <summary>Higher is more specific; used to pick the best certificate when several patterns match.</summary>
    public int Specificity { get; }

    public static bool TryParse(string pattern, out UrlMatchPattern? result)
    {
        result = null;
        var p = pattern.Trim();
        if (p.Length == 0)
        {
            return false;
        }

        if (p == "<all_urls>")
        {
            result = new UrlMatchPattern(p, null, "*", null, null, 0);
            return true;
        }

        string? scheme = null;
        var rest = p;
        var schemeEnd = p.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            scheme = p[..schemeEnd].ToLowerInvariant();
            rest = p[(schemeEnd + 3)..];
            if (scheme == "*")
            {
                scheme = null;
            }
        }

        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var authority = slash >= 0 ? rest[..slash] : rest;
        var pathGlob = slash >= 0 ? rest[slash..] : "/*";

        int? port = null;
        var colon = authority.LastIndexOf(':');
        if (colon >= 0 && !authority.EndsWith(']'))
        {
            var portText = authority[(colon + 1)..];
            authority = authority[..colon];
            if (portText != "*")
            {
                if (!int.TryParse(portText, out var parsedPort) || parsedPort is < 1 or > 65535)
                {
                    return false;
                }

                port = parsedPort;
            }
        }

        var host = authority.ToLowerInvariant();
        if (host.Length == 0 || (host.Contains('*', StringComparison.Ordinal) && host != "*" && !host.StartsWith("*.", StringComparison.Ordinal)))
        {
            return false;
        }

        // An IPv6 address is matched as Uri.IdnHost gives it: without brackets, in its shortest form ([0:0::1] is ::1).
        if (host.StartsWith('['))
        {
            if (!host.EndsWith(']') || !IPAddress.TryParse(host[1..^1], out var address) || address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            host = address.ToString();
        }

        // URLs are matched by their host in ASCII (Uri.IdnHost), so a host written in Unicode is matched in its ASCII form.
        if (!Ascii.IsValid(host))
        {
            var wildcard = host.StartsWith("*.", StringComparison.Ordinal) ? "*." : "";
            try
            {
                host = wildcard + Idn.GetAscii(host[wildcard.Length..]);
            }
            catch (ArgumentException)
            {
                return false; // no URL has such a host
            }
        }

        Regex? pathRegex = pathGlob is "/*" or "*" ? null
            : new Regex("^" + Regex.Escape(pathGlob).Replace(@"\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        var specificity = (host == "*" ? 0 : host.StartsWith("*.", StringComparison.Ordinal) ? 1000 + host.Length : 2000 + host.Length)
            + (scheme is null ? 0 : 50)
            + (port is null ? 0 : 100)
            + (pathRegex is null ? 0 : pathGlob.Replace("*", "", StringComparison.Ordinal).Length);
        result = new UrlMatchPattern(p, scheme, host, port, pathRegex, specificity);
        return true;
    }

    public bool IsMatch(Uri uri)
    {
        if (_scheme is not null && !string.Equals(_scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_port is { } port && port != uri.Port)
        {
            return false;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        if (_host != "*")
        {
            if (_host.StartsWith("*.", StringComparison.Ordinal))
            {
                var domain = _host[2..];
                if (host != domain && !host.EndsWith("." + domain, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else if (host != _host)
            {
                return false;
            }
        }

        return _path is null || _path.IsMatch(uri.AbsolutePath);
    }

    public override string ToString() => Pattern;
}
