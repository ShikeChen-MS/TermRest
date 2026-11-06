using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Recording;
using TermRest.Core.Scripting;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using TermRest.Tui.Tests.Infrastructure;
using TermRest.Tui.Theming;
using Xunit;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Color = Terminal.Gui.Drawing.Color;

namespace TermRest.Tui.Tests;

/// <summary>
/// Drives the real main window headlessly through Terminal.Gui's ANSI driver: renders to an in-memory screen,
/// injects mouse and keyboard input, and asserts on what a user would see.
/// </summary>
[Collection("TUI")]
public sealed class MainWindowTests : IDisposable
{
    private readonly string _directory;
    private readonly string _recordingTemp;
    private readonly string? _previousRecordingTemp = Environment.GetEnvironmentVariable(RequestRecorder.TempDirectoryVariable);

    public MainWindowTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "termrest-ui-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_directory);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "samples"), "*.json"))
        {
            File.Copy(file, Path.Combine(_directory, Path.GetFileName(file)));
        }

        // Recordings (and the check for unsaved ones at startup) use a folder of their own.
        _recordingTemp = _directory + "-recordings";
        Environment.SetEnvironmentVariable(RequestRecorder.TempDirectoryVariable, _recordingTemp);
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

    [Fact]
    public void Switching_the_theme_recolors_the_open_window_and_the_request_methods()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        try
        {
            ThemeCatalog.Apply(ThemeCatalog.Find(ThemeCatalog.BuiltIn, "Dracula")!);
            var post = ui.Find("POST POST JSON");
            var (method, name) = (CellAt(ui, post.X, post.Y), CellAt(ui, post.X + 5, post.Y));
            var menuBar = CellAt(ui, 149, 0);
            ui.App.End(token!);

            Assert.Equal(Color.Parse("#ffb86c"), method.Foreground);
            Assert.Equal(name.Background, method.Background);
            Assert.NotEqual(method.Foreground, name.Foreground);
            Assert.Equal(Color.Parse("#21222c"), menuBar.Background);
        }
        finally
        {
            ThemeCatalog.Apply(ThemeCatalog.BuiltIn[0]);
        }
    }

    [Fact]
    public void Color_theme_dialog_previews_themes_live_and_saves_the_choice()
    {
        var userDirectory = Path.Combine(_directory, "user-config");
        var workspaceThemes = ThemeCatalog.WorkspaceThemesDirectory(_directory);
        Directory.CreateDirectory(workspaceThemes);
        File.WriteAllText(Path.Combine(workspaceThemes, "team.json"), """{ "name": "Team Colors", "base": "Nord", "palette": { "bar": "#5c0000" } }""");
        using var ui = new Harness(_directory);
        ColorTheme? cancelled = null, chosen = null;
        string dialogScreen = "", previewedBeforeCancel = "", afterCancel = "", previewed = "";
        try
        {
            ui.App.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
            {
                ui.App.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
                {
                    ui.Click("Dracula");
                    previewedBeforeCancel = ThemeCatalog.Current.Name;
                    ui.Click("Cancel");
                    return false;
                });
                cancelled = Dialogs.ThemeDialog.Show(ui.App, ui.Workspace, userDirectory);
                afterCancel = ThemeCatalog.Current.Name;

                ui.App.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
                {
                    ui.App.InjectKey(Key.End); // custom themes come last
                    previewed = ThemeCatalog.Current.Name;
                    dialogScreen = ui.Screen();
                    ui.Click("Use theme");
                    return false;
                });
                chosen = Dialogs.ThemeDialog.Show(ui.App, ui.Workspace, userDirectory);
                ui.App.RequestStop();
                return false;
            });
            ui.App.AddTimeout(TimeSpan.FromSeconds(20), () =>
            {
                ui.App.RequestStop(); // never hang the test run when a click misses
                return true;
            });

            ui.App.Run(ui.Window);

            Assert.Equal(("Dracula", ThemeCatalog.DefaultName), (previewedBeforeCancel, afterCancel));
            Assert.Null(cancelled);
            OnScreen("Themes (* custom)", dialogScreen);
            OnScreen("TurboPascal 5", dialogScreen);
            OnScreen("Team Colors *", dialogScreen);
            OnScreen("Custom theme based on Nord", dialogScreen);
            OnScreen("200 OK", dialogScreen);
            OnScreen("404 Not Found", dialogScreen);
            OnScreen("500 Internal Server Error", dialogScreen);
            OnScreen("Use only in this workspace", dialogScreen);
            OnScreen("Custom themes:", dialogScreen);
            var buttons = dialogScreen.Split('\n').Single(l => l.Contains("Reload theme files", StringComparison.Ordinal));
            Assert.DoesNotContain("user-config", buttons, StringComparison.Ordinal); // a long folder path must not run into the buttons
            Assert.Equal("Team Colors", previewed);
            Assert.Equal("Team Colors", chosen?.Name);
            Assert.Equal("Team Colors", ThemeCatalog.Current.Name);
            Assert.Equal("Team Colors", UserSettings.Load(userDirectory).Theme);
            Assert.Null(ApiWorkspace.ReadSettings(_directory).Theme);
        }
        finally
        {
            ThemeCatalog.Apply(ThemeCatalog.BuiltIn[0]);
        }
    }

    [Fact]
    public async Task Record_button_records_the_requests_sent_and_stop_asks_where_to_save_them()
    {
        await using var server = await LocalEchoServer.StartAsync();
        using var ui = new Harness(_directory, runner => runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo");
        string idle = "", started = "", button = "", responseTitle = "", recorded = "", dialog = "", confirmation = "", stopped = "";
        Attribute idleDot = default, badge = default, menuBar = default;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            idle = ui.Screen();
            var record = ui.Find("● Record F12");
            idleDot = CellAt(ui, record.X, record.Y);
            menuBar = CellAt(ui, 1, 0);
            ui.Click("● Record F12");
            started = ui.Screen();
            var rec = ui.Find("REC 00:0");
            badge = CellAt(ui, rec.X, rec.Y);
            ui.App.InjectKey(Key.F5);
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            if (!ui.Window.RecordButton.Contains("1 request", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            {
                return true; // the request is still on its way
            }

            button = ui.Window.RecordButton;
            responseTitle = ui.Window.ResponseTitle;
            recorded = ui.Screen();
            SaveWhenStopDialogShows(ui, screen => dialog = screen, screen => confirmation = screen);
            ui.Click("Stop F12"); // stops: the dialog runs until saved
            stopped = ui.Screen();
            ui.App.RequestStop();
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromSeconds(30), () =>
        {
            ui.Runner.Recorder?.Discard(); // never hang the test run
            ui.Runner.Recorder = null;
            ui.App.RequestStop();
            return true;
        });

        ui.App.Run(ui.Window);

        // Idle: a button at the right end of the menu bar, with a red dot.
        Assert.Equal(0, At(idle, "● Record F12").Y);
        Assert.True(At(idle, "● Record F12").X > At(idle, "Help").X);
        var red = ThemeCatalog.Current.Palette.Error is var error && !error.Equals(Color.None) && !error.Equals(menuBar.Background) ? error : Color.Red;
        Assert.Equal(red, idleDot.Foreground);

        // Recording: a red badge with the elapsed time and the number of requests recorded.
        OnScreen("● REC 00:0", started);
        OnScreen("0 requests · Stop F12", started);
        Assert.Equal(red, badge.Background);
        Assert.Contains(" REC 00:", button, StringComparison.Ordinal);
        Assert.EndsWith("· 1 request · Stop F12  ", button, StringComparison.Ordinal);
        Assert.Equal("Response · ● recorded #1", responseTitle);
        OnScreen("Response · ● recorded #1", recorded);

        OnScreen("Stop recording", dialog);
        OnScreen("1 request recorded since", dialog);
        OnScreen("HAR 1.2 format", dialog);
        OnScreen("⟦ Keep recording ⟧", dialog);
        var file = Assert.Single(Directory.GetFiles(_directory, "termrest-recording-*.har"));
        OnScreen("Recording saved", confirmation);
        OnScreen(Path.GetFileName(file), confirmation);
        var entry = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!["log"]!["entries"]![0]!;
        Assert.Equal(server.BaseUrl + "/echo/get?foo=bar&source=termrest", entry["request"]!["url"]!.GetValue<string>());
        Assert.Equal(200, entry["response"]!["status"]!.GetValue<int>());
        Assert.Equal(("TermRest Demo", "Basics/GET with query"), (entry["_termrest"]!["collection"]!.GetValue<string>(), entry["_termrest"]!["request"]!.GetValue<string>()));
        Assert.Null(ui.Runner.Recorder);
        Assert.Equal(" ● Record F12 ", ui.Window.RecordButton);
        Assert.Equal("Response", ui.Window.ResponseTitle);
        Assert.StartsWith($"✓ Recording saved: {file} (1 request,", ui.Window.StatusMessage, StringComparison.Ordinal);
        OnScreen("● Record F12", stopped);
        Assert.Empty(Directory.Exists(_recordingTemp) ? Directory.GetDirectories(_recordingTemp) : []);
    }

    /// <summary>
    /// Drives the stop-recording dialog that a click is about to open (the click returns only once it is closed):
    /// saves to the suggested file, then closes the confirmation. Each step waits until its dialog is on screen;
    /// a fixed delay is not enough, as a slow step can make the next one due before the dialog has closed.
    /// </summary>
    private static void SaveWhenStopDialogShows(Harness ui, Action<string> dialogShown, Action<string> saved)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            var screen = ui.Screen();
            if (!screen.Contains("Stop recording", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            {
                return true;
            }

            dialogShown(screen);
            ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                var confirmation = ui.Screen();
                if (!confirmation.Contains("Recording saved", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
                {
                    return true;
                }

                saved(confirmation);
                ui.Click("► OK ◄");
                return false;
            });
            ui.Click("► Save ◄");
            return false;
        });
    }

    [Fact]
    public async Task A_json_body_file_must_be_valid_json_and_recordings_keep_only_its_path()
    {
        await using var server = await LocalEchoServer.StartAsync();
        using var ui = new Harness(_directory, runner => runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo");
        var good = Path.Combine(_directory, "order.json");
        var bad = Path.Combine(_directory, "broken.json");
        File.WriteAllText(good, "{\"item\": \"Only-In-The-File\"}");
        File.WriteAllText(bad, "{\"item\": book}");
        string fileMode = "", rejected = "", afterRejection = "", accepted = "", recording = "", sent = "", dialog = "";
        var step = 0;
        var deadline = DateTime.UtcNow.AddSeconds(25);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            if (DateTime.UtcNow > deadline)
            {
                ui.Runner.Recorder?.Discard(); // never hang the test run
                ui.Runner.Recorder = null;
                ui.App.RequestStop();
                return false;
            }

            var screen = ui.Screen();
            switch (step)
            {
                case 0:
                    ui.Click("POST POST JSON");
                    ui.Click("Body");
                    ui.Click("○ file");
                    ui.Click("○ JSON");
                    fileMode = ui.Screen();
                    ui.Window.ChooseBodyFile(bad);
                    step = 1;
                    break;
                case 1 when screen.Contains("File not used", StringComparison.Ordinal):
                    rejected = screen;
                    step = 2;
                    ui.Click("► OK ◄");
                    break;
                case 2:
                    afterRejection = ui.Window.BodyFileStatus;
                    ui.Window.ChooseBodyFile(good);
                    step = 3;
                    break;
                case 3 when ui.Window.BodyFileStatus.StartsWith('✓'):
                    accepted = ui.Window.BodyFileStatus;
                    ui.App.InjectKey(Key.F12);
                    recording = ui.Screen();
                    ui.App.InjectKey(Key.F5);
                    step = 4;
                    break;
                case 4 when ui.Window.StatusMessage.Contains("recorded with", StringComparison.Ordinal):
                    sent = ui.Window.StatusMessage;
                    step = 5;
                    SaveWhenStopDialogShows(ui, screen => dialog = screen, _ => { });
                    ui.Click("Stop F12"); // returns once the dialog is closed
                    break;
                case 5:
                    ui.App.InjectKey(Key.S.WithCtrl); // the edited request is saved, so quitting does not ask
                    ui.App.RequestStop();
                    return false;
            }

            return true;
        });

        ui.App.Run(ui.Window);

        OnScreen("◉ JSON", fileMode);
        OnScreen("Read from disk each time the request is sent, never loaded into memory.", fileMode);
        OnScreen("File not used", rejected);
        OnScreen("broken.json is not valid JSON: 'b' is an invalid start of a value (line 1, column 10).", rejected);
        Assert.StartsWith("Choose the file to send", afterRejection, StringComparison.Ordinal); // the bad file was not taken
        Assert.StartsWith("✓ order.json · ", accepted, StringComparison.Ordinal);
        Assert.EndsWith(" · checked as JSON", accepted, StringComparison.Ordinal);
        OnScreen("● Recording is on: it keeps this file's path, not its content.", recording);
        Assert.EndsWith("● recorded with the file's path, not its content", sent, StringComparison.Ordinal);
        OnScreen("1 of them sent a file: the recording keeps the file's path, not its content.", dialog);
        var har = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "termrest-recording-*.har"))))!;
        var request = har["log"]!["entries"]![0]!["request"]!;
        Assert.Equal(good, request["postData"]!["_file"]!["path"]!.GetValue<string>());
        Assert.Equal("application/json", request["postData"]!["mimeType"]!.GetValue<string>());
        Assert.DoesNotContain("Only-In-The-File", request.ToJsonString(), StringComparison.Ordinal);
        var saved = PostmanCollection.Load(Path.Combine(_directory, "TermRest Demo.postman_collection.json")).FindRequest("Basics/POST JSON")!.ReadSpec().Body;
        Assert.Equal((BodyMode.File, good, "json"), (saved.Mode, saved.FileSrc, saved.FileLanguage));
    }

    [Fact]
    public async Task Python_scripts_run_when_sending_and_the_response_shows_their_tests()
    {
        var python = TestPython.Require();
        await using var server = await LocalEchoServer.StartAsync();
        var setup = PostmanCollection.Load(Path.Combine(_directory, "TermRest Demo.postman_collection.json"));
        setup.SetScripts(setup.RootNode, new ScriptSet("tr.request.url += '&from=collection'", "", ScriptLanguage.Python));
        var node = setup.FindRequest("Basics/GET with query")!;
        var spec = node.ReadSpec();
        spec.ScriptLanguage = ScriptLanguage.Python;
        spec.TestScript = "@tr.test('the collection script added a parameter')\ndef _():\n    assert 'from=collection' in tr.response.json()['query']\nprint('checked', tr.response.status)";
        setup.UpdateRequest(node, spec);
        setup.Save();
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.SetPythonInterpreter(python);
        });
        var screen = "";
        var deadline = DateTime.UtcNow.AddSeconds(30);
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
        OnScreen("tests 1/1 passed", screen);
        var scripts = ui.Window.ResponsePane.ScriptsText;
        Assert.Contains("✓ the collection script added a parameter", scripts, StringComparison.Ordinal);
        Assert.Contains("[test · request 'Basics/GET with query'] checked 200", scripts, StringComparison.Ordinal);
        Assert.Contains("from=collection", System.Text.Encoding.UTF8.GetString(ui.Window.ResponsePane.Exchange!.Response!.Body), StringComparison.Ordinal);
        Assert.DoesNotContain("from=collection", ui.Window.Editor.Spec.Url, StringComparison.Ordinal); // scripts change what is sent, not the request
    }

    [Fact]
    public void The_scripts_tab_edits_a_requests_scripts_and_their_language()
    {
        var path = Path.Combine(_directory, "TermRest Demo.postman_collection.json");
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);

        ui.Click("POST POST JSON");
        ui.Click("Scripts");
        var tab = ui.Screen();
        var label = ui.Find("Pre-request (before sending)");
        ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(label.X + 2, label.Y + 1)));
        ui.Type("print(1)");
        var typed = ui.Window.Editor.Spec.Clone();
        ui.App.InjectKey(Key.S.WithCtrl);
        var savedPython = PostmanCollection.Load(path).FindRequest("Basics/POST JSON")!.ReadSpec();
        ui.Click("○ JavaScript (Postman)");
        var javaScript = ui.Screen();
        ui.App.InjectKey(Key.S.WithCtrl);
        ui.App.End(token!);

        OnScreen("◉ Python", tab);
        OnScreen("Run by your Python (Tools › Scripts…)", tab);
        Assert.Equal(("print(1)", ScriptLanguage.Python), (typed.PreRequestScript, typed.ScriptLanguage));
        Assert.Equal(("print(1)", ScriptLanguage.Python), (savedPython.PreRequestScript, savedPython.ScriptLanguage));
        OnScreen("Postman's JavaScript is kept in the collection but not run", javaScript);
        Assert.Equal(ScriptLanguage.JavaScript, PostmanCollection.Load(path).FindRequest("Basics/POST JSON")!.ReadSpec().ScriptLanguage);
        Assert.Contains("text/javascript", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Collection_scripts_are_edited_from_the_tree()
    {
        using var ui = new Harness(_directory);
        string menu = "", dialog = "";
        var deadline = DateTime.UtcNow.AddSeconds(15);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            // The collection's row in the tree (its name is also in the request pane's title).
            var row = ui.Screen().Split('\n').Select((line, y) => new Point(line.IndexOf("TermRest Demo", StringComparison.Ordinal), y)).First(p => p.X is >= 0 and < 40);
            ui.App.InjectSequence(InputInjectionExtensions.RightButtonClick(row));
            menu = ui.Screen();

            // The dialog opens inside the click below, which returns once it is closed: drive it from another timer.
            ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                var screen = ui.Screen();
                if (!screen.Contains("Scripts of collection 'TermRest Demo'", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
                {
                    return true;
                }

                dialog = screen;
                var label = ui.Find("Pre-request (before sending)");
                ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(label.X + 2, label.Y + 1)));
                ui.Type("tr.variables.set('a', 1)");
                ui.Click("► OK ◄");
                return false;
            });
            ui.Click("Scripts…");
            ui.App.InjectKey(Key.S.WithCtrl);
            ui.App.RequestStop();
            return false;
        });

        ui.App.Run(ui.Window);

        OnScreen("Scripts…", menu);
        OnScreen("They run for every request inside", dialog);
        var saved = PostmanCollection.Load(Path.Combine(_directory, "TermRest Demo.postman_collection.json"));
        Assert.Equal(new ScriptSet("tr.variables.set('a', 1)", "", ScriptLanguage.Python), saved.GetScripts(saved.RootNode));
    }

    [Fact]
    public void Quitting_while_recording_asks_about_the_recording_first()
    {
        using var ui = new Harness(_directory);
        string dialog = "";
        var stillRecording = false;
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            ui.App.InjectKey(Key.F12);
            ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                dialog = ui.Screen();
                ui.Click("⟦ Cancel ⟧");
                return false;
            });
            ui.App.InjectKey(Key.Q.WithCtrl); // asks; Cancel neither quits nor stops recording
            stillRecording = ui.Runner.Recorder is { IsRecording: true };
            ui.Runner.Recorder?.Discard();
            ui.Runner.Recorder = null;
            ui.App.RequestStop();
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromSeconds(20), () =>
        {
            ui.Runner.Recorder?.Discard();
            ui.Runner.Recorder = null;
            ui.App.RequestStop();
            return true;
        });

        ui.App.Run(ui.Window);

        OnScreen("Save the recording first?", dialog);
        OnScreen("Discard", dialog);
        Assert.True(stillRecording);
    }

    [Fact]
    public void Keyboard_help_shows_the_shortcuts_in_aligned_columns()
    {
        using var ui = new Harness(_directory);
        var screen = "";
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            ui.Click("Help");
            ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                screen = ui.Screen();
                ui.Click("► OK ◄");
                return false;
            });
            ui.Click("Keyboard & mouse");
            ui.App.RequestStop();
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromSeconds(20), () =>
        {
            ui.App.RequestStop();
            return true;
        });

        ui.App.Run(ui.Window);

        // Each column starts at the same place on every row (a message box would center every line on its own).
        int Column(params string[] texts) => Assert.Single(texts.Select(t => At(screen, t).X).Distinct());
        var keys = Column("F5, Ctrl+Enter", "Esc ", "F2 ", "F8 ", "F9 ");
        var actions = Column("Send (again to cancel)", "Cancel a running request", "Rename tree item", "Client certificates", "Collections ⇄ History");
        var moreKeys = Column("F12  ", "Tab, Shift+Tab", "←, → (tab row)", "Shift+F10", "Ctrl+L");
        var moreActions = Column("Start / stop recording", "Next / previous field", "Previous / next tab", "Focus the URL bar");
        Assert.True(keys < actions && actions < moreKeys && moreKeys < moreActions, screen);
        Assert.Equal(At(screen, "Esc ").Y + 1, At(screen, "Ctrl+S ").Y);
        OnScreen("Right-click the tree or the history for actions", screen);
    }

    [Fact]
    public void Keyboard_help_uses_one_column_on_narrow_screens()
    {
        var wide = MainWindow.KeysHelp(120).Split('\n');
        var narrow = MainWindow.KeysHelp(80).Split('\n');

        Assert.Equal(wide.Length + 8, narrow.Length);
        Assert.Contains("  F12             Start / stop recording", narrow);
        Assert.All(narrow.TakeWhile(l => l.Length > 0), l => Assert.True(l.Length <= 60, l));
        Assert.All(wide, l => Assert.True(l.Length <= 92, l));
    }

    [Fact]
    public void Text_dialogs_keep_lines_left_aligned_and_wrap_long_ones()
    {
        using var ui = new Harness(_directory);
        var screen = "";
        var path = "C:\\" + string.Join('\\', Enumerable.Repeat("folder_name", 20));
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                screen = ui.Screen();
                ui.Click("► OK ◄");
                return false;
            });
            Dialogs.Ui.ShowText(ui.App, "Details", $"Short: 1\nA longer label: 2\nPath: {path}");
            ui.App.RequestStop();
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromSeconds(20), () =>
        {
            ui.App.RequestStop();
            return true;
        });

        ui.App.Run(ui.Window);

        Assert.Equal(At(screen, "Short: 1").X, At(screen, "A longer label: 2").X);
        Assert.Equal(At(screen, "Short: 1").X, At(screen, "Path: C:\\folder_name").X);
        Assert.Equal(20, System.Text.RegularExpressions.Regex.Count(screen, "folder_name")); // wrapped, not cut; underscores kept
    }

    [Fact]
    public void Clicking_a_tab_opens_its_page_and_returns_to_the_control_last_focused_there()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        var paramsPage = ui.Screen();
        var tabs = TabPanels(ui).Single(p => p.Pages[0].Title == "Params");
        ui.Click("Headers");
        var headersPage = Drawn(ui);
        var headersFocused = tabs.Value!.HasFocus;
        var bulkEdit = Descendants(tabs.Value).OfType<Terminal.Gui.Views.Button>().Single(b => b.Text == "Bulk edit");
        bulkEdit.SetFocus();
        ui.Click("Body");
        var bodyPage = Drawn(ui);
        var onlySelectedShown = tabs.Pages.All(page => page.Visible == (page == tabs.Value));
        ui.Click("Headers");
        var focusedOnReturn = ui.App.Navigation!.GetFocused();
        ui.App.End(token!);

        OnScreen("debug", paramsPage); // the disabled query parameter
        Assert.DoesNotContain("application/json", paramsPage, StringComparison.Ordinal);

        // Drawn like Terminal.Gui's tabs: the selected tab opens into its page.
        OnScreen("╭──────╭───────╮────╮────╮───────╮────────╮", headersPage);
        OnScreen("│Params│Headers│Auth│Body│Scripts│Settings│", headersPage);
        OnScreen("├──────╯       ╰────┴────┴───────┴────────┴──", headersPage);
        OnScreen("application/json", headersPage);
        Assert.DoesNotContain("debug", headersPage, StringComparison.Ordinal);
        Assert.True(headersFocused);

        OnScreen("├──────┴───────┴────╯    ╰───────┴────────┴──", bodyPage);
        OnScreen("◉ none  ○ raw", bodyPage);
        Assert.DoesNotContain("application/json", bodyPage, StringComparison.Ordinal);
        Assert.True(onlySelectedShown); // the pages behind are neither laid out nor drawn
        Assert.Same(bulkEdit, focusedOnReturn);
    }

    [Fact]
    public void Arrow_keys_on_the_tab_row_switch_tabs()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        ui.Screen();
        var tabs = TabPanels(ui).Single(p => p.Pages[0].Title == "Params");
        var tabRow = tabs.SubViews.Single(v => !tabs.Pages.Contains(v));
        tabRow.SetFocus();
        var selected = new List<string>();
        foreach (var key in (Key[])[Key.CursorRight, Key.CursorRight, Key.CursorLeft, Key.CursorLeft, Key.CursorLeft, Key.Home, Key.End])
        {
            ui.App.InjectKey(key);
            selected.Add(tabs.Value!.Title);
        }

        var titles = At(Drawn(ui), "│Params│Headers│Auth│Body│Scripts│Settings│");
        var settingsWhileOnTabRow = CellAt(ui, titles.X + 34, titles.Y);
        var paramsWhileOnTabRow = CellAt(ui, titles.X + 1, titles.Y);
        ui.App.InjectKey(Key.CursorDown);
        var inPage = tabs.Value!.HasFocus && !tabRow.HasFocus;
        var screen = Drawn(ui);
        var settingsInPage = CellAt(ui, titles.X + 34, titles.Y);
        ui.App.End(token!);

        Assert.Equal(["Headers", "Auth", "Headers", "Params", "Settings", "Params", "Settings"], selected);
        Assert.NotEqual(paramsWhileOnTabRow, settingsWhileOnTabRow); // the selected tab is highlighted while the tab row has focus
        Assert.Equal(paramsWhileOnTabRow, settingsInPage);
        Assert.True(inPage);
        OnScreen("SSL certificate verification", screen);
    }

    /// <summary>What the main loop would show: draws only what changed (<see cref="Harness.Screen"/> redraws everything).</summary>
    private static string Drawn(Harness ui)
    {
        ui.App.LayoutAndDraw();
        return ui.App.Driver!.ToString() ?? "";
    }

    private static IEnumerable<Terminal.Gui.ViewBase.View> Descendants(Terminal.Gui.ViewBase.View view) =>
        view.SubViews.SelectMany(Descendants).Prepend(view);

    private static IEnumerable<Views.TabPanel> TabPanels(Harness ui) => Descendants(ui.Window).OfType<Views.TabPanel>();

    /// <summary>Position of the first occurrence of <paramref name="text"/> in a captured screen.</summary>
    private static Point At(string screen, string text)
    {
        var lines = screen.Split('\n');
        for (var y = 0; y < lines.Length; y++)
        {
            if (lines[y].IndexOf(text, StringComparison.Ordinal) is var x and >= 0)
            {
                return new Point(x, y);
            }
        }

        throw new Xunit.Sdk.XunitException($"'{text}' is not on screen:\n{screen}");
    }

    private static Attribute CellAt(Harness ui, int x, int y) =>
        ui.App.Driver!.Contents![y, x].Attribute ?? throw new Xunit.Sdk.XunitException($"No colors drawn at {x},{y}.");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RequestRecorder.TempDirectoryVariable, _previousRecordingTemp);
        foreach (var directory in (string[])[_directory, _recordingTemp])
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
