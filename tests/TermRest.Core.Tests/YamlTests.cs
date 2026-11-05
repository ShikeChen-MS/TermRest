using System.Text.Json.Nodes;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Yaml;
using Xunit;

namespace TermRest.Core.Tests;

public class YamlTests
{
    /// <summary>A request file in the style Postman writes (folded long text, quoted variables, a map of headers).</summary>
    private static readonly string Request = """
        $kind: http-request
        description: Lists the widgets the caller can see. Results are paginated and sorted by
          name; use the cursor from the previous page.
        url: '{{baseUrl}}/v1/widgets?limit=50'
        method: GET
        headers:
          Accept: application/json
          X-Trace: "on"
        queryParams:
          - key: limit
            value: '50'
          - key: cursor
            value: '{{cursor}}'
            disabled: true
            description: From the previous page
        scripts:
          - type: afterResponse
            code: |-
              pm.test('ok', () => pm.response.to.have.status(200));
              # not a comment: part of the script

            language: text/javascript
        # kept with the entry below
        examples: ./.resources/List widgets.resources/examples
        order: 2000

        """.ReplaceLineEndings("\n");

    [Fact]
    public void Parses_postman_style_yaml_into_json_values()
    {
        var file = YamlFile.Parse(Request);
        var json = file.Json;

        Assert.Equal("Lists the widgets the caller can see. Results are paginated and sorted by name; use the cursor from the previous page.", json["description"]!.GetValue<string>());
        Assert.Equal("{{baseUrl}}/v1/widgets?limit=50", json["url"]!.GetValue<string>());
        Assert.Equal("on", json["headers"]!["X-Trace"]!.GetValue<string>());
        Assert.Equal("50", json["queryParams"]![0]!["value"]!.GetValue<string>());
        Assert.True(json["queryParams"]![1]!["disabled"]!.GetValue<bool>());
        Assert.Equal("pm.test('ok', () => pm.response.to.have.status(200));\n# not a comment: part of the script", json["scripts"]![0]!["code"]!.GetValue<string>());
        Assert.Equal(2000L, json["order"]!.GetValue<long>());
        Assert.Equal(["$kind", "description", "url", "method", "headers", "queryParams", "scripts", "examples", "order"], json.Select(p => p.Key));
    }

    [Theory]
    [InlineData("a: 12", 12L)]
    [InlineData("a: -7", -7L)]
    [InlineData("a: 0x1F", 31L)]
    [InlineData("a: 1500.5", 1500.5)]
    [InlineData("a: true", true)]
    [InlineData("a: False", false)]
    [InlineData("a: '12'", "12")]
    [InlineData("a: \"true\"", "true")]
    [InlineData("a: yes", "yes")]
    [InlineData("a: 1.2.3", "1.2.3")]
    [InlineData("a: 0123abc", "0123abc")]
    public void Resolves_plain_scalars_with_the_yaml_1_2_core_schema(string yaml, object expected)
    {
        var value = YamlFile.Parse(yaml).Json["a"]!;

        Assert.Equal(JsonValue.Create(expected)!.ToJsonString(), value.ToJsonString());
    }

    [Theory]
    [InlineData("a:")]
    [InlineData("a: ~")]
    [InlineData("a: null")]
    public void Empty_and_null_values_are_null(string yaml)
    {
        Assert.Null(YamlFile.Parse(yaml).Json["a"]);
        Assert.True(YamlFile.Parse(yaml).Json.ContainsKey("a"));
    }

    [Fact]
    public void Rendering_unchanged_content_returns_the_exact_text()
    {
        var file = YamlFile.Parse(Request);

        Assert.Equal(Request, file.Render((JsonObject)file.Json.DeepClone(), YamlStyle.Default));
    }

    [Fact]
    public void Changing_one_value_rewrites_only_that_line()
    {
        var file = YamlFile.Parse(Request);
        var changed = (JsonObject)file.Json.DeepClone();
        changed["url"] = "{{baseUrl}}/v2/widgets?limit=50";
        changed["headers"]!["Accept"] = "application/xml";
        ((JsonArray)changed["queryParams"]!)[1]!["value"] = "{{nextCursor}}";

        var text = file.Render(changed, YamlStyle.Default);

        Assert.Equal(
            ["- url: '{{baseUrl}}/v1/widgets?limit=50'", "- Accept: application/json", "- value: '{{cursor}}'",
             "+ url: '{{baseUrl}}/v2/widgets?limit=50'", "+ Accept: application/xml", "+ value: '{{nextCursor}}'"],
            TestFiles.ChangedLines(Request, text));
        Assert.True(JsonNode.DeepEquals(changed, YamlFile.Parse(text).Json));
    }

    [Fact]
    public void Adding_and_removing_entries_keeps_the_rest()
    {
        var file = YamlFile.Parse(Request);
        var changed = (JsonObject)file.Json.DeepClone();
        changed.Remove("examples");
        changed.Insert(changed.IndexOf("scripts"), "body", new JsonObject { ["type"] = "json", ["content"] = "{\n  \"name\": \"x\"\n}" });
        ((JsonArray)changed["queryParams"]!).Add((JsonNode)new JsonObject { ["key"] = "sort", ["value"] = "name" });

        var text = file.Render(changed, YamlStyle.Default);

        Assert.Equal(
            ["- # kept with the entry below", "- examples: ./.resources/List widgets.resources/examples",
             "+ - key: sort", "+ value: name", "+ body:", "+ type: json", "+ content: |-", "+ {", "+ \"name\": \"x\"", "+ }"],
            TestFiles.ChangedLines(Request, text));
        Assert.True(JsonNode.DeepEquals(changed, YamlFile.Parse(text).Json));
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("")]
    [InlineData(" leading space")]
    [InlineData("trailing space ")]
    [InlineData("key: value")]
    [InlineData("a #hash")]
    [InlineData("#start")]
    [InlineData("- dash")]
    [InlineData("'quoted'")]
    [InlineData("say \"hi\"")]
    [InlineData("it's \"both\"")]
    [InlineData("{{var}}")]
    [InlineData("[x]")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("~")]
    [InlineData("0123")]
    [InlineData("1e3")]
    [InlineData("yes")]
    [InlineData("https://h/a?b=c#frag")]
    [InlineData("line1\nline2")]
    [InlineData("line1\nline2\n")]
    [InlineData("line1\n\n\n")]
    [InlineData("\nstarts with a break")]
    [InlineData("  indented first line\nsecond")]
    [InlineData("ends with spaces\n   ")]
    [InlineData("tab\there")]
    [InlineData("crlf\r\nline")]
    [InlineData("bell\u0007 and nul\0")]
    [InlineData("emoji 😀 and ü")]
    [InlineData("---")]
    [InlineData("...")]
    [InlineData("%percent")]
    [InlineData("@at")]
    [InlineData("`tick")]
    [InlineData("12:30")]
    public void Written_strings_read_back_unchanged(string value)
    {
        foreach (var style in (YamlStyle[])[YamlStyle.Default, new YamlStyle(PreferSingleQuotes: true)])
        {
            var root = new JsonObject
            {
                ["value"] = value,
                ["list"] = new JsonArray(value, new JsonObject { ["nested"] = value }),
            };

            var text = YamlFile.Write(root, style);
            var parsed = YamlFile.Parse(text).Json;

            Assert.True(JsonNode.DeepEquals(root, parsed), $"{text}\n→ {parsed.ToJsonString()}");
        }
    }

    [Fact]
    public void Writes_in_postman_style()
    {
        var root = new JsonObject
        {
            ["$kind"] = "http-request",
            ["url"] = "{{base}}/items",
            ["method"] = "POST",
            ["headers"] = new JsonObject { ["Content-Type"] = "application/json" },
            ["body"] = new JsonObject { ["type"] = "json", ["content"] = "{\n  \"a\": 1\n}" },
            ["queryParams"] = new JsonArray(new JsonObject { ["key"] = "n", ["value"] = "5" }),
            ["settings"] = new JsonObject(),
            ["order"] = 1000,
        };

        Assert.Equal(
            """
            $kind: http-request
            url: "{{base}}/items"
            method: POST
            headers:
              Content-Type: application/json
            body:
              type: json
              content: |-
                {
                  "a": 1
                }
            queryParams:
              - key: n
                value: "5"
            settings: {}
            order: 1000

            """.ReplaceLineEndings("\n"),
            YamlFile.Write(root, YamlStyle.Default));
        Assert.Contains("url: '{{base}}/items'", YamlFile.Write(root, new YamlStyle(PreferSingleQuotes: true)), StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_style_is_detected_from_existing_files()
    {
        Assert.True(YamlStyle.Detect([YamlFile.Parse(Request).Tree]).PreferSingleQuotes);
        Assert.False(YamlStyle.Detect([YamlFile.Parse("a: \"{{x}}\"\nb: \"1\"\nc: 'y'").Tree]).PreferSingleQuotes);
        Assert.False(YamlStyle.Detect([]).PreferSingleQuotes);
        Assert.True(YamlStyle.Detect([YamlFile.Parse("a: \"1\"\nb: '2'").Tree], new YamlStyle(PreferSingleQuotes: true)).PreferSingleQuotes);
    }

    [Fact]
    public void New_scalars_are_quoted_like_the_rest_of_their_document()
    {
        var single = YamlFile.Parse("url: '{{base}}/a'\n");
        var plain = YamlFile.Parse("method: GET\n");

        Assert.Equal("url: '{{base}}/a'\nid: '1'\n", single.Render(new JsonObject { ["url"] = "{{base}}/a", ["id"] = "1" }, YamlStyle.Default));
        Assert.Equal("method: GET\nid: '1'\n", plain.Render(new JsonObject { ["method"] = "GET", ["id"] = "1" }, new YamlStyle(PreferSingleQuotes: true)));
        Assert.Equal("method: GET\nid: \"1\"\n", plain.Render(new JsonObject { ["method"] = "GET", ["id"] = "1" }, YamlStyle.Default));
    }

    [Theory]
    [InlineData("a: [")]
    [InlineData("a: b\n  c: d")]
    [InlineData("- just\n- a list")]
    [InlineData("a: *alias")]
    public void Invalid_or_unsupported_yaml_is_reported(string yaml)
    {
        Assert.ThrowsAny<FormatException>(() => YamlFile.Parse(yaml));
    }

    [Fact]
    public void Json_equality_ignores_key_order_and_number_spelling()
    {
        // The writer relies on these semantics to decide what changed.
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["a"] = 1, ["b"] = 2 }, new JsonObject { ["b"] = 2, ["a"] = 1 }));
        Assert.True(JsonNode.DeepEquals(YamlFile.Parse("a: 1000").Json, new JsonObject { ["a"] = 1000 }));
    }
}
