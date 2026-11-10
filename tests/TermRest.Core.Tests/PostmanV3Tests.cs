using System.Text.Json.Nodes;
using TermRest.Core.Http;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Postman.V3;
using TermRest.Core.Scripting;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Variables;
using TermRest.Core.Workspace;
using Xunit;

namespace TermRest.Core.Tests;

public class PostmanV3Tests
{
    private static (ApiWorkspace Workspace, PostmanCollection Collection) Open(string root)
    {
        var workspace = ApiWorkspace.Open(root);
        return (workspace, Assert.Single(workspace.Collections));
    }

    private static IEnumerable<string> Tree(PostmanCollection collection) =>
        collection.Descendants().Select(n => (n is FolderNode ? "[" + n.PathInCollection + "]" : n.PathInCollection));

    [Fact]
    public void Opens_a_postman_v3_workspace()
    {
        var root = V3Fixture.Create();
        var (workspace, collection) = Open(root);

        Assert.True(collection.IsV3);
        Assert.Equal("Widget API", collection.Name);
        Assert.Equal(root, collection.BaseDirectory);
        Assert.Equal(
            ["Health", "[Widgets]", "Widgets/List widgets", "Widgets/Create widget", "Widgets/Upload image", "Widgets/Login form", "Widgets/GraphQL query", "[Admin]", "Admin/Reset"],
            Tree(collection));
        Assert.Contains(workspace.LoadErrors, e => e.Contains("non-HTTP", StringComparison.Ordinal));
        Assert.Equal(["Local"], workspace.Environments.Select(e => e.Name));
        Assert.True(workspace.Environments[0].IsYaml);
        Assert.Equal("Globals", workspace.Globals?.Name);
        Assert.True(workspace.UsesV3Layout);

        var list = collection.FindRequest("Widgets/List widgets")!.ReadSpec();
        Assert.Equal("{{baseUrl}}/v1/widgets?limit=50", list.Url);
        Assert.Equal("Lists the widgets the caller can see. Results are paginated and sorted by name; use the cursor from the previous page.", list.Description);
        Assert.Equal(["limit=50 on", "cursor={{cursor}} off"], list.QueryParams.Select(q => $"{q.Key}={q.Value} {(q.Enabled ? "on" : "off")}"));
        Assert.Equal("From the previous page", list.QueryParams[1].Description);
        Assert.Equal(["Accept: application/json"], list.Headers.Select(h => $"{h.Key}: {h.Value}"));
        Assert.Equal("pm.test('ok', () => pm.response.to.have.status(200));", list.Scripts.For(ScriptLanguage.JavaScript).Test);
        Assert.Null(list.Auth);

        var create = collection.FindRequest("Create widget")!.ReadSpec();
        Assert.Equal(("POST", BodyMode.Raw, "json"), (create.Method, create.Body.Mode, create.Body.RawLanguage));
        Assert.Contains("\"Sprocket\"", create.Body.Raw, StringComparison.Ordinal);
        Assert.Equal("Correlation id", create.Headers[1].Description);
        Assert.False(create.Headers[2].Enabled);

        var upload = collection.FindRequest("Upload image")!.ReadSpec();
        Assert.Equal(BodyMode.FormData, upload.Body.Mode);
        Assert.Equal(["title:text", "file:file"], upload.Body.FormData.Select(f => $"{f.Key}:{f.Type}"));

        var login = collection.FindRequest("Login form")!.ReadSpec();
        Assert.Equal(BodyMode.UrlEncoded, login.Body.Mode);
        Assert.False(login.Body.UrlEncoded[1].Enabled);
        Assert.Equal(("basic", "{{clientId}}"), (login.Auth!.Type, login.Auth.Get("username")));
        Assert.False(login.Settings.StrictSsl);

        var graphql = collection.FindRequest("GraphQL query")!.ReadSpec();
        Assert.Equal((BodyMode.GraphQL, "query { widgets { id name } }", "{\"first\": 5}"), (graphql.Body.Mode, graphql.Body.GraphQLQuery, graphql.Body.GraphQLVariables));

        var reset = collection.FindRequest("Reset")!.ReadSpec();
        Assert.Equal(["id=42"], reset.PathVariables.Select(p => $"{p.Key}={p.Value}"));

        Assert.Equal(["baseUrl", "apiKey"], collection.GetVariables().Select(v => v.Key));
        Assert.True(collection.GetVariables()[1].IsSecret);
    }

    [Fact]
    public void Requests_resolve_inherited_auth_and_variables_like_postman()
    {
        var (workspace, collection) = Open(V3Fixture.Create());
        workspace.ActiveEnvironment = workspace.Environments[0];
        using var runner = new RequestRunner(workspace);
        runner.LocalVariables["token"] = "t0k3n";

        var list = runner.Prepare(collection, collection.FindRequest("List widgets"), collection.FindRequest("List widgets")!.ReadSpec());
        Assert.Equal("http://localhost:8080/v1/widgets?limit=50", list.Url);
        var authorization = list.GetHeader("Authorization")!;
        Assert.StartsWith("Bear" + "er ", authorization, StringComparison.Ordinal);
        Assert.EndsWith(" t0k3n", authorization, StringComparison.Ordinal);

        var health = runner.Prepare(collection, collection.FindRequest("Health"), collection.FindRequest("Health")!.ReadSpec());
        Assert.Null(health.GetHeader("Authorization"));
        Assert.Null(health.GetHeader("X-Api-Key"));

        var reset = runner.Prepare(collection, collection.FindRequest("Reset"), collection.FindRequest("Reset")!.ReadSpec());
        Assert.Equal("http://localhost:8080/v1/widgets/42/reset", reset.Url);
        Assert.Equal("{{apiKey}}", collection.RootNode.Auth!.Get("value"));
    }

    [Fact]
    public void Saving_without_changes_writes_nothing()
    {
        var root = V3Fixture.Create();
        var before = V3Fixture.Snapshot(root);
        var times = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc);
        var (_, collection) = Open(root);

        collection.MarkDirty();
        collection.Save();

        Assert.Equal(before, V3Fixture.Snapshot(root));
        Assert.All(times, t => Assert.Equal(t.Value, File.GetLastWriteTimeUtc(t.Key)));
    }

    [Fact]
    public void Editing_a_request_rewrites_only_the_edited_lines()
    {
        var root = V3Fixture.Create();
        var before = V3Fixture.Snapshot(root);
        var (_, collection) = Open(root);
        var node = collection.FindRequest("Widgets/List widgets")!;
        var spec = node.ReadSpec();
        spec.Url = "{{baseUrl}}/v2/widgets?limit=50";
        spec.QueryParams = PostmanUrl.SyncQueryFromRaw(spec.Url, spec.QueryParams);
        spec.Headers[0].Value = "application/xml";
        collection.UpdateRequest(node, spec);

        var create = collection.FindRequest("Create widget")!;
        var createSpec = create.ReadSpec();
        createSpec.Body.Raw = "{\n  \"name\": \"Gear\",\n  \"size\": 3\n}";
        createSpec.Headers[2].Enabled = true;
        collection.UpdateRequest(create, createSpec);

        collection.Save();
        var after = V3Fixture.Snapshot(root);

        var changedFiles = after.Keys.Where(k => !before.TryGetValue(k, out var text) || text != after[k]).ToList();
        Assert.Equal(["postman/collections/Widget API/Widgets/Create widget.request.yaml", "postman/collections/Widget API/Widgets/List widgets.request.yaml"], changedFiles.Order());
        const string list = "postman/collections/Widget API/Widgets/List widgets.request.yaml";
        Assert.Equal(
            ["- url: '{{baseUrl}}/v1/widgets?limit=50'", "- Accept: application/json", "+ url: '{{baseUrl}}/v2/widgets?limit=50'", "+ Accept: application/xml"],
            TestFiles.ChangedLines(before[list], after[list]));
        const string createFile = "postman/collections/Widget API/Widgets/Create widget.request.yaml";
        Assert.Equal(["- disabled: true", "- \"name\": \"Sprocket\",", "+ \"name\": \"Gear\","], TestFiles.ChangedLines(before[createFile], after[createFile]));

        var reloaded = PostmanCollection.Load(V3Fixture.CollectionPath(root));
        Assert.True(spec.ContentEquals(reloaded.FindRequest("List widgets")!.ReadSpec()));
        Assert.True(createSpec.ContentEquals(reloaded.FindRequest("Create widget")!.ReadSpec()));
    }

    [Fact]
    public void File_bodies_keep_their_type_in_yaml()
    {
        var root = V3Fixture.Create();
        var (_, collection) = Open(root);
        var node = collection.FindRequest("Create widget")!;
        var spec = node.ReadSpec();
        spec.Body = new RequestBody { Mode = BodyMode.File, FileSrc = "payloads/widget.json", FileLanguage = "json" };
        collection.UpdateRequest(node, spec);
        collection.Save();

        var yaml = File.ReadAllText(Path.Combine(root, "postman/collections/Widget API/Widgets/Create widget.request.yaml")).ReplaceLineEndings("\n");
        Assert.Contains("  type: file\n  content:\n    src: payloads/widget.json\n    language: json\n", yaml, StringComparison.Ordinal);
        var reloaded = PostmanCollection.Load(V3Fixture.CollectionPath(root)).FindRequest("Create widget")!.ReadSpec();
        Assert.Equal((BodyMode.File, "payloads/widget.json", "json"), (reloaded.Body.Mode, reloaded.Body.FileSrc, reloaded.Body.FileLanguage));
    }

    [Fact]
    public void Renames_moves_deletes_and_additions_update_the_folder_layout()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        var (_, collection) = Open(root);
        var widgets = collection.FindFolder("Widgets")!;
        var admin = collection.FindFolder("Admin")!;

        collection.Rename(collection.FindRequest("List widgets")!, "List all widgets");
        collection.Move(collection.FindRequest("Create widget")!, collection.RootNode, 1);
        collection.Remove(collection.FindRequest("Upload image")!);
        collection.Rename(admin, "Administration");
        collection.AddRequest(admin, "Audit: who/what?", new HttpRequestSpec { Method = "GET", Url = "{{baseUrl}}/audit" });
        collection.Duplicate(collection.FindRequest("Health")!);
        collection.AddFolder(collection.RootNode, "Reports");
        collection.Save();

        string F(string relative) => Path.Combine(collectionPath, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(F("Widgets/List all widgets.request.yaml")));
        Assert.False(File.Exists(F("Widgets/List widgets.request.yaml")));
        Assert.True(File.Exists(F("Widgets/.resources/List all widgets.resources/examples/200 OK.example.yaml")));
        Assert.False(Directory.Exists(F("Widgets/.resources/List widgets.resources")));
        Assert.Contains("examples: ./.resources/List all widgets.resources/examples", File.ReadAllText(F("Widgets/List all widgets.request.yaml")), StringComparison.Ordinal);
        // Moved and new items get orders between their neighbors (Health 1000, Widgets 2000) or after the last one.
        Assert.True(File.Exists(F("Create widget.request.yaml")));
        Assert.Contains("order: 1666", File.ReadAllText(F("Create widget.request.yaml")), StringComparison.Ordinal);
        Assert.Contains("order: 1333", File.ReadAllText(F("Health Copy.request.yaml")), StringComparison.Ordinal);
        Assert.Equal("$kind: collection\norder: 3000\n", File.ReadAllText(F("Administration/.resources/definition.yaml")));
        Assert.Equal("$kind: collection\norder: 4000\n", File.ReadAllText(F("Reports/.resources/definition.yaml")));
        Assert.False(File.Exists(F("Widgets/Upload image.request.yaml")));
        Assert.True(File.Exists(F("Widgets/Subscribe.request.yaml")), "files TermRest does not show are kept");
        Assert.False(Directory.Exists(F("Admin")));
        Assert.True(File.Exists(F("Administration/Reset.request.yaml")));
        Assert.Contains("name: \"Audit: who/what?\"", File.ReadAllText(F("Administration/Audit- who-what-.request.yaml")), StringComparison.Ordinal);
        Assert.True(File.Exists(F("Health Copy.request.yaml")));
        Assert.True(File.Exists(F("Reports/.resources/definition.yaml")));
        Assert.Empty(Directory.EnumerateDirectories(collectionPath, ".termrest-saving-*"));

        // What is on disk now reads back as the same tree.
        var reloaded = PostmanCollection.Load(collectionPath);
        Assert.Equal(Tree(collection), Tree(reloaded));
        foreach (var request in collection.AllRequests())
        {
            Assert.True(request.ReadSpec().ContentEquals(reloaded.FindRequest(request.PathInCollection)!.ReadSpec()), request.PathInCollection);
        }

        // A second save with no further changes writes nothing.
        var snapshot = V3Fixture.Snapshot(root);
        collection.MarkDirty();
        collection.Save();
        Assert.Equal(snapshot, V3Fixture.Snapshot(root));
    }

    [Fact]
    public void Deleting_a_folder_keeps_requests_termrest_does_not_show()
    {
        var root = V3Fixture.Create();
        var (_, collection) = Open(root);

        collection.Remove(collection.FindFolder("Widgets")!);
        collection.Save();

        var widgets = Path.Combine(V3Fixture.CollectionPath(root), "Widgets");
        Assert.Equal(["Subscribe.request.yaml"], Directory.EnumerateFiles(widgets).Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(widgets, ".resources", "definition.yaml")));
    }

    [Fact]
    public void Collection_level_changes_go_to_the_definition_file()
    {
        var root = V3Fixture.Create();
        var (_, collection) = Open(root);
        var definition = Path.Combine(V3Fixture.CollectionPath(root), ".resources", "definition.yaml");
        var before = File.ReadAllText(definition);

        collection.Name = "Widget API v2";
        var variables = collection.GetVariables();
        variables[0].Value = "https://staging.example.com";
        collection.SetVariables(variables);
        collection.Save();

        Assert.True(Directory.Exists(V3Fixture.CollectionPath(root)), "the folder keeps its name");
        Assert.Equal(["- value: https://api.example.com", "+ name: Widget API v2", "+ value: https://staging.example.com"],
            TestFiles.ChangedLines(before, File.ReadAllText(definition)));
        Assert.Equal("Widget API v2", PostmanCollection.Load(V3Fixture.CollectionPath(root)).Name);
    }

    [Fact]
    public void Environments_convert_to_yaml_the_way_postman_writes_them()
    {
        var root = V3Fixture.Create();
        var json = Path.Combine(root, "Dev.postman_environment.json");
        File.WriteAllText(json, """
            {"id":"6b1c","name":"Dev","values":[
              {"key":"baseUrl","value":"https://api.example.com","type":"default","enabled":true},
              {"key":"token","value":"t-1","type":"secret","enabled":true},
              {"key":"old","value":"","type":"default","enabled":false}
            ],"_postman_variable_scope":"environment"}
            """);
        var yaml = Path.Combine(root, "Dev.environment.yaml");

        PostmanEnvironment.Load(json).Save(yaml);

        Assert.Equal(
            "name: Dev\nvalues:\n  - key: baseUrl\n    value: https://api.example.com\n    enabled: true\n  - key: token\n    value: t-1\n    enabled: true\n    type: secret\n  - key: old\n    value: \"\"\n    enabled: false\n",
            File.ReadAllText(yaml));
    }

    [Fact]
    public void Importing_a_json_collection_into_a_v3_workspace_converts_it_to_a_folder()
    {
        var root = V3Fixture.Create();
        var (workspace, _) = Open(root);
        var source = Path.Combine(AppContext.BaseDirectory, "samples", "TermRest Demo.postman_collection.json");

        var imported = workspace.ImportCollection(source);

        var folder = Path.Combine(root, "postman", "collections", "TermRest Demo");
        Assert.True(imported.IsV3);
        Assert.Equal(Path.GetFullPath(folder), Path.GetFullPath(imported.FilePath!));
        Assert.True(File.Exists(Path.Combine(folder, "Basics", "GET with query.request.yaml")));
        Assert.Empty(Directory.GetFiles(root, "*.json"));
        Assert.Equal(
            PostmanCollection.Load(source).AllRequests().Select(r => r.PathInCollection),
            ApiWorkspace.Open(root).FindCollection(folder)!.AllRequests().Select(r => r.PathInCollection));
    }

    [Fact]
    public void Long_values_are_wrapped_as_the_workspace_setting_says()
    {
        var root = V3Fixture.Create();
        Directory.CreateDirectory(Path.Combine(root, ".termrest"));
        File.WriteAllText(Path.Combine(root, ".termrest", "settings.json"), "{ \"yamlLineWidth\": 80 }");
        var (workspace, collection) = Open(root);
        Assert.Equal(80, collection.YamlLineWidth);
        Assert.Equal(80, workspace.Environments[0].YamlLineWidth);

        var health = collection.FindRequest("Health")!;
        var spec = health.ReadSpec();
        spec.Description = "The health endpoint answers without authentication, so monitors can call it every few seconds to check the service.";
        collection.UpdateRequest(health, spec);
        collection.Save();
        var file = Path.Combine(V3Fixture.CollectionPath(root), "Health.request.yaml");
        Assert.Equal(
            """
            $kind: http-request
            description: The health endpoint answers without authentication, so monitors can
              call it every few seconds to check the service.
            url: '{{baseUrl}}/health'
            method: GET
            auth:
              type: noauth
            order: 1000

            """.ReplaceLineEndings("\n"),
            File.ReadAllText(file));

        var environment = workspace.Environments[0];
        environment.SetValue("baseUrl", "http://localhost:8080 is where the service listens when it runs on a developer machine with default settings");
        environment.Save();
        Assert.Contains(
            "    value: 'http://localhost:8080 is where the service listens when it runs on a\n      developer machine with default settings'\n",
            File.ReadAllText(environment.FilePath!),
            StringComparison.Ordinal);

        var imported = workspace.ImportCollection(Path.Combine(AppContext.BaseDirectory, "samples", "TermRest Demo.postman_collection.json"));
        Assert.StartsWith(
            "$kind: collection\ndescription: Sample requests against Postman Echo (https://postman-echo.com).\n  Select the 'Postman Echo' environment to set {{baseUrl}}.\n",
            File.ReadAllText(V3Names.DefinitionPath(imported.FilePath!)),
            StringComparison.Ordinal);
        Assert.Equal(80, workspace.CreateCollection("Created").YamlLineWidth);

        // Changing the setting applies to everything that is open.
        workspace.Settings.YamlLineWidth = 0;
        workspace.SaveSettings();
        Assert.All(workspace.Collections, c => Assert.Equal(0, c.YamlLineWidth));
        Assert.Equal(0, environment.YamlLineWidth);
        spec.Description = "Second edit, still a long description, written on one line because wrapping is now turned off for this workspace.";
        collection.UpdateRequest(health, spec);
        collection.Save();
        Assert.Contains("\ndescription: Second edit, still a long description, written on one line because wrapping is now turned off for this workspace.\n", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Contains("\"yamlLineWidth\": 0", File.ReadAllText(workspace.SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void By_default_new_values_are_wrapped_like_the_collection_s_files()
    {
        // A collection that a wrapping Postman version wrote: new and changed values are wrapped the same way.
        var root = V3Fixture.Create();
        var listFile = Path.Combine(V3Fixture.CollectionPath(root), "Widgets", "List widgets.request.yaml");
        File.WriteAllText(listFile, File.ReadAllText(listFile).Replace(
            "description: Lists the widgets the caller can see. Results are paginated and sorted by\n  name; use the cursor from the previous page.",
            "description: Lists every widget, including archived ones, so that this\n  description is long enough to be wrapped by Postman.",
            StringComparison.Ordinal));
        var (_, collection) = Open(root);
        Assert.Null(collection.YamlLineWidth);
        Assert.Equal(80, collection.EffectiveYamlLineWidth);
        const string description = "The health endpoint answers without authentication, so monitors can call it every few seconds to check the service.";

        var health = collection.FindRequest("Health")!;
        var spec = health.ReadSpec();
        spec.Description = description;
        collection.UpdateRequest(health, spec);
        collection.Save();
        Assert.StartsWith(
            "$kind: http-request\ndescription: The health endpoint answers without authentication, so monitors can\n  call it every few seconds to check the service.\n",
            File.ReadAllText(Path.Combine(V3Fixture.CollectionPath(root), "Health.request.yaml")),
            StringComparison.Ordinal);

        // Long values that are not wrapped (or none long enough to tell): like current Postman versions, no wrapping.
        var plainRoot = V3Fixture.Create();
        var (_, plain) = Open(plainRoot);
        Assert.Equal(0, plain.EffectiveYamlLineWidth);
        health = plain.FindRequest("Health")!;
        plain.UpdateRequest(health, spec);
        plain.Save();
        Assert.StartsWith(
            $"$kind: http-request\ndescription: {description}\n",
            File.ReadAllText(Path.Combine(V3Fixture.CollectionPath(plainRoot), "Health.request.yaml")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Converting_to_v3_wraps_at_the_requested_width()
    {
        var source = PostmanCollection.Load(Path.Combine(AppContext.BaseDirectory, "samples", "TermRest Demo.postman_collection.json"));
        var directory = TestFiles.NewTempDirectory();

        var wrapped = source.ConvertToV3(Path.Combine(directory, "wrapped", "TermRest Demo"), 80);
        var unwrapped = source.ConvertToV3(Path.Combine(directory, "unwrapped", "TermRest Demo"));

        Assert.StartsWith(
            "$kind: collection\ndescription: Sample requests against Postman Echo (https://postman-echo.com).\n  Select the 'Postman Echo' environment to set {{baseUrl}}.\n",
            File.ReadAllText(V3Names.DefinitionPath(wrapped.FilePath!)),
            StringComparison.Ordinal);
        Assert.StartsWith(
            "$kind: collection\ndescription: Sample requests against Postman Echo (https://postman-echo.com). Select the 'Postman Echo' environment to set {{baseUrl}}.\n",
            File.ReadAllText(V3Names.DefinitionPath(unwrapped.FilePath!)),
            StringComparison.Ordinal);
        Assert.Equal(80, PostmanCollection.Load(wrapped.FilePath!).EffectiveYamlLineWidth);
        Assert.Equal(0, PostmanCollection.Load(unwrapped.FilePath!).EffectiveYamlLineWidth);
        Assert.Empty(PostmanCollection.Load(wrapped.FilePath!).Verify());
    }

    [Fact]
    public void Yaml_environments_save_only_changed_values()
    {
        var root = V3Fixture.Create();
        var (workspace, _) = Open(root);
        var environment = workspace.Environments[0];
        var path = environment.FilePath!;
        var before = File.ReadAllText(path);

        environment.SetValue("baseUrl", "http://localhost:9090");
        environment.SetValue("apiKey", "k-123");
        environment.Save();

        Assert.Equal(
            ["- value: 'http://localhost:8080'", "+ value: 'http://localhost:9090'", "+ - key: apiKey", "+ value: k-123"],
            TestFiles.ChangedLines(before, File.ReadAllText(path)));
        Assert.EndsWith("  - key: apiKey\n    value: k-123\n    enabled: true\n", File.ReadAllText(path), StringComparison.Ordinal);
        var reloaded = PostmanEnvironment.Load(path);
        Assert.Equal(["baseUrl", "token", "cursor", "apiKey"], reloaded.Values.Select(v => v.Key));
        Assert.True(reloaded.Values[1].IsSecret);
        Assert.False(reloaded.Values[3].IsSecret);

        var created = workspace.CreateEnvironment("Staging / EU");
        Assert.Equal(Path.Combine(root, "postman", "environments", "Staging - EU.environment.yaml"), created.FilePath);
        Assert.Equal("name: Staging / EU\nvalues: []\n", File.ReadAllText(created.FilePath!));
    }

    [Fact]
    public void New_collections_follow_the_workspace_layout()
    {
        var v3Root = V3Fixture.Create();
        var (v3Workspace, _) = Open(v3Root);
        var created = v3Workspace.CreateCollection("Orders API");
        Assert.True(created.IsV3);
        Assert.Equal("$kind: collection\n", File.ReadAllText(Path.Combine(v3Root, "postman", "collections", "Orders API", ".resources", "definition.yaml")));

        created.AddRequest(created.RootNode, "List orders", new HttpRequestSpec { Url = "{{baseUrl}}/orders" });
        created.Save();
        Assert.Equal("$kind: http-request\nurl: \"{{baseUrl}}/orders\"\nmethod: GET\norder: 1000\n",
            File.ReadAllText(Path.Combine(v3Root, "postman", "collections", "Orders API", "List orders.request.yaml")));

        var jsonWorkspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        Assert.False(jsonWorkspace.UsesV3Layout);
        Assert.False(jsonWorkspace.CreateCollection("Plain").IsV3);
        jsonWorkspace.Settings.CollectionFormat = "v3";
        Assert.True(jsonWorkspace.CreateCollection("Forced").IsV3);
    }

    [Fact]
    public void Opening_a_collection_folder_opens_its_workspace()
    {
        var root = V3Fixture.Create();

        var workspace = ApiWorkspace.Open(V3Fixture.CollectionPath(root));

        Assert.Equal(root, workspace.RootPath);
        Assert.Equal(V3Fixture.CollectionPath(root), workspace.FindCollection(V3Fixture.CollectionPath(root))?.FilePath);
        Assert.Single(workspace.Environments);
    }

    [Theory]
    [InlineData("TermRest Demo.postman_collection.json")]
    [InlineData("complex.postman_collection.json")]
    public void Converting_v21_to_v3_and_back_keeps_every_request(string file)
    {
        var source = PostmanCollection.Load(file.StartsWith("TermRest", StringComparison.Ordinal) ? TestFiles.DemoCollection : TestFiles.Data(file));
        var target = Path.Combine(TestFiles.NewTempDirectory(), source.Name);

        var v3 = V3CollectionStorage.ConvertFrom(source, target);
        var back = PostmanCollection.Parse(v3.ExportV21Json());

        Assert.Equal(Tree(source), Tree(v3));
        Assert.Equal(Tree(source), Tree(back));
        var preparer = new RequestPreparer(VariableResolver.Empty, null, new ExecutionDefaults());
        foreach (var request in source.AllRequests())
        {
            var expected = preparer.Prepare(request.ReadSpec(), source.GetEffectiveAuth(request), source.BaseDirectory);
            foreach (var converted in (PostmanCollection[])[v3, back])
            {
                var node = converted.FindRequest(request.PathInCollection)!;
                var actual = preparer.Prepare(node.ReadSpec(), converted.GetEffectiveAuth(node), source.BaseDirectory);
                Assert.Equal(expected.Method, actual.Method);
                Assert.Equal(expected.Url, actual.Url);
                Assert.Equal(expected.Headers, actual.Headers);
                if (request.ReadSpec().Body.Raw.Contains("{{$", StringComparison.Ordinal))
                {
                    Assert.Equal(request.ReadSpec().Body.Raw, node.ReadSpec().Body.Raw); // dynamic variables differ per send
                }
                else
                {
                    Assert.Equal(expected.Body.Describe(), actual.Body.Describe());
                }

                Assert.Equal(expected.AuthDescription, actual.AuthDescription);
                Assert.Equal(request.ReadSpec().Scripts, node.ReadSpec().Scripts);
            }
        }

        Assert.Equal(source.GetVariables().Select(v => $"{v.Key}={v.Value}"), v3.GetVariables().Select(v => $"{v.Key}={v.Value}"));
        var examples = source.AllRequests().Sum(r => (r.Json["response"] as JsonArray)?.Count ?? 0);
        Assert.Equal(examples, Directory.EnumerateFiles(target, "*.example.yaml", SearchOption.AllDirectories).Count());
    }
}
