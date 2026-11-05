using System.Text;
using TermRest.Core.Http;
using TermRest.Core.Recording;
using Xunit;

namespace TermRest.Core.Tests;

/// <summary>Redaction rules of recordings: names, values in their encodings, URLs, headers, cookies and bodies.</summary>
public class RedactionTests
{
    private static ExchangeRedactor For(params string[] secrets)
    {
        var request = new PreparedRequest();
        request.SecretValues.UnionWith(secrets);
        return new Redactor().For(request);
    }

    private static string RedactBody(ExchangeRedactor redactor, string body, BodyFormat format)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return Encoding.UTF8.GetString(ExchangeRedactor.Apply(bytes, redactor.BodySpans(bytes, format, "response.content")));
    }

    [Theory]
    [InlineData("Authorization", NameKind.Header)]
    [InlineData("Proxy-Authorization", NameKind.Header)]
    [InlineData("Cookie", NameKind.Header)]
    [InlineData("X-API-Key", NameKind.Header)]
    [InlineData("Ocp-Apim-Subscription-Key", NameKind.Header)]
    [InlineData("x-functions-key", NameKind.Header)]
    [InlineData("X-Amz-Security-Token", NameKind.Header)]
    [InlineData("X-CSRF-Token", NameKind.Header)]
    [InlineData("X-Hub-Signature-256", NameKind.Header)]
    [InlineData("Private-Token", NameKind.Header)]
    [InlineData("password", NameKind.Field)]
    [InlineData("newPassword", NameKind.Field)]
    [InlineData("client_secret", NameKind.Field)]
    [InlineData("access_token", NameKind.Field)]
    [InlineData("refreshToken", NameKind.Field)]
    [InlineData("apiKey", NameKind.Field)]
    [InlineData("private_key", NameKind.Field)]
    [InlineData("ConnectionString", NameKind.Field)]
    [InlineData("client_assertion", NameKind.Field)]
    [InlineData("code_verifier", NameKind.Field)]
    [InlineData("PHPSESSID", NameKind.Field)]
    [InlineData("cookies", NameKind.Field)]
    [InlineData("key", NameKind.QueryParameter)]
    [InlineData("sig", NameKind.QueryParameter)]
    [InlineData("X-Amz-Signature", NameKind.QueryParameter)]
    [InlineData("X-Amz-Credential", NameKind.QueryParameter)]
    public void Names_of_credentials_are_recognized(string name, NameKind kind) => Assert.True(SecretNames.IsSecret(name, kind), name);

    [Theory]
    [InlineData("token_type", NameKind.Field)]
    [InlineData("expires_in", NameKind.Field)]
    [InlineData("tokenUrl", NameKind.Field)]
    [InlineData("key", NameKind.Field)]
    [InlineData("sig", NameKind.Header)]
    [InlineData("Content-Type", NameKind.Header)]
    [InlineData("publicKey", NameKind.Field)]
    [InlineData("keyId", NameKind.Field)]
    [InlineData("code", NameKind.QueryParameter)]
    [InlineData("username", NameKind.Field)]
    [InlineData("WWW-Authenticate", NameKind.Header)]
    [InlineData("Idempotency-Key", NameKind.Header)]
    [InlineData("", NameKind.Field)]
    public void Other_names_are_kept(string name, NameKind kind) => Assert.False(SecretNames.IsSecret(name, kind), name);

    [Fact]
    public void Extra_names_from_the_settings_are_compared_without_case_and_separators()
    {
        var extra = SecretNames.NormalizeAll(["Tenant-Key", "ssn"]);

        Assert.True(SecretNames.IsSecret("x_tenant_key", NameKind.Field, extra));
        Assert.True(SecretNames.IsSecret("X-Tenant-Key", NameKind.Header, extra));
        Assert.True(SecretNames.IsSecret("SSN", NameKind.Header, extra));
        Assert.False(SecretNames.IsSecret("tenant", NameKind.Field, extra));
        Assert.False(SecretNames.IsSecret("tenant_key_id", NameKind.Field, extra));
    }

    [Fact]
    public void Json_bodies_keep_their_layout_and_lose_only_the_secret_values()
    {
        var redactor = For();
        var body = """
            {
              "user": "ada",
              "password": "hunter2-long",
              "note": "pw is hunter2-long",
              "profile": { "apiKey": "AK-123456789", "tags": ["a", "b"] },
              "credentials": [ { "id": 1, "value": "x\"y" }, true ],
              "token_type": "Bearer",
              "empty_password": "",
              "refresh_token": null,
              "count": 3
            }
            {"access_token": 12345678}
            """.ReplaceLineEndings("\n");

        var redacted = RedactBody(redactor, body, BodyFormat.Json);

        Assert.Equal("""
            {
              "user": "ada",
              "password": "[REDACTED]",
              "note": "pw is [REDACTED]",
              "profile": { "apiKey": "[REDACTED]", "tags": ["a", "b"] },
              "credentials": [ { "id": "[REDACTED]", "value": "[REDACTED]" }, "[REDACTED]" ],
              "token_type": "Bearer",
              "empty_password": "",
              "refresh_token": null,
              "count": 3
            }
            {"access_token": "[REDACTED]"}
            """.ReplaceLineEndings("\n"), redacted);
        Assert.Contains(redactor.Notes, n => n is { Name: "password", Reason: RedactionNote.NameReason, Count: 1 });
        Assert.Contains(redactor.Notes, n => n is { Name: "credentials", Count: 3 });
        Assert.Contains(redactor.Notes, n => n is { Name: null, Reason: RedactionNote.ValueReason, Count: 1 });
        Assert.Equal(7, redactor.Count);
    }

    [Fact]
    public void Broken_json_is_redacted_as_far_as_it_can_be_read()
    {
        var redacted = RedactBody(For("known-secret"), "{\"password\": \"abc\", \"x\": known-secret oops", BodyFormat.Json);

        Assert.Equal("{\"password\": \"[REDACTED]\", \"x\": [REDACTED] oops", redacted);
    }

    [Fact]
    public void Form_bodies_lose_the_values_of_secret_fields()
    {
        var redacted = RedactBody(For(), "grant_type=client_credentials&client_id=app&client_secret=S3cr3t%2BValue&scope=a+b", BodyFormat.Form);

        Assert.Equal("grant_type=client_credentials&client_id=app&client_secret=[REDACTED]&scope=a+b", redacted);
    }

    [Fact]
    public void Known_secrets_are_found_in_every_encoding()
    {
        var redactor = For("p+ss/w rd\"x");

        var redacted = redactor.Text(
            "raw p+ss/w rd\"x | uri p%2Bss%2Fw%20rd%22x | form p%2Bss%2Fw+rd%22x | json p\\u002Bss/w rd\\u0022x | minimal p+ss/w rd\\\"x | slash p+ss\\/w rd\"x",
            "response.content");

        Assert.Equal("raw [REDACTED] | uri [REDACTED] | form [REDACTED] | json [REDACTED] | minimal [REDACTED] | slash [REDACTED]", redacted);
    }

    [Fact]
    public void Short_values_are_not_searched_for()
    {
        var request = new PreparedRequest();
        request.SecretValues.Add("abc");
        request.SensitiveValues.Add("12345");

        Assert.Equal("abc 12345", new Redactor().For(request).Text("abc 12345", "x"));
    }

    [Fact]
    public void Urls_keep_their_shape()
    {
        var redactor = For("s3cr3t-path-key");

        var url = redactor.Url(
            "https://ada:pa55word@api.example.com/v1/s3cr3t-path-key/items?api_key=abc&page=2&sig=xyz%3D#access_token=eyJ0&token_type=Bearer",
            "request.url");

        Assert.Equal(
            "https://ada:[REDACTED]@api.example.com/v1/[REDACTED]/items?api_key=[REDACTED]&page=2&sig=[REDACTED]#access_token=[REDACTED]&token_type=Bearer",
            url);
        Assert.Equal("https://example.com/a?b=1#top", redactor.Url("https://example.com/a?b=1#top", "request.url"));
    }

    [Fact]
    public void Headers_keep_the_credential_scheme_and_cookie_names()
    {
        var redactor = For();

        Assert.Equal("Bearer [REDACTED]", redactor.Header("Authorization", "Bearer eyJhbGciOi.payload.sig", "request.headers"));
        Assert.Equal("Basic [REDACTED]", redactor.Header("authorization", "Basic YWRhOnNlY3JldA==", "request.headers"));
        Assert.Equal("[REDACTED]", redactor.Header("Authorization", "rawtokenwithoutscheme", "request.headers"));
        Assert.Equal("[REDACTED]", redactor.Header("X-API-Key", "k-123", "request.headers"));
        Assert.Equal("session=[REDACTED]; theme=[REDACTED]", redactor.Header("Cookie", "session=abc123; theme=dark", "request.headers"));
        Assert.Equal("sid=[REDACTED]; Path=/; HttpOnly; SameSite=Lax", redactor.Header("Set-Cookie", "sid=xyz789; Path=/; HttpOnly; SameSite=Lax", "response.headers"));
        Assert.Equal("https://app.example.com/cb?code=1&state=s#id_token=[REDACTED]", redactor.Header("Location", "https://app.example.com/cb?code=1&state=s#id_token=abc", "response.headers"));
        Assert.Equal("application/json", redactor.Header("Content-Type", "application/json", "request.headers"));

        // Values redacted by name are known afterwards: an echo of the token is redacted too.
        Assert.Equal("echo: [REDACTED]", redactor.Text("echo: eyJhbGciOi.payload.sig", "response.content"));
        Assert.Contains(redactor.Notes, n => n is { Location: "request.headers", Name: "Cookie", Reason: RedactionNote.CookieReason, Count: 2 });
        Assert.Contains(redactor.Notes, n => n is { Location: "request.headers", Name: "Authorization", Reason: RedactionNote.CredentialReason, Count: 2 });
    }

    [Fact]
    public void Values_learned_in_one_request_are_redacted_in_later_ones()
    {
        var recording = new Redactor();
        var first = recording.For(new PreparedRequest());
        RedactBody(first, "{\"access_token\": \"tok-ABCDEFGH-123\", \"expires_in\": 3600}", BodyFormat.Json);

        var second = recording.For(new PreparedRequest());

        Assert.Equal("https://api.example.com/x/[REDACTED]/items", second.Url("https://api.example.com/x/tok-ABCDEFGH-123/items", "request.url"));
    }

    [Theory]
    [InlineData("application/json", "{\"a\":1}", BodyFormat.Json)]
    [InlineData("application/problem+json; charset=utf-8", "{}", BodyFormat.Json)]
    [InlineData("application/x-www-form-urlencoded", "a=1", BodyFormat.Form)]
    [InlineData("text/html", "<p>hi</p>", BodyFormat.Text)]
    [InlineData("application/xml", "<a/>", BodyFormat.Text)]
    [InlineData(null, "[1,2]", BodyFormat.Json)]
    [InlineData(null, "plain words", BodyFormat.Text)]
    [InlineData("image/png", "PNG", BodyFormat.Binary)]
    [InlineData("text/plain", "a\0b", BodyFormat.Binary)]
    public void Body_formats_are_detected(string? contentType, string body, BodyFormat expected) =>
        Assert.Equal(expected, ExchangeRedactor.DetectFormat(contentType, Encoding.UTF8.GetBytes(body)));
}
