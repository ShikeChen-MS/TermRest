using System.Text;
using TermRest.Core.Http;
using TermRest.Core.Tests.Infrastructure;
using Xunit;

namespace TermRest.Core.Tests;

/// <summary>Body files: checked by type before they are sent, streamed through a small buffer.</summary>
public sealed class BodyFileTests
{
    private readonly string _directory = TestFiles.NewTempDirectory();

    private static string? Check(string path, string? language) => BodyFile.Check(path, language, TestContext.Current.CancellationToken);

    private string Write(string name, string text) => Write(name, Encoding.UTF8.GetBytes(text));

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Valid_json_passes_with_or_without_a_byte_order_mark()
    {
        Assert.Null(Check(Write("a.json", "{\"items\": [1, 2.5e3, \"é\", true, null], \"nested\": {\"x\": {}}}\n"), "json"));
        Assert.Null(Check(Write("bom.json", [0xEF, 0xBB, 0xBF, .. "[]"u8]), "json"));
        Assert.Null(Check(Write("scalar.json", "  42  "), "JSON"));
    }

    [Fact]
    public void Invalid_json_is_turned_down_saying_where_the_problem_is()
    {
        Assert.Equal(
            "order.json is not valid JSON: 'x' is an invalid start of a value (line 3, column 8).",
            Check(Write("order.json", "{\n  \"a\": 1,\n  \"b\": x\n}"), "json"));
        Assert.Equal("empty.json is empty.", Check(Write("empty.json", ""), "json"));
        Assert.Equal("blank.json is empty.", Check(Write("blank.json", " \r\n "), "json"));
        Assert.StartsWith("two.json is not valid JSON: '{' is invalid after a single JSON value", Check(Write("two.json", "{}\n{}"), "json"), StringComparison.Ordinal);
        Assert.StartsWith("cut.json is not valid JSON: ", Check(Write("cut.json", "{\"a\": [1, 2"), "json"), StringComparison.Ordinal);
        Assert.Contains("trailing comma", Check(Write("comma.json", "[1,]"), "json"), StringComparison.Ordinal);
    }

    [Fact]
    public void Json_must_be_utf8()
    {
        Assert.Equal(
            "utf16.json is UTF-16 text, but JSON must be UTF-8: save it as UTF-8.",
            Check(Write("utf16.json", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("{}")]), "json"));
        Assert.Equal(
            "plain16.json is UTF-16 text, but JSON must be UTF-8: save it as UTF-8.",
            Check(Write("plain16.json", Encoding.Unicode.GetBytes("{}")), "json"));

        // Latin-1 'é' in a string: the JSON reader itself would let it through.
        Assert.Equal(
            "latin1.json is not valid JSON: it contains bytes that are not UTF-8 text (line 2, column 15).",
            Check(Write("latin1.json", [.. "{\n  \"name\": \"Ren"u8, 0xE9, .. "\"\n}"u8]), "json"));
    }

    [Fact]
    public void Large_files_are_streamed_through_a_small_buffer()
    {
        // About 12 MB of small values, one 300 KB string (larger than the buffer), then the last value.
        var builder = new StringBuilder("[\n");
        for (var i = 0; i < 200_000; i++)
        {
            builder.Append("  {\"id\": ").Append(i).Append(", \"name\": \"item number ").Append(i).Append("\", \"ok\": true},\n");
        }

        builder.Append("  \"").Append('x', 300_000).Append("\",\n");
        var text = builder.ToString();
        var valid = Write("big.json", text + "  null\n]\n");
        var invalid = Write("bad.json", text + "  nul\n]\n");
        Assert.True(new FileInfo(valid).Length > 10_000_000);

        Assert.Null(Check(valid, "json")); // also fills the buffer pool
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(Check(valid, "json"));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 1024 * 1024, "the file was loaded into memory");

        var problem = Check(invalid, "json");
        Assert.StartsWith("bad.json is not valid JSON: ", problem, StringComparison.Ordinal);
        Assert.Contains($"(line {text.Count(c => c == '\n') + 1}, column ", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Xml_must_be_well_formed_and_its_dtd_is_never_followed()
    {
        Assert.Null(Check(Write("ok.xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<order id=\"1\"><item>book</item></order>"), "xml"));
        Assert.Null(Check(Write("utf16.xml", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("<a/>")]), "xml"));
        var problem = Check(Write("bad.xml", "<order>\n  <item>book</order>"), "xml");
        Assert.StartsWith("bad.xml is not well-formed XML: ", problem, StringComparison.Ordinal);
        Assert.Contains("(line 2, column ", problem, StringComparison.Ordinal);
        Assert.Equal("empty.xml is empty.", Check(Write("empty.xml", ""), "xml"));

        // An external entity would read another file: the DTD is skipped, so the entity is unknown.
        var secret = Write("secret.txt", "TOP-SECRET");
        var entity = Check(Write("entity.xml", $"<!DOCTYPE x [<!ENTITY e SYSTEM \"{new Uri(secret)}\">]>\n<x>&e;</x>"), "xml");
        Assert.StartsWith("entity.xml is not well-formed XML: ", entity, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET", entity, StringComparison.Ordinal);
    }

    [Fact]
    public void Xml_is_read_in_the_legacy_encoding_its_declaration_names()
    {
        // .NET knows these only from its code pages.
        Assert.Null(Check(Write("jp.xml", [.. "<?xml version='1.0' encoding='Shift_JIS'?>\n<a>"u8, 0x93, 0xFA, 0x96, 0x7B, .. "</a>"u8]), "xml"));
        Assert.Contains(
            "The 'a' start tag on line 2 position 2 does not match the end tag of 'b'",
            Check(Write("bad.xml", [.. "<?xml version=\"1.0\" encoding=\"ISO-8859-15\"?>\n<a>"u8, 0xA4, .. "</b>"u8]), "xml"),
            StringComparison.Ordinal);
        Assert.StartsWith("odd.xml is not well-formed XML: System does not support 'x-odd' encoding", Check(Write("odd.xml", "<?xml version=\"1.0\" encoding=\"x-odd\"?><a/>"), "xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Text_types_turn_down_binary_and_utf16_files()
    {
        Assert.Null(Check(Write("notes.txt", "plain text\n"), "text"));
        Assert.Null(Check(Write("empty.html", ""), "html"));
        Assert.Equal(
            "logo.txt looks like a binary file (it contains NUL bytes): choose Binary to send it as it is.",
            Check(Write("logo.txt", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D]), "text"));
        Assert.Equal(
            "wide.js is UTF-16 text: save it as UTF-8, or choose Binary to send it as it is.",
            Check(Write("wide.js", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("x = 1;")]), "javascript"));
    }

    [Fact]
    public void Binary_files_are_sent_unchecked_but_must_exist()
    {
        Assert.Null(Check(Write("data.bin", [0, 1, 2, 255]), null));
        var missing = Path.Combine(_directory, "missing.json");
        Assert.Equal($"{missing} does not exist.", Check(missing, "json"));
        Assert.Equal($"{missing} does not exist.", Check(missing, null));
        Assert.Equal("json", BodyFile.Normalize("JSON"));
        Assert.Null(BodyFile.Normalize("yaml"));
    }
}
