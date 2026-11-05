using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using TermRest.Tui.Tests.Infrastructure;
using Xunit;

namespace TermRest.Tui.Tests;

/// <summary>
/// Drives the real main window headlessly through Terminal.Gui's ANSI driver: renders to an in-memory screen,
/// injects mouse and keyboard input, and asserts on what a user would see.
/// </summary>
[Collection("TUI")]
public sealed class MainWindowTests : IDisposable
{
    private readonly string _directory;

    public MainWindowTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "termrest-ui-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_directory);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "samples"), "*.json"))
        {
            File.Copy(file, Path.Combine(_directory, Path.GetFileName(file)));
        }

        TuiApp.ConfigureKeyBindings();
    }

    private sealed class Harness : IDisposable
    {
        public Harness(string directory, Action<RequestRunner>? configure = null)
        {
            Workspace = ApiWorkspace.Open(directory);
            Runner = new RequestRunner(Workspace);
            configure?.Invoke(Runner);
            App = Application.Create();
            App.Init(DriverRegistry.Names.ANSI);
            App.Driver!.SetScreenSize(150, 45);
            Window = TuiApp.CreateWindow(Workspace, Runner);
        }

        public ApiWorkspace Workspace { get; }

        public RequestRunner Runner { get; }

        public IApplication App { get; }

        public MainWindow Window { get; }

        public string Screen()
        {
            App.LayoutAndDraw(forceRedraw: true);
            return App.Driver!.ToString() ?? "";
        }

        /// <summary>Screen position of the first occurrence of <paramref name="text"/>.</summary>
        public Point Find(string text)
        {
            var lines = Screen().Split('\n');
            for (var y = 0; y < lines.Length; y++)
            {
                var x = lines[y].IndexOf(text, StringComparison.Ordinal);
                if (x >= 0)
                {
                    return new Point(x, y);
                }
            }

            throw new Xunit.Sdk.XunitException($"'{text}' is not on screen:\n{string.Join('\n', lines)}");
        }

        public void Click(string text) => App.InjectSequence(InputInjectionExtensions.LeftButtonClick(Find(text)));

        public void Type(string text)
        {
            foreach (var c in text)
            {
                App.InjectKey(new Key(c));
            }
        }

        public void Dispose()
        {
            Window.Dispose();
            App.Dispose();
            Runner.Dispose();
        }
    }

    [Fact]
    public void Renders_menu_tree_editor_and_status_bar()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        var screen = ui.Screen();
        ui.App.End(token!);

        OnScreen("File", screen);
        OnScreen("Environment", screen);
        OnScreen("TermRest Demo", screen);
        OnScreen("GET  GET with query", screen);
        OnScreen("POST POST JSON", screen);
        OnScreen("{{baseUrl}}/get?foo=bar&source=termrest", screen);
        OnScreen("Params", screen);
        OnScreen("Headers", screen);
        OnScreen("Disabled parameters are kept but not sent", screen);
        OnScreen("Send", screen);
        OnScreen("Env: none", screen);
    }

    [Fact]
    public void Clicking_a_tree_item_opens_that_request()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);

        ui.Click("POST POST JSON");
        var screen = ui.Screen();
        ui.App.End(token!);

        Assert.Equal("{{baseUrl}}/post", ui.Window.Editor.Spec.Url);
        OnScreen("{{baseUrl}}/post", screen);
        OnScreen("TermRest Demo / Basics/POST JSON", screen);
    }

    [Fact]
    public void Typing_in_the_url_updates_params_and_ctrl_s_saves_postman_json()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);

        ui.Click("{{baseUrl}}/get?foo=bar&source=termrest");
        ui.App.InjectKey(Key.End);
        ui.Type("&page=2");
        var screen = ui.Screen();
        Assert.Equal("{{baseUrl}}/get?foo=bar&source=termrest&page=2", ui.Window.Editor.Spec.Url);
        Assert.Contains(ui.Window.Editor.Spec.QueryParams, q => q.Key == "page" && q.Value == "2");
        OnScreen("page", screen);
        OnScreen("●", screen);

        ui.App.InjectKey(Key.S.WithCtrl);
        ui.App.End(token!);

        var saved = PostmanCollection.Load(Path.Combine(_directory, "TermRest Demo.postman_collection.json"));
        var spec = saved.FindRequest("Basics/GET with query")!.ReadSpec();
        Assert.Equal("{{baseUrl}}/get?foo=bar&source=termrest&page=2", spec.Url);
        Assert.Equal(["foo", "source", "debug", "page"], spec.QueryParams.Select(q => q.Key));
        Assert.False(spec.QueryParams[2].Enabled);
        Assert.Contains("Saved", ui.Window.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Importing_the_open_collection_again_reloads_it_and_the_editor_stays_live()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        var path = Path.Combine(_directory, "TermRest Demo.postman_collection.json");
        var before = Assert.Single(ui.Workspace.Collections);

        ui.Window.ImportCollectionFrom(path);

        Assert.NotSame(before, Assert.Single(ui.Workspace.Collections));
        Assert.Contains("Reloaded 'TermRest Demo' from disk", ui.Window.StatusMessage, StringComparison.Ordinal);
        ui.Click("{{baseUrl}}/get?foo=bar&source=termrest");
        ui.App.InjectKey(Key.End);
        ui.Type("&after=reload");
        ui.App.InjectKey(Key.S.WithCtrl);
        ui.App.End(token!);

        var saved = PostmanCollection.Load(path);
        Assert.EndsWith("&after=reload", saved.FindRequest("Basics/GET with query")!.ReadSpec().Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task F5_sends_the_request_and_shows_the_response()
    {
        await using var server = await LocalEchoServer.StartAsync();
        using var ui = new Harness(_directory, runner => runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo");
        var screen = "";
        var deadline = DateTime.UtcNow.AddSeconds(20);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            ui.App.InjectKey(Key.F5);
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            screen = ui.Screen();
            if (screen.Contains("200 OK", StringComparison.Ordinal) || DateTime.UtcNow > deadline)
            {
                ui.App.RequestStop();
                return false;
            }

            return true;
        });

        ui.App.Run(ui.Window);

        OnScreen("200 OK", screen);
        Assert.Contains("\"path\": \"/echo/get\"", screen, StringComparison.Ordinal);
        Assert.Contains("\"query\": \"?foo=bar&source=termrest\"", screen, StringComparison.Ordinal);
        Assert.Equal(200, ui.Window.ResponsePane.Exchange?.Response?.StatusCode);
    }

    [Fact]
    public void Environment_menu_switches_the_active_environment()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);

        ui.App.InjectKey(Key.F7);
        var menu = ui.Screen();
        OnScreen("No environment", menu);
        ui.Click("  Postman Echo");
        var screen = ui.Screen();
        ui.App.End(token!);

        OnScreen("Env: Postman Echo", screen);
        Assert.Equal("Postman Echo", ui.Workspace.ActiveEnvironment?.Name);
        Assert.Equal("Postman Echo", ApiWorkspace.Open(_directory).ActiveEnvironment?.Name);
    }

    [Fact]
    public void Right_click_on_the_tree_opens_a_context_menu()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);

        ui.App.InjectSequence(InputInjectionExtensions.RightButtonClick(ui.Find("POST POST JSON")));
        var screen = ui.Screen();
        ui.App.End(token!);

        OnScreen("Duplicate", screen);
        OnScreen("Copy as cURL", screen);
        OnScreen("New request", screen);
    }

    [Fact]
    public async Task Mutual_tls_request_from_the_ui_presents_the_registered_client_certificate()
    {
        using var pki = TermRest.Core.Tests.Infrastructure.TestPki.Create();
        await using var server = await TermRest.Core.Tests.Infrastructure.EchoServer.StartAsync(pki.Server, requireClientCertificate: true);
        File.Copy(pki.ClientPfxPath, Path.Combine(_directory, "client.pfx"));
        File.Copy(pki.CaPemPath, Path.Combine(_directory, "ca.pem"));
        var setup = ApiWorkspace.Open(_directory);
        setup.Certificates.Entries.Add(new TermRest.Core.Certificates.CertificateEntry
        {
            Name = "test client",
            Matches = ["https://localhost/*"],
            Pfx = new TermRest.Core.Certificates.FileSource { Src = "client.pfx" },
            Passphrase = "env:TERMREST_UI_TEST_PFX",
        });
        setup.SaveCertificates();
        setup.Settings.CaBundle = "ca.pem";
        setup.SaveSettings();
        Environment.SetEnvironmentVariable("TERMREST_UI_TEST_PFX", TermRest.Core.Tests.Infrastructure.TestPki.Passphrase);

        using var ui = new Harness(_directory, runner => runner.LocalVariables["baseUrl"] = server.BaseUri.ToString().TrimEnd('/') + "/echo");
        var screen = "";
        var clickedTls = false;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            ui.App.InjectKey(Key.F5);
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            screen = ui.Screen();
            if (DateTime.UtcNow > deadline)
            {
                ui.App.RequestStop();
                return false;
            }

            if (!clickedTls && screen.Contains("200 OK", StringComparison.Ordinal))
            {
                var tab = ui.Find("│TLS│");
                ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(tab.X + 2, tab.Y)));
                clickedTls = true;
                return true;
            }

            if (clickedTls)
            {
                ui.App.RequestStop();
                return false;
            }

            return true;
        });

        ui.App.Run(ui.Window);

        OnScreen("200 OK", screen);
        OnScreen("Client cert sent  CN=TermRest Test Client", screen);
        Assert.Contains("trusted through the workspace CA bundle", ui.Window.ResponsePane.Exchange!.Tls.Single().Describe(), StringComparison.Ordinal);
        var exchange = ui.Window.ResponsePane.Exchange!;
        Assert.Equal(pki.Client.Thumbprint, exchange.Tls.Single().ClientCertificate?.Thumbprint);
        Assert.Contains(pki.Client.Thumbprint, System.Text.Encoding.UTF8.GetString(exchange.Response!.Body), StringComparison.Ordinal);
    }

    private static void OnScreen(string expected, string screen)
    {
        if (!screen.Contains(expected, StringComparison.Ordinal))
        {
            throw new Xunit.Sdk.XunitException($"'{expected}' is not on screen:\n{screen}");
        }
    }

    [Fact]
    public async Task History_pane_lists_sent_requests_and_reopens_them()
    {
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.ConfigureHistory(state);
        });
        var deadline = DateTime.UtcNow.AddSeconds(20);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            ui.App.InjectKey(Key.F5);
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            if (ui.Screen().Contains("200 OK", StringComparison.Ordinal) || DateTime.UtcNow > deadline)
            {
                ui.App.RequestStop();
                return false;
            }

            return true;
        });
        ui.App.Run(ui.Window);

        var token = ui.App.Begin(ui.Window);
        ui.Click("POST POST JSON");
        ui.App.InjectKey(Key.F9);
        var screen = ui.Screen();
        Assert.True(ui.Window.ShowingHistory);
        OnScreen("History (1)", screen);
        Assert.Equal("{{baseUrl}}/post", ui.Window.Editor.Spec.Url); // showing the pane does not change the request

        var row = Assert.Single(ui.Window.HistoryPane.Rows);
        Assert.Contains("200 GET    /echo/get?foo=bar&source=termrest", row, StringComparison.Ordinal);
        ui.Click("200 GET    /echo/get"); // the sidebar cuts the row off
        screen = ui.Screen();
        Assert.Equal("{{baseUrl}}/get?foo=bar&source=termrest", ui.Window.Editor.Spec.Url);
        Assert.Equal(200, ui.Window.ResponsePane.Exchange?.Response?.StatusCode);
        OnScreen("Request (from history ·", screen);
        OnScreen("\"path\": \"/echo/get\"", screen);

        ui.App.InjectKey(Key.Delete);
        screen = ui.Screen();
        ui.App.InjectKey(Key.F9);
        var back = ui.Screen();
        ui.App.End(token!);

        OnScreen("History (0)", screen);
        Assert.Empty(ui.Runner.History!.Load());
        OnScreen("Collections", back);
        Assert.False(ui.Window.ShowingHistory);
    }

    [Fact]
    public void Pasting_a_curl_command_into_the_url_bar_imports_it()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);

        ui.Click("{{baseUrl}}/get?foo=bar&source=termrest");
        // Terminals deliver the line breaks of a bracketed paste as CR.
        ui.App.RaisePasteEvent("curl -X POST 'https://api.example.com/items?x=1' \\\r  -H 'Content-Type: application/json' \\\r  -H 'X-Trace: 7' \\\r  --data-raw '{\"a\":1}'");
        var screen = ui.Screen();
        var spec = ui.Window.Editor.Spec;
        var modified = ui.Window.Editor.IsModified;
        var status = ui.Window.StatusMessage;
        ui.App.InjectKey(Key.S.WithCtrl);
        ui.App.End(token!);

        Assert.Equal(("POST", "https://api.example.com/items?x=1"), (spec.Method, spec.Url));
        Assert.Equal(["Content-Type: application/json", "X-Trace: 7"], spec.Headers.Select(h => $"{h.Key}: {h.Value}"));
        Assert.Equal((BodyMode.Raw, "{\"a\":1}"), (spec.Body.Mode, spec.Body.Raw));
        Assert.True(modified);
        Assert.Equal("Imported from cURL: POST https://api.example.com/items?x=1. Ctrl+S saves it.", status);
        OnScreen("Request — TermRest Demo / Basics/GET with query ●", screen);
        OnScreen("POST     ▼ https://api.example.com/items?x=1 ", screen);

        var saved = PostmanCollection.Load(Path.Combine(_directory, "TermRest Demo.postman_collection.json")).FindRequest("Basics/GET with query")!.ReadSpec();
        Assert.Equal(("POST", "https://api.example.com/items?x=1", "{\"a\":1}"), (saved.Method, saved.Url, saved.Body.Raw));
    }

    [Fact]
    public void Enter_on_a_curl_command_in_the_url_bar_imports_it_instead_of_sending()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);

        ui.Click("{{baseUrl}}/get?foo=bar&source=termrest");
        ui.App.InjectKey(Key.Home);
        ui.Type("curl -X DELETE ");
        ui.App.InjectKey(Key.Enter);
        var spec = ui.Window.Editor.Spec;
        var status = ui.Window.StatusMessage;
        ui.App.InjectKey(Key.S.WithCtrl);
        ui.App.End(token!);

        Assert.Equal(("DELETE", "{{baseUrl}}/get?foo=bar&source=termrest"), (spec.Method, spec.Url));
        Assert.StartsWith("Imported from cURL: DELETE {{baseUrl}}/get", status, StringComparison.Ordinal);
        Assert.Null(ui.Window.ResponsePane.Exchange);
    }

    [Fact]
    public void Imported_curl_commands_open_as_new_requests()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);

        ui.Window.ImportCurl("curl https://api.example.com/v1/users/42 -H 'Accept: application/json' -u ada:secret", intoCurrent: false);
        var screen = ui.Screen();
        ui.App.End(token!);

        OnScreen("Request (imported) ●", screen);
        OnScreen("https://api.example.com/v1/users/42", screen);
        Assert.Equal("basic", ui.Window.Editor.Spec.Auth?.Type);
        Assert.False(ui.Workspace.HasUnsavedChanges);
    }

    [Fact]
    public void V3_yaml_collections_are_edited_and_saved_in_place()
    {
        var root = V3Fixture.Create();
        using var ui = new Harness(root);
        var token = ui.App.Begin(ui.Window);
        var screen = ui.Screen();
        OnScreen("Widget API", screen);
        OnScreen("Widgets", screen);
        OnScreen("Env: none", screen);

        ui.Click("{{baseUrl}}/health");
        ui.App.InjectKey(Key.End);
        ui.Type("?probe=1");
        ui.App.InjectKey(Key.S.WithCtrl);
        ui.App.End(token!);

        var file = File.ReadAllText(Path.Combine(V3Fixture.CollectionPath(root), "Health.request.yaml"));
        Assert.Equal("$kind: http-request\nurl: '{{baseUrl}}/health?probe=1'\nmethod: GET\nqueryParams:\n  - key: probe\n    value: '1'\nauth:\n  type: noauth\norder: 1000\n", file);
        Assert.Contains("Saved Widget API", ui.Window.StatusMessage, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
