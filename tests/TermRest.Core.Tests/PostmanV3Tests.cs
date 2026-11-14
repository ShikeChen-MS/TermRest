using System.Text.Json.Nodes;
using TermRest.Core.Http;
using TermRest.Core.IO;
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
    public void A_collection_with_a_folder_named_postman_opens_as_a_collection_of_its_workspace()
    {
        var root = TestFiles.NewTempDirectory();
        var integrations = Path.Combine(root, "postman", "collections", "Integrations");
        foreach (var folder in (string[])["GitHub", "postman"])
        {
            Directory.CreateDirectory(Path.Combine(integrations, folder));
            File.WriteAllText(Path.Combine(integrations, folder, "Ping.request.yaml"), $"$kind: http-request\nurl: https://{folder.ToLowerInvariant()}.example.com/ping\nmethod: GET\n");
        }

        var workspace = ApiWorkspace.Open(integrations);

        Assert.Equal(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(workspace.RootPath));
        Assert.Equal(Path.TrimEndingDirectorySeparator(root), ApiWorkspace.RootFor(integrations));
        Assert.Equal(["Integrations"], workspace.Collections.Select(c => c.Name));
        Assert.NotNull(workspace.FindCollection(integrations));
    }

    [Fact]
    public void A_collection_whose_requests_are_all_in_folders_and_that_has_no_definition_is_loaded()
    {
        // Postman writes no definition for a collection without settings of its own; a folder of other files is no collection.
        var root = TestFiles.NewTempDirectory();
        var shop = Path.Combine(root, "postman", "collections", "Shop");
        Directory.CreateDirectory(Path.Combine(shop, "Cart", "Items"));
        File.WriteAllText(Path.Combine(shop, "Cart", "Items", "Add item.request.yaml"), "$kind: http-request\nurl: https://shop.example.com/cart/items\nmethod: POST\n");
        var stray = Path.Combine(root, "postman", "collections", "postman", "environments");
        Directory.CreateDirectory(stray);
        File.WriteAllText(Path.Combine(stray, "Dev.environment.yaml"), "name: Dev\nvalues: []\n");

        var (_, collection) = Open(root);

        Assert.Equal(("Shop", true), (collection.Name, collection.IsV3));
        Assert.Equal(["[Cart]", "[Cart/Items]", "Cart/Items/Add item"], Tree(collection));
        Assert.Throws<IOException>(() => PostmanCollection.Load(Path.Combine(root, "postman", "collections", "postman")));
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
    public void Credential_sets_are_chosen_by_their_url_rules_and_go_nowhere_else()
    {
        // Postman writes a set's rules as a map (url: pattern), its schema as a list of {key, value}; values may hold variables.
        var root = V3Fixture.Create();
        var definition = Path.Combine(V3Fixture.CollectionPath(root), "Widgets", ".resources", "definition.yaml");
        File.WriteAllText(definition, """
            $kind: collection
            auth:
              - id: p
                type: bearer
                name: Production
                credentials:
                  token: prod-token-123
                rules:
                  url: https://api.example.com/*
              - id: s
                type: bearer
                name: Sandbox
                credentials:
                  token: '{{sandboxToken}}'
                rules:
                  - key: url
                    value: '{{sandboxHost}}'
            order: 2000

            """.ReplaceLineEndings("\n"));
        var (workspace, collection) = Open(root);
        using var runner = new RequestRunner(workspace);
        runner.LocalVariables["sandboxToken"] = "sandbox-token-456";
        runner.LocalVariables["sandboxHost"] = "sandbox.example.com";
        var node = collection.FindRequest("Widgets/List widgets")!;
        PreparedRequest Prepare(string url)
        {
            var spec = node.ReadSpec();
            spec.Url = url;
            spec.QueryParams = [];
            return runner.Prepare(collection, node, spec);
        }

        var production = Prepare("https://api.example.com/v1/widgets");
        var sandbox = Prepare("https://sandbox.example.com/v1/widgets");
        var elsewhere = Prepare("https://hooks.example.org/notify");

        Assert.Equal("Bearer prod-token-123", production.GetHeader("Authorization"));
        Assert.Equal("Bearer sandbox-token-456", sandbox.GetHeader("Authorization"));
        Assert.Null(elsewhere.GetHeader("Authorization"));
        Assert.Contains(elsewhere.Warnings, w => w.StartsWith("No credential set of the inherited auth ('Production', 'Sandbox')", StringComparison.Ordinal));

        // The history keeps the set that was used, and none of the others; where none matched, no auth (not "inherit", which
        // an entry sent again on its own would take from the collection).
        Assert.Equal("{{sandboxToken}}", sandbox.Source!.Spec.Auth!.Get("token"));
        Assert.True(sandbox.Source.AuthInherited);
        Assert.Equal("noauth", elsewhere.Source!.Spec.Auth!.Type);

        // The file keeps its sets; a v2.1 copy can hold one auth only, so its first set stands in for them, and says so.
        var written = File.ReadAllText(definition);
        collection.MarkDirty();
        collection.Save();
        var writtenFile = File.ReadAllText(definition);
        Assert.Equal(written, writtenFile);
        var exported = collection.ExportV21Json();
        Assert.DoesNotContain(V3Convert.AuthSetsProperty, exported, StringComparison.Ordinal);
        Assert.DoesNotContain("sandbox", exported, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["folder 'Widgets'"], collection.CredentialSetsLeftOutOfV21());

        // A copy of the folder, which has no file to write over, keeps the sets and their rules, with ids of its own.
        collection.Duplicate(collection.FindFolder("Widgets")!);
        collection.Save();
        var copied = File.ReadAllText(Path.Combine(V3Fixture.CollectionPath(root), "Widgets Copy", ".resources", "definition.yaml"));
        Assert.DoesNotContain("id: p\n", copied, StringComparison.Ordinal);
        Assert.DoesNotContain("id: s\n", copied, StringComparison.Ordinal);
        var (_, reloaded) = Open(root);
        var sets = reloaded.FindFolder("Widgets Copy")!.Auth!.CredentialSets!;
        Assert.Equal(["Production", "Sandbox"], sets.Select(s => s.Name));
        Assert.Equal([KeyValuePair.Create("url", "https://api.example.com/*")], sets[0].Rules);
        Assert.Equal([KeyValuePair.Create("url", "{{sandboxHost}}")], sets[1].Rules);
    }

    [Fact]
    public void A_file_without_a_final_line_break_is_saved_without_one()
    {
        var root = V3Fixture.Create();
        var request = Path.Combine(V3Fixture.CollectionPath(root), "Health.request.yaml");
        var environmentFile = Path.Combine(root, "postman", "environments", "Local.environment.yaml");
        foreach (var file in (string[])[request, environmentFile])
        {
            File.WriteAllText(file, File.ReadAllText(file).TrimEnd('\n'));
        }

        var (workspace, collection) = Open(root);
        var health = collection.FindRequest("Health")!;
        var spec = health.ReadSpec();
        spec.Description = "Edited.";
        collection.UpdateRequest(health, spec);
        collection.Save();
        var environment = workspace.Environments[0];
        environment.SetValue("baseUrl", "http://localhost:9090");
        environment.Save();

        Assert.Contains("description: Edited.", File.ReadAllText(request), StringComparison.Ordinal);
        Assert.Contains("http://localhost:9090", File.ReadAllText(environmentFile), StringComparison.Ordinal);
        Assert.All((string[])[request, environmentFile], file => Assert.False(File.ReadAllText(file).EndsWith('\n'), file));
    }

    [Fact]
    public void A_file_without_a_final_line_break_gets_one_when_its_last_value_ends_with_one()
    {
        // Its last value is then written as a block (|), whose final line break is part of the value.
        var root = V3Fixture.Create();
        var request = Path.Combine(V3Fixture.CollectionPath(root), "Bulk.request.yaml");
        File.WriteAllText(request, "$kind: http-request\nurl: '{{baseUrl}}/_bulk'\nmethod: POST\norder: 3000\nbody:\n  type: text\n  content: |-\n    {\"index\":{}}");
        var environmentFile = Path.Combine(root, "postman", "environments", "Certs.environment.yaml");
        File.WriteAllText(environmentFile, "name: Certs\nvalues:\n  - key: pem\n    value: none");
        const string Bulk = "{\"index\":{}}\n{\"name\":\"Sprocket\"}\n"; // NDJSON, which must end with a line break
        const string Pem = "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n";

        var (workspace, collection) = Open(root);
        var bulk = collection.FindRequest("Bulk")!;
        var spec = bulk.ReadSpec();
        spec.Body!.Raw = Bulk;
        collection.UpdateRequest(bulk, spec);
        collection.Save();
        var certs = workspace.Environments.Single(e => e.Name == "Certs");
        certs.SetValue("pem", Pem);
        certs.Save();

        var (reopened, reloaded) = Open(root);
        Assert.Equal(Bulk, reloaded.FindRequest("Bulk")!.ReadSpec().Body!.Raw);
        Assert.Equal(Pem, reopened.Environments.Single(e => e.Name == "Certs").Values.Single(v => v.Key == "pem").Value);
        Assert.All((string[])[request, environmentFile], file => Assert.EndsWith("\n", File.ReadAllText(file), StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_without_a_final_line_break_that_ends_with_a_block_is_saved_as_it_is()
    {
        // As an editor that adds no final line break leaves it: the block's value has none either.
        var root = V3Fixture.Create();
        var request = Path.Combine(V3Fixture.CollectionPath(root), "Notes.request.yaml");
        File.WriteAllText(request, "$kind: http-request\nurl: '{{baseUrl}}/notes'\nmethod: GET\norder: 3000\ndescription: |\n  Notes.");
        var environmentFile = Path.Combine(root, "postman", "environments", "Certs.environment.yaml");
        File.WriteAllText(environmentFile, "name: Certs\nvalues:\n  - key: host\n    value: a\n  - key: pem\n    value: |\n      PEM");

        var (workspace, collection) = Open(root);
        var notes = collection.FindRequest("Notes")!;
        var spec = notes.ReadSpec();
        spec.Url = "{{baseUrl}}/notes?all=1";
        collection.UpdateRequest(notes, spec);
        collection.Save();
        var certs = workspace.Environments.Single(e => e.Name == "Certs");
        certs.SetValue("host", "b");
        certs.Save();

        var (reopened, reloaded) = Open(root);
        Assert.Equal(("{{baseUrl}}/notes?all=1", "Notes."), (reloaded.FindRequest("Notes")!.ReadSpec().Url, reloaded.FindRequest("Notes")!.ReadSpec().Description));
        Assert.Equal(["b", "PEM"], reopened.Environments.Single(e => e.Name == "Certs").Values.Select(v => v.Value));
        Assert.All((string[])[request, environmentFile], file => Assert.False(File.ReadAllText(file).EndsWith('\n'), file));
    }

    [Fact]
    public void A_block_that_ended_a_file_without_a_final_line_break_keeps_its_value_when_something_is_added_after_it()
    {
        // There its value has no final line break: copied as it is before new lines, it would get one.
        var root = V3Fixture.Create();
        var request = Path.Combine(V3Fixture.CollectionPath(root), "Notes.request.yaml");
        File.WriteAllText(request, "$kind: http-request\nurl: '{{baseUrl}}/notes'\nmethod: GET\ndescription: |\n  Notes."); // no order
        var environmentFile = Path.Combine(root, "postman", "environments", "Certs.environment.yaml");
        File.WriteAllText(environmentFile, "name: Certs\nvalues:\n  - key: pem\n    value: |\n      -----BEGIN-----\n      MIIB\n      -----END-----");

        var (workspace, collection) = Open(root);
        collection.AddRequest(collection.RootNode, "Zeta", new HttpRequestSpec { Url = "{{baseUrl}}/zeta" }); // Notes gets an order, last
        collection.Save();
        var certs = workspace.Environments.Single(e => e.Name == "Certs");
        certs.SetValue("token", "t-1"); // after the PEM
        certs.Save();

        var (reopened, reloaded) = Open(root);
        Assert.Equal("Notes.", reloaded.FindRequest("Notes")!.ReadSpec().Description);
        Assert.Contains("order:", File.ReadAllText(request), StringComparison.Ordinal);
        Assert.Equal(["-----BEGIN-----\nMIIB\n-----END-----", "t-1"], reopened.Environments.Single(e => e.Name == "Certs").Values.Select(v => v.Value));
        Assert.All((string[])[request, environmentFile], file => Assert.False(File.ReadAllText(file).EndsWith('\n'), file));
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
    public void Form_data_descriptions_are_kept_and_saved()
    {
        var root = V3Fixture.Create();
        var file = Path.Combine(V3Fixture.CollectionPath(root), "Widgets", "Upload image.request.yaml");
        File.WriteAllText(file, File.ReadAllText(file).Replace("      value: Foo Bar\n", "      value: Foo Bar\n      description: Shown under the image\n", StringComparison.Ordinal));
        var before = File.ReadAllText(file);
        var (_, collection) = Open(root);
        Assert.DoesNotContain(collection.Verify(), p => p.Contains("Upload image", StringComparison.Ordinal));

        var upload = collection.FindRequest("Upload image")!;
        var spec = upload.ReadSpec();
        spec.Body.FormData[1].Description = "A PNG or JPEG image";
        collection.UpdateRequest(upload, spec);
        collection.Save();

        var after = File.ReadAllText(file);
        Assert.Equal(["+ description: A PNG or JPEG image"], TestFiles.ChangedLines(before, after));
        Assert.Contains("      src: []\n      description: A PNG or JPEG image\n      disabled: false\n", after, StringComparison.Ordinal);
        var reloaded = PostmanCollection.Load(V3Fixture.CollectionPath(root)).FindRequest("Upload image")!.ReadSpec();
        Assert.Equal(["Shown under the image", "A PNG or JPEG image"], reloaded.Body.FormData.Select(f => f.Description));
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
    public void A_definition_that_could_not_be_read_is_never_overwritten_when_its_folder_moves()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        var definition = Path.Combine(collectionPath, "Widgets", ".resources", "definition.yaml");
        var conflicted = File.ReadAllText(definition).Replace(
            "description: Widget endpoints.\n",
            "<<<<<<< HEAD\ndescription: Widget endpoints.\n=======\ndescription: All widget endpoints.\n>>>>>>> feature\n",
            StringComparison.Ordinal);
        File.WriteAllText(definition, conflicted);
        var (workspace, collection) = Open(root);
        Assert.Contains(workspace.LoadErrors, e => e.Contains("definition.yaml", StringComparison.Ordinal));

        // Moved into another folder and renamed: the file moves along unchanged.
        collection.Move(collection.FindFolder("Widgets")!, collection.FindFolder("Admin")!, 0);
        collection.Rename(collection.FindFolder("Admin/Widgets")!, "Widgets v2");
        collection.Save();
        Assert.Equal(conflicted, File.ReadAllText(Path.Combine(collectionPath, "Admin", "Widgets v2", ".resources", "definition.yaml")));

        // Moved along with its parent.
        collection.Rename(collection.FindFolder("Admin")!, "Administration");
        collection.Save();
        Assert.Equal(conflicted, File.ReadAllText(Path.Combine(collectionPath, "Administration", "Widgets v2", ".resources", "definition.yaml")));
        Assert.NotNull(PostmanCollection.Load(collectionPath).FindRequest("Administration/Widgets v2/List widgets"));
    }

    [Fact]
    public void Edits_a_definition_that_could_not_be_read_would_have_to_hold_are_reported_not_dropped()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        var definition = Path.Combine(collectionPath, "Widgets", ".resources", "definition.yaml");
        var rootDefinition = Path.Combine(collectionPath, ".resources", "definition.yaml");
        var list = Path.Combine(collectionPath, "Widgets", "List widgets.request.yaml");
        const string conflicted = "<<<<<<< HEAD\n$kind: collection\n=======\n$kind: collection\ndescription: theirs\n>>>>>>> theirs\n";
        File.WriteAllText(definition, conflicted);
        File.WriteAllText(rootDefinition, conflicted);
        var (_, collection) = Open(root);

        collection.SetScripts(collection.FindFolder("Widgets")!, new ScriptSet("console.log('before');", "", ScriptLanguage.JavaScript));
        collection.Name = "Widget API v2"; // a name only the definition can hold
        SetMethod(collection, "Health", "HEAD");
        SetMethod(collection, "Widgets/List widgets", "PUT");
        File.AppendAllText(list, "# changed meanwhile\n");

        // Everything else is saved; both reasons are told, and overwriting cannot help the files that could not be read.
        var error = Assert.Throws<IOException>(() => collection.Save());
        Assert.All((string[])[definition, rootDefinition, list], file => Assert.Contains(file, error.Message, StringComparison.Ordinal));
        Assert.Equal(conflicted, File.ReadAllText(definition));
        Assert.Equal(conflicted, File.ReadAllText(rootDefinition));
        Assert.Contains("method: HEAD\n", File.ReadAllText(Path.Combine(collectionPath, "Health.request.yaml")), StringComparison.Ordinal);
        Assert.True(collection.IsDirty);
        Assert.Throws<IOException>(() => collection.Save(overwriteChanged: true));
        Assert.Contains("method: PUT\n", File.ReadAllText(list), StringComparison.Ordinal);
        Assert.Equal(conflicted, File.ReadAllText(definition));

        // Deleted to start over: saving writes what TermRest has.
        File.Delete(definition);
        File.Delete(rootDefinition);
        collection.Save();
        var reloaded = PostmanCollection.Load(collectionPath);
        Assert.Equal("Widget API v2", reloaded.Name);
        Assert.Equal("console.log('before');", reloaded.GetScripts(reloaded.FindFolder("Widgets")!).For(ScriptLanguage.JavaScript).PreRequest);
    }

    [Fact]
    public void Files_changed_on_disk_since_they_were_read_are_not_saved_over()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        string F(string relative) => Path.Combine(collectionPath, relative.Replace('/', Path.DirectorySeparatorChar));
        var (workspace, collection) = Open(root);
        var environment = workspace.Environments[0];

        // A git pull changes files TermRest edits (the first one keeps its length), and one it leaves alone; another is only touched.
        var list = F("Widgets/List widgets.request.yaml");
        var definition = F("Widgets/.resources/definition.yaml");
        var upload = F("Widgets/Upload image.request.yaml");
        var pulled = new Dictionary<string, string>
        {
            [list] = File.ReadAllText(list).Replace("/v1/widgets", "/v2/widgets", StringComparison.Ordinal),
            [definition] = File.ReadAllText(definition).Replace("Widget endpoints.", "All widget endpoints.", StringComparison.Ordinal),
            [upload] = File.ReadAllText(upload).Replace("Foo Bar", "Baz", StringComparison.Ordinal),
            [environment.FilePath!] = File.ReadAllText(environment.FilePath!).Replace("8080", "9090", StringComparison.Ordinal),
        };
        foreach (var (path, text) in pulled)
        {
            File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        }

        File.SetLastWriteTimeUtc(F("Health.request.yaml"), DateTime.UtcNow.AddMinutes(1));
        environment.MarkDirty();
        environment.Save(); // nothing to write: no conflict

        SetMethod(collection, "Widgets/List widgets", "PUT");
        SetMethod(collection, "Health", "HEAD");
        SetMethod(collection, "Widgets/Create widget", "PATCH");
        collection.SetScripts(collection.FindFolder("Widgets")!, new ScriptSet("console.log('before');", "", ScriptLanguage.JavaScript));
        environment.SetValue("token", "t-1");

        Assert.Equal([definition, list], Assert.Throws<FileChangedException>(() => collection.Save()).Paths.Order(StringComparer.Ordinal));
        Assert.Equal([environment.FilePath!], Assert.Throws<FileChangedException>(() => environment.Save()).Paths);
        Assert.All(pulled, p => Assert.Equal(p.Value, File.ReadAllText(p.Key)));
        Assert.Contains("method: HEAD\n", File.ReadAllText(F("Health.request.yaml")), StringComparison.Ordinal);
        Assert.Contains("method: PATCH\n", File.ReadAllText(F("Widgets/Create widget.request.yaml")), StringComparison.Ordinal);
        Assert.True(collection.IsDirty);
        Assert.Throws<FileChangedException>(() => collection.Save());

        collection.Save(overwriteChanged: true);
        environment.Save(overwriteChanged: true);

        Assert.False(collection.IsDirty);
        var reloaded = PostmanCollection.Load(collectionPath);
        var spec = reloaded.FindRequest("List widgets")!.ReadSpec();
        Assert.Equal(("PUT", "{{baseUrl}}/v1/widgets?limit=50"), (spec.Method, spec.Url));
        Assert.Equal("console.log('before');", reloaded.GetScripts(reloaded.FindFolder("Widgets")!).For(ScriptLanguage.JavaScript).PreRequest);
        Assert.Equal(pulled[upload], File.ReadAllText(upload));
        Assert.Equal(["http://localhost:8080", "t-1"], PostmanEnvironment.Load(environment.FilePath!).Values.Take(2).Select(v => v.Value));
        collection.MarkDirty();
        collection.Save();
    }

    [Fact]
    public void A_renamed_item_whose_file_disappeared_is_written_anew()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        var (_, collection) = Open(root);
        SetMethod(collection, "Admin/Reset", "POST");
        collection.Rename(collection.FindRequest("List widgets")!, "List all widgets");
        collection.Rename(collection.FindFolder("Admin")!, "Administration");

        // A git checkout removes them meanwhile.
        File.Delete(Path.Combine(collectionPath, "Widgets", "List widgets.request.yaml"));
        Directory.Delete(Path.Combine(collectionPath, "Admin"), recursive: true);
        collection.Save();

        var reloaded = PostmanCollection.Load(collectionPath);
        Assert.Equal("{{baseUrl}}/v1/widgets?limit=50", reloaded.FindRequest("Widgets/List all widgets")!.ReadSpec().Url);
        Assert.Equal("POST", reloaded.FindRequest("Administration/Reset")!.ReadSpec().Method);
        Assert.Empty(Directory.EnumerateDirectories(collectionPath, ".termrest-saving-*"));
    }

    [Fact]
    public void A_renamed_folder_whose_files_disappeared_is_written_anew_with_everything_it_held()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        var archive = Path.Combine(collectionPath, "Widgets", "Archive");
        Directory.CreateDirectory(Path.Combine(archive, ".resources"));
        File.WriteAllText(Path.Combine(archive, ".resources", "definition.yaml"), "$kind: collection\ndescription: Old widgets.\n");
        File.WriteAllText(Path.Combine(archive, "Old widget.request.yaml"), "$kind: http-request\nurl: https://h/old\nmethod: GET\n");
        var (_, collection) = Open(root);
        var requests = collection.AllRequests().ToDictionary(r => r.PathInCollection.Replace("Widgets/", "Widget endpoints/", StringComparison.Ordinal), r => r.ReadSpec());
        collection.Rename(collection.FindFolder("Widgets")!, "Widget endpoints");

        // A git checkout removes the folder meanwhile; nothing in it was edited.
        Directory.Delete(Path.Combine(collectionPath, "Widgets"), recursive: true);
        collection.Save();

        var reloaded = PostmanCollection.Load(collectionPath);
        Assert.Equal(requests.Keys.Order(StringComparer.Ordinal), reloaded.AllRequests().Select(r => r.PathInCollection).Order(StringComparer.Ordinal));
        Assert.All(requests, r => Assert.True(r.Value.ContentEquals(reloaded.FindRequest(r.Key)!.ReadSpec()), r.Key));
        Assert.Equal("bearer", reloaded.FindFolder("Widget endpoints")!.Auth?.Type);
        Assert.Equal("Old widgets.", reloaded.FindFolder("Widget endpoints/Archive")!.Json["description"]?.GetValue<string>());
    }

    [Fact]
    public void A_folder_deleted_on_disk_stays_deleted_until_something_is_written_into_it_then_comes_back_whole()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        var widgets = Path.Combine(collectionPath, "Widgets");
        Directory.CreateDirectory(Path.Combine(widgets, "Drafts"));
        var (_, collection) = Open(root);
        collection.SetScripts(collection.FindFolder("Widgets")!, new ScriptSet("console.log('widgets');", "", ScriptLanguage.JavaScript));
        collection.Save();

        // A git checkout removes the folder meanwhile; TermRest still shows it.
        Directory.Delete(widgets, recursive: true);
        SetMethod(collection, "Health", "HEAD");
        collection.Save();
        Assert.False(Directory.Exists(widgets));

        // Writing into it brings back its definition, so its auth and scripts still apply, and everything else it held.
        SetMethod(collection, "Widgets/Create widget", "PUT");
        collection.Save();
        var reloaded = PostmanCollection.Load(collectionPath);
        Assert.Equal("bearer", reloaded.GetEffectiveAuth(reloaded.FindRequest("Widgets/Create widget")!)?.Type);
        Assert.Equal("console.log('widgets');", reloaded.GetScripts(reloaded.FindFolder("Widgets")!).For(ScriptLanguage.JavaScript).PreRequest);
        Assert.Equal(Tree(collection), Tree(reloaded));
        Assert.All(collection.AllRequests(), r => Assert.True(r.ReadSpec().ContentEquals(reloaded.FindRequest(r.PathInCollection)!.ReadSpec()), r.PathInCollection));

        var snapshot = V3Fixture.Snapshot(root);
        collection.MarkDirty();
        collection.Save();
        Assert.Equal(snapshot, V3Fixture.Snapshot(root));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("move away and back")]
    public void A_folder_without_an_order_deleted_on_disk_stays_deleted_when_its_siblings_change(string change)
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        var admin = Path.Combine(collectionPath, "Admin");
        var (_, collection) = Open(root);

        // A pull of a teammate's deletion removes Admin, which has no definition, so no order: it gets one now.
        Directory.Delete(admin, recursive: true);
        switch (change)
        {
            case "add":
                collection.AddRequest(collection.RootNode, "Status", new HttpRequestSpec { Url = "{{baseUrl}}/status" });
                break;
            case "remove":
                collection.Remove(collection.FindRequest("Health")!);
                break;
            default:
                // Health gets a new order after Widgets, then one before it again.
                collection.Move(collection.FindRequest("Health")!, collection.RootNode, 1);
                collection.Save();
                collection.Move(collection.FindRequest("Health")!, collection.RootNode, 0);
                break;
        }

        collection.Save();
        Assert.False(Directory.Exists(admin));
        Assert.Equal(Tree(collection).Where(p => !p.Contains("Admin", StringComparison.Ordinal)), Tree(PostmanCollection.Load(collectionPath)));

        var snapshot = V3Fixture.Snapshot(root);
        collection.MarkDirty();
        collection.Save();
        Assert.Equal(snapshot, V3Fixture.Snapshot(root));

        // Once something is written into it, it comes back where TermRest shows it.
        SetMethod(collection, "Admin/Reset", "POST");
        collection.Save();
        Assert.Equal(Tree(collection), Tree(PostmanCollection.Load(collectionPath)));
    }

    [Fact]
    public void A_request_moved_into_a_folder_deleted_on_disk_brings_the_folder_back_whole()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        var (_, collection) = Open(root);

        Directory.Delete(Path.Combine(collectionPath, "Widgets"), recursive: true); // a git checkout, say
        collection.Move(collection.FindRequest("Admin/Reset")!, collection.FindFolder("Widgets")!, 0);
        collection.Save();

        var reloaded = PostmanCollection.Load(collectionPath);
        Assert.Equal("bearer", reloaded.GetEffectiveAuth(reloaded.FindRequest("Widgets/Reset")!)?.Type);
        Assert.Equal(Tree(collection), Tree(reloaded));
    }

    [Fact]
    public void A_collection_folder_deleted_on_disk_stays_deleted_until_something_is_written_into_it_then_comes_back_whole()
    {
        var root = V3Fixture.Create();
        var (workspace, _) = Open(root);
        var created = workspace.CreateCollection("Orders API");
        var orders = created.AddFolder(created.RootNode, "Orders");
        created.AddRequest(orders, "List orders", new HttpRequestSpec { Url = "{{baseUrl}}/orders" });
        created.Save();

        Directory.Delete(created.FilePath!, recursive: true);
        created.MarkDirty();
        created.Save();
        Assert.False(Directory.Exists(created.FilePath!));

        // Its definition comes back too, even with nothing but its kind: it is what makes the folder a collection.
        created.AddRequest(orders, "Create order", new HttpRequestSpec { Method = "POST", Url = "{{baseUrl}}/orders" });
        created.Save();
        Assert.Equal(["[Orders]", "Orders/List orders", "Orders/Create order"], Tree(ApiWorkspace.Open(root).FindCollection(created.FilePath!)!));
    }

    [Fact]
    public void A_request_whose_file_changed_on_disk_is_not_moved_until_it_may_be_saved_over()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        string F(string relative) => Path.Combine(collectionPath, relative.Replace('/', Path.DirectorySeparatorChar));
        var (_, collection) = Open(root);
        var list = F("Widgets/List widgets.request.yaml");
        var pulled = File.ReadAllText(list).Replace("/v1/widgets", "/v2/widgets", StringComparison.Ordinal);
        File.WriteAllText(list, pulled);
        File.SetLastWriteTimeUtc(list, DateTime.UtcNow.AddMinutes(1));

        collection.Rename(collection.FindRequest("List widgets")!, "List all widgets");
        collection.AddRequest(collection.FindFolder("Widgets")!, "List widgets", new HttpRequestSpec { Url = "{{baseUrl}}/v1/widgets/new" });
        SetMethod(collection, "Health", "HEAD");

        // The file stays where it is, with its examples, and keeps its name; the rest is saved.
        Assert.Equal([list], Assert.Throws<FileChangedException>(() => collection.Save()).Paths);
        Assert.Equal(pulled, File.ReadAllText(list));
        Assert.True(File.Exists(F("Widgets/.resources/List widgets.resources/examples/200 OK.example.yaml")));
        Assert.False(File.Exists(F("Widgets/List all widgets.request.yaml")));
        Assert.Contains("url: \"{{baseUrl}}/v1/widgets/new\"", File.ReadAllText(F("Widgets/List widgets-1.request.yaml")), StringComparison.Ordinal);
        Assert.Contains("method: HEAD\n", File.ReadAllText(F("Health.request.yaml")), StringComparison.Ordinal);
        Assert.True(collection.IsDirty);
        Assert.Throws<FileChangedException>(() => collection.Save());

        // Overwriting moves it and writes it at once: its examples path follows.
        collection.Save(overwriteChanged: true);
        Assert.False(File.Exists(list));
        Assert.Contains("examples: ./.resources/List all widgets.resources/examples", File.ReadAllText(F("Widgets/List all widgets.request.yaml")), StringComparison.Ordinal);
        Assert.True(File.Exists(F("Widgets/.resources/List all widgets.resources/examples/200 OK.example.yaml")));
        var reloaded = PostmanCollection.Load(collectionPath);
        Assert.Equal("{{baseUrl}}/v1/widgets?limit=50", reloaded.FindRequest("Widgets/List all widgets")!.ReadSpec().Url);
        Assert.Equal("{{baseUrl}}/v1/widgets/new", reloaded.FindRequest("Widgets/List widgets")!.ReadSpec().Url);
        collection.MarkDirty();
        collection.Save();
    }

    [Fact]
    public void A_deleted_folder_that_holds_a_request_held_back_goes_once_the_request_moves_out()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        string F(string relative) => Path.Combine(collectionPath, relative.Replace('/', Path.DirectorySeparatorChar));
        var reset = F("Admin/Reset.request.yaml");
        File.AppendAllText(reset, "examples: ./.resources/Reset.resources/examples\n");
        Directory.CreateDirectory(F("Admin/.resources/Reset.resources/examples"));
        File.WriteAllText(F("Admin/.resources/Reset.resources/examples/Done.example.yaml"), "$kind: http-example\nresponse:\n  statusCode: 204\n");
        var (_, collection) = Open(root);
        File.WriteAllText(reset, File.ReadAllText(reset).Replace("\"42\"", "\"43\"", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(reset, DateTime.UtcNow.AddMinutes(1));

        collection.Move(collection.FindRequest("Admin/Reset")!, collection.RootNode, 0);
        collection.Remove(collection.FindFolder("Admin")!);
        Assert.Equal([reset], Assert.Throws<FileChangedException>(() => collection.Save()).Paths);
        Assert.True(File.Exists(reset));

        collection.Save(overwriteChanged: true);

        Assert.False(Directory.Exists(F("Admin")));
        Assert.True(File.Exists(F("Reset.request.yaml")));
        Assert.True(File.Exists(F(".resources/Reset.resources/examples/Done.example.yaml")));
        Assert.Equal(["Reset", "Health", "[Widgets]"], Tree(PostmanCollection.Load(collectionPath)).Where(p => !p.StartsWith("Widgets/", StringComparison.Ordinal)));
    }

    [Fact]
    public void Values_whose_first_line_holds_only_spaces_are_saved_so_that_they_read_back()
    {
        var root = V3Fixture.Create();
        var (workspace, collection) = Open(root);
        const string body = "  \n{\n  \"name\": \"x\"\n}";
        var create = collection.FindRequest("Create widget")!;
        var spec = create.ReadSpec();
        spec.Body.Raw = body;
        collection.UpdateRequest(create, spec);
        var environment = workspace.Environments[0];
        environment.SetValue("note", "    \nsecond line");

        collection.Save();
        environment.Save();

        Assert.False(collection.IsDirty);
        Assert.Equal(body, PostmanCollection.Load(V3Fixture.CollectionPath(root)).FindRequest("Create widget")!.ReadSpec().Body.Raw);
        Assert.Equal("    \nsecond line", PostmanEnvironment.Load(environment.FilePath!).Values.Single(v => v.Key == "note").Value);
    }

    [Fact]
    public void A_file_that_would_not_read_back_is_not_written()
    {
        var root = V3Fixture.Create();
        var (_, collection) = Open(root);
        var file = Path.Combine(V3Fixture.CollectionPath(root), "Health.request.yaml");
        var before = File.ReadAllText(file);

        // A YAML key holds at most 1024 characters: such a header name cannot be written in a header map.
        var health = collection.FindRequest("Health")!;
        var spec = health.ReadSpec();
        spec.Headers.Add(new KeyValueEntry { Key = new string('X', 1100), Value = "1" });
        collection.UpdateRequest(health, spec);

        Assert.Contains(file, Assert.Throws<IOException>(() => collection.Save()).Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(file));
        Assert.True(collection.IsDirty);
    }

    [Fact]
    public void Converting_to_v3_writes_everything_or_nothing()
    {
        var source = PostmanCollection.Parse("""
            { "info": { "name": "Scripts", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [
                { "name": "Indented", "event": [ { "listen": "test", "script": { "type": "text/javascript", "exec": [ "    ", "pm.test('ok', () => {});" ] } } ],
                  "request": { "method": "GET", "url": "https://h/a" } },
                { "name": "Long header", "request": { "method": "GET", "header": [ { "key": "LONG", "value": "1" } ], "url": "https://h/b" } } ] }
            """.Replace("LONG", new string('X', 1100), StringComparison.Ordinal));
        var parent = TestFiles.NewTempDirectory();
        var target = Path.Combine(parent, "postman", "collections", "Scripts");

        Assert.Throws<IOException>(() => source.ConvertToV3(target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent));

        source.Remove(source.FindRequest("Long header")!);
        var converted = source.ConvertToV3(target);
        Assert.Equal("    \npm.test('ok', () => {});", converted.FindRequest("Indented")!.ReadSpec().Scripts.For(ScriptLanguage.JavaScript).Test);
    }

    private static void SetMethod(PostmanCollection collection, string request, string method)
    {
        var node = collection.FindRequest(request)!;
        var spec = node.ReadSpec();
        spec.Method = method;
        collection.UpdateRequest(node, spec);
    }

    [Fact]
    public void New_requests_never_take_the_file_of_a_request_termrest_does_not_show()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        var subscribe = File.ReadAllText(Path.Combine(collectionPath, "Widgets", "Subscribe.request.yaml"));
        var (_, collection) = Open(root);
        var widgets = collection.FindFolder("Widgets")!;

        collection.Rename(widgets, "Widget endpoints");
        collection.AddRequest(widgets, "Subscribe", new HttpRequestSpec { Url = "{{baseUrl}}/v1/subscribe" });
        collection.Save();

        var folder = Path.Combine(collectionPath, "Widget endpoints");
        Assert.Equal(subscribe, File.ReadAllText(Path.Combine(folder, "Subscribe.request.yaml")));
        Assert.Contains("name: Subscribe", File.ReadAllText(Path.Combine(folder, "Subscribe-1.request.yaml")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_can_take_the_name_of_a_deleted_folder_that_keeps_hidden_requests()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        var (_, collection) = Open(root);

        collection.Remove(collection.FindFolder("Widgets")!);
        collection.Rename(collection.FindFolder("Admin")!, "Widgets");
        collection.Save();

        Assert.Equal(["Subscribe.request.yaml"], Directory.EnumerateFiles(Path.Combine(collectionPath, "Widgets")).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(collectionPath, "Widgets-1", "Reset.request.yaml")));
        Assert.Contains("name: Widgets\n", File.ReadAllText(Path.Combine(collectionPath, "Widgets-1", ".resources", "definition.yaml")), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(collectionPath, ".termrest-saving-*"));
        Assert.NotNull(PostmanCollection.Load(collectionPath).FindRequest("Widgets/Reset"));

        var snapshot = V3Fixture.Snapshot(root);
        collection.MarkDirty();
        collection.Save();
        Assert.Equal(snapshot, V3Fixture.Snapshot(root));
    }

    [Fact]
    public void A_new_folder_never_takes_over_a_deleted_folder_that_keeps_hidden_requests()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        var (_, collection) = Open(root);

        collection.Remove(collection.FindFolder("Widgets")!);
        var widgets = collection.AddFolder(collection.RootNode, "Widgets");
        collection.AddRequest(widgets, "Ping", new HttpRequestSpec { Url = "{{baseUrl}}/ping" });
        collection.Move(collection.FindRequest("Health")!, widgets, 0);
        collection.Save();

        Assert.Equal(["Subscribe.request.yaml"], Directory.EnumerateFiles(Path.Combine(collectionPath, "Widgets")).Select(Path.GetFileName));
        Assert.Equal(["Health.request.yaml", "Ping.request.yaml"], Directory.EnumerateFiles(Path.Combine(collectionPath, "Widgets-1")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains("name: Widgets\n", File.ReadAllText(Path.Combine(collectionPath, "Widgets-1", ".resources", "definition.yaml")), StringComparison.Ordinal);
        var reloaded = PostmanCollection.Load(collectionPath);
        Assert.NotNull(reloaded.FindRequest("Widgets/Health"));
        Assert.NotNull(reloaded.FindRequest("Widgets/Ping"));

        var snapshot = V3Fixture.Snapshot(root);
        collection.MarkDirty();
        collection.Save();
        Assert.Equal(snapshot, V3Fixture.Snapshot(root));
    }

    [Fact]
    public void A_new_folder_never_takes_over_a_deleted_folder_that_arrives_with_its_renamed_parent()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        string F(string relative) => Path.Combine(collectionPath, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(F("Admin/Widgets/.resources"));
        File.WriteAllText(F("Admin/Widgets/.resources/definition.yaml"), "$kind: collection\ndescription: Old widgets.\n");
        File.WriteAllText(F("Admin/Widgets/Subscribe.request.yaml"), "$kind: websocket-request\nurl: wss://api.example.com/stream\n");
        var (_, collection) = Open(root);

        collection.Remove(collection.FindFolder("Admin/Widgets")!);
        var widgets = collection.AddFolder(collection.FindFolder("Admin")!, "Widgets");
        collection.AddRequest(widgets, "Ping", new HttpRequestSpec { Url = "{{baseUrl}}/ping" });
        collection.Rename(collection.FindFolder("Admin")!, "Administration");
        collection.Save();

        Assert.Equal(["Subscribe.request.yaml"], Directory.EnumerateFileSystemEntries(F("Administration/Widgets")).Select(Path.GetFileName));
        Assert.True(File.Exists(F("Administration/Widgets-1/Ping.request.yaml")));
        Assert.Contains("name: Widgets\n", File.ReadAllText(F("Administration/Widgets-1/.resources/definition.yaml")), StringComparison.Ordinal);
        Assert.NotNull(PostmanCollection.Load(collectionPath).FindRequest("Administration/Widgets/Ping"));

        var snapshot = V3Fixture.Snapshot(root);
        collection.MarkDirty();
        collection.Save();
        Assert.Equal(snapshot, V3Fixture.Snapshot(root));
    }

    [Fact]
    public void A_new_folder_keeps_its_definition_once_a_deleted_folder_of_the_same_name_goes()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        string F(string relative) => Path.Combine(collectionPath, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(F("Admin/Widgets"));
        File.Move(F("Admin/Reset.request.yaml"), F("Admin/Widgets/Reset.request.yaml"));
        var (_, collection) = Open(root);
        var reset = F("Admin/Widgets/Reset.request.yaml");
        File.WriteAllText(reset, File.ReadAllText(reset).Replace("\"42\"", "\"43\"", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(reset, DateTime.UtcNow.AddMinutes(1));

        // The changed request is held back in the deleted folder, which its renamed parent takes along.
        collection.Move(collection.FindRequest("Admin/Widgets/Reset")!, collection.RootNode, 0);
        collection.Remove(collection.FindFolder("Admin/Widgets")!);
        var widgets = collection.AddFolder(collection.FindFolder("Admin")!, "Widgets");
        collection.SetScripts(widgets, new ScriptSet("console.log('before');", "", ScriptLanguage.JavaScript));
        collection.Rename(collection.FindFolder("Admin")!, "Administration");
        Assert.Equal([F("Administration/Widgets/Reset.request.yaml")], Assert.Throws<FileChangedException>(() => collection.Save()).Paths);

        collection.Save(overwriteChanged: true);

        var reloaded = PostmanCollection.Load(collectionPath);
        Assert.Equal("console.log('before');", reloaded.GetScripts(reloaded.FindFolder("Administration/Widgets")!).For(ScriptLanguage.JavaScript).PreRequest);
        Assert.False(Directory.Exists(F("Administration/Widgets")));
        Assert.True(File.Exists(F("Reset.request.yaml")));
    }

    [Fact]
    public void A_folder_written_anew_never_takes_over_a_deleted_folder_that_keeps_hidden_requests()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        string F(string relative) => Path.Combine(collectionPath, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(F("Admin/Widgets"));
        File.WriteAllText(F("Admin/Widgets/Subscribe.request.yaml"), "$kind: websocket-request\nurl: wss://api.example.com/stream\n");
        var (_, collection) = Open(root);

        collection.Remove(collection.FindFolder("Admin/Widgets")!);
        collection.Move(collection.FindFolder("Widgets")!, collection.FindFolder("Admin")!, 0);
        Directory.Delete(F("Widgets"), recursive: true); // a git checkout, say
        collection.Save();

        Assert.Equal(["Subscribe.request.yaml"], Directory.EnumerateFileSystemEntries(F("Admin/Widgets")).Select(Path.GetFileName));
        Assert.Contains("name: Widgets\n", File.ReadAllText(F("Admin/Widgets-1/.resources/definition.yaml")), StringComparison.Ordinal);
        Assert.Equal("{{baseUrl}}/v1/widgets?limit=50", PostmanCollection.Load(collectionPath).FindRequest("Admin/Widgets/List widgets")!.ReadSpec().Url);
    }

    [Fact]
    public void A_failed_save_leaves_files_where_the_next_save_finds_them()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);

        // A file where Admin's .resources folder would go makes moving a request with examples into Admin fail.
        var blocker = Path.Combine(collectionPath, "Admin", ".resources");
        File.WriteAllText(blocker, "");
        var before = V3Fixture.Snapshot(root);
        var (_, collection) = Open(root);
        collection.Rename(collection.FindFolder("Widgets")!, "Widget endpoints");
        collection.Move(collection.FindRequest("List widgets")!, collection.FindFolder("Admin")!, 0);

        // Failing while files are moved: every move is undone.
        Assert.ThrowsAny<IOException>(() => collection.Save());
        Assert.Equal(before, V3Fixture.Snapshot(root));
        Assert.False(Directory.Exists(Path.Combine(collectionPath, "Widget endpoints")));
        Assert.Empty(Directory.EnumerateDirectories(collectionPath, ".termrest-saving-*"));

        File.Delete(blocker);
        collection.Save();
        Assert.True(File.Exists(Path.Combine(collectionPath, "Admin", ".resources", "List widgets.resources", "examples", "200 OK.example.yaml")));
        Assert.True(File.Exists(Path.Combine(collectionPath, "Widget endpoints", "Create widget.request.yaml")));

        // Failing after the files moved (here: writing Admin's new definition): the next save starts from where they are now.
        Directory.CreateDirectory(Path.Combine(collectionPath, "Admin", ".resources", "definition.yaml"));
        collection.Rename(collection.FindFolder("Admin")!, "Admin: tools");
        Assert.ThrowsAny<IOException>(() => collection.Save());
        var tools = Path.Combine(collectionPath, "Admin- tools");
        Assert.True(File.Exists(Path.Combine(tools, "Reset.request.yaml")));

        Directory.Delete(Path.Combine(tools, ".resources", "definition.yaml"));
        collection.Save();
        Assert.NotNull(PostmanCollection.Load(collectionPath).FindRequest("Admin: tools/List widgets"));
    }

    [Fact]
    public void Files_left_by_an_interrupted_save_are_reported()
    {
        var root = V3Fixture.Create();
        var leftover = Path.Combine(V3Fixture.CollectionPath(root), ".termrest-saving-0123", "0");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "x.request.yaml"), "$kind: http-request\n");

        var (workspace, _) = Open(root);

        Assert.Contains(workspace.LoadErrors, e => e.Contains(".termrest-saving-0123", StringComparison.Ordinal));
    }

    [Fact]
    public void Huge_numbers_and_orders_that_are_not_finite_neither_break_loading_nor_saving()
    {
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        var huge = "1" + new string('0', 309); // beyond even a double
        Replace(Path.Combine(collectionPath, "Health.request.yaml"), "order: 1000", $"order: {huge}");
        Replace(Path.Combine(collectionPath, "Widgets", "List widgets.request.yaml"), "  Accept: application/json\n", $"  Accept: application/json\n  X-Big: {huge}\n");
        File.AppendAllText(Path.Combine(collectionPath, "Admin", "Reset.request.yaml"), "order: '1e999'\n");
        File.AppendAllText(Path.Combine(root, "postman", "environments", "Local.environment.yaml"), $"  - key: big\n    value: {huge}\n");

        var (workspace, collection) = Open(root);
        Assert.Equal(huge, collection.FindRequest("List widgets")!.ReadSpec().Headers.Single(h => h.Key == "X-Big").Value);
        Assert.Equal(huge, workspace.Environments[0].Values.Single(v => v.Key == "big").Value);

        collection.AddRequest(collection.FindFolder("Admin")!, "Audit", new HttpRequestSpec { Url = "{{baseUrl}}/audit" });
        collection.AddRequest(collection.RootNode, "Status", new HttpRequestSpec { Url = "{{baseUrl}}/status" });
        collection.Save();

        var reloaded = PostmanCollection.Load(collectionPath);
        Assert.Equal(["Reset", "Audit"], reloaded.FindFolder("Admin")!.Children.Select(c => c.Name));
        Assert.Equal("Status", reloaded.RootNode.Children[^1].Name);

        static void Replace(string path, string text, string replacement) =>
            File.WriteAllText(path, File.ReadAllText(path).Replace(text, replacement, StringComparison.Ordinal));
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
    public void Variables_written_as_a_map_are_read_and_keep_their_form()
    {
        // Postman writes plain collection variables as a {name: value} map.
        var root = V3Fixture.Create();
        var definition = Path.Combine(V3Fixture.CollectionPath(root), ".resources", "definition.yaml");
        const string Variables = "variables:\n  baseUrl: http://localhost:5000\n  port: 5000\n  token: \"\"\n  unset:\n";
        File.WriteAllText(definition, "$kind: collection\n" + Variables);
        var (_, collection) = Open(root);

        Assert.Equal(["baseUrl=http://localhost:5000", "port=5000", "token=", "unset="], collection.GetVariables().Select(v => $"{v.Key}={v.Value}"));

        collection.Name = "Widget API v2"; // rewrites the definition
        collection.Save();
        Assert.Equal("$kind: collection\nname: Widget API v2\n" + Variables, File.ReadAllText(definition));

        var variables = collection.GetVariables();
        variables[0].Value = "https://staging.example.com";
        variables.Add(new KeyValueEntry { Key = "added", Value = "1" });
        collection.SetVariables(variables);
        collection.Save();
        Assert.Equal(["- baseUrl: http://localhost:5000", "+ baseUrl: https://staging.example.com", "+ added: \"1\""],
            TestFiles.ChangedLines("$kind: collection\nname: Widget API v2\n" + Variables, File.ReadAllText(definition)));

        // One a map can't hold makes it a list of entries.
        variables = collection.GetVariables();
        variables[2].Type = "secret";
        collection.SetVariables(variables);
        collection.Save();
        var reloaded = PostmanCollection.Load(V3Fixture.CollectionPath(root));
        Assert.Equal(["baseUrl=https://staging.example.com", "port=5000", "token= secret", "unset=", "added=1"],
            reloaded.GetVariables().Select(v => $"{v.Key}={v.Value}{(v.IsSecret ? " secret" : "")}"));
        Assert.Contains("variables:\n  - key: baseUrl\n", File.ReadAllText(definition), StringComparison.Ordinal);
    }

    [Fact]
    public void Definitions_keep_their_kind_and_variables_their_disabled_false()
    {
        // Some Postman versions write "$kind: folder" for folders and "disabled: false" for every variable.
        var root = V3Fixture.Create();
        var definition = Path.Combine(V3Fixture.CollectionPath(root), ".resources", "definition.yaml");
        var folderDefinition = Path.Combine(V3Fixture.CollectionPath(root), "Widgets", ".resources", "definition.yaml");
        File.WriteAllText(definition, "$kind: collection\nvariables:\n  - key: baseUrl\n    value: https://api.example.com\n    disabled: false\n  - key: old\n    value: x\n    disabled: true\n");
        File.WriteAllText(folderDefinition, File.ReadAllText(folderDefinition).Replace("$kind: collection", "$kind: folder", StringComparison.Ordinal));
        var folderBefore = File.ReadAllText(folderDefinition);
        var (_, collection) = Open(root);

        collection.Name = "Widget API v2";
        var variables = collection.GetVariables();
        variables[0].Value = "https://staging.example.com";
        collection.SetVariables(variables);
        collection.SetScripts(collection.FindFolder("Widgets")!, new ScriptSet([new LanguageScripts(ScriptLanguage.JavaScript, "", "pm.test('ok', () => {});")]));
        collection.Save();

        Assert.Equal(
            "$kind: collection\nname: Widget API v2\nvariables:\n  - key: baseUrl\n    value: https://staging.example.com\n    disabled: false\n  - key: old\n    value: x\n    disabled: true\n",
            File.ReadAllText(definition));
        Assert.Equal(["+ scripts:", "+ - type: http:afterResponse", "+ code: pm.test('ok', () => {});"],
            TestFiles.ChangedLines(folderBefore, File.ReadAllText(folderDefinition)).Where(l => !l.StartsWith("+ language", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("url: https://localhost:7281/api/products?wireless&categoryId=1\nmethod: GET\nqueryParams:\n  wireless: null\n  categoryId: \"1\"\n")]
    [InlineData("url: ?=\nmethod: GET\nqueryParams:\n  \"\": \"\"\n")]
    [InlineData("url: '{{baseUrl}}/push'\nmethod: POST\nbody:\n  mode: raw\n  raw: |\n    {\"token\": \"x\"}\nauth:\n  type: bearer\nscripts:\n  - type: afterResponse\n    code: pm.test('ok', () => {});\n")]
    [InlineData("url: '{{baseUrl}}/login'\nmethod: POST\nbody:\n  type: raw\n  raw: '{\"user\": \"a\"}'\n")]
    [InlineData("url: '{{baseUrl}}/upload'\nmethod: POST\nbody:\n  type: formdata\n  formdata:\n    - key: a\n      value: b\n")]
    public void An_edit_keeps_the_parts_of_a_request_file_it_does_not_change(string content)
    {
        // As other tools and Postman versions write them: a query map with a value left out or an empty name, a body in
        // v2.1's form or of a type TermRest doesn't know (which it can't read), auth without credentials, a script without
        // its language.
        var root = V3Fixture.Create();
        var path = Path.Combine(V3Fixture.CollectionPath(root), "Odd.request.yaml");
        var text = "$kind: http-request\n" + content + "order: 3000\n";
        File.WriteAllText(path, text);
        var (_, collection) = Open(root);
        var node = collection.FindRequest("Odd")!;

        var spec = node.ReadSpec();
        spec.Headers.Add(new KeyValueEntry { Key = "X-Edit", Value = "edited" });
        collection.UpdateRequest(node, spec);
        collection.Save();

        Assert.Equal(["+ headers:", "+ X-Edit: edited"], TestFiles.ChangedLines(text, File.ReadAllText(path)));
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
    public void Importing_into_a_v3_workspace_keeps_private_files_private()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes only.");
            return;
        }

        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var (workspace, _) = Open(V3Fixture.Create());
        var outside = TestFiles.NewTempDirectory();
        var environment = TestFiles.CopyToTemp(TestFiles.DemoEnvironment, outside);
        var collection = TestFiles.CopyToTemp(TestFiles.Data("complex.postman_collection.json"), outside); // folders and an example
        File.SetUnixFileMode(environment, Private);
        File.SetUnixFileMode(collection, Private);

        var importedEnvironment = workspace.ImportEnvironment(environment);
        var importedCollection = workspace.ImportCollection(collection);

        Assert.Equal(Private, File.GetUnixFileMode(importedEnvironment.FilePath!));
        Assert.Equal(Private | UnixFileMode.UserExecute, File.GetUnixFileMode(importedCollection.FilePath!));
        foreach (var folder in Directory.EnumerateDirectories(importedCollection.FilePath!, "*", SearchOption.AllDirectories))
        {
            Assert.Equal(Private | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
        }

        foreach (var file in Directory.EnumerateFiles(importedCollection.FilePath!, "*", SearchOption.AllDirectories))
        {
            Assert.Equal(Private, File.GetUnixFileMode(file));
        }
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
    public void A_line_width_whose_settings_could_not_be_saved_applies_to_what_is_open_all_the_same()
    {
        var root = V3Fixture.Create();
        Directory.CreateDirectory(Path.Combine(root, ".termrest"));
        File.WriteAllText(Path.Combine(root, ".termrest", "settings.json"), "{ \"yamlLineWidth\": 80 }");
        var (workspace, collection) = Open(root);

        // A merge conflict in the file since it was read: the settings are not saved, but apply for this session.
        File.WriteAllText(workspace.SettingsPath, "{ <<<<<<< HEAD");
        workspace.Settings.YamlLineWidth = 0;
        Assert.Throws<IOException>(workspace.SaveSettings);

        var health = collection.FindRequest("Health")!;
        var spec = health.ReadSpec();
        spec.Description = "The health endpoint answers without authentication, so monitors can call it every few seconds to check the service.";
        collection.UpdateRequest(health, spec);
        collection.Save();
        var environment = workspace.Environments[0];
        environment.SetValue("baseUrl", "http://localhost:8080 is where the service listens when it runs on a developer machine with default settings");
        environment.Save();

        Assert.Contains($"\ndescription: {spec.Description}\n", File.ReadAllText(Path.Combine(V3Fixture.CollectionPath(root), "Health.request.yaml")), StringComparison.Ordinal);
        Assert.Contains("    value: 'http://localhost:8080 is where the service listens when it runs on a developer machine with default settings'\n",
            File.ReadAllText(environment.FilePath!), StringComparison.Ordinal);
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
    public void Environment_values_postman_marks_secret_are_secret_and_keep_their_form()
    {
        // Postman marks them with "secret: true", and leaves the value out of the file.
        var root = V3Fixture.Create();
        var path = Path.Combine(root, "postman", "environments", "Dev.environment.yaml");
        const string Text = "name: Dev\nvalues:\n  - key: baseUrl\n    value: 'http://localhost:8080'\n  - key: jwt\n    secret: true\n"
                            + "  - key: password\n    value: ''\n    enabled: true\n    secret: true\n  - key: plain\n    value: x\n    secret: false\n";
        File.WriteAllText(path, Text);
        var (workspace, _) = Open(root);
        var environment = workspace.Environments.Single(e => e.Name == "Dev");

        Assert.Equal(["baseUrl", "jwt secret", "password secret", "plain"], environment.Values.Select(v => v.Key + (v.IsSecret ? " secret" : "")));

        // An edit elsewhere leaves them as they are; a new secret is marked the way the file marks them.
        var values = KeyValueEntry.CloneList(environment.Values);
        values[0].Value = "http://localhost:9090";
        values.Add(new KeyValueEntry { Key = "token", Value = "t", Type = "secret" });
        environment.SetValues(values);
        environment.Save();

        Assert.Equal(Text.Replace("8080", "9090", StringComparison.Ordinal) + "  - key: token\n    value: t\n    enabled: true\n    secret: true\n", File.ReadAllText(path));
        Assert.True(PostmanEnvironment.Load(path).Values.Single(v => v.Key == "token").IsSecret);
    }

    [Fact]
    public void Values_without_an_enabled_key_keep_their_lines_when_another_value_changes()
    {
        var root = V3Fixture.Create();
        var path = Path.Combine(root, "postman", "environments", "Tools.environment.yaml");
        File.WriteAllText(path, "name: Tools\nvalues:\n  - key: base_url\n    value: \"http://localhost:4789\"\n  - key: token\n    value: \"\"\n  - key: owner\n    value: org\n");
        var (workspace, _) = Open(root);
        var environment = workspace.Environments.Single(e => e.Name == "Tools");
        var before = File.ReadAllText(path);

        var values = KeyValueEntry.CloneList(environment.Values);
        values[1].Value = "t-1";
        values[2].Enabled = false;
        environment.SetValues(values);
        environment.Save();

        // Disabled as Postman marks it in such files: "disabled: true".
        Assert.Equal(["- value: \"\"", "+ value: \"t-1\"", "+ disabled: true"], TestFiles.ChangedLines(before, File.ReadAllText(path)));
        Assert.Equal([true, true, false], PostmanEnvironment.Load(path).Values.Select(v => v.Enabled));
    }

    [Fact]
    public void Environment_values_postman_disabled_are_not_used()
    {
        // As Postman writes them: "disabled: true", no "enabled".
        var root = V3Fixture.Create();
        var path = Path.Combine(root, "postman", "environments", "Test.environment.yaml");
        const string Text = "name: Test\nvalues:\n  - key: manualStartDate\n    value: '2026-10-30'\n    disabled: true\n    description: Overwrite start dates\n"
                            + "  - key: baseUrl\n    value: 'https://test.example.com'\n";
        File.WriteAllText(path, Text);
        var (workspace, _) = Open(root);
        var environment = workspace.Environments.Single(e => e.Name == "Test");

        Assert.Equal([false, true], environment.Values.Select(v => v.Enabled));
        Assert.Equal("{{manualStartDate}} https://test.example.com",
            new VariableResolver([new EntryListScope("environment", environment.Values)]).Resolve("{{manualStartDate}} {{baseUrl}}"));
        environment.MarkDirty();
        environment.Save();
        Assert.Equal(Text, File.ReadAllText(path));

        // Enabled again, it loses its "disabled".
        var values = KeyValueEntry.CloneList(environment.Values);
        values[0].Enabled = true;
        environment.SetValues(values);
        environment.Save();
        Assert.Equal(Text.Replace("    disabled: true\n", "", StringComparison.Ordinal), File.ReadAllText(path));
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
    public void Names_starting_with_a_dot_stay_visible()
    {
        Assert.Equal("-NET API", V3Names.Safe(".NET API"));
        Assert.Equal("--resources", V3Names.Safe("..resources"));
        Assert.Equal("untitled", V3Names.Safe("..."));

        // A device name Windows reserves, also with an extension: the "-" follows the name.
        Assert.Equal(["CON-", "con-.example.org", "aux- .payments", "LPT¹-", "console.log", "con-artist"],
            ((string[])["CON", "con.example.org", "aux .payments", "LPT¹", "console.log", "con-artist"]).Select(V3Names.Safe));

        var root = V3Fixture.Create();
        var (workspace, collection) = Open(root);
        var created = workspace.CreateCollection(".NET API");
        var folder = collection.AddFolder(collection.RootNode, ".well-known");
        collection.AddRequest(folder, ".resources", new HttpRequestSpec { Url = "{{baseUrl}}/.well-known/resources" });
        collection.Save();

        var reopened = ApiWorkspace.Open(root);
        Assert.Contains(reopened.Collections, c => c.Name == ".NET API");
        Assert.NotNull(reopened.FindCollection(V3Fixture.CollectionPath(root))!.FindRequest(".well-known/.resources"));

        // Creating a collection never replaces one that is already there.
        Assert.Throws<IOException>(() => V3CollectionStorage.Create(created.FilePath!, "Other"));
        Assert.Equal("$kind: collection\nname: .NET API\n", File.ReadAllText(V3Names.DefinitionPath(created.FilePath!)));
    }

    [Fact]
    public void Names_too_long_for_a_file_name_are_never_cut_inside_a_character()
    {
        var name = new string('a', 63) + "\U0001F600 tail"; // the emoji is two UTF-16 units, the 64th and 65th
        var stem = new string('a', 63);
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        var (_, collection) = Open(root);

        var folder = collection.AddFolder(collection.RootNode, name);
        collection.AddRequest(folder, name, new HttpRequestSpec { Url = "{{baseUrl}}/smile" });
        collection.Save();

        Assert.True(File.Exists(Path.Combine(collectionPath, stem, stem + V3Names.RequestSuffix)));
        Assert.Equal("{{baseUrl}}/smile", PostmanCollection.Load(collectionPath).FindRequest($"{name}/{name}")!.ReadSpec().Url);
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
    [InlineData("legacy-v20.postman_collection.json")]
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

    [Fact]
    public void Converting_to_v3_keeps_the_other_v2_forms_termrest_reads()
    {
        var source = PostmanCollection.Parse("""
            {
              "info": { "name": "Forms", "schema": "https://schema.getpostman.com/json/collection/v2.0.0/collection.json" },
              "item": [ {
                "name": "Users",
                "event": [
                  { "listen": "prerequest", "disabled": true, "script": { "type": "text/javascript", "exec": [ "pm.variables.set('page', 3);" ] } },
                  { "listen": "test", "script": { "type": "text/javascript", "exec": [ "pm.test('ok', () => {});" ] } }
                ],
                "request": {
                  "method": "GET",
                  "header": "Accept: application/json\nX-Trace: 1",
                  "auth": { "type": "basic", "basic": { "username": "ada", "password": "s3cret" } },
                  "url": { "protocol": "https", "host": [ "api", "example", "com" ], "path": [ "users" ], "query": [ { "key": "page", "value": "2" } ] }
                },
                "response": [ { "name": "OK", "originalRequest": { "method": "GET", "header": "Accept: application/json", "url": { "host": [ "api", "example", "com" ], "path": [ "users" ] } },
                                "code": 200, "status": "OK", "header": [], "body": "[]" } ]
              } ]
            }
            """);

        var converted = source.ConvertToV3(Path.Combine(TestFiles.NewTempDirectory(), "Forms"));

        var spec = converted.FindRequest("Users")!.ReadSpec();
        Assert.Equal("https://api.example.com/users?page=2", spec.Url);
        Assert.Equal(["Accept: application/json", "X-Trace: 1"], spec.Headers.Select(h => $"{h.Key}: {h.Value}"));
        Assert.Equal(("basic", "ada", "s3cret"), (spec.Auth!.Type, spec.Auth.Get("username"), spec.Auth.Get("password")));
        Assert.Equal(new LanguageScripts(ScriptLanguage.JavaScript, "", "pm.test('ok', () => {});"), Assert.Single(spec.Scripts.Languages)); // a disabled script does not run
        var example = File.ReadAllText(Path.Combine(converted.FilePath!, ".resources", "Users.resources", "examples", "OK.example.yaml"));
        Assert.Contains("  url: api.example.com/users\n", example, StringComparison.Ordinal);
        Assert.Contains("    Accept: application/json\n", example, StringComparison.Ordinal);
    }

    [Fact]
    public void Converting_to_v3_writes_variables_and_example_names_as_postman_has_them()
    {
        var source = PostmanCollection.Parse("""
            {
              "info": { "name": "Shapes", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "variable": [
                { "id": "8f2e", "key": "baseUrl", "value": "https://api.example.com", "type": "string", "name": "Base URL", "system": false },
                { "key": "port", "value": 42 },
                { "key": "debug", "value": true },
                { "key": "apiKey", "value": "sk-live-123", "type": "secret" }
              ],
              "item": [ {
                "name": "Users",
                "request": { "method": "GET", "url": "{{baseUrl}}/users" },
                "response": [
                  { "name": "OK", "code": 200, "status": "OK", "body": "[]" },
                  { "name": "ok", "code": 200, "status": "OK", "body": "[]" },
                  { "name": "Created: user / 201", "code": 201, "status": "Created", "body": "{}" }
                ]
              } ]
            }
            """);

        var converted = source.ConvertToV3(Path.Combine(TestFiles.NewTempDirectory(), "Shapes"));

        // As Postman's v3 schema has them: values are text, a secret variable has no value (it stays in the JSON file it
        // came from), and v2.1's other keys are left out.
        Assert.Equal(["apiKey"], source.SecretValuesLeftOutOfV3());
        var definition = File.ReadAllText(Path.Combine(converted.FilePath!, ".resources", "definition.yaml"));
        Assert.Contains("  - key: baseUrl\n    value: https://api.example.com\n  - key: port\n    value: \"42\"\n  - key: debug\n    value: \"true\"\n  - key: apiKey\n    secret: true\n", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-123", definition, StringComparison.Ordinal);
        Assert.Equal(["baseUrl=https://api.example.com", "port=42", "debug=true", "apiKey="], converted.GetVariables().Select(v => $"{v.Key}={v.Value}"));

        // Example names their files can't have are kept in the files.
        var examples = Path.Combine(converted.FilePath!, ".resources", "Users.resources", "examples");
        Assert.DoesNotContain("name:", File.ReadAllText(Path.Combine(examples, "OK.example.yaml")), StringComparison.Ordinal);
        Assert.StartsWith("$kind: http-example\nname: ok\nrequest:", File.ReadAllText(Path.Combine(examples, "ok-1.example.yaml")), StringComparison.Ordinal);
        Assert.StartsWith("$kind: http-example\nname: \"Created: user / 201\"\nrequest:", File.ReadAllText(Path.Combine(examples, "Created- user - 201.example.yaml")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_renamed_request_takes_its_examples_path_along_as_the_file_writes_it()
    {
        // Postman writes the path with "./" and without.
        var root = V3Fixture.Create();
        var collectionPath = V3Fixture.CollectionPath(root);
        var file = Path.Combine(collectionPath, "Widgets", "List widgets.request.yaml");
        File.WriteAllText(file, File.ReadAllText(file).Replace("examples: ./.resources/", "examples: .resources/", StringComparison.Ordinal));
        Assert.Contains("examples: .resources/List widgets.resources/examples", File.ReadAllText(file), StringComparison.Ordinal);
        var (_, collection) = Open(root);

        collection.Rename(collection.FindRequest("List widgets")!, "List all widgets");
        collection.Save();

        Assert.Contains("examples: .resources/List all widgets.resources/examples\n", File.ReadAllText(Path.Combine(collectionPath, "Widgets", "List all widgets.request.yaml")), StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(collectionPath, "Widgets", ".resources", "List all widgets.resources", "examples")));
    }

    [Fact]
    public void Converting_to_v3_keeps_descriptions_as_text()
    {
        // Collections that Postman generates from OpenAPI describe fields and parameters with {content, type} objects.
        var source = PostmanCollection.Parse("""
            {
              "info": { "name": "Uploads", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "variable": [ { "key": "baseUrl", "value": "https://api.example.com", "description": { "content": "Where the API runs", "type": "text/plain" } } ],
              "item": [ {
                "name": "Upload",
                "request": {
                  "method": "POST",
                  "header": [ { "key": "X-Tenant", "value": "acme", "description": { "content": "Tenant id", "type": "text/plain" } } ],
                  "body": { "mode": "formdata", "formdata": [
                    { "key": "title", "value": "Foo", "type": "text", "description": "(Required) Shown under the image" },
                    { "key": "file", "type": "file", "src": [], "description": { "content": "(Required) A PNG or JPEG image", "type": "text/plain" } },
                    { "key": "tags", "value": "a", "type": "text", "description": { "content": "", "type": "text/plain" } },
                    { "key": "note", "value": "b", "type": "text", "description": null }
                  ] },
                  "url": { "raw": "{{baseUrl}}/upload?dryRun=1", "host": [ "{{baseUrl}}" ], "path": [ "upload" ],
                           "query": [ { "key": "dryRun", "value": "1", "description": { "content": "Validate only", "type": "text/plain" } } ] }
                },
                "response": [ { "name": "OK", "code": 200, "status": "OK", "body": "{}",
                                "header": [ { "key": "X-Rate-Remaining", "value": "9", "description": { "content": "Uploads left today", "type": "text/plain" } } ] } ]
              } ]
            }
            """);

        var converted = source.ConvertToV3(Path.Combine(TestFiles.NewTempDirectory(), "Uploads"));

        // As read back from the v3 files.
        var request = (JsonObject)converted.FindRequest("Upload")!.Json["request"]!;
        var parts = ((JsonArray)request["body"]!["formdata"]!).Cast<JsonObject>().ToList();
        Assert.Equal(["(Required) Shown under the image", "(Required) A PNG or JPEG image"], parts.Take(2).Select(p => p["description"]!.GetValue<string>()));
        Assert.All(parts.Skip(2), p => Assert.False(p.ContainsKey("description")));
        Assert.Equal("Tenant id", request["header"]![0]!["description"]!.GetValue<string>());
        Assert.Equal("Validate only", request["url"]!["query"]![0]!["description"]!.GetValue<string>());
        Assert.Equal("Where the API runs", converted.Root["variable"]![0]!["description"]!.GetValue<string>());
        var example = File.ReadAllText(Path.Combine(converted.FilePath!, ".resources", "Upload.resources", "examples", "OK.example.yaml"));
        Assert.Contains("      description: Uploads left today\n", example, StringComparison.Ordinal);
        Assert.Empty(converted.Verify());
    }
}
