using TermRest.Core.Certificates;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using Xunit;

namespace TermRest.Core.Tests;

public class WorkspaceAndCollectionTests
{
    private static ApiWorkspace OpenSampleCopy()
    {
        var directory = TestFiles.NewTempDirectory();
        TestFiles.CopyToTemp(TestFiles.DemoCollection, directory);
        TestFiles.CopyToTemp(TestFiles.DemoEnvironment, directory);
        return ApiWorkspace.Open(directory);
    }

    [Fact]
    public void Opens_collections_environments_and_persists_settings()
    {
        var workspace = OpenSampleCopy();

        Assert.Empty(workspace.LoadErrors);
        Assert.Equal("TermRest Demo", Assert.Single(workspace.Collections).Name);
        Assert.Equal("Postman Echo", Assert.Single(workspace.Environments).Name);

        workspace.ActiveEnvironment = workspace.Environments[0];
        workspace.Settings.CaBundle = "certs/ca.pem";
        workspace.SaveSettings();

        var reopened = ApiWorkspace.Open(workspace.RootPath);
        Assert.Equal("Postman Echo", reopened.ActiveEnvironment?.Name);
        Assert.Equal("certs/ca.pem", reopened.Settings.CaBundle);
    }

    [Fact]
    public void Runner_resolves_environment_over_collection_variables()
    {
        var workspace = OpenSampleCopy();
        workspace.ActiveEnvironment = workspace.Environments[0];
        using var runner = new RequestRunner(workspace);
        var collection = workspace.Collections[0];
        var node = collection.FindRequest("Auth/Auth inherited from folder")!;

        var prepared = runner.Prepare(collection, node, node.ReadSpec());

        Assert.Equal("https://postman-echo.com/headers", prepared.Url);
        Assert.Equal("Bearer env-token-123", prepared.GetHeader("Authorization"));

        runner.LocalVariables["token"] = "cli-token";
        Assert.Equal("Bearer cli-token", runner.Prepare(collection, node, node.ReadSpec()).GetHeader("Authorization"));

        workspace.ActiveEnvironment = null;
        runner.LocalVariables.Clear();
        var noEnvironment = runner.Prepare(collection, node, node.ReadSpec());
        Assert.Equal("Bearer collection-token", noEnvironment.GetHeader("Authorization"));
        Assert.Contains("baseUrl", noEnvironment.UnresolvedVariables);
    }

    [Fact]
    public void Path_variables_and_disabled_params_are_applied_when_preparing()
    {
        var workspace = OpenSampleCopy();
        workspace.ActiveEnvironment = workspace.Environments[0];
        using var runner = new RequestRunner(workspace);
        var collection = workspace.Collections[0];

        Assert.Equal("https://postman-echo.com/status/418", runner.Prepare(collection, collection.FindRequest("Status by path variable"), collection.FindRequest("Status by path variable")!.ReadSpec()).Url);
        Assert.Equal("https://postman-echo.com/get?foo=bar&source=termrest", runner.Prepare(collection, null, collection.FindRequest("GET with query")!.ReadSpec()).Url);
    }

    [Fact]
    public void Collection_edits_add_rename_duplicate_move_and_remove()
    {
        var workspace = OpenSampleCopy();
        var collection = workspace.Collections[0];
        var basics = collection.FindFolder("Basics")!;

        var folder = collection.AddFolder(collection.RootNode, "New folder");
        var request = collection.AddRequest(folder, "New request", new HttpRequestSpec { Method = "DELETE", Url = "https://example.com/items/1" });
        collection.Rename(request, "Delete item");
        var copy = (RequestNode)collection.Duplicate(request);
        collection.Move(copy, basics, 0);
        collection.Remove(collection.FindRequest("Basics/POST JSON")!);
        collection.Save();

        var reloaded = PostmanCollection.Load(collection.FilePath!);
        Assert.Equal(["Delete item Copy", "GET with query", "Form URL-encoded", "Status by path variable"], reloaded.FindFolder("Basics")!.Children.Select(c => c.Name));
        var saved = reloaded.FindRequest("New folder/Delete item")!.ReadSpec();
        Assert.Equal("DELETE", saved.Method);
        Assert.Equal("https://example.com/items/1", saved.Url);
        Assert.Throws<InvalidOperationException>(() => reloaded.Move(reloaded.FindFolder("New folder")!, reloaded.FindFolder("New folder")!, 0));
    }

    [Fact]
    public void Requests_named_like_url_paths_are_found_by_path_and_name()
    {
        var collection = PostmanCollection.Create("Paths");
        var top = collection.AddRequest(collection.RootNode, "/users", new HttpRequestSpec { Url = "https://example.com/users" });
        var folder = collection.AddFolder(collection.RootNode, "v1");
        var nested = collection.AddRequest(folder, "users/:id", new HttpRequestSpec { Url = "https://example.com/v1/users/:id" });

        Assert.Same(top, collection.FindRequest("/users"));
        Assert.Same(nested, collection.FindRequest("v1/users/:id"));
        Assert.Same(nested, collection.FindRequest("/v1/users/:id"));
        Assert.Same(nested, collection.FindRequest("users/:id"));
        Assert.Same(folder, collection.FindFolder("/v1/"));
        Assert.Same(collection.RootNode, collection.FindFolder(""));
        Assert.Null(collection.FindRequest("users"));
    }

    [Fact]
    public void New_collection_and_environment_files_use_postman_format()
    {
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());

        var collection = workspace.CreateCollection("My API");
        var environment = workspace.CreateEnvironment("dev");
        environment.SetValues([new KeyValueEntry { Key = "baseUrl", Value = "http://localhost:5000" }]);
        environment.Save();

        Assert.EndsWith("My API.postman_collection.json", collection.FilePath, StringComparison.Ordinal);
        var text = File.ReadAllText(collection.FilePath!);
        Assert.Contains("\"schema\": \"https://schema.getpostman.com/json/collection/v2.1.0/collection.json\"", text, StringComparison.Ordinal);
        Assert.Contains("\t\"item\": []", text, StringComparison.Ordinal);
        var envText = File.ReadAllText(environment.FilePath!);
        Assert.Contains("\"type\": \"default\",\n\t\t\t\"enabled\": true", envText, StringComparison.Ordinal);
    }

    [Fact]
    public void Certificate_registry_round_trips_newman_format()
    {
        var workspace = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        workspace.Certificates.Entries.Add(new CertificateEntry
        {
            Name = "dev",
            Matches = ["https://*.contoso.com/*"],
            Cert = new FileSource { Src = "certs/client.pem" },
            Key = new FileSource { Src = "certs/client.key" },
            Passphrase = "env:CLIENT_KEY_PASS",
        });
        workspace.Certificates.Entries.Add(new CertificateEntry { Name = "store", Matches = ["https://internal/*"], Store = new StoreSource { Thumbprint = "ABCD" } });
        workspace.SaveCertificates();

        var text = File.ReadAllText(workspace.CertificatesPath);
        Assert.Contains("\"cert\": {", text, StringComparison.Ordinal);
        Assert.Contains("\"x-store\": {", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pfx\"", text, StringComparison.Ordinal);

        var reopened = ApiWorkspace.Open(workspace.RootPath);
        Assert.Equal(["dev", "store"], reopened.Certificates.Entries.Select(e => e.Name));
        Assert.Equal(CertificateSourceKind.Store, reopened.Certificates.Entries[1].Kind);
    }

    [Fact]
    public void Importing_an_open_collection_reloads_it_but_never_drops_unsaved_changes()
    {
        var workspace = OpenSampleCopy();
        var original = workspace.Collections[0];
        var node = original.FindRequest("Basics/GET with query")!;
        var spec = node.ReadSpec();
        spec.Url += "&edited=1";
        spec.QueryParams = PostmanUrl.SyncQueryFromRaw(spec.Url, spec.QueryParams);
        original.UpdateRequest(node, spec);
        Assert.True(original.IsDirty);

        var error = Assert.Throws<InvalidOperationException>(() => workspace.ImportCollection(original.FilePath!));
        Assert.Contains("unsaved changes", error.Message, StringComparison.Ordinal);
        Assert.Same(original, Assert.Single(workspace.Collections));
        Assert.True(workspace.HasUnsavedChanges);

        var reloaded = workspace.ReloadCollection(original);
        Assert.Same(reloaded, Assert.Single(workspace.Collections));
        Assert.False(workspace.HasUnsavedChanges);
        Assert.DoesNotContain("edited=1", reloaded.FindRequest("Basics/GET with query")!.ReadSpec().Url, StringComparison.Ordinal);

        var again = workspace.ImportCollection(reloaded.FilePath!);
        Assert.Same(again, Assert.Single(workspace.Collections));
        Assert.Same(again, workspace.FindCollection(Path.Combine(workspace.RootPath, ".", Path.GetFileName(again.FilePath!))));
    }

    [Fact]
    public void Importing_an_open_environment_refuses_to_drop_unsaved_changes()
    {
        var workspace = OpenSampleCopy();
        var environment = workspace.Environments[0];
        environment.SetValues([new KeyValueEntry { Key = "changed", Value = "1" }]);
        Assert.True(environment.IsDirty);

        Assert.Throws<InvalidOperationException>(() => workspace.ImportEnvironment(environment.FilePath!));
        Assert.Same(environment, Assert.Single(workspace.Environments));

        environment.Save();
        var reloaded = workspace.ImportEnvironment(environment.FilePath!);
        Assert.Same(reloaded, Assert.Single(workspace.Environments));
        Assert.Equal("changed", Assert.Single(reloaded.Values).Key);
    }
}
