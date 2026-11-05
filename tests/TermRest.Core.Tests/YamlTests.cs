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

    // ----- wrapping long values ------------------------------------------------------------------------

    private const string LongHeaderName = "X-An-Extremely-Long-Header-Name-That-Keeps-Going-And-Going-Forever";

    /// <summary>A request with long values of every kind (expected output: what Postman's YAML library writes).</summary>
    private static JsonObject LongValues() => new()
    {
        ["$kind"] = "http-request",
        ["description"] = "Lists the widgets the caller can see. Results are paginated and sorted by name; use the cursor from the previous page.",
        ["url"] = "{{baseUrl}}/v1/organizations/org_7d3k2m9x0q1w5e8r4t6y/projects/prj_a1b2c3d4e5f6/archived-items",
        ["headers"] = new JsonObject
        {
            ["X-Custom"] = "A custom header whose value is long enough to need wrapping once it passes the width",
            [LongHeaderName] = "this value starts on the line after its key because the key leaves no room on the first line",
        },
        ["queryParams"] = new JsonArray(new JsonObject
        {
            ["key"] = "filter",
            ["value"] = "status eq 'open' and owner eq 'me' and created gt 2024-01-01 and priority in (1,2,3)",
        }),
        ["body"] = new JsonObject
        {
            ["type"] = "json",
            ["content"] = "{\n    \"name\": \"Sprocket\",\n    \"notes\": \"This note is deliberately long so that the line goes well past the eighty column limit.\"\n}",
        },
        ["scripts"] = new JsonArray(
            new JsonObject
            {
                ["type"] = "afterResponse",
                ["code"] = "pm.test('status is 200', () => pm.response.to.have.status(200)); pm.test('json body', () => pm.response.to.be.json);\nconsole.log('done');",
                ["language"] = "text/javascript",
            },
            new JsonObject
            {
                ["type"] = "beforeRequest",
                ["code"] = "const url = 'https://example.com/a/very/long/path/that/cannot/be/broken/because/it/has/no/spaces/at/all';\nconsole.log(url);",
            }),
        ["tags"] = new JsonArray("short", "a tag that is very long and goes on and on until it has to be wrapped onto another line"),
    };

    [Fact]
    public void Long_values_are_wrapped_like_postman_at_80_columns()
    {
        Assert.Equal(
            """
            $kind: http-request
            description: Lists the widgets the caller can see. Results are paginated and
              sorted by name; use the cursor from the previous page.
            url: "{{baseUrl}}/v1/organizations/org_7d3k2m9x0q1w5e8r4t6y/projects/prj_a1b2c3\
              d4e5f6/archived-items"
            headers:
              X-Custom: A custom header whose value is long enough to need wrapping once it
                passes the width
              X-An-Extremely-Long-Header-Name-That-Keeps-Going-And-Going-Forever:
                this value starts on the line after its key because the key leaves no room
                on the first line
            queryParams:
              - key: filter
                value: status eq 'open' and owner eq 'me' and created gt 2024-01-01 and priority
                  in (1,2,3)
            body:
              type: json
              content: >-
                {
                    "name": "Sprocket",
                    "notes": "This note is deliberately long so that the line goes well past the eighty column limit."
                }
            scripts:
              - type: afterResponse
                code: >-
                  pm.test('status is 200', () => pm.response.to.have.status(200));
                  pm.test('json body', () => pm.response.to.be.json);

                  console.log('done');
                language: text/javascript
              - type: beforeRequest
                code: |-
                  const url = 'https://example.com/a/very/long/path/that/cannot/be/broken/because/it/has/no/spaces/at/all';
                  console.log(url);
            tags:
              - short
              - a tag that is very long and goes on and on until it has to be wrapped onto
                another line

            """.ReplaceLineEndings("\n"),
            YamlFile.Write(LongValues(), YamlStyle.Default.WithLineWidth(80)));
    }

    [Fact]
    public void Any_width_can_be_chosen_and_0_never_wraps()
    {
        Assert.Equal(
            """
            $kind: http-request
            description: Lists the widgets the
              caller can see. Results are paginated
              and sorted by name; use the cursor
              from the previous page.
            url: "{{baseUrl}}/v1/organizations/org_\
              7d3k2m9x0q1w5e8r4t6y/projects/prj_a1b\
              2c3d4e5f6/archived-items"
            headers:
              X-Custom: A custom header whose value is
                long enough to need wrapping once it
                passes the width
              X-An-Extremely-Long-Header-Name-That-Keeps-Going-And-Going-Forever:
                this value starts on the line after
                its key because the key leaves no
                room on the first line
            queryParams:
              - key: filter
                value: status eq 'open' and owner eq
                  'me' and created gt 2024-01-01 and
                  priority in (1,2,3)
            body:
              type: json
              content: >-
                {
                    "name": "Sprocket",
                    "notes": "This note is deliberately long so that the line goes well past the eighty column limit."
                }
            scripts:
              - type: afterResponse
                code: >-
                  pm.test('status is 200', () =>
                  pm.response.to.have.status(200));
                  pm.test('json body', () =>
                  pm.response.to.be.json);

                  console.log('done');
                language: text/javascript
              - type: beforeRequest
                code: |-
                  const url = 'https://example.com/a/very/long/path/that/cannot/be/broken/because/it/has/no/spaces/at/all';
                  console.log(url);
            tags:
              - short
              - a tag that is very long and goes on
                and on until it has to be wrapped
                onto another line

            """.ReplaceLineEndings("\n"),
            YamlFile.Write(LongValues(), YamlStyle.Default.WithLineWidth(40)));

        var unwrapped = YamlFile.Write(LongValues(), YamlStyle.Default.WithLineWidth(0));
        Assert.Equal(
            """
            $kind: http-request
            description: Lists the widgets the caller can see. Results are paginated and sorted by name; use the cursor from the previous page.
            url: "{{baseUrl}}/v1/organizations/org_7d3k2m9x0q1w5e8r4t6y/projects/prj_a1b2c3d4e5f6/archived-items"
            headers:
              X-Custom: A custom header whose value is long enough to need wrapping once it passes the width
              X-An-Extremely-Long-Header-Name-That-Keeps-Going-And-Going-Forever: this value starts on the line after its key because the key leaves no room on the first line
            queryParams:
              - key: filter
                value: status eq 'open' and owner eq 'me' and created gt 2024-01-01 and priority in (1,2,3)
            body:
              type: json
              content: |-
                {
                    "name": "Sprocket",
                    "notes": "This note is deliberately long so that the line goes well past the eighty column limit."
                }
            scripts:
              - type: afterResponse
                code: |-
                  pm.test('status is 200', () => pm.response.to.have.status(200)); pm.test('json body', () => pm.response.to.be.json);
                  console.log('done');
                language: text/javascript
              - type: beforeRequest
                code: |-
                  const url = 'https://example.com/a/very/long/path/that/cannot/be/broken/because/it/has/no/spaces/at/all';
                  console.log(url);
            tags:
              - short
              - a tag that is very long and goes on and on until it has to be wrapped onto another line

            """.ReplaceLineEndings("\n"),
            unwrapped);
        Assert.Equal(unwrapped, YamlFile.Write(LongValues(), YamlStyle.Default));
        foreach (var style in (YamlStyle[])[YamlStyle.Default.WithLineWidth(80), YamlStyle.Default.WithLineWidth(40), YamlStyle.Default])
        {
            Assert.True(JsonNode.DeepEquals(LongValues(), YamlFile.Parse(YamlFile.Write(LongValues(), style)).Json));
        }
    }

    public static TheoryData<string> LongStrings() =>
    [
        string.Join(' ', Enumerable.Repeat("word", 60)),
        "double  spaces  are  never  places  to  break  " + string.Join("  ", Enumerable.Repeat("long-ish-token", 12)),
        "tabs\tbetween\twords\t" + string.Join('\t', Enumerable.Repeat("tabbed", 20)),
        "key: value pairs make it quoted " + string.Join(' ', Enumerable.Repeat("and long", 20)),
        "a #hash forces quotes " + string.Join(' ', Enumerable.Repeat("and more text", 12)),
        "{{start}} with a variable " + string.Join(' ', Enumerable.Repeat("then words", 15)),
        "it's got 'single' quotes " + string.Join(' ', Enumerable.Repeat("filler", 20)),
        "say \"hi\" with double quotes " + string.Join(' ', Enumerable.Repeat("filler", 20)),
        "control\u0001chars\u001fand\ttabs " + string.Join(' ', Enumerable.Repeat("x\u0007y", 30)),
        "{{noSpacesAtAll}}" + string.Concat(Enumerable.Repeat("abcdefghij", 25)),
        "\\backslashes\\n and \\\"quotes\\\" " + string.Join(' ', Enumerable.Repeat("\\path\\to", 20)),
        "unicode é ü ✓ 😀 " + string.Join(' ', Enumerable.Repeat("日本語のテキスト", 20)),
        "trailing space at the end of a long line of text that needs wrapping somewhere along the way ",
        "  leading spaces and a long line of text that needs wrapping somewhere along the way please",
        "first line\nsecond line " + string.Join(' ', Enumerable.Repeat("long", 40)) + "\nthird",
        "paragraph one " + string.Join(' ', Enumerable.Repeat("one", 30)) + "\n\nparagraph two " + string.Join(' ', Enumerable.Repeat("two", 30)) + "\n",
        "{\n  \"indented\": \"" + string.Join(' ', Enumerable.Repeat("json", 40)) + "\"\n}\n\n\n",
        "\tTabbed first line " + string.Join(' ', Enumerable.Repeat("tab", 30)) + "\nnext",
        "line one " + string.Join(' ', Enumerable.Repeat("crlf", 30)) + "\r\nline two\r\n",
        "ends with spaces " + string.Join(' ', Enumerable.Repeat("w", 50)) + " \n   ",
        "space before break " + string.Join(' ', Enumerable.Repeat("w", 40)) + " \nand after\n  indented continuation",
        "line\u2028separator and next\u0085line " + string.Join(' ', Enumerable.Repeat("w", 50)),
        string.Concat(Enumerable.Repeat("x", 300)),
        string.Concat(Enumerable.Repeat("\\x", 120)),
        "escapes right after a space \u0001\u0002 " + string.Join(" \u0003", Enumerable.Repeat("ab", 40)),
        string.Join(' ', Enumerable.Repeat("\u0007\u0008\u001b", 40)),
    ];

    [Theory]
    [MemberData(nameof(LongStrings))]
    public void Wrapped_values_read_back_unchanged(string value)
    {
        JsonNode deep = new JsonObject { ["key"] = value, ["list"] = new JsonArray(value) };
        for (var level = 0; level < 12; level++)
        {
            deep = new JsonObject { [$"level{level}"] = deep };
        }

        foreach (var width in (int[])[1, 10, 20, 40, 80, 120])
        {
            foreach (var preferSingle in (bool[])[false, true])
            {
                var root = new JsonObject
                {
                    ["value"] = value,
                    ["nested"] = new JsonObject { ["deeper"] = new JsonObject { ["key"] = value } },
                    ["list"] = new JsonArray(value, new JsonObject { ["key"] = value, ["other"] = value }, new JsonArray(value)),
                    [LongHeaderName] = value,
                    ["deep"] = deep.DeepClone(),
                };

                var text = YamlFile.Write(root, new YamlStyle(preferSingle).WithLineWidth(width));
                var parsed = YamlFile.Parse(text).Json;

                Assert.True(JsonNode.DeepEquals(root, parsed), $"width {width}:\n{text}\n→ {parsed.ToJsonString()}");
            }
        }
    }

    [Fact]
    public void Line_width_is_detected_from_how_files_were_written()
    {
        const string wrapped = "description: Lists the widgets the caller can see. Results are paginated and\n  sorted by name; use the cursor from the previous page.\n";
        const string unwrapped = "description: Lists the widgets the caller can see. Results are paginated and sorted by name; use the cursor from the previous page.\n";
        const string neither = "description: Short.\nurl: https://example.com\n";

        Assert.Equal(80, YamlStyle.Detect([YamlFile.Parse(wrapped).Tree]).LineWidth);
        Assert.Equal(0, YamlStyle.Detect([YamlFile.Parse(unwrapped).Tree]).LineWidth);
        Assert.Equal(0, YamlStyle.Detect([YamlFile.Parse(neither).Tree]).LineWidth);
        Assert.Equal(80, YamlStyle.Detect([YamlFile.Parse(neither).Tree], new YamlStyle(false, 80)).LineWidth);
        Assert.Equal(80, YamlStyle.Detect([YamlFile.Parse(wrapped).Tree, YamlFile.Parse(wrapped).Tree, YamlFile.Parse(unwrapped).Tree]).LineWidth);

        // Folded the way no Postman version does (the first line is too long): no evidence either way.
        Assert.Equal(0, YamlStyle.Detect([YamlFile.Parse(Request).Tree]).LineWidth);

        // The user's choice wins over the files.
        var fixedStyle = YamlStyle.Detect([YamlFile.Parse(wrapped).Tree], YamlStyle.Default.WithLineWidth(0));
        Assert.Equal((0, true), (fixedStyle.LineWidth, fixedStyle.LineWidthIsFixed));
        Assert.Equal(100, YamlStyle.Detect([YamlFile.Parse(unwrapped).Tree], YamlStyle.Default.WithLineWidth(100)).LineWidth);
    }

    [Fact]
    public void Wrapping_applies_to_changed_values_only()
    {
        var file = YamlFile.Parse("""
            $kind: http-request
            description: Lists the widgets the caller can see. Results are paginated and
              sorted by name; use the cursor from the previous page.
            url: https://example.com/widgets
            order: 1000

            """.ReplaceLineEndings("\n"));
        var changed = (JsonObject)file.Json.DeepClone();
        changed["url"] = "https://example.com/widgets?filter=a long filter value that makes this line longer than eighty columns";

        // The file is wrapped: so is the new value; the unchanged one keeps its line breaks.
        var text = file.Render(changed, YamlStyle.Default);
        Assert.Equal(
            ["- url: https://example.com/widgets", "+ url: https://example.com/widgets?filter=a long filter value that makes this line", "+ longer than eighty columns"],
            TestFiles.ChangedLines(file.Tree.Text, text));

        // Never wrapping: still only the changed value is affected.
        text = file.Render(changed, YamlStyle.Default.WithLineWidth(0));
        Assert.Equal(
            ["- url: https://example.com/widgets", "+ url: https://example.com/widgets?filter=a long filter value that makes this line longer than eighty columns"],
            TestFiles.ChangedLines(file.Tree.Text, text));
    }

    [Fact]
    public void Edited_blocks_keep_their_literal_or_folded_style()
    {
        var file = YamlFile.Parse("""
            literal: |-
              short
              lines
            folded: >-
              short
              text

            """.ReplaceLineEndings("\n"));
        var longLine = "a line that is long enough to be folded if this value were new: " + string.Join(' ', Enumerable.Repeat("word", 12));
        var changed = new JsonObject { ["literal"] = longLine + "\nmore", ["folded"] = "now\nshort" };

        var text = file.Render(changed, YamlStyle.Default.WithLineWidth(80));

        Assert.StartsWith("literal: |-\n  " + longLine + "\n  more\nfolded: >-\n  now\n\n  short\n", text, StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(changed, YamlFile.Parse(text).Json));
    }
}
