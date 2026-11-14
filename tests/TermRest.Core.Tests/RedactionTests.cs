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
    [InlineData("X-Auth-Key", NameKind.Header)]
    [InlineData("key", NameKind.QueryParameter)]
    [InlineData("sig", NameKind.QueryParameter)]
    [InlineData("auth", NameKind.QueryParameter)]
    [InlineData("X-Amz-Signature", NameKind.QueryParameter)]
    [InlineData("X-Amz-Credential", NameKind.QueryParameter)]
    public void Names_of_credentials_are_recognized(string name, NameKind kind) => Assert.True(SecretNames.IsSecret(name, kind), name);

    [Theory]
    [InlineData("token_type", NameKind.Field)]
    [InlineData("expires_in", NameKind.Field)]
    [InlineData("tokenUrl", NameKind.Field)]
    [InlineData("key", NameKind.Field)]
    [InlineData("sig", NameKind.Header)]
    [InlineData("auth", NameKind.Field)]
    [InlineData("Content-Type", NameKind.Header)]
    [InlineData("publicKey", NameKind.Field)]
    [InlineData("keyId", NameKind.Field)]
    [InlineData("code", NameKind.QueryParameter)]
    [InlineData("username", NameKind.Field)]
    [InlineData("WWW-Authenticate", NameKind.Header)]
    [InlineData("Idempotency-Key", NameKind.Header)]
    [InlineData("Access-Control-Allow-Credentials", NameKind.Header)]
    [InlineData("", NameKind.Field)]
    public void Other_names_are_kept(string name, NameKind kind) => Assert.False(SecretNames.IsSecret(name, kind), name);

    [Fact]
    public void Extra_names_from_the_settings_are_compared_without_case_and_separators()
    {
        var extra = SecretNames.NormalizeAll(["Tenant-Key", "ssn", "Access-Control-Allow-Credentials"]);

        Assert.True(SecretNames.IsSecret("x_tenant_key", NameKind.Field, extra));
        Assert.True(SecretNames.IsSecret("X-Tenant-Key", NameKind.Header, extra));
        Assert.True(SecretNames.IsSecret("SSN", NameKind.Header, extra));
        Assert.False(SecretNames.IsSecret("tenant", NameKind.Field, extra));
        Assert.False(SecretNames.IsSecret("tenant_key_id", NameKind.Field, extra));

        // Names added in the settings win over those known not to be credentials.
        Assert.True(SecretNames.IsSecret("Access-Control-Allow-Credentials", NameKind.Header, extra));
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
    public void Form_data_bodies_lose_the_values_of_parts_named_like_credentials_whatever_their_shape()
    {
        // Typed in the editor: line breaks without CR, padding after the delimiter, a name without quotes, a header in lower case.
        var redactor = For("known-secret");
        var typed = RedactBody(redactor, "--b \nContent-Disposition: form-data; name=client_secret\n\nS3cr3t-Value\n--b\ncontent-disposition: form-data; name=\"note\"\n\nknown-secret\n--b--\n", BodyFormat.Multipart);

        Assert.Equal("--b \nContent-Disposition: form-data; name=client_secret\n\n[REDACTED]\n--b\ncontent-disposition: form-data; name=\"note\"\n\n[REDACTED]\n--b--\n", typed);
        Assert.Contains(redactor.Notes, n => n is { Name: "client_secret", Reason: RedactionNote.NameReason, Count: 1 });

        // Cut off at the size limit inside a value, without the blank line, without a delimiter first: nothing known is lost.
        Assert.Equal("--b\r\nContent-Disposition: form-data; name=\"password\"\r\n\r\n[REDACTED]", RedactBody(redactor, "--b\r\nContent-Disposition: form-data; name=\"password\"\r\n\r\nhunter2-lo", BodyFormat.Multipart));
        Assert.Equal("--b\r\nContent-Disposition: form-data; name=\"password\"\r\n[REDACTED]", RedactBody(redactor, "--b\r\nContent-Disposition: form-data; name=\"password\"\r\nknown-secret", BodyFormat.Multipart));
        Assert.Equal("password=[REDACTED]", RedactBody(redactor, "password=known-secret", BodyFormat.Multipart));

        // With a binary file it stays binary (base64), the bytes of a secret part overwritten.
        byte[] binary = [.. "--b\r\nContent-Disposition: form-data; name=\"password\"\r\n\r\nhunter2-long\r\n--b\r\nContent-Disposition: form-data; name=\"photo\"; filename=\"a.png\"\r\n\r\n"u8, 0x89, 0x00, 0xFF, .. "\r\n--b--\r\n"u8];
        Assert.Equal(BodyFormat.Binary, ExchangeRedactor.DetectFormat("multipart/form-data; boundary=b", binary));
        var span = Assert.Single(For().BodySpans(binary, BodyFormat.Binary, "request.postData"));
        Assert.Equal("hunter2-long", Encoding.UTF8.GetString(binary.AsSpan(span.Start, span.End - span.Start)));
    }

    [Fact]
    public void Known_secrets_are_found_in_every_encoding()
    {
        var redactor = For("p+ss/w rd\"x");

        var redacted = redactor.Text(
            "raw p+ss/w rd\"x | uri p%2Bss%2Fw%20rd%22x | form p%2Bss%2Fw+rd%22x | url p+ss/w%20rd%22x | json p\\u002Bss/w rd\\u0022x | minimal p+ss/w rd\\\"x | slash p+ss\\/w rd\"x",
            "response.content");

        Assert.Equal("raw [REDACTED] | uri [REDACTED] | form [REDACTED] | url [REDACTED] | json [REDACTED] | minimal [REDACTED] | slash [REDACTED]", redacted);
    }

    [Fact]
    public void A_secret_is_looked_for_as_a_url_holds_it()
    {
        // Only what a URL can't hold is escaped there: neither URL encoding of a value gives that form.
        foreach (var c in Enumerable.Range(1, 0x7F).Where(i => i != '#').Select(i => ((char)i).ToString()).Concat(["é", "€", "😀"]))
        {
            var value = $"a{c}b%2F%zz";
            var url = new Uri("https://example.com/p?x=" + value).AbsoluteUri;
            Assert.Equal(url[(url.IndexOf("?x=", StringComparison.Ordinal) + 3)..], SecretPatterns.InUrl(value));
        }
    }

    [Fact]
    public void A_value_redacted_by_name_is_found_in_a_url_with_its_hash_encoded()
    {
        var redactor = For();
        RedactBody(redactor, "{\"password\":\"P@ss#w0rd!\"}", BodyFormat.Json);

        // TermRest sends a '#' in a path or query value as %23, and leaves '@' and '!' as they are.
        Assert.Equal("https://h/users/[REDACTED]?pin=[REDACTED]", redactor.Url("https://h/users/P@ss%23w0rd!?pin=P@ss%23w0rd!", "request.url"));
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

        // Firebase takes an ID token or a database secret as ?auth=.
        Assert.Equal("https://app.firebaseio.com/users.json?print=pretty&auth=[REDACTED]", redactor.Url("https://app.firebaseio.com/users.json?print=pretty&auth=eyJhbGciOiJSUzI1NiJ9.e30.c2ln", "request.url"));
    }

    [Fact]
    public void A_url_user_name_without_a_password_is_redacted_as_the_credential_it_is()
    {
        const string Key = "sk_test_" + "4eC39HqLyjWDarjtT1zdp7dc"; // Stripe's sample test key, split for secret scanning
        var redactor = For();

        // Stripe takes its secret key as the user name, with an empty password or none: it is sent as Basic auth.
        Assert.Equal("https://[REDACTED]:@api.stripe.com/v1/customers", redactor.Url($"https://{Key}:@api.stripe.com/v1/customers", "request.url"));
        Assert.Equal("https://[REDACTED]@api.example.com:8443/a@b", redactor.Url("https://abc@api.example.com:8443/a@b", "request.url"));
        foreach (var url in (string[])["https://:@h/", "https://@h/", "https://h:8443/x?y=a@b"])
        {
            Assert.Equal(url, redactor.Url(url, "request.url"));
        }

        Assert.Contains(redactor.Notes, n => n is { Location: "request.url", Reason: RedactionNote.UrlUserReason, Count: 2 });
        Assert.Equal("[REDACTED] abc", redactor.Text($"{Key} abc", "response.content")); // a short user name is not looked for
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
    public void The_user_name_and_password_of_a_basic_credential_are_redacted_wherever_they_appear_later()
    {
        const string Key = "Fr3shd3sk-ApiK3y-42";
        static string Encode(string credentials) => Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        var recording = new Redactor();
        var first = recording.For(new PreparedRequest());

        // Freshdesk takes its API key as the user name, with X as the password (typed headers, as Copy as cURL gives them).
        Assert.Equal("Basic [REDACTED]", first.Header("Authorization", "Basic " + Encode(Key + ":X"), "request.headers"));
        Assert.Equal("basic [REDACTED]", first.Header("Proxy-Authorization", "basic " + Encode("ada:hunter2-long"), "request.headers"));

        // Values shorter than 8 characters (X, ada) are not looked for.
        Assert.Equal("[REDACTED] X ada [REDACTED]", recording.For(new PreparedRequest()).Text($"{Key} X ada hunter2-long", "response.content"));
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

    [Fact]
    public void Every_value_learned_in_a_long_recording_is_still_redacted()
    {
        var recording = new Redactor();
        var tokens = Enumerable.Range(0, 300).Select(i => $"csrf-{i:D4}-{Guid.NewGuid():N}").ToList();
        foreach (var token in tokens)
        {
            recording.For(new PreparedRequest()).Header("X-CSRF-Token", token, "response.headers"); // a fresh one with every response
        }

        var later = recording.For(new PreparedRequest());

        Assert.Equal(string.Join(' ', tokens.Select(_ => Redactor.Placeholder)), later.Text(string.Join(' ', tokens), "request.headers"));
    }

    [Fact]
    public void Many_values_are_all_found_in_one_pass()
    {
        var random = new Random(5);
        var patterns = new SecretPatterns();
        var list = new List<byte[]>();
        for (var i = 0; i < 600; i++)
        {
            var tail = new byte[random.Next(4, 30)];
            random.NextBytes(tail);

            // Values that share their start (JWTs) or their end (base64 padding), and short ones (secrets of 4 to 7 bytes).
            byte[] pattern = (i % 4) switch
            {
                0 => [.. "eyJhbGciOiJIUzI1NiJ9."u8, .. tail],
                1 => [.. tail, .. "=="u8],
                2 => tail[..Math.Min(tail.Length, 7)],
                _ => tail,
            };
            list.Add(pattern);
            patterns.Add(pattern);
        }

        var data = new byte[300_000];
        random.NextBytes(data);
        foreach (var pattern in list.Concat(list.Take(100)))
        {
            pattern.CopyTo(data, random.Next(0, data.Length - pattern.Length)); // some overlap others
        }

        var found = new List<RedactedSpan>();
        patterns.Find(data, found);

        var covered = new bool[data.Length];
        foreach (var span in found)
        {
            Assert.Contains(list, p => p.AsSpan().SequenceEqual(data.AsSpan(span.Start, span.End - span.Start)));
            covered.AsSpan(span.Start, span.End - span.Start).Fill(true);
        }

        foreach (var pattern in list)
        {
            for (var at = 0; (at = IndexOf(pattern, at)) >= 0; at++)
            {
                Assert.True(covered.AsSpan(at, pattern.Length).IndexOf(false) < 0, $"{Convert.ToHexString(pattern)} at {at} was not found.");
            }
        }

        int IndexOf(byte[] pattern, int from) => data.AsSpan(from).IndexOf(pattern) is var index and >= 0 ? from + index : -1;
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
    [InlineData("text/plain", "{\"access_token\":\"x\"}", BodyFormat.Json)]
    [InlineData("text/html; charset=utf-8", " [1]", BodyFormat.Json)]
    [InlineData("multipart/form-data; boundary=b", "--b\r\n\r\nx\r\n--b--", BodyFormat.Multipart)]
    [InlineData("Multipart/Form-Data; boundary=b", "--b\r\n\r\n\0\r\n--b--", BodyFormat.Binary)]
    [InlineData("application/ubjson", "{i\u0003latd", BodyFormat.Binary)] // binary JSON: "json" is not a word of its subtype
    [InlineData("application/x-yaml", "a: 1", BodyFormat.Text)]
    public void Body_formats_are_detected(string? contentType, string body, BodyFormat expected) =>
        Assert.Equal(expected, ExchangeRedactor.DetectFormat(contentType, Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void Text_bodies_stay_text_where_they_are_not_utf8()
    {
        byte[] cut = [.. "{\"note\":\"caf"u8, 0xC3]; // cut at the size limit inside "é"
        byte[] latin1 = [.. "{\"name\":\"Caf"u8, 0xE9, .. "\"}"u8];

        Assert.Equal(BodyFormat.Json, ExchangeRedactor.DetectFormat("application/json", cut));
        Assert.Equal(BodyFormat.Json, ExchangeRedactor.DetectFormat(null, cut));
        Assert.Equal(BodyFormat.Json, ExchangeRedactor.DetectFormat("application/json; charset=utf-8", latin1));
        Assert.Equal(BodyFormat.Text, ExchangeRedactor.DetectFormat("text/plain", [.. "caf"u8, 0xE9]));
        Assert.Equal(BodyFormat.Binary, ExchangeRedactor.DetectFormat(null, latin1)); // without a content type, only UTF-8 is text
    }

    [Fact]
    public void Relative_urls_with_a_url_in_their_query_are_redacted()
    {
        var redactor = For();

        Assert.Equal("/reset?return=https://app.example.com/&reset_token=[REDACTED]",
            redactor.Header("Location", "/reset?return=https://app.example.com/&reset_token=8f3c1d2e9a7b", "response.headers"));
        Assert.Equal("login?next=a://b&password=[REDACTED]", redactor.Url("login?next=a://b&password=hunter2-long", "response.headers"));
    }

    [Fact]
    public void A_cookie_set_again_is_looked_for_with_its_latest_value_only()
    {
        var recording = new Redactor();
        var shop = new PreparedRequest { Uri = new Uri("https://shop.example.com/cart") };
        for (var i = 0; i < 50; i++)
        {
            recording.For(shop).Header("Set-Cookie", $"AWSALB=balancer-{i:D4}-value; Path=/", "response.headers");
        }

        recording.For(new PreparedRequest { Uri = new Uri("https://other.example.com/") }).Header("Cookie", "AWSALB=other-host-value", "request.headers");
        var later = recording.For(shop);

        // Load balancers set their cookie again with every response: earlier values are no longer looked for, so the
        // work for each request does not grow with the recording (cookie headers are always redacted).
        Assert.Equal("[REDACTED] [REDACTED] balancer-0048-value", later.Text("balancer-0049-value other-host-value balancer-0048-value", "response.content"));
    }

    [Fact]
    public void A_report_learns_from_an_exchange_what_its_recording_would()
    {
        const string Password = "hunter2-long";
        const string Session = "sid-0123456789";
        const string Token = "tok-0123456789abcdef";
        var login = new PreparedRequest { Method = "POST", Url = "https://api.example.com/login", Uri = new Uri("https://api.example.com/login") };
        login.Headers.Add(new("Content-Type", "application/json"));
        login.Body = new PreparedBody { Kind = PreparedBodyKind.Text, Text = $$"""{"user":"ada","password":"{{Password}}"}""" };
        var exchange = new HttpExchange(login)
        {
            Response = new ResponseSnapshot
            {
                StatusCode = 200,
                Headers = [new("Set-Cookie", $"session={Session}; Path=/; HttpOnly")],
                ContentType = "application/json",
                Body = Encoding.UTF8.GetBytes($$"""{"access_token":"{{Token}}","token_type":"Bearer"}"""),
            },
        };
        var report = new Redactor();

        var redact = report.ForReport(exchange);
        var next = report.For(new PreparedRequest { Uri = new Uri("https://api.example.com/me") });

        // Test errors quote values that are in no variable: a field of the request body, a cookie, a field of the response.
        Assert.Equal("expected '[REDACTED]' to equal 'x'", redact.Text($"expected '{Password}' to equal 'x'", "tests"));
        Assert.Equal("session [REDACTED]", redact.Text($"session {Session}", "tests"));
        Assert.Equal("expected '[REDACTED]' to have a length of 32", redact.Text($"expected '{Token}' to have a length of 32", "tests"));
        Assert.Equal("token [REDACTED]", next.Text($"token {Token}", "tests"));
    }

    [Fact]
    public void A_report_learns_the_values_of_form_data_parts_named_like_credentials_in_a_raw_body()
    {
        var login = new PreparedRequest { Method = "POST", Url = "https://api.example.com/login", Uri = new Uri("https://api.example.com/login") };
        login.Headers.Add(new("Content-Type", "multipart/form-data; boundary=----WebKitFormBoundaryX"));
        login.Body = new PreparedBody
        {
            Kind = PreparedBodyKind.Text,
            Text = "------WebKitFormBoundaryX\r\nContent-Disposition: form-data; name=\"password\"\r\n\r\nHunter2-Secret\r\n------WebKitFormBoundaryX--\r\n",
        };

        var redact = new Redactor().ForReport(new HttpExchange(login));

        Assert.Equal("expected '[REDACTED]' to equal 'x'", redact.Text("expected 'Hunter2-Secret' to equal 'x'", "tests"));
    }
}
