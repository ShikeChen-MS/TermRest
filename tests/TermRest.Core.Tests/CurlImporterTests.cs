using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TermRest.Core.Certificates;
using TermRest.Core.Http;
using TermRest.Core.Import;
using TermRest.Core.Model;
using TermRest.Core.Tests.Infrastructure;
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
    public void Long_names_and_texts_are_cut_without_splitting_a_character()
    {
        var name = CurlImporter.Import($"curl 'https://api.example.com/{new string('a', 58)}😀tail'").SuggestedName;
        Assert.Equal(new string('a', 58) + "…", name);

        var error = Assert.Throws<FormatException>(() => CurlImporter.Import(new string('b', 38) + "😀more and more words"));
        Assert.Contains($"'{new string('b', 38)}…'", error.Message, StringComparison.Ordinal);
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
    public void Form_data_with_escapes_that_are_not_utf8_is_sent_as_typed()
    {
        // Copy as cURL on a page in windows-1252: decoded, %F6 would become U+FFFD, sent as %EF%BF%BD.
        var preparer = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults());
        var latin1 = CurlImporter.Import("curl -d 'city=K%F6ln&zip=50667' https://h/form").Spec;
        var utf8 = CurlImporter.Import("curl -d 'city=K%C3%B6ln' https://h/form").Spec;

        Assert.Equal((BodyMode.Raw, "city=K%F6ln&zip=50667"), (latin1.Body.Mode, latin1.Body.Raw));
        Assert.Equal("application/x-www-form-urlencoded", Header(latin1, "Content-Type"));
        Assert.Equal("city=K%F6ln&zip=50667", preparer.Prepare(latin1, null, "").Body.Describe());
        Assert.Equal(["city=Köln"], utf8.Body.UrlEncoded.Select(f => $"{f.Key}={f.Value}"));
        Assert.Equal("city=K%C3%B6ln", preparer.Prepare(utf8, null, "").Body.Describe());
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
    [InlineData("curl -T report.json https://uploads.example.com/inbox/", "https://uploads.example.com/inbox/report.json", "")]
    [InlineData("curl -T report.json http://h", "http://h/report.json", "")]
    [InlineData("curl -T 'sub/my report.json' https://uploads.example.com/inbox/", "https://uploads.example.com/inbox/my%20report.json", "")]
    [InlineData("curl -T 'C:\\data\\a&b.json' 'https://h/in/?#top'", "https://h/in/a%26b.json#top", "")]
    [InlineData("curl -T report.json '{{baseUrl}}/inbox/' --url-query a=1", "{{baseUrl}}/inbox/report.json?a=1", "a=1")]
    [InlineData("curl -T report.json 'https://uploads.example.com/inbox/?x=1'", "https://uploads.example.com/inbox/?x=1", "x=1")]
    [InlineData("curl -T report.json https://h/inbox", "https://h/inbox", "")]
    [InlineData("curl -T report.json '{{baseUrl}}'", "{{baseUrl}}", "")]
    [InlineData("curl -T - https://h/inbox/", "https://h/inbox/", "")]
    public void An_upload_to_a_folder_goes_to_the_file_s_name_as_curl_sends_it(string command, string url, string query)
    {
        var spec = CurlImporter.Import(command).Spec;

        // curl adds the file's name to a URL whose path is empty or ends with '/' and that has no query.
        Assert.Equal(("PUT", url, query), (spec.Method, spec.Url, string.Join('&', spec.QueryParams.Select(q => $"{q.Key}={q.Value}"))));
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

        // Notes above a command at a prompt, as a terminal session or a README has them: detected, and imported.
        const string Noted = "# list the users\n$ curl https://h/users";
        Assert.True(CurlImporter.LooksLikeCurl(Noted));
        Assert.Equal("https://h/users", CurlImporter.Import(Noted).Spec.Url);
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
    [InlineData("https://api.github.com/repos/curl/curl", false)] // URLs, sent as they are
    [InlineData("{{baseUrl}}/convert/curl", false)]
    [InlineData("https://downloads.example.com/curl.exe", false)]
    [InlineData("curl", false)] // nothing to import
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

    [Theory]
    [InlineData("```\nit's\n```")]
    [InlineData("{\"op\":\"^\"}")]
    [InlineData("a^\nb")]
    public void Exported_commands_are_read_as_posix_whatever_their_values_hold(string raw)
    {
        var preparer = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults());
        var before = preparer.Prepare(new HttpRequestSpec { Method = "POST", Url = "https://h/x", Body = new RequestBody { Mode = BodyMode.Raw, Raw = raw } }, null, "");

        var imported = CurlImporter.Import(CurlExporter.Export(before));

        // A backtick or caret ending a line, or a ^", inside a quoted value says nothing about the shell.
        Assert.Equal(ShellDialect.Posix, imported.Dialect);
        Assert.Empty(imported.Warnings);
        Assert.Equal((before.Url, raw), (imported.Spec.Url, imported.Spec.Body.Raw));
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
    [InlineData("POST")]
    [InlineData("PUT")]
    public void A_request_without_a_body_comes_back_from_its_curl_command_without_a_content_type(string method)
    {
        // The export sends Content-Length: 0 as TermRest does, with --data-raw '' -H 'Content-Type:': the import drops curl's
        // default type with it, rather than adding a typed one TermRest would then leave out with a warning.
        var preparer = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults());
        var curl = CurlExporter.Export(preparer.Prepare(new HttpRequestSpec { Method = method, Url = "https://h/jobs/42/cancel" }, null, ""), multiline: false);
        Assert.Contains("--data-raw '' -H 'Content-Type:'", curl, StringComparison.Ordinal);

        var imported = CurlImporter.Import(curl).Spec;
        var after = preparer.Prepare(imported, imported.Auth, "");

        Assert.Equal(method, after.Method);
        Assert.DoesNotContain(imported.Headers, h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PreparedBodyKind.None, after.Body.Kind);
        Assert.Empty(after.Warnings);

        // Empty data alone (curl -d '', a common way to send an empty POST) gets no type either.
        var empty = CurlImporter.Import($"curl -X {method} -d '' https://h/jobs/42/cancel").Spec;
        Assert.DoesNotContain(empty.Headers, h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(preparer.Prepare(empty, empty.Auth, "").Warnings);
    }

    [Theory]
    [InlineData("digest", null, "--digest -u 'ada:pw'")]
    [InlineData("ntlm", "CORP", "--ntlm -u 'CORP\\ada:pw'")]
    public void Challenge_credentials_are_exported_for_their_own_scheme_only(string type, string? domain, string expected)
    {
        var auth = new AuthSpec { Type = type };
        auth.Set("username", "ada");
        auth.Set("password", "pw");
        if (domain is not null)
        {
            auth.Set("domain", domain);
        }

        var preparer = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults());
        var curl = CurlExporter.Export(preparer.Prepare(new HttpRequestSpec { Url = "https://h/x", Auth = auth }, auth, ""), multiline: false);

        Assert.Contains(expected, curl, StringComparison.Ordinal);
        var imported = CurlImporter.Import(curl).Spec.Auth!;
        Assert.Equal((type, "ada", "pw", domain), (imported.Type, imported.Get("username"), imported.Get("password"), imported.Get("domain")));
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

    [Fact]
    public void A_single_line_powershell_command_may_start_with_the_call_operator()
    {
        var result = CurlImporter.Import("& curl.exe https://h/x -H 'X-Note: it''s'");

        Assert.Equal(ShellDialect.PowerShell, result.Dialect);
        Assert.Equal(("https://h/x", "it's"), (result.Spec.Url, Header(result.Spec, "X-Note")));
    }

    [Fact]
    public void A_powershell_value_that_ends_in_a_backslash_ends_at_its_quote()
    {
        // In PowerShell's "…" only ` escapes: \" is a backslash and the closing quote, where the other shells read a quote.
        var result = CurlImporter.Import("""
            curl.exe --data-raw "C:\temp\" `
              https://h/upload
            """);

        Assert.Equal(ShellDialect.PowerShell, result.Dialect);
        Assert.Equal(("https://h/upload", "C:\\temp\\"), (result.Spec.Url, result.Spec.Body.Raw));
    }

    [Fact]
    public void Browser_escapes_of_emoji_keep_the_character()
    {
        // Firefox and Chrome escape text one UTF-16 unit at a time: 😀 is \ud83d\ude00. A lone half is still dropped.
        var spec = CurlImporter.Import("""curl 'https://h/x' -H $'X-Mood: \ud83d\ude00!' --data-raw $'{"e":"\ud83d\ude00","lone":"a\ud83db"}'""").Spec;

        Assert.Equal("😀!", Header(spec, "X-Mood"));
        Assert.Equal("{\"e\":\"😀\",\"lone\":\"ab\"}", spec.Body.Raw);
    }

    [Fact]
    public void Json_data_read_from_a_file_becomes_a_file_body()
    {
        var file = CurlImporter.Import("curl --json @payload.json https://h/x");
        Assert.Equal(("POST", BodyMode.File, "payload.json"), (file.Spec.Method, file.Spec.Body.Mode, file.Spec.Body.FileSrc));
        Assert.Equal(("application/json", "application/json"), (Header(file.Spec, "Content-Type"), Header(file.Spec, "Accept")));
        Assert.Empty(file.Warnings);

        var typed = CurlImporter.Import("curl --json @payload.json -H 'Content-Type: application/vnd.api+json' https://h/x").Spec;
        Assert.Equal("application/vnd.api+json", Header(typed, "Content-Type"));

        Assert.Contains(CurlImporter.Import("curl --json @- https://h/x").Warnings, w => w.Contains("standard input", StringComparison.Ordinal));
        Assert.Contains(CurlImporter.Import("curl --json @a.json --json '{}' https://h/x").Warnings, w => w.Contains("a.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Exported_commands_are_read_by_the_shell_and_curl_as_written()
    {
        var photo = Path.Combine(TestFiles.NewTempDirectory(), "photo.png");
        File.WriteAllBytes(photo, [1, 2, 3]);
        var preparer = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults());
        var prepared = preparer.Prepare(new HttpRequestSpec
        {
            Method = "post|sh",
            Url = "https://h/upload",
            Body = new RequestBody
            {
                Mode = BodyMode.FormData,
                FormData =
                [
                    new() { Key = "key", Value = "@/home/u/.ssh/id_rsa", Type = "text" },
                    new() { Key = "note", Value = "<notes.txt", Type = "text" },
                    new() { Key = "html", Value = "a;type=text/html", Type = "text" },
                    new() { Key = "photo", Type = "file", Src = photo },
                ],
            },
        }, null, AppContext.BaseDirectory);

        var curl = CurlExporter.Export(prepared);

        // A shell would run what follows an unquoted '|'; curl would read a file for a text value that starts with '@' or '<'.
        Assert.Contains("-X 'POST|SH'", curl, StringComparison.Ordinal);
        Assert.Contains("--form-string 'key=@/home/u/.ssh/id_rsa'", curl, StringComparison.Ordinal);
        var imported = CurlImporter.Import(curl).Spec;
        Assert.Equal("POST|SH", imported.Method);
        Assert.Equal(["key=@/home/u/.ssh/id_rsa", "note=<notes.txt", "html=a;type=text/html"], imported.Body.FormData.Where(f => !f.IsFile).Select(f => $"{f.Key}={f.Value}"));
        Assert.Equal(photo, Assert.Single(imported.Body.FormData, f => f.IsFile).Src);
        Assert.StartsWith("curl -X PUT 'https://h/x'", CurlExporter.Export(preparer.Prepare(new HttpRequestSpec { Method = "PUT", Url = "https://h/x" }, null, ""), multiline: false), StringComparison.Ordinal);

        // curl would make three requests of this one.
        var globbed = CurlExporter.Export(preparer.Prepare(new HttpRequestSpec { Url = "https://h/items?ids=[1-3]" }, null, ""), multiline: false);
        Assert.StartsWith("curl --globoff 'https://h/items?ids=[1-3]'", globbed, StringComparison.Ordinal);
        Assert.Equal(("https://h/items?ids=[1-3]", 0), (CurlImporter.Import(globbed).Spec.Url, CurlImporter.Import(globbed).Warnings.Count));
    }

    [Fact]
    public void The_note_about_an_os_store_certificate_is_a_line_of_its_own()
    {
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=Ops $(id) & co", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var prepared = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults()).Prepare(new HttpRequestSpec { Url = "https://h/x" }, null, "");
        prepared.ClientCertificate = new ClientCertificateSelection(
            new CertificateEntry { Name = "ops", Store = new StoreSource { Thumbprint = certificate.Thumbprint } },
            new LoadedCertificate { Certificate = certificate, Description = "store" },
            "registry");

        var lines = CurlExporter.Export(prepared).Split('\n');

        // A comment inside the command would swallow the line continuation (bash), or become curl arguments (zsh, where
        // '#' starts no comment at the prompt): the subject is quoted for that case too.
        Assert.Equal($"# client certificate from OS store: 'CN=Ops $(id) & co' ({certificate.Thumbprint})", lines[0]);
        Assert.Equal(["curl \\", "  'https://h/x' \\", "  --location --max-redirs 10 --proto-redir '=https'"], lines[1..]);
    }

    [Fact]
    public void Form_data_files_are_exported_so_that_curl_reads_only_them()
    {
        var directory = Path.Combine(TestFiles.NewTempDirectory(), "in;a,b");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, OperatingSystem.IsWindows() ? "c;d,e.txt" : "c;d,e \"f\" \\g.txt");
        File.WriteAllText(file, "x");
        var preparer = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults());
        HttpRequestSpec Upload(string name) => new()
        {
            Method = "POST",
            Url = "https://h/upload",
            Body = new RequestBody { Mode = BodyMode.FormData, FormData = [new() { Key = name, Type = "file", Src = file }] },
        };

        var curl = CurlExporter.Export(preparer.Prepare(Upload("doc"), null, ""), multiline: false);

        // Quoted for curl, which would take a ';' in the path for a parameter and a ',' for a list of files.
        Assert.Contains("-F 'doc=@\"", curl, StringComparison.Ordinal);
        Assert.Equal(file, Assert.Single(CurlImporter.Import(curl).Spec.Body.FormData).Src);

        // curl ends a field's name at the first '=': with this one, it would send its own environment instead of the file.
        var refused = Assert.Throws<NotSupportedException>(() => CurlExporter.Export(preparer.Prepare(Upload("doc=</proc/self/environ;"), null, "")));
        Assert.Contains("'doc=</proc/self/environ;'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_and_head_requests_with_a_body_keep_their_method()
    {
        var preparer = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults { FollowRedirects = false });
        string Export(string method, string? raw) => CurlExporter.Export(preparer.Prepare(new HttpRequestSpec
        {
            Method = method,
            Url = "https://h/_search",
            Body = raw is null ? new RequestBody() : new RequestBody { Mode = BodyMode.Raw, Raw = raw, RawLanguage = "json" },
        }, null, ""), multiline: false);

        // A body makes curl send POST, unless the method is named; an empty one is no body, as in Postman.
        Assert.Equal("curl 'https://h/_search'", Export("GET", null));
        Assert.Equal("curl 'https://h/_search'", Export("GET", ""));
        Assert.Equal("curl -X GET 'https://h/_search' --data-raw ' ' -H 'Content-Type: application/json'", Export("GET", " "));
        Assert.Equal("GET", CurlImporter.Import(Export("GET", "{\"query\":{}}")).Spec.Method);

        // --head takes no body, and after -X HEAD curl waits for a response body: a body can't be sent.
        Assert.Equal("curl --head 'https://h/_search'", Export("HEAD", ""));
        Assert.Contains("HEAD", Assert.Throws<NotSupportedException>(() => Export("HEAD", "{}")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_url_is_exported_as_it_is_sent()
    {
        var preparer = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults { FollowRedirects = false });
        var before = preparer.Prepare(new HttpRequestSpec { Url = "https://bücher.example/search", QueryParams = [new() { Key = "q", Value = "hello world" }] }, null, "");

        var curl = CurlExporter.Export(before, multiline: false);

        // curl refuses a URL with a space, and may not know how to look up a host name that is not ASCII.
        Assert.Equal("curl 'https://xn--bcher-kva.example/search?q=hello%20world'", curl);
        var after = preparer.Prepare(CurlImporter.Import(curl).Spec, null, "");
        Assert.Equal((before.Uri!.IdnHost, before.Uri.PathAndQuery), (after.Uri!.IdnHost, after.Uri.PathAndQuery));
    }

    [Fact]
    public void Redirects_are_not_followed_where_curl_would_send_credentials_to_other_hosts()
    {
        var apikey = new AuthSpec { Type = "apikey" };
        apikey.Set("key", "X-API-Key");
        apikey.Set("value", "sk-live");
        var bearer = new AuthSpec { Type = "bearer" };
        bearer.Set("token", "t0k");
        string Export(AuthSpec auth, bool follow = true, KeyValueEntry? header = null) => CurlExporter.Export(
            new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults { FollowRedirects = follow })
                .Prepare(new HttpRequestSpec { Url = "https://api.example.com/files/1", Headers = header is null ? [] : [header], Auth = auth }, auth, ""),
            multiline: false);

        // TermRest sends an API key (and Proxy-Authorization) to the request's own origin only; curl withholds only
        // Authorization and Cookie from the hosts it is redirected to.
        var curl = Export(apikey, header: new() { Key = "Proxy-Authorization", Value = "Basic cDpw" });

        Assert.Equal(
            "# redirects are not followed: curl would send 'Proxy-Authorization', 'X-API-Key' to every host it is redirected to\n"
            + "curl 'https://api.example.com/files/1' -H 'Proxy-Authorization: Basic cDpw' -H 'X-API-Key: sk-live'",
            curl);
        Assert.True(CurlImporter.LooksLikeCurl(curl));
        Assert.Equal("sk-live", Header(CurlImporter.Import(curl).Spec, "X-API-Key"));
        Assert.EndsWith(" -H 'Authorization: Bearer t0k' --location --max-redirs 10 --proto-redir '=https'", Export(bearer), StringComparison.Ordinal);
        Assert.Equal("curl 'https://api.example.com/files/1' -H 'X-API-Key: sk-live'", Export(apikey, follow: false));
    }

    [Fact]
    public void Redirects_change_the_method_and_keep_the_body_as_in_termrest()
    {
        string Export(string method, RequestBody body, bool follow = true) => CurlExporter.Export(
            new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults { FollowRedirects = follow })
                .Prepare(new HttpRequestSpec { Method = method, Url = "https://h/login", Body = body }, null, ""),
            multiline: false);
        var form = new RequestBody { Mode = BodyMode.UrlEncoded, UrlEncoded = [new() { Key = "user", Value = "ada" }] };
        var json = new RequestBody { Mode = BodyMode.Raw, RawLanguage = "json", Raw = "{}" };

        // The body alone makes curl send POST, and a GET without it after a 301, 302 or 303; after -X POST it would POST again.
        var post = Export("POST", form);
        Assert.Equal("curl 'https://h/login' --data-urlencode 'user=ada' --location --max-redirs 10 --proto-redir '=https'", post);
        Assert.Equal("POST", CurlImporter.Import(post).Spec.Method);

        // TermRest keeps other methods and their body after a 301 or 302, and a GET with its body also after a 303.
        Assert.Equal("curl -X PUT 'https://h/login' --data-urlencode 'user=ada' --location --max-redirs 10 --proto-redir '=https' --post301 --post302", Export("PUT", form));
        Assert.EndsWith(" --proto-redir '=https' --post301 --post302 --post303", Export("GET", json), StringComparison.Ordinal);
        Assert.EndsWith(" --data-raw '{}' -H 'Content-Type: application/json'", Export("PATCH", json, follow: false), StringComparison.Ordinal);

        // A request without a body goes the same way, with Content-Length: 0, which curl sends for empty data.
        Assert.Equal("curl 'https://h/login' --data-raw '' -H 'Content-Type:' --location --max-redirs 10 --proto-redir '=https'", Export("POST", new RequestBody()));
        Assert.Equal(
            "curl -X PUT 'https://h/login' --data-raw '' -H 'Content-Type:' --location --max-redirs 10 --proto-redir '=https' --post301 --post302",
            Export("PUT", new RequestBody()));
    }

    [Theory]
    [InlineData("https://h/x", true, 5, " --location --max-redirs 5 --proto-redir '=https'")]
    [InlineData("http://h/x", true, 5, " --location --max-redirs 5")]
    [InlineData("https://h/x", true, 0, "")]
    [InlineData("https://h/x", false, 5, "")]
    public void Redirects_are_followed_as_far_and_where_termrest_follows_them(string url, bool follow, int max, string expected)
    {
        var spec = new HttpRequestSpec { Url = url, Settings = new RequestSettings { FollowRedirects = follow, MaxRedirects = max } };

        var curl = CurlExporter.Export(new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults()).Prepare(spec, null, ""), multiline: false);

        // curl would follow up to 50 redirects, also from https to http; with a limit of 0, TermRest follows none.
        Assert.Equal($"curl '{url}'{expected}", curl);
        var imported = CurlImporter.Import(curl);
        Assert.Empty(imported.Warnings);
        Assert.Equal(expected.Length > 0 ? max : null, imported.Spec.Settings.MaxRedirects);
    }

    [Fact]
    public void Max_redirs_minus_one_keeps_the_workspace_limit()
    {
        var imported = CurlImporter.Import("curl -L --max-redirs -1 https://h/x");

        // curl's -1 is no limit, which TermRest has not; following none instead would also lose --location when exported.
        Assert.Null(imported.Spec.Settings.MaxRedirects);
        Assert.Equal("--max-redirs -1: curl follows any number of redirects; TermRest follows up to the workspace's maximum (Tools > Settings).",
            Assert.Single(imported.Warnings));
        var prepared = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults()).Prepare(imported.Spec, null, "");
        Assert.Equal("curl 'https://h/x' --location --max-redirs 10 --proto-redir '=https'", CurlExporter.Export(prepared, multiline: false));
    }

    [Fact]
    public void Headers_are_exported_as_termrest_sends_them()
    {
        var preparer = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults { FollowRedirects = false });
        string Export(RequestBody body) => CurlExporter.Export(preparer.Prepare(new HttpRequestSpec
        {
            Method = "POST",
            Url = "https://h/x",
            Headers =
            [
                new() { Key = "Content-Length", Value = "2" },
                new() { Key = "Content-Type", Value = "application/json" },
                new() { Key = "Transfer-Encoding", Value = "chunked" },
                new() { Key = "X-Empty", Value = "" },
            ],
            Body = body,
        }, null, ""), multiline: false);

        // TermRest computes Content-Length from the body, and sends no headers about a body it doesn't have.
        var raw = Export(new RequestBody { Mode = BodyMode.Raw, RawLanguage = "json", Raw = "{\"a\":1}" });
        Assert.Equal("curl 'https://h/x' -H 'Content-Type: application/json' -H 'Transfer-Encoding: chunked' -H 'X-Empty;' --data-raw '{\"a\":1}'", raw);
        Assert.Equal("curl 'https://h/x' -H 'X-Empty;' --data-raw '' -H 'Content-Type:'", Export(new RequestBody()));

        // curl would leave out 'X-Empty: ' instead of sending it empty.
        Assert.Equal("", Header(CurlImporter.Import(raw).Spec, "X-Empty"));
    }

    [Fact]
    public void Form_data_parts_are_exported_with_the_content_types_termrest_sends()
    {
        var photo = Path.Combine(TestFiles.NewTempDirectory(), "photo.png");
        File.WriteAllBytes(photo, [1, 2, 3]);
        var prepared = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults { FollowRedirects = false }).Prepare(new HttpRequestSpec
        {
            Method = "POST",
            Url = "https://h/upload",
            Body = new RequestBody
            {
                Mode = BodyMode.FormData,
                FormData =
                [
                    new() { Key = "meta", Value = "<\"a\" \\ b", Type = "text", ContentType = "application/json" },
                    new() { Key = "text", Value = "x", Type = "text", ContentType = "text/plain;charset=\"a;b\"" },
                    new() { Key = "note", Value = "y", Type = "text" },
                    new() { Key = "photo", Type = "file", Src = photo },
                    new() { Key = "entry", Type = "file", Src = photo, ContentType = "application/atom+xml;type=entry" },
                    new() { Key = "typed", Type = "file", Src = photo, ContentType = "png" },
                ],
            },
        }, null, "");

        var curl = CurlExporter.Export(prepared, multiline: false);

        // --form-string can't carry a type: typed text is "quoted" for -F, which then reads no file for a leading '<' or '@'.
        // A type goes as it is typed (curl keeps a ';' in a parameter's value), and a file without one is given the type
        // TermRest finds for its extension: curl knows fewer.
        var path = photo.Replace("\\", "\\\\", StringComparison.Ordinal);
        Assert.Contains("""-F 'meta="<\"a\" \\ b";type=application/json' -F 'text="x";type=text/plain;charset="a;b"' --form-string 'note=y' """, curl, StringComparison.Ordinal);
        Assert.Contains($"""-F 'photo=@"{path}";type=image/png' -F 'entry=@"{path}";type=application/atom+xml;type=entry' -F 'typed=@"{path}";type=png'""", curl, StringComparison.Ordinal);
        Assert.Equal(
            ["meta=<\"a\" \\ b (application/json)", "text=x (text/plain;charset=\"a;b\")", "note=y ()", $"photo={photo} (image/png)", $"entry={photo} (application/atom+xml;type=entry)", $"typed={photo} (png)"],
            CurlImporter.Import(curl).Spec.Body.FormData.Select(f => $"{f.Key}={(f.IsFile ? f.Src : f.Value)} ({f.ContentType})"));
    }

    [Theory]
    [InlineData("text", "text/plain; x=\"a;headers=@/etc/passwd;\"")]
    [InlineData("file", "text/plain; x=\"a,/etc/passwd\"")]
    [InlineData("file", "text/plain; filename=a.txt")]
    [InlineData("text", "not a type")]
    public void Form_data_content_types_that_curl_would_misread_are_refused(string type, string contentType)
    {
        var photo = Path.Combine(TestFiles.NewTempDirectory(), "photo.png");
        File.WriteAllBytes(photo, [1]);
        var field = new KeyValueEntry { Key = "part", Value = "v", Type = type, Src = photo, ContentType = contentType };
        var prepared = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults()).Prepare(new HttpRequestSpec
        {
            Method = "POST",
            Url = "https://h/upload",
            Body = new RequestBody { Mode = BodyMode.FormData, FormData = [field] },
        }, null, "");

        // curl reads a ';' as the start of a setting (headers=@file reads a file) and, after a file, a ',' as another file to
        // send; it ends a type at a space (TermRest sends all of it).
        var refused = Assert.Throws<NotSupportedException>(() => CurlExporter.Export(prepared));
        Assert.Contains("'part'", refused.Message, StringComparison.Ordinal);
    }
}
