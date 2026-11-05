using TermRest.Core.Http;
using TermRest.Core.Import;
using TermRest.Core.Model;
using TermRest.Core.Variables;
using Xunit;

namespace TermRest.Core.Tests;

public class CurlImporterTests
{
    private static string Header(HttpRequestSpec spec, string name) =>
        spec.Headers.Single(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value!;

    [Fact]
    public void Imports_a_multi_line_bash_command()
    {
        var result = CurlImporter.Import("""
            curl -X POST 'https://api.example.com/v1/items?page=2&sort=name' \
              -H 'Content-Type: application/json' \
              -H "Authorization: Bearer abc.def" \
              --data-raw '{"name":"widget","tags":["a","b"]}'
            """);

        var spec = result.Spec;
        Assert.Equal(ShellDialect.Posix, result.Dialect);
        Assert.Equal("POST", spec.Method);
        Assert.Equal("https://api.example.com/v1/items?page=2&sort=name", spec.Url);
        Assert.Equal(["page=2", "sort=name"], spec.QueryParams.Select(q => $"{q.Key}={q.Value}"));
        Assert.Equal("application/json", Header(spec, "Content-Type"));
        Assert.Equal("Bearer abc.def", Header(spec, "Authorization"));
        Assert.Equal(BodyMode.Raw, spec.Body.Mode);
        Assert.Equal("json", spec.Body.RawLanguage);
        Assert.Equal("{\"name\":\"widget\",\"tags\":[\"a\",\"b\"]}", spec.Body.Raw);
        Assert.Equal("v1/items", result.SuggestedName);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Imports_chrome_copy_as_curl_bash_with_ansi_c_quoting()
    {
        var result = CurlImporter.Import("""
            curl 'https://example.com/api/notes' \
              -H 'accept: */*' \
              -H 'content-type: application/json' \
              -b 'session=abc; theme=dark' \
              --data-raw $'{"text":"it\'s \\"quoted\\"\\nnext","emoji":"\u2713"}' \
              --compressed
            """);

        var spec = result.Spec;
        Assert.Equal("POST", spec.Method);
        Assert.Equal("{\"text\":\"it's \\\"quoted\\\"\\nnext\",\"emoji\":\"✓\"}", spec.Body.Raw);
        Assert.Equal("session=abc; theme=dark", Header(spec, "Cookie"));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Imports_chrome_copy_as_curl_cmd()
    {
        var result = CurlImporter.Import("""
            curl ^"https://example.com/api/items?q=a^%^20b^" ^
              -H ^"accept: application/json^" ^
              -H ^"content-type: application/json^" ^
              --data-raw ^"^{^\^"name^\^":^\^"x y^\^",^\^"path^\^":^\^"C:^\^\^\^\temp^\^",^\^"note^\^":^\^"a^\^\nb^\^"^}^"
            """);

        var spec = result.Spec;
        Assert.Equal(ShellDialect.Cmd, result.Dialect);
        Assert.Equal("https://example.com/api/items?q=a%20b", spec.Url);
        Assert.Equal("application/json", Header(spec, "accept"));
        Assert.Equal("{\"name\":\"x y\",\"path\":\"C:\\\\temp\",\"note\":\"a\\nb\"}", spec.Body.Raw);
    }

    [Fact]
    public void Hand_written_cmd_commands_keep_windows_paths()
    {
        var spec = CurlImporter.Import("curl.exe --cert C:\\certs\\client.pem --key C:\\certs\\client.key \"https://h/x\" ^\n  -H \"A: b\"").Spec;

        Assert.Equal(("C:\\certs\\client.pem", "C:\\certs\\client.key"), (spec.Certificate!.CertSrc, spec.Certificate.KeySrc));
        Assert.Equal(("https://h/x", "b"), (spec.Url, Header(spec, "A")));
    }

    [Fact]
    public void Imports_powershell_with_backtick_continuations()
    {
        var result = CurlImporter.Import(""""
            curl.exe -X PATCH "https://api.example.com/users/42" `
              -H "Content-Type: application/json" `
              -H "X-Note: say ""hi""" `
              -d '{"name":"Ada","quote":"it''s"}'
            """");

        var spec = result.Spec;
        Assert.Equal(ShellDialect.PowerShell, result.Dialect);
        Assert.Equal("PATCH", spec.Method);
        Assert.Equal("say \"hi\"", Header(spec, "X-Note"));
        Assert.Equal("{\"name\":\"Ada\",\"quote\":\"it's\"}", spec.Body.Raw);
    }

    [Fact]
    public void Form_data_becomes_urlencoded_fields_with_decoded_values()
    {
        var spec = CurlImporter.Import("curl -d 'a=1&b=hello%20world+x' -d c=3 --data-urlencode 'q=x&y z' https://h/form").Spec;

        Assert.Equal("POST", spec.Method);
        Assert.Equal(BodyMode.UrlEncoded, spec.Body.Mode);
        Assert.Equal(["a=1", "b=hello world x", "c=3", "q=x&y z"], spec.Body.UrlEncoded.Select(f => $"{f.Key}={f.Value}"));
        Assert.DoesNotContain(spec.Headers, h => h.Key == "Content-Type");
    }

    [Fact]
    public void Get_moves_data_into_the_query_string()
    {
        var spec = CurlImporter.Import("curl -G -d 'q=1' --data-urlencode 'name=a b' --url-query 'lang=en us' https://h/search").Spec;

        Assert.Equal("GET", spec.Method);
        Assert.Equal("https://h/search?lang=en%20us&q=1&name=a%20b", spec.Url);
        Assert.Equal(BodyMode.None, spec.Body.Mode);
    }

    [Fact]
    public void Multipart_form_fields_and_files()
    {
        var spec = CurlImporter.Import("""curl -F 'file=@/tmp/a b.png;type=image/png' -F name=x -F 'note="a;b"' --form-string 'raw=@literal' https://h/upload""").Spec;

        Assert.Equal(BodyMode.FormData, spec.Body.Mode);
        var file = spec.Body.FormData[0];
        Assert.True(file.IsFile);
        Assert.Equal("/tmp/a b.png", file.Src);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(["name=x", "note=a;b", "raw=@literal"], spec.Body.FormData.Skip(1).Select(f => $"{f.Key}={f.Value}"));
    }

    [Fact]
    public void Body_type_follows_the_content_type_and_curl_defaults()
    {
        var json = CurlImporter.Import("curl --json '{\"a\":1}' https://h/x");
        Assert.Equal(("POST", "json", "{\"a\":1}"), (json.Spec.Method, json.Spec.Body.RawLanguage, json.Spec.Body.Raw));
        Assert.Equal("application/json", Header(json.Spec, "Accept"));

        var bareJson = CurlImporter.Import("curl -d '{\"a\":1}' https://h/x");
        Assert.Equal("json", bareJson.Spec.Body.RawLanguage);
        Assert.Contains(bareJson.Warnings, w => w.Contains("application/json", StringComparison.Ordinal));

        var text = CurlImporter.Import("curl -d 'just text' https://h/x").Spec;
        Assert.Equal(("text", "just text"), (text.Body.RawLanguage, text.Body.Raw));
        Assert.Equal("application/x-www-form-urlencoded", Header(text, "Content-Type"));

        var xml = CurlImporter.Import("curl -H 'Content-Type: application/xml' -d '<a/>' https://h/x").Spec;
        Assert.Equal("xml", xml.Body.RawLanguage);

        var file = CurlImporter.Import("curl --data-binary @payload.bin -H 'Content-Type: application/octet-stream' https://h/x").Spec;
        Assert.Equal((BodyMode.File, "payload.bin"), (file.Body.Mode, file.Body.FileSrc));

        var upload = CurlImporter.Import("curl -T report.csv https://h/files/report.csv").Spec;
        Assert.Equal(("PUT", BodyMode.File, "report.csv"), (upload.Method, upload.Body.Mode, upload.Body.FileSrc));

        Assert.Equal("HEAD", CurlImporter.Import("curl -I https://h/x").Spec.Method);
    }

    [Theory]
    [InlineData("curl -u 'ada:s3cret:x' https://h", "basic", "ada", "s3cret:x", null)]
    [InlineData("curl --digest -u ada:pw https://h", "digest", "ada", "pw", null)]
    [InlineData("curl --ntlm -u 'CORP\\ada:pw' https://h", "ntlm", "ada", "pw", "CORP")]
    [InlineData("curl --anyauth -u 'CORP\\ada:pw' https://h", "ntlm", "ada", "pw", "CORP")]
    public void Credentials_become_auth_helpers(string command, string type, string user, string password, string? domain)
    {
        var auth = CurlImporter.Import(command).Spec.Auth!;

        Assert.Equal(type, auth.Type);
        Assert.Equal(user, auth.Get("username"));
        Assert.Equal(password, auth.Get("password"));
        Assert.Equal(domain, auth.Get("domain"));
    }

    [Fact]
    public void Bearer_token_option_becomes_bearer_auth()
    {
        var auth = CurlImporter.Import("curl --oauth2-bearer tok123 https://h").Spec.Auth!;

        Assert.Equal(("bearer", "tok123"), (auth.Type, auth.Get("token")));
    }

    [Fact]
    public void Client_certificates_and_tls_verification_are_imported()
    {
        var pem = CurlImporter.Import("curl --cert certs/client.pem --key certs/client.key --pass 'p w' -k https://mtls.example.com");
        var certificate = pem.Spec.Certificate!;
        Assert.Equal(("certs/client.pem", "certs/client.key", "p w"), (certificate.CertSrc, certificate.KeySrc, certificate.Passphrase));
        Assert.Null(certificate.PfxSrc);
        Assert.False(pem.Spec.Settings.StrictSsl);
        Assert.Contains(pem.Warnings, w => w.Contains("passphrase", StringComparison.Ordinal));

        var pfx = CurlImporter.Import("curl --cert-type P12 --cert 'C:\\certs\\client.pfx:secret' https://mtls.example.com").Spec.Certificate!;
        Assert.Equal(("C:\\certs\\client.pfx", "secret"), (pfx.PfxSrc, pfx.Passphrase));
        Assert.Null(pfx.CertSrc);

        var escaped = CurlImporter.Import("curl -E 'odd\\:name.pem' https://h").Spec.Certificate!;
        Assert.Equal(("odd:name.pem", null), (escaped.CertSrc, escaped.Passphrase));
    }

    [Fact]
    public void Short_options_can_be_combined_and_take_attached_values()
    {
        var result = CurlImporter.Import("curl -sSLk -XPUT -H'X-A: 1' -H 'Referer: https://ref.example.com/' https://h/x");

        Assert.Equal("PUT", result.Spec.Method);
        Assert.Equal("https://h/x", result.Spec.Url);
        Assert.Equal("1", Header(result.Spec, "X-A"));
        Assert.False(result.Spec.Settings.StrictSsl);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Only_the_first_command_is_imported_and_unquoted_query_strings_survive()
    {
        var piped = CurlImporter.Import("$ curl https://h/api | jq .");
        Assert.Equal("https://h/api", piped.Spec.Url);
        Assert.Contains(piped.Warnings, w => w.Contains("jq", StringComparison.Ordinal));

        Assert.Equal("https://h/?a=1&b=2", CurlImporter.Import("curl https://h/?a=1&b=2").Spec.Url);
        Assert.Equal("{{baseUrl}}/users", CurlImporter.Import("curl {{baseUrl}}/users").Spec.Url);
    }

    [Fact]
    public void Unsupported_options_are_reported_and_proxies_point_to_settings()
    {
        var result = CurlImporter.Import("curl -x http://proxy:8080 --max-time 5 --cacert ca.pem --aws-sigv4 aws:amz https://h");

        Assert.Equal("https://h", result.Spec.Url);
        Assert.Contains(result.Warnings, w => w.StartsWith("--proxy", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.StartsWith("--max-time", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.StartsWith("--cacert", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.StartsWith("--aws-sigv4", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("wget https://h")]
    [InlineData("curl -X POST -H 'A: b'")]
    [InlineData("   ")]
    public void Rejects_what_is_not_a_curl_command_with_a_url(string text)
    {
        Assert.Throws<FormatException>(() => CurlImporter.Import(text));
    }

    [Theory]
    [InlineData("curl https://h", true)]
    [InlineData("  $ curl -s https://h", true)]
    [InlineData("/usr/bin/curl https://h", true)]
    [InlineData("curl.exe https://h", true)]
    [InlineData("https://h", false)]
    [InlineData("curling", false)]
    public void Detects_curl_commands(string text, bool expected)
    {
        Assert.Equal(expected, CurlImporter.LooksLikeCurl(text));
    }

    [Fact]
    public void Exported_commands_import_to_the_same_request()
    {
        var original = new HttpRequestSpec
        {
            Method = "POST",
            Url = "https://api.example.com/v1/items?page=2&q=a%20b",
            Headers = [new() { Key = "X-Trace", Value = "it's 1" }, new() { Key = "Accept", Value = "application/json" }],
            Body = new RequestBody { Mode = BodyMode.Raw, RawLanguage = "json", Raw = "{\"name\":\"it's\",\"n\":\"a\\nb\"}" },
            Settings = new RequestSettings { StrictSsl = false },
        };
        var preparer = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults());
        var before = preparer.Prepare(original, null, AppContext.BaseDirectory);

        var imported = CurlImporter.Import(CurlExporter.Export(before));
        var after = preparer.Prepare(imported.Spec, imported.Spec.Auth, AppContext.BaseDirectory);

        // The exporter spells out the body's content type as a header; everything that goes on the wire is the same.
        static IEnumerable<KeyValuePair<string, string>> WithoutContentType(PreparedRequest r) =>
            r.Headers.Where(h => !h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).OrderBy(h => h.Key);
        Assert.Empty(imported.Warnings);
        Assert.Equal(before.Method, after.Method);
        Assert.Equal(before.Url, after.Url);
        Assert.Equal(WithoutContentType(before), WithoutContentType(after));
        Assert.Equal(before.Body.Text, after.Body.Text);
        Assert.Equal(before.Body.ContentType, after.Body.ContentType);
        Assert.Equal(before.StrictSsl, after.StrictSsl);
    }

    [Fact]
    public void Exported_form_requests_import_to_the_same_fields()
    {
        var original = new HttpRequestSpec
        {
            Method = "PUT",
            Url = "https://h/form",
            Body = new RequestBody { Mode = BodyMode.UrlEncoded, UrlEncoded = [new() { Key = "a", Value = "1 & 2" }, new() { Key = "b", Value = "x=y" }] },
            Auth = new AuthSpec { Type = "basic" },
        };
        original.Auth.Set("username", "ada");
        original.Auth.Set("password", "pw");
        var preparer = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults());
        var before = preparer.Prepare(original, original.Auth, AppContext.BaseDirectory);

        var imported = CurlImporter.Import(CurlExporter.Export(before)).Spec;
        var after = preparer.Prepare(imported, imported.Auth, AppContext.BaseDirectory);

        Assert.Equal("PUT", after.Method);
        Assert.Equal(["a=1 & 2", "b=x=y"], imported.Body.UrlEncoded.Select(f => $"{f.Key}={f.Value}"));
        Assert.Equal(before.GetHeader("Authorization"), after.GetHeader("Authorization"));
        Assert.Equal(before.Body.Describe(), after.Body.Describe());
    }

    [Theory]
    [InlineData("curl -X POST 'https://api.example.com/items?x=1' \\")]
    [InlineData("curl -X POST \"https://api.example.com/items?x=1\" ^\r\n")]
    [InlineData("curl.exe -X POST 'https://api.example.com/items?x=1' `")]
    public void A_dangling_line_continuation_from_a_partial_copy_is_ignored(string command)
    {
        var result = CurlImporter.Import(command);

        Assert.Equal(("POST", "https://api.example.com/items?x=1"), (result.Spec.Method, result.Spec.Url));
        Assert.Empty(result.Warnings);
    }
}
