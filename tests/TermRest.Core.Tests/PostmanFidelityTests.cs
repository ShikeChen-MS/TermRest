using System.Text.Json.Nodes;
using TermRest.Core.Json;
using TermRest.Core.Postman;
using TermRest.Core.Tests.Infrastructure;
using Xunit;

namespace TermRest.Core.Tests;

public class PostmanFidelityTests
{
    [Theory]
    [InlineData("samples/TermRest Demo.postman_collection.json")]
    [InlineData("TestData/complex.postman_collection.json")]
    [InlineData("TestData/legacy-v20.postman_collection.json")]
    public void Load_then_serialize_is_byte_identical(string relativePath)
    {
        var path = Path.Combine(AppContext.BaseDirectory, relativePath);
        var original = File.ReadAllText(path);

        var collection = PostmanCollection.Load(path);

        Assert.Equal(original, collection.Serialize());
    }

    [Fact]
    public void Environment_round_trips_byte_identical()
    {
        var path = TestFiles.CopyToTemp(TestFiles.DemoEnvironment);
        var original = File.ReadAllBytes(path);

        PostmanEnvironment.Load(path).Save();

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Save_preserves_bom_crlf_and_trailing_newline()
    {
        var directory = TestFiles.NewTempDirectory();
        var path = Path.Combine(directory, "crlf.postman_collection.json");
        var lf = File.ReadAllText(TestFiles.DemoCollection);
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(crlf)]);
        var before = File.ReadAllBytes(path);

        var collection = PostmanCollection.Load(path);
        collection.MarkDirty();
        collection.Save();

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Saving_a_file_with_mixed_line_breaks_writes_the_kind_most_lines_have()
    {
        var directory = TestFiles.NewTempDirectory();
        var lf = File.ReadAllText(TestFiles.DemoCollection);
        var lines = lf.Split('\n');
        lines[1] += "\r"; // one line saved by a Windows editor, or a merge
        var mostlyLf = Path.Combine(directory, "lf.postman_collection.json");
        File.WriteAllText(mostlyLf, string.Join('\n', lines));
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);
        var mostlyCrLf = Path.Combine(directory, "crlf.postman_collection.json");
        File.WriteAllText(mostlyCrLf, crlf.Replace("\"info\": {\r\n", "\"info\": {\n", StringComparison.Ordinal));

        foreach (var path in (string[])[mostlyLf, mostlyCrLf])
        {
            var collection = PostmanCollection.Load(path);
            collection.MarkDirty();
            collection.Save();
        }

        // Only the stray line changes.
        Assert.Equal(lf, File.ReadAllText(mostlyLf));
        Assert.Equal(crlf, File.ReadAllText(mostlyCrLf));
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("quote \" and \\ backslash", "\"quote \\\" and \\\\ backslash\"")]
    [InlineData("<html> & 'single' + plus", "\"<html> & 'single' + plus\"")]
    [InlineData("tab\tnewline\nreturn\r", "\"tab\\tnewline\\nreturn\\r\"")]
    [InlineData("escape \u001b and \u0000", "\"escape \\u001b and \\u0000\"")]
    [InlineData("ünïcödé 世界 ✓", "\"ünïcödé 世界 ✓\"")]
    [InlineData("emoji 😀", "\"emoji 😀\"")]
    [InlineData("line\u2028separator", "\"line\u2028separator\"")]
    public void Strings_are_escaped_like_JSON_stringify(string value, string expected)
    {
        Assert.Equal(expected, PostmanJson.Serialize(JsonValue.Create(value)));
    }

    [Fact]
    public void Nested_structures_use_tabs_and_empty_containers_stay_inline()
    {
        var node = PostmanJson.Parse("{\"a\":[],\"b\":{},\"c\":[1,{\"d\":null}]}");

        Assert.Equal("{\n\t\"a\": [],\n\t\"b\": {},\n\t\"c\": [\n\t\t1,\n\t\t{\n\t\t\t\"d\": null\n\t\t}\n\t]\n}", PostmanJson.Serialize(node));
    }

    [Fact]
    public void Numbers_keep_their_original_text()
    {
        var node = PostmanJson.Parse("{\"big\":12345678901234567890123,\"f\":1.50,\"e\":1e3}");

        Assert.Equal("{\n\t\"big\": 12345678901234567890123,\n\t\"f\": 1.50,\n\t\"e\": 1e3\n}", PostmanJson.Serialize(node));
    }
}
