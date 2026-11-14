using System.Text;
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
using TermRest.Core.Recording;
using TermRest.Core.Scripting;
using TermRest.Core.Workspace;
using TermRest.Tui.Dialogs;
using TermRest.Tui.Theming;
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
    private readonly RecordingIndicator _recordIndicator;
    private readonly Shortcut _message;

    /// <summary>Cancels the requests still on their way when the window closes, those of requests no longer shown too.</summary>
    private readonly CancellationTokenSource _closing = new();
    private MenuItem? _recordMenuItem;
    private PostmanCollection? _collection;
    private RequestNode? _node;
    private PostmanCollection? _scratch;
    private CancellationTokenSource? _sending;

    /// <summary>Counts the requests shown in the editor, so that a response arriving for an earlier one is not shown under another.</summary>
    private int _opened;
    private bool _closed;
    private PopoverMenu? _popover;
    private bool _quitConfirmed;
    private bool _showingHistory;
    private bool _historyLoaded;

    /// <summary>The history entry shown in the editor (a detached request), if any.</summary>
    private HistoryEntry? _historyEntry;

    /// <summary>
    /// The request in its collection that the history entry shown was sent from, while it is still there: a request sent
    /// again from history runs with its folders' scripts and inherits their auth (it is not edited).
    /// </summary>
    private RequestNode? _historyOrigin;

    /// <summary>Name suggested when a detached request (cURL import, history) is added to a collection.</summary>
    private string? _detachedName;

    /// <summary>Where the last recording was saved this session (the next one is suggested there).</summary>
    private string? _recordingFolder;

    /// <summary>The recording error last shown in the status bar: each is shown once (see <see cref="UpdateRecording"/>).</summary>
    private string? _recordingErrorShown;

    /// <summary>What the tree showed for the current collection when <see cref="UpdateTitles"/> last refreshed it.</summary>
    private (object? Item, bool Dirty, string? Name) _treeTitleState;

    public MainWindow(ApiWorkspace workspace, RequestRunner runner)
    {
        ThemeCatalog.EnsureApplied();
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
            HotKeySpecifier = Ui.NoHotKey, // its title names the request
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

            // Where a relative body file is looked for when sending: the folder of the collection sending it.
            BaseDirectory = () => (_collection ?? _scratch)?.BaseDirectory ?? Environment.CurrentDirectory,

            // The request's scripts run with those of its collection and folders, in one language (a request typed into the
            // editor a delete emptied is a scratch request, as Send has it).
            ScriptPlanner = (scripts, settings) => _runner.PlanScripts(_collection ?? (_scratch ??= PostmanCollection.Create("Scratch")), SendContext, scripts, settings),
            DefaultScriptLanguage = _runner.ScriptLanguageOrder[0],
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

        // Window shortcuts, not application-wide ones: they must not act behind an open dialog. Names and messages are
        // shown as written.
        _environmentShortcut = Ui.AsWritten(new Shortcut { Title = EnvironmentLabel(), Key = Key.F7, Action = ShowEnvironmentMenu });
        _message = Ui.AsWritten(new Shortcut { Title = "", CanFocus = false });
        var statusBar = new StatusBar(
        [
            new Shortcut { Title = "Send", Key = Key.F5, Action = Send },
            new Shortcut { Title = "Save", Key = Key.S.WithCtrl, Action = Save },
            new Shortcut { Title = "New", Key = Key.N.WithCtrl, Action = NewRequest },
            _environmentShortcut,
            new Shortcut { Title = "Certs", Key = Key.F8, Action = ShowCertificates },
            new Shortcut { Title = "History", Key = Key.F9, Action = ToggleHistory },
            new Shortcut { Title = "Menu", Key = Key.F10 },
            new Shortcut { Title = "Quit", Key = Key.Q.WithCtrl, Action = Quit },
            _message,
        ]);

        // Top right, on the menu bar row: always in view, and a red badge with a running clock while recording.
        _recordIndicator = new RecordingIndicator();
        _recordIndicator.Clicked += (_, _) => ToggleRecording();
        ThemeCatalog.Changed += OnThemeChanged;

        Add(_menu, _sidebar, _requestFrame, _responseFrame, statusBar, _recordIndicator);

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
                // The request open already only goes to its URL bar: opening it again would clear its response.
                if (request != _node)
                {
                    OpenRequest(request);
                }

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
            Notify($"⚠ {workspace.LoadErrors.Count} problem(s) loading files or the color theme (Help > Workspace info)");
        }

        if (runner.BlockedProxy is not null)
        {
            const string notice = "The proxy in this workspace's settings (.termrest/settings.json) is not used until you allow it: Tools > Settings > Save";
            Notify(_message.Title.Length > 0 ? $"{_message.Title} · {notice}" : $"⚠ {notice}");
        }

        if (RequestRecorder.FindUnsaved().Count is > 0 and var unsaved)
        {
            var notice = $"{unsaved} unsaved recording(s) found: Tools > Recover unsaved recordings";
            Notify(_message.Title.Length > 0 ? $"{_message.Title} · {notice}" : $"⚠ {notice}");
        }
    }

    /// <summary>Set when the user opens another workspace (opened already): the host restarts the UI with it.</summary>
    public ApiWorkspace? NextWorkspace { get; private set; }

    /// <summary>Whether the request shown is on its way (the UI stays quick until its response is shown).</summary>
    internal bool IsSending => _sending is not null;

    // Test hooks (InternalsVisibleTo TermRest.Tui.Tests).
    internal RequestEditor Editor => _editor;

    internal ResponseView ResponsePane => _response;

    internal HistoryView HistoryPane => _history;

    internal bool ShowingHistory => _showingHistory;

    internal string StatusMessage => _message.Title;

    /// <summary>The text of the record button at the top right.</summary>
    internal string RecordButton => _recordIndicator.Caption;

    /// <summary>The title of the response pane (it says when the response shown was recorded).</summary>
    internal string ResponseTitle => _responseFrame.Title;

    /// <summary>What the Body tab says about the body file.</summary>
    internal string BodyFileStatus => _editor.BodyFileStatus;

    /// <summary>Uses a body file as if it was picked in the Body tab (for tests: the file dialog is Terminal.Gui's).</summary>
    internal void ChooseBodyFile(string path) => _editor.ChooseBodyFile(path);

    /// <summary>Selects and opens a request (used by snapshots and tests).</summary>
    internal void ShowRequest(RequestNode node)
    {
        if (OpenRequest(node))
        {
            _tree.Select(node);
        }
    }

    /// <summary>Sends the current request and shows the result without an event loop (used by snapshots).</summary>
    internal async Task SendAndShowAsync(CancellationToken cancellationToken = default)
    {
        var collection = _collection ?? (_scratch ??= PostmanCollection.Create("Scratch"));
        var exchange = await _runner.SendAsync(collection, SendContext, _editor.Spec, null, cancellationToken).ConfigureAwait(false);
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
            new Line(),
            new MenuItem("Collection or folder scrip_ts…", "", () => EditScripts(TargetFolder())),
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
            new MenuItem("Scri_pts…", "", ShowScriptSettings),
            new MenuItem("Color _theme…", "", ShowThemes),
            new MenuItem("Clear _cookies", "", ClearCookies),
            new Line(),
            _recordMenuItem = new MenuItem("Start _recording", Key.F12, ToggleRecording),
            new MenuItem("Recover _unsaved recordings…", "", RecoverRecordings),
        ]),
        new MenuBarItem("_Help",
        [
            new MenuItem("_Keyboard & mouse", "", ShowKeys),
            new MenuItem("_Scripting", "", ShowScriptingHelp),
            new MenuItem("_Workspace info", "", ShowWorkspaceInfo),
            new MenuItem("_About TermRest", "", ShowAbout),
        ]),
    ]);

    // ----- request lifecycle -----------------------------------------------------------------

    /// <summary>Opens a request in the editor; false when the user keeps the one there (see <see cref="ConfirmDiscardRequest"/>).</summary>
    private bool OpenRequest(RequestNode node)
    {
        if (!ConfirmDiscardRequest())
        {
            return false;
        }

        CommitEditor();
        if (node != _node)
        {
            DetachSending();
        }

        _collection = node.Collection;
        _node = node;
        _historyEntry = null;
        _historyOrigin = null;
        _detachedName = null;
        _editor.Load(node.ReadSpec());
        ClearResponse();
        UpdateTitles();
        return true;
    }

    private void StartScratchRequest()
    {
        _scratch ??= PostmanCollection.Create("Scratch");
        _collection = _scratch;
        _node = null;
        _historyEntry = null;
        _historyOrigin = null;
        _detachedName = null;
        _editor.Load(new HttpRequestSpec { Url = "https://postman-echo.com/get?hello=world" });
        ClearResponse();
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

    /// <summary>
    /// Before the request in the editor makes way for another: one that is not in a collection (scratch, imported, from
    /// history) and was changed would be lost, so the user may add it to a collection first. False when the user cancels.
    /// </summary>
    private bool ConfirmDiscardRequest()
    {
        if (_node is not null || !_editor.IsModified)
        {
            return true;
        }

        return MessageBox.Query(App!, "Unsaved request", "This request is not in a collection, so your changes to it would be lost.",
                "Cancel", "Discard", "Add to collection…") switch
        {
            2 => AddToCollection(),
            1 => true,
            _ => false,
        };
    }

    /// <summary>Adds the request in the editor to a collection (asking where and its name), then edits it there; false when cancelled.</summary>
    private bool AddToCollection()
    {
        if (AddRequest() is not { } node)
        {
            return false;
        }

        Adopt(node);
        return true;
    }

    /// <summary>
    /// The request in the editor, now <paramref name="node"/> in a collection, is edited there: it keeps its response, and
    /// one still on its way is shown under it. What the editor says about its context (inherited auth, scripts) is redone.
    /// </summary>
    private void Adopt(RequestNode node)
    {
        _collection = node.Collection;
        _node = node;
        _historyEntry = null;
        _historyOrigin = null;
        _detachedName = null;
        _editor.RefreshContext();
        UpdateTitles();
    }

    /// <summary>
    /// Sends the request shown, or cancels the one on its way. No exception may leave this async void method: .NET would
    /// post it to the UI's synchronization context, which throws it back at once, then throw it on the thread pool, ending
    /// TermRest (and losing unsaved changes). It goes where the main loop's unexpected errors go instead: shown and logged.
    /// </summary>
    private async void Send()
    {
        try
        {
            await SendAsync();
        }
        catch (Exception ex)
        {
            // Once the window no longer runs (quit, another workspace), there is nothing to show it in.
            TuiApp.ReportUnexpectedError(IsRunning ? App : null, ex);
        }
    }

    private async Task SendAsync()
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

        // Sending *** for a password could count as a failed sign-in (and lock the account): Cancel is the default.
        if (HasMaskedCredentials
            && MessageBox.Query(App!, "Masked credentials", "This request has *** for credentials the history masked. Enter them again before sending it.",
                "Send ***", "Cancel") != 0)
        {
            return;
        }

        var collection = _collection ?? (_scratch ??= PostmanCollection.Create("Scratch"));
        var previous = _response.Exchange;
        var opened = _opened;
        PreparedRequest? prepared = null;
        _response.ShowRunningScripts(); // replaced at once when there are no pre-request scripts
        SetResponseTitle(null);
        _editor.SetSending(true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _sending = cancellation;
        var recorder = _runner.Recorder;
        var recordedBefore = recorder?.Count ?? 0;
        HttpExchange exchange;
        try
        {
            // Scripts run first; the request is shown as soon as it is ready to go.
            exchange = await _runner.SendAsync(collection, SendContext, _editor.Spec, ready =>
            {
                prepared = ready;
                if (opened == _opened)
                {
                    _response.ShowSending(ready);
                }
            }, cancellation.Token);
        }
        catch (Exception ex)
        {
            if (prepared is null)
            {
                // It could not even be prepared (a certificate that does not load…): nothing was sent.
                if (_sending == cancellation)
                {
                    _sending = null;
                }

                if (_closed)
                {
                    return;
                }

                if (opened != _opened)
                {
                    Notify($"⚠ Earlier request not sent: {ex.Message}");
                    return;
                }

                _editor.SetSending(false);
                if (previous is null)
                {
                    _response.Clear();
                }
                else
                {
                    _response.ShowExchange(previous);
                }

                Ui.Error(App!, "Cannot send", ex.Message);
                return;
            }

            exchange = new HttpExchange(prepared) { ErrorMessage = ex.Message, Exception = ex };
        }
        finally
        {
            if (_sending == cancellation)
            {
                _sending = null;
            }
        }

        // The window may have been closed (quit, workspace switch) while the request was in flight.
        if (_closed)
        {
            return;
        }

        // Another request may have been opened meanwhile: the response is not shown under it (the history has it).
        var shown = opened == _opened;
        if (shown)
        {
            _editor.SetSending(false);
            _response.ShowExchange(exchange);
        }

        var recordedAs = recorder is not null && ReferenceEquals(recorder, _runner.Recorder) && recorder.Count > recordedBefore ? recorder.Count : (int?)null;
        if (shown)
        {
            SetResponseTitle(recordedAs);
        }

        var result = exchange.Response is null ? "" : $"{exchange.Request.Method} {exchange.Request.Uri?.Host} → {exchange.Response.StatusCode}";
        if (!shown)
        {
            result = $"Earlier request: {(result.Length > 0 ? result : exchange.ErrorMessage)}";
        }

        if (recordedAs is not null && exchange.Request.Body.HasFiles)
        {
            result += (result.Length == 0 ? "" : " · ") + "● recorded with the file's path, not its content";
        }

        Notify(result);
        UpdateRecording();
        if (exchange.HistoryEntry is { } entry && _historyLoaded)
        {
            _history.Prepend(entry);
            UpdateSidebarTitle();
        }
    }

    private void Save()
    {
        CommitEditor();
        var (saved, changed) = Ui.SaveEach(App!, _workspace.RootPath, SaveItems(_workspace, includeCollections: true));
        if (changed.Count > 0)
        {
            Notify($"⚠ Not saved: {string.Join(", ", changed)} changed on disk.");
        }
        else if (_node is null && _editor.IsModified)
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

    /// <summary>What saving writes: changed collections (when asked), environments and globals, then the settings.</summary>
    internal static IEnumerable<Ui.SaveItem> SaveItems(ApiWorkspace workspace, bool includeCollections)
    {
        var collections = includeCollections ? workspace.Collections.Where(c => c.IsDirty).ToList() : [];
        foreach (var collection in collections)
        {
            yield return new(Path.GetFileName(collection.FilePath!), overwrite => collection.Save(overwriteChanged: overwrite));
        }

        foreach (var environment in workspace.Environments.Where(e => e.IsDirty).ToList())
        {
            yield return new(Path.GetFileName(environment.FilePath!), overwrite => environment.Save(overwriteChanged: overwrite));
        }

        if (workspace.Globals is { IsDirty: true } globals)
        {
            yield return new(Path.GetFileName(globals.FilePath!), overwrite => globals.Save(overwriteChanged: overwrite));
        }

        yield return new(null, _ => workspace.SaveSettings());
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
        var adopting = _node is null; // the request in the editor becomes the new one (see AddRequest)
        if (AddRequest() is not { } node)
        {
            return;
        }

        if (adopting)
        {
            // Before the tree selects it, which would open it as another request (clearing its response, detaching its send).
            Adopt(node);
            _tree.Select(node);
        }
        else
        {
            _tree.Select(node);
            OpenRequest(node);
        }

        _editor.FocusUrl();
    }

    /// <summary>
    /// Asks for a name and adds a request to the selected folder (or a new collection): the request in the editor when it
    /// is not in a collection yet (scratch, imported, from history), else an empty one. Null when cancelled.
    /// </summary>
    private RequestNode? AddRequest()
    {
        // The request shown becomes the new one (it is not in a collection): with *** in it, the collection would send ***.
        if (HasMaskedCredentials
            && MessageBox.Query(App!, "Masked credentials", "This request has *** for credentials the history masked: added to a collection like this, it would keep sending ***. Type them again first.",
                "Add with ***", "Cancel") != 0)
        {
            return null;
        }

        var folder = TargetFolder();
        if (folder is null)
        {
            if (!NewCollection())
            {
                return null;
            }

            folder = _workspace.Collections[^1].RootNode;
        }

        if (Ui.PromptText(App!, "New request", $"Name (in {folder.Collection.Name}{(folder is CollectionRootNode ? "" : " / " + folder.PathInCollection)})", _detachedName ?? "New request") is not { Length: > 0 } name)
        {
            return null;
        }

        CommitEditor();
        HttpRequestSpec? spec = null;
        if (_node is null)
        {
            // A scratch, imported or history request becomes the new request.
            spec = _editor.Spec.Clone();
            _editor.MarkClean();
        }

        var node = folder.Collection.AddRequest(folder, name.Trim(), spec);
        _tree.RefreshItem(folder is CollectionRootNode ? folder.Collection : folder);
        return node;
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
        if (copy is not RequestNode request || OpenRequest(request))
        {
            _tree.Select(copy);
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

        // Only the request open in the editor makes way: one that is not in a collection stays.
        var openDeleted = _node is not null && (_node == node || _node.Ancestors().Contains(node));
        if (openDeleted)
        {
            _node = null;
            _editor.MarkClean();
        }

        // A request from history sent from the deleted request is sent on its own from now on.
        var forgetOrigin = _historyOrigin is not null && (_historyOrigin == node || _historyOrigin.Ancestors().Contains(node));
        var originGone = forgetOrigin ? ForgetHistoryOrigin() : null;

        node.Collection.Remove(node);
        _tree.RefreshItem(parent is CollectionRootNode ? parent.Collection : parent);
        _tree.RefreshItem(node.Collection);
        if (openDeleted)
        {
            DetachSending();
            _collection = null;
            _editor.Load(new HttpRequestSpec());
            ClearResponse();
        }
        else if (forgetOrigin)
        {
            _editor.RefreshContext(); // without the folders it inherited auth and scripts from
        }

        UpdateTitles();
        if (originGone is not null)
        {
            Notify(originGone);
        }
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

        if (item is PostmanCollection or FolderNode)
        {
            items.Add(new Line());
            items.Add(new MenuItem("Scrip_ts…", "", () => EditScripts(TargetFolder())));
        }

        if (item is RequestNode request)
        {
            // On the right-clicked request: the editor may show another one (from history, imported…) while it stays selected.
            items.Add(new Line());
            items.Add(new MenuItem("_Send", "", () =>
            {
                if (UseRequest(request))
                {
                    Send();
                }
            }));
            items.Add(new MenuItem("Copy as c_URL", "", () =>
            {
                if (UseRequest(request))
                {
                    CopyAsCurl();
                }
            }));
        }

        ShowPopover(items, position);
    }

    /// <summary>Opens a request for an action on it, unless it is open already: then the action takes the changes made to it.</summary>
    private bool UseRequest(RequestNode request) => request == _node || OpenRequest(request);

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
            items.Add(Ui.AsWritten(new MenuItem($"{(env == _workspace.ActiveEnvironment ? "● " : "  ")}{_workspace.DisplayName(env)}", "", () => SetEnvironment(env))));
        }

        items.Add(new Line());
        items.Add(new MenuItem("Manage environments…", "", () => ShowEnvironments()));
        ShowPopover(items, null);
    }

    private void SetEnvironment(PostmanEnvironment? environment)
    {
        _workspace.ActiveEnvironment = environment;
        Ui.TrySave(App!, _workspace.SaveSettings);
        _environmentShortcut.Title = EnvironmentLabel();
        _editor.RefreshCertificateInfo();
        Notify($"Environment: {(environment is null ? "none" : _workspace.DisplayName(environment))}");
    }

    private void ShowEnvironments()
    {
        try
        {
            EnvironmentsDialog.Show(App!, _workspace);
        }
        finally
        {
            _environmentShortcut.Title = EnvironmentLabel();
            _editor.RefreshCertificateInfo();
        }
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
        if (SettingsDialog.Show(App!, _workspace, _runner, out var saved))
        {
            _runner.ConfigureHistory();
            if (_historyLoaded)
            {
                LoadHistory();
                UpdateSidebarTitle();
            }

            // A recording that is on redacts the names added to the settings from now on, and keeps any removed.
            var added = _runner.Recorder?.AddRedactNames(_workspace.Settings.RecordingRedact ?? []) ?? [];
            var done = saved ? "Settings saved." : "Settings applied for this session, not saved.";
            Notify(added.Count == 0 ? done
                : $"{done} The recording also redacts {string.Join(", ", added)} from now on; requests already recorded are unchanged.");
        }
    }

    private void ShowThemes()
    {
        var theme = ThemeDialog.Show(App!, _workspace);
        SetNeedsDraw();
        if (theme is not null)
        {
            Notify($"Color theme: {theme.Name}{(_workspace.Settings.Theme is null ? "" : " (this workspace)")}");
        }
    }

    // ----- scripts ---------------------------------------------------------------------------

    /// <summary>Edits the scripts of a collection or folder: they run around every request inside.</summary>
    private void EditScripts(FolderNode? folder)
    {
        if (folder is null)
        {
            Notify("Select a collection or folder first.");
            return;
        }

        var collection = folder.Collection;
        var owner = folder is CollectionRootNode ? $"collection '{collection.Name}'" : $"folder '{folder.PathInCollection}'";
        if (ScriptsDialog.Edit(App!, owner, collection.GetScripts(folder), _runner.ScriptLanguageOrder[0]) is { } scripts && collection.SetScripts(folder, scripts))
        {
            _tree.RefreshItem(collection);
            _editor.RefreshScripts();
            UpdateTitles();
            Notify($"Scripts of {owner} changed (Ctrl+S saves the collection).");
        }
    }

    private void ShowScriptSettings()
    {
        ScriptsDialog.ShowRuntimes(App!, _workspace, _runner);
        _editor.DefaultScriptLanguage = _runner.ScriptLanguageOrder[0];
        _editor.RefreshScripts();
        Notify("Script languages, in order: " + string.Join(", ", _runner.ScriptLanguageOrder.Select(language =>
            $"{ScriptLanguage.Label(language)} ({_runner.GetRuntime(language) ?? "no runtime"})")));
    }

    private void ShowScriptingHelp() => Ui.ShowText(App!, "Scripting", ScriptingHelp);

    internal const string ScriptingHelp = """
        Scripts run in a runtime you choose in Tools › Scripts… for each workspace: Python for Python scripts, Node.js
        or Bun for JavaScript (Postman's pm.* scripts), the .NET SDK (10 or later) for C#, PowerShell (7 or later) for
        PowerShell. TermRest carries no script engine: one process per runtime runs the scripts, starting for the first
        one and stopping after 5 idle minutes. Each phase may take up to 30 seconds. Scripts whose runtime is not chosen
        do not run: the response's Scripts tab says so.

        When they run
          Pre-request scripts run before the request is prepared, then post-response (test) scripts once the response
          has arrived. Each phase runs the scripts of the collection, then its folders (outermost first), then the
          request, one at a time. A pre-request script that fails stops the request: it is not sent.
          Edit collection and folder scripts with right-click › Scripts…, and a request's in its Scripts tab.
          Values scripts set are kept until TermRest closes (Postman's "current values"): files never change.

        One language per request
          A collection, folder or request can have a pre-request and a post-response script in each language (Language
          shows them; a • marks those with scripts). A request runs the scripts of one language only, in one runtime:
          the one its Scripts tab chooses (Run in), or with Auto the first language of your order (Tools › Scripts…,
          ▲ ▼) that has all the scripts it runs. Scripts in other languages are kept, and do not run.
          The boxes above a request's scripts say whether its pre-request and post-response scripts run, its
          collection's and folders' too: ticked when there are some. Untick one to skip them; tick one without scripts
          and the request needs one. When no language has all the scripts a request runs, it is not sent.

        Python: the tr object
          tr.environment, tr.collection_variables, tr.globals
                       .get(name), .set(name, value), .unset(name), .has(name), .to_dict(), and tr.environment["name"].
          tr.variables   reads every scope (this request, environment, collection, globals); .set() is for this
                       request only; .replace_in("{{base}}/x") replaces {{variables}} in a text.
          tr.request     .method, .url, .headers (.get .set .add .remove), .body: change them before sending.
          tr.response    .status (or .code), .reason, .headers, .time (ms), .size, .text(), .json(), .content (bytes).
          tr.test(name, fn)  records a test that fails when fn raises (assert …); also @tr.test("name") on a function.
          tr.info        .phase ("prerequest" or "test"), .script, .request_name, .collection, .environment.
          print(…)       output goes to the response's Scripts tab, with the tests and errors.

          @tr.test("returns the new item")
          def _():
              assert tr.response.status == 201, tr.response.status
              assert tr.response.json()["name"] == "Ada"

        JavaScript: Postman's pm object
          pm.environment, pm.collectionVariables, pm.globals, pm.variables   .get .set .unset .has .toObject .replaceIn
          pm.request     .method, .url (.query, .toString()), .headers (.add .upsert .remove .get), .body.raw
          pm.response    .code, .status, .headers, .responseTime, .json(), .text(), .to.have.status(200), .to.be.ok …
          pm.test(name, fn), pm.expect(value).to.equal(…) (chai's assertions), pm.sendRequest(…), pm.info, console.log
          The old API works too: tests["name"] = …, postman.setEnvironmentVariable(…), responseBody, responseCode.
          require() loads Node.js modules and the packages installed in the workspace (npm install lodash crypto-js …);
          _, CryptoJS, cheerio and tv4 load those packages when first used, as Postman's globals.

          pm.test("returns the new item", () => {
              pm.response.to.have.status(201);
              pm.expect(pm.response.json().name).to.eql("Ada");
          });

        C#: the tr object, as in Python
          A script is the body of an async method: statements, local functions, await. It may start with using
          directives and #:package Name@1.2.3 (NuGet packages; also #:project, #:property). TermRest builds the
          scripts of a collection into one small program with dotnet: the first build takes a few seconds, a change
          about a second, and built programs are kept, so they start at once next time. Builds leave the SDK's compiler
          server running for 10 minutes (100 MB or more, as any dotnet build does): Stop scripts in Tools › Scripts…
          stops it.
          tr.Environment, tr.CollectionVariables, tr.Globals   ["name"], .Get .Set .Unset .Has .ToDictionary
          tr.Variables   reads every scope; .Set() is for this request only; .ReplaceIn("{{base}}/x").
          tr.Request     .Method, .Url, .Headers (.Get .Set .Add .Remove, ["name"]), .Body: change them before sending.
          tr.Response    .Status (or .Code), .Reason, .Headers, .Time (ms), .Size, .Text(), .Json() (a JsonNode), .Content.
          tr.Test(name, () => …) (async too), tr.Assert(condition), tr.AssertEqual(expected, actual), tr.Info.
          Console.WriteLine(…) output goes to the Scripts tab.

          tr.Test("returns the new item", () =>
          {
              tr.AssertEqual(201, tr.Response.Status);
              tr.AssertEqual("Ada", tr.Response.Json()["name"]);
          });

        PowerShell: the $tr object, as in C#
          $tr.Environment, $tr.CollectionVariables, $tr.Globals   .Get(name) .Set(name, value) .Unset .Has .ToHashtable
          $tr.Variables  reads every scope; .Set() is for this request only; .ReplaceIn('{{base}}/x').
          $tr.Request    .Method, .Url, .Headers (.Get .Set .Add .Remove .Items()), .Body: change them before sending.
          $tr.Response   .Status (or .Code), .Reason, .Headers, .Time (ms), .Size, .Text(), .Json() (ConvertFrom-Json),
                       .Content().
          $tr.Test(name, { … }), $tr.Assert(condition), $tr.AssertEqual(expected, actual), $tr.Info.
          Output, Write-Host and warnings go to the Scripts tab (output inside $tr.Test blocks is dropped: use Write-Host
          there). Errors stop the script ($ErrorActionPreference is Stop). Your profile is not loaded; modules load as
          you import them.

          $tr.Test('returns the new item', {
              $tr.AssertEqual(201, $tr.Response.Status)
              $tr.AssertEqual('Ada', $tr.Response.Json().name)
          })

        The runtimes run in the workspace folder, so scripts can import your own modules from it. Scripts run with your
        permissions: choose runtimes only for workspaces whose collections you trust.
        """;

    // ----- recording -------------------------------------------------------------------------

    /// <summary>Starts recording, or stops it and asks where to save it.</summary>
    private void ToggleRecording()
    {
        if (_runner.Recorder is { } active)
        {
            StopRecording(active, RecordingDialogPurpose.Stop);
            return;
        }

        // The settings may name more values to redact: a recording made without them could keep those secrets.
        if (_workspace.SettingsError is { } settingsError)
        {
            Ui.Error(App!, "Recording", "The recording could not start: the workspace settings could not be read, so the extra names to redact " +
                $"(recordingRedact) are unknown. Fix or delete the file, then open the workspace again.\n\n{settingsError}");
            return;
        }

        RequestRecorder recorder;
        try
        {
            recorder = RequestRecorder.Start(new RecordingOptions
            {
                WorkspaceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(_workspace.RootPath)),
                WorkspaceRoot = _workspace.RootPath,
                RedactNames = _workspace.Settings.RecordingRedact ?? [],
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(App!, "Recording", $"The recording could not start: {ex.Message}");
            return;
        }

        recorder.Changed += OnRecordingChanged;
        _runner.Recorder = recorder;
        UpdateRecording();
        App?.AddTimeout(TimeSpan.FromMilliseconds(500), () => TickRecording(recorder));
        Notify("● Recording the requests you send (secrets redacted). F12 or the REC badge stops it and saves a HAR file.");
    }

    /// <summary>Asks where to save the recording. Returns false when the user goes on recording (or does not close after all).</summary>
    private bool StopRecording(RequestRecorder recorder, RecordingDialogPurpose purpose)
    {
        var path = RecordingDialog.DefaultPath(_recordingFolder ?? _workspace.RootPath, recorder.StartedAt);
        var (result, saved) = RecordingDialog.Show(App!, recorder, path, purpose);
        if (result == RecordingDialogResult.Kept)
        {
            UpdateRecording();
            return false;
        }

        recorder.Changed -= OnRecordingChanged;
        if (ReferenceEquals(_runner.Recorder, recorder))
        {
            _runner.Recorder = null;
        }

        UpdateRecording();
        Notify(saved is null ? "Recording discarded." : SavedMessage(saved));
        return true;
    }

    private void RecoverRecordings()
    {
        var unsaved = RequestRecorder.FindUnsaved();
        if (unsaved.Count == 0)
        {
            Ui.Info(App!, "Unsaved recordings", "There are no unsaved recordings.\n\nRecordings that TermRest could not save (it was closed or crashed while recording) are offered here.");
            return;
        }

        foreach (var item in unsaved)
        {
            RequestRecorder recovered;
            try
            {
                recovered = RequestRecorder.Recover(item);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Ui.Error(App!, "Unsaved recording", $"The recording in {item.TempDirectory} could not be opened: {ex.Message}");
                continue;
            }

            var folder = _recordingFolder ?? (Directory.Exists(item.WorkspaceRoot) ? item.WorkspaceRoot : _workspace.RootPath);
            var (result, saved) = RecordingDialog.Show(App!, recovered, RecordingDialog.DefaultPath(folder, item.StartedAt), RecordingDialogPurpose.Recover);
            if (result == RecordingDialogResult.Kept)
            {
                recovered.Dispose(); // kept for next time
                Notify("Unsaved recordings are kept: Tools > Recover unsaved recordings.");
                return;
            }

            Notify(saved is null ? "Unsaved recording discarded." : SavedMessage(saved));
        }
    }

    private string SavedMessage(RecordingResult saved)
    {
        _recordingFolder = Path.GetDirectoryName(saved.Path);
        var requests = saved.Requests == 1 ? "1 request" : $"{saved.Requests} requests";
        Ui.ShowText(App!, "Recording saved",
            $"{saved.Path}\n\n{requests}, {HttpExchange.FormatSize(saved.Bytes)}{(saved.Redactions > 0 ? $", {saved.Redactions} secret value(s) redacted" : "")}.\n" +
            "Open it in browser dev tools (Network > Import HAR), any HAR viewer, or a text editor.");
        return $"✓ Recording saved: {saved.Path} ({requests}, {HttpExchange.FormatSize(saved.Bytes)})";
    }

    /// <summary>Raised on any thread when the recorder wrote (or failed to write) a request.</summary>
    private void OnRecordingChanged(object? sender, EventArgs e)
    {
        // Not Invoke: from another thread it waits for the UI thread, which may itself be waiting for the recorder.
        if (!_closed)
        {
            App?.AddTimeout(TimeSpan.Zero, () =>
            {
                UpdateRecording();
                return false;
            });
        }
    }

    private void UpdateRecording()
    {
        if (_closed)
        {
            return;
        }

        _editor.SetRecording(_runner.Recorder is not null);
        if (_runner.Recorder is { } recorder)
        {
            _recordIndicator.ShowRecording(recorder.StartedAt, recorder.Count);
            _recordMenuItem?.Title = "Stop _recording and save…";

            // Once, not again over every later message (a response's status). Compared by reference: each failure sets
            // a new message, so a request that cannot be recorded for the same reason again is said again.
            if (recorder.LastError is { } error && !ReferenceEquals(error, _recordingErrorShown))
            {
                _recordingErrorShown = error;
                Notify("⚠ " + error);
            }
        }
        else
        {
            _recordIndicator.ShowIdle();
            _recordMenuItem?.Title = "Start _recording";
            SetResponseTitle(null);
        }
    }

    /// <summary>Blinks the recording badge and advances its clock; stops by itself when that recording ends.</summary>
    private bool TickRecording(RequestRecorder recorder)
    {
        if (_closed || !ReferenceEquals(_runner.Recorder, recorder))
        {
            return false;
        }

        _recordIndicator.Tick();
        return true;
    }

    /// <summary>The response pane's title: says whether (and as which request) the response shown was recorded.</summary>
    private void SetResponseTitle(int? recordedAs)
    {
        var title = recordedAs is { } number ? $"Response · ● recorded #{number}" : "Response";
        if (_responseFrame.Title != title)
        {
            _responseFrame.Title = title;
        }
    }

    private void ClearResponse()
    {
        _response.Clear();
        SetResponseTitle(null);
    }

    /// <summary>
    /// Another request is shown in the editor: a request still on its way for the one before goes on (its response goes
    /// to the history and the status bar, not under this one), and Send sends this one.
    /// </summary>
    private void DetachSending()
    {
        _opened++;
        if (_sending is not null)
        {
            _sending = null;
            _editor.SetSending(false);
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => _recordIndicator.SetNeedsDraw();

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
        // A v3 collection's or folder's credential sets: the URL selects one when the request is sent.
        static string Describe(AuthSpec auth) => auth.CredentialSets is { } sets
            ? $"the first of its credential sets ({string.Join(", ", sets.Select(s => s.Name))}) whose URL rules match"
            : AuthSpec.DisplayName(auth.Type) ?? auth.Type;

        if (SendContext is not { } node)
        {
            return _collection?.RootNode.Auth is { } rootAuth ? $"Collection auth: {Describe(rootAuth)}" : "No auth is set on the collection.";
        }

        foreach (var folder in node.Ancestors())
        {
            if (folder.Auth is { } auth)
            {
                var source = folder is CollectionRootNode ? "the collection" : $"folder '{folder.Name}'";
                return auth.CredentialSets is null ? $"Currently: {Describe(auth)} from {source}." : $"Currently: from {source}, {Describe(auth)}.";
            }
        }

        return "No folder or collection auth is set, so no auth is sent.";
    }

    private string EnvironmentLabel() => $"Env: {(_workspace.ActiveEnvironment is { } active ? _workspace.DisplayName(active) : "none")}";

    // ----- workspace ---------------------------------------------------------------------------

    private void OpenWorkspace()
    {
        if (Ui.PickFile(App!, "Open workspace folder", _workspace.RootPath, directory: true) is { } path)
        {
            OpenWorkspaceFrom(path);
        }
    }

    /// <summary>Opens another workspace (after asking about unsaved changes): the host restarts the UI with it.</summary>
    internal void OpenWorkspaceFrom(string path)
    {
        if (!ConfirmUnsaved())
        {
            return;
        }

        try
        {
            NextWorkspace = ApiWorkspace.Open(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(App!, "Cannot open workspace", ex.Message);
            return;
        }

        _quitConfirmed = true;
        RequestStop();
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

            var collection = _workspace.ImportCollection(path, out var secretValuesLeftOut);
            _tree.Reload(_workspace);
            _tree.Select(collection);
            var leftOut = secretValuesLeftOut.Count == 0 ? "" : $" · ⚠ the values of its secret variables ({string.Join(", ", secretValuesLeftOut)}) are not written, as in Postman's v3 collections: set them in an environment";
            Notify($"Imported '{collection.Name}' into {Path.GetRelativePath(_workspace.RootPath, collection.FilePath!)}{leftOut}");
        }
        catch (Exception ex)
        {
            Ui.Error(App!, "Import failed", ex.Message);
        }
    }

    private void ReloadCollection(PostmanCollection loaded)
    {
        var reopen = _collection == loaded ? _node?.PathInCollection : null;

        // A request from history stays in the editor, with the reloaded collection's variables.
        var fromHistory = _collection == loaded && _node is null;

        // Loaded first: a file that no longer loads throws, and the editor goes on editing its request.
        var reloaded = _workspace.ReloadCollection(loaded);
        var detached = _collection == loaded && !fromHistory;
        if (detached)
        {
            // Detach the editor so nothing is written into the copy that was replaced.
            _node = null;
            _collection = null;
            _editor.MarkClean();
        }

        string? originGone = null;
        if (fromHistory)
        {
            _collection = reloaded;
            if (_historyEntry is { } entry && OriginalRequest(entry, reloaded) is { } original)
            {
                _historyOrigin = original;
            }
            else
            {
                originGone = ForgetHistoryOrigin();
            }

            _editor.RefreshContext(); // the reloaded folders' auth and scripts
        }

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

            // Only the editor this reload detached: a request typed into the one a delete emptied has nothing to do with it.
            if (detached)
            {
                DetachSending();
                _editor.Load(new HttpRequestSpec());
                ClearResponse();
            }
        }

        UpdateTitles();
        Notify($"Reloaded '{reloaded.Name}' from disk.{(originGone is null ? "" : " " + originGone)}");
    }

    private void ImportEnvironment()
    {
        if (Ui.PickFile(App!, "Import Postman environment", null, false, ("Postman environment", [".json", ".yaml", ".yml"])) is not { } path)
        {
            return;
        }

        try
        {
            var environment = _workspace.ImportEnvironment(path);

            // The active environment's label can change: one of the same name adds its file name, its own file a new name.
            _environmentShortcut.Title = EnvironmentLabel();
            _editor.RefreshCertificateInfo();
            Notify(environment.IsGlobals ? $"Imported the workspace's globals: '{environment.Name}'" : $"Imported environment '{_workspace.DisplayName(environment)}'");
        }
        catch (Exception ex)
        {
            Ui.Error(App!, "Import failed", ex.Message);
        }
    }

    private void CopyAsCurl()
    {
        // As Send: a request typed into the editor a delete emptied is a scratch request.
        var prepared = _runner.Prepare(_collection ?? (_scratch ??= PostmanCollection.Create("Scratch")), SendContext, _editor.Spec);
        if (prepared.Errors.Count > 0)
        {
            // Not a request TermRest would send (an invalid URL, a missing file, a line break in a header): no command for it,
            // as on the command line (a command for it would send something else, such as an empty body).
            Ui.Error(App!, "Copy as cURL", string.Join("\n", prepared.Errors));
            return;
        }

        string curl;
        try
        {
            curl = CurlExporter.Export(prepared);
        }
        catch (NotSupportedException ex)
        {
            Ui.Error(App!, "Copy as cURL", ex.Message);
            return;
        }

        var masked = HasMaskedCredentials ? " It has *** for the credentials the history masked." : "";
        var warnings = prepared.Warnings.Count == 0 ? "" : " ⚠ " + string.Join(" ", prepared.Warnings);
        if (Ui.CopyToClipboard(App, curl))
        {
            Notify("cURL command copied to the clipboard." + masked + warnings);
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

        _history.Limit = _runner.History?.MaxEntries ?? int.MaxValue;
        _history.SetEntries(entries, _runner.History is null ? "History is off (Tools > Settings)." : "No requests yet: send one with F5.");
    }

    /// <summary>
    /// Shows a sent request and its recorded response; it can be edited and re-sent, or saved with Ctrl+N. False when the
    /// user keeps the request open now (see <see cref="ConfirmDiscardRequest"/>).
    /// </summary>
    internal bool OpenHistoryEntry(HistoryEntry entry)
    {
        // Open already, unchanged: it may be on its way (Send again), and its response is to be shown here.
        if (entry == _historyEntry && !_editor.IsModified)
        {
            return true;
        }

        if (!ConfirmDiscardRequest())
        {
            return false;
        }

        CommitEditor();
        DetachSending();
        _node = null;

        // Variables keep resolving against the request's collection while it is still in the workspace. When several have its
        // name and which one it was is not known, the request is sent on its own.
        var collection = CollectionOf(entry, out var namesakes);
        _collection = collection ?? (_scratch ??= PostmanCollection.Create("Scratch"));
        _historyEntry = entry;
        _detachedName = entry.RequestPath is null ? null : entry.Name;
        var spec = entry.ReadSpec();

        // Sent again, it runs as its request in the collection does (with the scripts of its folders), while that is still
        // there. Auth it inherited is taken from its folders again: the history has a masked copy of it.
        _historyOrigin = OriginalRequest(entry, collection);
        if (entry.AuthInherited && _historyOrigin is not null)
        {
            spec.Auth = null;
        }

        _editor.Load(spec);
        byte[]? body = null;
        try
        {
            body = _runner.History?.ReadBody(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The entry is still useful without its body.
        }

        _response.ShowExchange(entry.ToExchange(body), fromHistory: true);
        SetResponseTitle(null);
        UpdateTitles();
        var masked = HasMaskedCredentials ? "Credentials are *** in the history: type them again before sending." : null;
        if (namesakes > 1)
        {
            var unknown = $"{namesakes} collections are named '{entry.Collection}' and which one this request was sent from is not known: it is sent on its own";
            Notify(masked is null ? $"⚠ {unknown}, without their variables and scripts." : $"⚠ {unknown}. {masked}");
        }
        else if (masked is not null)
        {
            Notify("⚠ " + masked);
        }

        return true;
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
                if (UseHistoryEntry(entry))
                {
                    Send();
                }
            }),
            new MenuItem("Save to _collection…", "", () =>
            {
                if (UseHistoryEntry(entry))
                {
                    NewRequest();
                }
            }),
            new MenuItem("Copy as c_URL", "", () =>
            {
                if (UseHistoryEntry(entry))
                {
                    CopyAsCurl();
                }
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

    /// <summary>
    /// The request in its collection a history entry was sent from, while it is there: the one at its exact path. Unlike
    /// <see cref="PostmanCollection.FindRequest"/>, never one found by its name alone, which may be another request, in
    /// a folder with other auth and scripts.
    /// </summary>
    private RequestNode? OriginalRequest(HistoryEntry entry) => OriginalRequest(entry, CollectionOf(entry, out _));

    /// <param name="collection">The entry's collection, if it is loaded (see <see cref="CollectionOf"/>).</param>
    private static RequestNode? OriginalRequest(HistoryEntry entry, PostmanCollection? collection)
    {
        if (entry.RequestPath is not { } path || collection is null)
        {
            return null;
        }

        // Names may hold slashes, so requests in different folders can share a path: then which one it was is not known.
        RequestNode? found = null;
        foreach (var request in collection.AllRequests())
        {
            if (request.PathInCollection == path)
            {
                if (found is not null && found.Parent != request.Parent)
                {
                    return null;
                }

                found ??= request;
            }
        }

        return found;
    }

    /// <summary>
    /// The collection a history entry was sent from, while it is loaded: the one in its file; else (an entry of an earlier
    /// version, a file renamed since) the one with its name, while no other has that name. Null when several do (then
    /// <paramref name="namesakes"/> says how many): which one it was is not known.
    /// </summary>
    private PostmanCollection? CollectionOf(HistoryEntry entry, out int namesakes)
    {
        namesakes = 0;
        if (entry.CollectionFile is { } file
            && _workspace.Collections.FirstOrDefault(c => string.Equals(_workspace.FileOf(c), file, StringComparison.OrdinalIgnoreCase)) is { } collection)
        {
            return collection;
        }

        var named = _workspace.Collections.Where(c => c.Name == entry.Collection).ToList();
        namesakes = named.Count;
        return namesakes == 1 ? named[0] : null;
    }

    /// <summary>Opens a history entry for an action on it, unless it is open already: then the action takes the changes made to it.</summary>
    private bool UseHistoryEntry(HistoryEntry entry) => entry == _historyEntry || OpenHistoryEntry(entry);

    /// <summary>Whether the request shown still has *** for credentials the history masked (see <see cref="HistoryEntry.CredentialsMasked"/>).</summary>
    private bool HasMaskedCredentials => _historyEntry is { CredentialsMasked: true } && HistoryEntry.HasMaskedCredentials(_editor.Spec);

    /// <summary>The request whose collection context (folder scripts, inherited auth) the request shown is sent in.</summary>
    private RequestNode? SendContext => _node ?? _historyOrigin;

    /// <summary>
    /// The request the history entry shown was sent from is gone (deleted, or not found when its collection was reloaded):
    /// the entry is sent on its own from now on. When it inherited its auth there and still inherits it, the auth it was
    /// sent with is put back: inheriting now would take the collection's auth, which may be meant for another host. Other
    /// changes made to the request are kept. Returns what to tell the user, or null when its auth stays as it is.
    /// </summary>
    private string? ForgetHistoryOrigin()
    {
        _historyOrigin = null;
        if (_node is not null || _historyEntry is not { AuthInherited: true } entry || _editor.Spec.Auth is not null
            || entry.ReadSpec().Auth is not { } auth)
        {
            return null;
        }

        var spec = _editor.Spec;
        spec.Auth = auth;
        if (_editor.IsModified)
        {
            _editor.Replace(spec);
        }
        else
        {
            _editor.Load(spec);
        }

        var gone = $"'{entry.RequestPath}' is no longer in its collection: this request now has the auth it was sent with";
        return HasMaskedCredentials ? $"⚠ {gone}. Credentials are *** in the history: type them again before sending." : $"{gone} (Auth tab).";
    }

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
            var bodiesLeft = store.Clear();
            _history.SetEntries([], "History cleared.");
            UpdateSidebarTitle();
            if (bodiesLeft is not null)
            {
                Ui.Error(App!, "History", $"History cleared, but some response bodies could not be deleted: {bodiesLeft.Message}");
            }
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
            // Pasted into a request from history, a command is a request of its own, as one imported from the menu: not the
            // request the entry was sent from, whose auth and scripts (its own and its folders') are not meant for the host it
            // names.
            var detach = _node is null && _historyEntry is not null;
            if (detach)
            {
                _historyEntry = null;
                _historyOrigin = null;
                _collection = _scratch ??= PostmanCollection.Create("Scratch");
                _detachedName = imported.SuggestedName;
            }
            else
            {
                spec.Description = _editor.Spec.Description;
                spec.Scripts = _editor.Spec.Scripts;
                spec.ScriptSettings = _editor.Spec.ScriptSettings;
            }

            _editor.Replace(spec);
            if (detach)
            {
                _editor.RefreshContext();
            }
        }
        else
        {
            if (!ConfirmDiscardRequest())
            {
                return;
            }

            CommitEditor();
            DetachSending();
            _node = null;
            _historyEntry = null;
            _historyOrigin = null;
            _collection = _scratch ??= PostmanCollection.Create("Scratch");
            _detachedName = imported.SuggestedName;
            _editor.Replace(spec);
            ClearResponse();
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

    /// <summary>The keyboard shortcuts in Help › Keyboard &amp; mouse (two columns: the first half, then the second).</summary>
    private static readonly (string Keys, string Action)[] Shortcuts =
    [
        ("F5, Ctrl+Enter", "Send (again to cancel)"),
        ("Esc", "Cancel a running request"),
        ("Ctrl+S", "Save all changes"),
        ("Ctrl+N", "New request"),
        ("F2", "Rename tree item"),
        ("Del", "Delete item, row or entry"),
        ("Insert", "Add a table row"),
        ("F7", "Switch environment"),
        ("F8", "Client certificates"),
        ("F9", "Collections ⇄ History"),
        ("F12", "Start / stop recording"),
        ("F6, Shift+F6", "Next / previous pane"),
        ("Tab, Shift+Tab", "Next / previous field"),
        ("←, → (tab row)", "Previous / next tab"),
        ("F10", "Menu"),
        ("Shift+F10", "Context menu"),
        ("Ctrl+L", "Focus the URL bar"),
        ("Ctrl+Q", "Quit"),
    ];

    private void ShowKeys() => Ui.ShowText(App!, "Keyboard & mouse", KeysHelp(App?.Driver?.Cols ?? 120));

    /// <summary>The help text, with the shortcuts in aligned columns: two, or one when two do not fit the screen.</summary>
    internal static string KeysHelp(int screenColumns)
    {
        var keyWidth = Shortcuts.Max(s => s.Keys.Length) + 2;
        var half = (Shortcuts.Length + 1) / 2;
        var twoColumns = 2 + keyWidth + Shortcuts.Take(half).Max(s => s.Action.Length) + 4 + keyWidth + Shortcuts.Skip(half).Max(s => s.Action.Length);
        var columns = screenColumns >= twoColumns + 8 ? 2 : 1; // + the dialog's frame and margins
        var rows = (Shortcuts.Length + columns - 1) / columns;
        var actionWidth = Shortcuts.Take(rows).Max(s => s.Action.Length) + 4;
        var builder = new StringBuilder("Keyboard\n");
        for (var row = 0; row < rows; row++)
        {
            var line = new StringBuilder("  ");
            for (var column = 0; column < columns && (column * rows) + row < Shortcuts.Length; column++)
            {
                var (keys, action) = Shortcuts[(column * rows) + row];
                line.Append(keys.PadRight(keyWidth)).Append(action.PadRight(actionWidth));
            }

            builder.Append(line.ToString().TrimEnd()).Append('\n');
        }

        builder.Append("""

            Mouse
              Click anything; drag pane borders to resize.
              Right-click the tree or the history for actions; double-click a table row to edit it.
              Click the checkbox of a row to turn it on or off.

            Enter in the URL bar sends too. Paste a curl command into it to import the command.
            F12 records the requests you send into a HAR file, with secrets redacted.
            """);
        return builder.ToString().ReplaceLineEndings("\n");
    }

    private void ShowWorkspaceInfo()
    {
        var formats = string.Join(", ", _workspace.Collections.GroupBy(c => c.FormatName).Select(g => $"{g.Count()} {g.Key}"));
        var info = $"Folder: {_workspace.RootPath}\n" +
                   $"Collections: {_workspace.Collections.Count}{(formats.Length > 0 ? $" ({formats})" : "")} · Environments: {_workspace.Environments.Count} · Globals: {(_workspace.Globals is null ? "no" : "yes")}\n" +
                   $"New collections: {(_workspace.UsesV3Layout ? "Postman v3 YAML folders (postman/collections)" : "Postman v2.1 JSON files")}\n" +
                   $"Long YAML lines: {WorkspaceSettings.DescribeYamlLineWidth(_workspace.Settings.YamlLineWidth)}{DescribeDetectedWrapping()}\n" +
                   $"Color theme: {ThemeCatalog.Current.Name} ({(ThemeCatalog.Current.FilePath ?? "built in")}){(_workspace.Settings.Theme is null ? "" : ", chosen for this workspace")}\n" +
                   $"Certificates: {_workspace.Certificates.Entries.Count} ({Path.GetRelativePath(_workspace.RootPath, _workspace.CertificatesPath)})\n" +
                   $"SSL verification: {(_workspace.Settings.StrictSsl ? "on" : "off")} · CA bundle: {_workspace.Settings.CaBundle ?? "none"}\n" +
                   $"Proxy: {DescribeProxy()}\n" +
                   $"History: {(_runner.History is { } history ? history.Folder : "off")}\n" +
                   $"Recording: {(_runner.Recorder is { } recorder ? $"on since {recorder.StartedAt:HH:mm:ss}, {recorder.Count} request(s), temporary files in {recorder.TempDirectory}" : $"off (temporary files go to {RequestRecorder.DefaultTempRoot()})")}";
        if (_workspace.LoadErrors.Count > 0)
        {
            info += "\n\nProblems while loading:\n" + string.Join('\n', _workspace.LoadErrors);
        }

        Ui.ShowText(App!, "Workspace", info);
    }

    /// <summary>The proxy requests go through (its password masked), and a proxy of the workspace's settings that is not allowed.</summary>
    private string DescribeProxy()
    {
        const string system = "system (OS / HTTPS_PROXY settings)";
        var proxy = _workspace.Settings.Proxy?.Trim();
        if (!WorkspaceSettings.IsProxyUrl(proxy))
        {
            return string.Equals(proxy, "none", StringComparison.OrdinalIgnoreCase) ? "none" : system;
        }

        var url = PostmanUrl.MaskCredentials(proxy);
        return _runner.BlockedProxy is null ? url : $"{system}, not the workspace's {url} until you allow it: Tools > Settings > Save";
    }

    /// <summary>When wrapping follows the files: the YAML collections whose files are wrapped.</summary>
    private string DescribeDetectedWrapping()
    {
        if (_workspace.Settings.YamlLineWidth is not null)
        {
            return "";
        }

        var wrapped = _workspace.Collections.Where(c => c.EffectiveYamlLineWidth is > 0).Select(c => $"{c.Name} ({c.EffectiveYamlLineWidth} columns)").ToList();
        return wrapped.Count == 0 ? "" : "; wrapped: " + string.Join(", ", wrapped);
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
        var title = _node is not null
            ? $"Request — {_workspace.DisplayName(_node.Collection)} / {_node.PathInCollection}{dirty}"
            : _historyEntry is { } entry
                ? $"Request (from history · {entry.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}){dirty}"
                : $"Request ({(_detachedName is null ? "scratch" : "imported")}){dirty}";

        // This runs on every keystroke in the editor: redraw only what changed.
        if (_requestFrame.Title != title)
        {
            _requestFrame.Title = title;
        }

        var treeItem = _collection ?? (object)_workspace;
        var treeState = (treeItem, _collection?.IsDirty == true, _collection?.Name);
        if (treeState != _treeTitleState)
        {
            _treeTitleState = treeState;
            _tree.RefreshItem(treeItem);
        }
    }

    private void UpdateSidebarTitle()
    {
        var title = _showingHistory ? $"History ({_history.Count})" : "Collections";
        if (_sidebar.Title != title)
        {
            _sidebar.Title = title;
        }
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
            RequestStop();
        }
    }

    /// <summary>Returns false when the user cancels.</summary>
    private bool ConfirmUnsaved()
    {
        CommitEditor();
        if (!ConfirmDiscardRequest())
        {
            return false;
        }

        if (_runner.Recorder is { } recorder && !StopRecording(recorder, RecordingDialogPurpose.Close))
        {
            return false;
        }

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

        // Also a menu item's key, but Terminal.Gui delivers those only to an open menu.
        if (key == Key.L.WithCtrl)
        {
            _editor.FocusUrl();
            return true;
        }

        if (key == Key.F12)
        {
            ToggleRecording();
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
        if (disposing && !_closed)
        {
            _closed = true;
            _closing.Cancel();
            _closing.Dispose();
            ThemeCatalog.Changed -= OnThemeChanged;

            // Closed without saving (normally the user is asked first): the files stay for Tools > Recover.
            if (_runner.Recorder is { } recorder)
            {
                recorder.Changed -= OnRecordingChanged;
                _runner.Recorder = null;
                recorder.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
