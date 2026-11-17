using System.Text;
using System.Text.Json;
using TermRest.Core.Http;
using TermRest.Core.Json;
using Xunit;

namespace TermRest.Core.Tests;

public class JsonPathTests
{
    /// <summary>The example of RFC 9535, section 1.5.</summary>
    private const string Store = """
        { "store": {
            "book": [
              { "category": "reference", "author": "Nigel Rees", "title": "Sayings of the Century", "price": 8.95 },
              { "category": "fiction", "author": "Evelyn Waugh", "title": "Sword of Honour", "price": 12.99 },
              { "category": "fiction", "author": "Herman Melville", "title": "Moby Dick", "isbn": "0-553-21311-3", "price": 8.99 },
              { "category": "fiction", "author": "J. R. R. Tolkien", "title": "The Lord of the Rings", "isbn": "0-395-19395-8", "price": 22.99 }
            ],
            "bicycle": { "color": "red", "price": 399 }
          }
        }
        """;

    [Theory]
    [InlineData("$.store.book[*].author", """["Nigel Rees","Evelyn Waugh","Herman Melville","J. R. R. Tolkien"]""")]
    [InlineData("$..author", """["Nigel Rees","Evelyn Waugh","Herman Melville","J. R. R. Tolkien"]""")]
    [InlineData("$.store..price", "[8.95,12.99,8.99,22.99,399]")]
    [InlineData("$..book[2].author", """["Herman Melville"]""")]
    [InlineData("$..book[-1].title", """["The Lord of the Rings"]""")]
    [InlineData("$..book[:2].title", """["Sayings of the Century","Sword of Honour"]""")]
    [InlineData("$..book[::-2].title", """["The Lord of the Rings","Sword of Honour"]""")]
    [InlineData("$..book[?@.isbn].title", """["Moby Dick","The Lord of the Rings"]""")]
    [InlineData("$..book[?@.price<10].title", """["Sayings of the Century","Moby Dick"]""")]
    [InlineData("$.store.book[?@.category == 'fiction' && @.price > 20].author", """["J. R. R. Tolkien"]""")]
    [InlineData("$..book[?search(@.author, 'Mel+')].title", """["Moby Dick"]""")]
    [InlineData("$..book[?length(@.title) > 20].title", """["Sayings of the Century","The Lord of the Rings"]""")]
    [InlineData("$.store[?count(@.*) == 2]", """[{"color":"red","price":399}]""")]
    [InlineData("$.store[ 'bicycle' , 'nothing' ] .color", """["red"]""")]
    [InlineData("$.nothing", "[]")]
    public void Selects_like_rfc_9535(string query, string expected) => Assert.Equal(expected, Filter(Store, query));

    [Theory]
    [InlineData("""[{"a":1},{"a":1.0},{"a":1e0},{"a":10E-1},{"a":"1"},{"a":true}]""", "$[?@.a == 1]", """[{"a":1},{"a":1.0},{"a":1e0},{"a":10E-1}]""")]
    [InlineData("[9007199254740993,9007199254740992,1e400,-0]", "$[?@ == 9007199254740993 || @ > 1e399 || @ == 0]", "[9007199254740993,1e400,-0]")]
    [InlineData("""[{"a":null},{"b":1},{}]""", "$[?@.a == @.c]", """[{"b":1},{}]""")]
    [InlineData("""[[1,2],[1,2.0],[2,1],{"x":[1]},{"x":[1.0]}]""", "$[?@ == $[0] || @ == $[3]]", """[[1,2],[1,2.0],{"x":[1]},{"x":[1.0]}]""")]
    [InlineData("""["\uFFFF","😀","a"]""", """$[?@ > '\uFFFF']""", """["😀"]""")]
    [InlineData("""[true,false,null,"b"]""", "$[?@ < 'c' || @ == true]", """[true,"b"]""")]
    public void Compares_values_as_rfc_9535_says(string document, string query, string expected) => Assert.Equal(expected, Filter(document, query));

    [Theory]
    [InlineData("""["😀😀","ab","abc"]""", "$[?length(@) == 2]", """["😀😀","ab"]""")]
    [InlineData("""[{"a":[1,2,3]},{"a":[1]},{"a":7}]""", "$[?count(@.a[*]) > 1]", """[{"a":[1,2,3]}]""")]
    [InlineData("""[{"a":[5]},{"a":[5,6]}]""", "$[?value(@.a[*]) == 5]", """[{"a":[5]}]""")]
    [InlineData("""["a😀b","a😀😀b","ab"]""", "$[?match(@, 'a.b')]", """["a😀b"]""")]
    [InlineData("""["😀","😁","😃","x"]""", "$[?match(@, '[😀-😁]+')]", """["😀","😁"]""")]
    [InlineData("""["😀","😃","x","😀😀"]""", "$[?match(@, '[^😀]')]", """["😃","x"]""")]
    [InlineData("""["𝒜","A","a","1"]""", """$[?match(@, '\\p{Lu}')]""", """["𝒜","A"]""")]
    [InlineData("""["𝒜","A","a","1"]""", """$[?match(@, '[\\P{Lu}]')]""", """["a","1"]""")]
    [InlineData("""["abc","xabcx"]""", "$[?search(@, 'b')]", """["abc","xabcx"]""")]
    [InlineData("""["abc","xabcx"]""", "$[?match(@, 'b')]", "[]")]
    [InlineData("""["abc","x\nabc","abc\n"]""", "$[?search(@, '^abc$')]", """["abc"]""")]
    [InlineData("""["a(b","ab"]""", "$[?match(@, 'a(b')]", "[]")]
    [InlineData("""{"pattern":"[0-9]+","values":["12","a1"]}""", "$.values[?match(@, $.pattern)]", """["12"]""")]
    public void Functions_work_on_characters_not_utf16_units(string document, string query, string expected) => Assert.Equal(expected, Filter(document, query));

    [Theory]
    [InlineData("", "The query is empty", 0)]
    [InlineData("$.a[", "The query ends where", 4)]
    [InlineData("$[?@.a = 1]", "Compare with ==", 7)]
    [InlineData("$[?length(@.*) > 1]", "can select more than one value", 10)]
    [InlineData("$[?foo(@)]", "Unknown function foo()", 3)]
    [InlineData("$[?match(@.a, 'x') == true]", "match() is true or false, not a value", 3)]
    [InlineData("$[?count(@.a)]", "count() is a value, not a test", 3)]
    [InlineData("$[?@.a == True]", "Unexpected 'T'", 10)]
    [InlineData("$[01]", "does not start with 0", 2)]
    [InlineData("$[9007199254740992]", "out of range", 2)]
    [InlineData(@"$['a\x']", @"\x is not an escape", 4)]
    [InlineData(@"$['\uD83D']", "is half of a character", 3)]
    public void Says_where_a_query_is_wrong(string query, string message, int position)
    {
        var ex = Assert.Throws<JsonPathException>(() => JsonPath.Parse(query));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.Equal(position, ex.Position);
        Assert.EndsWith($"(character {position + 1}).", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deeply_nested_query_is_refused_rather_than_run_out_of_stack()
    {
        var query = "$[?" + new string('(', 10_000) + "@" + new string(')', 10_000) + "]";
        var ex = Assert.Throws<JsonPathException>(() => JsonPath.Parse(query));
        Assert.Contains("more than 64 deep", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cancelled_filter_stops()
    {
        var body = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Range(0, 100_000)) + "]");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => BodyFilter.Apply(JsonPath.Parse("$..*"), body, "application/json", cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Json_lines_are_filtered_as_an_array_of_their_values()
    {
        var body = "{\"id\":1}\n\n{\"id\":2}\r\n{\"id\":3}\n"u8.ToArray();
        Assert.Equal("[1,3]", Apply("$[0,2].id", body, "application/x-ndjson", indented: false).Text);

        // One value written over several lines, as some servers label any JSON.
        Assert.Equal("[1]", Apply("$.id", "{\n\"id\": 1\n}"u8.ToArray(), "application/x-ndjson", indented: false).Text);
    }

    [Fact]
    public void A_body_is_read_in_its_charset_and_results_are_indented_by_two_spaces()
    {
        var body = Encoding.Latin1.GetBytes("{\"name\":{\"first\":\"José\"}}");
        var result = Apply("$.name", body, "application/json; charset=iso-8859-1");
        Assert.Equal("[\n  {\n    \"first\": \"José\"\n  }\n]", result.Text);
        Assert.Equal((1, 1), (result.Count, result.Shown));
    }

    [Fact]
    public void Values_beyond_the_length_asked_for_are_left_out_and_counted()
    {
        var body = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Range(0, 1000)) + "]");
        var result = Apply("$[*]", body, "application/json", indented: false, maxBytes: 100);
        Assert.Equal(1000, result.Count);
        Assert.InRange(result.Shown, 20, 40);
        using var shown = JsonDocument.Parse(result.Text);
        Assert.Equal(result.Shown, shown.RootElement.GetArrayLength());
    }

    [Theory]
    [InlineData("", "application/json", "The body is empty.")]
    [InlineData("<a/>", "application/xml", "The body is not JSON: line 1, byte 1.")]
    [InlineData("[\"\\ud83d\"]", "application/json", "The body has a string holding half of a character")]
    public void Says_why_a_body_cannot_be_filtered(string body, string contentType, string message)
    {
        var ex = Assert.Throws<JsonPathException>(() => Apply("$[0]", Encoding.UTF8.GetBytes(body), contentType));
        Assert.StartsWith(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_binary_body_cannot_be_filtered()
    {
        var ex = Assert.Throws<JsonPathException>(() => Apply("$", [0x89, 0x50, 0x4E, 0x47, 0x00], "image/png"));
        Assert.Equal("The body is binary, not JSON.", ex.Message);
    }

    /// <summary>
    /// Opt-in check against the JSONPath Compliance Test Suite (set TERMREST_JSONPATH_CTS to its cts.json, from
    /// github.com/jsonpath-standard/jsonpath-compliance-test-suite): invalid queries are refused, and valid ones select
    /// what the suite expects.
    /// </summary>
    [Fact]
    public void Passes_the_jsonpath_compliance_test_suite()
    {
        var path = Environment.GetEnvironmentVariable("TERMREST_JSONPATH_CTS");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Assert.Skip("Set TERMREST_JSONPATH_CTS to the cts.json of the JSONPath Compliance Test Suite to run.");
            return;
        }

        using var suite = JsonDocument.Parse(File.ReadAllText(path));
        var tests = suite.RootElement.GetProperty("tests");
        var failures = new List<string>();
        foreach (var test in tests.EnumerateArray())
        {
            var name = test.GetProperty("name").GetString();
            var selector = test.GetProperty("selector").GetString()!;
            if (test.TryGetProperty("invalid_selector", out var invalid) && invalid.GetBoolean())
            {
                try
                {
                    JsonPath.Parse(selector);
                    failures.Add($"{name}: {selector} is not valid, but parsed");
                }
                catch (JsonPathException)
                {
                }

                continue;
            }

            try
            {
                var actual = JsonPath.Parse(selector).Select(test.GetProperty("document"), TestContext.Current.CancellationToken);
                var expected = test.TryGetProperty("result", out var result) ? [result] : test.GetProperty("results").EnumerateArray().ToList();
                if (!expected.Any(e => e.GetArrayLength() == actual.Count && e.EnumerateArray().Zip(actual).All(p => JsonElement.DeepEquals(p.First, p.Second))))
                {
                    failures.Add($"{name}: {selector} selected [{string.Join(",", actual.Select(a => a.GetRawText()))}], expected {expected[0].GetRawText()}");
                }
            }
            catch (JsonPathException ex)
            {
                failures.Add($"{name}: {selector} failed: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {tests.GetArrayLength()} failed:\n" + string.Join("\n", failures));
        TestContext.Current.SendDiagnosticMessage($"{tests.GetArrayLength()} compliance tests passed.");
    }

    private static string Filter(string document, string query) => Apply(query, Encoding.UTF8.GetBytes(document), "application/json", indented: false).Text;

    private static FilteredBody Apply(string query, byte[] body, string contentType, bool indented = true, long maxBytes = long.MaxValue) =>
        BodyFilter.Apply(JsonPath.Parse(query), body, contentType, indented, maxBytes, TestContext.Current.CancellationToken);
}
