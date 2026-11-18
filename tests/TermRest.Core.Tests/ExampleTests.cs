using System.Text.Json.Nodes;
using TermRest.Core.Http;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using Xunit;

namespace TermRest.Core.Tests;

public class ExampleTests
{
    private const string ListExample = "Widgets/.resources/List widgets.resources/examples/200 OK.example.yaml";

    private static PostmanCollection OpenV3(string root) => Assert.Single(ApiWorkspace.Open(root).Collections);

    private static string F(string root, string path) => Path.Combine(V3Fixture.CollectionPath(root), path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The lines of <paramref name="after"/> that are not in <paramref name="before"/>, and the reverse.</summary>
    private static (string[] Added, string[] Removed) Diff(string before, string after)
    {
        var old = before.Split('\n');
        var now = after.Split('\n');
        return ([.. now.Except(old)], [.. old.Except(now)]);
    }

    [Fact]
    public void V3_examples_are_read_when_first_needed_and_saved_where_they_changed_only()
    {
        var root = V3Fixture.Create();
        var collection = OpenV3(root);
        var list = collection.FindRequest("Widgets/List widgets")!;
        var health = collection.FindRequest("Health")!;

        // Known from the request's examples reference, without reading the example files.
        Assert.True(collection.HasExamples(list));
        Assert.False(collection.HasExamples(health));
        Assert.Empty((JsonArray)list.Json["response"]!);

        var example = Assert.Single(collection.GetExamples(list));
        var spec = example.ReadSpec();
        Assert.Equal(("200 OK", 200, "OK", "[]", "json"), (spec.Name, spec.Code, spec.Status, spec.Body, spec.Language));
        Assert.Equal(["Content-Type: application/json"], spec.Headers.Select(h => $"{h.Key}: {h.Value}"));
        Assert.Equal("GET", spec.Request.Method);
        Assert.Same(example, Assert.Single(collection.GetExamples(list)));

        var before = File.ReadAllText(F(root, ListExample));
        var request = File.ReadAllText(F(root, "Widgets/List widgets.request.yaml"));
        spec.Body = "[{\"id\": 1}]";
        Assert.True(collection.UpdateExample(example, spec));
        collection.Save();

        var (added, removed) = Diff(before, File.ReadAllText(F(root, ListExample)));
        Assert.Equal(["    content: '[{\"id\": 1}]'"], added);
        Assert.Equal(["    content: '[]'"], removed);
        Assert.Equal(request, File.ReadAllText(F(root, "Widgets/List widgets.request.yaml")));

        var reopened = OpenV3(root);
        Assert.Equal("[{\"id\": 1}]", Assert.Single(reopened.GetExamples(reopened.FindRequest("Widgets/List widgets")!)).ReadSpec().Body);
    }

    [Fact]
    public void A_new_v3_example_gets_a_file_named_for_it_and_its_request_refers_to_the_folder()
    {
        var root = V3Fixture.Create();
        var collection = OpenV3(root);
        var health = collection.FindRequest("Health")!;
        var spec = new ExampleSpec
        {
            Name = "Up: all/good",
            Request = health.ReadSpec(),
            Code = 200,
            Status = "OK",
            Headers = [new KeyValueEntry { Key = "Content-Type", Value = "application/json" }],
            Body = "{\"status\": \"up\"}",
            Language = "json",
        };
        collection.AddExample(health, spec);
        collection.Save();

        var file = F(root, ".resources/Health.resources/examples/Up- all-good.example.yaml");
        Assert.StartsWith("$kind: http-example\nname: \"Up: all/good\"\nrequest:\n  url: \"{{baseUrl}}/health\"\n  method: GET\nresponse:\n  statusCode: 200\n",
            File.ReadAllText(file), StringComparison.Ordinal);
        Assert.EndsWith("order: 1000\n", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Contains("examples: ./.resources/Health.resources/examples\n", File.ReadAllText(F(root, "Health.request.yaml")), StringComparison.Ordinal);

        var reloaded = OpenV3(root);
        var read = Assert.Single(reloaded.GetExamples(reloaded.FindRequest("Health")!)).ReadSpec();
        Assert.Equal(("Up: all/good", 200, "{\"status\": \"up\"}", "json"), (read.Name, read.Code, read.Body, read.Language));
        Assert.Equal("{{baseUrl}}/health", read.Request.Url);
    }

    [Fact]
    public void Renamed_and_deleted_v3_examples_rename_and_delete_their_files()
    {
        var root = V3Fixture.Create();
        var collection = OpenV3(root);
        var list = collection.FindRequest("Widgets/List widgets")!;
        var example = Assert.Single(collection.GetExamples(list));

        collection.RenameExample(example, "Empty list");
        collection.Save();
        Assert.False(File.Exists(F(root, ListExample)));
        var renamed = F(root, "Widgets/.resources/List widgets.resources/examples/Empty list.example.yaml");
        Assert.DoesNotContain("name:", File.ReadAllText(renamed), StringComparison.Ordinal);

        collection.RenameExample(example, "empty LIST");
        collection.Save();
        var recased = Directory.EnumerateFiles(Path.GetDirectoryName(renamed)!).Select(Path.GetFileName);
        Assert.Equal(["empty LIST.example.yaml"], recased);

        collection.RemoveExample(example);
        collection.Save();
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(renamed)!));
        var reopened = OpenV3(root);
        Assert.Empty(reopened.GetExamples(reopened.FindRequest("Widgets/List widgets")!));
    }

    [Fact]
    public void A_copy_of_a_v3_example_stays_right_after_its_original()
    {
        var root = V3Fixture.Create();
        var collection = OpenV3(root);
        var list = collection.FindRequest("Widgets/List widgets")!;
        var original = Assert.Single(collection.GetExamples(list));
        collection.AddExample(list, new ExampleSpec { Name = "Not found", Request = list.ReadSpec(), Code = 404, Status = "Not Found", Language = "text" });
        collection.Save();

        collection.DuplicateExample(original);
        collection.Save();

        var reopened = OpenV3(root);
        Assert.Equal(["200 OK", "200 OK Copy", "Not found"], reopened.GetExamples(reopened.FindRequest("Widgets/List widgets")!).Select(e => e.Name));
    }

    [Theory]
    [InlineData("Widgets")] // its folder, as a git checkout may remove it
    [InlineData("Widgets/List widgets.request.yaml")] // its own file
    public void An_example_saved_for_a_request_whose_file_went_brings_the_request_back(string gone)
    {
        var root = V3Fixture.Create();
        var collection = OpenV3(root);
        var list = collection.FindRequest("Widgets/List widgets")!;
        var example = Assert.Single(collection.GetExamples(list));
        var spec = example.ReadSpec();
        spec.Body = "[1]";
        Assert.True(collection.UpdateExample(example, spec));
        if (Directory.Exists(F(root, gone)))
        {
            Directory.Delete(F(root, gone), recursive: true);
        }
        else
        {
            File.Delete(F(root, gone));
        }

        collection.Save();

        // The request and its folder as they were (the folder's auth and scripts too), not an example nothing shows.
        Assert.True(File.Exists(F(root, "Widgets/List widgets.request.yaml")));
        Assert.True(File.Exists(F(root, "Widgets/.resources/definition.yaml")));
        var reopened = OpenV3(root);
        Assert.Equal("[1]", Assert.Single(reopened.GetExamples(reopened.FindRequest("Widgets/List widgets")!)).ReadSpec().Body);
    }

    [Fact]
    public void A_v3_collection_exported_or_copied_takes_its_examples_along()
    {
        var root = V3Fixture.Create();
        var collection = OpenV3(root);

        var exported = (JsonObject)JsonNode.Parse(collection.ExportV21Json())!;
        var widgets = (JsonArray)exported["item"]![1]!["item"]!;
        var example = (JsonObject)widgets[0]!["response"]![0]!;
        Assert.Equal(("200 OK", 200, "OK", "[]", "json"),
            (example["name"]!.GetValue<string>(), example["code"]!.GetValue<int>(), example["status"]!.GetValue<string>(), example["body"]!.GetValue<string>(),
             example["_postman_previewlanguage"]!.GetValue<string>()));

        var copy = (RequestNode)collection.Duplicate(collection.FindRequest("Widgets/List widgets")!);
        collection.Save();
        Assert.Equal("200 OK", Assert.Single(collection.GetExamples(copy)).Name);
        Assert.True(File.Exists(F(root, "Widgets/.resources/List widgets Copy.resources/examples/200 OK.example.yaml")));
    }

    [Fact]
    public void Examples_of_a_json_collection_are_edited_in_place()
    {
        const string json = """
            {
            	"info": {
            		"name": "Shop",
            		"schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
            	},
            	"item": [
            		{
            			"name": "Get item",
            			"request": {
            				"method": "GET",
            				"header": [],
            				"url": "https://shop.example.com/items/1"
            			},
            			"response": [
            				{
            					"id": "6a1f",
            					"name": "Found",
            					"originalRequest": {
            						"method": "GET",
            						"header": [],
            						"url": "https://shop.example.com/items/1"
            					},
            					"status": "OK",
            					"code": 200,
            					"_postman_previewlanguage": "json",
            					"header": [
            						{
            							"key": "Content-Type",
            							"value": "application/json"
            						}
            					],
            					"cookie": [],
            					"body": "{\"id\": 1}"
            				}
            			]
            		}
            	]
            }
            """;
        var collection = PostmanCollection.Parse(json);
        var item = collection.FindRequest("Get item")!;
        var example = Assert.Single(collection.GetExamples(item));
        var spec = example.ReadSpec();
        spec.Code = 404;
        spec.Status = "Not Found";
        Assert.True(collection.UpdateExample(example, spec));
        Assert.Equal(json.Replace("\"OK\"", "\"Not Found\"", StringComparison.Ordinal).Replace("200", "404", StringComparison.Ordinal), collection.Serialize());

        var created = collection.AddExample(item, new ExampleSpec { Name = "Gone", Request = item.ReadSpec(), Code = 410, Status = "Gone", Language = "text" });
        Assert.Equal(["name", "originalRequest", "status", "code", "_postman_previewlanguage", "header", "cookie", "body"], created.Json.Select(p => p.Key));
        Assert.Equal(["Found", "Gone"], collection.GetExamples(item).Select(e => e.Name));

        var copy = collection.DuplicateExample(example);
        Assert.Equal(["Found", "Found Copy", "Gone"], collection.GetExamples(item).Select(e => e.Name));
        Assert.Null(copy.Json["id"]);

        collection.RemoveExample(created);
        collection.RemoveExample(copy);
        Assert.Equal(json.Replace("\"OK\"", "\"Not Found\"", StringComparison.Ordinal).Replace("200", "404", StringComparison.Ordinal), collection.Serialize());
    }

    [Fact]
    public void An_example_made_from_a_response_holds_its_text_and_says_why_a_binary_body_is_left_out()
    {
        var request = new HttpRequestSpec { Url = "https://shop.example.com/items/1" };
        var json = new ResponseSnapshot
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers = [KeyValuePair.Create("Content-Type", "application/json; charset=utf-8")],
            Body = "{\"name\":\"Zoë\"}"u8.ToArray(),
            ContentType = "application/json; charset=utf-8",
        };
        var example = ExampleMapper.FromResponse("200 OK", request, json, out var note);
        Assert.Null(note);
        Assert.Equal(("200 OK", 200, "OK", "{\"name\":\"Zoë\"}", "json"), (example.Name, example.Code, example.Status, example.Body, example.Language));
        Assert.Equal(["Content-Type: application/json; charset=utf-8"], example.Headers.Select(h => $"{h.Key}: {h.Value}"));
        Assert.Equal(request.Url, example.Request.Url);

        var image = new ResponseSnapshot { StatusCode = 200, ReasonPhrase = "OK", Body = [0x89, 0x50, 0x4E, 0x47], ContentType = "image/png" };
        var picture = ExampleMapper.FromResponse("Logo", request, image, out note);
        Assert.Equal(("", "text"), (picture.Body, picture.Language));
        Assert.Contains("image/png", note, StringComparison.Ordinal);
    }
}
