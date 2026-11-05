using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Http;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Workspace;
using TermRest.Tui.Dialogs;
using TermRest.Tui.Views;

namespace TermRest.Tui;

/// <summary>
/// The application window: menu bar, collection tree, request editor, response viewer and status bar.
/// Everything is reachable with the mouse and with the keyboard (F10 menu, F6 to cycle panes, shortcuts below).
/// </summary>
internal sealed class MainWindow : Runnable
{
    private readonly ApiWorkspace _workspace;
    private readonly RequestRunner _runner;
    private readonly MenuBar _menu;
    private readonly FrameView _sidebar;
    private readonly FrameView _requestFrame;
    private readonly FrameView _responseFrame;
    private readonly CollectionTree _tree;
    private readonly RequestEditor _editor;
    private readonly ResponseView _response;
    private readonly Shortcut _environmentShortcut;
    private readonly Shortcut _message;
    private PostmanCollection? _collection;
    private RequestNode? _node;
    private PostmanCollection? _scratch;
    private CancellationTokenSource? _sending;
    private PopoverMenu? _popover;
    private bool _quitConfirmed;

    public MainWindow(ApiWorkspace workspace, RequestRunner runner)
    {
        _workspace = workspace;
        _runner = runner;
        Title = "TermRest";
        Width = Dim.Fill();
        Height = Dim.Fill();

        _menu = BuildMenu();

        _sidebar = new FrameView
        {
            Title = "Collections",
            X = 0,
            Y = Pos.Bottom(_menu),
            Width = Dim.Percent(28),
            Height = Dim.Fill(1),
            Arrangement = ViewArrangement.RightResizable,
        };
        _tree = new CollectionTree(workspace);
        _sidebar.Add(_tree);

        _requestFrame = new FrameView
        {
            Title = "Request",
            X = Pos.Right(_sidebar),
            Y = Pos.Bottom(_menu),
            Width = Dim.Fill(),
            Height = Dim.Percent(50),
            Arrangement = ViewArrangement.BottomResizable,
        };
        _editor = new RequestEditor
        {
            DescribeCertificate = DescribeCertificate,
            DescribeInheritedAuth = DescribeInheritedAuth,
        };
        _requestFrame.Add(_editor);

        _responseFrame = new FrameView
        {
            Title = "Response",
            X = Pos.Right(_sidebar),
            Y = Pos.Bottom(_requestFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };
        _response = new ResponseView();
        _responseFrame.Add(_response);

        _environmentShortcut = new Shortcut { Title = EnvironmentLabel(), Key = Key.F7, BindKeyToApplication = true, Action = ShowEnvironmentMenu };
        _message = new Shortcut { Title = "", CanFocus = false };
        var statusBar = new StatusBar(
        [
            new Shortcut { Title = "Send", Key = Key.F5, BindKeyToApplication = true, Action = Send },
            new Shortcut { Title = "Save", Key = Key.S.WithCtrl, BindKeyToApplication = true, Action = Save },
            new Shortcut { Title = "New", Key = Key.N.WithCtrl, BindKeyToApplication = true, Action = NewRequest },
            _environmentShortcut,
            new Shortcut { Title = "Certs", Key = Key.F8, BindKeyToApplication = true, Action = ShowCertificates },
            new Shortcut { Title = "Menu", Key = Key.F10 },
            new Shortcut { Title = "Quit", Key = Key.Q.WithCtrl, BindKeyToApplication = true, Action = Quit },
            _message,
        ]);

        Add(_menu, _sidebar, _requestFrame, _responseFrame, statusBar);

        _tree.SelectionChanged += (_, item) =>
        {
            if (item is RequestNode request && request != _node)
            {
                OpenRequest(request);
            }
        };
        _tree.ItemActivated += (_, item) =>
        {
            if (item is RequestNode request)
            {
                OpenRequest(request);
                _editor.FocusUrl();
            }
        };
        _tree.ContextMenuRequested += (_, position) => ShowContextMenu(position);
        _tree.DeleteRequested += (_, item) => DeleteItem(item);
        _tree.RenameRequested += (_, item) => RenameItem(item);
        _editor.Modified += (_, _) => UpdateTitles();
        _editor.SendRequested += (_, _) => Send();
        _editor.ManageCertificatesRequested += (_, _) => ShowCertificates();
        _response.Notice += (_, message) => Notify(message);

        var first = workspace.Collections.SelectMany(c => c.AllRequests()).FirstOrDefault();
        if (first is not null)
        {
            _tree.Select(first);
            OpenRequest(first);
        }
        else
        {
            StartScratchRequest();
        }

        if (workspace.LoadErrors.Count > 0)
        {
            Notify($"⚠ {workspace.LoadErrors.Count} file(s) could not be loaded (Help > Workspace info)");
        }
    }

    /// <summary>Set when the user opens another workspace; the host restarts the UI with it.</summary>
    public string? NextWorkspacePath { get; private set; }

    // Test hooks (InternalsVisibleTo TermRest.Tui.Tests).
    internal RequestEditor Editor => _editor;

    internal ResponseView ResponsePane => _response;

    internal string StatusMessage => _message.Title;

    /// <summary>Selects and opens a request (used by snapshots and tests).</summary>
    internal void ShowRequest(RequestNode node)
    {
        _tree.Select(node);
        OpenRequest(node);
    }

    /// <summary>Sends the current request and shows the result without an event loop (used by snapshots).</summary>
    internal async Task SendAndShowAsync(CancellationToken cancellationToken = default)
    {
        var prepared = _runner.Prepare(_collection ?? (_scratch ??= PostmanCollection.Create("Scratch")), _node, _editor.Spec);
        var exchange = await _runner.SendAsync(prepared, cancellationToken).ConfigureAwait(false);
        _response.ShowExchange(exchange);
    }

    // ----- menu ------------------------------------------------------------------------------

    private MenuBar BuildMenu() => new(
    [
        new MenuBarItem("_File",
        [
            new MenuItem("_New collection…", "", () => NewCollection()),
            new MenuItem("New _environment…", "", () => ShowEnvironments()),
            new Line(),
            new MenuItem("_Open workspace folder…", "", OpenWorkspace),
            new MenuItem("_Import Postman collection…", "", ImportCollection),
            new MenuItem("Import Postman e_nvironment…", "", ImportEnvironment),
            new Line(),
            new MenuItem("_Save all", Key.S.WithCtrl, Save),
            new Line(),
            new MenuItem("E_xit", Key.Q.WithCtrl, Quit),
        ]),
        new MenuBarItem("_Request",
        [
            new MenuItem("_Send", Key.F5, Send),
            new MenuItem("_New request", Key.N.WithCtrl, NewRequest),
            new MenuItem("New _folder…", "", NewFolder),
            new MenuItem("_Rename…", Key.F2, () => RenameItem(_tree.SelectedItem)),
            new MenuItem("_Duplicate", "", () => DuplicateItem(_tree.SelectedItem)),
            new MenuItem("De_lete…", "", () => DeleteItem(_tree.SelectedItem)),
            new Line(),
            new MenuItem("Copy as c_URL", "", CopyAsCurl),
            new MenuItem("Focus _URL bar", Key.L.WithCtrl, () => _editor.FocusUrl()),
        ]),
        new MenuBarItem("_Environment",
        [
            new MenuItem("_Select environment…", Key.F7, ShowEnvironmentMenu),
            new MenuItem("_Manage environments…", "", () => ShowEnvironments()),
        ]),
        new MenuBarItem("_Tools",
        [
            new MenuItem("Client _certificates…", Key.F8, ShowCertificates),
            new MenuItem("_Settings…", "", ShowSettings),
            new MenuItem("Clear _cookies", "", ClearCookies),
        ]),
        new MenuBarItem("_Help",
        [
            new MenuItem("_Keyboard & mouse", "", ShowKeys),
            new MenuItem("_Workspace info", "", ShowWorkspaceInfo),
            new MenuItem("_About TermRest", "", ShowAbout),
        ]),
    ]);

    // ----- request lifecycle -----------------------------------------------------------------

    private void OpenRequest(RequestNode node)
    {
        CommitEditor();
        _collection = node.Collection;
        _node = node;
        _editor.Load(node.ReadSpec());
        _response.Clear();
        UpdateTitles();
    }

    private void StartScratchRequest()
    {
        _scratch ??= PostmanCollection.Create("Scratch");
        _collection = _scratch;
        _node = null;
        _editor.Load(new HttpRequestSpec { Url = "https://postman-echo.com/get?hello=world" });
        _response.Clear();
        UpdateTitles();
        Notify("No collections yet: this is a scratch request. Use Ctrl+N or File > New collection to save requests.");
    }

    /// <summary>Writes pending editor changes into the in-memory collection (not to disk).</summary>
    private void CommitEditor()
    {
        if (_node is null || _collection is null || !_editor.IsModified)
        {
            return;
        }

        if (_collection.UpdateRequest(_node, _editor.Spec))
        {
            _tree.RefreshItem(_collection);
        }

        _editor.MarkClean();
        UpdateTitles();
    }

    private async void Send()
    {
        if (_sending is not null)
        {
            _sending.Cancel();
            return;
        }

        var collection = _collection ?? (_scratch ??= PostmanCollection.Create("Scratch"));
        PreparedRequest prepared;
        try
        {
            prepared = _runner.Prepare(collection, _node, _editor.Spec);
        }
        catch (Exception ex)
        {
            Ui.Error(App!, "Cannot send", ex.Message);
            return;
        }

        _response.ShowSending(prepared);
        _editor.SetSending(true);
        using var cancellation = new CancellationTokenSource();
        _sending = cancellation;
        try
        {
            var exchange = await _runner.SendAsync(prepared, cancellation.Token);
            _response.ShowExchange(exchange);
            Notify(exchange.Response is null ? "" : $"{exchange.Request.Method} {exchange.Request.Uri?.Host} → {exchange.Response.StatusCode}");
        }
        catch (Exception ex)
        {
            _response.ShowExchange(new HttpExchange(prepared) { ErrorMessage = ex.Message, Exception = ex });
        }
        finally
        {
            _sending = null;
            _editor.SetSending(false);
        }
    }

    private void Save()
    {
        CommitEditor();
        var saved = new List<string>();
        try
        {
            foreach (var collection in _workspace.Collections.Where(c => c.IsDirty))
            {
                collection.Save();
                saved.Add(Path.GetFileName(collection.FilePath!));
            }

            foreach (var environment in _workspace.Environments.Where(e => e.IsDirty))
            {
                environment.Save();
                saved.Add(Path.GetFileName(environment.FilePath!));
            }

            if (_workspace.Globals?.IsDirty == true)
            {
                _workspace.Globals.Save();
            }

            _workspace.SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(App!, "Save failed", ex.Message);
        }

        if (_node is null && _collection == _scratch && _editor.IsModified)
        {
            Notify("This scratch request is not in a collection yet: use Request > New request to keep it.");
        }
        else
        {
            Notify(saved.Count == 0 ? "Nothing to save." : $"Saved {string.Join(", ", saved)}");
        }

        _tree.RefreshAll();
        UpdateTitles();
    }

    // ----- tree operations -------------------------------------------------------------------

    private FolderNode? TargetFolder()
    {
        return _tree.SelectedItem switch
        {
            PostmanCollection collection => collection.RootNode,
            FolderNode folder => folder,
            RequestNode request => request.Parent,
            _ => _workspace.Collections.FirstOrDefault()?.RootNode,
        };
    }

    private void NewRequest()
    {
        var folder = TargetFolder();
        if (folder is null)
        {
            if (!NewCollection())
            {
                return;
            }

            folder = _workspace.Collections[^1].RootNode;
        }

        if (Ui.PromptText(App!, "New request", $"Name (in {folder.Collection.Name}{(folder is CollectionRootNode ? "" : " / " + folder.PathInCollection)})", "New request") is not { Length: > 0 } name)
        {
            return;
        }

        CommitEditor();
        HttpRequestSpec? spec = null;
        if (_node is null && _collection == _scratch)
        {
            spec = _editor.Spec.Clone();
        }

        var node = folder.Collection.AddRequest(folder, name.Trim(), spec);
        _tree.RefreshItem(folder is CollectionRootNode ? folder.Collection : folder);
        _tree.Select(node);
        OpenRequest(node);
        _editor.FocusUrl();
    }

    private bool NewCollection()
    {
        if (Ui.PromptText(App!, "New collection", "Collection name", "My API") is not { Length: > 0 } name)
        {
            return false;
        }

        try
        {
            var collection = _workspace.CreateCollection(name.Trim());
            _tree.AddCollection(collection);
            Notify($"Created {Path.GetFileName(collection.FilePath)}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(App!, "Cannot create collection", ex.Message);
            return false;
        }
    }

    private void NewFolder()
    {
        var parent = TargetFolder();
        if (parent is null)
        {
            Ui.Info(App!, "New folder", "Create a collection first (File > New collection).");
            return;
        }

        if (Ui.PromptText(App!, "New folder", "Folder name", "New folder") is { Length: > 0 } name)
        {
            var folder = parent.Collection.AddFolder(parent, name.Trim());
            _tree.RefreshItem(parent is CollectionRootNode ? parent.Collection : parent);
            _tree.Select(folder);
            UpdateTitles();
        }
    }

    private void RenameItem(object? item)
    {
        switch (item)
        {
            case PostmanCollection collection when Ui.PromptText(App!, "Rename collection", "Name", collection.Name) is { Length: > 0 } name:
                collection.Name = name.Trim();
                _tree.RefreshItem(collection);
                break;
            case CollectionNode node when Ui.PromptText(App!, "Rename", "Name", node.Name) is { Length: > 0 } name:
                node.Collection.Rename(node, name.Trim());
                _tree.RefreshItem(node);
                _tree.RefreshItem(node.Collection);
                break;
        }

        UpdateTitles();
    }

    private void DuplicateItem(object? item)
    {
        if (item is not CollectionNode node || node.Parent is null)
        {
            return;
        }

        CommitEditor();
        var copy = node.Collection.Duplicate(node);
        _tree.RefreshItem(node.Parent is CollectionRootNode ? node.Collection : node.Parent);
        _tree.Select(copy);
        if (copy is RequestNode request)
        {
            OpenRequest(request);
        }
    }

    private void DeleteItem(object? item)
    {
        if (item is PostmanCollection collection)
        {
            Ui.Info(App!, "Delete collection", $"Collections are files: delete '{Path.GetFileName(collection.FilePath)}' from the workspace folder to remove it.");
            return;
        }

        if (item is not CollectionNode node || node.Parent is not { } parent)
        {
            return;
        }

        var what = node is FolderNode folder ? $"folder '{node.Name}' and its {folder.Children.Count} item(s)" : $"request '{node.Name}'";
        if (!Ui.Confirm(App!, "Delete", $"Delete {what}? You can undo by not saving (Ctrl+S writes the change).", "Delete", "Cancel"))
        {
            return;
        }

        if (_node is not null && (_node == node || _node.Ancestors().Contains(node)))
        {
            _node = null;
            _editor.MarkClean();
        }

        node.Collection.Remove(node);
        _tree.RefreshItem(parent is CollectionRootNode ? parent.Collection : parent);
        _tree.RefreshItem(node.Collection);
        if (_node is null)
        {
            _collection = null;
            _editor.Load(new HttpRequestSpec());
            _response.Clear();
        }

        UpdateTitles();
    }

    private void ShowContextMenu(System.Drawing.Point? position)
    {
        var item = _tree.SelectedItem;
        var items = new List<View>
        {
            new MenuItem("New _request", "", NewRequest),
            new MenuItem("New _folder…", "", NewFolder),
        };
        if (item is not null)
        {
            items.Add(new Line());
            items.Add(new MenuItem("Re_name…", "", () => RenameItem(item)));
        }

        if (item is CollectionNode)
        {
            items.Add(new MenuItem("_Duplicate", "", () => DuplicateItem(item)));
            items.Add(new MenuItem("De_lete…", "", () => DeleteItem(item)));
        }

        if (item is RequestNode)
        {
            items.Add(new Line());
            items.Add(new MenuItem("_Send", "", Send));
            items.Add(new MenuItem("Copy as c_URL", "", CopyAsCurl));
        }

        ShowPopover(items, position);
    }

    // ----- environments, certificates, settings --------------------------------------------

    private void ShowEnvironmentMenu()
    {
        var items = new List<View>
        {
            new MenuItem($"{(_workspace.ActiveEnvironment is null ? "● " : "  ")}No environment", "", () => SetEnvironment(null)),
        };
        foreach (var environment in _workspace.Environments)
        {
            var env = environment;
            items.Add(new MenuItem($"{(env == _workspace.ActiveEnvironment ? "● " : "  ")}{env.Name}", "", () => SetEnvironment(env)));
        }

        items.Add(new Line());
        items.Add(new MenuItem("Manage environments…", "", () => ShowEnvironments()));
        ShowPopover(items, null);
    }

    private void SetEnvironment(PostmanEnvironment? environment)
    {
        _workspace.ActiveEnvironment = environment;
        _workspace.SaveSettings();
        _environmentShortcut.Title = EnvironmentLabel();
        _editor.RefreshCertificateInfo();
        Notify($"Environment: {environment?.Name ?? "none"}");
    }

    private void ShowEnvironments()
    {
        EnvironmentsDialog.Show(App!, _workspace);
        _environmentShortcut.Title = EnvironmentLabel();
        _editor.RefreshCertificateInfo();
    }

    private void ShowCertificates()
    {
        string? url = null;
        try
        {
            url = _runner.CreateResolver(_collection).Resolve(PostmanUrl.StripQueryAndHash(_editor.Spec.Url));
        }
        catch (Exception)
        {
            // best effort: only used to suggest a URL pattern
        }

        CertificatesDialog.Show(App!, _workspace, _runner, url);
        _editor.RefreshCertificateInfo();
    }

    private void ShowSettings()
    {
        if (SettingsDialog.Show(App!, _workspace))
        {
            Notify("Settings saved.");
        }
    }

    private void ClearCookies()
    {
        foreach (System.Net.Cookie cookie in _runner.Executor.Cookies.GetAllCookies())
        {
            cookie.Expired = true;
        }

        Notify("Cookies cleared.");
    }

    private string DescribeCertificate(HttpRequestSpec spec)
    {
        string url;
        try
        {
            url = _runner.CreateResolver(_collection).Resolve(PostmanUrl.StripQueryAndHash(spec.Url)).Trim();
        }
        catch (Exception)
        {
            return "";
        }

        if (url.Length > 0 && !url.Contains("://", StringComparison.Ordinal))
        {
            url = "http://" + url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return "Client certificates apply to https:// URLs only.";
        }

        if (spec.Certificate is { } pinned && (pinned.CertSrc is not null || pinned.PfxSrc is not null))
        {
            return $"Pinned on this request: {pinned.Name ?? pinned.CertSrc ?? pinned.PfxSrc}";
        }

        return _workspace.Certificates.FindBestMatch(uri) is { } match
            ? $"'{match.Entry.Name}' — {match.Entry.DescribeSource()}\n(matched {match.Pattern})"
            : $"None registered for {uri.Host}. Use Manage certificates… (F8) to add one.";
    }

    private string DescribeInheritedAuth()
    {
        if (_node is null)
        {
            return _collection?.RootNode.Auth is { } rootAuth ? $"Collection auth: {AuthSpec.DisplayName(rootAuth.Type)}" : "No auth is set on the collection.";
        }

        foreach (var folder in _node.Ancestors())
        {
            if (folder.Auth is { } auth)
            {
                var source = folder is CollectionRootNode ? "the collection" : $"folder '{folder.Name}'";
                return $"Currently: {AuthSpec.DisplayName(auth.Type)} from {source}.";
            }
        }

        return "No folder or collection auth is set, so no auth is sent.";
    }

    private string EnvironmentLabel() => $"Env: {_workspace.ActiveEnvironment?.Name ?? "none"}";

    // ----- workspace ---------------------------------------------------------------------------

    private void OpenWorkspace()
    {
        if (Ui.PickFile(App!, "Open workspace folder", _workspace.RootPath, directory: true) is not { } path || !ConfirmUnsaved())
        {
            return;
        }

        NextWorkspacePath = path;
        _quitConfirmed = true;
        App!.RequestStop();
    }

    private void ImportCollection()
    {
        if (Ui.PickFile(App!, "Import Postman collection (v2.0 / v2.1 JSON)", null, false, ("Postman collection", [".json"])) is not { } path)
        {
            return;
        }

        try
        {
            var collection = _workspace.ImportCollection(path);
            _tree.Reload(_workspace);
            _tree.Select(collection);
            Notify($"Imported '{collection.Name}' into {Path.GetFileName(collection.FilePath)}");
        }
        catch (Exception ex)
        {
            Ui.Error(App!, "Import failed", ex.Message);
        }
    }

    private void ImportEnvironment()
    {
        if (Ui.PickFile(App!, "Import Postman environment", null, false, ("Postman environment", [".json"])) is not { } path)
        {
            return;
        }

        try
        {
            var environment = _workspace.ImportEnvironment(path);
            Notify($"Imported environment '{environment.Name}'");
        }
        catch (Exception ex)
        {
            Ui.Error(App!, "Import failed", ex.Message);
        }
    }

    private void CopyAsCurl()
    {
        if (_collection is null && _scratch is null)
        {
            return;
        }

        var prepared = _runner.Prepare(_collection ?? _scratch!, _node, _editor.Spec);
        var curl = CurlExporter.Export(prepared);
        if (Ui.CopyToClipboard(App, curl))
        {
            Notify("cURL command copied to the clipboard.");
        }
        else
        {
            Ui.Info(App!, "cURL", curl);
        }
    }

    // ----- help ----------------------------------------------------------------------------------

    private void ShowKeys() => Ui.Info(App!, "Keyboard & mouse",
        """
        Mouse: click anything; drag pane borders to resize; right-click the tree for actions;
        double-click a table row to edit it; click the checkbox column to enable/disable a row.

        F5 / Enter in URL   Send (again to cancel)      Esc          Cancel a running request
        Ctrl+S              Save all changes            Ctrl+N       New request
        F2                  Rename tree item            Del          Delete tree item
        F7                  Switch environment          F8           Client certificates
        F6 / Shift+F6       Next / previous pane        Tab          Next field
        F10                 Menu                        Shift+F10    Context menu
        Ctrl+L              Focus the URL bar           Ctrl+Q       Quit
        """);

    private void ShowWorkspaceInfo()
    {
        var info = $"Folder: {_workspace.RootPath}\n" +
                   $"Collections: {_workspace.Collections.Count} · Environments: {_workspace.Environments.Count} · Globals: {(_workspace.Globals is null ? "no" : "yes")}\n" +
                   $"Certificates: {_workspace.Certificates.Entries.Count} ({Path.GetRelativePath(_workspace.RootPath, _workspace.CertificatesPath)})\n" +
                   $"SSL verification: {(_workspace.Settings.StrictSsl ? "on" : "off")} · CA bundle: {_workspace.Settings.CaBundle ?? "none"}";
        if (_workspace.LoadErrors.Count > 0)
        {
            info += "\n\nCould not load:\n" + string.Join('\n', _workspace.LoadErrors);
        }

        Ui.Info(App!, "Workspace", info);
    }

    private void ShowAbout() => Ui.Info(App!, "About TermRest",
        $"TermRest {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\n" +
        "A terminal REST client with Postman-compatible collections and client-certificate support.\n" +
        $".NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");

    // ----- plumbing --------------------------------------------------------------------------

    private void ShowPopover(List<View> items, System.Drawing.Point? position)
    {
        if (App is null)
        {
            return;
        }

        if (_popover is not null)
        {
            App.Popovers?.DeRegister(_popover);
            _popover.Dispose();
        }

        _popover = new PopoverMenu(items);
        App.Popovers?.Register(_popover);
        _popover.MakeVisible(position);
    }

    private void UpdateTitles()
    {
        var dirty = _editor.IsModified || _collection?.IsDirty == true ? " ●" : "";
        _requestFrame.Title = _node is null
            ? $"Request (scratch){dirty}"
            : $"Request — {_node.Collection.Name} / {_node.PathInCollection}{dirty}";
        _requestFrame.SetNeedsDraw();
        _tree.RefreshItem(_collection ?? (object)_workspace);
    }

    private void Notify(string message)
    {
        _message.Title = message;
        _message.SetNeedsDraw();
    }

    private void Quit()
    {
        if (ConfirmUnsaved())
        {
            _quitConfirmed = true;
            App?.RequestStop();
        }
    }

    /// <summary>Returns false when the user cancels.</summary>
    private bool ConfirmUnsaved()
    {
        CommitEditor();
        if (!_workspace.HasUnsavedChanges)
        {
            return true;
        }

        var choice = MessageBox.Query(App!, "Unsaved changes", "Save changes to collections and environments?", "Cancel", "Don't save", "Save");
        switch (choice)
        {
            case 2:
                Save();
                return !_workspace.HasUnsavedChanges;
            case 1:
                return true;
            default:
                return false;
        }
    }

    protected override bool OnKeyDown(Key key)
    {
        // Esc is Terminal.Gui's default Quit key; in the main window it cancels a running request instead.
        if (key == Key.Esc)
        {
            _sending?.Cancel();
            return true;
        }

        if (key == Key.Enter.WithCtrl)
        {
            Send();
            return true;
        }

        return base.OnKeyDown(key);
    }

    protected override bool OnIsRunningChanging(bool oldIsRunning, bool newIsRunning)
    {
        if (oldIsRunning && !newIsRunning && !_quitConfirmed && !ConfirmUnsaved())
        {
            return true;
        }

        return base.OnIsRunningChanging(oldIsRunning, newIsRunning);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sending?.Cancel();
        }

        base.Dispose(disposing);
    }
}
