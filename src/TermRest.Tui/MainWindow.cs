using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.History;
using TermRest.Core.Http;
using TermRest.Core.Import;
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
    private readonly HistoryView _history;
    private readonly RequestEditor _editor;
    private readonly ResponseView _response;
    private readonly Shortcut _environmentShortcut;
    private readonly Shortcut _message;
    private PostmanCollection? _collection;
    private RequestNode? _node;
    private PostmanCollection? _scratch;
    private CancellationTokenSource? _sending;
    private bool _closed;
    private PopoverMenu? _popover;
    private bool _quitConfirmed;
    private bool _showingHistory;
    private bool _historyLoaded;

    /// <summary>The history entry shown in the editor (a detached request), if any.</summary>
    private HistoryEntry? _historyEntry;

    /// <summary>Name suggested when a detached request (cURL import, history) is added to a collection.</summary>
    private string? _detachedName;

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
        _history = new HistoryView { Visible = false };
        _sidebar.Add(_tree, _history);

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
            new Shortcut { Title = "History", Key = Key.F9, BindKeyToApplication = true, Action = ToggleHistory },
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
        _history.EntrySelected += (_, entry) => OpenHistoryEntry(entry);
        _history.EntryActivated += (_, entry) =>
        {
            OpenHistoryEntry(entry);
            _editor.FocusUrl();
        };
        _history.ContextMenuRequested += (_, position) => ShowHistoryContextMenu(position);
        _history.DeleteRequested += (_, entry) => DeleteHistoryEntry(entry);
        _editor.Modified += (_, _) => UpdateTitles();
        _editor.SendRequested += (_, _) => Send();
        _editor.ManageCertificatesRequested += (_, _) => ShowCertificates();
        _editor.CurlPasted += (_, command) => ImportCurl(command, intoCurrent: true);
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

    internal HistoryView HistoryPane => _history;

    internal bool ShowingHistory => _showingHistory;

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
            new MenuItem("Import c_URL command…", "", ShowCurlImport),
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
            new MenuItem("_Import from cURL…", "", ShowCurlImport),
            new MenuItem("_History", Key.F9, ToggleHistory),
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
        _historyEntry = null;
        _detachedName = null;
        _editor.Load(node.ReadSpec());
        _response.Clear();
        UpdateTitles();
    }

    private void StartScratchRequest()
    {
        _scratch ??= PostmanCollection.Create("Scratch");
        _collection = _scratch;
        _node = null;
        _historyEntry = null;
        _detachedName = null;
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

        // A curl command typed (or pasted keystroke by keystroke) into the URL bar is imported rather than sent.
        if (CurlImporter.LooksLikeCurl(_editor.Spec.Url))
        {
            ImportCurl(_editor.Spec.Url, intoCurrent: true);
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
        HttpExchange exchange;
        try
        {
            exchange = await _runner.SendAsync(prepared, cancellation.Token);
        }
        catch (Exception ex)
        {
            exchange = new HttpExchange(prepared) { ErrorMessage = ex.Message, Exception = ex };
        }
        finally
        {
            _sending = null;
        }

        // The window may have been closed (quit, workspace switch) while the request was in flight.
        if (_closed)
        {
            return;
        }

        _editor.SetSending(false);
        _response.ShowExchange(exchange);
        Notify(exchange.Response is null ? "" : $"{exchange.Request.Method} {exchange.Request.Uri?.Host} → {exchange.Response.StatusCode}");
        if (exchange.HistoryEntry is { } entry && _historyLoaded)
        {
            _history.Prepend(entry);
            UpdateSidebarTitle();
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

        if (_node is null && _editor.IsModified)
        {
            Notify("This request is not in a collection yet: press Ctrl+N (Request > New request) to add it to one.");
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

        if (Ui.PromptText(App!, "New request", $"Name (in {folder.Collection.Name}{(folder is CollectionRootNode ? "" : " / " + folder.PathInCollection)})", _detachedName ?? "New request") is not { Length: > 0 } name)
        {
            return;
        }

        CommitEditor();
        HttpRequestSpec? spec = null;
        if (_node is null)
        {
            // A scratch, imported or history request becomes the new request.
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
            _runner.ConfigureHistory();
            if (_historyLoaded)
            {
                LoadHistory();
                UpdateSidebarTitle();
            }

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
        if (Ui.PickFile(App!, "Import Postman collection (v2.0 / v2.1 JSON)", null, false, ("Postman collection", [".json"])) is { } path)
        {
            ImportCollectionFrom(path);
        }
    }

    internal void ImportCollectionFrom(string path)
    {
        try
        {
            CommitEditor();
            if (_workspace.FindCollection(path) is { } loaded)
            {
                // Picking a file that is already open means "reload it from disk".
                if (!loaded.IsDirty || Ui.Confirm(App!, "Reload collection",
                        $"'{loaded.Name}' is already open and has unsaved changes.\nReload it from disk and discard them?", "Reload", "Cancel"))
                {
                    ReloadCollection(loaded);
                }

                return;
            }

            var collection = _workspace.ImportCollection(path);
            _tree.Reload(_workspace);
            _tree.Select(collection);
            Notify($"Imported '{collection.Name}' into {Path.GetRelativePath(_workspace.RootPath, collection.FilePath!)}");
        }
        catch (Exception ex)
        {
            Ui.Error(App!, "Import failed", ex.Message);
        }
    }

    private void ReloadCollection(PostmanCollection loaded)
    {
        var reopen = _collection == loaded ? _node?.PathInCollection : null;
        if (_collection == loaded)
        {
            // Detach the editor first so nothing is written into the copy being replaced.
            _node = null;
            _collection = null;
            _editor.MarkClean();
        }

        var reloaded = _workspace.ReloadCollection(loaded);
        _tree.Reload(_workspace);
        if (reopen is not null && reloaded.FindRequest(reopen) is { } node)
        {
            _tree.Select(node);
            if (_node != node)
            {
                OpenRequest(node);
            }
        }
        else
        {
            _tree.Select(reloaded);
            if (_collection is null)
            {
                _editor.Load(new HttpRequestSpec());
                _response.Clear();
            }
        }

        UpdateTitles();
        Notify($"Reloaded '{reloaded.Name}' from disk.");
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

    // ----- history -------------------------------------------------------------------------------

    /// <summary>F9: the sidebar shows either the collections or the request history.</summary>
    private void ToggleHistory() => ShowHistory(!_showingHistory);

    internal void ShowHistory(bool show)
    {
        if (show && !_historyLoaded)
        {
            LoadHistory();
        }

        _showingHistory = show;
        _tree.Visible = !show;
        _history.Visible = show;
        UpdateSidebarTitle();
        if (show)
        {
            _history.FocusList();
        }
        else
        {
            _tree.SetFocus();
        }
    }

    private void LoadHistory()
    {
        _historyLoaded = true;
        List<HistoryEntry> entries = [];
        try
        {
            entries = _runner.History?.Load() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify($"History could not be read: {ex.Message}");
        }

        _history.SetEntries(entries, _runner.History is null ? "History is off (Tools > Settings)." : "No requests yet: send one with F5.");
    }

    /// <summary>Shows a sent request and its recorded response; it can be edited and re-sent, or saved with Ctrl+N.</summary>
    internal void OpenHistoryEntry(HistoryEntry entry)
    {
        CommitEditor();
        _node = null;

        // Variables keep resolving against the request's collection while it is still in the workspace.
        _collection = _workspace.Collections.FirstOrDefault(c => c.Name == entry.Collection) ?? (_scratch ??= PostmanCollection.Create("Scratch"));
        _historyEntry = entry;
        _detachedName = entry.RequestPath is null ? null : entry.Name;
        _editor.Load(entry.ReadSpec());
        byte[]? body = null;
        try
        {
            body = _runner.History?.ReadBody(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The entry is still useful without its body.
        }

        _response.ShowExchange(entry.ToExchange(body));
        UpdateTitles();
    }

    private void ShowHistoryContextMenu(System.Drawing.Point? position)
    {
        if (_history.Selected is not { } entry)
        {
            return;
        }

        var items = new List<View>
        {
            new MenuItem("_Open", "", () => OpenHistoryEntry(entry)),
            new MenuItem("_Send again", "", () =>
            {
                OpenHistoryEntry(entry);
                Send();
            }),
            new MenuItem("Save to _collection…", "", () =>
            {
                OpenHistoryEntry(entry);
                NewRequest();
            }),
            new MenuItem("Copy as c_URL", "", () =>
            {
                OpenHistoryEntry(entry);
                CopyAsCurl();
            }),
        };
        if (OriginalRequest(entry) is { } original)
        {
            items.Add(new MenuItem("Go to _request in collection", "", () =>
            {
                ShowHistory(false);
                ShowRequest(original);
            }));
        }

        items.Add(new Line());
        items.Add(new MenuItem("_Delete entry", "", () => DeleteHistoryEntry(entry)));
        items.Add(new MenuItem("C_lear history…", "", ClearHistory));
        ShowPopover(items, position);
    }

    private RequestNode? OriginalRequest(HistoryEntry entry) =>
        entry.RequestPath is { } path ? _workspace.Collections.FirstOrDefault(c => c.Name == entry.Collection)?.FindRequest(path) : null;

    private void DeleteHistoryEntry(HistoryEntry entry)
    {
        try
        {
            _runner.History?.Delete(entry.Id);
            _history.Remove(entry);
            UpdateSidebarTitle();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(App!, "History", ex.Message);
        }
    }

    private void ClearHistory()
    {
        if (_runner.History is not { } store || !Ui.Confirm(App!, "Clear history", $"Delete all {_history.Count} entries of this workspace's history?", "Clear", "Cancel"))
        {
            return;
        }

        try
        {
            store.Clear();
            _history.SetEntries([], "History cleared.");
            UpdateSidebarTitle();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(App!, "History", ex.Message);
        }
    }

    // ----- cURL import ---------------------------------------------------------------------------

    private void ShowCurlImport()
    {
        if (CurlImportDialog.Show(App!) is { } imported)
        {
            ApplyImportedCurl(imported, intoCurrent: false);
        }
    }

    /// <summary>
    /// Imports a curl command: pasted into the URL bar it replaces the current request (like Postman); from the menu
    /// it opens as a new request that Ctrl+N adds to a collection.
    /// </summary>
    internal void ImportCurl(string command, bool intoCurrent)
    {
        try
        {
            ApplyImportedCurl(CurlImporter.Import(command), intoCurrent);
        }
        catch (FormatException ex) when (intoCurrent)
        {
            Notify($"Not imported: {ex.Message}");
        }
        catch (FormatException ex)
        {
            Ui.Error(App!, "Not a curl command", ex.Message);
        }
    }

    private void ApplyImportedCurl(CurlImportResult imported, bool intoCurrent)
    {
        var spec = imported.Spec;
        if (intoCurrent)
        {
            spec.Description = _editor.Spec.Description;
            spec.PreRequestScript = _editor.Spec.PreRequestScript;
            spec.TestScript = _editor.Spec.TestScript;
            _editor.Replace(spec);
        }
        else
        {
            CommitEditor();
            _node = null;
            _historyEntry = null;
            _collection = _scratch ??= PostmanCollection.Create("Scratch");
            _detachedName = imported.SuggestedName;
            _editor.Replace(spec);
            _response.Clear();
        }

        UpdateTitles();
        var next = _node is null ? "Ctrl+N adds it to a collection." : "Ctrl+S saves it.";
        if (imported.Warnings.Count == 0)
        {
            Notify($"Imported from cURL: {spec.Method} {spec.Url}. {next}");
        }
        else if (intoCurrent)
        {
            // Pasting should not interrupt with a dialog; the notes are short enough for the status bar.
            Notify($"Imported from cURL ({imported.Warnings.Count} note(s): {string.Join(" ", imported.Warnings)}) {next}");
        }
        else
        {
            Notify($"Imported from cURL: {spec.Method} {spec.Url}. {next}");
            Ui.Info(App!, "cURL import notes", string.Join('\n', imported.Warnings.Select(w => "• " + w)));
        }
    }

    // ----- help ----------------------------------------------------------------------------------

    private void ShowKeys() => Ui.Info(App!, "Keyboard & mouse",
        """
        Mouse: click anything; drag pane borders to resize; right-click the tree or history for actions;
        double-click a table row to edit it; click the checkbox column to enable/disable a row.

        F5 / Enter in URL   Send (again to cancel)      Esc          Cancel a running request
        Ctrl+S              Save all changes            Ctrl+N       New request (keeps an imported one)
        F2                  Rename tree item            Del          Delete tree item / history entry
        F7                  Switch environment          F8           Client certificates
        F9                  Collections ⇄ History       F6 / Shift+F6  Next / previous pane
        F10                 Menu                        Shift+F10    Context menu
        Ctrl+L              Focus the URL bar           Ctrl+Q       Quit
        Paste a curl command into the URL bar to import it (also File > Import cURL command).
        """);

    private void ShowWorkspaceInfo()
    {
        var formats = string.Join(", ", _workspace.Collections.GroupBy(c => c.FormatName).Select(g => $"{g.Count()} {g.Key}"));
        var info = $"Folder: {_workspace.RootPath}\n" +
                   $"Collections: {_workspace.Collections.Count}{(formats.Length > 0 ? $" ({formats})" : "")} · Environments: {_workspace.Environments.Count} · Globals: {(_workspace.Globals is null ? "no" : "yes")}\n" +
                   $"New collections: {(_workspace.UsesV3Layout ? "Postman v3 YAML folders (postman/collections)" : "Postman v2.1 JSON files")}\n" +
                   $"Certificates: {_workspace.Certificates.Entries.Count} ({Path.GetRelativePath(_workspace.RootPath, _workspace.CertificatesPath)})\n" +
                   $"SSL verification: {(_workspace.Settings.StrictSsl ? "on" : "off")} · CA bundle: {_workspace.Settings.CaBundle ?? "none"}\n" +
                   $"History: {(_runner.History is { } history ? history.Folder : "off")}";
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
        _requestFrame.Title = _node is not null
            ? $"Request — {_node.Collection.Name} / {_node.PathInCollection}{dirty}"
            : _historyEntry is { } entry
                ? $"Request (from history · {entry.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}){dirty}"
                : $"Request ({(_detachedName is null ? "scratch" : "imported")}){dirty}";
        _requestFrame.SetNeedsDraw();
        _tree.RefreshItem(_collection ?? (object)_workspace);
    }

    private void UpdateSidebarTitle()
    {
        _sidebar.Title = _showingHistory ? $"History ({_history.Count})" : "Collections";
        _sidebar.SetNeedsDraw();
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
            _closed = true;
            _sending?.Cancel();
        }

        base.Dispose(disposing);
    }
}
