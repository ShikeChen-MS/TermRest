using System.Collections.ObjectModel;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Import;
using TermRest.Core.Model;
using TermRest.Core.Postman;

namespace TermRest.Tui.Views;

/// <summary>
/// Request editor: method + URL + Send on top, then tabs for Params, Headers, Auth, Body, Scripts and Settings.
/// Edits are applied to <see cref="Spec"/> immediately; <see cref="Modified"/> tells the window the request is dirty.
/// </summary>
internal sealed class RequestEditor : View
{
    private readonly DropDownList _method;
    private readonly TextField _url;
    private readonly Button _send;
    private readonly TabPanel _tabs;
    private readonly View _paramsTab;
    private readonly KeyValueTable _query;
    private readonly Label _pathLabel;
    private readonly KeyValueTable _pathVariables;
    private readonly KeyValueTable _headers;
    private readonly AuthEditor _auth;
    private readonly BodyEditor _body;
    private readonly ScriptsEditor _scripts;
    private readonly OptionSelector _strictSsl;
    private readonly OptionSelector _followRedirects;
    private readonly Label _certificateInfo;
    private readonly TextArea _description;
    private bool _loading;
    private bool _syncing;

    public RequestEditor()
    {
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _method = new DropDownList { X = 0, Y = 0, Width = 10, Text = "GET" };
        _method.Source = new ListWrapper<string>(new ObservableCollection<string>(HttpRequestSpec.StandardMethods));
        _method.TextChanged += (_, _) =>
        {
            if (!_loading)
            {
                Spec.Method = _method.Text.Trim().ToUpperInvariant();
                MarkModified();
            }
        };

        _send = new Button { Text = "Send", X = Pos.AnchorEnd(10), Y = 0, IsDefault = false };
        _send.Accepted += (_, _) => SendRequested?.Invoke(this, EventArgs.Empty);

        _url = new UrlField { X = Pos.Right(_method) + 1, Y = 0, Width = Dim.Fill(11) };
        _url.TextChanged += (_, _) => OnUrlEdited();
        _url.Pasting += (_, e) =>
        {
            // Pasting a curl command into the URL bar imports it (like Postman).
            if (CurlImporter.LooksLikeCurl(e.Text))
            {
                e.Handled = true;
                CurlPasted?.Invoke(this, e.Text);
            }
        };
        _url.KeyDown += (_, key) =>
        {
            if (key == Key.Enter)
            {
                SendRequested?.Invoke(this, EventArgs.Empty);
                key.Handled = true;
            }
        };

        _tabs = new TabPanel { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };

        _paramsTab = new View { Title = "Params", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _query = new KeyValueTable(KeyValueKind.QueryParams) { Height = Dim.Percent(65) };
        _query.Changed += (_, _) => OnQueryEdited();
        _pathLabel = new Label { Text = "Path variables", X = 0, Y = Pos.Bottom(_query) };
        _pathVariables = new KeyValueTable(KeyValueKind.PathVariables) { Y = Pos.Bottom(_pathLabel), Height = Dim.Fill() };
        _pathVariables.Changed += (_, _) => MarkModified();
        _paramsTab.Add(_query, _pathLabel, _pathVariables);

        var headersTab = new View { Title = "Headers", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _headers = new KeyValueTable(KeyValueKind.Headers);
        _headers.Changed += (_, _) => MarkModified();
        headersTab.Add(_headers);

        var authTab = new View { Title = "Auth", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _auth = new AuthEditor();
        _auth.Changed += (_, _) =>
        {
            if (!_loading)
            {
                Spec.Auth = _auth.Auth;
                MarkModified();
            }
        };
        authTab.Add(_auth);

        var bodyTab = new View { Title = "Body", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _body = new BodyEditor();
        _body.Changed += (_, _) => MarkModified();
        bodyTab.Add(_body);

        var scriptsTab = new View { Title = "Scripts", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _scripts = new ScriptsEditor();
        _scripts.Changed += (_, _) =>
        {
            var scripts = _scripts.Scripts;
            Spec.PreRequestScript = scripts.PreRequest;
            Spec.TestScript = scripts.Test;
            Spec.ScriptLanguage = scripts.Language;
            MarkModified();
        };
        scriptsTab.Add(_scripts);

        var settingsTab = new View { Title = "Settings", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _strictSsl = new OptionSelector { X = 30, Y = 0, Orientation = Orientation.Horizontal, Labels = ["Workspace default", "On", "Off"], Value = 0 };
        _strictSsl.ValueChanged += (_, e) =>
        {
            if (!_loading && e.NewValue is int i)
            {
                Spec.Settings.StrictSsl = i switch { 1 => true, 2 => false, _ => null };
                MarkModified();
            }
        };
        _followRedirects = new OptionSelector { X = 30, Y = 1, Orientation = Orientation.Horizontal, Labels = ["Workspace default", "On", "Off"], Value = 0 };
        _followRedirects.ValueChanged += (_, e) =>
        {
            if (!_loading && e.NewValue is int i)
            {
                Spec.Settings.FollowRedirects = i switch { 1 => true, 2 => false, _ => null };
                MarkModified();
            }
        };
        _certificateInfo = new Label { X = 30, Y = 3, Width = Dim.Fill(), Height = 2, Text = "" };
        var manageCertificates = new Button { Text = "Manage certificates…", X = 30, Y = 5 };
        manageCertificates.Accepted += (_, _) => ManageCertificatesRequested?.Invoke(this, EventArgs.Empty);
        _description = new TextArea { X = 0, Y = 8, Width = Dim.Fill(), Height = Dim.Fill() };
        _description.UserEdited += (_, _) =>
        {
            var text = _description.GetText();
            Spec.Description = text.Length == 0 ? null : text;
            MarkModified();
        };
        settingsTab.Add(
            new Label { Text = "SSL certificate verification", X = 0, Y = 0 },
            _strictSsl,
            new Label { Text = "Follow redirects", X = 0, Y = 1 },
            _followRedirects,
            new Label { Text = "Client certificate", X = 0, Y = 3 },
            _certificateInfo,
            manageCertificates,
            new Label { Text = "Description (Markdown)", X = 0, Y = 7 },
            _description);

        _tabs.AddTabs(_paramsTab, headersTab, authTab, bodyTab, scriptsTab, settingsTab);
        _tabs.ValueChanged += (_, e) =>
        {
            if (e.NewValue == settingsTab)
            {
                RefreshCertificateInfo();
            }
        };

        Add(_method, _url, _send, _tabs);
        Load(new HttpRequestSpec());
    }

    public HttpRequestSpec Spec { get; private set; } = new();

    public bool IsModified { get; private set; }

    public event EventHandler? Modified;

    public event EventHandler? SendRequested;

    public event EventHandler? ManageCertificatesRequested;

    /// <summary>A curl command was pasted into the URL bar.</summary>
    public event EventHandler<string>? CurlPasted;

    /// <summary>Describes which client certificate the current URL would use (supplied by the main window).</summary>
    public Func<HttpRequestSpec, string>? DescribeCertificate { get; set; }

    public Func<string>? DescribeInheritedAuth
    {
        get => _auth.DescribeInherited;
        set => _auth.DescribeInherited = value;
    }

    /// <summary>Folder that the request's relative file paths start from (its collection's folder).</summary>
    public Func<string>? BaseDirectory
    {
        get => _body.BaseDirectory;
        set => _body.BaseDirectory = value;
    }

    /// <summary>What the Body tab says about the body file (for tests).</summary>
    internal string BodyFileStatus => _body.FileStatus;

    /// <summary>The Scripts tab (for tests).</summary>
    internal ScriptsEditor ScriptsTab => _scripts;

    /// <summary>Whether requests are being recorded: the Body tab then says how body files are recorded.</summary>
    public void SetRecording(bool recording) => _body.Recording = recording;

    /// <summary>Uses a body file as if the user picked it in the Body tab (for tests).</summary>
    internal void ChooseBodyFile(string path) => _body.ChooseFile(path);

    public void Load(HttpRequestSpec spec)
    {
        _loading = true;
        try
        {
            Spec = spec;
            _method.Text = spec.Method;
            _url.Text = spec.Url;
            _query.SetEntries(spec.QueryParams);
            _pathVariables.SetEntries(spec.PathVariables);
            UpdatePathVariablesVisibility();
            _headers.SetEntries(spec.Headers);
            _auth.Load(spec.Auth);
            _body.Load(spec.Body);
            _scripts.Load(spec.Scripts);
            _strictSsl.Value = spec.Settings.StrictSsl switch { true => 1, false => 2, null => 0 };
            _followRedirects.Value = spec.Settings.FollowRedirects switch { true => 1, false => 2, null => 0 };
            _description.SetText(spec.Description ?? "");
            RefreshCertificateInfo();
            IsModified = false;
        }
        finally
        {
            _loading = false;
        }
    }

    public void MarkClean() => IsModified = false;

    /// <summary>Loads a request that replaces the current one (e.g. from a pasted curl command): marked as modified.</summary>
    public void Replace(HttpRequestSpec spec)
    {
        Load(spec);
        MarkModified();
    }

    public void SetSending(bool sending)
    {
        _send.Text = sending ? "Cancel" : "Send";
        SetNeedsDraw();
    }

    public void FocusUrl() => _url.SetFocus();

    public void RefreshCertificateInfo() => _certificateInfo.Text = DescribeCertificate?.Invoke(Spec) ?? "";

    private void OnUrlEdited()
    {
        if (_loading || _syncing)
        {
            return;
        }

        // Some terminals deliver a paste as typed text: a whole curl command appearing at once is imported too.
        var text = _url.Text;
        if (text.Length - Spec.Url.Length > 8 && CurlImporter.LooksLikeCurl(text) && !CurlImporter.LooksLikeCurl(Spec.Url))
        {
            if (App is { } app)
            {
                app.Invoke(() => CurlPasted?.Invoke(this, text));
            }
            else
            {
                CurlPasted?.Invoke(this, text);
            }

            return;
        }

        _syncing = true;
        try
        {
            Spec.Url = _url.Text;
            Spec.QueryParams = PostmanUrl.SyncQueryFromRaw(Spec.Url, Spec.QueryParams);
            Spec.PathVariables = PostmanUrl.SyncPathVariables(Spec.Url, Spec.PathVariables);
            _query.SetEntries(Spec.QueryParams);
            _pathVariables.SetEntries(Spec.PathVariables);
            UpdatePathVariablesVisibility();
        }
        finally
        {
            _syncing = false;
        }

        MarkModified();
    }

    private void OnQueryEdited()
    {
        if (_loading || _syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            Spec.Url = PostmanUrl.SyncRawFromQuery(Spec.Url, Spec.QueryParams);
            _url.Text = Spec.Url;
        }
        finally
        {
            _syncing = false;
        }

        MarkModified();
    }

    private void UpdatePathVariablesVisibility()
    {
        var hasPathVariables = Spec.PathVariables.Count > 0;
        _pathLabel.Visible = hasPathVariables;
        _pathVariables.Visible = hasPathVariables;
        _query.Height = hasPathVariables ? Dim.Percent(65) : Dim.Fill();
        _paramsTab.SetNeedsLayout();
    }

    private void MarkModified()
    {
        if (_loading)
        {
            return;
        }

        IsModified = true;
        Modified?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The URL bar. <see cref="TextField"/> keeps only the first line of a paste; a pasted curl command is kept whole so
    /// the <see cref="View.Pasting"/> handler can import it (anything else is still reduced to one line before insertion).
    /// </summary>
    private sealed class UrlField : TextField
    {
        protected override string OnSanitizingPaste(string raw) =>
            CurlImporter.LooksLikeCurl(raw)
                ? string.Concat(raw.Where(c => !char.IsControl(c) || c is '\t' or '\r' or '\n'))
                : base.OnSanitizingPaste(raw);

        protected override bool OnPaste(string text) =>
            base.OnPaste(text.AsSpan().ContainsAny('\r', '\n') ? base.OnSanitizingPaste(text) : text);
    }
}
