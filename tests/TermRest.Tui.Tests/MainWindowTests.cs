using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using TermRest.Core.Http;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Recording;
using TermRest.Core.Scripting;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using TermRest.Tui.Dialogs;
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
            Runner = new RequestRunner(Workspace) { RequireScriptRuntimes = false }; // as in the app (TuiApp)
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
    public void Ctrl_F_finds_in_the_response_from_any_field_and_F3_goes_to_the_next_match()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        ui.Window.ResponsePane.ShowExchange(new HttpExchange(new PreparedRequest { Method = "GET", Url = "https://api.example.com/x" })
        {
            Response = new ResponseSnapshot { StatusCode = 200, ReasonPhrase = "OK", ContentType = "application/json", Body = "{\"a\":\"xy\",\"b\":\"xy\"}"u8.ToArray() },
        });

        ui.App.InjectKey(Key.L.WithCtrl); // in the URL bar, where Ctrl+F moved the cursor right
        ui.App.InjectKey(Key.F.WithCtrl);
        ui.Type("xy");
        var found = (ui.Window.Editor.Spec.Url, ui.Window.ResponsePane.FindStatus);
        ui.App.InjectKey(Key.F3);
        var next = ui.Window.ResponsePane.FindStatus;
        ui.App.InjectKey(Key.F3.WithShift);
        var previous = ui.Window.ResponsePane.FindStatus;
        ui.App.InjectKey(Key.Esc);
        var screen = ui.Screen();
        ui.App.InjectKey(Key.S.WithCtrl); // nothing left unsaved, whatever the keys did
        ui.App.End(token!);

        Assert.Equal(("{{baseUrl}}/get?foo=bar&source=termrest", "1 of 2"), found);
        Assert.Equal("2 of 2", next);
        Assert.Equal("1 of 2", previous);
        Assert.DoesNotContain("Match case", screen, StringComparison.Ordinal); // Esc closed the find bar
    }

    [Fact]
    public void A_raw_body_is_colored_in_the_language_chosen_for_it()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);

        ui.Click("POST POST JSON");
        var json = ui.Window.Editor.RawBodySyntax;
        ui.Click("Body");
        ui.Click("XML");
        var xml = ui.Window.Editor.RawBodySyntax;
        ui.Click("Text");
        var text = ui.Window.Editor.RawBodySyntax;
        ui.App.InjectKey(Key.S.WithCtrl);
        ui.App.End(token!);

        Assert.Equal(("json", "xml", null), (json, xml, text));
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
    public void Saving_over_a_collection_changed_on_disk_asks_first_and_the_other_files_are_saved_anyway()
    {
        var path = Path.Combine(_directory, "TermRest Demo.postman_collection.json");
        using var ui = new Harness(_directory);
        var environment = ui.Workspace.Environments[0];

        // A git pull changes the collection while it is open.
        var pulled = File.ReadAllText(path).Replace("\"name\": \"TermRest Demo\"", "\"name\": \"TermRest Demo v2\"", StringComparison.Ordinal);
        File.WriteAllText(path, pulled);
        string? notice = null;
        var onDiskAfterCancel = "";
        var environmentSaved = false;

        var screens = Drive(ui, () =>
            {
                ui.Click("{{baseUrl}}/get?foo=bar&source=termrest");
                ui.App.InjectKey(Key.End);
                ui.Type("&page=2");
                environment.SetValue("baseUrl", "http://localhost:5000");
                ui.App.InjectKey(Key.S.WithCtrl); // asks: Enter is Cancel
                notice = ui.Window.StatusMessage;
                onDiskAfterCancel = File.ReadAllText(path);
                environmentSaved = !environment.IsDirty;
                ui.App.InjectKey(Key.S.WithCtrl); // asks, and overwrites
                ui.App.RequestStop();
            },
            ("Changed on disk", () => ui.App.InjectKey(Key.Enter)),
            ("Changed on disk", () => ui.Click("Overwrite")));

        Assert.Equal(2, screens.Count);
        OnScreen("TermRest Demo.postman_collection.json", screens[0]);
        Assert.Equal(pulled, onDiskAfterCancel);
        Assert.Contains("Not saved: TermRest Demo.postman_collection.json changed on disk", notice, StringComparison.Ordinal);
        Assert.True(environmentSaved);
        Assert.Equal("http://localhost:5000", PostmanEnvironment.Load(environment.FilePath!).Values.Single(v => v.Key == "baseUrl").Value);
        var saved = PostmanCollection.Load(path);
        Assert.Equal(("TermRest Demo", "{{baseUrl}}/get?foo=bar&source=termrest&page=2"), (saved.Name, saved.FindRequest("Basics/GET with query")!.ReadSpec().Url));
        Assert.StartsWith("Saved TermRest Demo.postman_collection.json", ui.Window.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_goes_on_after_a_file_fails_for_any_reason_and_logs_the_unexpected_failures()
    {
        var state = Path.Combine(_directory, "state");
        var previousState = Environment.GetEnvironmentVariable(TermRest.Core.History.HistoryStore.StateDirectoryVariable);
        Environment.SetEnvironmentVariable(TermRest.Core.History.HistoryStore.StateDirectoryVariable, state);
        try
        {
            using var ui = new Harness(_directory);
            var written = new List<string>();
            List<string> saved = [];

            var screens = Drive(ui, () =>
                {
                    (saved, _) = Dialogs.Ui.SaveEach(ui.App, _directory,
                    [
                        new("Broken.postman_collection.json", _ => throw new InvalidOperationException("A value could not be written.")),
                        new("Locked.postman_environment.json", _ => throw new UnauthorizedAccessException("Access to Locked is denied.")),
                        new("Dev.postman_environment.json", _ => written.Add("Dev")),
                        new(null, _ => written.Add("settings")),
                    ]);
                    ui.App.RequestStop();
                },
                ("Save failed", () => ui.Click("► OK ◄")));

            OnScreen("Broken.postman_collection.json: A value could not be written.", Assert.Single(screens));
            OnScreen("Access to Locked is denied.", screens[0]);
            Assert.Equal(["Dev", "settings"], written);
            Assert.Equal(["Dev.postman_environment.json"], saved);
            var log = File.ReadAllText(Path.Combine(state, "errors.log"));
            Assert.Contains("System.InvalidOperationException: A value could not be written.", log, StringComparison.Ordinal);
            Assert.DoesNotContain("Locked", log, StringComparison.Ordinal); // a disk or permission problem is no bug
        }
        finally
        {
            Environment.SetEnvironmentVariable(TermRest.Core.History.HistoryStore.StateDirectoryVariable, previousState);
        }
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
    public async Task Enter_on_the_request_open_in_the_tree_goes_to_its_url_bar_and_keeps_its_response()
    {
        await using var server = await LocalEchoServer.StartAsync();
        using var ui = new Harness(_directory, runner => runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo");
        TermRest.Core.Http.HttpExchange? sent = null, shown = null;
        Terminal.Gui.ViewBase.View? focused = null;

        var screens = Drive(ui, () => ui.App.InjectKey(Key.F5),
            ("200 OK", () =>
            {
                sent = ui.Window.ResponsePane.Exchange;
                ui.Click("GET  GET with query"); // the request sent, open already
                ui.App.InjectKey(Key.Enter);
                shown = ui.Window.ResponsePane.Exchange;
                focused = ui.App.Navigation!.GetFocused();
                ui.App.RequestStop();
            }));

        Assert.Single(screens);
        Assert.NotNull(sent);
        Assert.Same(sent, shown);
        Assert.Equal("{{baseUrl}}/get?foo=bar&source=termrest", Assert.IsAssignableFrom<Terminal.Gui.Views.TextField>(focused).Text);
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
    public void Names_are_shown_as_written_and_their_underscores_make_no_hotkeys()
    {
        // Terminal.Gui takes the first _ of a view's text as a hotkey marker: it hides it and the next letter becomes a key.
        var setup = ApiWorkspace.Open(_directory);
        setup.CreateEnvironment("prod");
        setup.ActiveEnvironment = setup.CreateEnvironment("dev_local");
        setup.SaveSettings();
        var demo = setup.Collections[0];
        var request = demo.AddRequest(demo.RootNode, "get_pets").PathInCollection;
        demo.Save();
        using var ui = new Harness(_directory);
        var main = "";
        var afterAltL = "";
        var saved = "";
        Terminal.Gui.ViewBase.View? focused = null, afterAltP = null;

        var screens = Drive(ui, () =>
            {
                ui.Window.ShowRequest(ui.Workspace.Collections[0].FindRequest(request)!);
                ui.Window.ShowHistory(false); // the tree has the focus
                main = ui.Screen();
                focused = ui.App.Navigation!.GetFocused();
                ui.App.InjectKey(Key.P.WithAlt); // the letter after the _ of the request's name, in the pane's title
                afterAltP = ui.App.Navigation.GetFocused();
                ui.App.InjectKey(Key.L.WithAlt); // the letter after the _ of the environment's name, in the status bar
                afterAltL = ui.Screen();
                ui.App.InjectKey(Key.F7);
            },
            ("Manage environments…", () => ui.Click("Manage environments…")),
            ("Active environment:", () =>
            {
                // The label naming the active environment is followed by Set active, which its hotkey would press.
                ui.Click("  prod");
                ui.App.InjectKey(Key.L.WithAlt);
                Descendants(ui.App.TopRunnableView!).OfType<Terminal.Gui.Views.Button>().Single(b => b.Text == "Rename…").SetFocus();
                ui.App.InjectKey(Key.L);
                ui.Click("Close");
            }),
            ("Env:", () =>
            {
                ui.App.Driver!.SetScreenSize(180, 45); // room for the status bar's message
                ui.Window.Editor.FocusUrl();
                ui.Type("x");
                ui.App.InjectKey(Key.S.WithCtrl);
                saved = ui.Screen();
                ui.App.RequestStop();
            }));

        Assert.Equal(3, screens.Count);
        OnScreen("Request — TermRest Demo / get_pets", main);
        OnScreen("Env: dev_local", main);
        Assert.Same(focused, afterAltP);
        Assert.DoesNotContain("Manage environments…", afterAltL, StringComparison.Ordinal);
        OnScreen("● dev_local", screens[0]);
        OnScreen("Active environment: dev_local", screens[1]);
        OnScreen("Env: dev_local", screens[2]);
        OnScreen("Saved TermRest Demo.postman_collection.json", saved);
        Assert.Equal("dev_local", ApiWorkspace.Open(_directory).ActiveEnvironment?.Name);
    }

    [Fact]
    public void A_certificate_s_name_is_shown_as_written_and_makes_no_hotkey_of_its_underscore()
    {
        // The label naming the request's certificate is followed by Manage certificates…, which its hotkey would press.
        var setup = ApiWorkspace.Open(_directory);
        setup.Certificates.Entries.Add(new TermRest.Core.Certificates.CertificateEntry
        {
            Name = "dev_cert",
            Matches = ["https://localhost/*"],
            Pfx = new TermRest.Core.Certificates.FileSource { Src = "missing.pfx" },
        });
        setup.SaveCertificates();
        using var ui = new Harness(_directory, runner => runner.LocalVariables["baseUrl"] = "https://localhost/echo");
        var settings = "";

        var screens = Drive(ui, () =>
            {
                var tab = ui.Find("│Settings│");
                ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(tab.X + 2, tab.Y)));
                settings = ui.Screen();
                ui.App.InjectKey(Key.C.WithAlt); // the letter after the _ of the certificate's name
                ui.App.InjectKey(Key.C);
                ui.App.RequestStop();
            },
            ("Registry:", () => ui.Click("Close")));

        OnScreen("dev_cert", settings);
        Assert.Empty(screens); // the certificates dialog never opened
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
    public async Task A_stream_is_shown_as_it_arrives_and_esc_stops_it_keeping_its_events()
    {
        await using var server = await TermRest.Core.Tests.Infrastructure.EchoServer.StartAsync();
        using var ui = new Harness(_directory, runner => runner.LocalVariables["baseUrl"] = server.Url("events/0/20"));
        var (live, stopped, events) = ("", "", "");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            ui.App.InjectKey(Key.F5);
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            var screen = ui.Screen();
            if (DateTime.UtcNow > deadline)
            {
                (live, stopped, events) = (live.Length > 0 ? live : screen, stopped.Length > 0 ? stopped : screen, screen);
                ui.App.RequestStop();
                return false;
            }

            if (live.Length == 0)
            {
                if (screen.Contains("200 OK · streaming ·", StringComparison.Ordinal) && screen.Contains("data: {\"n\":", StringComparison.Ordinal))
                {
                    live = screen;
                    ui.App.InjectKey(Key.Esc);
                }
            }
            else if (stopped.Length == 0)
            {
                if (screen.Contains("stream stopped", StringComparison.Ordinal))
                {
                    stopped = screen;
                    var tab = ui.Find("│Events (");
                    ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(tab.X + 2, tab.Y)));
                }
            }
            else
            {
                events = screen;
                ui.App.RequestStop();
                return false;
            }

            return true;
        });

        ui.App.Run(ui.Window);

        OnScreen("200 OK · streaming ·", live);
        OnScreen("so far · Esc stops it", live);
        OnScreen("⟦ Stop ⟧", live);
        OnScreen("· stream stopped", stopped);
        OnScreen("Event", events);
        OnScreen("tick", events);
        OnScreen("{\"n\":1}", events);
        var response = ui.Window.ResponsePane.Exchange!.Response!;
        Assert.True(response.Stopped);
        Assert.StartsWith("id: 1\nevent: tick\ndata: {\"n\":1}\n\n", System.Text.Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Esc_once_a_stream_has_arrived_cancels_the_scripts_that_run_on_it()
    {
        var node = TestJavaScript.Require();
        await using var server = await TermRest.Core.Tests.Infrastructure.EchoServer.StartAsync();
        var setup = PostmanCollection.Load(Path.Combine(_directory, "TermRest Demo.postman_collection.json"));
        var request = setup.FindRequest("Basics/GET with query")!;
        var spec = request.ReadSpec();
        spec.Scripts = new ScriptSet("", "await new Promise(resolve => setTimeout(resolve, 30000));", ScriptLanguage.JavaScript);
        setup.UpdateRequest(request, spec);
        setup.Save();
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.Url("events/2/10");
            runner.SetJavaScriptRuntime(node);
        });
        var (running, done) = ("", "");
        var pressed = DateTime.MaxValue;
        var deadline = DateTime.UtcNow.AddSeconds(25);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            ui.App.InjectKey(Key.F5);
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            var screen = ui.Screen();
            if (DateTime.UtcNow > deadline)
            {
                (running, done) = (running.Length > 0 ? running : screen, screen);
                ui.App.RequestStop();
                return false;
            }

            if (running.Length == 0)
            {
                if (screen.Contains(" received …", StringComparison.Ordinal))
                {
                    running = screen;
                    pressed = DateTime.UtcNow;
                    ui.App.InjectKey(Key.Esc);
                }
            }
            else if (!ui.Window.IsSending)
            {
                done = screen;
                ui.App.RequestStop();
                return false;
            }

            return true;
        });

        ui.App.Run(ui.Window);

        OnScreen("(Esc or Send to cancel)", running);
        OnScreen("⟦ Cancel ⟧", running);
        Assert.True(DateTime.UtcNow - pressed < TimeSpan.FromSeconds(15), done);
        var exchange = ui.Window.ResponsePane.Exchange!;
        Assert.Contains("Cancelled while the test scripts ran.", exchange.Scripts!.Notes);
        Assert.Equal(200, exchange.Response!.StatusCode);
    }

    [Fact]
    public async Task A_stream_still_arriving_when_another_request_is_opened_is_stopped_and_kept_in_the_history()
    {
        await using var server = await TermRest.Core.Tests.Infrastructure.EchoServer.StartAsync();
        var state = TestFiles.NewTempDirectory();
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.Url("events/0/20"); // until the client goes
            runner.ConfigureHistory(state);
        });
        var arrived = "";
        var step = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            ui.App.InjectKey(Key.F5);
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            if (DateTime.UtcNow > deadline)
            {
                ui.App.RequestStop();
                return false;
            }

            switch (step)
            {
                case 0 when ui.Window.ResponsePane.StatusText.Contains("· streaming ·", StringComparison.Ordinal):
                    ui.Click("POST POST JSON");
                    step = 1;
                    break;
                case 1 when ui.Runner.History!.Load().Count == 1:
                    arrived = ui.Window.StatusMessage;
                    ui.App.RequestStop();
                    return false;
            }

            return true;
        });

        ui.App.Run(ui.Window);

        Assert.StartsWith("Earlier request: GET localhost → 200", arrived, StringComparison.Ordinal);
        Assert.StartsWith("id: 1\n", System.Text.Encoding.UTF8.GetString(ui.Runner.History!.ReadBody(Assert.Single(ui.Runner.History.Load()))!), StringComparison.Ordinal);
    }

    /// <summary>A screen's text with its lines joined and its box-drawing characters left out, so a check doesn't depend on where a dialog wraps.</summary>
    private static string Unwrapped(string screen) => System.Text.RegularExpressions.Regex.Replace(screen, @"[\s\u2500-\u259F]+", " ");

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
    public async Task The_history_pane_lists_no_more_entries_than_the_history_keeps()
    {
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.ConfigureHistory(state);
            runner.History!.MaxEntries = 2;
        });
        var sent = 0;
        var screen = "";
        var deadline = DateTime.UtcNow.AddSeconds(20);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            if (ui.Window.IsSending && DateTime.UtcNow < deadline)
            {
                return true;
            }

            if (sent == 0)
            {
                ui.App.InjectKey(Key.F9); // listed while the requests are sent
            }

            if (sent < 3 && DateTime.UtcNow < deadline)
            {
                ui.App.InjectKey(Key.F5);
                sent++;
                return true;
            }

            screen = ui.Screen();
            ui.App.RequestStop();
            return false;
        });

        ui.App.Run(ui.Window);

        Assert.Equal(3, sent);
        Assert.StartsWith("GET 127.0.0.1 → 200", ui.Window.StatusMessage, StringComparison.Ordinal);
        Assert.Equal((2, 2), (ui.Window.HistoryPane.Count, ui.Window.HistoryPane.Rows.Count));
        OnScreen("History (2)", screen);
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
        ui.Window.Editor.MarkClean(); // closing would ask about the imported request, which is in no collection
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
    public async Task A_request_that_could_not_be_recorded_is_said_once_and_not_over_every_later_message()
    {
        await using var server = await LocalEchoServer.StartAsync();
        using var ui = new Harness(_directory, runner => runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo");
        const string Failed = "A request could not be recorded: There is not enough space on the disk.";
        string first = "", second = "";

        var screens = Drive(ui, () =>
            {
                ui.App.InjectKey(Key.F12);

                // As when writing a request failed (the disk was full for a moment): the recording goes on.
                typeof(RequestRecorder).GetProperty(nameof(RequestRecorder.LastError))!.SetValue(ui.Runner.Recorder, Failed);
                ui.App.InjectKey(Key.F5);
            },
            ("· 1 request ·", () =>
            {
                first = ui.Window.StatusMessage;
                ui.App.InjectKey(Key.F5);
            }),
            ("· 2 requests ·", () =>
            {
                second = ui.Window.StatusMessage;
                var recorder = ui.Runner.Recorder!;
                ui.Runner.Recorder = null; // not asked about when closing
                recorder.Discard();
                ui.App.RequestStop();
            }));

        Assert.Equal(2, screens.Count);
        Assert.Equal("⚠ " + Failed, first);
        Assert.StartsWith("GET 127.0.0.1 → 200", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Names_added_to_the_settings_while_recording_are_redacted_from_then_on()
    {
        await using var server = await LocalEchoServer.StartAsync();
        var setup = PostmanCollection.Load(Path.Combine(_directory, "TermRest Demo.postman_collection.json"));
        var node = setup.FindRequest("Basics/GET with query")!;
        var spec = node.ReadSpec();
        spec.Headers.Add(new KeyValueEntry { Key = "X-Tenant-Key", Value = "t-123456" });
        setup.UpdateRequest(node, spec);
        setup.Save();

        // Saving the settings turns the history on: it is kept in this test's folder.
        var previousState = Environment.GetEnvironmentVariable(TermRest.Core.History.HistoryStore.StateDirectoryVariable);
        Environment.SetEnvironmentVariable(TermRest.Core.History.HistoryStore.StateDirectoryVariable, Path.Combine(_directory, "state"));
        try
        {
            using var ui = new Harness(_directory, runner => runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo");
            var har = Path.Combine(_directory, "recorded.har");
            var notice = "";

            var screens = Drive(ui, () =>
                {
                    ui.App.InjectKey(Key.F12);
                    ui.Click("Tools");
                    ui.Click("Settings…"); // returns once the dialog is closed
                    notice = ui.Window.StatusMessage;
                    ui.App.InjectKey(Key.F5);
                },
                ("Workspace settings", () =>
                {
                    var label = ui.Find("Recordings redact");
                    ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(label.X + 23, label.Y)));
                    ui.Type("Tenant-Key");
                    ui.Click("► Save ◄");
                }),
                ("· 1 request ·", () =>
                {
                    var recorder = ui.Runner.Recorder!;
                    ui.Runner.Recorder = null; // not asked about when closing
                    recorder.Save(har);
                    ui.App.RequestStop();
                }));

            Assert.Equal(2, screens.Count);
            Assert.Equal("Settings saved. The recording also redacts Tenant-Key from now on; requests already recorded are unchanged.", notice);
            var text = File.ReadAllText(har);
            Assert.DoesNotContain("t-123456", text, StringComparison.Ordinal);
            var log = System.Text.Json.Nodes.JsonNode.Parse(text)!["log"]!;
            Assert.Contains(log["entries"]![0]!["request"]!["headers"]!.AsArray(),
                h => h!["name"]!.GetValue<string>() == "X-Tenant-Key" && h["value"]!.GetValue<string>() == "[REDACTED]");
            Assert.Equal(["Tenant-Key"], log["_termrest"]!["redaction"]!["extraNames"]!.AsArray().Select(n => n!.GetValue<string>()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TermRest.Core.History.HistoryStore.StateDirectoryVariable, previousState);
        }
    }

    [Fact]
    public void Settings_that_could_not_be_saved_apply_for_the_session_and_are_not_said_to_be_saved()
    {
        using var ui = new Harness(_directory);
        var notice = "";

        var screens = Drive(ui, () =>
            {
                // A merge conflict in the file since TermRest read it: saving over it is refused.
                Directory.CreateDirectory(ui.Workspace.ConfigDirectory);
                File.WriteAllText(ui.Workspace.SettingsPath, "{ <<<<<<< HEAD");
                ui.Click("Tools");
                ui.Click("Settings…"); // returns once the dialog is closed
                notice = ui.Window.StatusMessage;
                ui.App.RequestStop();
            },
            ("Workspace settings", () =>
            {
                ui.Click("Verify server certificates");
                ui.Click("► Save ◄");
            }),
            ("Save failed", () => ui.App.InjectKey(Key.Enter)));

        Assert.Equal(2, screens.Count);
        Assert.Equal("Settings applied for this session, not saved.", notice);
        Assert.False(ui.Workspace.Settings.StrictSsl);
    }

    [Fact]
    public void A_proxy_that_came_with_the_workspace_is_used_once_the_settings_are_saved()
    {
        // A cloned repository's settings: its proxy is not used (the system's is), and the UI says so.
        const string proxy = "http://ada:Pr0xy-s3cret@127.0.0.1:1";
        Directory.CreateDirectory(Path.Combine(_directory, ".termrest"));
        File.WriteAllText(Path.Combine(_directory, ".termrest", "settings.json"), $$"""{ "proxy": "{{proxy}}" }""");
        var userDirectory = TestFiles.NewTempDirectory();
        using var ui = new Harness(_directory, runner => runner.ConfigureProxy(userDirectory));
        var notice = ui.Window.StatusMessage;
        var unused = ui.Runner.CreateDefaults().Proxy;
        string info = "", dialog = "";
        var saved = false;

        var screens = Drive(ui, () =>
            {
                ui.Click("Help");
                ui.Click("Workspace info"); // returns once the dialog is closed
                SettingsDialog.Show(ui.App, ui.Workspace, ui.Runner, out saved, userDirectory);
                ui.App.RequestStop();
            },
            ("Proxy: system", () =>
            {
                info = ui.Screen();
                ui.App.InjectKey(Key.Enter); // OK
            }),
            ("Workspace settings", () =>
            {
                dialog = ui.Screen();
                ui.Click("► Save ◄"); // the user's own choice now: allowed for this folder
            }));

        Assert.Equal(2, screens.Count);
        Assert.Contains("The proxy in this workspace's settings (.termrest/settings.json) is not used until you allow it: Tools > Settings > Save", notice, StringComparison.Ordinal);
        Assert.Null(unused);
        // Joined: the dialog wraps long lines where the screen's width has it (Terminal.Gui may set 120 columns again on Windows).
        OnScreen("Proxy: system (OS / HTTPS_PROXY settings), not the workspace's http://ada:***@127.0.0.1:1 until you allow it: Tools > Settings > Save", Unwrapped(info));
        Assert.DoesNotContain("Pr0xy-s3cret", info, StringComparison.Ordinal);
        OnScreen("⚠ Set by the workspace: not used until you save", dialog);
        OnScreen("http://ada:***@127.0.0.1:1", dialog); // its password masked, as everywhere; saved as it was
        Assert.DoesNotContain("Pr0xy-s3cret", dialog, StringComparison.Ordinal);
        Assert.True(saved);
        Assert.Null(ui.Runner.BlockedProxy);
        Assert.Equal(proxy, ui.Runner.CreateDefaults().Proxy);
        Assert.True(UserSettings.Load(userDirectory).IsProxyAllowed(_directory, proxy));
        Assert.DoesNotContain("Pr0xy-s3cret", File.ReadAllText(Path.Combine(userDirectory, "settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Files_from_outside_the_workspace_are_sent_once_allowed_in_the_settings()
    {
        var userDirectory = TestFiles.NewTempDirectory();
        using var ui = new Harness(_directory, runner => runner.ConfigureFiles(userDirectory));
        var only = ui.Runner.CreateDefaults().FilesFolder;
        var saved = false;

        var screens = Drive(ui, () =>
            {
                SettingsDialog.Show(ui.App, ui.Workspace, ui.Runner, out saved, userDirectory);
                ui.App.RequestStop();
            },
            ("Workspace settings", () =>
            {
                ui.Click("Allow files from outside this folder");
                ui.Click("► Save ◄");
            }));

        Assert.Single(screens);
        Assert.Equal(ui.Workspace.RootPath, only);
        Assert.True(saved);
        Assert.Null(ui.Runner.CreateDefaults().FilesFolder);
        Assert.True(UserSettings.Load(userDirectory).AreOutsideFilesAllowed(ui.Workspace.RootPath));
    }

    [Fact]
    public void Settings_written_by_hand_as_requests_read_them_can_be_saved()
    {
        // No redirects followed (0) and a proxy keyword in another case: valid settings that the dialog must not refuse.
        Directory.CreateDirectory(Path.Combine(_directory, ".termrest"));
        File.WriteAllText(Path.Combine(_directory, ".termrest", "settings.json"), """{ "maxRedirects": 0, "proxy": "None" }""");
        using var ui = new Harness(_directory);
        var (changed, saved) = (false, false);

        var screens = Drive(ui, () =>
            {
                changed = SettingsDialog.Show(ui.App, ui.Workspace, ui.Runner, out saved, TestFiles.NewTempDirectory());
                ui.App.RequestStop();
            },
            ("Workspace settings", () => ui.Click("► Save ◄")));

        Assert.Single(screens);
        Assert.True(changed && saved);
        Assert.Equal((0, "none"), (ui.Workspace.Settings.MaxRedirects, ui.Workspace.Settings.Proxy));
    }

    [Fact]
    public void A_proxy_sign_in_typed_in_the_settings_is_kept_in_the_users_private_file_not_the_workspace()
    {
        var userDirectory = TestFiles.NewTempDirectory();
        using var ui = new Harness(_directory, runner => runner.ConfigureProxy(userDirectory));
        var saved = false;

        var screens = Drive(ui, () =>
            {
                SettingsDialog.Show(ui.App, ui.Workspace, ui.Runner, out saved, userDirectory);
                ui.App.RequestStop();
            },
            ("Workspace settings", () =>
            {
                var label = ui.Find("Proxy sign-in");
                ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(label.X + 23, label.Y))); // its user field
                ui.Type("CONTOSO\\ada");
                ui.App.InjectKey(Key.Tab); // the password
                ui.Type("s3cret-pw");
                ui.Click("► Save ◄");
            }));

        Assert.Single(screens);
        Assert.True(saved);
        var credential = ui.Runner.CreateDefaults().ProxyCredential; // the system's proxy, used at once
        Assert.Equal(("ada", "s3cret-pw", "CONTOSO"), (credential?.UserName, credential?.Password, credential?.Domain));
        var file = File.ReadAllText(Path.Combine(userDirectory, ProxyCredentialStore.FileName));
        Assert.Contains("CONTOSO\\\\ada", file, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("s3cret-pw", file, StringComparison.Ordinal);
        }

        var workspaceSettings = ui.Workspace.SettingsPath;
        Assert.False(File.Exists(workspaceSettings) && File.ReadAllText(workspaceSettings).Contains("ada", StringComparison.Ordinal));
    }

    [Fact]
    public void A_proxy_sign_in_left_masked_moves_to_the_new_proxy_and_sign_ins_another_termrest_saved_stay()
    {
        var userDirectory = TestFiles.NewTempDirectory();
        var kept = ProxyCredentialStore.Load(userDirectory);
        kept.Set("system", "CONTOSO\\ada", "s3cret-pw");
        kept.Save(userDirectory);
        using var ui = new Harness(_directory, runner => runner.ConfigureProxy(userDirectory));

        // Another TermRest (another workspace) saves a sign-in for its proxy meanwhile.
        var other = ProxyCredentialStore.Load(userDirectory);
        other.Set("http://other-proxy:8080", "bob", "hunter2-pw");
        other.Save(userDirectory);

        var saved = false;
        var screens = Drive(ui, () =>
            {
                SettingsDialog.Show(ui.App, ui.Workspace, ui.Runner, out saved, userDirectory);
                ui.App.RequestStop();
            },
            ("Workspace settings", () =>
            {
                // A proxy URL in place of the system's; its sign-in left as shown, the password masked.
                var label = ui.Find("Proxy ");
                ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(label.X + 23, label.Y)));
                ui.App.InjectKey(Key.End);
                foreach (var _ in "system")
                {
                    ui.App.InjectKey(Key.Backspace);
                }

                ui.Type("http://new-proxy:3128");
                ui.Click("► Save ◄");
            }));

        Assert.Single(screens);
        Assert.True(saved);
        var credential = ui.Runner.CreateDefaults().ProxyCredential;
        Assert.Equal(("http://new-proxy:3128", "ada", "s3cret-pw", "CONTOSO"),
            (ui.Workspace.Settings.Proxy, credential?.UserName, credential?.Password, credential?.Domain));
        var file = ProxyCredentialStore.Load(userDirectory);
        Assert.Equal("hunter2-pw", file.Find("http://other-proxy:8080")?.Password);
        Assert.Equal("s3cret-pw", file.Find("http://new-proxy:3128")?.Password);
    }

    [Theory]
    [InlineData("http://ada:***@proxy:3128", "http://ada:s3cret@proxy:3128")] // left as shown
    [InlineData("http://ada:***@proxy:8080", "http://ada:s3cret@proxy:8080")] // only the port changed
    [InlineData("http://bob:***@proxy:3128", "http://bob:***@proxy:3128")] // another user: as typed
    [InlineData("http://ada:n3w@proxy:3128", "http://ada:n3w@proxy:3128")] // a new password
    [InlineData("none", "none")]
    public void A_proxy_password_left_masked_in_the_settings_stays_as_it_was(string typed, string saved)
    {
        const string Original = "http://ada:s3cret@proxy:3128";

        Assert.Equal(saved, SettingsDialog.Unmasked(typed, PostmanUrl.MaskCredentials(Original), Original));
        Assert.Equal("http://tok3n@proxy:3128", SettingsDialog.Unmasked("http://***@proxy:3128", "http://***@proxy:3128", "http://tok3n@proxy:3128")); // a user name alone: the credential
    }

    [Fact]
    public void The_settings_and_certificates_dialogs_show_every_control_on_terminals_of_80_and_100_columns()
    {
        using var ui = new Harness(_directory);
        var shown = new List<string>();
        var cut = new List<string>();

        // A dialog does not scroll: each control must be in its content area (its buttons within its border), and it on screen.
        void Check()
        {
            foreach (var columns in (int[])[80, 100])
            {
                ui.App.Driver!.SetScreenSize(columns, 30); // set here: an answer to Terminal.Gui's size query sets 120×30 again now and then
                shown.Add(ui.Screen());
                var dialog = (Terminal.Gui.Views.Dialog)ui.App.TopRunnableView!;
                var frame = dialog.FrameToScreen();
                var content = dialog.ViewportToScreen();
                var controls = Descendants(dialog).Skip(1).Where(v => v.CanFocus && v.Visible).Select(v => (View: v, Area: content))
                    .Concat(dialog.Buttons.Select(b => (View: (Terminal.Gui.ViewBase.View)b, Area: Rectangle.Inflate(frame, -1, -1))));
                cut.AddRange(controls.Where(c => !c.Area.Contains(c.View.ViewportToScreen()))
                    .Select(c => $"{columns} columns, {dialog.Title}: {c.View.GetType().Name} '{c.View.Text}' at {c.View.ViewportToScreen()}, not in {c.Area}"));
                if (!new Rectangle(0, 0, columns, 30).Contains(frame))
                {
                    cut.Add($"{columns} columns: {dialog.Title} at {frame}");
                }
            }

            ui.App.InjectKey(Key.Esc);
        }

        var screens = Drive(ui, () =>
            {
                ui.Click("Tools");
                ui.Click("Settings…"); // returns once the dialog is closed
                ui.App.InjectKey(Key.F8); // the client certificates, likewise
                ui.App.RequestStop();
            },
            ("Workspace settings", Check),
            ("Client certificates", Check));

        Assert.Equal(2, screens.Count);
        Assert.True(cut.Count == 0, string.Join('\n', cut.Concat(shown)));
        OnScreen("system (OS / HTTPS_PROXY settings)", shown[0]); // an underscore in a hint is not a hotkey marker
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
        spec.Scripts = new ScriptSet("", "@tr.test('the collection script added a parameter')\ndef _():\n    assert 'from=collection' in tr.response.json()['query']\nprint('checked', tr.response.status)",
            ScriptLanguage.Python);
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
    public void The_scripts_tab_keeps_a_script_per_language_and_says_which_language_runs()
    {
        var path = Path.Combine(_directory, "TermRest Demo.postman_collection.json");
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        Point OnRunIn(string text)
        {
            var runIn = ui.Find("Run in");
            return new Point(ui.Screen().Split('\n')[runIn.Y].IndexOf(text, runIn.X, StringComparison.Ordinal), runIn.Y);
        }

        ui.Click("POST POST JSON");
        ui.Click("Scripts");
        var tab = ui.Screen();
        var label = ui.Find("Pre-request (before sending)");
        ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(label.X + 2, label.Y + 1)));
        ui.Type("print(1)");
        var python = ui.Screen();
        var typed = ui.Window.Editor.Spec.Clone();

        // Another language shows its own scripts: the Python one is kept.
        ui.Click("○ JavaScript");
        var javaScript = ui.Screen();
        var test = ui.Find("Post-response (tests)");
        ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(test.X + 2, test.Y + 1)));
        ui.Type("pm.test('js', () => {});");
        var mixed = ui.Screen();
        var status = ui.Window.Editor.ScriptsTab.Status;

        // Turning pre-request scripts off leaves the JavaScript test, which runs; then the request chooses PowerShell.
        ui.Click("Pre-request (before sending)");
        var off = ui.Window.Editor.ScriptsTab.Status;
        ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(OnRunIn("PowerShell")));
        var chosen = ui.Screen();
        ui.App.InjectKey(Key.S.WithCtrl);
        ui.App.End(token!);

        OnScreen("◉ Python", tab);
        OnScreen("Run by your Python (Tools › Scripts…)", tab);
        OnScreen("→ runs no scripts", tab);
        OnScreen("◉ Python •", python);
        OnScreen("☑ Pre-request (before sending)", python);
        OnScreen("→ runs Python", python);
        Assert.Equal(new ScriptSet("print(1)", "", ScriptLanguage.Python), typed.Scripts);
        OnScreen("Run by your Node.js or Bun (Tools › Scripts…) with Postman's pm object", javaScript);
        OnScreen("◉ JavaScript •", mixed);
        Assert.Equal("✗ no language has them all", status);
        Assert.Equal("→ runs JavaScript", off);
        OnScreen("☐ Pre-request (before sending)", chosen);
        OnScreen("◉ PowerShell", chosen);
        Assert.Equal("✗ 1 script lacks PowerShell", ui.Window.Editor.ScriptsTab.Status);
        OnScreen("Run in   ○ Auto", chosen); // no line of the hint spills into the next row

        var saved = PostmanCollection.Load(path).FindRequest("Basics/POST JSON")!;
        Assert.Equal(new ScriptSet([new LanguageScripts(ScriptLanguage.Python, "print(1)", ""), new LanguageScripts(ScriptLanguage.JavaScript, "", "pm.test('js', () => {});")]),
            saved.ReadSpec().Scripts);
        Assert.Equal(new ScriptSettings(ScriptLanguage.PowerShell, PreRequest: false), saved.ReadSpec().ScriptSettings);
        Assert.Equal(
            """[{"listen":"prerequest","script":{"exec":["print(1)"],"type":"text/x-python"},"disabled":true},""" +
            """{"listen":"test","script":{"exec":["pm.test('js', () => {});"],"type":"text/javascript","packages":{}}}]""",
            saved.Json["event"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    [Fact]
    public void Scripts_are_colored_by_their_language_in_the_theme_s_script_colors()
    {
        var dracula = ThemeCatalog.Find(ThemeCatalog.BuiltIn, "Dracula")!;
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        Attribute keyword, plain, comment, command, parameter, variable, unreadable;
        try
        {
            ThemeCatalog.Apply(dracula);
            ui.Click("POST POST JSON");
            ui.Click("Scripts");
            var editor = ui.Find("Pre-request (before sending)") + new Size(2, 1);
            ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(editor));
            ui.Type("if ok: # done");
            var python = ui.Find("if ok: # done");
            (keyword, plain, comment) = (CellAt(ui, python.X, python.Y), CellAt(ui, python.X + 3, python.Y), CellAt(ui, python.X + 7, python.Y));

            ui.Click("○ PowerShell");
            ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(editor));
            ui.Type("Get-Item -Path $x");
            var powerShell = ui.Find("Get-Item -Path $x");
            (command, parameter, variable) = (CellAt(ui, powerShell.X, powerShell.Y), CellAt(ui, powerShell.X + 9, powerShell.Y), CellAt(ui, powerShell.X + 15, powerShell.Y));

            // A script color that would not show on the editor's background is drawn in the text color.
            ThemeCatalog.Apply(new ColorTheme { Name = "Unreadable", Palette = dracula.Palette with { SyntaxParameter = dracula.Palette.Input }, Schemes = dracula.Schemes });
            ui.Screen();
            unreadable = CellAt(ui, powerShell.X + 9, powerShell.Y);
        }
        finally
        {
            ui.App.InjectKey(Key.S.WithCtrl); // saved, so closing does not ask about unsaved changes
            ui.App.End(token!);
            ThemeCatalog.Apply(ThemeCatalog.BuiltIn[0]);
        }

        var palette = dracula.Palette;
        Assert.Equal(new Attribute(palette.SyntaxKeyword, palette.Input), keyword);
        Assert.Equal(new Attribute(palette.Text, palette.Input), plain);
        Assert.Equal(new Attribute(palette.SyntaxComment, palette.Input), comment);
        Assert.Equal(new Attribute(palette.SyntaxFunction, palette.Input), command);
        Assert.Equal(new Attribute(palette.SyntaxParameter, palette.Input), parameter);
        Assert.Equal(new Attribute(palette.SyntaxVariable, palette.Input), variable);
        Assert.Equal(new Attribute(palette.Text, palette.Input), unreadable);
    }

    [Fact]
    public async Task CSharp_scripts_are_built_and_run_by_the_chosen_dotnet()
    {
        var dotnet = TestCSharp.Require();
        await using var server = await LocalEchoServer.StartAsync();
        var setup = PostmanCollection.Load(Path.Combine(_directory, "TermRest Demo.postman_collection.json"));
        var request = setup.FindRequest("Basics/GET with query")!;
        var spec = request.ReadSpec();
        spec.Scripts = new ScriptSet("",
            $"// {Guid.NewGuid():N}\ntr.Test(\"status is 200\", () => tr.AssertEqual(200, tr.Response.Status));\nConsole.WriteLine(\"query \" + tr.Response.Json()[\"query\"]);",
            ScriptLanguage.CSharp);
        setup.UpdateRequest(request, spec);
        setup.Save();
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.SetCSharpRuntime(dotnet);
        });
        var ran = "";
        var deadline = DateTime.UtcNow.AddSeconds(90);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            ui.App.InjectKey(Key.F5);
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            var screen = ui.Screen();
            if (screen.Contains("tests 1/1 passed", StringComparison.Ordinal) || DateTime.UtcNow > deadline)
            {
                ran = screen;
                ui.App.RequestStop();
                return false;
            }

            return true;
        });

        ui.App.Run(ui.Window);

        OnScreen("tests 1/1 passed", ran);
        var scripts = ui.Window.ResponsePane.ScriptsText;
        Assert.Contains("✓ status is 200", scripts, StringComparison.Ordinal);
        Assert.Contains("[test · request 'Basics/GET with query'] query ?foo=bar&source=termrest", scripts, StringComparison.Ordinal);
        Assert.Contains("The C# scripts were built", scripts, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PowerShell_scripts_run_in_the_chosen_pwsh()
    {
        var pwsh = TestPowerShell.Require();
        await using var server = await LocalEchoServer.StartAsync();
        var setup = PostmanCollection.Load(Path.Combine(_directory, "TermRest Demo.postman_collection.json"));
        var request = setup.FindRequest("Basics/GET with query")!;
        var spec = request.ReadSpec();
        spec.Scripts = new ScriptSet(
            "$tr.Request.Headers.Set('X-From', 'pwsh')",
            "$tr.Test('status is 200', { $tr.AssertEqual(200, $tr.Response.Status) })\nWrite-Host ('sent ' + $tr.Request.Headers.Get('X-From'))",
            ScriptLanguage.PowerShell);
        setup.UpdateRequest(request, spec);
        setup.Save();
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.SetPowerShellRuntime(pwsh);
        });
        var ran = "";
        var deadline = DateTime.UtcNow.AddSeconds(60);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            ui.App.InjectKey(Key.F5);
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            var screen = ui.Screen();
            if (screen.Contains("tests 1/1 passed", StringComparison.Ordinal) || DateTime.UtcNow > deadline)
            {
                ran = screen;
                ui.App.RequestStop();
                return false;
            }

            return true;
        });

        ui.App.Run(ui.Window);

        OnScreen("tests 1/1 passed", ran);
        var scripts = ui.Window.ResponsePane.ScriptsText;
        Assert.Contains("✓ status is 200", scripts, StringComparison.Ordinal);
        Assert.Contains("[test · request 'Basics/GET with query'] sent pwsh", scripts, StringComparison.Ordinal);
        Assert.Equal(["PowerShell"], ui.Runner.RunningRuntimes);
    }

    [Fact]
    public async Task Postman_javascript_scripts_are_skipped_until_a_runtime_is_chosen_then_run()
    {
        var node = TestJavaScript.Require();
        await using var server = await LocalEchoServer.StartAsync();
        var setup = PostmanCollection.Load(Path.Combine(_directory, "TermRest Demo.postman_collection.json"));
        var node1 = setup.FindRequest("Basics/GET with query")!;
        var spec = node1.ReadSpec();
        spec.Scripts = new ScriptSet("", "pm.test('status is 200', () => pm.response.to.have.status(200));\nconsole.log('query', pm.response.json().query);",
            ScriptLanguage.JavaScript);
        setup.UpdateRequest(node1, spec);
        setup.Save();
        using var ui = new Harness(_directory, runner => runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo");
        string skipped = "", ran = "";
        var sends = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            ui.App.InjectKey(Key.F5);
            sends = 1;
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            var screen = ui.Screen();
            if (DateTime.UtcNow > deadline)
            {
                ui.App.RequestStop();
                return false;
            }

            if (sends == 1 && screen.Contains("200 OK", StringComparison.Ordinal))
            {
                skipped = screen;
                ui.Runner.SetJavaScriptRuntime(node); // as Tools › Scripts… does once saved
                ui.App.InjectKey(Key.F5);
                sends = 2;
            }
            else if (sends == 2 && screen.Contains("tests 1/1 passed", StringComparison.Ordinal))
            {
                ran = screen;
                ui.App.RequestStop();
                return false;
            }

            return true;
        });

        ui.App.Run(ui.Window);

        OnScreen("⚠ scripts not run", skipped);
        OnScreen("tests 1/1 passed", ran);
        var scripts = ui.Window.ResponsePane.ScriptsText;
        Assert.Contains("✓ status is 200", scripts, StringComparison.Ordinal);
        Assert.Contains("[test · request 'Basics/GET with query'] query ?foo=bar&source=termrest", scripts, StringComparison.Ordinal);
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
    public async Task The_scripts_dialog_chooses_a_runtime_for_each_language()
    {
        TestCSharp.Require();

        // Find takes dotnet on PATH, else the .NET installed for the user (~/.dotnet); TERMREST_TEST_DOTNET may name another.
        var sdk = (await ScriptRuntimeLocator.FindAsync(ScriptLanguage.CSharp, _directory, r => r, TestContext.Current.CancellationToken))?.Runtime;
        if (sdk is null)
        {
            Assert.Skip("Find looks for dotnet on PATH and in ~/.dotnet, and neither is a .NET SDK 10 or later.");
        }

        var settings = TestFiles.NewTempDirectory();
        using var ui = new Harness(_directory);
        string dialog = "", found = "";
        var deadline = DateTime.UtcNow.AddSeconds(30);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            var clicked = false;
            ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                var screen = ui.Screen();
                if (!screen.Contains("Stop scripts", StringComparison.Ordinal))
                {
                    return DateTime.UtcNow < deadline;
                }

                if (!clicked)
                {
                    // The third row's Find: C#.
                    dialog = screen;
                    var finds = screen.Split('\n').SelectMany((line, y) => Enumerable.Range(0, line.Length)
                        .Where(x => string.CompareOrdinal(line, x, "Find", 0, 4) == 0).Select(x => new Point(x, y))).ToList();
                    ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(finds[2]));
                    clicked = true;
                    return true;
                }

                if (!screen.Contains("✓ .NET SDK", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
                {
                    return true;
                }

                found = screen;
                ui.Click("► Save ◄");
                return false;
            });
            ScriptsDialog.ShowRuntimes(ui.App, ui.Workspace, ui.Runner, settings);
            found = found.Length > 0 ? found : "closed before Find answered:\n" + ui.Screen();
            ui.App.RequestStop();
            return false;
        });

        ui.App.Run(ui.Window);

        OnScreen("None chosen: Python scripts do not run.", dialog);
        OnScreen("None chosen: JavaScript scripts do not run.", dialog);
        OnScreen("None chosen: C# scripts do not run.", dialog);
        OnScreen("None chosen: PowerShell scripts do not run.", dialog);
        Assert.True(found.Contains("✓ .NET SDK", StringComparison.Ordinal), $"{dialog}\n----\n{found}");
        Assert.Equal(sdk, UserSettings.Load(settings).GetScriptRuntime(ScriptLanguage.CSharp, ui.Workspace.RootPath));
        Assert.Equal(sdk, ui.Runner.CSharpRuntime);
        Assert.Null(ui.Runner.PythonInterpreter);
    }

    [Fact]
    public void The_scripts_dialog_orders_the_languages_for_every_workspace()
    {
        var settings = TestFiles.NewTempDirectory();
        using var ui = new Harness(_directory);
        string before = "", after = "";
        Point Up(int row) => ui.Screen().Split('\n')
            .SelectMany((line, y) => Enumerable.Range(0, line.Length).Where(x => string.CompareOrdinal(line, x, "⟦ ▲ ⟧", 0, 5) == 0).Select(x => new Point(x + 2, y)))
            .ElementAt(row);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                before = ui.Screen();

                // PowerShell, the last row, moves up twice: it becomes second.
                ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(Up(3)));
                ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(Up(2)));
                after = ui.Screen();
                ui.Click("► Save ◄");
                return false;
            });
            ScriptsDialog.ShowRuntimes(ui.App, ui.Workspace, ui.Runner, settings);
            ui.App.RequestStop();
            return false;
        });

        ui.App.Run(ui.Window);

        OnScreen("4 PowerShell", before);
        OnScreen("▲ ▼ change the order, for all workspaces", before);
        OnScreen("2 PowerShell", after);
        OnScreen("3 JavaScript", after);
        IReadOnlyList<string> order = [ScriptLanguage.Python, ScriptLanguage.PowerShell, ScriptLanguage.JavaScript, ScriptLanguage.CSharp];
        Assert.Equal(order, UserSettings.Load(settings).GetScriptLanguageOrder());
        Assert.Equal(order, ui.Runner.ScriptLanguageOrder);
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
        var keys = Column("F5, Ctrl+Enter", "Esc ", "F2 ", "Insert ", "F8 ", "F9 ");
        var actions = Column("Send (again to cancel)", "Cancel a request, stop a stream", "Rename tree item", "Add a table row", "Client certificates", "Collections ⇄ History");
        var moreKeys = Column("F12  ", "Tab, Shift+Tab", "←, → (tab row)", "Shift+F10", "Ctrl+L", "Ctrl+F ", "F3, Shift+F3");
        var moreActions = Column("Start / stop recording", "Next / previous field", "Previous / next tab", "Focus the URL bar", "Find in the response", "Next / previous match");
        Assert.True(keys < actions && actions < moreKeys && moreKeys < moreActions, screen);
        Assert.Equal(At(screen, "Esc ").Y + 1, At(screen, "Ctrl+S ").Y);
        OnScreen("Right-click the tree or the history for actions", screen);
    }

    [Fact]
    public void Keyboard_help_uses_one_column_on_narrow_screens()
    {
        var wide = MainWindow.KeysHelp(120).Split('\n');
        var narrow = MainWindow.KeysHelp(80).Split('\n');

        Assert.Equal(wide.Length + 10, narrow.Length);
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
    public void A_help_text_taller_than_the_screen_scrolls_to_its_end()
    {
        using var ui = new Harness(_directory);
        string top = "", end = "";
        var closed = false;

        var screens = Drive(ui, () =>
            {
                ui.App.Driver!.SetScreenSize(120, 30);
                ui.Click("Help");
                ui.Click("Scripting");
                ui.App.RequestStop();
            },
            ("Scripts run in a runtime you choose", () =>
            {
                top = ui.Screen();
                for (var i = 0; i < 6; i++)
                {
                    ui.App.InjectKey(Key.PageDown);
                }

                end = ui.Screen();
                ui.App.InjectKey(Key.Enter); // OK
                closed = ui.App.TopRunnable is { StopRequested: true };
            }));

        Assert.Single(screens);
        Assert.DoesNotContain("PowerShell: the $tr object", top, StringComparison.Ordinal);
        OnScreen("PowerShell: the $tr object, as in C#", end);
        OnScreen("permissions: choose runtimes only for workspaces whose collections you trust.", end);
        OnScreen("► OK ◄", end);
        Assert.True(closed);
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

    [Fact]
    public void Certificate_dialog_buttons_do_their_job_and_leave_the_dialog_open()
    {
        var setup = ApiWorkspace.Open(_directory);
        setup.Certificates.Entries.Add(new TermRest.Core.Certificates.CertificateEntry
        {
            Name = "Team cert",
            Matches = ["https://localhost/*"],
            Pfx = new TermRest.Core.Certificates.FileSource { Src = "missing.pfx" },
        });
        setup.SaveCertificates();
        using var ui = new Harness(_directory);

        var screens = Drive(ui, () =>
            {
                CertificatesDialog.Show(ui.App, ui.Workspace, ui.Runner, "https://localhost/api");
                ui.App.RequestStop();
            },
            ("Team cert", () =>
            {
                ui.Click("Team cert");
                ui.App.InjectKey(Key.Enter); // tests the certificate
            }),
            ("✗", () => ui.Click("On/Off")),
            ("Team cert", () => ui.Click("Add PEM…")),
            ("Leave the key empty", () => ui.Click("Browse…")),
            ("Filename (▲)", () => ui.App.InjectKey(Key.Esc)),
            ("Leave the key empty", () => ui.Click("Cancel")),
            ("Registry:", () => ui.Click("Close")));

        Assert.Equal(7, screens.Count);
        OnScreen("missing.pfx", screens[1].Split('\n').Single(l => l.Contains('✗', StringComparison.Ordinal)));
        Assert.Contains("off", screens[2].Split('\n').First(l => l.Contains("Team cert", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.True(Assert.Single(ApiWorkspace.Open(_directory).Certificates.Entries).Disabled);
    }

    [Fact]
    public void The_certificate_dialog_asks_before_saving_over_a_registry_changed_on_disk_and_cancel_loads_it()
    {
        var setup = ApiWorkspace.Open(_directory);
        setup.Certificates.Entries.Add(new TermRest.Core.Certificates.CertificateEntry
        {
            Name = "Team cert",
            Matches = ["https://localhost/*"],
            Pfx = new TermRest.Core.Certificates.FileSource { Src = "team.pfx" },
        });
        setup.SaveCertificates();
        using var ui = new Harness(_directory);

        // A git pull adds a teammate's certificate while TermRest is open.
        setup.Certificates.Entries.Add(new TermRest.Core.Certificates.CertificateEntry
        {
            Name = "Partner cert",
            Matches = ["https://partner.example.com/*"],
            Pfx = new TermRest.Core.Certificates.FileSource { Src = "partner.pfx" },
        });
        setup.SaveCertificates();
        var pulled = File.ReadAllText(setup.CertificatesPath);
        var afterCancel = "";

        var screens = Drive(ui, () =>
            {
                CertificatesDialog.Show(ui.App, ui.Workspace, ui.Runner, null);
                ui.App.RequestStop();
            },
            ("Team cert", () =>
            {
                ui.Click("Team cert");
                ui.Click("On/Off");
            }),
            ("Changed on disk", () => ui.App.InjectKey(Key.Enter)), // Cancel, the default
            ("Partner cert", () =>
            {
                afterCancel = File.ReadAllText(setup.CertificatesPath);
                ui.Click("Team cert");
                ui.Click("On/Off"); // read since: saved without asking
            }),
            ("Registry:", () => ui.Click("Close")));

        Assert.Equal(4, screens.Count);
        OnScreen("certificates.json", screens[1]);
        OnScreen("Overwrite", screens[1]);
        Assert.Equal(pulled, afterCancel);
        Assert.Equal([("Team cert", true), ("Partner cert", false)], ApiWorkspace.Open(_directory).Certificates.Entries.Select(e => (e.Name, e.Disabled)));
    }

    [Fact]
    public void Environment_dialog_buttons_do_their_job_and_leave_the_dialog_open()
    {
        using var ui = new Harness(_directory);

        var screens = Drive(ui, () =>
            {
                EnvironmentsDialog.Show(ui.App, ui.Workspace);
                ui.App.RequestStop();
            },
            ("Active environment: (none)", () => ui.Click("New…")),
            ("New environment", () =>
            {
                ui.Type("Staging");
                ui.Click("► OK ◄");
            }),
            ("Staging", () => ui.Click("Set active")),
            ("Active environment: Staging", () => ui.Click("Close")));

        Assert.Equal(4, screens.Count);
        var saved = ApiWorkspace.Open(_directory);
        Assert.Equal(["Postman Echo", "Staging"], saved.Environments.Select(e => e.Name).Order());
        Assert.Equal("Staging", saved.ActiveEnvironment?.Name);
    }

    [Fact]
    public void Without_an_environment_the_dialog_takes_no_values_until_one_is_created()
    {
        // Values typed before an environment exists would have nowhere to go.
        var empty = TestFiles.NewTempDirectory();
        using var ui = new Harness(empty);

        var screens = Drive(ui, () =>
            {
                EnvironmentsDialog.Show(ui.App, ui.Workspace);
                ui.App.RequestStop();
            },
            ("No environments yet", () => ui.Click("New…")),
            ("New environment", () =>
            {
                ui.App.RaisePasteEvent("Local");
                ui.Click("► OK ◄");
            }),
            ("+ Add", () => ui.Click("Close")));

        Assert.Equal(3, screens.Count);
        Assert.DoesNotContain("+ Add", screens[0], StringComparison.Ordinal);
        Assert.DoesNotContain("No environments yet", screens[2], StringComparison.Ordinal);
        Assert.Equal("Local", Assert.Single(ApiWorkspace.Open(empty).Environments).Name);
    }

    [Fact]
    public void The_environment_dialog_goes_on_editing_its_environment_when_new_or_import_fails()
    {
        var file = Path.Combine(_directory, "Postman Echo.postman_environment.json");
        using var ui = new Harness(_directory);

        var screens = Drive(ui, () =>
            {
                EnvironmentsDialog.Show(ui.App, ui.Workspace);
                ui.App.RequestStop();
            },
            ("Active environment: (none)", () =>
            {
                // New environments go to postman/environments, a folder that can't be made under a file of that name.
                ui.Workspace.Settings.CollectionFormat = "v3";
                File.WriteAllText(Path.Combine(_directory, "postman"), "");
                ui.Click("New…");
            }),
            ("New environment", () =>
            {
                ui.App.RaisePasteEvent("Staging");
                ui.Click("► OK ◄");
            }),
            ("Cannot create environment", () =>
            {
                ui.Workspace.Settings.CollectionFormat = null;
                File.Delete(Path.Combine(_directory, "postman"));
                ui.Click("► OK ◄");
            }),
            ("Active environment: (none)", () =>
            {
                ui.Click("unused");
                ui.App.InjectKey(Key.Space); // on
                ui.Click("Import…"); // its own file, changed here: refused
            }),
            ("Filename (▲)", () =>
            {
                ui.App.RaisePasteEvent(file); // over the folder the picker shows
                ui.App.InjectKey(Key.Enter);
            }),
            ("Import failed", () => ui.Click("► OK ◄")),
            ("Active environment: (none)", () =>
            {
                ui.Click("token");
                ui.App.InjectKey(Key.Space); // off
                ui.Click("Set active");
            }),
            ("Active environment: Postman Echo", () => ui.Click("Close")));

        Assert.Equal(8, screens.Count);
        OnScreen("already open", screens[5]);
        Assert.Equal([true, false, true], PostmanEnvironment.Load(file).Values.Select(v => v.Enabled));
        var saved = ApiWorkspace.Open(_directory);
        Assert.Equal("Postman Echo", Assert.Single(saved.Environments).Name);
        Assert.Equal("Postman Echo", saved.ActiveEnvironment?.Name);
    }

    [Fact]
    public void Lists_scrolled_sideways_show_characters_outside_the_BMP_as_replacement_characters()
    {
        // Terminal.Gui's lists cut a row scrolled sideways at a UTF-16 index: inside an emoji, drawing it failed.
        var setup = ApiWorkspace.Open(_directory);
        setup.CreateEnvironment("Prod 🚀");
        setup.CreateEnvironment("Prod 🚀"); // both show their file names then: wider than the list
        var themes = ThemeCatalog.WorkspaceThemesDirectory(_directory);
        Directory.CreateDirectory(themes);
        File.WriteAllText(Path.Combine(themes, "rocket.json"), """{ "name": "Rocket 🚀 at night, a theme with a long name", "base": "Nord" }""");
        using var ui = new Harness(_directory);
        string environments = "", custom = "", themeList = "";
        void ScrollRight()
        {
            var list = Descendants(ui.App.TopRunnableView!).OfType<Terminal.Gui.Views.ListView>().Single();
            for (var i = 0; i < 8; i++)
            {
                ui.App.InjectMouse(new Mouse { ScreenPosition = list.FrameToScreen().Location, Flags = MouseFlags.WheeledRight });
            }
        }

        try
        {
            var screens = Drive(ui, () =>
                {
                    EnvironmentsDialog.Show(ui.App, ui.Workspace);
                    ThemeDialog.Show(ui.App, ui.Workspace, Path.Combine(_directory, "user-config"));
                    ui.App.RequestStop();
                },
                ("Active environment:", () =>
                {
                    ScrollRight();
                    environments = ui.Screen();
                    ui.Click("Close");
                }),
                ("Themes (* custom)", () =>
                {
                    ui.App.InjectKey(Key.End); // custom themes come last
                    custom = ui.Screen();
                    ScrollRight();
                    themeList = ui.Screen();
                    ui.Click("Cancel");
                }));

            Assert.Equal(2, screens.Count);
            OnScreen("  Prod \uFFFD (Prod \uFFFD", screens[0]);
            OnScreen(" (Prod \uFFFD.postman_environment", environments); // scrolled past the name's own \uFFFD
            OnScreen("Rocket \uFFFD at night", custom);
            Assert.DoesNotContain("Rocket \uFFFD", themeList, StringComparison.Ordinal);
        }
        finally
        {
            ThemeCatalog.Apply(ThemeCatalog.BuiltIn[0]);
        }
    }

    [Fact]
    public void An_unexpected_error_in_a_dialog_loses_none_of_its_changes()
    {
        // The error goes on to the handler of unexpected errors, as any error in the main loop does; here it is caught
        // where the dialog was opened.
        var file = Path.Combine(_directory, "Postman Echo.postman_environment.json");
        var userDirectory = Path.Combine(_directory, "user-config");
        var original = ThemeCatalog.Current.Name;
        Exception? environmentsError = null, themeError = null;
        string statusBar = "", previewed = "", restored = "";
        List<bool> saved = [];
        string? active = null;
        using (var ui = new Harness(_directory))
        {
            Drive(ui, () => ui.App.InjectKey(Key.F7),
                ("Manage environments…", () =>
                {
                    environmentsError = Record.Exception(() => ui.Click("Manage environments…"));
                    statusBar = ui.Screen();
                    saved = [.. PostmanEnvironment.Load(file).Values.Select(v => v.Enabled)];
                    active = ApiWorkspace.Open(_directory).ActiveEnvironment?.Name;
                    ui.App.RequestStop();
                }),
                ("Active environment:", () =>
                {
                    ui.Click("token");
                    ui.App.InjectKey(Key.Space); // off
                    ui.Click("Set active");
                    throw new InvalidOperationException("Unexpected");
                }));
        }

        using (var ui = new Harness(_directory))
        {
            try
            {
                Drive(ui, () =>
                    {
                        themeError = Record.Exception(() => ThemeDialog.Show(ui.App, ui.Workspace, userDirectory));
                        restored = ThemeCatalog.Current.Name;
                        ui.App.RequestStop();
                    },
                    ("Themes (* custom)", () =>
                    {
                        ui.Click("Dracula");
                        previewed = ThemeCatalog.Current.Name;
                        throw new InvalidOperationException("Unexpected");
                    }));
            }
            finally
            {
                ThemeCatalog.Apply(ThemeCatalog.BuiltIn[0]);
            }
        }

        Assert.IsType<InvalidOperationException>(environmentsError);
        OnScreen("Env: Postman Echo", statusBar);
        Assert.Equal([true, false, false], saved);
        Assert.Equal("Postman Echo", active);
        Assert.IsType<InvalidOperationException>(themeError);
        Assert.Equal(("Dracula", original), (previewed, restored));
    }

    [Fact]
    public void Importing_an_environment_named_as_the_active_one_names_its_file_in_the_status_bar()
    {
        var setup = ApiWorkspace.Open(_directory);
        var active = setup.CreateEnvironment("Production");
        setup.ActiveEnvironment = active;
        setup.SaveSettings();
        var exported = Path.Combine(_directory, "exports", "Production.postman_environment.json");
        Directory.CreateDirectory(Path.GetDirectoryName(exported)!);
        File.Copy(active.FilePath!, exported);
        using var ui = new Harness(_directory);
        var imported = "";

        var screens = Drive(ui, () =>
            {
                ui.Click("File");
                ui.Click("Import Postman environment…"); // returns once it is imported
                ui.App.Driver!.SetScreenSize(200, 45); // room for the status bar's message
                imported = ui.Screen();
                ui.App.RequestStop();
            },
            ("Filename (▲)", () =>
            {
                ui.App.RaisePasteEvent(exported);
                ui.App.InjectKey(Key.Enter);
            }));

        Assert.Single(screens);
        Assert.Equal(2, ui.Workspace.Environments.Count(e => e.Name == "Production"));
        OnScreen("Env: Production (Production.postman_environment.json)", imported);
    }

    [Fact]
    public void Editing_a_certificate_from_an_os_store_keeps_its_store()
    {
        const string Thumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";
        var setup = ApiWorkspace.Open(_directory);
        setup.Certificates.Entries.Add(new TermRest.Core.Certificates.CertificateEntry
        {
            Name = "Partner cert",
            Matches = ["https://partner.example.com/*"],
            Store = new TermRest.Core.Certificates.StoreSource { StoreName = "TrustedPeople", Thumbprint = Thumbprint },
        });
        setup.SaveCertificates();
        using var ui = new Harness(_directory);

        var screens = Drive(ui, () =>
            {
                CertificatesDialog.Show(ui.App, ui.Workspace, ui.Runner, null);
                ui.App.RequestStop();
            },
            ("Partner cert", () =>
            {
                ui.Click("Partner cert");
                ui.Click("Edit…");
            }),
            ("Store location", () => ui.Click("► Save ◄")),
            ("Registry:", () => ui.Click("Close")));

        Assert.Equal(3, screens.Count);
        var store = Assert.Single(ApiWorkspace.Open(_directory).Certificates.Entries).Store;
        Assert.Equal(("CurrentUser", "TrustedPeople", Thumbprint), (store?.Location, store?.StoreName, store?.Thumbprint));
    }

    [Fact]
    public void Browse_buttons_open_the_file_picker_and_leave_their_dialog_open()
    {
        using var ui = new Harness(_directory);
        bool? settingsSaved = null;
        var field = new KeyValueEntry();

        var screens = Drive(ui, () =>
            {
                settingsSaved = SettingsDialog.Show(ui.App, ui.Workspace, ui.Runner, out _);
                field = KeyValueEntryDialog.Show(ui.App, "Edit form field", new KeyValueEntry { Key = "avatar", Type = "text" }, Views.KeyValueKind.FormData);
                ui.App.RequestStop();
            },
            ("Workspace settings", () =>
            {
                ui.Click("Verify server certificates"); // not saved: the dialog is cancelled
                ui.Click("Browse…");
            }),
            ("Filename (▲)", () => ui.App.InjectKey(Key.Esc)),
            ("Workspace settings", () => ui.Click("Cancel")),
            ("Edit form field", () => ui.Click("Browse…")),
            ("Filename (▲)", () => ui.App.InjectKey(Key.Esc)),
            ("Edit form field", () => ui.Click("Cancel")));

        Assert.Equal(6, screens.Count);
        Assert.False(settingsSaved);
        Assert.True(ui.Workspace.Settings.StrictSsl);
        Assert.True(ApiWorkspace.ReadSettings(_directory).StrictSsl);
        Assert.Null(field);
    }

    [Fact]
    public void Browse_in_the_recording_dialog_does_not_end_the_recording()
    {
        using var ui = new Harness(_directory);
        using var recorder = RequestRecorder.Start(new RecordingOptions { WorkspaceRoot = _directory });
        var result = RecordingDialogResult.Saved;

        var screens = Drive(ui, () =>
            {
                (result, _) = RecordingDialog.Show(ui.App, recorder, RecordingDialog.DefaultPath(_directory, recorder.StartedAt), RecordingDialogPurpose.Stop);
                ui.App.RequestStop();
            },
            ("Stop recording", () => ui.Click("Browse…")),
            ("Filename (▲)", () => ui.App.InjectKey(Key.Esc)),
            ("Stop recording", () => ui.Click("Keep recording")));
        var recording = recorder.IsRecording;
        if (recording)
        {
            recorder.Discard();
        }

        Assert.Equal(3, screens.Count);
        Assert.Equal(RecordingDialogResult.Kept, result);
        Assert.True(recording);
        Assert.Empty(Directory.GetFiles(_directory, "*.har"));
    }

    [Fact]
    public void Theme_dialog_buttons_leave_the_dialog_open()
    {
        var userDirectory = Path.Combine(_directory, "user-config");
        using var ui = new Harness(_directory);
        ColorTheme? chosen = ThemeCatalog.Current;
        try
        {
            var screens = Drive(ui, () =>
                {
                    chosen = ThemeDialog.Show(ui.App, ui.Workspace, userDirectory);
                    ui.App.RequestStop();
                },
                ("Themes (* custom)", () => ui.Click("Reload theme files")),
                ("Themes (* custom)", () => ui.Click("Copy as custom theme…")),
                ("Name of the new theme", () => ui.Click("Cancel")),
                ("Themes (* custom)", () => ui.Click("Cancel")));

            Assert.Equal(4, screens.Count);
            Assert.Null(chosen);
        }
        finally
        {
            ThemeCatalog.Apply(ThemeCatalog.BuiltIn[0]);
        }
    }

    [Fact]
    public void A_theme_copy_never_takes_the_name_of_a_custom_theme()
    {
        // Only one theme of a name is loaded, the one read last: a copy named like another custom theme would never show.
        var userDirectory = Path.Combine(_directory, "user-config");
        var themesFolder = UserSettings.ThemesDirectory(userDirectory);
        ThemeCatalog.Apply(ThemeCatalog.BuiltIn[0]);
        var taken = $"My {ThemeCatalog.BuiltIn[0].Name}";
        ThemeCatalog.WriteCopy(ThemeCatalog.BuiltIn[0], taken, themesFolder);
        using var ui = new Harness(_directory);
        try
        {
            var screens = Drive(ui, () =>
                {
                    ThemeDialog.Show(ui.App, ui.Workspace, userDirectory);
                    ui.App.RequestStop();
                },
                ("Themes (* custom)", () => ui.Click("Copy as custom theme…")),
                ("Name of the new theme", () =>
                {
                    ui.App.InjectKey(Key.End);
                    ui.App.InjectKey(Key.Backspace);
                    ui.App.InjectKey(Key.Backspace); // the name proposed without its " 2": the taken one
                    ui.Click("► OK ◄");
                }),
                ("exists already", () => ui.Click("► OK ◄")),
                ("Themes (* custom)", () => ui.Click("Copy as custom theme…")),
                ("Name of the new theme", () => ui.Click("► OK ◄")),
                ("Custom theme created", () => ui.Click("► OK ◄")),
                ("Themes (* custom)", () => ui.Click("Cancel")));

            Assert.Equal(7, screens.Count);
            OnScreen($"{taken} 2", screens[1]);
            Assert.Equal([$"{taken} 2.json", $"{taken}.json"], Directory.GetFiles(themesFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
        finally
        {
            ThemeCatalog.Apply(ThemeCatalog.BuiltIn[0]);
        }
    }

    [Fact]
    public async Task A_response_that_arrives_after_another_request_was_opened_is_not_shown_there()
    {
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/hold"; // answered once released
            runner.ConfigureHistory(state);
        });
        string opened = "", arrivedStatus = "";
        TermRest.Core.Http.HttpExchange? shownOnArrival = null, sent = null;
        var step = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            ui.App.InjectKey(Key.F5); // GET with query
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            if (DateTime.UtcNow > deadline)
            {
                server.Release();
                ui.App.RequestStop();
                return false;
            }

            switch (step)
            {
                case 0 when ui.Window.ResponsePane.StatusText.StartsWith("Sending GET", StringComparison.Ordinal):
                    ui.Click("POST POST JSON");
                    opened = ui.Screen();
                    server.Release();
                    step = 1;
                    break;
                case 1 when ui.Runner.History!.Load().Count == 1:
                    shownOnArrival = ui.Window.ResponsePane.Exchange;
                    arrivedStatus = ui.Window.StatusMessage;
                    ui.App.InjectKey(Key.F5); // sends POST JSON
                    step = 2;
                    break;
                case 2 when ui.Window.ResponsePane.Exchange is { } exchange:
                    sent = exchange;
                    ui.App.RequestStop();
                    return false;
            }

            return true;
        });

        ui.App.Run(ui.Window);

        OnScreen("POST     ▼ {{baseUrl}}/post", opened);
        OnScreen("No response yet", opened);
        Assert.DoesNotContain("Cancel", opened, StringComparison.Ordinal); // the Send button is POST JSON's
        Assert.Null(shownOnArrival);
        Assert.Equal("Response", ui.Window.ResponseTitle);
        Assert.StartsWith("Earlier request: GET 127.0.0.1 → 200", arrivedStatus, StringComparison.Ordinal);
        Assert.Equal("/hold/post", System.Text.Json.Nodes.JsonNode.Parse(sent!.Response!.Body)!["path"]!.GetValue<string>());
        Assert.Equal(["/hold/post", "/hold/get?foo=bar&source=termrest"], ui.Runner.History!.Load().Select(e => new Uri(e.Url).PathAndQuery));
    }

    [Fact]
    public async Task A_request_added_to_a_collection_with_Ctrl_N_keeps_its_response_and_its_send()
    {
        await using var server = await LocalEchoServer.StartAsync();
        using var ui = new Harness(_directory);
        var arrived = "";

        var screens = Drive(ui, () =>
            {
                ui.Window.ImportCurl($"curl {server.BaseUrl}/hold/imported", intoCurrent: false);
                ui.App.InjectKey(Key.F5);
            },
            ("Sending GET", () => ui.App.InjectKey(Key.N.WithCtrl)), // while it is on its way
            ("Name (in TermRest Demo / Basics)", () =>
            {
                ui.Click("► OK ◄");
                server.Release();
            }),
            ("200 OK", () =>
            {
                arrived = ui.Window.StatusMessage;
                ui.App.InjectKey(Key.S.WithCtrl);
                ui.App.RequestStop();
            }));

        Assert.Equal(3, screens.Count);
        Assert.StartsWith("GET 127.0.0.1 → 200", arrived, StringComparison.Ordinal); // not "Earlier request: …"
        Assert.Equal("/hold/imported", ui.Window.ResponsePane.Exchange!.Request.Uri!.AbsolutePath);
        var saved = PostmanCollection.Load(Path.Combine(_directory, "TermRest Demo.postman_collection.json"));
        Assert.Contains($"{server.BaseUrl}/hold/imported", saved.AllRequests().Select(r => r.ReadSpec().Url));
    }

    [Fact]
    public void A_request_added_to_a_collection_with_Ctrl_N_says_what_it_inherits_there()
    {
        using var ui = new Harness(_directory);
        IEnumerable<string> AuthNotes() => Descendants(ui.Window.Editor).OfType<Views.AuthEditor>().Single().SubViews
            .SelectMany(Descendants).OfType<Terminal.Gui.Views.Label>().Select(l => l.Text);
        List<string> imported = [], added = [];

        var screens = Drive(ui, () =>
            {
                ui.Window.ShowRequest(ui.Workspace.Collections[0].FindRequest("Auth/Auth inherited from folder")!); // its folder is the target
                ui.Window.ImportCurl("curl https://api.example.com/v1/me", intoCurrent: false);
                imported = [.. AuthNotes()];
                ui.App.InjectKey(Key.N.WithCtrl);
            },
            ("Name (in TermRest Demo / Auth)", () => ui.Click("► OK ◄")),
            ("Request — TermRest Demo / Auth/", () =>
            {
                added = [.. AuthNotes()];
                ui.App.InjectKey(Key.S.WithCtrl);
                ui.App.RequestStop();
            }));

        Assert.Equal(2, screens.Count);
        Assert.Contains("No auth is set on the collection.", imported); // a scratch request's
        Assert.Contains("Currently: Bearer Token from folder 'Auth'.", added);
        Assert.DoesNotContain("No auth is set on the collection.", added);
    }

    [Fact]
    public void Changes_to_a_request_outside_a_collection_are_not_lost_without_asking()
    {
        const string Imported = "https://api.example.com/v1/users/42";
        using var ui = new Harness(_directory);
        string afterCancel = "", afterDelete = "", afterDiscard = "";

        var screens = Drive(ui, () =>
            {
                ui.Window.ImportCurl($"curl {Imported} -H 'Accept: application/json'", intoCurrent: false);
                ui.Click("POST POST JSON"); // asks: Cancel keeps the imported request
                afterCancel = ui.Window.Editor.Spec.Url;
                ui.Click("Request");
                ui.Click("Delete…"); // POST JSON, selected in the tree but not open
                afterDelete = ui.Window.Editor.Spec.Url;
                ui.Click("GET  GET with query"); // asks: Discard opens it
                afterDiscard = ui.Window.Editor.Spec.Url;
                ui.Window.ImportCurl($"curl {Imported} -H 'Accept: application/json'", intoCurrent: false);
                ui.App.InjectKey(Key.Q.WithCtrl); // asks: Add to collection…, then saving quits
            },
            ("Unsaved request", () => ui.Click("Cancel")),
            ("Delete request 'POST JSON'?", () => ui.Click("► Delete ◄")),
            ("Unsaved request", () => ui.Click("Discard")),
            ("Unsaved request", () => ui.Click("Add to collection…")),
            ("Name (in TermRest Demo / Basics)", () => ui.Click("► OK ◄")),
            ("Unsaved changes", () => ui.Click("► Save ◄")));

        Assert.Equal(6, screens.Count);
        Assert.Equal((Imported, Imported), (afterCancel, afterDelete));
        Assert.Equal("{{baseUrl}}/get?foo=bar&source=termrest", afterDiscard);
        Assert.False(ui.Window.IsRunning);
        var saved = PostmanCollection.Load(Path.Combine(_directory, "TermRest Demo.postman_collection.json"));
        Assert.Null(saved.FindRequest("Basics/POST JSON"));
        Assert.StartsWith("Basics/", saved.AllRequests().Single(r => r.ReadSpec().Url == Imported).PathInCollection, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_an_edited_history_request_to_a_collection_keeps_the_changes()
    {
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.ConfigureHistory(state);
        });

        var screens = Drive(ui, () => ui.App.InjectKey(Key.F5),
            ("200 OK", () =>
            {
                ui.App.InjectKey(Key.F9);
                ui.Click("200 GET    /echo/get");
                ui.Click("{{baseUrl}}/get?foo=bar&source=termrest");
                ui.App.InjectKey(Key.End);
                ui.Type("&edited=1");
                ui.App.InjectSequence(InputInjectionExtensions.RightButtonClick(ui.Find("200 GET    /echo/get")));
                ui.Click("Save to collection…");
            }),
            ("Name (in TermRest Demo / Basics)", () => ui.Click("► OK ◄")),
            ("Request — TermRest Demo / Basics/", () =>
            {
                ui.App.InjectKey(Key.S.WithCtrl);
                ui.App.RequestStop();
            }));

        Assert.Equal(3, screens.Count);
        var saved = PostmanCollection.Load(Path.Combine(_directory, "TermRest Demo.postman_collection.json"));
        Assert.Contains("{{baseUrl}}/get?foo=bar&source=termrest&edited=1", saved.AllRequests().Select(r => r.ReadSpec().Url));
    }

    [Fact]
    public async Task A_history_request_whose_credentials_were_masked_asks_before_it_is_sent_again()
    {
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner => runner.ConfigureHistory(state));
        var auth = new AuthSpec { Type = "basic" };
        auth.Set("username", "ada");
        auth.Set("password", "typed-s3cret");
        await ui.Runner.SendAsync(PostmanCollection.Create("Scratch"), null, new HttpRequestSpec { Url = server.BaseUrl + "/echo/masked", Auth = auth }, null,
            TestContext.Current.CancellationToken);
        string? notice = null;
        var sentAfterCancel = -1;

        var screens = Drive(ui, () =>
            {
                ui.App.InjectKey(Key.F9);
                ui.Click("/echo/masked");
                notice = ui.Window.StatusMessage;
                ui.App.InjectKey(Key.F5); // asks, and is cancelled
                ui.App.InjectKey(Key.F5); // asks: Enter is Cancel
                sentAfterCancel = ui.Runner.History!.Load().Count;
                ui.App.InjectKey(Key.F5); // asks, and is sent
            },
            ("Masked credentials", () => ui.App.InjectKey(Key.Esc)),
            ("Masked credentials", () => ui.App.InjectKey(Key.Enter)),
            ("Masked credentials", () => ui.Click("Send ***")),
            ("200 OK", () => ui.App.RequestStop()));

        Assert.Equal(4, screens.Count);
        Assert.Contains("***", notice, StringComparison.Ordinal);
        Assert.Equal(1, sentAfterCancel);
        Assert.Equal(2, ui.Runner.History!.Load().Count);
    }

    [Fact]
    public void Names_are_taken_without_the_spaces_around_them()
    {
        using var ui = new Harness(_directory);
        List<string?> names = [];

        var screens = Drive(ui, () =>
            {
                names.Add(Dialogs.Ui.PromptText(ui.App, "First name", "Name"));
                names.Add(Dialogs.Ui.PromptText(ui.App, "Second name", "Name"));
                ui.App.RequestStop();
            },
            ("First name", () =>
            {
                ui.Type("  Ada  ");
                ui.Click("► OK ◄");
            }),
            ("Second name", () =>
            {
                ui.Type("   ");
                ui.Click("► OK ◄");
            }));

        Assert.Equal(2, screens.Count);
        Assert.Equal(["Ada", ""], names); // spaces alone are no name: New request, Rename … then do nothing
    }

    [Fact]
    public void The_recording_clock_counts_hours_past_a_day()
    {
        using var ui = new Harness(_directory);
        var indicator = new Views.RecordingIndicator();
        ui.Window.Add(indicator);

        indicator.ShowRecording(DateTimeOffset.Now - new TimeSpan(25, 3, 0), 2);

        Assert.Contains(" REC 25:03:0", indicator.Caption, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_bar_shortcuts_do_not_act_behind_a_dialog()
    {
        using var ui = new Harness(_directory);
        string? name = null;
        var stillRunning = false;

        var screens = Drive(ui, () =>
            {
                name = Dialogs.Ui.PromptText(ui.App, "Name it", "Name");
                stillRunning = ui.Window.IsRunning;
                ui.App.InjectKey(Key.Q.WithCtrl); // in the main window, it quits
            },
            ("Name it", () => ui.App.InjectKey(Key.Q.WithCtrl)),
            ("Name it", () =>
            {
                ui.App.InjectKey(Key.F5);
                ui.App.InjectKey(Key.F8);
            }),
            ("Name it", () =>
            {
                ui.Type("Ada");
                ui.Click("► OK ◄");
            }));

        Assert.Equal(3, screens.Count);
        Assert.Equal("Ada", name);
        Assert.True(stillRunning);
        Assert.StartsWith("No response yet", ui.Window.ResponsePane.StatusText, StringComparison.Ordinal);
        Assert.False(ui.Window.IsRunning);
    }

    [Fact]
    public void A_workspace_folder_that_cannot_be_opened_leaves_the_current_one_open()
    {
        var other = Path.Combine(_directory, "other");
        Directory.CreateDirectory(other);
        var missing = Path.Combine(_directory, "missing");
        using var ui = new Harness(_directory);
        var stayed = false;

        var screens = Drive(ui, () =>
            {
                ui.Window.OpenWorkspaceFrom(missing);
                stayed = ui.Window.IsRunning && ui.Window.NextWorkspace is null;
                ui.Window.OpenWorkspaceFrom(other);
            },
            ("Cannot open workspace", () => ui.Click("► OK ◄")));

        OnScreen("does not exist", Assert.Single(screens));
        Assert.True(stayed);
        Assert.False(ui.Window.IsRunning);
        Assert.Equal(other, ui.Window.NextWorkspace?.RootPath);
        Assert.Equal(2, TuiApp.Run(missing)); // nor does the first one end the program
    }

    [Fact]
    public void Unexpected_errors_are_logged_in_the_users_own_state_folder()
    {
        var state = Path.Combine(_directory, "state");

        var log = TuiApp.LogUnexpectedError(new InvalidOperationException("first"), state);
        TuiApp.LogUnexpectedError(new InvalidOperationException("second"), state);

        Assert.Equal(Path.Combine(state, "errors.log"), log);
        var text = File.ReadAllText(log);
        Assert.Contains("System.InvalidOperationException: first", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: second", text, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(log));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(state));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // the state folder can't be written: the error is shown all the same
    public async Task An_unexpected_error_after_sending_is_shown_and_logged_and_TermRest_goes_on(bool canLog)
    {
        var state = Path.Combine(_directory, "state");
        if (!canLog)
        {
            File.WriteAllText(state, "a file where the folder would be");
        }

        var previousState = Environment.GetEnvironmentVariable(TermRest.Core.History.HistoryStore.StateDirectoryVariable);
        Environment.SetEnvironmentVariable(TermRest.Core.History.HistoryStore.StateDirectoryVariable, state);
        try
        {
            await using var server = await LocalEchoServer.StartAsync();
            using var ui = new Harness(_directory, runner => runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo");

            // Once the response is in, a step fails (saying the result in the status bar): an exception that leaves the
            // async void method sending the request would end the process.
            var failed = false;
            foreach (var shortcut in Descendants(ui.Window).OfType<Terminal.Gui.Views.Shortcut>())
            {
                shortcut.TitleChanged += (_, e) =>
                {
                    if (!failed && e.Value.Contains("→ 200", StringComparison.Ordinal))
                    {
                        failed = true;
                        throw new InvalidOperationException("The result could not be shown.");
                    }
                };
            }

            var goesOn = false;
            var screens = Drive(ui, () => ui.App.InjectKey(Key.F5),
                ("Unexpected error", () => ui.Click("► OK ◄")),
                ("200 OK", () =>
                {
                    goesOn = ui.Window.IsRunning && !ui.Window.IsSending;
                    ui.App.RequestStop();
                }));

            Assert.Equal(2, screens.Count);
            OnScreen("The result could not be shown.", screens[0]);
            Assert.True(goesOn);
            if (!canLog)
            {
                Assert.DoesNotContain("Details were", screens[0], StringComparison.Ordinal);
                return;
            }

            OnScreen("Details were", screens[0]);
            Assert.Contains("System.InvalidOperationException: The result could not be shown.", File.ReadAllText(Path.Combine(state, "errors.log")),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TermRest.Core.History.HistoryStore.StateDirectoryVariable, previousState);
        }
    }

    [Fact]
    public void The_ui_runs_at_full_pace_only_while_it_is_in_use()
    {
        using var ui = new Harness(_directory);
        var idle = Application.MaximumIterationsPerSecond;
        var busy = false;
        var seen = new List<(bool Active, ushort Rate)>();
        using var pace = new UiPace(ui.App, () => busy) { ActiveFor = TimeSpan.FromMilliseconds(300) };
        void Note() => seen.Add((pace.IsActive, Application.MaximumIterationsPerSecond));
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            Note();
            ui.Click("Collections");
            Note();
            busy = true; // a request is on its way
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(900), () =>
        {
            Note();
            busy = false;
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(1700), () =>
        {
            Note();
            ui.App.RequestStop();
            return false;
        });

        ui.App.Run(ui.Window);

        (bool, ushort)[] expected = [(false, idle), (true, TuiApp.IterationsPerSecond), (true, TuiApp.IterationsPerSecond), (false, idle)];
        Assert.Equal(expected, seen);
    }

    /// <summary>
    /// Runs <paramref name="act"/> from the main loop while <paramref name="steps"/> answer the dialogs it opens: each
    /// step waits until its text is on screen (and no dialog is closing), then acts; the screens they saw are returned
    /// (fewer than the steps when one did not come). The next step is scheduled before a step acts, as an action that
    /// opens a dialog returns only once that is closed. After 20 s whatever is open is closed, without unsaved changes,
    /// so a test fails instead of hanging.
    /// </summary>
    private static List<string> Drive(Harness ui, Action act, params (string WaitFor, Action Act)[] steps)
    {
        var screens = new List<string>();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        void Next(int index)
        {
            if (index == steps.Length)
            {
                return;
            }

            ui.App.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
            {
                var screen = ui.Screen();
                if (ui.App.TopRunnable is { StopRequested: true } || !screen.Contains(steps[index].WaitFor, StringComparison.Ordinal))
                {
                    return DateTime.UtcNow < deadline;
                }

                screens.Add(screen);
                Next(index + 1);
                steps[index].Act();
                return false;
            });
        }

        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            Next(0);
            act();
            return false;
        });
        ui.App.AddTimeout(TimeSpan.FromSeconds(1), () =>
        {
            if (DateTime.UtcNow > deadline)
            {
                if (ui.App.TopRunnableView == ui.Window)
                {
                    ui.Window.Editor.MarkClean();
                    ui.App.InjectKey(Key.S.WithCtrl);
                }

                ui.App.RequestStop();
            }

            return true;
        });

        ui.App.Run(ui.Window);
        return screens;
    }

    [Fact]
    public async Task Save_as_example_keeps_the_response_under_its_request_where_it_opens_for_editing()
    {
        await using var server = await LocalEchoServer.StartAsync();
        using var ui = new Harness(_directory, runner => runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo");
        var offered = false;
        var screens = Drive(ui, () => ui.App.InjectKey(Key.F5),
            ("200 OK", () =>
            {
                offered = ui.Window.ResponsePane.OffersSaveAsExample;
                ui.Window.SaveResponseAsExample("Echoed");
                ui.App.InjectKey(Key.S.WithCtrl); // saved: stopping asks nothing
                ui.App.RequestStop();
            }));
        Assert.Single(screens);
        Assert.True(offered);
        OnScreen("Save as example", screens[0]);

        var collection = ui.Workspace.Collections[0];
        var request = collection.FindRequest("Basics/GET with query")!;
        var example = Assert.Single(collection.GetExamples(request));
        var spec = example.ReadSpec();
        Assert.Equal(("Echoed", 200, "OK", "json"), (spec.Name, spec.Code, spec.Status, spec.Language));
        Assert.Contains("/echo/get", spec.Body, StringComparison.Ordinal);
        Assert.Equal("{{baseUrl}}/get?foo=bar&source=termrest", spec.Request.Url);

        var token = ui.App.Begin(ui.Window);
        ui.Click("e.g. Echoed");
        var screen = ui.Screen();
        OnScreen("Example — TermRest Demo / Basics/GET with query / Echoed", screen);
        OnScreen("│Params│Headers│Body│", screen);
        OnScreen("Example response", screen);
        Assert.True(ui.Window.Editor.ExampleMode);

        ui.Window.ExampleEditor.CodeField.Text = "201";
        ui.App.InjectKey(Key.S.WithCtrl);
        ui.App.End(token!);

        var saved = PostmanCollection.Load(collection.FilePath!);
        var written = Assert.Single(saved.GetExamples(saved.FindRequest("Basics/GET with query")!)).ReadSpec();
        Assert.Equal(("Echoed", 201, "OK"), (written.Name, written.Code, written.Status));
        Assert.Equal(spec.Body, written.Body);
    }

    [Fact]
    public void An_example_is_not_sent_and_its_edits_are_saved_until_a_request_is_opened()
    {
        var file = Path.Combine(_directory, "TermRest Demo.postman_collection.json");
        var demo = PostmanCollection.Load(file);
        var post = demo.FindRequest("Basics/POST JSON")!;
        demo.AddExample(post, new ExampleSpec { Name = "Created", Request = post.ReadSpec(), Code = 201, Status = "Created", Body = "{}", Language = "json" });
        demo.Save();

        using var ui = new Harness(_directory);
        var collection = ui.Workspace.Collections[0];
        var request = collection.FindRequest("Basics/POST JSON")!;
        var token = ui.App.Begin(ui.Window);
        ui.Window.ShowExample(Assert.Single(collection.GetExamples(request)));

        ui.App.InjectKey(Key.F5);
        var notice = ui.Window.StatusMessage;
        ui.Window.ExampleEditor.BodyArea.SetFocus();
        ui.Type("[");
        ui.App.InjectKey(Key.S.WithCtrl);
        ui.Window.ShowRequest(request);
        var screen = ui.Screen();
        ui.App.End(token!);

        Assert.Contains("An example is not sent", notice, StringComparison.Ordinal);
        Assert.False(ui.Window.Editor.ExampleMode);
        OnScreen("│Params│Headers│Auth│Body│Scripts│Settings│", screen);
        OnScreen("Request — TermRest Demo / Basics/POST JSON", screen);
        var saved = PostmanCollection.Load(file);
        Assert.Equal("[{}", Assert.Single(saved.GetExamples(saved.FindRequest("Basics/POST JSON")!)).ReadSpec().Body);
    }

    [Fact]
    public void Space_and_clicking_the_checkbox_column_turn_table_rows_on_and_off()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        var debug = ui.Find("debug"); // the disabled query parameter; foo is two rows above it

        ui.Click("debug");
        ui.App.InjectKey(Key.Space);
        var afterSpace = ui.Window.Editor.Spec.Clone();
        ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(debug.X - 2, debug.Y - 2)));
        var afterClick = ui.Window.Editor.Spec.Clone();
        var screen = ui.Screen();
        ui.App.InjectKey(Key.S.WithCtrl);
        ui.App.End(token!);

        Assert.True(afterSpace.QueryParams.Single(q => q.Key == "debug").Enabled);
        Assert.Equal("{{baseUrl}}/get?foo=bar&source=termrest&debug=1", afterSpace.Url);
        Assert.False(afterClick.QueryParams.Single(q => q.Key == "foo").Enabled);
        Assert.Equal("{{baseUrl}}/get?source=termrest&debug=1", afterClick.Url);
        OnScreen("☐│foo", screen);
        OnScreen("☑│debug", screen);
        var saved = PostmanCollection.Load(Path.Combine(_directory, "TermRest Demo.postman_collection.json")).FindRequest("Basics/GET with query")!.ReadSpec();
        Assert.Equal([false, true, true], saved.QueryParams.Select(q => q.Enabled));
    }

    [Fact]
    public void The_history_filter_stays_quick_with_thousands_of_entries()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        var entries = Enumerable.Range(0, 5_000).Select(i => new TermRest.Core.History.HistoryEntry
        {
            Id = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Time = DateTimeOffset.Now,
            Method = "GET",
            Url = $"https://api.example.com/items/{i}",
            Item = new System.Text.Json.Nodes.JsonObject { ["name"] = $"Item {i}" },
            Status = 200,
        }).ToList();
        ui.App.InjectKey(Key.F9);
        var filter = ui.Find("Filter:");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        ui.Window.HistoryPane.SetEntries(entries, "No requests sent yet.");
        ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(filter.X + 9, filter.Y)));
        ui.Type("/49");
        watch.Stop();
        var rows = ui.Window.HistoryPane.Rows.ToList();
        var screen = ui.Screen();
        ui.App.End(token!);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Filtering took {watch.Elapsed}."); // 0.3 s here; 96 s when each row was added on its own
        Assert.Equal(111, rows.Count); // items/49, items/490…499 and items/4900…4999
        Assert.EndsWith("200 GET    /items/49", rows[0], StringComparison.Ordinal);
        OnScreen("200 GET    /items/49 ", screen);
    }

    [Fact]
    public void Deleting_words_and_cutting_lines_in_a_text_editor_change_the_request()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        ui.Click("POST POST JSON");
        ui.Click("Body");
        ui.Click("Ada Lovelace");
        var editor = Assert.IsType<Views.TextArea>(ui.App.Navigation!.GetFocused());
        var bodies = new List<(string Editor, string Request)>();
        foreach (var key in (Key[])[Key.Delete.WithCtrl, Key.K.WithCtrl, Key.Backspace.WithCtrl, Key.Backspace.WithCtrl.WithShift])
        {
            ui.App.InjectKey(key);
            bodies.Add((editor.GetText(), ui.Window.Editor.Spec.Body.Raw));
        }

        var modified = ui.Window.Editor.IsModified;
        ui.App.InjectKey(Key.S.WithCtrl);
        ui.App.End(token!);

        Assert.Equal(4, bodies.Select(b => b.Editor).Distinct().Count()); // each key changed the text
        Assert.All(bodies, body => Assert.Equal(body.Editor, body.Request));
        Assert.True(modified);
        var saved = PostmanCollection.Load(Path.Combine(_directory, "TermRest Demo.postman_collection.json")).FindRequest("Basics/POST JSON")!.ReadSpec();
        Assert.Equal(bodies[^1].Editor, saved.Body.Raw);
    }

    [Fact]
    public void Ctrl_L_and_Ctrl_N_are_left_to_the_window_in_text_editors()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        var dialogs = new List<string>();
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            // Runs only while a dialog is open: notes what it shows and closes it.
            dialogs.Add(ui.Screen());
            ui.App.InjectKey(Key.Esc);
            return true;
        });
        ui.Click("POST POST JSON");
        ui.Click("Body");
        ui.Click("Ada Lovelace");
        var editor = Assert.IsType<Views.TextArea>(ui.App.Navigation!.GetFocused());

        var ctrlLHandled = editor.NewKeyDownEvent(Key.L.WithCtrl);
        ui.App.InjectKey(Key.N.WithCtrl);
        editor.SetFocus();
        ui.App.InjectKey(Key.L.WithCtrl);
        var focused = ui.App.Navigation!.GetFocused();
        ui.App.End(token!);

        Assert.False(ctrlLHandled); // left to the window (Focus URL bar), not TextView's color picker
        OnScreen("Name (in TermRest Demo / Basics)", Assert.Single(dialogs)); // New request, not the cursor moving down
        Assert.Equal("UrlField", focused?.GetType().Name);
    }

    [Fact]
    public void Auth_tokens_and_key_values_are_masked_like_passwords()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        ui.Click("POST POST JSON");
        ui.Click("Auth");
        var shown = new List<string>();
        (string Type, string Field, string Secret)[] fields = [("Bearer Token", "Token", "bearer-s3cret"), ("API Key", "Value", "apikey-s3cret"), ("OAuth 2.0", "Access token", "oauth-s3cret")];
        foreach (var (type, field, secret) in fields)
        {
            ui.Click(type);
            var label = ui.Find(field);
            ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(label.X + 17, label.Y)));
            ui.Type(secret);
            shown.Add(ui.Screen());
        }

        var auth = ui.Window.Editor.Spec.Auth;
        ui.App.InjectKey(Key.S.WithCtrl);
        ui.App.End(token!);

        Assert.All(shown, screen => Assert.DoesNotContain("s3cret", screen, StringComparison.Ordinal));
        Assert.Equal(("oauth2", "oauth-s3cret"), (auth?.Type, auth?.Get("accessToken")));
    }

    /// <summary>OAuth 2.0 auth that fetches its token from <paramref name="server"/> (as a confidential client, unless the secret is set empty).</summary>
    private static AuthSpec OAuth2(OAuthServer server, string grant, params (string Key, string Value)[] settings)
    {
        var auth = new AuthSpec { Type = "oauth2" };
        foreach (var (key, value) in (ReadOnlySpan<(string, string)>)[("grant_type", grant), ("accessTokenUrl", server.Url("token")), ("clientId", OAuthServer.ClientId),
                     ("clientSecret", OAuthServer.ClientSecret), .. settings])
        {
            auth.Set(key, value);
        }

        return auth;
    }

    private static string? SentAuthorization(TermRest.Core.Http.HttpExchange? exchange) =>
        System.Text.Json.Nodes.JsonNode.Parse(exchange!.Response!.Body)!["authorization"]!.GetValue<string>();

    [Fact]
    public void Choosing_another_oauth2_grant_shows_the_settings_it_takes()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        ui.Click("POST POST JSON");
        ui.Click("Auth");
        ui.Click("OAuth 2.0");
        var before = ui.Screen();
        ui.Click("Client credentials"); // opens the list of grants
        ui.App.InjectKey(Key.CursorDown);
        ui.App.InjectKey(Key.Enter);
        ui.App.TimedEvents?.RunTimers(); // the fields are built again once the dropdown's own event is over
        var after = ui.Screen();
        var auth = ui.Window.Editor.Spec.Auth;
        var labels = Descendants(ui.Window.Editor).OfType<Terminal.Gui.Views.Label>().Select(l => l.Text).ToList();
        var focused = ui.App.Navigation?.GetFocused();
        ui.App.InjectKey(Key.S.WithCtrl);
        ui.App.End(token!);

        OnScreen("⟦ Get new token ⟧", before);
        OnScreen("Client credentials", before);
        Assert.Equal(("oauth2", "password_credentials"), (auth?.Type, auth?.Get("grant_type")));
        OnScreen("Password credentials", after);
        Assert.Contains("Username", labels);
        Assert.Contains("Password", labels);

        // The new dropdown has the focus the old one had.
        Assert.NotNull(focused);
        Assert.Contains(focused, Descendants(ui.Window.Editor).OfType<Terminal.Gui.Views.DropDownList>().SelectMany(Descendants));
    }

    [Fact]
    public void The_oauth2_settings_scroll_with_the_wheel_over_their_fields()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        ui.Window.Editor.Load(new HttpRequestSpec
        {
            Url = "https://api.example.com/me",
            Auth = new AuthSpec { Type = "oauth2", Params = [new AuthParam { Key = "grant_type", Value = "authorization_code_with_pkce" }] },
        });
        ui.Click("Auth");
        var top = ui.Screen();
        var field = ui.Find("Header prefix");
        for (var i = 0; i < 30; i++)
        {
            ui.App.InjectMouse(new Terminal.Gui.Input.Mouse { ScreenPosition = field with { X = field.X + 20 }, Flags = Terminal.Gui.Input.MouseFlags.WheeledDown });
        }

        var bottom = ui.Screen();
        var auth = ui.Window.Editor.Spec.Auth;
        ui.App.End(token!);

        OnScreen("⟦ Get new token ⟧", top);
        Assert.DoesNotContain("Fields may use {{variables}}.", top, StringComparison.Ordinal);
        Assert.DoesNotContain("⟦ Get new token ⟧", bottom, StringComparison.Ordinal);
        OnScreen("Fields may use {{variables}}.", bottom);
        Assert.Equal("authorization_code_with_pkce", auth?.Get("grant_type")); // the wheel over the grant's dropdown changed nothing
    }

    [Fact]
    public async Task Get_new_token_fetches_an_oauth2_token_and_forget_token_has_the_request_fetch_another()
    {
        await using var server = await OAuthServer.StartAsync();
        using var ui = new Harness(_directory);
        string before = "", fetched = "", forgotten = "", status = "";
        TermRest.Core.Http.HttpExchange? sent = null;

        Drive(ui, () =>
            {
                ui.Window.Editor.Load(new HttpRequestSpec { Url = server.Url("echo"), Auth = OAuth2(server, "client_credentials") });
                ui.Click("Auth");
                before = ui.Window.Editor.TokenStatus;
                ui.Click("Get new token");
            },
            ("Fetched ", () =>
            {
                fetched = ui.Window.Editor.TokenStatus;
                status = ui.Window.StatusMessage;
                ui.Click("Forget token");
                forgotten = ui.Window.Editor.TokenStatus;
                ui.App.InjectKey(Key.F5);
            }),
            ("200 OK", () =>
            {
                sent = ui.Window.ResponsePane.Exchange;
                ui.Window.Editor.MarkClean();
                ui.App.RequestStop();
            }));

        Assert.Equal("None yet: fetched when sent.", before);
        Assert.Matches(@"^Fetched \d\d:\d\d:\d\d, expires in 60 min\.$", fetched);
        Assert.StartsWith($"✓ New OAuth 2.0 token from {server.Url("token")} (200 in ", status, StringComparison.Ordinal);
        Assert.Equal("None yet: fetched when sent.", forgotten);
        Assert.Equal(2, server.AccessTokens.Count);
        Assert.Equal($"Bearer {server.AccessTokens.Last()}", SentAuthorization(sent));
        Assert.StartsWith("OAuth 2.0, client credentials: new token from", sent!.Request.AuthDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signing_in_waits_in_a_dialog_that_closes_by_itself_when_the_browser_returns()
    {
        await using var server = await OAuthServer.StartAsync();
        using var ui = new Harness(_directory);
        using var browser = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? returning = null;

        // The user signs in once the dialog is up: the server sends the browser back to TermRest's loopback port.
        ui.Runner.OAuth2Interaction = new SignInDialog(() => ui.App, _ => { }, url =>
        {
            returning = Task.Run(async () =>
            {
                await seen.Task;
                var location = (await browser.GetAsync(url, TestContext.Current.CancellationToken)).Headers.Location;
                await browser.GetAsync(location, TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);
            return true;
        });
        TermRest.Core.Http.HttpExchange? sent = null;

        var screens = Drive(ui, () =>
            {
                ui.Window.Editor.Load(new HttpRequestSpec
                {
                    Url = server.Url("echo"),
                    Auth = OAuth2(server, "authorization_code_with_pkce", ("authUrl", server.Url("authorize")), ("clientSecret", "")),
                });
                ui.App.InjectKey(Key.F5);
            },
            ("Waiting for the browser to return to http://127.0.0.1:", () => seen.SetResult()),
            ("200 OK", () =>
            {
                sent = ui.Window.ResponsePane.Exchange;
                ui.Window.Editor.MarkClean();
                ui.App.RequestStop();
            }));

        await returning!;
        OnScreen("Sign in to localhost in the browser that opened, or at this address:", screens[0]);
        Assert.DoesNotContain("Sign in (OAuth 2.0)", screens[1], StringComparison.Ordinal);
        Assert.Equal($"Bearer {Assert.Single(server.AccessTokens)}", SentAuthorization(sent));
    }

    [Fact]
    public async Task The_address_the_browser_ended_on_is_pasted_into_the_sign_in_dialog()
    {
        await using var server = await OAuthServer.StartAsync();
        using var ui = new Harness(_directory);
        using var browser = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        string? ended = null;
        Task? visiting = null;

        // No browser here: the user signs in elsewhere, and copies the address of the callback page it ends on.
        ui.Runner.OAuth2Interaction = new SignInDialog(() => ui.App, _ => { }, url =>
        {
            visiting = Task.Run(async () => Volatile.Write(ref ended, (await browser.GetAsync(url, TestContext.Current.CancellationToken)).Headers.Location!.ToString()),
                TestContext.Current.CancellationToken);
            return false;
        });
        TermRest.Core.Http.HttpExchange? sent = null;

        var screens = Drive(ui, () =>
            {
                ui.Window.Editor.Load(new HttpRequestSpec
                {
                    Url = server.Url("echo"),
                    Auth = OAuth2(server, "authorization_code", ("authUrl", server.Url("authorize")), ("redirect_uri", "https://oauth.pstmn.io/v1/callback")),
                });
                ui.App.InjectKey(Key.F5);
            },
            ("Paste the address it ends on", () =>
            {
                SpinWait.SpinUntil(() => Volatile.Read(ref ended) is not null, TimeSpan.FromSeconds(10));
                ui.Type(ended!);
                ui.App.InjectKey(Key.Enter);
            }),
            ("200 OK", () =>
            {
                sent = ui.Window.ResponsePane.Exchange;
                ui.Window.Editor.MarkClean();
                ui.App.RequestStop();
            }));

        await visiting!;
        OnScreen("Open this address in a browser and sign in to localhost:", screens[0]);
        OnScreen("Once you are signed in, the browser goes to https://oauth.pstmn.io/v1/callback.", screens[0]);
        Assert.Equal($"Bearer {Assert.Single(server.AccessTokens)}", SentAuthorization(sent));
    }

    [Fact]
    public void Enter_on_an_environment_value_edits_it_and_the_dialog_stays_open()
    {
        using var ui = new Harness(_directory);

        var screens = Drive(ui, () =>
            {
                EnvironmentsDialog.Show(ui.App, ui.Workspace);
                ui.App.RequestStop();
            },
            ("Active environment:", () =>
            {
                ui.Click("disabled values are ignored"); // a value only this dialog shows
                ui.App.InjectKey(Key.Enter); // returns once the row's editor is closed
            }),
            ("Edit variable", () => ui.App.InjectKey(Key.Esc)),
            ("Active environment:", () => ui.Click("Close")));

        Assert.Equal(3, screens.Count); // the Environments dialog was still open to close
    }

    [Fact]
    public void Editing_a_row_keeps_what_was_left_alone_whole()
    {
        const string Pem = "-----BEGIN PRIVATE KEY-----\nMIIB\n-----END PRIVATE KEY-----";
        var setup = ApiWorkspace.Open(_directory);
        var echo = Assert.Single(setup.Environments);
        echo.SetValues([.. echo.Values, new KeyValueEntry { Key = "client_key", Value = Pem, Description = "PEM\nPKCS#8", Type = "default" }]);
        echo.Save();
        using var ui = new Harness(_directory);
        KeyValueEntry? flag = null;

        var screens = Drive(ui, () =>
            {
                EnvironmentsDialog.Show(ui.App, ui.Workspace);
                flag = KeyValueEntryDialog.Show(ui.App, "Edit query parameter", new KeyValueEntry { Key = "flag", Value = null }, Views.KeyValueKind.QueryParams);
                ui.App.RequestStop();
            },
            ("client_key", () => ui.App.InjectSequence(InputInjectionExtensions.LeftButtonDoubleClick(ui.Find("client_key")))),
            ("Edit variable", () =>
            {
                ui.Click("mask value");
                ui.Click("► OK ◄");
            }),
            ("Active environment:", () => ui.Click("Close")),
            ("Edit query parameter", () => ui.App.InjectKey(Key.Enter)));

        Assert.Equal(4, screens.Count);
        OnScreen("MIIB", screens[1]); // all of it, to edit
        var saved = Assert.Single(ApiWorkspace.Open(_directory).Environments).Values.Single(v => v.Key == "client_key");
        Assert.Equal((Pem, "PEM\nPKCS#8", true), (saved.Value, saved.Description, saved.IsSecret));
        Assert.Equal(("flag", null), (flag?.Key, flag?.Value)); // not ?flag=
    }

    [Fact]
    public void Bulk_edit_applied_without_a_change_changes_no_row()
    {
        // Terminal.Gui keeps each character composed (NFC): the bulk text read back is not the text it was given.
        const string Decomposed = "Cafe\u0301 \u2126";
        var setup = PostmanCollection.Load(Path.Combine(_directory, "TermRest Demo.postman_collection.json"));
        var request = setup.FindRequest("Basics/POST JSON")!;
        var spec = request.ReadSpec();
        spec.Headers.Add(new KeyValueEntry { Key = "X-Note", Value = Decomposed });
        setup.UpdateRequest(request, spec);
        setup.Save();
        using var ui = new Harness(_directory);
        bool blankLine = true, edited = false;
        (bool Modified, string? Value) unchanged = default;
        var headers = new List<string>();

        var screens = Drive(ui, () =>
            {
                ui.Click("GET  GET with query");
                ui.Click("Bulk edit");
                blankLine = ui.Window.Editor.IsModified;
                ui.Click("POST POST JSON");
                ui.Click("Headers");
                ui.Click("Bulk edit");
                unchanged = (ui.Window.Editor.IsModified, ui.Window.Editor.Spec.Headers.Single(h => h.Key == "X-Note").Value);
                ui.Click("Bulk edit");
                (edited, headers) = (ui.Window.Editor.IsModified, ui.Window.Editor.Spec.Headers.Select(h => $"{h.Key}:{h.Value}").ToList());
                ui.App.InjectKey(Key.S.WithCtrl);
                ui.App.RequestStop();
            },
            ("Bulk edit query parameters", () =>
            {
                ui.App.InjectKey(Key.End.WithCtrl);
                ui.App.InjectKey(Key.Enter); // a blank line: the same rows
                ui.Click("► Apply ◄");
            }),
            ("Bulk edit headers", () => ui.Click("► Apply ◄")),
            ("Bulk edit headers", () =>
            {
                ui.App.InjectKey(Key.End.WithCtrl);
                ui.App.InjectKey(Key.Enter);
                ui.Type("X-New:1");
                ui.Click("► Apply ◄");
            }));

        Assert.Equal(3, screens.Count);
        Assert.False(blankLine);
        Assert.Equal((false, Decomposed), unchanged);
        Assert.True(edited);
        Assert.Contains("X-New:1", headers);
    }

    [Fact]
    public async Task The_tree_s_context_menu_sends_the_request_right_clicked()
    {
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.ConfigureHistory(state);
        });
        await ui.Runner.SendAsync(PostmanCollection.Create("Scratch"), null, new HttpRequestSpec { Method = "DELETE", Url = server.BaseUrl + "/echo/users/42" }, null,
            TestContext.Current.CancellationToken);

        var screens = Drive(ui, () =>
            {
                ui.App.InjectKey(Key.F9);
                ui.Click("/echo/users/42"); // GET with query stays selected in the tree
                ui.App.InjectKey(Key.F9);
                ui.App.InjectSequence(InputInjectionExtensions.RightButtonClick(ui.Find("GET  GET with query")));
                var copy = ui.Find("Copy as cURL");
                ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(copy.X, copy.Y - 1))); // Send
            },
            ("200 OK", () => ui.App.RequestStop()));

        Assert.Single(screens);
        Assert.Equal("{{baseUrl}}/get?foo=bar&source=termrest", ui.Window.Editor.Spec.Url);
        Assert.Equal(["/echo/get?foo=bar&source=termrest", "/echo/users/42"], ui.Runner.History!.Load().Select(e => new Uri(e.Url).PathAndQuery));
    }

    [Fact]
    public void Copy_as_curl_says_why_when_curl_cannot_send_the_request_the_same_way()
    {
        var path = Path.Combine(_directory, "TermRest Demo.postman_collection.json");
        var setup = PostmanCollection.Load(path);
        var node = setup.FindRequest("Basics/GET with query")!;
        var spec = node.ReadSpec();
        spec.Method = "POST";
        spec.Body = new RequestBody { Mode = BodyMode.FormData, FormData = [new() { Key = "doc=</etc/passwd;", Value = "1" }] };
        setup.UpdateRequest(node, spec);
        setup.Save();
        using var ui = new Harness(_directory, runner => runner.LocalVariables["baseUrl"] = "https://api.example.com");
        ui.App.Driver!.Clipboard = new FakeClipboard();

        var screens = Drive(ui, () =>
            {
                ui.App.InjectSequence(InputInjectionExtensions.RightButtonClick(ui.Find("POST GET with query")));
                ui.Click("Copy as cURL");
                ui.App.RequestStop();
            },
            ("cannot be sent with curl", () => ui.Click("► OK ◄")));

        Assert.Single(screens);
        OnScreen("'doc=</etc/passwd;'", screens[0]);
        Assert.DoesNotContain("Unexpected error", screens[0], StringComparison.Ordinal);
        Assert.DoesNotContain("copied", ui.Window.StatusMessage ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void Copy_as_curl_copies_no_command_for_a_request_TermRest_would_not_send()
    {
        // Its body file is not on this machine: TermRest sends nothing, and a command would replace the resource with nothing.
        var path = Path.Combine(_directory, "TermRest Demo.postman_collection.json");
        var setup = PostmanCollection.Load(path);
        var node = setup.FindRequest("Basics/GET with query")!;
        var spec = node.ReadSpec();
        spec.Method = "PUT";
        spec.Body = new RequestBody { Mode = BodyMode.File, FileSrc = "fixtures/missing-user.json" };
        setup.UpdateRequest(node, spec);
        setup.Save();
        using var ui = new Harness(_directory, runner => runner.LocalVariables["baseUrl"] = "https://api.example.com");
        ui.App.Driver!.Clipboard = new FakeClipboard();
        var message = "";

        var screens = Drive(ui, () =>
            {
                ui.App.InjectSequence(InputInjectionExtensions.RightButtonClick(ui.Find("PUT  GET with query")));
                ui.Click("Copy as cURL");
                ui.App.RequestStop();
            },
            ("► OK ◄", () =>
            {
                message = ui.App.TopRunnableView!.Text;
                ui.Click("► OK ◄");
            }));

        Assert.Single(screens);
        Assert.Matches(@"^Body file '.*missing-user\.json' does not exist\.$", message);
        Assert.DoesNotContain("copied", ui.Window.StatusMessage ?? "", StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(ui.App.Clipboard!.GetClipboardData()));
    }

    [Fact]
    public void A_request_typed_where_a_deleted_one_was_is_a_scratch_request_that_reloads_leave_alone()
    {
        const string Typed = "https://typed.example.com/items";
        var zoo = Path.Combine(_directory, "Zoo.postman_collection.json"); // after TermRest Demo, whose first request is open
        File.WriteAllText(zoo, """
            {
              "info": { "name": "Zoo", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{ "name": "Health", "request": { "method": "GET", "url": "https://zoo.example.com/health" } }]
            }
            """);
        using var ui = new Harness(_directory);
        ui.App.Driver!.Clipboard = new FakeClipboard();
        string copied = "", url = "", status = "";
        var modified = false;

        var screens = Drive(ui, () =>
            {
                ui.Click("Request");
                ui.Click("Delete…"); // GET with query, open in the editor, which it empties
                status = ui.Window.Editor.ScriptsTab.Status;
                ui.App.InjectKey(Key.L.WithCtrl);
                ui.Type(Typed);
                ui.Click("Request");
                ui.Click("Copy as cURL");
                copied = ui.App.Clipboard!.GetClipboardData() ?? "";
                ui.Window.ImportCollectionFrom(zoo); // reloads Zoo
                (url, modified) = (ui.Window.Editor.Spec.Url, ui.Window.Editor.IsModified);
                ui.Window.Editor.MarkClean();
                ui.App.RequestStop();
            },
            ("Delete request 'GET with query'?", () => ui.Click("► Delete ◄")),
            ("Unsaved changes", () => ui.Click("Don't save")));

        Assert.Equal(2, screens.Count);
        Assert.Equal("→ runs no scripts", status);
        Assert.Contains(Typed, copied, StringComparison.Ordinal);
        Assert.Equal((Typed, true), (url, modified));
    }

    [Fact]
    public async Task A_curl_command_pasted_into_a_request_from_history_is_sent_on_its_own()
    {
        // Not as the request the entry was sent from: its token, and its script that adds another, are not meant for the
        // host the command names.
        var path = Path.Combine(_directory, "TermRest Demo.postman_collection.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"value\": \"{{token}}\"", "\"value\": \"folder-s3cret\"", StringComparison.Ordinal)); // the Auth folder's
        var setup = PostmanCollection.Load(path);
        var node = setup.FindRequest("Auth/Auth inherited from folder")!;
        var withScript = node.ReadSpec();
        withScript.Scripts = new ScriptSet("tr.request.headers.set('X-Signature', 'request-s3cret')", "", ScriptLanguage.Python);
        setup.UpdateRequest(node, withScript);
        setup.Save();
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.ConfigureHistory(state);
        });
        var collection = Assert.Single(ui.Workspace.Collections);
        var request = collection.FindRequest("Auth/Auth inherited from folder")!;
        await ui.Runner.SendAsync(collection, request, request.ReadSpec(), TestContext.Current.CancellationToken);
        ScriptSet? historyScripts = null, pastedScripts = null;

        var screens = Drive(ui, () =>
            {
                ui.App.InjectKey(Key.F9);
                ui.Click("/echo/headers"); // its history entry, which takes its auth from the folder
                historyScripts = ui.Window.Editor.Spec.Scripts;
                ui.Click("{{baseUrl}}/headers");
                ui.App.RaisePasteEvent($"curl '{server.BaseUrl}/echo/pasted'");
                pastedScripts = ui.Window.Editor.Spec.Scripts;
                ui.App.InjectKey(Key.F5);
            },
            ("200 OK", () => ui.App.RequestStop()),
            ("Unsaved request", () => ui.Click("Discard")));

        Assert.Equal(2, screens.Count);
        OnScreen("Request (imported)", screens[0]);
        Assert.True(historyScripts!.Has(ScriptPhase.PreRequest));
        Assert.True(pastedScripts!.IsEmpty);
        var sent = ui.Window.ResponsePane.Exchange!.Request;
        Assert.EndsWith("/echo/pasted", sent.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(sent.Headers, h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_curl_command_pasted_into_a_request_from_history_on_its_way_leaves_that_request_to_arrive_on_its_own()
    {
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.ConfigureHistory(state);
        });
        var collection = Assert.Single(ui.Workspace.Collections);
        var request = collection.FindRequest("Basics/GET with query")!;
        await ui.Runner.SendAsync(collection, request, request.ReadSpec(), TestContext.Current.CancellationToken);
        ui.Runner.LocalVariables["baseUrl"] = server.BaseUrl + "/hold"; // sent again, it waits
        var sending = true;
        string arrived = "", pane = "";

        var screens = Drive(ui, () =>
            {
                ui.App.InjectKey(Key.F9);
                ui.Click("/echo/get");
                ui.App.InjectKey(Key.F5);
            },
            ("Sending GET", () =>
            {
                ui.Click("{{baseUrl}}/get?foo=bar&source=termrest");
                ui.App.RaisePasteEvent($"curl '{server.BaseUrl}/echo/pasted'");
                sending = ui.Window.IsSending; // F5 would cancel that request, not send this one
                pane = ui.Window.ResponsePane.StatusText;
                server.Release();
            }),
            ("History (2)", () =>
            {
                arrived = ui.Window.StatusMessage;
                ui.App.RequestStop();
            }),
            ("Unsaved request", () => ui.Click("Discard")));

        Assert.Equal(3, screens.Count);
        Assert.False(sending);
        Assert.StartsWith("No response yet", pane, StringComparison.Ordinal); // not "Sending…" for a request it no longer shows
        Assert.StartsWith("Earlier request: GET 127.0.0.1 → 200", arrived, StringComparison.Ordinal); // not under the pasted one
    }

    [Fact]
    public void A_collection_that_no_longer_loads_is_not_reloaded_and_the_editor_goes_on_editing_its_request()
    {
        var path = Path.Combine(_directory, "TermRest Demo.postman_collection.json");
        using var ui = new Harness(_directory);
        var edited = "";

        var screens = Drive(ui, () =>
            {
                File.WriteAllText(path, "<<<<<<< HEAD\n" + File.ReadAllText(path) + "\n=======\n>>>>>>> theirs\n");
                ui.Window.ImportCollectionFrom(path); // reloads it, and fails
                ui.Click("{{baseUrl}}/get?foo=bar&source=termrest");
                ui.App.InjectKey(Key.End);
                ui.Type("&kept=1");
                edited = ui.Screen();
                ui.Click("POST POST JSON");
                ui.App.RequestStop();
            },
            ("Import failed", () => ui.Click("► OK ◄")),
            ("Unsaved changes", () => ui.Click("Don't save")));

        Assert.Equal(2, screens.Count);
        OnScreen("TermRest Demo / Basics/GET with query ●", edited);
        Assert.EndsWith("&kept=1", Assert.Single(ui.Workspace.Collections).FindRequest("Basics/GET with query")!.ReadSpec().Url, StringComparison.Ordinal);
        Assert.Equal("{{baseUrl}}/post", ui.Window.Editor.Spec.Url);
    }

    [Fact]
    public async Task Clicking_the_history_entry_being_sent_again_leaves_its_response_to_come_there()
    {
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.ConfigureHistory(state);
        });
        var collection = Assert.Single(ui.Workspace.Collections);
        var request = collection.FindRequest("Basics/GET with query")!;
        await ui.Runner.SendAsync(collection, request, request.ReadSpec(), TestContext.Current.CancellationToken);
        ui.Runner.LocalVariables["baseUrl"] = server.BaseUrl + "/hold"; // sent again, it waits
        var sending = false;
        var status = "";

        var screens = Drive(ui, () =>
            {
                ui.App.InjectKey(Key.F9);
                ui.Click("/echo/get");
                ui.App.InjectKey(Key.F5);
            },
            ("Sending GET", () =>
            {
                ui.Click("/echo/get"); // its row, still the selected one
                sending = ui.Window.IsSending;
                server.Release();
            }),
            ("History (2)", () =>
            {
                status = ui.Window.StatusMessage;
                ui.App.RequestStop();
            }));

        Assert.Equal(2, screens.Count);
        Assert.True(sending);
        Assert.StartsWith("GET 127.0.0.1 → 200", status, StringComparison.Ordinal);
        Assert.Equal("/hold/get", System.Text.Json.Nodes.JsonNode.Parse(ui.Window.ResponsePane.Exchange!.Response!.Body)!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_history_request_with_masked_credentials_goes_into_a_collection_only_when_confirmed()
    {
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner => runner.ConfigureHistory(state));
        var auth = new AuthSpec { Type = "basic" };
        auth.Set("username", "alice");
        auth.Set("password", "typed-s3cret");
        await ui.Runner.SendAsync(PostmanCollection.Create("Scratch"), null, new HttpRequestSpec { Url = server.BaseUrl + "/echo/masked", Auth = auth }, null,
            TestContext.Current.CancellationToken);
        var collection = Assert.Single(ui.Workspace.Collections);
        var requests = collection.AllRequests().Count();
        var afterCancel = -1;

        void SaveToCollection()
        {
            ui.App.InjectSequence(InputInjectionExtensions.RightButtonClick(ui.Find("200 GET    /echo/masked")));
            ui.Click("Save to collection…");
        }

        var screens = Drive(ui, () =>
            {
                ui.App.InjectKey(Key.F9);
                ui.Click("/echo/masked");
                SaveToCollection(); // asks: Enter is Cancel
                afterCancel = collection.AllRequests().Count();
                SaveToCollection(); // asks, and is added
            },
            ("Masked credentials", () => ui.App.InjectKey(Key.Enter)),
            ("Masked credentials", () => ui.Click("Add with ***")),
            ("Name (in TermRest Demo / Basics)", () => ui.Click("► OK ◄")),
            ("Request — TermRest Demo / Basics/", () =>
            {
                ui.App.InjectKey(Key.S.WithCtrl);
                ui.App.RequestStop();
            }));

        Assert.Equal(4, screens.Count);
        Assert.Equal(requests, afterCancel);
        Assert.Equal("***", collection.AllRequests().Single(r => r.ReadSpec().Url.EndsWith("/echo/masked", StringComparison.Ordinal)).ReadSpec().Auth?.Get("password"));
    }

    [Fact]
    public async Task Sending_again_a_request_that_inherited_its_auth_takes_that_auth_from_its_folder_again()
    {
        var path = Path.Combine(_directory, "TermRest Demo.postman_collection.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"value\": \"{{token}}\"", "\"value\": \"folder-s3cret\"", StringComparison.Ordinal)); // the Auth folder's
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.ConfigureHistory(state);
        });
        var collection = Assert.Single(ui.Workspace.Collections);
        var request = collection.FindRequest("Auth/Auth inherited from folder")!;
        await ui.Runner.SendAsync(collection, request, request.ReadSpec(), TestContext.Current.CancellationToken);
        var entry = Assert.Single(ui.Runner.History!.Load());
        var notice = "";

        var screens = Drive(ui, () =>
            {
                ui.App.InjectKey(Key.F9);
                ui.Click("/echo/headers");
                notice = ui.Window.StatusMessage;
                ui.App.InjectKey(Key.F5); // sent without asking
            },
            ("200 OK", () => ui.App.RequestStop()));

        Assert.Single(screens);
        Assert.True(entry is { AuthInherited: true, CredentialsMasked: true });
        Assert.DoesNotContain("***", notice, StringComparison.Ordinal);
        Assert.Equal(2, ui.Runner.History!.Load().Count);
        Assert.Contains(new KeyValuePair<string, string>("Authorization", "Bearer folder-s3cret"), ui.Window.ResponsePane.Exchange!.Request.Headers);

        // Sent as its request in the collection: the new entry inherits too, so sending it again works the same way.
        var resent = ui.Runner.History!.Load()[0];
        Assert.True(resent is { AuthInherited: true, RequestPath: "Auth/Auth inherited from folder" }, $"{resent.AuthInherited} {resent.RequestPath}");
    }

    [Fact]
    public async Task A_history_request_says_what_it_inherits_from_its_collection_once_that_is_reloaded()
    {
        var path = Path.Combine(_directory, "TermRest Demo.postman_collection.json");
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.ConfigureHistory(state);
        });
        var collection = Assert.Single(ui.Workspace.Collections);
        var request = collection.FindRequest("Auth/Auth inherited from folder")!;
        await ui.Runner.SendAsync(collection, request, request.ReadSpec(), TestContext.Current.CancellationToken);
        IEnumerable<string> AuthNotes() => Descendants(ui.Window.Editor).OfType<Views.AuthEditor>().Single().SubViews
            .SelectMany(Descendants).OfType<Terminal.Gui.Views.Label>().Select(l => l.Text);
        List<string> before = [], after = [];

        var screens = Drive(ui, () =>
        {
            ui.App.InjectKey(Key.F9);
            ui.Click("/echo/headers");
            before = [.. AuthNotes()];

            // A pull gives the folder an API key instead of its bearer token, and the collection is reloaded.
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"type\": \"bearer\"", "\"type\": \"apikey\"", StringComparison.Ordinal)
                .Replace("\"bearer\": [", "\"apikey\": [", StringComparison.Ordinal));
            ui.Window.ImportCollectionFrom(path);
            after = [.. AuthNotes()];
            ui.App.RequestStop();
        });

        Assert.Empty(screens);
        Assert.Contains("Currently: Bearer Token from folder 'Auth'.", before);
        Assert.Contains("Currently: API Key from folder 'Auth'.", after);
    }

    [Fact]
    public async Task A_history_request_no_longer_runs_the_scripts_of_the_folder_its_deleted_request_was_in()
    {
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.ConfigureHistory(state);
        });
        var collection = Assert.Single(ui.Workspace.Collections);
        var request = collection.FindRequest("Auth/Basic auth")!; // its own auth: deleting it leaves the editor's request as it is
        await ui.Runner.SendAsync(collection, request, request.ReadSpec(), TestContext.Current.CancellationToken);
        collection.SetScripts(collection.FindFolder("Auth")!, new ScriptSet("console.log('folder')", "", ScriptLanguage.JavaScript));
        string before = "", after = "";

        var screens = Drive(ui, () =>
            {
                ui.Window.ShowRequest(request);
                ui.App.InjectKey(Key.F9);
                ui.Click("/echo/b"); // its history entry (/echo/basic-auth), which the folder's script runs for
                before = ui.Window.Editor.ScriptsTab.Status;
                ui.App.InjectKey(Key.F9); // the request is still selected in the tree
                ui.App.InjectKey(Key.Delete);
                after = ui.Window.Editor.ScriptsTab.Status;
                ui.App.RequestStop();
            },
            ("Delete request 'Basic auth'?", () => ui.Click("► Delete ◄")),
            ("Unsaved changes", () => ui.Click("Don't save")));

        Assert.Equal(2, screens.Count);
        Assert.Equal("→ runs JavaScript", before);
        Assert.Equal("→ runs no scripts", after);
    }

    [Fact]
    public async Task A_history_request_that_inherited_its_auth_is_sent_with_that_auth_once_its_request_is_deleted()
    {
        AuthFolderOverridingTheCollectionsAuth();
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.ConfigureHistory(state);
        });
        var collection = Assert.Single(ui.Workspace.Collections);
        var request = collection.FindRequest("Auth/Auth inherited from folder")!;
        await ui.Runner.SendAsync(collection, request, request.ReadSpec(), TestContext.Current.CancellationToken);
        AuthSpec? auth = null;
        var notice = "";

        var screens = Drive(ui, () =>
            {
                ui.Window.ShowRequest(request);
                ui.App.InjectKey(Key.F9);
                ui.Click("/echo/headers"); // its history entry, which takes its auth from the folder
                ui.App.InjectKey(Key.F9); // the request is still selected in the tree
                ui.App.InjectKey(Key.Delete);
                auth = ui.Window.Editor.Spec.Auth;
                notice = ui.Window.StatusMessage;
                ui.App.InjectKey(Key.F9);
                ui.App.InjectKey(Key.F5); // asks: its token is ***
            },
            ("Delete request 'Auth inherited from folder'?", () => ui.Click("► Delete ◄")),
            ("Masked credentials", () => ui.Click("Send ***")),
            ("History (2)", () => ui.App.RequestStop()),
            ("Unsaved changes", () => ui.Click("Don't save")));

        Assert.Equal(4, screens.Count);
        Assert.Equal(("bearer", "***"), (auth?.Type, auth?.Get("token")));
        Assert.StartsWith("⚠ 'Auth/Auth inherited from folder' is no longer in its collection", notice, StringComparison.Ordinal);
        var sent = ui.Window.ResponsePane.Exchange!.Request.Headers;
        Assert.Contains(new KeyValuePair<string, string>("Authorization", "Bearer ***"), sent);
        Assert.DoesNotContain(sent, h => h.Key == "X-Service-A");
    }

    [Fact]
    public async Task A_history_request_that_inherited_its_auth_keeps_that_auth_and_its_changes_when_a_reload_loses_its_request()
    {
        var file = AuthFolderOverridingTheCollectionsAuth();
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.ConfigureHistory(state);
        });
        var collection = Assert.Single(ui.Workspace.Collections);
        var request = collection.FindRequest("Auth/Auth inherited from folder")!;
        await ui.Runner.SendAsync(collection, request, request.ReadSpec(), TestContext.Current.CancellationToken);
        var token = ui.App.Begin(ui.Window);

        ui.App.InjectKey(Key.F9);
        ui.Click("/echo/headers");
        var inherited = ui.Window.Editor.Spec.Auth;
        ui.Click("{{baseUrl}}/headers");
        ui.App.InjectKey(Key.End);
        ui.Type("?edited=1");
        File.WriteAllText(file, File.ReadAllText(file).Replace("Auth inherited from folder", "Renamed by a pull", StringComparison.Ordinal));
        ui.Window.ImportCollectionFrom(file); // reloads it
        var (auth, url, modified, status) = (ui.Window.Editor.Spec.Auth, ui.Window.Editor.Spec.Url, ui.Window.Editor.IsModified, ui.Window.StatusMessage);
        ui.Window.Editor.MarkClean();
        ui.App.End(token!);

        Assert.Null(inherited);
        Assert.Equal(("bearer", "***"), (auth?.Type, auth?.Get("token")));
        Assert.Equal(("{{baseUrl}}/headers?edited=1", true), (url, modified));
        Assert.StartsWith("Reloaded 'TermRest Demo' from disk. ⚠ 'Auth/Auth inherited from folder' is no longer in its collection", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_history_request_is_not_sent_as_another_request_that_has_its_name()
    {
        File.WriteAllText(Path.Combine(_directory, "Ops.postman_collection.json"), """
            {
              "info": { "name": "Ops", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [
                { "name": "Health", "request": { "method": "GET", "url": "{{baseUrl}}/health" } },
                {
                  "name": "Admin",
                  "auth": { "type": "bearer", "bearer": [{ "key": "token", "value": "admin-s3cret", "type": "string" }] },
                  "item": [{ "name": "Health", "request": { "method": "GET", "url": "{{baseUrl}}/admin/health" } }]
                }
              ]
            }
            """);
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.ConfigureHistory(state);
        });
        var ops = ui.Workspace.Collections.Single(c => c.Name == "Ops");
        var health = ops.FindRequest("Health")!;
        await ui.Runner.SendAsync(ops, health, health.ReadSpec(), TestContext.Current.CancellationToken);

        var screens = Drive(ui, () =>
            {
                ui.Click("GET  Health"); // the one sent; Admin/Health is in its folder, closed
                ui.App.InjectKey(Key.Delete);
                ui.App.InjectKey(Key.F9);
                ui.Click("/echo/health");
                ui.App.InjectKey(Key.F5);
            },
            ("Delete request 'Health'?", () => ui.Click("► Delete ◄")),
            ("History (2)", () => ui.App.RequestStop()),
            ("Unsaved changes", () => ui.Click("Don't save")));

        Assert.Equal(3, screens.Count);
        Assert.Equal("/echo/health", ui.Window.ResponsePane.Exchange!.Request.Uri?.AbsolutePath);
        Assert.DoesNotContain(ui.Window.ResponsePane.Exchange.Request.Headers, h => h.Key == "Authorization");
    }

    [Fact]
    public async Task A_history_request_whose_path_another_request_shares_is_sent_on_its_own()
    {
        // Names may hold slashes: Admin › v2 › Health and Admin › "v2/Health" are both Admin/v2/Health.
        File.WriteAllText(Path.Combine(_directory, "Ops.postman_collection.json"), """
            {
              "info": { "name": "Ops", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "item": [{
                "name": "Admin",
                "item": [
                  {
                    "name": "v2",
                    "auth": { "type": "bearer", "bearer": [{ "key": "token", "value": "admin-s3cret", "type": "string" }] },
                    "item": [{ "name": "Health", "request": { "method": "GET", "url": "{{baseUrl}}/admin/health" } }]
                  },
                  { "name": "v2/Health", "request": { "method": "GET", "url": "{{baseUrl}}/health" } }
                ]
              }]
            }
            """);
        await using var server = await LocalEchoServer.StartAsync();
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner =>
        {
            runner.LocalVariables["baseUrl"] = server.BaseUrl + "/echo";
            runner.ConfigureHistory(state);
        });
        var ops = ui.Workspace.Collections.Single(c => c.Name == "Ops");
        var health = ops.AllRequests().Single(r => r.Name == "v2/Health");
        await ui.Runner.SendAsync(ops, health, health.ReadSpec(), TestContext.Current.CancellationToken);

        var screens = Drive(ui, () =>
            {
                ui.App.InjectKey(Key.F9);
                ui.Click("/echo/health");
                ui.App.InjectKey(Key.F5);
            },
            ("History (2)", () => ui.App.RequestStop()));

        Assert.Single(screens);
        Assert.Equal("/echo/health", ui.Window.ResponsePane.Exchange!.Request.Uri?.AbsolutePath);
        Assert.DoesNotContain(ui.Window.ResponsePane.Exchange.Request.Headers, h => h.Key == "Authorization");
    }

    [Fact]
    public async Task A_history_request_is_sent_again_in_its_own_collection_when_another_has_its_name()
    {
        // An updated export, imported next to the collection it updates: both are 'Orders API', with other hosts and tokens.
        await using var server = await LocalEchoServer.StartAsync();
        File.WriteAllText(Path.Combine(_directory, "Orders API.postman_collection.json"), OrdersApi(server.BaseUrl + "/echo/v1", "v1-s3cret"));
        var export = Path.Combine(_directory, "exports", "Orders API.postman_collection.json");
        Directory.CreateDirectory(Path.GetDirectoryName(export)!);
        File.WriteAllText(export, OrdersApi(server.BaseUrl + "/echo/v2", "v2-s3cret"));
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner => runner.ConfigureHistory(state));
        var token = ui.App.Begin(ui.Window);
        ui.Window.ImportCollectionFrom(export);
        ui.App.End(token!);
        var update = ui.Workspace.Collections[^1];
        var request = update.FindRequest("Get order")!;
        await ui.Runner.SendAsync(update, request, request.ReadSpec(), TestContext.Current.CancellationToken);
        TermRest.Core.Http.HttpExchange? resent = null;

        var screens = Drive(ui, () =>
            {
                ui.App.InjectKey(Key.F9);
                ui.Click("/echo/v2/order"); // the sidebar cuts the row off
                ui.App.InjectKey(Key.F5);
            },
            ("History (2)", () =>
            {
                resent = ui.Window.ResponsePane.Exchange;
                ui.App.InjectSequence(InputInjectionExtensions.RightButtonClick(ui.Find("/echo/v2/order")));
                ui.Click("Go to request in collection");
                ui.App.InjectKey(Key.F5);
            }),
            ("200 OK", () => ui.App.RequestStop()));

        Assert.Equal(2, screens.Count);
        Assert.Equal("Orders API (2).postman_collection.json", Path.GetFileName(update.FilePath));
        Assert.Equal("/echo/v2/orders/1", resent?.Request.Uri?.AbsolutePath);
        Assert.Contains(new KeyValuePair<string, string>("Authorization", "Bearer v2-s3cret"), resent!.Request.Headers);
        Assert.Equal("Orders API (2).postman_collection.json", ui.Runner.History!.Load()[0].CollectionFile);

        // Go to request opened the request in the update: sent from there, it goes to the update's host.
        Assert.Equal("/echo/v2/orders/1", ui.Window.ResponsePane.Exchange!.Request.Uri?.AbsolutePath);
    }

    [Fact]
    public async Task A_history_request_from_an_earlier_version_is_sent_on_its_own_when_collections_share_its_name()
    {
        await using var server = await LocalEchoServer.StartAsync();
        File.WriteAllText(Path.Combine(_directory, "Orders API.postman_collection.json"), OrdersApi(server.BaseUrl + "/echo/v1", "v1-s3cret"));
        File.WriteAllText(Path.Combine(_directory, "Orders API (2).postman_collection.json"), OrdersApi(server.BaseUrl + "/echo/v2", "v2-s3cret"));
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner => runner.ConfigureHistory(state));
        var original = ui.Workspace.Collections.Single(c => Path.GetFileName(c.FilePath) == "Orders API.postman_collection.json");
        var request = original.FindRequest("Get order")!;
        await ui.Runner.SendAsync(original, request, request.ReadSpec(), TestContext.Current.CancellationToken);

        // As earlier versions wrote it: the entry names its collection, not the collection's file.
        var history = ui.Runner.History!.FilePath;
        File.WriteAllText(history, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(history), "\"collectionFile\":\"[^\"]*\",", ""));
        var status = "";
        AuthSpec? auth = null;

        var screens = Drive(ui, () =>
            {
                ui.App.InjectKey(Key.F9);
                ui.Click("/echo/v1/order");
                (status, auth) = (ui.Window.StatusMessage, ui.Window.Editor.Spec.Auth);
                ui.App.InjectKey(Key.F5); // asks: its token is ***
            },
            ("Masked credentials", () => ui.App.InjectKey(Key.Enter)),
            ("Request (from history", () => ui.App.RequestStop()));

        Assert.Equal(2, screens.Count);
        Assert.Null(Assert.Single(ui.Runner.History.Load()).CollectionFile);
        Assert.StartsWith("⚠ 2 collections are named 'Orders API' and which one this request was sent from is not known: it is sent on its own. Credentials are ***",
            status, StringComparison.Ordinal);
        Assert.Equal(("bearer", "***"), (auth?.Type, auth?.Get("token")));
    }

    [Fact]
    public void Save_as_asks_before_replacing_a_file_and_says_when_it_saved_only_the_start_of_the_body()
    {
        // The save dialog may open on the workspace folder, where a click picks a collection.
        var file = Path.Combine(_directory, "TermRest Demo.postman_collection.json");
        var collection = File.ReadAllText(file);
        var copy = Path.Combine(_directory, "history-copy.txt");
        var state = Path.Combine(_directory, ".state");
        using var ui = new Harness(_directory, runner => runner.ConfigureHistory(state));
        var cut = new TermRest.Core.Http.HttpExchange(new TermRest.Core.Http.PreparedRequest { Method = "GET", Url = "https://api.example.com/log" })
        {
            Response = new TermRest.Core.Http.ResponseSnapshot { StatusCode = 200, ReasonPhrase = "OK", ContentType = "text/plain", Body = "the start"u8.ToArray(), BodyTruncated = true },
        };
        var entry = new TermRest.Core.History.HistoryEntry
        {
            Id = "cut",
            Time = DateTimeOffset.Now,
            Method = "GET",
            Url = "https://api.example.com/log",
            Item = new System.Text.Json.Nodes.JsonObject { ["name"] = "Log", ["request"] = new System.Text.Json.Nodes.JsonObject { ["method"] = "GET", ["url"] = "https://api.example.com/log" } },
            Status = 200,
            BodySaved = true,
            BodyTruncated = true,
        };
        ui.Runner.History!.Add(entry, "token=***"u8.ToArray());
        var (afterCancel, replaced, fromHistory) = ("", "", "");

        void Choose(string path)
        {
            ui.App.RaisePasteEvent(path);
            ui.App.InjectKey(Key.Enter);
        }

        void ClickReplace()
        {
            // The button, not the question's "Replace it?" above it.
            var lines = ui.Screen().Split('\n');
            var y = Array.FindLastIndex(lines, line => line.Contains("Replace", StringComparison.Ordinal));
            ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(lines[y].IndexOf("Replace", StringComparison.Ordinal), y)));
        }

        var screens = Drive(ui, () =>
            {
                ui.Window.ResponsePane.ShowExchange(cut);
                ui.Click("Save as…"); // returns once it is answered
                afterCancel = File.ReadAllText(file);
                ui.Click("Save as…");
                replaced = ui.Window.StatusMessage;
                ui.Window.OpenHistoryEntry(entry);
                ui.Click("Save as…");
                fromHistory = ui.Window.StatusMessage;
                ui.App.RequestStop();
            },
            ("Filename (▲)", () => Choose(file)),
            ("exists.", () => ui.App.InjectKey(Key.Enter)), // Cancel
            ("Filename (▲)", () => Choose(file)),
            ("exists.", ClickReplace),
            ("Filename (▲)", () => Choose(copy)));

        Assert.Equal(5, screens.Count);
        Assert.Equal(collection, afterCancel);
        Assert.Equal("the start", File.ReadAllText(file));
        Assert.Equal($"⚠ Saved 9 B to {file}: only the start of the body, cut at the maximum response size (Tools > Settings)", replaced);
        Assert.Equal("token=***", File.ReadAllText(copy));
        Assert.Equal($"⚠ Saved 9 B to {copy}: only the start of the body, as the history keeps it (secrets masked as ***)", fromHistory);
    }

    /// <summary>
    /// The sample collection with auth for another service on the collection, which the Auth folder overrides with a
    /// bearer token typed in (so the history masks it). Returns the collection's file.
    /// </summary>
    private string AuthFolderOverridingTheCollectionsAuth()
    {
        var file = Path.Combine(_directory, "TermRest Demo.postman_collection.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        root["auth"] = System.Text.Json.Nodes.JsonNode.Parse("""
            { "type": "apikey", "apikey": [{ "key": "key", "value": "X-Service-A" }, { "key": "value", "value": "service-a-s3cret" }, { "key": "in", "value": "header" }] }
            """);
        root["item"]![1]!["auth"]!["bearer"]![0]!["value"] = "folder-s3cret";
        File.WriteAllText(file, root.ToJsonString());
        return file;
    }

    /// <summary>A collection named 'Orders API' whose request goes to <paramref name="baseUrl"/> with the bearer token it inherits.</summary>
    private static string OrdersApi(string baseUrl, string token) => """
        {
          "info": { "name": "Orders API", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
          "auth": { "type": "bearer", "bearer": [{ "key": "token", "value": "TOKEN", "type": "string" }] },
          "item": [{ "name": "Get order", "request": { "method": "GET", "url": "{{baseUrl}}/orders/1" } }],
          "variable": [{ "key": "baseUrl", "value": "BASE_URL" }]
        }
        """.Replace("TOKEN", token, StringComparison.Ordinal).Replace("BASE_URL", baseUrl, StringComparison.Ordinal);

    [Fact]
    public void Recording_does_not_start_while_the_workspace_settings_cannot_be_read()
    {
        Directory.CreateDirectory(Path.Combine(_directory, ".termrest"));
        File.WriteAllText(Path.Combine(_directory, ".termrest", "settings.json"), "{ \"recordingRedact\": [\"X-Tenant\" ");
        using var ui = new Harness(_directory);
        RequestRecorder? recorder = null;

        var screens = Drive(ui, () =>
            {
                ui.App.InjectKey(Key.F12);
                recorder = ui.Runner.Recorder;
                ui.Runner.Recorder = null; // one that started anyway is not asked about when closing
                ui.App.RequestStop();
            },
            ("recordingRedact", () => ui.Click("► OK ◄")));
        recorder?.Discard();

        Assert.Single(screens);
        Assert.Null(recorder);
    }

    [Fact]
    public void Tabs_take_their_width_and_the_tab_key_still_moves_to_the_next_field()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        ui.Window.ResponsePane.ShowExchange(TextResponse("id\tname\n1\tAda"));
        var left = new List<bool>();
        ui.Click("name");
        var screen = ui.Screen();
        var response = Assert.IsType<Views.TextArea>(ui.Window.MostFocused);
        foreach (var key in (Key[])[Key.Tab, Key.Tab.WithShift])
        {
            response.SetFocus();
            ui.App.InjectKey(key);
            left.Add(ui.Window.MostFocused != response);
        }

        ui.Click("POST POST JSON");
        ui.Click("Body");
        ui.Click("Ada Lovelace");
        var body = Assert.IsType<Views.TextArea>(ui.Window.MostFocused);
        ui.App.InjectKey(Key.Tab);
        left.Add(ui.Window.MostFocused != body);
        var (raw, modified) = (body.GetText(), ui.Window.Editor.IsModified);
        ui.App.InjectKey(Key.S.WithCtrl); // closing must not ask, even if this fails
        ui.App.End(token!);

        OnScreen("id  name", screen); // a tab moves on to the next multiple of 4 columns
        OnScreen("1   Ada", screen);
        Assert.Equal([true, true, true], left);
        Assert.DoesNotContain('\t', raw); // no tab typed
        Assert.False(modified);
    }

    [Fact]
    public void A_lone_carriage_return_is_a_line_break_and_nothing_next_to_it_is_lost()
    {
        var file = Path.Combine(_directory, "TermRest Demo.postman_collection.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("Ada Lovelace", "Ada\\rLovelace", StringComparison.Ordinal));
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        ui.App.Driver!.Clipboard = new FakeClipboard();
        ui.Window.ResponsePane.ShowExchange(TextResponse("10%\r20%\ndone"));
        ui.Click("Copy");
        var (copied, response) = (ui.App.Clipboard!.GetClipboardData(), ui.Screen());

        ui.Click("POST POST JSON");
        ui.Click("Body");
        var body = ui.Screen();
        ui.Click("Lovelace");
        ui.Type("!");
        var edited = ui.Window.Editor.Spec.Body.Raw;
        ui.App.InjectKey(Key.S.WithCtrl);
        ui.App.End(token!);

        Assert.Equal(At(response, "10%") with { Y = At(response, "10%").Y + 1 }, At(response, "20%"));
        Assert.Equal("10%\r20%\ndone", copied); // as it came, not as shown
        Assert.Equal(At(body, "\"name\": \"Ada").Y + 1, At(body, "Lovelace\",").Y);
        const string Expected = "{\n    \"id\": \"{{$guid}}\",\n    \"name\": \"Ada\n!Lovelace\",\n    \"sentAt\": \"{{$isoTimestamp}}\"\n}";
        Assert.Equal(Expected, edited);
        Assert.Equal(Expected, PostmanCollection.Load(file).FindRequest("Basics/POST JSON")!.ReadSpec().Body.Raw);
    }

    [Fact]
    public void A_response_header_too_long_for_the_pane_is_marked_cut_and_opens_whole_to_read_and_copy()
    {
        const string Value = "Bearer error=\"invalid_token\", error_description=\"The token expired at '11/13/2026 21:32:45'\", scope=\"orders.read orders.write\", realm=\"orders\"";
        using var ui = new Harness(_directory);
        ui.App.Driver!.Clipboard = new FakeClipboard();
        string table = "", shown = "", titled = "";

        var screens = Drive(ui, () =>
            {
                ui.Window.ResponsePane.ShowExchange(TextResponse("ok", new("WWW-Authenticate", Value), new("X_Request_Id", "7f3a")));
                var tabs = ui.Find("Timeline"); // the response's tabs: the request has a Headers tab too
                ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(ui.Screen().Split('\n')[tabs.Y].IndexOf("Headers", StringComparison.Ordinal), tabs.Y)));
                table = ui.Screen();
                ui.Click("WWW-Authenticate");
                ui.App.InjectKey(Key.Enter); // returns once the dialog is closed
                ui.Click("X_Request_Id");
                ui.App.InjectKey(Key.Enter);
                ui.App.RequestStop();
            },
            ("realm=\"orders\"", () =>
            {
                shown = ui.Screen();
                ui.Click("► Copy ◄");
            }),
            ("► Copy ◄", () =>
            {
                titled = ui.Screen();
                ui.App.InjectKey(Key.Esc);
            }));

        Assert.Equal(2, screens.Count);
        var row = table.Split('\n').Single(l => l.Contains("WWW-Authenticate", StringComparison.Ordinal));
        Assert.Contains("…", row, StringComparison.Ordinal);
        Assert.DoesNotContain("realm", table, StringComparison.Ordinal);
        OnScreen(Value, Unwrapped(shown));
        Assert.Equal(Value, ui.App.Clipboard!.GetClipboardData());
        Assert.Equal("The value of WWW-Authenticate copied to the clipboard.", ui.Window.StatusMessage);
        OnScreen("┥X_Request_Id┝", titled); // the name as written, its _ no hotkey
    }

    [Fact]
    public void Keys_that_delete_nothing_leave_the_request_unmodified()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        ui.App.Driver!.Clipboard = new FakeClipboard();
        ui.Click("Scripts");
        var label = ui.Find("Pre-request (before sending)");
        ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(label.X, label.Y + 1)));
        var script = Assert.IsType<Views.TextArea>(ui.Window.MostFocused);
        foreach (var key in (Key[])[Key.Backspace, Key.Delete, Key.Backspace.WithCtrl, Key.Delete.WithCtrl, Key.K.WithCtrl, Key.Backspace.WithCtrl.WithShift])
        {
            ui.App.InjectKey(key);
        }

        var emptyScript = (script.GetText(), ui.Window.Editor.IsModified);
        ui.Click("POST POST JSON");
        ui.Click("Body");
        ui.Click("Ada Lovelace");
        foreach (var key in (Key[])[Key.Home.WithCtrl, Key.Backspace, Key.Backspace.WithCtrl, Key.End.WithCtrl, Key.Delete, Key.Delete.WithCtrl])
        {
            ui.App.InjectKey(key);
        }

        var (body, unsaved, screen) = (ui.Window.Editor.IsModified, ui.Workspace.HasUnsavedChanges, ui.Screen());
        ui.App.InjectKey(Key.S.WithCtrl); // closing must not ask, even if this fails
        ui.App.End(token!);

        Assert.Equal(("", false), emptyScript);
        Assert.False(body);
        Assert.False(unsaved);
        OnScreen("Request — TermRest Demo / Basics/POST JSON", screen);
        Assert.DoesNotContain("POST JSON ●", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void Characters_the_editor_keeps_composed_are_no_edit_and_a_script_left_alone_keeps_its_bytes()
    {
        // Terminal.Gui keeps each character composed (NFC): a decomposed é (as macOS file names have it) and the ohm sign
        // come back from an editor changed. U+FFFE (a byte order mark read the wrong way) is one normalizing refuses.
        const string Decomposed = "Cafe\u0301 \u2126";
        const string Body = $"{{\"name\": \"{Decomposed}\", \"bom\": \"\uFFFE\"}}";
        var path = Path.Combine(_directory, "TermRest Demo.postman_collection.json");
        var setup = PostmanCollection.Load(path);
        var request = setup.FindRequest("Basics/POST JSON")!;
        var spec = request.ReadSpec();
        spec.Body.Raw = Body;
        spec.Scripts = new ScriptSet($"# {Decomposed}", $"# {Decomposed}", ScriptLanguage.Python);
        setup.UpdateRequest(request, spec);
        setup.Save();
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        ui.Click("POST POST JSON");
        ui.Click("Body");
        ui.Click("\"name\"");
        foreach (var key in (Key[])[Key.Home.WithCtrl, Key.Backspace, Key.End.WithCtrl, Key.Delete])
        {
            ui.App.InjectKey(key);
        }

        var body = (ui.Window.Editor.IsModified, ui.Workspace.HasUnsavedChanges, ui.Window.Editor.Spec.Body.Raw);
        ui.Click("Scripts");
        var label = ui.Find("Pre-request (before sending)");
        ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(label.X, label.Y + 1)));
        ui.App.InjectKey(Key.Home.WithCtrl);
        ui.Type("x");
        var scripts = ui.Window.Editor.Spec.Scripts.For(ScriptLanguage.Python);
        ui.App.InjectKey(Key.S.WithCtrl);
        ui.App.End(token!);

        Assert.Equal((false, false, Body), body);
        Assert.Equal($"x# {Decomposed.Normalize()}", scripts.PreRequest); // edited: as the editor holds it
        Assert.Equal($"# {Decomposed}", scripts.Test);
        Assert.Equal($"# {Decomposed}", PostmanCollection.Load(path).FindRequest("Basics/POST JSON")!.ReadSpec().Scripts.For(ScriptLanguage.Python).Test);
    }

    [Fact]
    public void Ctrl_N_makes_a_new_request_from_lists_and_tables_and_Insert_adds_rows()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        var dialogs = new List<string>();
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            // Runs only while a dialog is open: notes what it shows and closes it.
            dialogs.Add(ui.Screen());
            ui.App.InjectKey(Key.Esc);
            return true;
        });
        var pressed = new List<string>();
        void CtrlN()
        {
            pressed.Add(ui.Window.MostFocused?.GetType().Name ?? "");
            ui.App.InjectKey(Key.N.WithCtrl);
        }

        ui.Click("GET  GET with query");
        CtrlN();
        ui.Click("debug");
        CtrlN();
        ui.Click("Headers");
        ui.Click("Accept");
        CtrlN();
        ui.App.InjectKey(Key.InsertChar);
        ui.Click("Auth");
        ui.Click("Inherit auth from parent");
        CtrlN();
        ui.Click("GET  Status by path");
        ui.Click("Params");
        Descendants(ui.Window).OfType<Views.KeyValueTable>().Single(t => t.Entries.Any(e => e.Key == "code")).SubViews.OfType<Terminal.Gui.Views.TableView>().Single().SetFocus();
        CtrlN();
        ui.App.InjectKey(Key.InsertChar); // path variables follow the URL: nothing to add or remove
        ui.App.InjectKey(Key.Delete);
        var pathVariables = ui.Window.Editor.Spec.PathVariables.Select(v => v.Key).ToList();
        ui.Window.ResponsePane.ShowExchange(TextResponse("ok", new KeyValuePair<string, string>("X-Trace", "7")));
        var tabs = ui.Find("Timeline"); // the response's tabs: the request has a Headers tab too
        ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(ui.Screen().Split('\n')[tabs.Y].IndexOf("Headers", StringComparison.Ordinal), tabs.Y)));
        ui.Click("X-Trace");
        CtrlN();
        ui.App.InjectKey(Key.F9);
        ui.Window.HistoryPane.SetEntries(HistoryEntries(3), "No requests sent yet.");
        ui.Click("/items/1");
        var opened = ui.Window.HistoryPane.Selected;
        CtrlN();
        var (selected, url) = (ui.Window.HistoryPane.Selected, ui.Window.Editor.Spec.Url);
        var unsaved = ui.Workspace.HasUnsavedChanges || ui.Window.Editor.IsModified;
        ui.App.InjectKey(Key.S.WithCtrl);
        ui.App.End(token!);

        Assert.Equal(["TreeView`1", "TableView", "TableView", "ListView", "TableView", "TableView", "ListView"], pressed);
        Assert.Equal(8, dialogs.Count);
        Assert.Equal(7, dialogs.Count(d => d.Contains("Name (in TermRest Demo / Basics)", StringComparison.Ordinal)));
        OnScreen("Add header", dialogs[3]); // Insert, after the third Ctrl+N
        Assert.Equal(["code"], pathVariables);
        Assert.Equal(opened, selected); // not the next entry
        Assert.Equal("https://api.example.com/items/1", url);
        Assert.False(unsaved);
    }

    [Fact]
    public void The_history_filter_says_when_nothing_matches_even_after_scrolling_down()
    {
        using var ui = new Harness(_directory);
        var token = ui.App.Begin(ui.Window);
        ui.App.InjectKey(Key.F9);
        ui.Window.HistoryPane.SetEntries(HistoryEntries(100), "No requests sent yet.");
        var list = ui.Find("/items/0");
        for (var i = 0; i < 80; i++)
        {
            ui.App.InjectMouse(new Mouse { ScreenPosition = list, Flags = MouseFlags.WheeledDown });
        }

        var scrolled = ui.Screen();
        var filter = ui.Find("Filter:");
        ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(filter.X + 9, filter.Y)));
        ui.Type("no-such-request");
        var (rows, selected, screen) = (ui.Window.HistoryPane.Rows.ToList(), ui.Window.HistoryPane.Selected, ui.Screen());
        ui.App.End(token!);

        Assert.DoesNotContain("/items/0 ", scrolled, StringComparison.Ordinal);
        Assert.Equal(["No match."], rows);
        Assert.Null(selected);
        OnScreen("No match.", screen);
    }

    [Fact]
    public void A_typed_body_file_is_looked_up_in_the_background_once_typing_pauses()
    {
        using var ui = new Harness(_directory);
        var path = Path.Combine(_directory, "order.json");
        File.WriteAllText(path, "{\"item\": 1}");
        string typing = "", found = "", missing = "";
        var step = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        ui.App.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            if (DateTime.UtcNow > deadline)
            {
                ui.Window.Editor.MarkClean();
                ui.App.RequestStop();
                return false;
            }

            switch (step)
            {
                case 0:
                    ui.Click("POST POST JSON");
                    ui.Click("Body");
                    ui.Click("○ file");
                    var label = ui.Find("File:");
                    ui.App.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(label.X + 8, label.Y)));
                    ui.Type(path);
                    typing = ui.Window.BodyFileStatus; // the disk is not read while typing
                    step = 1;
                    break;
                case 1 when ui.Window.BodyFileStatus.StartsWith("order.json", StringComparison.Ordinal):
                    found = ui.Window.BodyFileStatus;
                    ui.Type("x");
                    step = 2;
                    break;
                case 2 when ui.Window.BodyFileStatus.StartsWith('✗'):
                    missing = ui.Window.BodyFileStatus;
                    ui.App.InjectKey(Key.S.WithCtrl);
                    ui.App.RequestStop();
                    return false;
            }

            return true;
        });

        ui.App.Run(ui.Window);

        Assert.Equal($"Looking for {path}…", typing);
        Assert.Equal("order.json · 11 B · sent as it is", found);
        Assert.Equal($"✗ {path}x does not exist.", missing);
    }

    [Fact]
    public void A_body_file_outside_the_workspace_is_not_looked_at_until_files_from_outside_are_allowed()
    {
        // As Send has it: on Windows, looking at \\server\share\x signs in to that server with the user's credentials.
        var secret = Path.Combine(TestFiles.NewTempDirectory(), "credentials");
        File.WriteAllText(secret, "s3cret");
        using var ui = new Harness(_directory);
        string Shown(BodyMode mode)
        {
            ui.Window.Editor.Load(new HttpRequestSpec { Url = "https://api.example.com/upload", Body = new RequestBody { Mode = mode, Raw = "{}", FileSrc = secret } });
            return ui.Window.BodyFileStatus;
        }

        var raw = Shown(BodyMode.Raw); // a raw body keeps a file path it doesn't send
        var file = Shown(BodyMode.File);
        ui.Runner.OutsideFilesAllowed = true;
        ui.Window.Editor.RefreshBodyFile();

        Assert.Equal("", raw);
        Assert.Equal($"✗ {secret} is outside the workspace folder: it is not sent unless you allow files from outside it (Tools › Settings…).", file);
        Assert.Equal("credentials · 6 B · sent as it is", ui.Window.BodyFileStatus);
    }

    /// <summary>A plain-text response, as if it had just arrived.</summary>
    private static TermRest.Core.Http.HttpExchange TextResponse(string body, params KeyValuePair<string, string>[] headers) =>
        new(new TermRest.Core.Http.PreparedRequest { Method = "GET", Url = "https://api.example.com/log" })
        {
            Response = new TermRest.Core.Http.ResponseSnapshot
            {
                StatusCode = 200,
                ReasonPhrase = "OK",
                ContentType = "text/plain",
                Body = System.Text.Encoding.UTF8.GetBytes(body),
                Headers = [new("Content-Type", "text/plain"), .. headers],
            },
        };

    private static List<TermRest.Core.History.HistoryEntry> HistoryEntries(int count) =>
    [
        .. Enumerable.Range(0, count).Select(i => new TermRest.Core.History.HistoryEntry
        {
            Id = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Time = DateTimeOffset.Now,
            Method = "GET",
            Url = $"https://api.example.com/items/{i}",
            Item = new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = $"Item {i}",
                ["request"] = new System.Text.Json.Nodes.JsonObject { ["method"] = "GET", ["url"] = $"https://api.example.com/items/{i}" },
            },
            Status = 200,
        }),
    ];

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
