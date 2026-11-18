using System.Text.Json.Nodes;
using TermRest.Core.Http;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Scripting;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using Xunit;

namespace TermRest.Core.Tests;

public class IterationDataTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private async Task<List<string>> RowsAsync(string content, string extension)
    {
        var path = Path.Combine(TestFiles.NewTempDirectory(), "data" + extension);
        await File.WriteAllTextAsync(path, content, _ct);
        var rows = new List<string>();
        var relaxed = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        await foreach (var row in IterationDataFile.Open(path).ReadAsync(_ct))
        {
            rows.Add(row.ToJsonString(relaxed));
        }

        return rows;
    }

    [Fact]
    public async Task A_csv_data_file_has_a_header_row_and_quoted_values_as_rfc_4180_has_them()
    {
        var rows = await RowsAsync(
            "\uFEFFname,note,city\r\n" +
            "Ada,\"says \"\"hi\"\", then, bye\",London\r\n" +
            "\r\n" +
            ",,\r\n" + // a spreadsheet's empty row
            "Grace,\"two\nlines\"\n" + // a value missing at the end
            "Zoë,x\"y,Paris,\n", // a quote inside a value; an empty value after the last
            ".csv");

        Assert.Equal(
        [
            """{"name":"Ada","note":"says \"hi\", then, bye","city":"London"}""",
            """{"name":"Grace","note":"two\nlines","city":""}""",
            """{"name":"Zoë","note":"x\"y","city":"Paris"}""",
        ], rows);

        var bad = Assert.Throws<InvalidDataException>(() => RowsAsync("a,b\n1,2,3\n", ".csv").GetAwaiter().GetResult());
        Assert.Equal("data.csv: row 1 has 3 values, but the header row names 2. Put a value with a comma in double quotes.", bad.Message);
    }

    [Fact]
    public async Task A_json_data_file_is_an_array_of_objects_whose_values_keep_their_types()
    {
        var rows = await RowsAsync("""  [ { "id": 7, "admin": true, "tags": ["a"], "name": null }, { "id": 8 } ]""", ".json");
        Assert.Equal(["""{"id":7,"admin":true,"tags":["a"],"name":null}""", """{"id":8}"""], rows);

        // JSON whatever the extension, as its first character says; CSV otherwise.
        Assert.Equal(["""{"id":1}"""], await RowsAsync("""[{"id": 1}]""", ".txt"));

        var notObject = Assert.Throws<InvalidDataException>(() => RowsAsync("""[{"id": 1}, 2]""", ".json").GetAwaiter().GetResult());
        Assert.Equal("data.json: item 2 of the array is not an object ({\"name\": value, …}) but number.", notObject.Message);
        var broken = Assert.Throws<InvalidDataException>(() => RowsAsync("""[{"id": 1""", ".json").GetAwaiter().GetResult());
        Assert.StartsWith("data.json is not a JSON array of objects:", broken.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Data_variables_come_after_this_requests_and_before_the_environments()
    {
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var environment = workspace.CreateEnvironment("dev");
        environment.SetValues([new KeyValueEntry { Key = "who", Value = "env" }, new KeyValueEntry { Key = "host", Value = "env.example.com" }]);
        workspace.ActiveEnvironment = environment;
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace) { Iteration = 1, IterationCount = 3 };
        runner.IterationData = (JsonObject)JsonNode.Parse("""{ "who": "data", "id": 42, "admin": true, "tags": ["a"] }""")!;
        runner.LocalVariables["id"] = "local";

        var prepared = runner.Prepare(collection, null, new HttpRequestSpec { Url = "https://{{host}}/{{who}}/{{id}}?admin={{admin}}&tags={{tags}}" });

        Assert.Equal("https://env.example.com/data/local?admin=true&tags=[\"a\"]", prepared.Url);
    }

    public static TheoryData<string> Languages => [ScriptLanguage.JavaScript, ScriptLanguage.Python, ScriptLanguage.CSharp, ScriptLanguage.PowerShell];

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task Scripts_see_the_iteration_and_its_data(string language)
    {
        var runtime = language switch
        {
            ScriptLanguage.JavaScript => TestJavaScript.Require(),
            ScriptLanguage.Python => TestPython.Require(),
            ScriptLanguage.CSharp => TestCSharp.Require(),
            _ => TestPowerShell.Require(),
        };
        await using var server = await EchoServer.StartAsync();
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var collection = workspace.CreateCollection("API");
        var test = language switch
        {
            ScriptLanguage.JavaScript => """
                pm.test('data', () => {
                  pm.expect(pm.iterationData.get('count')).to.equal(3);
                  pm.expect(pm.iterationData.get('name')).to.equal('Ada');
                  pm.expect(pm.variables.get('name')).to.equal('Ada');
                  pm.expect([pm.info.iteration, pm.info.iterationCount]).to.eql([1, 2]);
                  pm.expect(data.name).to.equal('Ada');
                });
                """,
            ScriptLanguage.Python => """
                tr.test("data", lambda: None)
                assert tr.iteration_data.get("count") == 3
                assert tr.iteration_data.get("name") == "Ada" and tr.variables.get("name") == "Ada"
                assert (tr.info.iteration, tr.info.iteration_count) == (1, 2)
                """,
            ScriptLanguage.CSharp => """
                tr.Test("data", () => { });
                tr.AssertEqual("3", tr.IterationData.Get("count"));
                tr.AssertEqual("Ada", tr.Variables.Get("name"));
                tr.AssertEqual(1, tr.Info.Iteration);
                tr.AssertEqual(2, tr.Info.IterationCount);
                """,
            _ => """
                $tr.Test('data', { })
                $tr.AssertEqual('3', $tr.IterationData.Get('count'))
                $tr.AssertEqual('Ada', $tr.Variables.Get('name'))
                $tr.AssertEqual(1, $tr.Info.Iteration)
                $tr.AssertEqual(2, $tr.Info.IterationCount)
                """,
        };
        var request = collection.AddRequest(collection.RootNode, "Echo", new HttpRequestSpec
        {
            Url = server.Url("echo") + "?name={{name}}",
            Scripts = new ScriptSet("", test, language),
        });
        using var runner = new RequestRunner(workspace) { Iteration = 1, IterationCount = 2 };
        runner.SetRuntime(language, runtime);
        runner.IterationData = (JsonObject)JsonNode.Parse("""{ "name": "Ada", "count": 3 }""")!;

        var exchange = await runner.SendAsync(collection, request, request.ReadSpec(), null, _ct);

        var results = exchange.Scripts!;
        Assert.True(results.Error is null, results.Describe());
        Assert.Equal([("data", true)], results.Tests.Select(t => (t.Name, t.Passed)));
        Assert.EndsWith("?name=Ada", exchange.Request.Url, StringComparison.Ordinal);
    }
}
