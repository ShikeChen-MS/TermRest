using System.Text.Json.Nodes;
using TermRest.Core.Json;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Tests.Infrastructure;
using Xunit;

namespace TermRest.Core.Tests;

public class PostmanRequestMapperTests
{
    private static (PostmanCollection Collection, RequestNode Node, string Original) Load(string file, string requestPath)
    {
        var path = TestFiles.CopyToTemp(file);
        var collection = PostmanCollection.Load(path);
        var node = collection.FindRequest(requestPath) ?? throw new InvalidOperationException($"{requestPath} not found");
        return (collection, node, collection.Serialize());
    }

    [Fact]
    public void Reads_query_parameters_including_disabled_ones()
    {
        var (_, node, _) = Load(TestFiles.DemoCollection, "Basics/GET with query");

        var spec = node.ReadSpec();

        Assert.Equal("GET", spec.Method);
        Assert.Equal("{{baseUrl}}/get?foo=bar&source=termrest", spec.Url);
        Assert.Equal(["foo", "source", "debug"], spec.QueryParams.Select(q => q.Key));
        Assert.False(spec.QueryParams[2].Enabled);
        Assert.Equal("Disabled parameters are kept but not sent", spec.QueryParams[2].Description);
        Assert.Single(spec.Headers);
    }

    [Fact]
    public void Unchanged_spec_does_not_modify_the_document()
    {
        var (collection, node, original) = Load(Data("complex.postman_collection.json"), "Folder A/Nested folder/Deep request");

        var changed = collection.UpdateRequest(node, node.ReadSpec());

        Assert.False(changed);
        Assert.False(collection.IsDirty);
        Assert.Equal(original, collection.Serialize());
    }

    [Fact]
    public void Changing_only_the_method_rewrites_only_the_method()
    {
        var (collection, node, original) = Load(Data("complex.postman_collection.json"), "Folder A/Nested folder/Deep request");
        var spec = node.ReadSpec();
        spec.Method = "PUT";

        collection.UpdateRequest(node, spec);

        var changes = TestFiles.ChangedLines(original, collection.Serialize());
        Assert.Equal(["- \"method\": \"PATCH\",", "+ \"method\": \"PUT\","], changes);
        Assert.True(collection.IsDirty);
    }

    [Fact]
    public void Editing_the_url_regenerates_structured_parts_and_keeps_unknown_properties()
    {
        var (collection, node, _) = Load(TestFiles.DemoCollection, "Basics/GET with query");
        var spec = node.ReadSpec();
        spec.Url = "https://api.contoso.com:8443/v2/items?foo=baz&source=termrest";
        spec.QueryParams = PostmanUrl.SyncQueryFromRaw(spec.Url, spec.QueryParams);

        collection.UpdateRequest(node, spec);

        var url = (JsonObject)node.Json["request"]!["url"]!;
        Assert.Equal("https://api.contoso.com:8443/v2/items?foo=baz&source=termrest", url["raw"].GetString());
        Assert.Equal("https", url["protocol"].GetString());
        Assert.Equal(["api", "contoso", "com"], url["host"]!.AsArray().Select(n => n.GetString()));
        Assert.Equal("8443", url["port"].GetString());
        Assert.Equal(["v2", "items"], url["path"]!.AsArray().Select(n => n.GetString()));
        var query = url["query"]!.AsArray().Select(n => (JsonObject)n!).ToList();
        Assert.Equal(["foo", "source", "debug"], query.Select(q => q["key"].GetString()));
        Assert.Equal("baz", query[0]["value"].GetString());
        Assert.True(query[2]["disabled"].GetBool());
        Assert.Equal("Disabled parameters are kept but not sent", query[2]["description"].GetString());

        // Re-reading yields the same spec (stable round trip).
        var reread = PostmanCollection.Parse(collection.Serialize()).FindRequest("Basics/GET with query")!.ReadSpec();
        Assert.True(reread.ContentEquals(spec));
    }

    [Fact]
    public void Disabling_a_parameter_keeps_it_in_the_query_array_but_not_in_raw()
    {
        var (collection, node, _) = Load(TestFiles.DemoCollection, "Basics/GET with query");
        var spec = node.ReadSpec();
        spec.QueryParams[1].Enabled = false;
        spec.Url = PostmanUrl.SyncRawFromQuery(spec.Url, spec.QueryParams);

        collection.UpdateRequest(node, spec);

        var url = node.Json["request"]!["url"]!;
        Assert.Equal("{{baseUrl}}/get?foo=bar", url["raw"].GetString());
        Assert.True(url["query"]![1]!["disabled"].GetBool());
    }

    [Fact]
    public void Adding_a_header_writes_postman_style_entry()
    {
        var (collection, node, _) = Load(TestFiles.DemoCollection, "Basics/POST JSON");
        var spec = node.ReadSpec();
        spec.Headers.Add(new KeyValueEntry { Key = "X-Trace", Value = "{{$guid}}" });

        collection.UpdateRequest(node, spec);

        var header = (JsonObject)node.Json["request"]!["header"]![0]!;
        Assert.Equal("{\"key\":\"X-Trace\",\"value\":\"{{$guid}}\",\"type\":\"text\"}", header.ToJsonString());
    }

    [Fact]
    public void Header_edits_preserve_unknown_properties()
    {
        var (collection, node, _) = Load(Data("complex.postman_collection.json"), "Folder A/Nested folder/Deep request");
        var spec = node.ReadSpec();
        spec.Headers[2].Value = "2";

        collection.UpdateRequest(node, spec);

        var header = (JsonObject)node.Json["request"]!["header"]![2]!;
        Assert.Equal("2", header["value"].GetString());
        Assert.NotNull(header["x-custom"]);
        Assert.True(node.Json["request"]!["header"]![0]!["disabled"].GetBool());
    }

    [Fact]
    public void Switching_body_mode_drops_stale_mode_data()
    {
        var (collection, node, _) = Load(TestFiles.DemoCollection, "Basics/POST JSON");
        var spec = node.ReadSpec();
        spec.Body.Mode = BodyMode.UrlEncoded;
        spec.Body.UrlEncoded = [new KeyValueEntry { Key = "a", Value = "1" }];

        collection.UpdateRequest(node, spec);

        var body = (JsonObject)node.Json["request"]!["body"]!;
        Assert.Equal("urlencoded", body["mode"].GetString());
        Assert.Null(body["raw"]);
        Assert.Null(body["options"]);
        Assert.Equal("text", body["urlencoded"]![0]!["type"].GetString());
    }

    [Fact]
    public void Raw_body_language_is_written_to_options()
    {
        var (collection, node, _) = Load(TestFiles.DemoCollection, "Basics/Form URL-encoded");
        var spec = node.ReadSpec();
        spec.Body.Mode = BodyMode.Raw;
        spec.Body.Raw = "<a/>";
        spec.Body.RawLanguage = "xml";

        collection.UpdateRequest(node, spec);

        Assert.Equal("{\n\t\"mode\": \"raw\",\n\t\"raw\": \"<a/>\",\n\t\"options\": {\n\t\t\"raw\": {\n\t\t\t\"language\": \"xml\"\n\t\t}\n\t}\n}", PostmanJson.Serialize(node.Json["request"]!["body"]!));
    }

    [Fact]
    public void File_body_type_is_kept_in_the_file_object()
    {
        var (collection, node, _) = Load(TestFiles.DemoCollection, "Basics/Form URL-encoded");
        var spec = node.ReadSpec();
        spec.Body.Mode = BodyMode.File;
        spec.Body.FileSrc = "payloads/order.json";
        spec.Body.FileLanguage = "json";

        collection.UpdateRequest(node, spec);

        Assert.Equal("{\n\t\"mode\": \"file\",\n\t\"file\": {\n\t\t\"src\": \"payloads/order.json\",\n\t\t\"language\": \"json\"\n\t}\n}", PostmanJson.Serialize(node.Json["request"]!["body"]!));
        var binary = node.ReadSpec();
        Assert.Equal("json", binary.Body.FileLanguage);
        binary.Body.FileLanguage = null;
        collection.UpdateRequest(node, binary);
        Assert.Null(node.Json["request"]!["body"]!["file"]!["language"]);

        // A type TermRest does not know is read as binary, and kept as it is unless the type is changed.
        node.Json["request"]!["body"]!["file"]!["language"] = "yaml";
        var unknown = node.ReadSpec();
        Assert.Null(unknown.Body.FileLanguage);
        unknown.Body.FileSrc = "payloads/order.yaml";
        collection.UpdateRequest(node, unknown);
        Assert.Equal(("payloads/order.yaml", "yaml"), (node.Json["request"]!["body"]!["file"]!["src"].GetString(), node.Json["request"]!["body"]!["file"]!["language"].GetString()));
    }

    [Fact]
    public void Setting_auth_to_inherit_removes_the_auth_block()
    {
        var (collection, node, _) = Load(TestFiles.DemoCollection, "Auth/Basic auth");
        var spec = node.ReadSpec();
        Assert.Equal("basic", spec.Auth!.Type);
        Assert.Equal("postman", spec.Auth.Get("username"));

        spec.Auth = null;
        collection.UpdateRequest(node, spec);

        Assert.Null(node.Json["request"]!["auth"]);
        Assert.Equal("bearer", collection.GetEffectiveAuth(node)!.Type);
    }

    [Fact]
    public void Inherited_auth_resolves_from_folder_then_collection()
    {
        var collection = PostmanCollection.Load(Data("complex.postman_collection.json"));

        Assert.Equal("apikey", collection.GetEffectiveAuth(collection.FindRequest("Folder A/GraphQL")!)!.Type);
        Assert.Equal("bearer", collection.GetEffectiveAuth(collection.FindRequest("Short form url")!)!.Type);
        Assert.Equal("oauth2", collection.GetEffectiveAuth(collection.FindRequest("OAuth2 with token")!)!.Type);
    }

    [Fact]
    public void Auth_params_keep_non_string_values()
    {
        var (collection, node, _) = Load(Data("complex.postman_collection.json"), "OAuth2 with token");
        var spec = node.ReadSpec();
        spec.Auth!.Set("accessToken", "new-token");

        collection.UpdateRequest(node, spec);

        var parameters = node.Json["request"]!["auth"]!["oauth2"]!.AsArray();
        Assert.Equal("new-token", parameters[0]!["value"].GetString());
        Assert.Equal("false", parameters[2]!["value"]!.ToJsonString());
        Assert.Equal("{\"scope\":\"read\"}", parameters[3]!["value"]!.ToJsonString());
    }

    [Fact]
    public void Legacy_v20_auth_is_read_and_written_in_v20_shape()
    {
        var (collection, node, _) = Load(Data("legacy-v20.postman_collection.json"), "Basic v2.0");
        Assert.Equal(PostmanSchemaVersion.V20, collection.SchemaVersion);
        var spec = node.ReadSpec();
        Assert.Equal("ada", spec.Auth!.Get("username"));

        spec.Auth.Set("username", "grace");
        collection.UpdateRequest(node, spec);

        var basic = (JsonObject)node.Json["request"]!["auth"]!["basic"]!;
        Assert.Equal("grace", basic["username"].GetString());
        Assert.True(basic["saveHelperData"].GetBool());
    }

    [Fact]
    public void Short_form_url_stays_a_string_when_possible()
    {
        var (collection, node, _) = Load(Data("complex.postman_collection.json"), "Short form url");
        var spec = node.ReadSpec();
        spec.Url = "https://example.com/plain?x=2";
        spec.QueryParams = PostmanUrl.SyncQueryFromRaw(spec.Url, spec.QueryParams);

        collection.UpdateRequest(node, spec);

        Assert.Equal("https://example.com/plain?x=2", node.Json["request"]!["url"].GetString());
    }

    [Fact]
    public void String_request_is_upgraded_to_an_object_when_edited()
    {
        var (collection, node, _) = Load(Data("complex.postman_collection.json"), "String request");
        var spec = node.ReadSpec();
        Assert.Equal("https://example.com/string-request", spec.Url);
        spec.Method = "DELETE";

        collection.UpdateRequest(node, spec);

        var request = (JsonObject)node.Json["request"]!;
        Assert.Equal("DELETE", request["method"].GetString());
        Assert.Equal("https://example.com/string-request", request["url"].GetString());
    }

    [Fact]
    public void Scripts_are_read_and_written_as_exec_lines()
    {
        var (collection, node, _) = Load(Data("complex.postman_collection.json"), "Folder A/Nested folder/Deep request");
        var spec = node.ReadSpec();
        Assert.Contains("pm.response.to.have.status(200);", spec.TestScript, StringComparison.Ordinal);

        spec.TestScript = "pm.test('ok', () => {});\npm.expect(1).to.eql(1);";
        collection.UpdateRequest(node, spec);

        var test = node.Json["event"]!.AsArray().First(e => e!["listen"].GetString() == "test")!;
        Assert.Equal(["pm.test('ok', () => {});", "pm.expect(1).to.eql(1);"], test["script"]!["exec"]!.AsArray().Select(l => l.GetString()));
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001", node.Json["event"]![0]!["script"]!["id"].GetString());
    }

    [Fact]
    public void Form_data_files_and_settings_round_trip()
    {
        var (_, node, _) = Load(Data("complex.postman_collection.json"), "Folder A/Nested folder/Deep request");
        var spec = node.ReadSpec();

        Assert.Equal(BodyMode.FormData, spec.Body.Mode);
        Assert.Equal("files/a.txt\nfiles/b.bin", spec.Body.FormData[2].Src);
        Assert.False(spec.Body.FormData[3].Enabled);
        Assert.Equal("application/json", spec.Body.FormData[3].ContentType);
        Assert.False(spec.Settings.StrictSsl);
        Assert.False(spec.Settings.FollowRedirects);
        Assert.Equal("contoso", spec.PathVariables.Single(v => v.Key == "tenant").Value);
        Assert.Equal("tenant id", spec.PathVariables[0].Description);
        Assert.Null(spec.QueryParams[0].Value);
    }

    private static string Data(string name) => TestFiles.Data(name);
}
