using System.Globalization;
using System.Text;
using System.Text.Json;
using TermRest.Core.Http;
using TermRest.Core.Json;

namespace TermRest.Core.Auth;

/// <summary>Why an OAuth 2.0 token could not be had (a token request or sign-in failed); the message is the request's error.</summary>
public sealed class OAuth2Exception(string message) : Exception(message);

/// <summary>An OAuth 2.0 access token a token endpoint issued (RFC 6749, section 5.1).</summary>
public sealed class OAuth2Token
{
    public required string AccessToken { get; init; }

    public string? TokenType { get; init; }

    public string? RefreshToken { get; init; }

    public string? IdToken { get; init; }

    /// <summary>The scope granted, when the server says (it may grant less than asked for).</summary>
    public string? Scope { get; init; }

    public required DateTimeOffset ObtainedAt { get; init; }

    /// <summary>When it expires (<c>expires_in</c>); null when the server does not say: it is used until a new one is asked for.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Whether it can still be sent at <paramref name="now"/>: until it expires, less a margin (a minute, or a tenth of its
    /// lifetime for a short-lived one) so that it does not expire on its way.
    /// </summary>
    public bool IsFresh(DateTimeOffset now)
    {
        if (ExpiresAt is not { } expires)
        {
            return true;
        }

        var margin = TimeSpan.FromTicks(Math.Clamp((expires - ObtainedAt).Ticks / 10, 0, TimeSpan.TicksPerMinute));
        return now < expires - margin;
    }

    /// <summary>When it expires, said for <paramref name="now"/>: "expires in 59 min", "expired", "no expiry given".</summary>
    public string DescribeExpiry(DateTimeOffset now)
    {
        if (ExpiresAt is not { } expires)
        {
            return "no expiry given";
        }

        var left = expires - now;
        return left <= TimeSpan.Zero ? "expired"
            : left < TimeSpan.FromMinutes(1) ? $"expires in {left.TotalSeconds:0} s"
            : left < TimeSpan.FromHours(2) ? $"expires in {left.TotalMinutes:0} min"
            : left < TimeSpan.FromDays(2) ? $"expires in {left.TotalHours:0} h"
            : $"expires {expires.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    /// <summary>
    /// The token in a token endpoint's successful response (its <paramref name="fields"/>, see <see cref="OAuth2Response"/>);
    /// a refresh that returns no new refresh token keeps <paramref name="previousRefreshToken"/>. Null without an access token.
    /// </summary>
    internal static OAuth2Token? From(IReadOnlyDictionary<string, string> fields, DateTimeOffset now, string? previousRefreshToken)
    {
        if (fields.GetValueOrDefault("access_token") is not { Length: > 0 } accessToken)
        {
            return null;
        }

        // A number, or a number in a string (as some servers write it).
        var expiresIn = fields.GetValueOrDefault("expires_in") is { } text
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds / 2
            ? TimeSpan.FromSeconds(seconds)
            : (TimeSpan?)null;
        return new OAuth2Token
        {
            AccessToken = accessToken,
            TokenType = fields.GetValueOrDefault("token_type"),
            RefreshToken = fields.GetValueOrDefault("refresh_token") is { Length: > 0 } refreshToken ? refreshToken : previousRefreshToken,
            IdToken = fields.GetValueOrDefault("id_token"),
            Scope = fields.GetValueOrDefault("scope"),
            ObtainedAt = now,
            ExpiresAt = now + expiresIn,
        };
    }
}

/// <summary>Where a token a request got came from.</summary>
public enum OAuth2TokenOrigin
{
    /// <summary>Fetched earlier, and still fresh.</summary>
    Cached,

    /// <summary>Fetched now.</summary>
    Fetched,

    /// <summary>Refreshed now, with the refresh token of the one fetched earlier.</summary>
    Refreshed,
}

/// <summary>A token a request got, where it came from, and the token endpoint it came from.</summary>
/// <param name="Endpoint">The token endpoint's URL, without its query (see <see cref="OAuth2Tokens.Display"/>).</param>
/// <param name="Exchange">How the token request went (status and time): "200 in 85 ms"; null for a cached token.</param>
public sealed record OAuth2Result(OAuth2Token Token, OAuth2TokenOrigin Origin, string Endpoint, string? Exchange = null)
{
    /// <summary>What the request's auth was, for its timeline: the grant, and where its token came from (no secret).</summary>
    public string Describe(OAuth2Settings settings, DateTimeOffset now) => Origin switch
    {
        OAuth2TokenOrigin.Fetched => $"OAuth 2.0, {settings.GrantName}: new token from {Endpoint} ({Exchange}), {Token.DescribeExpiry(now)}",
        OAuth2TokenOrigin.Refreshed => $"OAuth 2.0, {settings.GrantName}: token refreshed at {Endpoint} ({Exchange}), {Token.DescribeExpiry(now)}",
        _ => $"OAuth 2.0, {settings.GrantName}: token from {Endpoint}, fetched {Token.ObtainedAt.ToLocalTime():HH:mm:ss}, {Token.DescribeExpiry(now)}",
    };
}

/// <summary>Reads what a token endpoint answers.</summary>
internal static class OAuth2Response
{
    /// <summary>
    /// The fields of a token endpoint's response: a JSON object's string and number members (RFC 6749, section 5.1), or a
    /// form (as GitHub answers a client that does not accept JSON). Null for anything else (an HTML error page).
    /// </summary>
    public static Dictionary<string, string>? ReadFields(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body).Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement is not { ValueKind: JsonValueKind.Object } root)
                {
                    return null;
                }

                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        fields[property.Name] = property.Value.GetString()!;
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        fields[property.Name] = property.Value.GetRawText();
                    }
                }

                return fields;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (text.Length == 0 || text.Contains('<', StringComparison.Ordinal) || !text.Contains('=', StringComparison.Ordinal) || text.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return ParseForm(text);
    }

    /// <summary>The fields of a form or query (<c>a=1&amp;b=x%20y</c>; a '+' is a space); a field named twice keeps its first value.</summary>
    public static Dictionary<string, string> ParseForm(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Decode(equals < 0 ? pair : pair[..equals]);
            fields.TryAdd(key, equals < 0 ? "" : Decode(pair[(equals + 1)..]));
        }

        return fields;

        static string Decode(string value)
        {
            try
            {
                return Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return value;
            }
        }
    }

    /// <summary>
    /// What a failed token request's response says: its <c>error</c> and <c>error_description</c> (RFC 6749, section 5.2),
    /// or the start of a text body; empty when it says nothing.
    /// </summary>
    public static string DescribeError(ResponseSnapshot response, IReadOnlyDictionary<string, string>? fields)
    {
        if (fields?.GetValueOrDefault("error") is { Length: > 0 } error)
        {
            return fields.GetValueOrDefault("error_description") is { Length: > 0 } description ? $"{error}: {description}" : error;
        }

        if (!BodyFormatter.LooksLikeText(response.Body, response.ContentType))
        {
            return "";
        }

        var text = string.Join(' ', Encoding.UTF8.GetString(response.Body).Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length <= 300 ? text : text[..300] + " …";
    }
}
