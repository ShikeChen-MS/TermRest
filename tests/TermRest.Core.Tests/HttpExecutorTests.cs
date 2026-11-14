using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using TermRest.Core.Certificates;
using TermRest.Core.Http;
using TermRest.Core.Json;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Variables;
using TermRest.Core.Workspace;
using Xunit;

namespace TermRest.Core.Tests;

/// <summary>End-to-end tests against in-process Kestrel servers (HTTP, HTTPS and mutual TLS).</summary>
public sealed class HttpExecutorTests : IDisposable
{
    private readonly TestPki _pki = TestPki.Create();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static PreparedRequest Prepare(HttpRequestSpec spec, ExecutionDefaults? defaults = null, ClientCertificateProvider? certificates = null, AuthSpec? auth = null, VariableResolver? variables = null) =>
        new RequestPreparer(variables ?? VariableResolver.Empty, certificates, defaults ?? new ExecutionDefaults()).Prepare(spec, auth ?? spec.Auth, AppContext.BaseDirectory);

    private static JsonObject Json(HttpExchange exchange)
    {
        Assert.True(exchange.IsSuccess, exchange.ErrorMessage);
        return (JsonObject)PostmanJson.Parse(Encoding.UTF8.GetString(exchange.Response!.Body));
    }

    /// <summary>Applies a content coding; raw-deflate is deflate data without its zlib wrapper, as some servers send it.</summary>
    private static byte[] Encode(string coding, byte[] data)
    {
        using var output = new MemoryStream();
        using (Stream encoder = coding switch
        {
            "gzip" or "x-gzip" => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "deflate" => new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "raw-deflate" => new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "br" => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
            _ => throw new ArgumentOutOfRangeException(nameof(coding)),
        })
        {
            encoder.Write(data);
        }

        return output.ToArray();
    }

    [Fact]
    public async Task Sends_method_query_headers_and_json_body()
    {
        await using var server = await EchoServer.StartAsync();
        var spec = new HttpRequestSpec
        {
            Method = "POST",
            Url = server.Url("echo/items?a=1&b={{b}}"),
            Headers = [new() { Key = "X-Test", Value = "{{who}}" }, new() { Key = "X-Off", Value = "no", Enabled = false }],
            Body = new RequestBody { Mode = BodyMode.Raw, RawLanguage = "json", Raw = "{\"name\":\"{{who}}\"}" },
        };
        spec.QueryParams = PostmanUrl.SyncQueryFromRaw(spec.Url, []);
        spec.QueryParams.Add(new KeyValueEntry { Key = "disabled", Value = "x", Enabled = false });
        var variables = new VariableResolver([new DictionaryScope("t", new Dictionary<string, string> { ["b"] = "two", ["who"] = "ada" })]);

        var exchange = await new HttpExecutor().SendAsync(Prepare(spec, variables: variables), _ct);

        var echo = Json(exchange);
        Assert.Equal(200, exchange.Response!.StatusCode);
        Assert.Equal("POST", echo["method"].GetString());
        Assert.Equal("/echo/items", echo["path"].GetString());
        Assert.Equal("?a=1&b=two", echo["rawQuery"].GetString());
        Assert.Equal("ada", echo["headers"]!["x-test"].GetString());
        Assert.Null(echo["headers"]!["x-off"]);
        Assert.StartsWith("application/json", echo["headers"]!["content-type"].GetString(), StringComparison.Ordinal);
        Assert.Equal("{\"name\":\"ada\"}", echo["body"].GetString());
        Assert.StartsWith("TermRest/", echo["headers"]!["user-agent"].GetString(), StringComparison.Ordinal);
        Assert.NotNull(exchange.Timing.Connect);
        Assert.NotNull(exchange.Timing.Total);
    }

    [Fact]
    public async Task Sends_urlencoded_multipart_and_file_bodies()
    {
        await using var server = await EchoServer.StartAsync();
        var file = Path.Combine(_pki.Directory, "upload.txt");
        await File.WriteAllTextAsync(file, "file-content", _ct);
        var executor = new HttpExecutor();

        var urlencoded = Json(await executor.SendAsync(Prepare(new HttpRequestSpec
        {
            Method = "POST",
            Url = server.Url("echo"),
            Body = new RequestBody { Mode = BodyMode.UrlEncoded, UrlEncoded = [new() { Key = "role", Value = "admin & owner" }, new() { Key = "x", Value = "1", Enabled = false }] },
        }), _ct));
        Assert.Equal("role=admin+%26+owner", urlencoded["body"].GetString());

        var multipart = Json(await executor.SendAsync(Prepare(new HttpRequestSpec
        {
            Method = "POST",
            Url = server.Url("echo"),
            Body = new RequestBody { Mode = BodyMode.FormData, FormData = [new() { Key = "text", Value = "hello" }, new() { Key = "doc", Type = "file", Src = file }] },
        }), _ct));
        Assert.StartsWith("multipart/form-data", multipart["headers"]!["content-type"].GetString(), StringComparison.Ordinal);
        Assert.Contains("file-content", multipart["body"].GetString(), StringComparison.Ordinal);
        Assert.Contains("filename=\"upload.txt\"", multipart["body"].GetString(), StringComparison.Ordinal);

        var binary = Json(await executor.SendAsync(Prepare(new HttpRequestSpec
        {
            Method = "PUT",
            Url = server.Url("echo"),
            Body = new RequestBody { Mode = BodyMode.File, FileSrc = file },
        }), _ct));
        Assert.Equal("file-content", binary["body"].GetString());
        Assert.Equal("application/octet-stream", binary["headers"]!["content-type"].GetString());

        // A path pasted with spaces around it, which the Body tab finds, is sent too.
        var spaced = Json(await executor.SendAsync(Prepare(new HttpRequestSpec
        {
            Method = "PUT",
            Url = server.Url("echo"),
            Body = new RequestBody { Mode = BodyMode.File, FileSrc = $" {file} " },
        }), _ct));
        Assert.Equal("file-content", spaced["body"].GetString());
    }

    [Fact]
    public async Task Form_data_names_and_file_names_are_sent_in_utf8_as_browsers_send_them()
    {
        await using var server = await EchoServer.StartAsync();
        var file = Path.Combine(_pki.Directory, "résumé.txt");
        await File.WriteAllTextAsync(file, "cv", _ct);

        var echo = Json(await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec
        {
            Method = "POST",
            Url = server.Url("echo"),
            Body = new RequestBody
            {
                Mode = BodyMode.FormData,
                FormData = [new() { Key = "名前", Value = "x" }, new() { Key = "say \"hi\"\r\n", Value = "y" }, new() { Key = "cv", Type = "file", Src = file }],
            },
        }), _ct));

        // Form parsers don't decode RFC 2047 encoded-words, and RFC 7578 rules out filename*: only '"', CR and LF are escaped.
        var body = echo["body"].GetString();
        Assert.Contains("Content-Disposition: form-data; name=\"名前\"\r\n", body, StringComparison.Ordinal);
        Assert.Contains("Content-Disposition: form-data; name=\"say %22hi%22%0D%0A\"\r\n", body, StringComparison.Ordinal);
        Assert.Contains("Content-Disposition: form-data; name=\"cv\"; filename=\"résumé.txt\"\r\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Form_data_content_types_are_sent_as_typed()
    {
        await using var server = await EchoServer.StartAsync();
        var photo = Path.Combine(_pki.Directory, "cat.jpg");
        await File.WriteAllTextAsync(photo, "x", _ct);

        var exchange = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec
        {
            Method = "POST",
            Url = server.Url("echo"),
            Body = new RequestBody
            {
                Mode = BodyMode.FormData,
                FormData = [new() { Key = "photo", Type = "file", Src = photo, ContentType = "jpeg" }, new() { Key = "note", Value = "n", ContentType = "not a type" }],
            },
        }), _ct);

        // As curl and Postman send them (curl -F 'photo=@cat.jpg;type=jpeg'); .NET would refuse the first.
        var body = Json(exchange)["body"].GetString();
        Assert.Contains("filename=\"cat.jpg\"\r\nContent-Type: jpeg\r\n", body, StringComparison.Ordinal);
        Assert.Contains("name=\"note\"\r\nContent-Type: not a type\r\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_form_data_file_without_a_content_type_gets_the_type_of_its_extension()
    {
        await using var server = await EchoServer.StartAsync();
        string Write(string name)
        {
            var path = Path.Combine(_pki.Directory, name);
            File.WriteAllText(path, "x");
            return path;
        }

        var exchange = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec
        {
            Method = "POST",
            Url = server.Url("echo"),
            Body = new RequestBody
            {
                Mode = BodyMode.FormData,
                FormData =
                [
                    new() { Key = "photo", Type = "file", Src = Write("photo.PNG") },
                    new() { Key = "data", Type = "file", Src = Write("data.json") },
                    new() { Key = "blob", Type = "file", Src = Write("blob.bin") },
                ],
            },
        }), _ct);

        // As curl and Postman choose it: an upload endpoint may check it.
        var body = Json(exchange)["body"].GetString();
        Assert.Contains("filename=\"photo.PNG\"\r\nContent-Type: image/png\r\n", body, StringComparison.Ordinal);
        Assert.Contains("filename=\"data.json\"\r\nContent-Type: application/json\r\n", body, StringComparison.Ordinal);
        Assert.Contains("filename=\"blob.bin\"\r\nContent-Type: application/octet-stream\r\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_form_data_content_type_with_a_line_break_is_refused()
    {
        var prepared = Prepare(new HttpRequestSpec
        {
            Method = "POST",
            Url = "http://127.0.0.1:1/",
            Body = new RequestBody { Mode = BodyMode.FormData, FormData = [new() { Key = "note", Value = "n", ContentType = "text/plain\r\nX-Injected: yes" }] },
        });

        // Sent as typed, it would add a header to the part.
        Assert.Equal("The content type of form-data field 'note' contains a line break or NUL character.", Assert.Single(prepared.Errors));
    }

    [Fact]
    public async Task Files_opened_for_a_form_data_body_that_cannot_be_sent_are_closed()
    {
        var first = Path.Combine(_pki.Directory, "first.txt");
        var second = Path.Combine(_pki.Directory, "second.txt");
        await File.WriteAllTextAsync(first, "1", _ct);
        await File.WriteAllTextAsync(second, "2", _ct);
        var prepared = Prepare(new HttpRequestSpec
        {
            Method = "POST",
            Url = "http://127.0.0.1:1/",
            Body = new RequestBody { Mode = BodyMode.FormData, FormData = [new() { Key = "a", Type = "file", Src = first }, new() { Key = "b", Type = "file", Src = second }] },
        });
        File.Delete(second); // gone by the time the request is sent

        var exchange = await new HttpExecutor().SendAsync(prepared, _ct);

        Assert.Null(exchange.Response);
        Assert.Contains("second.txt", exchange.ErrorMessage, StringComparison.Ordinal);
        File.Delete(first); // Windows refuses while the file is still open
    }

    [Fact]
    public async Task A_file_another_program_has_open_for_writing_is_sent()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows refuses to open such a file for a reader that shares it for reading only; Unix does not.");
        }

        await using var server = await EchoServer.StartAsync();
        var file = Path.Combine(_pki.Directory, "report.csv");
        await File.WriteAllTextAsync(file, "a,b", _ct);
        var executor = new HttpExecutor();

        // As Excel, or a program writing a log, keeps it open.
        await using var writer = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var binary = await executor.SendAsync(Prepare(new HttpRequestSpec { Method = "PUT", Url = server.Url("echo"), Body = new RequestBody { Mode = BodyMode.File, FileSrc = file } }), _ct);
        var form = await executor.SendAsync(Prepare(new HttpRequestSpec
        {
            Method = "POST",
            Url = server.Url("echo"),
            Body = new RequestBody { Mode = BodyMode.FormData, FormData = [new() { Key = "report", Type = "file", Src = file }] },
        }), _ct);

        Assert.Equal("a,b", Json(binary)["body"].GetString());
        Assert.Contains("filename=\"report.csv\"\r\nContent-Type: application/octet-stream\r\n\r\na,b\r\n", Json(form)["body"].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Typed_body_files_are_checked_when_sent_and_get_their_content_type()
    {
        await using var server = await EchoServer.StartAsync();
        var file = Path.Combine(_pki.Directory, "order.json");
        await File.WriteAllTextAsync(file, "{\"item\": \"book\"}", _ct);
        var executor = new HttpExecutor();
        HttpRequestSpec Spec(params KeyValueEntry[] headers) => new()
        {
            Method = "POST",
            Url = server.Url("echo"),
            Headers = [.. headers],
            Body = new RequestBody { Mode = BodyMode.File, FileSrc = file, FileLanguage = "json" },
        };

        var sent = Json(await executor.SendAsync(Prepare(Spec()), _ct));
        Assert.Equal("{\"item\": \"book\"}", sent["body"].GetString());
        Assert.Equal("application/json", sent["headers"]!["content-type"].GetString());
        var patch = Json(await executor.SendAsync(Prepare(Spec(new KeyValueEntry { Key = "Content-Type", Value = "application/merge-patch+json" })), _ct));
        Assert.Equal("application/merge-patch+json", patch["headers"]!["content-type"].GetString());
        Assert.Contains("--data-binary '@" + file + "' \\\n  -H 'Content-Type: application/json'", CurlExporter.Export(Prepare(Spec())), StringComparison.Ordinal);

        // Edited into invalid JSON after it was chosen: it is checked again when sent, and not sent.
        await File.WriteAllTextAsync(file, "{\"item\": book}", _ct);
        var prepared = Prepare(Spec());
        var refused = await executor.SendAsync(prepared, _ct);
        Assert.Null(refused.Response);
        Assert.Null(refused.Timing.Connect);
        Assert.Equal("Not sent: order.json is not valid JSON: 'b' is an invalid start of a value (line 1, column 10).", refused.ErrorMessage);
        Assert.Single(prepared.Errors); // so history and recordings leave it out
    }

    [Fact]
    public async Task An_xml_body_file_in_a_legacy_encoding_is_checked_in_it_and_sent_as_it_is()
    {
        await using var server = await EchoServer.StartAsync();
        var file = Path.Combine(_pki.Directory, "order.xml");
        byte[] order = [.. "<?xml version=\"1.0\" encoding=\"windows-1252\"?>\n<order item=\"Caf"u8, 0xE9, .. "\" price=\"5 "u8, 0x80, .. "\"/>"u8];
        await File.WriteAllBytesAsync(file, order, _ct);

        // As legacy SOAP and EDI services send them: .NET knows the encoding only from its code pages.
        var exchange = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec
        {
            Method = "POST",
            Url = server.Url("mirror"),
            Body = new RequestBody { Mode = BodyMode.File, FileSrc = file, FileLanguage = "xml" },
        }), _ct);

        Assert.True(exchange.IsSuccess, exchange.ErrorMessage);
        Assert.Equal(order, exchange.Response!.Body);
        Assert.Equal("application/xml", exchange.Response.ContentType);
    }

    [Fact]
    public async Task A_file_that_grows_while_it_is_sent_is_sent_as_long_as_it_was()
    {
        var log = Path.Combine(_pki.Directory, "app.log");
        await File.WriteAllTextAsync(log, "started\n", _ct);

        // With Expect: 100-continue, the body goes when /echo reads it, after the headers: the log grows in between.
        await using var server = await EchoServer.StartAsync(beforeEcho: () => File.AppendAllText(log, "more\n"));
        var executor = new HttpExecutor();
        HttpRequestSpec Spec(string method, RequestBody body) => new()
        {
            Method = method,
            Url = server.Url("echo"),
            Headers = [new() { Key = "Expect", Value = "100-continue" }],
            Body = body,
        };

        var binary = await executor.SendAsync(Prepare(Spec("PUT", new RequestBody { Mode = BodyMode.File, FileSrc = log })), _ct);
        var form = await executor.SendAsync(Prepare(Spec("POST", new RequestBody { Mode = BodyMode.FormData, FormData = [new() { Key = "log", Type = "file", Src = log }] })), _ct);

        // As long as Content-Length said: .NET fails a request whose body is longer.
        Assert.Equal("started\n", Json(binary)["body"].GetString());
        Assert.Contains("filename=\"app.log\"\r\nContent-Type: application/octet-stream\r\n\r\nstarted\nmore\n\r\n--", Json(form)["body"].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_body_is_no_body_and_has_no_content_type_as_in_postman()
    {
        await using var server = await EchoServer.StartAsync();
        var executor = new HttpExecutor();
        var variables = new VariableResolver([new DictionaryScope("env", new Dictionary<string, string> { ["payload"] = "" })]);
        var typedJson = new KeyValueEntry { Key = "Content-Type", Value = "application/json" };
        (string Case, RequestBody Body, KeyValueEntry? Header, string? Warning)[] cases =
        [
            ("raw", new() { Mode = BodyMode.Raw, RawLanguage = "json", Raw = "" }, null, null),
            ("raw, empty once resolved", new() { Mode = BodyMode.Raw, RawLanguage = "json", Raw = "{{payload}}" }, null, null),
            ("raw, Content-Type typed", new() { Mode = BodyMode.Raw, RawLanguage = "json", Raw = "" }, typedJson, "'Content-Type' was not sent because the request has no body."),
            ("urlencoded", new() { Mode = BodyMode.UrlEncoded, UrlEncoded = [new() { Key = "a", Value = "1", Enabled = false }, new() { Key = "", Value = "x" }] }, null, null),
            ("form-data", new() { Mode = BodyMode.FormData, FormData = [new() { Key = "a", Value = "1", Enabled = false }] }, null, null),
            ("file", new() { Mode = BodyMode.File, FileLanguage = "json" }, null, "No file is chosen for the body: the request is sent without one."),
        ];

        foreach (var (name, body, header, warning) in cases)
        {
            var prepared = Prepare(new HttpRequestSpec { Method = "POST", Url = server.Url("echo"), Headers = header is null ? [] : [header], Body = body }, variables: variables);

            var echo = Json(await executor.SendAsync(prepared, _ct));

            // Some servers refuse an empty JSON body (Fastify answers 400).
            Assert.True(echo["headers"]!["content-type"] is null, name);
            Assert.Equal((name, "", warning), (name, echo["body"].GetString(), prepared.Warnings.SingleOrDefault()));
            Assert.Equal($"curl '{server.Url("echo")}' --data-raw '' -H 'Content-Type:' --location --max-redirs 10", CurlExporter.Export(prepared, multiline: false));
        }
    }

    [Fact]
    public async Task A_request_without_a_body_is_exported_with_the_content_length_it_is_sent_with()
    {
        await using var server = await EchoServer.StartAsync();
        var executor = new HttpExecutor();
        var url = server.Url("echo");
        (string Method, string Curl)[] cases =
        [
            ("POST", $"curl '{url}' --data-raw '' -H 'Content-Type:'"),
            ("PUT", $"curl -X PUT '{url}' --data-raw '' -H 'Content-Type:'"),
            ("PURGE", $"curl -X PURGE '{url}' --data-raw '' -H 'Content-Type:'"),
            ("DELETE", $"curl -X DELETE '{url}'"),
            ("OPTIONS", $"curl -X OPTIONS '{url}'"),
            ("GET", $"curl '{url}'"),
        ];

        foreach (var (method, curl) in cases)
        {
            var prepared = Prepare(new HttpRequestSpec { Method = method, Url = url, Settings = new RequestSettings { FollowRedirects = false } });

            var echo = Json(await executor.SendAsync(prepared, _ct));

            // curl sends Content-Length: 0 only with (empty) data; IIS answers a POST without one with 411 Length Required.
            Assert.Equal(curl, CurlExporter.Export(prepared, multiline: false));
            Assert.Equal((method, curl.Contains("--data-raw", StringComparison.Ordinal) ? "0" : null), (method, echo["headers"]!["content-length"]?.GetString()));
        }
    }

    [Fact]
    public async Task Applies_auth_helpers()
    {
        await using var server = await EchoServer.StartAsync();
        var executor = new HttpExecutor();
        var spec = new HttpRequestSpec { Url = server.Url("echo") };

        var basic = new AuthSpec { Type = "basic" };
        basic.Set("username", "ada");
        basic.Set("password", "secret");
        Assert.Equal("Basic YWRhOnNlY3JldA==", Json(await executor.SendAsync(Prepare(spec, auth: basic), _ct))["headers"]!["authorization"].GetString());

        var apikey = new AuthSpec { Type = "apikey" };
        apikey.Set("key", "api_key");
        apikey.Set("value", "k1");
        apikey.Set("in", "query");
        Assert.Equal("k1", Json(await executor.SendAsync(Prepare(spec, auth: apikey), _ct))["query"]!["api_key"].GetString());

        var bearer = new AuthSpec { Type = "bearer" };
        bearer.Set("token", "t0k");
        Assert.Equal("Bearer t0k", Json(await executor.SendAsync(Prepare(spec, auth: bearer), _ct))["headers"]!["authorization"].GetString());

        // As in Postman, an empty token (one a login request's script sets later) adds no header: the request's own stays,
        // and credentials in the URL apply.
        var empty = new AuthSpec { Type = "bearer" };
        empty.Set("token", "{{token}}");
        var variables = new VariableResolver([new DictionaryScope("env", new Dictionary<string, string> { ["token"] = "" })]);
        var typed = new HttpRequestSpec { Url = server.Url("echo"), Headers = [new() { Key = "Authorization", Value = "Token abc" }] };
        var prepared = Prepare(typed, auth: empty, variables: variables);
        Assert.Equal("Token abc", Json(await executor.SendAsync(prepared, _ct))["headers"]!["authorization"].GetString());
        Assert.Equal(["Bearer token is empty: the request was sent without it."], prepared.Warnings);
        var withUser = Prepare(new HttpRequestSpec { Url = server.Url("echo").Replace("://", "://ada:secret@", StringComparison.Ordinal) }, auth: empty, variables: variables);
        Assert.Equal("Basic YWRhOnNlY3JldA==", Json(await executor.SendAsync(withUser, _ct))["headers"]!["authorization"].GetString());
    }

    [Fact]
    public async Task An_api_key_or_token_in_the_query_is_encoded_and_replaces_a_parameter_of_the_same_name()
    {
        await using var server = await EchoServer.StartAsync();
        var executor = new HttpExecutor();
        var apikey = new AuthSpec { Type = "apikey" };
        apikey.Set("key", "api_key");
        apikey.Set("value", "ab&cd #+é");
        apikey.Set("in", "query");
        var oauth = new AuthSpec { Type = "oauth2" };
        oauth.Set("accessToken", "t0k+en/=");
        oauth.Set("addTokenTo", "queryParams");

        var prepared = Prepare(new HttpRequestSpec { Url = server.Url("echo?api_key=old&page=2") }, auth: apikey);
        var query = Json(await executor.SendAsync(prepared, _ct))["query"]!;

        Assert.Equal(("ab&cd #+é", "2"), (query["api_key"].GetString(), query["page"].GetString()));
        Assert.Equal("The auth helper replaced the 'api_key' query parameter.", Assert.Single(prepared.Warnings));
        Assert.Equal(server.Url("echo?page=2&api_key=***"), prepared.Redact(prepared.Url)); // the URL as the history keeps it
        Assert.Equal("t0k+en/=", Json(await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("echo") }, auth: oauth), _ct))["query"]!["access_token"].GetString());
    }

    [Fact]
    public async Task A_hash_in_a_path_or_query_value_and_a_question_mark_in_a_path_variable_are_sent_as_part_of_the_value()
    {
        await using var server = await EchoServer.StartAsync();
        var executor = new HttpExecutor();
        var variables = new VariableResolver([new DictionaryScope("env", new Dictionary<string, string> { ["tag"] = "#news", ["name"] = "report#1.pdf" })]);

        // The URL would end its query at a '#': what follows would not be sent.
        var query = Json(await executor.SendAsync(Prepare(new HttpRequestSpec
        {
            Url = server.Url("echo/search"),
            QueryParams = [new() { Key = "q", Value = "{{tag}}" }, new() { Key = "color", Value = "#ff0000" }, new() { Key = "page", Value = "2" }],
        }, variables: variables), _ct));
        Assert.Equal("?q=%23news&color=%23ff0000&page=2", query["rawQuery"].GetString());

        // And its path at a '#' or, in a path variable, a '?'; a {{variable}} in the path keeps a '?' (it starts the query, as in Postman).
        var prepared = Prepare(new HttpRequestSpec
        {
            Url = server.BaseUri + "echo/files/:file/{{name}}?page=2",
            PathVariables = [new() { Key = "file", Value = "what?#1" }],
            QueryParams = [new() { Key = "page", Value = "2" }],
        }, variables: variables);
        var path = Json(await executor.SendAsync(prepared, _ct));
        Assert.Equal(server.Url("echo/files/what%3F%231/report%231.pdf?page=2"), prepared.Uri!.AbsoluteUri);
        Assert.Equal(("/echo/files/what?#1/report#1.pdf", "?page=2"), (path["path"].GetString(), path["rawQuery"].GetString()));
    }

    [Theory]
    [InlineData("localhost:{0}/echo/oauth/authorize?redirect_uri=http://localhost:4200/cb")]
    [InlineData("127.0.0.1:{0}/echo/login?next=https://app.example.com/")]
    public async Task A_url_without_a_scheme_is_sent_as_http_also_when_its_query_holds_a_url(string template)
    {
        await using var server = await EchoServer.StartAsync();
        var url = string.Format(CultureInfo.InvariantCulture, template, server.BaseUri.Port);
        var prepared = Prepare(new HttpRequestSpec { Url = url });

        var echo = Json(await new HttpExecutor().SendAsync(prepared, _ct));

        // As curl and Postman send it: a "://" in the query names no scheme.
        Assert.Equal("http://" + url, prepared.Uri!.AbsoluteUri);
        Assert.Equal(url[url.IndexOf('/', StringComparison.Ordinal)..], echo["path"].GetString() + echo["rawQuery"].GetString());
        Assert.Equal($"curl 'http://{url}' --location --max-redirs 10", CurlExporter.Export(prepared, multiline: false));
    }

    [Fact]
    public async Task Credentials_in_the_url_are_sent_as_basic_auth_unless_the_request_has_its_own()
    {
        await using var server = await EchoServer.StartAsync();
        await using var other = await EchoServer.StartAsync();
        var executor = new HttpExecutor();
        var url = $"http://ada:p%40ss%20w0rd@localhost:{server.BaseUri.Port}/echo";

        var prepared = Prepare(new HttpRequestSpec { Url = url });
        var echo = Json(await executor.SendAsync(prepared, _ct));

        Assert.Equal("Basic " + Convert.ToBase64String("ada:p@ss w0rd"u8), echo["headers"]!["authorization"].GetString());
        Assert.Equal("Basic (from the URL)", prepared.AuthDescription);
        Assert.Equal($"http://ada:***@localhost:{server.BaseUri.Port}/echo", prepared.Redact(prepared.Url)); // the URL as the history keeps it

        // A typed Authorization header, or an auth helper, comes first.
        var bearer = new AuthSpec { Type = "bearer" };
        bearer.Set("token", "t0k");
        var typed = Json(await executor.SendAsync(Prepare(new HttpRequestSpec { Url = url, Headers = [new() { Key = "Authorization", Value = "Bearer typed" }] }), _ct));
        Assert.Equal("Bearer typed", typed["headers"]!["authorization"].GetString());
        Assert.Equal("Bearer t0k", Json(await executor.SendAsync(Prepare(new HttpRequestSpec { Url = url }, auth: bearer), _ct))["headers"]!["authorization"].GetString());

        // Like other credentials, they stay with the request's origin.
        var redirect = url.Replace("/echo", "/redirect-to?url=" + Uri.EscapeDataString(other.Url("echo")), StringComparison.Ordinal);
        Assert.Null(Json(await executor.SendAsync(Prepare(new HttpRequestSpec { Url = redirect }), _ct))["headers"]!["authorization"]);
    }

    [Theory]
    [InlineData("Ab1/Xy9zQ")] // in base64, and in passwords
    [InlineData("S3cr#t?!x")]
    [InlineData("p@ss\\word")]
    public async Task Variables_in_the_user_info_of_a_url_are_sent_whatever_they_hold(string password)
    {
        // As Postman sends them: a '/', '#' or '?' would otherwise end the authority, a '@' start the host.
        await using var server = await EchoServer.StartAsync();
        var variables = new VariableResolver([new DictionaryScope("env", new Dictionary<string, string> { ["user"] = "deploy", ["pw"] = password })]);

        var prepared = Prepare(new HttpRequestSpec { Url = $"http://{{{{user}}}}:{{{{pw}}}}@localhost:{server.BaseUri.Port}/echo" }, variables: variables);
        var echo = Json(await new HttpExecutor().SendAsync(prepared, _ct));
        var invalid = Prepare(new HttpRequestSpec { Url = "https://deploy:{{pw}}@{{host}}/v1" }, variables: variables);

        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("deploy:" + password)), echo["headers"]!["authorization"].GetString());
        Assert.Equal("/echo", echo["path"].GetString());
        Assert.Equal("'https://deploy:***@{{host}}/v1' is not a valid http(s) URL.", Assert.Single(invalid.Errors));
    }

    [Fact]
    public async Task A_url_a_redirect_leads_to_does_not_take_the_credentials_of_the_request_url()
    {
        await using var server = await EchoServer.StartAsync();
        var url = $"http://ada:pw@localhost:{server.BaseUri.Port}/redirect-to?url=%2Fecho";
        var followed = Prepare(new HttpRequestSpec { Url = url });
        var stopped = Prepare(new HttpRequestSpec { Url = url, Settings = new RequestSettings { MaxRedirects = 0 } });

        var exchange = await new HttpExecutor().SendAsync(followed, _ct);
        await new HttpExecutor().SendAsync(stopped, _ct);

        // They are sent as an Authorization header, to the request's origin; where the redirect is shown, kept or recorded,
        // the target would otherwise hold the password.
        Assert.Equal("Basic " + Convert.ToBase64String("ada:pw"u8), Json(exchange)["headers"]!["authorization"].GetString());
        Assert.Equal(server.Url("echo"), exchange.Response!.FinalUri!.AbsoluteUri);
        Assert.Equal($"The redirect to {server.Url("echo")} was not followed: it would exceed the maximum number of redirects (0).", Assert.Single(stopped.Warnings));
    }

    [Fact]
    public async Task A_redirect_to_a_url_that_is_not_http_is_reported_without_its_password()
    {
        await using var server = await EchoServer.StartAsync();
        var prepared = Prepare(new HttpRequestSpec { Url = server.Url("redirect-to?url=" + Uri.EscapeDataString("ftp://ada:pw12@files.example.com/report.csv")) });

        var exchange = await new HttpExecutor().SendAsync(prepared, _ct);

        Assert.Equal(302, exchange.Response!.StatusCode);
        Assert.Equal("The redirect to ftp://ada:***@files.example.com/report.csv was not followed: TermRest follows only http and https URLs.", Assert.Single(prepared.Warnings));
    }

    [Fact]
    public async Task Header_values_with_line_breaks_are_not_sent_and_a_trailing_line_break_is_dropped()
    {
        await using var server = await EchoServer.StartAsync();
        var variables = new VariableResolver([new DictionaryScope("env", new Dictionary<string, string>
        {
            ["injected"] = "x\r\nX-Injected: yes",
            ["nul"] = "x\0y",
            ["pasted"] = "t0k\r\n",
        })]);
        var bearer = new AuthSpec { Type = "bearer" };
        bearer.Set("token", "{{injected}}");

        var header = Prepare(new HttpRequestSpec { Url = server.Url("echo"), Headers = [new() { Key = "X-Test", Value = "{{injected}}" }] }, variables: variables);
        var nul = Prepare(new HttpRequestSpec { Url = server.Url("echo"), Headers = [new() { Key = "X-Test", Value = "{{nul}}" }] }, variables: variables);
        var auth = Prepare(new HttpRequestSpec { Url = server.Url("echo") }, auth: bearer, variables: variables);

        Assert.Equal("Header 'X-Test' contains a line break or NUL character.", Assert.Single(header.Errors));
        Assert.Single(nul.Errors);
        Assert.Equal("Header 'Authorization' contains a line break or NUL character.", Assert.Single(auth.Errors));
        var refused = await new HttpExecutor().SendAsync(header, _ct);
        Assert.Null(refused.Response);
        Assert.Null(refused.Timing.Connect);

        bearer.Set("token", "{{pasted}}");
        var pasted = Prepare(new HttpRequestSpec { Url = server.Url("echo") }, auth: bearer, variables: variables);
        Assert.Equal("Bearer t0k", Json(await new HttpExecutor().SendAsync(pasted, _ct))["headers"]!["authorization"].GetString());
    }

    [Fact]
    public async Task Header_values_that_are_not_ascii_are_sent_in_utf8()
    {
        await using var server = await EchoServer.StartAsync();
        var file = Path.Combine(_pki.Directory, "cv.pdf");
        await File.WriteAllTextAsync(file, "cv", _ct);
        var variables = new VariableResolver([new DictionaryScope("env", new Dictionary<string, string> { ["name"] = "Zoë" })]);

        // As curl sends them; .NET would refuse the request.
        var exchange = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec
        {
            Method = "PUT",
            Url = server.Url("echo"),
            Headers =
            [
                new() { Key = "X-User", Value = "José" },
                new() { Key = "X-Name", Value = "{{name}}" },
                new() { Key = "Content-Disposition", Value = "attachment; filename=\"résumé.pdf\"" },
            ],
            Body = new RequestBody { Mode = BodyMode.File, FileSrc = file },
        }, variables: variables), _ct);

        var headers = Json(exchange)["headers"]!;
        Assert.Equal(("José", "Zoë", "attachment; filename=\"résumé.pdf\""),
            (headers["x-user"].GetString(), headers["x-name"].GetString(), headers["content-disposition"].GetString()));
        Assert.Contains(new KeyValuePair<string, string>("X-Name", "Zoë"), exchange.SentHeaders);
    }

    [Fact]
    public async Task Cookies_from_the_jar_go_back_with_the_bytes_the_server_sent()
    {
        // One value in UTF-8 and one in Latin-1 (as Node or Python write a non-ASCII header); .NET reads both as Latin-1.
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var noContent = "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray();
        var served = ServeRawAsync(listener,
            Encoding.Latin1.GetBytes("HTTP/1.1 204 No Content\r\nSet-Cookie: u=Zo\u00C3\u00AB\r\nSet-Cookie: l=caf\u00E9\r\nConnection: close\r\n\r\n"),
            noContent,
            noContent);
        var executor = new HttpExecutor();
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";

        await executor.SendAsync(Prepare(new HttpRequestSpec { Url = url }), _ct);
        await executor.SendAsync(Prepare(new HttpRequestSpec { Url = url }), _ct);
        await executor.SendAsync(Prepare(new HttpRequestSpec { Url = url, Headers = [new() { Key = "Cookie", Value = "t=✓" }] }), _ct);
        var received = await served;

        var second = Encoding.Latin1.GetString(received[1]);
        Assert.Contains("u=Zo\u00C3\u00AB", second, StringComparison.Ordinal);
        Assert.Contains("l=caf\u00E9", second, StringComparison.Ordinal);

        // A Cookie header typed with characters that are not ASCII is sent in UTF-8, as every header is.
        Assert.Contains("t=\u00E2\u009C\u0093", Encoding.Latin1.GetString(received[2]), StringComparison.Ordinal);
    }

    /// <summary>Answers each connection to <paramref name="listener"/> with the next response; returns the requests received.</summary>
    private Task<List<byte[]>> ServeRawAsync(System.Net.Sockets.TcpListener listener, params byte[][] responses) => Task.Run(async () =>
    {
        var received = new List<byte[]>();
        foreach (var response in responses)
        {
            using var connection = await listener.AcceptTcpClientAsync(_ct);
            var stream = connection.GetStream();
            var request = new byte[16384];
            var read = 0;
            int count;
            while (request.AsSpan(0, read).IndexOf("\r\n\r\n"u8) < 0 && (count = await stream.ReadAsync(request.AsMemory(read), _ct)) > 0)
            {
                read += count;
            }

            received.Add(request[..read]);
            await stream.WriteAsync(response, _ct);
        }

        return received;
    }, _ct);

    [Theory]
    [InlineData("digest")]
    [InlineData("ntlm")]
    public async Task Challenge_credentials_are_never_sent_as_basic(string type)
    {
        await using var server = await EchoServer.StartAsync();
        var auth = new AuthSpec { Type = type };
        auth.Set("username", "ada");
        auth.Set("password", "secret");

        var exchange = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("challenge/basic") }, auth: auth), _ct);

        Assert.Equal(401, exchange.Response!.StatusCode);
    }

    [Fact]
    public async Task Digest_credentials_answer_digest_challenges_also_after_a_redirect_on_the_same_origin()
    {
        await using var server = await EchoServer.StartAsync();
        var executor = new HttpExecutor();
        var digest = new AuthSpec { Type = "digest" };
        digest.Set("username", "ada");
        digest.Set("password", "secret");

        var direct = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("challenge/digest") }, auth: digest), _ct);
        var redirected = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("redirect-to?url=%2Fchallenge%2Fdigest") }, auth: digest), _ct);

        Assert.Equal((200, 200), (direct.Response?.StatusCode, redirected.Response?.StatusCode));
        Assert.StartsWith("Digest username=\"ada\"", Json(direct)["headers"]!["authorization"].GetString(), StringComparison.Ordinal);
        Assert.StartsWith("Digest username=\"ada\"", Json(redirected)["headers"]!["authorization"].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Follows_redirects_and_keeps_cookies()
    {
        await using var server = await EchoServer.StartAsync();
        var executor = new HttpExecutor();

        var redirected = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("redirect") }), _ct);
        Assert.Equal("true", Json(redirected)["query"]!["redirected"].GetString());

        var notFollowed = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("redirect"), Settings = new RequestSettings { FollowRedirects = false } }), _ct);
        Assert.Equal(302, notFollowed.Response!.StatusCode);

        await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("set-cookie") }), _ct);
        Assert.Contains("session=abc123", Json(await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("echo") }), _ct))["headers"]!["cookie"].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_to_another_origin_drops_credentials_and_keeps_other_headers()
    {
        await using var origin = await EchoServer.StartAsync();
        await using var other = await EchoServer.StartAsync();
        var apikey = new AuthSpec { Type = "apikey" };
        apikey.Set("key", "X-API-Key");
        apikey.Set("value", "k1");
        var spec = new HttpRequestSpec
        {
            Url = origin.Url("redirect-to?url=" + Uri.EscapeDataString(other.Url("echo"))),
            Headers =
            [
                new() { Key = "Authorization", Value = "Bearer typed" },
                new() { Key = "Cookie", Value = "typed=1" },
                new() { Key = "Host", Value = $"localhost:{origin.BaseUri.Port}" },
                new() { Key = "X-Custom", Value = "kept" },
            ],
        };

        var exchange = await new HttpExecutor().SendAsync(Prepare(spec, auth: apikey), _ct);

        var headers = Json(exchange)["headers"]!;
        Assert.Null(headers["x-api-key"]);
        Assert.Null(headers["authorization"]);
        Assert.Null(headers["cookie"]);
        Assert.Equal($"localhost:{other.BaseUri.Port}", headers["host"].GetString());
        Assert.Equal("kept", headers["x-custom"].GetString());
        Assert.Equal(other.Url("echo"), exchange.Response!.FinalUri?.ToString());
        Assert.Equal("k1", exchange.SentHeaders.Single(h => h.Key == "X-API-Key").Value); // what was sent to the origin
        Assert.True(exchange.Timing is { Connect: not null, Waiting: not null, Download: not null }, exchange.Timing.ToString());
    }

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task A_redirect_on_the_same_origin_keeps_credentials_cookies_method_and_body(int status)
    {
        await using var server = await EchoServer.StartAsync();
        var bearer = new AuthSpec { Type = "bearer" };
        bearer.Set("token", "t0k");
        var spec = new HttpRequestSpec
        {
            Method = "POST",
            Url = server.Url($"redirect-to?status={status}&cookie=hop%3D1&url=%2Fecho%2Fitems%2F"),
            Headers = [new() { Key = "Cookie", Value = "typed=1" }],
            Body = new RequestBody { Mode = BodyMode.Raw, RawLanguage = "json", Raw = "{\"a\":1}" },
        };

        var exchange = await new HttpExecutor().SendAsync(Prepare(spec, auth: bearer), _ct);

        var echo = Json(exchange);
        Assert.Equal(("POST", "/echo/items/", "{\"a\":1}"), (echo["method"].GetString(), echo["path"].GetString(), echo["body"].GetString()));
        Assert.Equal("Bearer t0k", echo["headers"]!["authorization"].GetString());
        Assert.Contains("typed=1", echo["headers"]!["cookie"].GetString(), StringComparison.Ordinal);
        Assert.Contains("hop=1", echo["headers"]!["cookie"].GetString(), StringComparison.Ordinal);
        Assert.Equal(server.Url("echo/items/"), exchange.Response!.FinalUri?.ToString());
    }

    [Theory]
    [InlineData(301, "POST", "GET")]
    [InlineData(302, "POST", "GET")]
    [InlineData(303, "PUT", "GET")]
    [InlineData(302, "PUT", "PUT")]
    public async Task Redirects_change_the_method_like_browsers(int status, string method, string expected)
    {
        await using var server = await EchoServer.StartAsync();
        var spec = new HttpRequestSpec
        {
            Method = method,
            Url = server.Url($"redirect-to?status={status}&url=%2Fecho"),
            Body = new RequestBody { Mode = BodyMode.Raw, RawLanguage = "json", Raw = "{\"a\":1}" },
        };

        var echo = Json(await new HttpExecutor().SendAsync(Prepare(spec), _ct));

        Assert.Equal(expected, echo["method"].GetString());
        Assert.Equal(expected == method ? "{\"a\":1}" : "", echo["body"].GetString());
        Assert.Equal(expected == method, echo["headers"]!["content-type"] is not null);
    }

    [Fact]
    public async Task A_307_redirect_sends_a_file_body_again()
    {
        await using var server = await EchoServer.StartAsync();
        var file = Path.Combine(_pki.Directory, "upload.bin");
        await File.WriteAllTextAsync(file, "file-content", _ct);
        var spec = new HttpRequestSpec { Method = "PUT", Url = server.Url("redirect-to?status=307&url=%2Fecho"), Body = new RequestBody { Mode = BodyMode.File, FileSrc = file } };

        var echo = Json(await new HttpExecutor().SendAsync(Prepare(spec), _ct));

        Assert.Equal(("PUT", "file-content"), (echo["method"].GetString(), echo["body"].GetString()));
    }

    [Fact]
    public async Task A_typed_transfer_encoding_is_left_out_when_the_request_has_no_body()
    {
        await using var server = await EchoServer.StartAsync();
        var executor = new HttpExecutor();
        HttpRequestSpec Spec(string method, string path, RequestBody body) => new()
        {
            Method = method,
            Url = server.Url(path),
            Headers = [new() { Key = "Transfer-Encoding", Value = "chunked" }],
            Body = body,
        };
        var json = new RequestBody { Mode = BodyMode.Raw, RawLanguage = "json", Raw = "{\"a\":1}" };

        var chunked = Json(await executor.SendAsync(Prepare(Spec("PUT", "echo", json)), _ct));
        Assert.Equal(("chunked", "{\"a\":1}"), (chunked["headers"]!["transfer-encoding"].GetString(), chunked["body"].GetString()));

        // A 302 turns the POST into a GET without its body.
        var redirected = Json(await executor.SendAsync(Prepare(Spec("POST", "redirect-to?status=302&url=%2Fecho", json)), _ct));
        Assert.Equal(("GET", ""), (redirected["method"].GetString(), redirected["body"].GetString()));
        Assert.Null(redirected["headers"]!["transfer-encoding"]);

        var get = Prepare(Spec("GET", "echo", new RequestBody()));
        Assert.Null(Json(await executor.SendAsync(get, _ct))["headers"]!["transfer-encoding"]);
        Assert.Equal("'Transfer-Encoding' was not sent because the request has no body.", Assert.Single(get.Warnings));
    }

    [Fact]
    public async Task Max_redirects_limits_the_redirects_followed()
    {
        await using var server = await EchoServer.StartAsync();
        var executor = new HttpExecutor();
        var twice = server.Url("redirect-to?url=%2Fredirect"); // → /redirect → /echo?redirected=true
        PreparedRequest Limited(string url, int max) => Prepare(new HttpRequestSpec { Url = url, Settings = new RequestSettings { MaxRedirects = max } });

        var zero = Limited(server.Url("redirect"), 0);
        Assert.Equal(302, (await executor.SendAsync(zero, _ct)).Response!.StatusCode);
        Assert.Contains("The redirect to " + server.Url("echo?redirected=true") + " was not followed", Assert.Single(zero.Warnings), StringComparison.Ordinal);

        var one = Limited(twice, 1);
        var stopped = await executor.SendAsync(one, _ct);
        Assert.Equal((302, server.Url("redirect")), (stopped.Response!.StatusCode, stopped.Response.FinalUri?.ToString()));
        Assert.Contains("maximum number of redirects (1)", Assert.Single(one.Warnings), StringComparison.Ordinal);

        Assert.Equal("true", Json(await executor.SendAsync(Limited(twice, 2), _ct))["query"]!["redirected"].GetString());

        // A negative limit (curl's -1 for no limit, as earlier cURL imports kept it) leaves the workspace's.
        var negative = Limited(twice, -1);
        var followed = await executor.SendAsync(negative, _ct);
        Assert.Equal((200, server.Url("echo?redirected=true")), (followed.Response!.StatusCode, followed.Response.FinalUri?.ToString()));
        Assert.Empty(negative.Warnings);
    }

    [Fact]
    public async Task The_request_timeout_covers_the_redirects()
    {
        await using var server = await EchoServer.StartAsync();

        var exchange = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("redirect-to?url=%2Fslow") }, new ExecutionDefaults { Timeout = TimeSpan.FromMilliseconds(300) }), _ct);

        Assert.Equal("Request timed out after 300 ms.", exchange.ErrorMessage);
    }

    [Fact]
    public async Task A_redirect_from_https_to_http_is_not_followed()
    {
        await using var secure = await EchoServer.StartAsync(_pki.Server);
        await using var plain = await EchoServer.StartAsync();
        var prepared = Prepare(new HttpRequestSpec { Url = secure.Url("redirect-to?url=" + Uri.EscapeDataString(plain.Url("echo"))) }, new ExecutionDefaults { CaBundlePath = _pki.CaPemPath });

        var exchange = await new HttpExecutor().SendAsync(prepared, _ct);

        Assert.Equal(302, exchange.Response!.StatusCode);
        Assert.Contains("from https to http", Assert.Single(prepared.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_status_codes_timeouts_cancellation_and_connection_errors()
    {
        await using var server = await EchoServer.StartAsync();
        var executor = new HttpExecutor();

        Assert.Equal(503, (await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("status/503") }), _ct)).Response!.StatusCode);

        var timedOut = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("slow") }, new ExecutionDefaults { Timeout = TimeSpan.FromMilliseconds(200) }), _ct);
        Assert.Contains("timed out", timedOut.ErrorMessage, StringComparison.Ordinal);

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Assert.True((await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("slow") }), cancel.Token)).Cancelled);

        var refused = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = "http://127.0.0.1:1/" }), _ct);
        Assert.Null(refused.Response);
        Assert.StartsWith("ConnectionError: ", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("→", refused.ErrorMessage, StringComparison.Ordinal); // the socket's message is in the outer one already

        var invalid = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = "{{undefinedBase}}/x" }), _ct);
        Assert.Contains("not a valid", invalid.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("undefinedBase", invalid.Request.UnresolvedVariables);
    }

    [Fact]
    public async Task A_stalled_tls_handshake_is_reported_as_a_connect_timeout()
    {
        // The OS completes the TCP handshake for the listener's backlog, but nothing ever answers the TLS ClientHello.
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var executor = new HttpExecutor { ConnectTimeout = TimeSpan.FromSeconds(1) };

        var exchange = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = $"https://127.0.0.1:{port}/" }), _ct);

        Assert.Null(exchange.Response);
        Assert.False(exchange.Cancelled);
        Assert.Contains($"TLS handshake with 127.0.0.1:{port} did not complete within", exchange.ErrorMessage, StringComparison.Ordinal);
        Assert.NotNull(exchange.Timing.Connect);
    }

    [Fact]
    public async Task Requests_that_cannot_be_built_report_an_error_instead_of_throwing()
    {
        var executor = new HttpExecutor();

        var badMethod = await executor.SendAsync(Prepare(new HttpRequestSpec { Method = "{{verb}}", Url = "http://127.0.0.1:1/" }), _ct);
        Assert.Contains("is not a valid HTTP method", badMethod.ErrorMessage, StringComparison.Ordinal);

        var badProxy = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = "http://127.0.0.1:1/" }, new ExecutionDefaults { Proxy = "http://bad proxy:x" }), _ct);
        Assert.Null(badProxy.Response);
        Assert.False(string.IsNullOrEmpty(badProxy.ErrorMessage));
    }

    [Fact]
    public async Task The_error_of_a_url_that_is_not_valid_masks_its_password()
    {
        // Not valid while its port is unresolved; nothing knows its password as a secret then (a short one escapes reports).
        var prepared = Prepare(new HttpRequestSpec { Url = "http://ada:pw12@localhost:{{port}}/x" });

        var exchange = await new HttpExecutor().SendAsync(prepared, _ct);

        Assert.Equal("'http://ada:***@localhost:{{port}}/x' is not a valid http(s) URL.", Assert.Single(prepared.Errors));
        Assert.Equal(prepared.Errors[0], exchange.ErrorMessage);

        // A user name without a password is the credential (an API key sent as Basic auth), so it is masked instead.
        foreach (var (url, shown) in (ReadOnlySpan<(string, string)>)[
                     ("https://sk_test_abc@{{host}}/v1/charges", "https://***@{{host}}/v1/charges"),
                     ("https://sk_test_abc:@api.example.com:{{port}}/v1", "https://***:@api.example.com:{{port}}/v1")])
        {
            Assert.Equal($"'{shown}' is not a valid http(s) URL.", Assert.Single(Prepare(new HttpRequestSpec { Url = url }).Errors));
        }
    }

    [Theory]
    [InlineData("S3cret'Pr0xy", "Pr0xy")]
    [InlineData("my pass", "my pass")] // a space, as typed in the proxy setting
    [InlineData("P%C3%A4ss%20wort", "Päss wort")] // .NET's message unescapes it
    public async Task A_proxy_password_is_masked_in_the_error_of_a_tunnel_the_proxy_refused(string password, string shown)
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var served = ServeRawAsync(listener, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
        var proxy = $"http://ada:{password}@127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

        var exchange = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec { Url = "https://api.example.com/x" }, new ExecutionDefaults { Proxy = proxy }), _ct);

        Assert.StartsWith("CONNECT api.example.com:443 ", Encoding.ASCII.GetString(Assert.Single(await served)), StringComparison.Ordinal);
        Assert.Null(exchange.Response);
        Assert.Contains("'http://ada:***@127.0.0.1:", exchange.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(shown, exchange.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reloading_certificates_does_not_break_a_request_that_already_selected_one()
    {
        await using var server = await EchoServer.StartAsync(_pki.Server, requireClientCertificate: true);
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        workspace.Settings.CaBundle = _pki.CaPemPath;
        workspace.Certificates.Entries.Add(new CertificateEntry
        {
            Name = "client",
            Matches = [$"https://localhost:{server.BaseUri.Port}/*"],
            Pfx = new FileSource { Src = _pki.ClientPfxPath },
            Passphrase = TestPki.Passphrase,
        });
        using var runner = new RequestRunner(workspace);
        var prepared = runner.Prepare(PostmanCollection.Create("t"), null, new HttpRequestSpec { Url = server.Url("echo") });
        Assert.NotNull(prepared.ClientCertificate);

        runner.ReloadCertificates(); // the user edits certificates (F8) while this request is in flight

        var exchange = await runner.SendAsync(prepared, _ct);
        Assert.Equal(_pki.Client.Thumbprint, Json(exchange)["clientCertificateThumbprint"].GetString());
        Assert.Equal(_pki.Client.Thumbprint, exchange.Tls.Single().ClientCertificate?.Thumbprint);
        Assert.Equal(_pki.Client.Thumbprint, prepared.ClientCertificate.Summary.Thumbprint);
    }

    [Fact]
    public async Task Untrusted_server_certificate_is_rejected_unless_trusted_or_verification_is_off()
    {
        await using var server = await EchoServer.StartAsync(_pki.Server);
        var executor = new HttpExecutor();
        var spec = new HttpRequestSpec { Url = server.Url("echo") };

        var strict = await executor.SendAsync(Prepare(spec), _ct);
        Assert.Null(strict.Response);
        Assert.Contains("rejected", strict.ErrorMessage, StringComparison.Ordinal);
        Assert.False(Assert.Single(strict.Tls).Accepted);

        var insecure = await executor.SendAsync(Prepare(spec, new ExecutionDefaults { StrictSsl = false }), _ct);
        Assert.Equal(200, insecure.Response!.StatusCode);
        Assert.Contains("turned off", insecure.Tls.Single().AcceptReason, StringComparison.Ordinal);

        var perRequest = new HttpRequestSpec { Url = server.Url("echo"), Settings = new RequestSettings { StrictSsl = false } };
        Assert.Equal(200, (await executor.SendAsync(Prepare(perRequest), _ct)).Response!.StatusCode);

        var trusted = await executor.SendAsync(Prepare(spec, new ExecutionDefaults { CaBundlePath = _pki.CaPemPath }), _ct);
        Assert.Equal(200, trusted.Response!.StatusCode);
        var tls = trusted.Tls.Single();
        Assert.True(tls.Accepted);
        Assert.Contains("CA bundle", tls.AcceptReason, StringComparison.Ordinal);
        Assert.NotEqual(System.Security.Authentication.SslProtocols.None, tls.Protocol);
        Assert.Equal("CN=localhost", tls.ServerChain[0].Subject);
    }

    [Theory]
    [InlineData("pem")]
    [InlineData("pem-encrypted")]
    [InlineData("pfx")]
    [InlineData("pem-chain")]
    public async Task Mutual_tls_presents_the_matching_client_certificate(string kind)
    {
        await using var server = await EchoServer.StartAsync(_pki.Server, requireClientCertificate: true);
        var entry = kind switch
        {
            "pem" => new CertificateEntry { Cert = new FileSource { Src = "client.pem" }, Key = new FileSource { Src = "client.key" } },
            "pem-chain" => new CertificateEntry { Cert = new FileSource { Src = "client-chain.pem" }, Key = new FileSource { Src = "client.key" } },
            "pem-encrypted" => new CertificateEntry { Cert = new FileSource { Src = "client.pem" }, Key = new FileSource { Src = "client.enc.key" }, Passphrase = "{{keyPass}}" },
            _ => new CertificateEntry { Pfx = new FileSource { Src = "client.pfx" }, Passphrase = TestPki.Passphrase },
        };
        entry.Name = kind;
        entry.Matches = [$"https://localhost:{server.BaseUri.Port}/*"];
        var registry = new CertificateRegistry(_pki.Directory) { Entries = [entry] };
        using var provider = new ClientCertificateProvider(registry);
        var variables = new VariableResolver([new DictionaryScope("env", new Dictionary<string, string> { ["keyPass"] = TestPki.Passphrase })]);
        var defaults = new ExecutionDefaults { CaBundlePath = _pki.CaPemPath };

        var prepared = Prepare(new HttpRequestSpec { Url = server.Url("echo") }, defaults, provider, variables: variables);
        Assert.Empty(prepared.Warnings);
        var exchange = await new HttpExecutor().SendAsync(prepared, _ct);

        var echo = Json(exchange);
        Assert.Equal(_pki.Client.Thumbprint, echo["clientCertificateThumbprint"].GetString());
        var tls = exchange.Tls.Single();
        Assert.True(tls.ClientCertificate is not null, tls.Describe());
        Assert.Equal(_pki.Client.Thumbprint, tls.ClientCertificate.Thumbprint);
        Assert.Contains("matched", tls.ClientCertificateOrigin, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("client.pem")]
    [InlineData("client-chain.pem")] // with its chain: through a context on Linux
    public async Task Tls_details_are_those_of_the_handshake_when_the_server_closes_the_connection(string certificate)
    {
        // The connection is disposed once the response is read, and its TLS session with it.
        await using var server = await EchoServer.StartAsync(_pki.Server, requireClientCertificate: true);
        using var provider = new ClientCertificateProvider(new CertificateRegistry(_pki.Directory)
        {
            Entries = [new CertificateEntry { Name = "client", Matches = [$"https://localhost:{server.BaseUri.Port}/*"], Cert = new FileSource { Src = certificate }, Key = new FileSource { Src = "client.key" } }],
        });

        var exchange = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("close") }, new ExecutionDefaults { CaBundlePath = _pki.CaPemPath }, provider), _ct);

        Assert.Equal(_pki.Client.Thumbprint, Json(exchange)["clientCertificateThumbprint"].GetString());
        Assert.Equal("close", exchange.Response!.Headers.Single(h => h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)).Value);
        var tls = exchange.Tls.Single();
        Assert.True(tls.Protocol != System.Security.Authentication.SslProtocols.None && tls.CipherSuite is not null, tls.Describe());
        Assert.True(tls.ClientCertificate is not null, tls.Describe());
        Assert.Equal((_pki.Client.Thumbprint, $"matched https://localhost:{server.BaseUri.Port}/*"), (tls.ClientCertificate.Thumbprint, tls.ClientCertificateOrigin));
        if (!OperatingSystem.IsMacOS() || certificate == "client.pem") // there a certificate sent through a context shows no request
        {
            Assert.True(tls.ClientCertificateRequested, tls.Describe());
        }
    }

    [Fact]
    public async Task A_client_certificate_the_server_asks_for_after_the_handshake_is_shown_as_sent()
    {
        // The server renegotiates (TLS 1.2). From the second request on, Windows answers from the credential it cached for the
        // certificate, without asking TermRest's selection callback: only the session tells.
        await using var server = await EchoServer.StartAsync(_pki.Server, delayClientCertificate: true);
        using var provider = new ClientCertificateProvider(new CertificateRegistry(_pki.Directory)
        {
            Entries = [new CertificateEntry { Name = "client", Matches = [$"https://localhost:{server.BaseUri.Port}/*"], Cert = new FileSource { Src = "client.pem" }, Key = new FileSource { Src = "client.key" } }],
        });

        for (var i = 0; i < 2; i++)
        {
            var exchange = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("renegotiate") }, new ExecutionDefaults { CaBundlePath = _pki.CaPemPath }, provider), _ct);

            var received = Json(exchange)["clientCertificateThumbprint"].GetString();
            var tls = exchange.Tls.Single();
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(_pki.Client.Thumbprint, received);
            }

            if (received is not null) // where the platform renegotiates
            {
                Assert.True(tls.ClientCertificate?.Thumbprint == received && (tls.ClientCertificateRequested || OperatingSystem.IsMacOS()), $"{i}: {tls.Describe()}");
            }
        }
    }

    [Fact]
    public async Task Mutual_tls_sends_the_intermediates_loaded_with_the_certificate()
    {
        var (client, intermediate) = _pki.IssueClientViaIntermediate();
        await using var server = await EchoServer.StartAsync(_pki.Server, requireClientCertificate: true);
        using var provider = new ClientCertificateProvider(new CertificateRegistry(_pki.Directory)
        {
            Entries = [new CertificateEntry { Name = "chain", Matches = [$"https://localhost:{server.BaseUri.Port}/*"], Cert = new FileSource { Src = "client-intermediate-chain.pem" }, Key = new FileSource { Src = "client-intermediate.key" } }],
        });

        var echo = Json(await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("echo") }, new ExecutionDefaults { CaBundlePath = _pki.CaPemPath }, provider), _ct));

        Assert.Equal(client, echo["clientCertificateThumbprint"].GetString());
        Assert.Contains(intermediate, echo["clientCertificateChain"]!.AsArray().Select(thumbprint => thumbprint.GetString()));
    }

    [Fact]
    public async Task Mutual_tls_without_a_certificate_fails_with_a_clear_error()
    {
        await using var server = await EchoServer.StartAsync(_pki.Server, requireClientCertificate: true);

        var exchange = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("echo") }, new ExecutionDefaults { CaBundlePath = _pki.CaPemPath }), _ct);

        var diagnostics = exchange.Response is null ? "" : Encoding.UTF8.GetString(exchange.Response.Body) + " | " + string.Join(" | ", exchange.Tls.Select(t => t.Describe()));
        Assert.True(exchange.Response is null, diagnostics);
        Assert.False(string.IsNullOrEmpty(exchange.ErrorMessage));
    }

    [Theory]
    [InlineData("client.pem")]
    [InlineData("client-chain.pem")] // with its chain
    public async Task A_client_certificate_is_not_reported_as_requested_when_the_server_does_not_ask_for_one(string certificate)
    {
        await using var server = await EchoServer.StartAsync(_pki.Server);
        var entry = new CertificateEntry { Name = "unasked", Cert = new FileSource { Src = certificate }, Key = new FileSource { Src = "client.key" } };
        entry.Matches = [$"https://localhost:{server.BaseUri.Port}/*"];
        using var provider = new ClientCertificateProvider(new CertificateRegistry(_pki.Directory) { Entries = [entry] });

        var exchange = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("echo") }, new ExecutionDefaults { CaBundlePath = _pki.CaPemPath }, provider), _ct);

        Assert.Equal(200, exchange.Response?.StatusCode);
        var tls = exchange.Tls.Single();
        Assert.False(tls.ClientCertificateRequested, tls.Describe());
        if (!OperatingSystem.IsMacOS()) // there, the certificate chosen shows even when it was not sent
        {
            Assert.True(tls.ClientCertificate is null, tls.Describe());
        }
    }

    [Fact]
    public async Task A_certificate_sent_with_its_chain_is_not_presented_by_later_requests_without_a_certificate()
    {
        await using var quiet = await EchoServer.StartAsync(_pki.Server);
        await using var asking = await EchoServer.StartAsync(_pki.Server, askForClientCertificate: true);
        await using var requiring = await EchoServer.StartAsync(_pki.Server, requireClientCertificate: true);
        var defaults = new ExecutionDefaults { CaBundlePath = _pki.CaPemPath };
        using var provider = new ClientCertificateProvider(new CertificateRegistry(_pki.Directory)
        {
            Entries = [new CertificateEntry { Name = "chain", Matches = [$"https://localhost:{quiet.BaseUri.Port}/*"], Cert = new FileSource { Src = "client-chain.pem" }, Key = new FileSource { Src = "client.key" } }],
        });
        async Task<HttpExchange> Send(EchoServer server) => await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("echo") }, defaults, provider), _ct);

        // .NET 10.0.15 on Windows cached the credential of a connection given an SslStreamCertificateContext, certificate
        // included, as the one for connections without a certificate, if none was cached yet (as in a new process).
        ClearTlsCredentialCache();
        Assert.Null(Json(await Send(quiet))["clientCertificateThumbprint"].GetString());

        Assert.Null(Json(await Send(asking))["clientCertificateThumbprint"].GetString());
        var refused = await Send(requiring);
        Assert.True(refused.Response is null, refused.Response is null ? "" : Encoding.UTF8.GetString(refused.Response.Body));
    }

    /// <summary>Empties .NET's process-wide cache of TLS client credentials (used on Windows).</summary>
    private static void ClearTlsCredentialCache() =>
        (typeof(System.Net.Security.SslStream).Assembly.GetType("System.Net.Security.SslSessionsCache")
            ?.GetField("s_cachedCreds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) as System.Collections.IDictionary)?.Clear();

    [Fact]
    public async Task Request_pinned_certificate_is_used()
    {
        await using var server = await EchoServer.StartAsync(_pki.Server, requireClientCertificate: true);
        using var provider = new ClientCertificateProvider(new CertificateRegistry(_pki.Directory));
        var spec = new HttpRequestSpec
        {
            Url = server.Url("echo"),
            Certificate = new CertificateSpec { Name = "pinned", CertSrc = _pki.ClientCertPemPath, KeySrc = _pki.ClientKeyPemPath },
        };

        var exchange = await new HttpExecutor().SendAsync(Prepare(spec, new ExecutionDefaults { CaBundlePath = _pki.CaPemPath }, provider), _ct);

        Assert.Equal(_pki.Client.Thumbprint, Json(exchange)["clientCertificateThumbprint"].GetString());
    }

    [Fact]
    public async Task Each_redirect_gets_the_client_certificate_registered_for_its_own_url()
    {
        await using var registered = await EchoServer.StartAsync(_pki.Server);
        await using var asking = await EchoServer.StartAsync(_pki.Server, askForClientCertificate: true);
        await using var plain = await EchoServer.StartAsync();
        var defaults = new ExecutionDefaults { CaBundlePath = _pki.CaPemPath };
        ClientCertificateProvider Registry(EchoServer server) => new(new CertificateRegistry(_pki.Directory)
        {
            Entries = [new CertificateEntry { Name = "client", Matches = [$"https://localhost:{server.BaseUri.Port}/*"], Pfx = new FileSource { Src = "client.pfx" }, Passphrase = TestPki.Passphrase }],
        });
        var toAsking = "redirect-to?url=" + Uri.EscapeDataString(asking.Url("echo"));

        // Registered for the first URL only: not presented to the server the redirect leads to, although it asks for one.
        using var forRegistered = Registry(registered);
        var elsewhere = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec { Url = registered.Url(toAsking) }, defaults, forRegistered), _ct);
        Assert.Null(Json(elsewhere)["clientCertificateThumbprint"].GetString());
        Assert.Equal(2, elsewhere.Tls.Count);
        Assert.Null(elsewhere.Tls[1].ClientCertificate);

        // Registered for the redirect target: presented there, also when the request started on plain http.
        using var forAsking = Registry(asking);
        var upgraded = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec { Url = plain.Url(toAsking) }, defaults, forAsking), _ct);
        Assert.Equal(_pki.Client.Thumbprint, Json(upgraded)["clientCertificateThumbprint"].GetString());
        Assert.Equal(_pki.Client.Thumbprint, upgraded.Tls.Single().ClientCertificate?.Thumbprint);
    }

    [Fact]
    public async Task A_certificate_pinned_without_url_patterns_is_not_presented_to_another_origin_a_redirect_leads_to()
    {
        await using var first = await EchoServer.StartAsync(_pki.Server);
        await using var asking = await EchoServer.StartAsync(_pki.Server, askForClientCertificate: true);
        using var provider = new ClientCertificateProvider(new CertificateRegistry(_pki.Directory));
        var defaults = new ExecutionDefaults { CaBundlePath = _pki.CaPemPath };
        HttpRequestSpec Spec(params string[] matches) => new()
        {
            Url = first.Url("redirect-to?url=" + Uri.EscapeDataString(asking.Url("echo"))),
            Certificate = new CertificateSpec { Name = "pinned", CertSrc = _pki.ClientCertPemPath, KeySrc = _pki.ClientKeyPemPath, Matches = [.. matches] },
        };

        var unpatterned = await new HttpExecutor().SendAsync(Prepare(Spec(), defaults, provider), _ct);
        Assert.Null(Json(unpatterned)["clientCertificateThumbprint"].GetString());

        // URL patterns say where else it goes.
        var patterned = await new HttpExecutor().SendAsync(Prepare(Spec($"https://localhost:{first.BaseUri.Port}/*", $"https://localhost:{asking.BaseUri.Port}/*"), defaults, provider), _ct);
        Assert.Equal(_pki.Client.Thumbprint, Json(patterned)["clientCertificateThumbprint"].GetString());
    }

    [Fact]
    public async Task A_certificate_file_that_cannot_be_read_is_reported_and_the_request_sent_without_it()
    {
        await using var plain = await EchoServer.StartAsync();
        await using var secure = await EchoServer.StartAsync(_pki.Server);
        using var provider = new ClientCertificateProvider(new CertificateRegistry(_pki.Directory)
        {
            Entries = [new CertificateEntry { Name = "locked", Matches = [$"https://localhost:{secure.BaseUri.Port}/*"], Cert = new FileSource { Src = "client.pem" }, Key = new FileSource { Src = "client.key" } }],
        });
        var defaults = new ExecutionDefaults { CaBundlePath = _pki.CaPemPath };
        using var locked = new FileStream(_pki.ClientKeyPemPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // Selected when the request is prepared …
        var direct = Prepare(new HttpRequestSpec { Url = secure.Url("echo") }, defaults, provider);
        Assert.Null(direct.ClientCertificate);
        Assert.StartsWith("Client certificate 'locked' not used: ", Assert.Single(direct.Warnings), StringComparison.Ordinal);

        // … or when a redirect leads to its URL.
        var redirected = Prepare(new HttpRequestSpec { Url = plain.Url("redirect-to?url=" + Uri.EscapeDataString(secure.Url("echo"))) }, defaults, provider);
        var exchange = await new HttpExecutor().SendAsync(redirected, _ct);
        Assert.Null(Json(exchange)["clientCertificateThumbprint"].GetString());
        Assert.StartsWith("Client certificate 'locked' not used: ", Assert.Single(redirected.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_ca_bundle_does_not_make_a_certificate_issued_for_other_purposes_a_server_certificate()
    {
        // Kestrel refuses a certificate without the server-auth usage, so a bare TLS listener presents it.
        using var clientAuthOnly = _pki.IssueLocalhost("1.3.6.1.5.5.7.3.2");
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(_ct);
            await using var tls = new System.Net.Security.SslStream(connection.GetStream());
            await tls.AuthenticateAsServerAsync(clientAuthOnly);
            var request = new byte[4096];
            var read = 0;
            int count;
            while (request.AsSpan(0, read).IndexOf("\r\n\r\n"u8) < 0 && (count = await tls.ReadAsync(request.AsMemory(read), _ct)) > 0)
            {
                read += count;
            }

            await tls.WriteAsync("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray(), _ct);
        }, _ct);
        var url = $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";

        var exchange = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec { Url = url }, new ExecutionDefaults { CaBundlePath = _pki.CaPemPath }), _ct);

        Assert.Null(exchange.Response);
        var tls = Assert.Single(exchange.Tls);
        Assert.False(tls.Accepted, tls.Describe());
        Assert.Contains("rejected", exchange.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Imports the client certificate into the Windows CurrentUser\My store with a NON-EXPORTABLE key, uses it by
    /// thumbprint, then removes it. Opt-in because it modifies the user's certificate store.
    /// </summary>
    [Fact]
    public async Task Mutual_tls_with_non_exportable_key_from_the_windows_store()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("TERMREST_STORE_TESTS") == "1",
            "Set TERMREST_STORE_TESTS=1 on Windows to run (temporarily adds a certificate to CurrentUser\\My).");

        using var stored = X509CertificateLoader.LoadPkcs12FromFile(_pki.ClientPfxPath, TestPki.Passphrase, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(stored);
        try
        {
            await using var server = await EchoServer.StartAsync(_pki.Server, requireClientCertificate: true);
            var registry = new CertificateRegistry(_pki.Directory)
            {
                Entries = [new CertificateEntry { Name = "store", Matches = ["https://localhost/*"], Store = new StoreSource { Thumbprint = stored.Thumbprint } }],
            };
            using var provider = new ClientCertificateProvider(registry);

            var exchange = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("echo") }, new ExecutionDefaults { CaBundlePath = _pki.CaPemPath }, provider), _ct);

            Assert.Equal(_pki.Client.Thumbprint, Json(exchange)["clientCertificateThumbprint"].GetString());
            Assert.Throws<System.Security.Cryptography.CryptographicException>(() =>
            {
                using var fromStore = CertificateLoader.LoadFromStore(new StoreSource { Thumbprint = stored.Thumbprint });
                fromStore.Certificate.Export(X509ContentType.Pkcs12);
            });
        }
        finally
        {
            store.Remove(stored);
        }
    }

    [Fact]
    public async Task Body_formatter_pretty_prints_json_and_xml()
    {
        await using var server = await EchoServer.StartAsync();
        var executor = new HttpExecutor();

        var json = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("echo") }), _ct);
        var pretty = BodyFormatter.Format(json.Response!.Body, json.Response.ContentType);
        Assert.Contains("\n  \"method\": \"GET\"", pretty, StringComparison.Ordinal);
        Assert.Equal("json", BodyFormatter.Language(json.Response.Body, json.Response.ContentType));

        var xml = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("xml") }), _ct);
        Assert.Contains("\n  <item id=\"1\">x</item>", BodyFormatter.Format(xml.Response!.Body, xml.Response.ContentType), StringComparison.Ordinal);

        Assert.StartsWith("00000000  00 01 ff", BodyFormatter.Format([0, 1, 255], "application/octet-stream"), StringComparison.Ordinal);
    }

    [Fact]
    public void Office_documents_are_binary_though_their_types_name_xml_formats()
    {
        // The start of a ZIP file, which .xlsx, .docx and .pptx files are.
        byte[] zip = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00, 0x08, 0x00, 0x00, 0x00, 0x21, 0x00, 0x8C, 0x5A, 0xED, 0xC1];
        foreach (var office in (string[])[
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation"])
        {
            Assert.True(BodyFormatter.IsBinary(zip, office), office);
            Assert.StartsWith("00000000  50 4b 03 04", BodyFormatter.Format(zip, office), StringComparison.Ordinal);
        }

        // A type is text when a word of its subtype names a text format.
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect/></svg>"u8.ToArray();
        Assert.False(BodyFormatter.IsBinary(svg, "image/svg+xml"));
        Assert.Contains("\n  <rect />", BodyFormatter.Format(svg, "image/svg+xml"), StringComparison.Ordinal);
        string[] text = ["application/atom+xml; charset=utf-8", "application/problem+json", "application/x-ndjson", "application/x-amz-json-1.1", "text/csv"];
        Assert.DoesNotContain(text, type => BodyFormatter.IsBinary("x"u8.ToArray(), type));
    }

    [Fact]
    public void Xml_with_a_document_type_is_shown_as_received_without_expanding_its_entities()
    {
        // A few hundred bytes whose entities expand to millions of characters.
        var entities = string.Concat(Enumerable.Range(1, 6).Select(i => $"<!ENTITY e{i} \"{string.Concat(Enumerable.Repeat($"&e{i - 1};", 10))}\">"));
        var bomb = $"<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY e0 \"<x/>\">{entities}]><r>&e6;</r>";

        Assert.Equal(bomb, BodyFormatter.Format(Encoding.UTF8.GetBytes(bomb), "application/xml"));
        Assert.Equal("<a>\n  <b>x</b>\n</a>", BodyFormatter.Format("<a> <b>x</b> </a>"u8.ToArray(), "application/xml").ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task A_content_type_dotnet_cannot_parse_is_read_as_the_server_sent_it()
    {
        // A trailing ";", as PHP's header() and servlets write it: .NET reads no type from it, Postman reads its charset.
        await using var server = await EchoServer.StartAsync();
        const string Type = "text/html; charset=ISO-8859-1;";

        var exchange = await new HttpExecutor().SendAsync(Prepare(new HttpRequestSpec { Url = server.Url($"bytes/636166E9?type={Uri.EscapeDataString(Type)}") }), _ct);

        Assert.Equal(Type, exchange.Response!.ContentType);
        Assert.Equal("café", BodyFormatter.Format(exchange.Response.Body, exchange.Response.ContentType));
    }

    [Fact]
    public void Body_formatter_decodes_legacy_charsets()
    {
        // Not built into .NET: history --show, send, the response pane and scripts read them with the code page tables.
        Assert.Equal("café", BodyFormatter.Format([.. "caf"u8, 0xE9], "text/html; charset=windows-1252"));
        Assert.Equal("日本", BodyFormatter.Format([0x93, 0xFA, 0x96, 0x7B], "text/plain; charset=Shift_JIS"));

        // UTF-16 without a byte order mark, in either byte order (RFC 2781).
        Assert.Equal("<a>hi</a>", BodyFormatter.Format(Encoding.BigEndianUnicode.GetBytes("<a>hi</a>"), "text/xml; charset=utf-16", pretty: false));
        Assert.Equal("<a>hi</a>", BodyFormatter.Format(Encoding.Unicode.GetBytes("<a>hi</a>"), "text/xml; charset=utf-16", pretty: false));
    }

    [Fact]
    public async Task A_body_of_known_length_is_read_into_one_buffer_of_that_size()
    {
        var data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4 * 1024 * 1024);
        async Task<(byte[] Body, bool Truncated, long Allocated)> Read(HttpResponseMessage response, long maxBytes)
        {
            // Content over a MemoryStream completes synchronously, so this thread does all the allocations.
            var before = GC.GetAllocatedBytesForCurrentThread();
            var task = HttpExecutor.ReadBodyAsync(response, maxBytes, _ct);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(task.IsCompletedSuccessfully);
            var (body, truncated, _, _) = await task;
            return (body, truncated, allocated);
        }

        using var full = new HttpResponseMessage { Content = new StreamContent(new MemoryStream(data)) };
        var (body, truncated, allocated) = await Read(full, 50L * 1024 * 1024);
        Assert.True(body.AsSpan().SequenceEqual(data) && !truncated);
        Assert.True(allocated < data.Length + 1.25 * 1024 * 1024, $"{allocated:N0} bytes allocated to read {data.Length:N0}"); // and a buffer for the first megabyte

        // A declared length is not allocated before the body arrives.
        using var lying = new HttpResponseMessage { Content = new StreamContent(new MemoryStream(data, 0, 16 * 1024)) };
        lying.Content.Headers.ContentLength = 8L * data.Length;
        (body, truncated, allocated) = await Read(lying, 50L * 1024 * 1024);
        Assert.True(body.AsSpan().SequenceEqual(data.AsSpan(0, 16 * 1024)) && !truncated);
        Assert.True(allocated < 1.25 * 1024 * 1024, $"{allocated:N0} bytes allocated to read 16 KB of a declared {8L * data.Length:N0}");

        using var cut = new HttpResponseMessage { Content = new StreamContent(new MemoryStream(data)) };
        (body, truncated, allocated) = await Read(cut, 1024 * 1024);
        Assert.True(body.AsSpan().SequenceEqual(data.AsSpan(0, 1024 * 1024)) && truncated);
        Assert.True(allocated < 1.25 * 1024 * 1024, $"{allocated:N0} bytes allocated to read 1 MB");

        // HEAD responses declare the length of a body they do not have.
        using var head = new HttpResponseMessage { Content = new ByteArrayContent([]), RequestMessage = new HttpRequestMessage(HttpMethod.Head, "http://localhost/") };
        head.Content.Headers.ContentLength = data.Length;
        (body, _, allocated) = await Read(head, 50L * 1024 * 1024);
        Assert.Empty(body);
        Assert.True(allocated < 256 * 1024, $"{allocated:N0} bytes allocated to read an empty body");
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("x-gzip")]
    [InlineData("deflate")] // zlib data, as the standard has it
    [InlineData("deflate", "raw-deflate")] // as some servers send it
    [InlineData("br")]
    [InlineData("deflate, gzip")] // applied in that order
    [InlineData("identity")]
    public async Task Bodies_are_decoded_as_their_content_encoding_says(string header, string? applied = null)
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"id\":1,\"name\":\"item\"},", 500)));
        var encoded = (applied ?? header).Split(", ").Aggregate(data, (bytes, coding) => coding == "identity" ? bytes : Encode(coding, bytes));
        using var response = new HttpResponseMessage { Content = new ByteArrayContent(encoded) };
        response.Content.Headers.TryAddWithoutValidation("Content-Encoding", header);

        var (body, truncated, received, warning) = await HttpExecutor.ReadBodyAsync(response, 50L * 1024 * 1024, _ct);

        Assert.Equal(data, body);
        Assert.Equal((false, encoded.Length, null), (truncated, received, warning));
    }

    [Fact]
    public async Task A_compressed_body_is_decoded_into_one_buffer_of_its_size_and_within_the_size_limit()
    {
        var data = new byte[8 * 1024 * 1024];
        var gzip = Encode("gzip", data);
        async Task<(byte[] Body, bool Truncated, long Allocated)> Read(long maxBytes)
        {
            using var response = new HttpResponseMessage { Content = new ByteArrayContent(gzip) };
            response.Content.Headers.ContentEncoding.Add("gzip");
            var before = GC.GetAllocatedBytesForCurrentThread();
            var task = HttpExecutor.ReadBodyAsync(response, maxBytes, _ct);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(task.IsCompletedSuccessfully);
            var (body, truncated, received, warning) = await task;
            Assert.Equal((gzip.Length, null), (received, warning));
            return (body, truncated, allocated);
        }

        var (body, truncated, allocated) = await Read(50L * 1024 * 1024);
        Assert.True(body.AsSpan().SequenceEqual(data) && !truncated);
        Assert.True(allocated < gzip.Length + data.Length + 1.5 * 1024 * 1024, $"{allocated:N0} bytes allocated to decode {data.Length:N0}");

        // A small body that decodes to a huge one stops at the limit.
        (body, truncated, allocated) = await Read(1024 * 1024);
        Assert.True(body.Length == 1024 * 1024 && truncated);
        Assert.True(allocated < gzip.Length + 1.5 * 1024 * 1024, $"{allocated:N0} bytes allocated to decode 1 MB");
    }

    [Fact]
    public async Task Accept_encoding_is_sent_unless_typed_and_a_compressed_response_keeps_its_headers()
    {
        await using var server = await EchoServer.StartAsync();
        var executor = new HttpExecutor();
        var data = "{\"compressed\":true}"u8.ToArray();
        var gzip = Encode("gzip", data);

        var generated = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("echo") }), _ct);
        var typed = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url("echo"), Headers = [new() { Key = "Accept-Encoding", Value = "identity" }] }), _ct);
        var compressed = await executor.SendAsync(Prepare(new HttpRequestSpec { Url = server.Url($"bytes/{Convert.ToHexString(gzip)}?type=application/json&encoding=gzip") }), _ct);

        Assert.Equal("gzip, deflate, br", Json(generated)["headers"]!["accept-encoding"].GetString());
        Assert.Equal("gzip, deflate, br", generated.SentHeaders.Single(h => h.Key == "Accept-Encoding").Value);
        Assert.Equal("identity", Json(typed)["headers"]!["accept-encoding"].GetString());
        Assert.Equal("identity", typed.SentHeaders.Single(h => h.Key == "Accept-Encoding").Value);
        var response = compressed.Response!;
        Assert.Equal(data, response.Body);
        Assert.Equal(("gzip", gzip.Length.ToString(CultureInfo.InvariantCulture)), (response.GetHeader("Content-Encoding"), response.GetHeader("Content-Length")));
        Assert.Equal(gzip.Length, response.ReceivedBodySize);
        Assert.Empty(compressed.Request.Warnings);
    }

    [Theory]
    [InlineData("gzip", "The body could not be decoded (Content-Encoding: gzip): it is not valid gzip data. It is kept as received.")]
    [InlineData("br", "The body could not be decoded (Content-Encoding: br): it is not valid br data. It is kept as received.")]
    [InlineData("zstd", "The body is kept as received: TermRest cannot decode Content-Encoding 'zstd'.")]
    public async Task A_body_that_cannot_be_decoded_is_kept_as_received_with_the_status_and_headers(string coding, string warning)
    {
        await using var server = await EchoServer.StartAsync();
        var plain = "{\"plain\":true}"u8.ToArray();
        var prepared = Prepare(new HttpRequestSpec { Url = server.Url($"bytes/{Convert.ToHexString(plain)}?type=application/json&encoding={coding}") });

        var exchange = await new HttpExecutor().SendAsync(prepared, _ct);

        Assert.True(exchange.IsSuccess, exchange.ErrorMessage);
        Assert.Equal((200, coding), (exchange.Response!.StatusCode, exchange.Response.GetHeader("Content-Encoding")));
        Assert.Equal(plain, exchange.Response.Body);
        Assert.Equal(warning, Assert.Single(prepared.Warnings));
    }

    [Fact]
    public void Curl_export_quotes_values()
    {
        var prepared = Prepare(new HttpRequestSpec
        {
            Method = "POST",
            Url = "https://api.example.com/x?q=it's",
            Headers = [new() { Key = "Accept", Value = "application/json" }],
            Body = new RequestBody { Mode = BodyMode.Raw, Raw = "{\"a\":\"it's\"}", RawLanguage = "json" },
            Settings = new RequestSettings { StrictSsl = false },
        });

        var curl = CurlExporter.Export(prepared, multiline: false);

        Assert.Equal(
            "curl 'https://api.example.com/x?q=it'\\''s' -H 'Accept: application/json' --data-raw '{\"a\":\"it'\\''s\"}' -H 'Content-Type: application/json' --insecure --location --max-redirs 10 --proto-redir '=https'",
            curl);
    }

    [Fact]
    public void Curl_export_gives_client_certificate_files_with_their_full_paths()
    {
        // Relative to the workspace (registry) or to the collection's folder (pinned on a request), not to curl's folder.
        using var provider = new ClientCertificateProvider(new CertificateRegistry(_pki.Directory)
        {
            Entries =
            [
                new CertificateEntry { Name = "pem", Matches = ["https://pem.example/*"], Cert = new FileSource { Src = "client.pem" }, Key = new FileSource { Src = "client.key" } },
                new CertificateEntry { Name = "pfx", Matches = ["https://pfx.example/*"], Pfx = new FileSource { Src = "client.pfx" }, Passphrase = TestPki.Passphrase },
            ],
        });
        var collection = Directory.CreateDirectory(Path.Combine(_pki.Directory, "collection")).FullName;
        File.Copy(_pki.ClientPfxPath, Path.Combine(collection, "pinned.pfx"));
        string Export(HttpRequestSpec spec)
        {
            var prepared = new RequestPreparer(VariableResolver.Empty, provider, new ExecutionDefaults()).Prepare(spec, null, collection);
            Assert.NotNull(prepared.ClientCertificate);
            return CurlExporter.Export(prepared, multiline: false);
        }

        Assert.EndsWith($" --cert '{_pki.ClientCertPemPath}' --key '{_pki.ClientKeyPemPath}'", Export(new HttpRequestSpec { Url = "https://pem.example/" }), StringComparison.Ordinal);
        Assert.EndsWith($" --cert-type P12 --cert '{_pki.ClientPfxPath}'", Export(new HttpRequestSpec { Url = "https://pfx.example/" }), StringComparison.Ordinal);
        var pinned = new HttpRequestSpec { Url = "https://pem.example/", Certificate = new CertificateSpec { PfxSrc = "pinned.pfx", Passphrase = TestPki.Passphrase } };
        Assert.EndsWith($" --cert-type P12 --cert '{Path.Combine(collection, "pinned.pfx")}'", Export(pinned), StringComparison.Ordinal);
    }

    [Fact]
    public void Curl_export_follows_no_redirects_with_a_client_certificate_file()
    {
        using var provider = new ClientCertificateProvider(new CertificateRegistry(_pki.Directory)
        {
            Entries = [new CertificateEntry { Name = "pem", Matches = ["https://pem.example/*"], Cert = new FileSource { Src = "client.pem" }, Key = new FileSource { Src = "client.key" } }],
        });
        var apikey = new AuthSpec { Type = "apikey" };
        apikey.Set("key", "X-API-Key");
        apikey.Set("value", "k1");
        string Export(AuthSpec? auth) => CurlExporter.Export(
            new RequestPreparer(VariableResolver.Empty, provider, new ExecutionDefaults()).Prepare(new HttpRequestSpec { Url = "https://pem.example/", Auth = auth }, auth, ""),
            multiline: false);

        // curl presents the certificate it is given on every connection; TermRest selects one for each URL a redirect leads to.
        Assert.Equal(
            "# redirects are not followed: curl would present the client certificate to every host it is redirected to\n"
            + $"curl 'https://pem.example/' --cert '{_pki.ClientCertPemPath}' --key '{_pki.ClientKeyPemPath}'",
            Export(null));
        Assert.StartsWith(
            "# redirects are not followed: curl would send 'X-API-Key' and present the client certificate to every host it is redirected to\ncurl ",
            Export(apikey), StringComparison.Ordinal);
    }

    public void Dispose() => _pki.Dispose();
}
