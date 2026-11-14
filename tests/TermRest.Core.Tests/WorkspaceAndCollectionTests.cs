using System.Text.Json.Nodes;
using TermRest.Core.Certificates;
using TermRest.Core.Http;
using TermRest.Core.IO;
using TermRest.Core.Json;
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
    public void Environments_of_the_same_name_are_told_apart_by_their_file()
    {
        var directory = TestFiles.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "Staging.postman_environment.json"), """{ "name": "Staging", "values": [ { "key": "baseUrl", "value": "https://old" } ] }""");
        var export = Path.Combine(TestFiles.NewTempDirectory(), "Staging.postman_environment.json");
        File.WriteAllText(export, """{ "name": "Staging", "values": [ { "key": "baseUrl", "value": "https://new" } ] }""");
        var workspace = ApiWorkspace.Open(directory);

        // A teammate's updated export, imported next to the old one: it keeps the name.
        var updated = workspace.ImportEnvironment(export);
        workspace.ActiveEnvironment = updated;
        Assert.Same(updated, workspace.ActiveEnvironment);
        workspace.SaveSettings();

        // Opened again, it is loaded first ("Staging (2)" sorts before "Staging."), and the old one second.
        var reopened = ApiWorkspace.Open(directory);
        Assert.Equal(["Staging (2).postman_environment.json", "Staging.postman_environment.json"], reopened.Environments.Select(e => Path.GetFileName(e.FilePath!)));
        Assert.Equal("https://new", reopened.ActiveEnvironment?.Values[0].Value);
        reopened.ActiveEnvironment = reopened.Environments[1];
        reopened.SaveSettings();
        Assert.Equal("https://old", ApiWorkspace.Open(directory).ActiveEnvironment?.Values[0].Value);

        // The name decides when that file is gone (renamed outside TermRest), and in settings of earlier versions.
        File.Move(Path.Combine(directory, "Staging.postman_environment.json"), Path.Combine(directory, "Staging-old.postman_environment.json"));
        Assert.Equal("https://new", ApiWorkspace.Open(directory).ActiveEnvironment?.Values[0].Value);
        File.WriteAllText(workspace.SettingsPath, """{ "activeEnvironment": "staging" }""");
        Assert.Equal("https://new", ApiWorkspace.Open(directory).ActiveEnvironment?.Values[0].Value);
    }

    [Fact]
    public void Environments_of_the_same_name_are_shown_with_their_file()
    {
        var directory = TestFiles.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "Staging.postman_environment.json"), """{ "name": "Staging", "values": [] }""");
        File.WriteAllText(Path.Combine(directory, "Staging (2).postman_environment.json"), """{ "name": "staging", "values": [] }""");
        File.WriteAllText(Path.Combine(directory, "Prod.postman_environment.json"), """{ "name": "Prod", "values": [] }""");

        // Where their files have the same name too, with the file's path in the workspace.
        var folder = Directory.CreateDirectory(Path.Combine(directory, "postman", "environments")).FullName;
        File.WriteAllText(Path.Combine(folder, "Staging.postman_environment.json"), """{ "name": "Staging", "values": [] }""");

        var workspace = ApiWorkspace.Open(directory);

        Assert.Equal(["Prod", "staging (Staging (2).postman_environment.json)", "Staging (Staging.postman_environment.json)",
                      "Staging (postman/environments/Staging.postman_environment.json)"],
            workspace.Environments.Select(workspace.DisplayName));
    }

    [Fact]
    public void The_active_environment_s_name_and_file_are_saved_together()
    {
        // As "termrest convert dev.postman_environment.json" leaves them: both named "dev".
        var directory = TestFiles.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "dev.postman_environment.json"), """{ "name": "dev", "values": [] }""");
        var yamlPath = Path.Combine(Directory.CreateDirectory(Path.Combine(directory, "postman", "environments")).FullName, "dev.environment.yaml");
        File.WriteAllText(yamlPath, "name: dev\nvalues: []\n");
        var workspace = ApiWorkspace.Open(directory);
        workspace.ActiveEnvironment = workspace.Environments.Single(e => !e.IsYaml);
        workspace.SaveSettings();

        // A git pull changes the settings meanwhile; then the other "dev" is picked.
        File.WriteAllText(workspace.SettingsPath, """{ "activeEnvironment": "prod", "activeEnvironmentFile": "prod.postman_environment.json" }""");
        workspace.ActiveEnvironment = workspace.Environments.Single(e => e.IsYaml);
        workspace.SaveSettings();

        var saved = JsonNode.Parse(File.ReadAllText(workspace.SettingsPath))!;
        Assert.Equal(("dev", "postman/environments/dev.environment.yaml"), ((string?)saved["activeEnvironment"], (string?)saved["activeEnvironmentFile"]));
        Assert.Equal(yamlPath, ApiWorkspace.Open(directory).ActiveEnvironment?.FilePath);
    }

    [Fact]
    public void User_settings_are_kept_in_the_user_s_configuration_folder()
    {
        var directory = TestFiles.NewTempDirectory();
        Assert.Null(UserSettings.Load(directory).Theme);

        new UserSettings { Theme = "Nord" }.Save(directory);
        Assert.Equal("Nord", UserSettings.Load(directory).Theme);
        Assert.Contains("\"theme\": \"Nord\"", File.ReadAllText(Path.Combine(directory, "settings.json")), StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(directory, "settings.json"), "{ not json");
        Assert.Null(UserSettings.Load(directory).Theme);

        var previous = Environment.GetEnvironmentVariable(UserSettings.DirectoryVariable);
        try
        {
            Environment.SetEnvironmentVariable(UserSettings.DirectoryVariable, directory);
            Assert.Equal(Path.GetFullPath(directory), UserSettings.DefaultDirectory());
            Assert.Equal(Path.Combine(directory, "themes"), UserSettings.ThemesDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable(UserSettings.DirectoryVariable, previous);
        }
    }

    [Fact]
    public void A_proxy_in_the_workspace_settings_is_used_once_allowed_for_its_folder_and_again_after_each_change()
    {
        // A cloned repository's settings: a proxy that would see every request, and the CA it would read them with.
        var root = TestFiles.NewTempDirectory();
        var config = TestFiles.NewTempDirectory();
        const string proxy = "http://ada:Pr0xy-s3cret@collect.example:8080";
        var settings = Path.Combine(Directory.CreateDirectory(Path.Combine(root, ".termrest")).FullName, "settings.json");
        File.WriteAllText(settings, $$"""{ "proxy": "{{proxy}}", "caBundle": "certs/ca.pem" }""");
        ExecutionDefaults Defaults(string workspace, out string? blocked)
        {
            using var runner = new RequestRunner(ApiWorkspace.Open(workspace));
            runner.ConfigureProxy(config);
            blocked = runner.BlockedProxy;
            return runner.CreateDefaults();
        }

        var defaults = Defaults(root, out var blocked);
        Assert.Equal(proxy, blocked);
        Assert.Null(defaults.Proxy); // the system's proxy settings
        Assert.Equal(Path.Combine(root, "certs", "ca.pem"), defaults.CaBundlePath); // the other settings apply

        var user = UserSettings.Load(config);
        Assert.True(user.AllowProxy(root, proxy));
        Assert.False(user.AllowProxy(root + Path.DirectorySeparatorChar, proxy)); // nothing to save
        user.Save(config);
        var saved = File.ReadAllText(Path.Combine(config, "settings.json"));
        Assert.Contains("\"allowedProxies\": {", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("Pr0xy-s3cret", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("collect.example", saved, StringComparison.Ordinal);
        Assert.Equal(proxy, Defaults(root, out blocked).Proxy);
        Assert.Null(blocked);

        // The same proxy in another folder, and a changed one (a git pull), must be allowed again.
        var clone = TestFiles.NewTempDirectory();
        Directory.CreateDirectory(Path.Combine(clone, ".termrest"));
        File.Copy(settings, Path.Combine(clone, ".termrest", "settings.json"));
        Assert.Null(Defaults(clone, out blocked).Proxy);
        Assert.Equal(proxy, blocked);
        File.WriteAllText(settings, """{ "proxy": "http://collect.example:3128" }""");
        Assert.Null(Defaults(root, out blocked).Proxy);
        Assert.Equal("http://collect.example:3128", blocked);

        // The system's proxy settings, or none, need no approval.
        File.WriteAllText(settings, """{ "proxy": "none" }""");
        Assert.Equal("none", Defaults(root, out blocked).Proxy);
        Assert.Null(blocked);
        Assert.True(user.AllowProxy(root, null));
        Assert.Null(user.AllowedProxies);
    }

    [Fact]
    public void Requests_send_only_the_files_of_their_workspace_until_others_are_allowed_for_it()
    {
        // A collection from a repository someone else wrote could send any file of the user's to a server of its choosing.
        var root = TestFiles.NewTempDirectory();
        var config = TestFiles.NewTempDirectory();
        File.WriteAllText(Path.Combine(root, "order.json"), "{}");
        var secret = Path.Combine(TestFiles.NewTempDirectory(), "credentials");
        File.WriteAllText(secret, "s3cret");
        var workspace = ApiWorkspace.Open(root);
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace);
        runner.ConfigureFiles(config);
        List<string> Errors(BodyMode mode, params string[] files) => runner.Prepare(collection, null, new HttpRequestSpec
        {
            Method = "POST",
            Url = "https://api.example/upload",
            Body = new RequestBody
            {
                Mode = mode,
                FileSrc = files[0],
                FormData = [.. files.Select(f => new KeyValueEntry { Key = "file", Type = "file", Src = f })],
            },
        }).Errors;

        Assert.Empty(Errors(BodyMode.FormData, "order.json", Path.Combine(root, "order.json")));
        var escaping = Path.Combine("..", Path.GetFileName(Path.GetDirectoryName(secret))!, "credentials"); // from the collection's folder
        foreach (var outside in (string[])[secret, escaping])
        {
            Assert.Equal($"Form-data file '{secret}' is outside the workspace folder, so it is not sent (a collection could otherwise send any of " +
                         "your files): allow files from outside it for this workspace in the full-screen UI (Tools › Settings…), or with --allow-outside-files.",
                Assert.Single(Errors(BodyMode.FormData, outside)));
        }

        Assert.StartsWith($"Body file '{secret}' is outside the workspace folder, so it is not sent", Assert.Single(Errors(BodyMode.File, escaping)), StringComparison.Ordinal);

        // Nor through a link in the workspace (a repository can hold one), where links can be made.
        var link = Path.Combine(root, "avatar.png");
        try
        {
            File.CreateSymbolicLink(link, secret);
            Assert.StartsWith($"Form-data file '{link}' leads outside the workspace folder through a link, so it is not sent",
                Assert.Single(Errors(BodyMode.FormData, "avatar.png")), StringComparison.Ordinal);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Windows needs Developer Mode or elevation for symbolic links
        }

        // Allowed for this workspace folder in the user's settings (Tools › Settings…): any file is sent, there only.
        var user = UserSettings.Load(config);
        Assert.True(user.AllowOutsideFiles(root, true));
        Assert.False(user.AllowOutsideFiles(root + Path.DirectorySeparatorChar, true)); // nothing to save
        user.Save(config);
        runner.ConfigureFiles(config);
        Assert.Empty(Errors(BodyMode.FormData, secret, escaping));
        using var other = new RequestRunner(ApiWorkspace.Open(TestFiles.NewTempDirectory()));
        other.ConfigureFiles(config);
        Assert.False(other.OutsideFilesAllowed);
        Assert.True(user.AllowOutsideFiles(root, false));
        Assert.Null(user.OutsideFilesAllowed);
    }

    [Fact]
    public void A_link_that_climbs_out_of_the_workspace_through_another_link_is_not_sent()
    {
        // The OS takes a .. after a link from where the link leads, not from the path as written: repo/d/../id_rsa, written in
        // full in a link, looks like the repository's own decoy, but is the file next to where d leads.
        var top = TestFiles.NewTempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(top, "repo")).FullName;
        Directory.CreateDirectory(Path.Combine(top, "keys"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(top, "id_rsa"), "the user's key");
        File.WriteAllText(Path.Combine(root, "id_rsa"), "a decoy");
        File.WriteAllText(Path.Combine(root, "order.json"), "{}");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "d"), Path.Combine("..", "keys"));
            File.CreateSymbolicLink(Path.Combine(root, "logo.png"), Path.Combine(root, "d", "..", "id_rsa"));
            File.CreateSymbolicLink(Path.Combine(root, "sub", "order.json"), Path.Combine("..", "order.json"));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"Cannot create symbolic links here (Windows needs Developer Mode or elevation): {e.Message}");
        }

        var workspace = ApiWorkspace.Open(root);
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace);
        List<string> Errors(string file) => runner.Prepare(collection, null, new HttpRequestSpec
        {
            Method = "PUT",
            Url = "https://api.example/upload",
            Body = new RequestBody { Mode = BodyMode.File, FileSrc = file },
        }).Errors;

        // Linux and macOS read the key there. Windows takes such a .. as text when the link is made: it leads to the decoy.
        var leadsOut = File.ReadAllText(Path.Combine(root, "logo.png")) == "the user's key";
        Assert.True(leadsOut || OperatingSystem.IsWindows());
        if (leadsOut)
        {
            Assert.StartsWith($"Body file '{Path.Combine(root, "logo.png")}' leads outside the workspace folder through a link, so it is not sent",
                Assert.Single(Errors("logo.png")), StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(Errors("logo.png"));
        }

        Assert.Empty(Errors(Path.Combine("sub", "order.json"))); // a link out of a folder and back into the workspace
    }

    [Fact]
    public void Certificates_and_a_ca_bundle_on_network_shares_are_not_opened_until_files_from_outside_are_allowed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), @"Network paths (\\server\share) are Windows'.");

        // A cloned repository's settings and certificate registry, and a request pinning a certificate. On Windows, looking at a
        // file on a share signs in to its server with the user's credentials (nothing answers here: a look would wait).
        const string share = @"\\termrest.invalid\share";
        var root = TestFiles.NewTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, ".termrest"));
        File.WriteAllText(Path.Combine(root, ".termrest", "settings.json"), new JsonObject { ["caBundle"] = share + @"\ca.pem" }.ToJsonString());
        var workspace = ApiWorkspace.Open(root);
        workspace.Certificates.Entries.Add(new CertificateEntry { Name = "shared", Matches = ["<all_urls>"], Pfx = new FileSource { Src = share + @"\client.pfx" } });
        var collection = workspace.CreateCollection("API");
        using var runner = new RequestRunner(workspace);

        var pinned = runner.Prepare(collection, null, new HttpRequestSpec
        {
            Url = "https://api.example/",
            Certificate = new CertificateSpec { Name = "pinned", CertSrc = share + @"\client.pem", KeySrc = share + @"\client.key" },
        });
        var registered = runner.Prepare(collection, null, new HttpRequestSpec { Url = "https://api.example/" });

        Assert.Contains($@"CA bundle '{share}\ca.pem' not loaded: {ClientCertificateProvider.OnNetworkShare}", pinned.Warnings);
        Assert.Contains($@"Client certificate 'pinned' not used: '{share}\client.pem': {ClientCertificateProvider.OnNetworkShare}", pinned.Warnings);
        Assert.Contains($@"Client certificate 'shared' not used: '{share}\client.pfx': {ClientCertificateProvider.OnNetworkShare}", registered.Warnings);
        Assert.Null(registered.ClientCertificate);

        Assert.All((string[])[@"\\server\share\c.pem", "//server/share/c.pem", @"\\?\UNC\server\share\c.pem", @"\??\UNC\server\share\c.pem", @"\\.\pipe\x"],
            path => Assert.True(RealPath.IsNetworkPath(path), path));
        Assert.All((string[])[@"C:\certs\c.pem", @"\\?\C:\certs\c.pem", @"\??\C:\c.pem", @"\\?\Volume{0a1b2c3d-0000-0000-0000-000000000000}\c.pem", @"certs\c.pem", @"\certs"],
            path => Assert.False(RealPath.IsNetworkPath(path), path));
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
    public void New_collection_and_environment_files_get_names_valid_on_every_os()
    {
        // A workspace is shared through Git: Windows can't check out a name with | or ?, nor two names differing in case.
        var root = TestFiles.NewTempDirectory();
        var workspace = ApiWorkspace.Open(root);

        var collection = workspace.CreateCollection("Orders | v2?");
        var environment = workspace.CreateEnvironment("CON");
        var sameButCase = workspace.CreateCollection("orders | V2?");
        var longName = workspace.CreateEnvironment(new string('x', 300)); // too long for a file name: the name inside keeps it

        Assert.Equal(Path.Combine(root, "Orders - v2-.postman_collection.json"), collection.FilePath);
        Assert.Equal(Path.Combine(root, "CON-.postman_environment.json"), environment.FilePath);
        Assert.Equal(Path.Combine(root, "orders - V2- (2).postman_collection.json"), sameButCase.FilePath);
        Assert.Equal(Path.Combine(root, new string('x', 64) + ".postman_environment.json"), longName.FilePath);
        Assert.Equal(["Orders | v2?", "orders | V2?"], ApiWorkspace.Open(root).Collections.Select(c => c.Name).Order(StringComparer.Ordinal));
        Assert.Contains(ApiWorkspace.Open(root).Environments, e => e.Name == new string('x', 300));
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
    public void Certificates_changed_on_disk_since_they_were_read_are_saved_over_only_when_told_to()
    {
        var directory = TestFiles.NewTempDirectory();
        var workspace = ApiWorkspace.Open(directory); // no registry yet
        workspace.Certificates.Entries.Add(new CertificateEntry { Name = "mine", Matches = ["https://mine.example/*"] });
        var teammate = ApiWorkspace.Open(directory); // a git pull, or another TermRest
        teammate.Certificates.Entries.Add(new CertificateEntry { Name = "theirs", Matches = ["https://theirs.example/*"] });
        teammate.SaveCertificates();
        var pulled = File.ReadAllText(workspace.CertificatesPath);

        Assert.Equal([workspace.CertificatesPath], Assert.Throws<FileChangedException>(workspace.SaveCertificates).Paths);
        Assert.Equal(pulled, File.ReadAllText(workspace.CertificatesPath));

        workspace.ReloadCertificates();
        workspace.Certificates.Entries[0].Disabled = true;
        workspace.SaveCertificates();
        Assert.True(Assert.Single(ApiWorkspace.Open(directory).Certificates.Entries).Disabled);

        teammate.Certificates.Entries.Add(new CertificateEntry { Name = "more", Matches = ["https://more.example/*"] });
        Assert.Throws<FileChangedException>(teammate.SaveCertificates);
        teammate.SaveCertificates(overwriteChanged: true);
        Assert.Equal([("theirs", false), ("more", false)], ApiWorkspace.Open(directory).Certificates.Entries.Select(e => (e.Name, e.Disabled)));
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
    public void Importing_a_collection_the_workspace_would_not_load_where_it_is_copies_it_in()
    {
        // A v2.1 export saved under another name (as "openapi2postmanv2 -o collection.json" writes it), in the workspace folder.
        var directory = TestFiles.NewTempDirectory();
        var workspace = ApiWorkspace.Open(directory);
        var export = Path.Combine(directory, "collection.json");
        File.Copy(TestFiles.DemoCollection, export);

        Assert.Equal(Path.Combine(directory, "TermRest Demo.postman_collection.json"), workspace.ImportCollection(export).FilePath);

        // Added by a git pull where the workspace keeps collections: loaded where it is.
        var pulled = Path.Combine(directory, "Pulled.postman_collection.json");
        File.WriteAllText(pulled, """{ "info": { "name": "Pulled", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" }, "item": [] }""");
        Assert.Equal(pulled, workspace.ImportCollection(pulled).FilePath);
        Assert.Equal(["Pulled", "TermRest Demo"], ApiWorkspace.Open(directory).Collections.Select(c => c.Name));

        // In the v3 layout it is converted, as a JSON collection from elsewhere is.
        var root = V3Fixture.Create();
        var v3 = ApiWorkspace.Open(root);
        var rootExport = Path.Combine(root, "collection.json");
        File.Copy(TestFiles.DemoCollection, rootExport);
        var converted = v3.ImportCollection(rootExport);
        Assert.True(converted.IsV3);
        Assert.Equal(Path.Combine(root, "postman", "collections", "TermRest Demo"), converted.FilePath);
        Assert.NotNull(ApiWorkspace.Open(root).FindCollection(converted.FilePath!));
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

    [Fact]
    public void A_collection_folder_is_found_with_a_trailing_separator()
    {
        var folder = V3Fixture.CollectionPath(V3Fixture.Create());
        var workspace = ApiWorkspace.Open(folder + Path.DirectorySeparatorChar);

        var collection = workspace.FindCollection(folder + Path.DirectorySeparatorChar);
        Assert.NotNull(collection);
        Assert.Same(collection, workspace.FindCollection(folder + Path.AltDirectorySeparatorChar));
        Assert.Same(collection, workspace.FindCollection(folder));
    }

    [Fact]
    public void Settings_and_certificates_that_cannot_be_read_are_never_saved_over()
    {
        var directory = TestFiles.NewTempDirectory();
        var config = Directory.CreateDirectory(Path.Combine(directory, ApiWorkspace.ConfigDirectoryName)).FullName;
        const string settings = "{ \"caBundle\": \"C:\\certs\\ca.pem\", \"proxy\": \"http://proxy:8080\" }"; // a Windows path typed in: invalid JSON
        const string certificates = "<<<<<<< HEAD\n[]\n=======\n[]\n>>>>>>> theirs\n";
        File.WriteAllText(Path.Combine(config, "settings.json"), settings);
        File.WriteAllText(Path.Combine(config, "certificates.json"), certificates);

        var workspace = ApiWorkspace.Open(directory);

        Assert.Equal<string?>([workspace.SettingsError, workspace.CertificatesError], workspace.LoadErrors);
        Assert.StartsWith(workspace.SettingsPath, workspace.SettingsError, StringComparison.Ordinal);
        Assert.StartsWith(workspace.CertificatesPath, workspace.CertificatesError, StringComparison.Ordinal);
        workspace.Settings.Theme = "Nord";
        Assert.Contains(workspace.SettingsPath, Assert.Throws<IOException>(workspace.SaveSettings).Message, StringComparison.Ordinal);
        workspace.Certificates.Entries.Add(new CertificateEntry { Name = "new", Matches = ["https://h/*"] });
        Assert.Contains(workspace.CertificatesPath, Assert.Throws<IOException>(workspace.SaveCertificates).Message, StringComparison.Ordinal);
        Assert.Equal(settings, File.ReadAllText(workspace.SettingsPath));
        Assert.Equal(certificates, File.ReadAllText(workspace.CertificatesPath));

        // A file deleted meanwhile (to start over) holds nothing to lose.
        File.Delete(workspace.SettingsPath);
        workspace.SaveSettings();
        Assert.Equal("Nord", ApiWorkspace.Open(directory).Settings.Theme);
    }

    [Fact]
    public void Files_with_a_duplicate_key_are_reported_instead_of_crashing()
    {
        // A merge conflict resolved by keeping both lines.
        var directory = TestFiles.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "Api.postman_collection.json"), """
            { "info": { "name": "Api", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [ { "name": "Ping", "description": "ours", "description": "theirs", "request": { "method": "GET", "url": "https://h/ping" } } ] }
            """);
        File.WriteAllText(Path.Combine(directory, "dev.postman_environment.json"), """
            { "name": "dev", "values": [ { "key": "baseUrl", "value": "https://ours", "value": "https://theirs", "enabled": true } ] }
            """);

        var workspace = ApiWorkspace.Open(directory);

        Assert.Empty(workspace.Collections);
        Assert.Empty(workspace.Environments);
        Assert.Equal(2, workspace.LoadErrors.Count(e => e.Contains("Duplicate property", StringComparison.Ordinal)));
    }

    [Fact]
    public void Files_with_half_of_a_surrogate_pair_are_reported_instead_of_crashing()
    {
        // JavaScript's JSON.stringify writes half of an emoji that was cut in two as an escape.
        var directory = TestFiles.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "Api.postman_collection.json"), """
            { "info": { "name": "Api", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              // a comment may hold "\ud83d", and so may a string whose backslash is escaped:
              "item": [ { "name": "Ping \\ud83d", "request": { "method": "GET", "url": "https://h/ping" } },
                        { "name": "Pong \ud83d\ude00", "request": { "method": "GET", "url": "https://h/pong" } } ] }
            """);
        File.WriteAllText(Path.Combine(directory, "dev.postman_environment.json"), """
            { "name": "dev",
              "values": [ { "key": "greeting", "value": "Hi \ud83d", "enabled": true } ] }
            """);
        File.WriteAllText(Path.Combine(directory, "workspace.postman_globals.json"), "{ \"name\": \"Globals\", \"values\": [ { \"key\": \"x\\uDE00\", \"value\": \"\" } ] }");

        var workspace = ApiWorkspace.Open(directory);

        Assert.Equal(["Ping \\ud83d", "Pong 😀"], Assert.Single(workspace.Collections).AllRequests().Select(r => r.Name));
        Assert.Empty(workspace.Environments);
        Assert.Null(workspace.Globals);
        Assert.Equal(2, workspace.LoadErrors.Count);
        Assert.StartsWith("dev.postman_environment.json: ", workspace.LoadErrors[0], StringComparison.Ordinal);
        Assert.Contains("\"\\ud83d\" at line 2, column 49", workspace.LoadErrors[0], StringComparison.Ordinal);
        Assert.Contains("\"\\uDE00\" at line 1, column 45", workspace.LoadErrors[1], StringComparison.Ordinal);
        Assert.Null(PostmanJson.TryPrettyPrint("{ \"a\": \"\\ud83d\" }"));
    }

    [Fact]
    public void Importing_an_environment_writes_it_in_the_workspace_s_format()
    {
        var root = V3Fixture.Create();
        var workspace = ApiWorkspace.Open(root);
        var outside = TestFiles.NewTempDirectory();

        // Added by a git pull where the workspace keeps environments: loaded where it is.
        var pulled = Path.Combine(root, "postman", "environments", "Staging.environment.yaml");
        File.WriteAllText(pulled, "name: Staging\nvalues:\n  - key: token\n    value: s3cret\n    enabled: true\n");
        Assert.Equal(pulled, workspace.ImportEnvironment(pulled).FilePath);

        // From elsewhere: converted to YAML in the v3 layout.
        var json = TestFiles.CopyToTemp(TestFiles.DemoEnvironment, outside);
        var imported = workspace.ImportEnvironment(json);
        Assert.Equal(Path.Combine(root, "postman", "environments", "Postman Echo.environment.yaml"), imported.FilePath);
        Assert.Equal(PostmanEnvironment.Load(json).Values.Select(v => v.Key), PostmanEnvironment.Load(imported.FilePath!).Values.Select(v => v.Key));

        // And to JSON in a JSON workspace.
        var plain = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var converted = plain.ImportEnvironment(pulled);
        Assert.Equal(Path.Combine(plain.RootPath, "Staging.postman_environment.json"), converted.FilePath);
        Assert.Equal("s3cret", PostmanEnvironment.Load(converted.FilePath!).Values[0].Value);

        // A file that cannot be loaded leaves nothing behind, and the error does not show its values.
        var broken = Path.Combine(outside, "Broken.postman_environment.json");
        File.WriteAllText(broken, "name: Broken\nvalues:\n  - key: token\n    value: s3cret\n");
        Assert.DoesNotContain("s3cret", Assert.Throws<InvalidDataException>(() => plain.ImportEnvironment(broken)).Message, StringComparison.Ordinal);
        Assert.Equal([converted.FilePath], Directory.GetFiles(plain.RootPath));
        Assert.Equal(["Local", "Staging", "Postman Echo"], workspace.Environments.Select(e => e.Name));
        Assert.DoesNotContain(ApiWorkspace.Open(root).LoadErrors, e => !e.Contains("non-HTTP", StringComparison.Ordinal));
    }

    [Fact]
    public void Importing_a_globals_export_makes_it_the_workspace_s_globals()
    {
        var export = Path.Combine(TestFiles.NewTempDirectory(), "My Workspace.postman_globals.json");
        File.WriteAllText(export, """{ "name": "My Workspace Globals", "values": [ { "key": "tenant", "value": "acme", "enabled": true } ], "_postman_variable_scope": "globals" }""");

        // A JSON workspace keeps its globals in workspace.postman_globals.json.
        var plain = ApiWorkspace.Open(TestFiles.NewTempDirectory());
        var globals = plain.ImportEnvironment(export);
        Assert.Same(globals, plain.Globals);
        Assert.Empty(plain.Environments);
        Assert.Equal(Path.Combine(plain.RootPath, "workspace.postman_globals.json"), globals.FilePath);
        Assert.Equal("acme", ApiWorkspace.Open(plain.RootPath).Globals?.Values.Single().Value);

        // The v3 layout in postman/globals/workspace.globals.yaml, where Postman keeps them.
        var root = V3Fixture.Create();
        File.Delete(Path.Combine(root, "postman", "globals", "workspace.globals.yaml"));
        ApiWorkspace.Open(root).ImportEnvironment(export);
        var reopened = ApiWorkspace.Open(root);
        Assert.Equal(Path.Combine(root, "postman", "globals", "workspace.globals.yaml"), reopened.Globals?.FilePath);
        Assert.Equal("acme", reopened.Globals?.Values.Single().Value);
        Assert.Equal(["Local"], reopened.Environments.Select(e => e.Name));
    }

    [Fact]
    public void Importing_globals_is_refused_while_the_workspace_has_its_own()
    {
        var export = Path.Combine(TestFiles.NewTempDirectory(), "My Workspace.postman_globals.json");
        File.WriteAllText(export, """{ "name": "My Workspace Globals", "values": [], "_postman_variable_scope": "globals" }""");
        var root = V3Fixture.Create();
        var workspace = ApiWorkspace.Open(root);
        var own = workspace.Globals!;
        var before = V3Fixture.Snapshot(root);

        var error = Assert.Throws<InvalidOperationException>(() => workspace.ImportEnvironment(export));

        Assert.Contains(Path.Combine("postman", "globals", "workspace.globals.yaml"), error.Message, StringComparison.Ordinal);
        Assert.Same(own, workspace.Globals);
        Assert.Equal(["Local"], workspace.Environments.Select(e => e.Name));
        Assert.Equal(before, V3Fixture.Snapshot(root));

        // Also one that could not be read: it is not saved over, nor joined by another that Open might load in its place.
        var directory = TestFiles.NewTempDirectory();
        File.WriteAllText(Path.Combine(directory, "team.postman_globals.json"), "<<<<<<< HEAD\n");
        var conflicted = ApiWorkspace.Open(directory);
        Assert.Null(conflicted.Globals);
        Assert.Contains("team.postman_globals.json", Assert.Throws<InvalidOperationException>(() => conflicted.ImportEnvironment(export)).Message, StringComparison.Ordinal);
        Assert.Equal(["team.postman_globals.json"], Directory.GetFiles(directory).Select(Path.GetFileName));

        // Its own file is reloaded, as an open environment file is, but never over unsaved changes.
        own.SetValue("tenant", "acme");
        Assert.Contains("unsaved changes", Assert.Throws<InvalidOperationException>(() => workspace.ImportEnvironment(own.FilePath!)).Message, StringComparison.Ordinal);
        Assert.Same(own, workspace.Globals);
        own.Save();
        var reloaded = workspace.ImportEnvironment(own.FilePath!);
        Assert.Same(reloaded, workspace.Globals);
        Assert.Equal(("tenant", "acme"), (reloaded.Values.Single().Key, reloaded.Values.Single().Value));
        Assert.Equal(["Local"], workspace.Environments.Select(e => e.Name));
    }

    [Fact]
    public void Saving_settings_writes_only_what_changed_and_keeps_the_rest_of_the_file()
    {
        var directory = TestFiles.NewTempDirectory();
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(directory, ApiWorkspace.ConfigDirectoryName)).FullName, "settings.json");
        File.WriteAllText(path, "{\n  // shared with the team\n  \"strictSsl\": false,\n  \"proxy\": \"http://proxy:8080\",\n  \"futureSetting\": [1, 2],\n}\n");
        var workspace = ApiWorkspace.Open(directory);

        // Nothing changed: nothing written.
        var written = File.GetLastWriteTimeUtc(path);
        var text = File.ReadAllText(path);
        workspace.SaveSettings();
        Assert.Equal((text, written), (File.ReadAllText(path), File.GetLastWriteTimeUtc(path)));

        // A git pull changes the file meanwhile; this session changes other settings.
        File.WriteAllText(path, "{ \"strictSsl\": true, \"proxy\": \"http://proxy:8080\", \"futureSetting\": [1, 2], \"recordingRedact\": [\"X-Tenant-Key\"] }");
        workspace.Settings.Theme = "Nord";
        workspace.Settings.Proxy = null;
        workspace.SaveSettings();

        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(["strictSsl", "futureSetting", "recordingRedact", "theme"], saved.Select(p => p.Key));
        Assert.Equal("""{"strictSsl":true,"futureSetting":[1,2],"recordingRedact":["X-Tenant-Key"],"theme":"Nord"}""", saved.ToJsonString());
        Assert.False(workspace.Settings.StrictSsl, "the settings in use are not reloaded");

        // A file that cannot be read now (a merge conflict) is not saved over.
        const string conflicted = "<<<<<<< HEAD\n{ \"theme\": \"Nord\" }\n=======\n{ \"theme\": \"Dracula\" }\n>>>>>>> theirs\n";
        File.WriteAllText(path, conflicted);
        workspace.Settings.TimeoutMs = 5000;
        Assert.Contains(path, Assert.Throws<IOException>(workspace.SaveSettings).Message, StringComparison.Ordinal);
        Assert.Equal(conflicted, File.ReadAllText(path));
    }

    [Fact]
    public void Json_files_changed_on_disk_since_they_were_read_are_not_saved_over()
    {
        var workspace = OpenSampleCopy();
        var collection = workspace.Collections[0];
        var environment = workspace.Environments[0];

        // A git pull changes both files; TermRest's edits meanwhile must not undo that silently.
        var pulledCollection = File.ReadAllText(collection.FilePath!).Replace("\"name\": \"TermRest Demo\"", "\"name\": \"TermRest Demo v2\"", StringComparison.Ordinal);
        File.WriteAllText(collection.FilePath!, pulledCollection);
        var pulledEnvironment = File.ReadAllText(environment.FilePath!).Replace("env-token-123", "env-token-456", StringComparison.Ordinal); // as long
        File.WriteAllText(environment.FilePath!, pulledEnvironment);
        File.SetLastWriteTimeUtc(environment.FilePath!, DateTime.UtcNow.AddMinutes(1));
        var node = collection.FindRequest("Basics/GET with query")!;
        var spec = node.ReadSpec();
        spec.Method = "HEAD";
        collection.UpdateRequest(node, spec);
        environment.SetValue("baseUrl", "http://localhost:5000");

        Assert.Equal([Path.GetFullPath(collection.FilePath!)], Assert.Throws<FileChangedException>(() => collection.Save()).Paths);
        Assert.Equal([Path.GetFullPath(environment.FilePath!)], Assert.Throws<FileChangedException>(() => environment.Save()).Paths);
        Assert.Equal(pulledCollection, File.ReadAllText(collection.FilePath!));
        Assert.Equal(pulledEnvironment, File.ReadAllText(environment.FilePath!));
        Assert.True(collection.IsDirty && environment.IsDirty);

        collection.Save(overwriteChanged: true);
        environment.Save(overwriteChanged: true);
        Assert.False(collection.IsDirty || environment.IsDirty);
        var reloaded = PostmanCollection.Load(collection.FilePath!);
        Assert.Equal(("TermRest Demo", "HEAD"), (reloaded.Name, reloaded.FindRequest("Basics/GET with query")!.ReadSpec().Method));
        Assert.Equal("http://localhost:5000", PostmanEnvironment.Load(environment.FilePath!).Values[0].Value);

        // Only touched: not a change.
        File.SetLastWriteTimeUtc(collection.FilePath!, DateTime.UtcNow.AddMinutes(2));
        collection.MarkDirty();
        collection.Save();
    }

    [Fact]
    public void User_settings_that_cannot_be_read_are_reported_and_never_saved_over()
    {
        var directory = TestFiles.NewTempDirectory();
        var path = Path.Combine(directory, "settings.json");
        const string text = "{ \"pythonInterpreters\": { \"C:\\work\": \"C:\\Python312\\python.exe\" } }";
        File.WriteAllText(path, text);

        var settings = UserSettings.Load(directory);
        settings.Theme = "Nord";

        Assert.StartsWith(path + ": ", settings.LoadError, StringComparison.Ordinal);
        Assert.Contains(path, Assert.Throws<IOException>(() => settings.Save(directory)).Message, StringComparison.Ordinal);
        Assert.Equal(text, File.ReadAllText(path));
        Assert.Null(UserSettings.Load(TestFiles.NewTempDirectory()).LoadError);
    }

    [Fact]
    public void Commands_can_open_a_workspace_without_its_other_collections()
    {
        var directory = TestFiles.NewTempDirectory();
        var collection = TestFiles.CopyToTemp(TestFiles.DemoCollection, directory);
        TestFiles.CopyToTemp(TestFiles.DemoEnvironment, directory);
        File.WriteAllText(Path.Combine(directory, "Broken.postman_collection.json"), "<<<<<<< HEAD");

        var all = ApiWorkspace.Open(collection);
        Assert.Single(all.Collections);
        Assert.Single(all.LoadErrors);

        var requested = ApiWorkspace.Open(collection, CollectionLoading.Requested);
        Assert.Equal("TermRest Demo", Assert.Single(requested.Collections).Name);
        Assert.Empty(requested.LoadErrors);
        Assert.Equal("Postman Echo", Assert.Single(requested.Environments).Name);
        Assert.Single(ApiWorkspace.Open(directory, CollectionLoading.Requested).LoadErrors); // a workspace folder: every collection

        var none = ApiWorkspace.Open(collection, CollectionLoading.None);
        Assert.Equal((directory, 0, 0, 1), (none.RootPath, none.Collections.Count, none.LoadErrors.Count, none.Environments.Count));
    }

    [Fact]
    public void A_file_in_the_postman_folders_opens_the_workspace_that_holds_them()
    {
        // A JSON collection kept in postman/collections belongs to the workspace there, as a v3 collection does.
        var root = TestFiles.NewTempDirectory();
        var config = Directory.CreateDirectory(Path.Combine(root, ApiWorkspace.ConfigDirectoryName)).FullName;
        File.WriteAllText(Path.Combine(config, "settings.json"), """{ "activeEnvironment": "staging", "proxy": "http://proxy:8080" }""");
        var collection = TestFiles.CopyToTemp(TestFiles.DemoCollection, Directory.CreateDirectory(Path.Combine(root, "postman", "collections")).FullName);
        var environment = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "postman", "environments")).FullName, "staging.environment.yaml");
        File.WriteAllText(environment, "name: staging\nvalues: []\n");
        var globals = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "postman", "globals")).FullName, "workspace.postman_globals.json");
        File.WriteAllText(globals, """{ "name": "Globals", "values": [], "_postman_variable_scope": "globals" }""");

        var workspace = ApiWorkspace.Open(collection, CollectionLoading.Requested);

        Assert.Equal((root, "http://proxy:8080", environment), (workspace.RootPath, workspace.Settings.Proxy, workspace.ActiveEnvironment?.FilePath));
        Assert.Same(Assert.Single(workspace.Collections), workspace.FindCollection(collection));
        Assert.Empty(workspace.LoadErrors);

        // So do environment and globals files there (a globals file is no collection).
        foreach (var file in new[] { environment, globals })
        {
            var opened = ApiWorkspace.Open(file, CollectionLoading.Requested);
            Assert.Equal((root, globals, 0), (opened.RootPath, opened.Globals?.FilePath, opened.LoadErrors.Count));
        }

        // A collection anywhere else opens the folder it is in, as before.
        Assert.Equal(root, ApiWorkspace.Open(TestFiles.CopyToTemp(TestFiles.DemoCollection, root)).RootPath);
        var exports = Directory.CreateDirectory(Path.Combine(root, "postman", "exports")).FullName;
        Assert.Equal(exports, ApiWorkspace.Open(TestFiles.CopyToTemp(TestFiles.DemoCollection, exports)).RootPath);
    }
}
