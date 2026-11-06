using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Http;
using TermRest.Core.Json;
using TermRest.Core.Model;
using TermRest.Tui.Dialogs;
using TermRest.Tui.Theming;

namespace TermRest.Tui.Views;

/// <summary>Body tab: none / raw (JSON, XML, text…) / form-data / x-www-form-urlencoded / file / GraphQL.</summary>
internal sealed class BodyEditor : View
{
    private static readonly BodyMode[] Modes = [BodyMode.None, BodyMode.Raw, BodyMode.FormData, BodyMode.UrlEncoded, BodyMode.File, BodyMode.GraphQL];
    private static readonly string[] Languages = ["json", "xml", "text", "html", "javascript"];
    private static readonly string[] LanguageLabels = ["JSON", "XML", "Text", "HTML", "JavaScript"];
    private static readonly string?[] FileLanguages = [null, .. Languages];

    private readonly OptionSelector _mode;
    private readonly OptionSelector _language;
    private readonly Button _beautify;
    private readonly TextArea _raw;
    private readonly KeyValueTable _formData;
    private readonly KeyValueTable _urlEncoded;
    private readonly OptionSelector _fileType;
    private readonly View _filePanel;
    private readonly TextField _filePath;
    private readonly Label _fileStatus;
    private readonly Label _recordingNotice;
    private readonly View _graphQLPanel;
    private readonly TextArea _graphQLQuery;
    private readonly TextArea _graphQLVariables;
    private readonly Label _noneLabel;
    private RequestBody _body = new();
    private bool _loading;
    private int _fileCheck; // a check still running when this changes belongs to another file or request

    public BodyEditor()
    {
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _mode = new OptionSelector
        {
            X = 0,
            Y = 0,
            Orientation = Orientation.Horizontal,
            Labels = ["none", "raw", "form-data", "x-www-form-urlencoded", "file", "GraphQL"],
            Value = 0,
        };
        _mode.ValueChanged += (_, e) =>
        {
            if (!_loading && e.NewValue is int index)
            {
                _body.Mode = Modes[index];
                if (_body.Mode == BodyMode.Raw && _body.RawLanguage == "text" && string.IsNullOrEmpty(_body.Raw))
                {
                    _body.RawLanguage = "json";
                    _loading = true;
                    _language!.Value = 0;
                    _loading = false;
                }

                ShowMode();
                RaiseChanged();
            }
        };

        _language = new OptionSelector { X = 0, Y = 1, Orientation = Orientation.Horizontal, Labels = LanguageLabels, Value = 0 };
        _language.ValueChanged += (_, e) =>
        {
            if (!_loading && e.NewValue is int index)
            {
                _body.RawLanguage = Languages[index];
                RaiseChanged();
            }
        };
        _beautify = new Button { Text = "Beautify", X = Pos.AnchorEnd(12), Y = 1, NoDecorations = true, ShadowStyle = null };
        _beautify.Accepted += (_, _) => Beautify();

        _raw = new TextArea { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill() };
        _raw.UserEdited += (_, _) =>
        {
            _body.Raw = _raw.GetText();
            RaiseChanged();
        };

        _formData = new KeyValueTable(KeyValueKind.FormData) { X = 0, Y = 1 };
        _formData.Changed += (_, _) => RaiseChanged();
        _urlEncoded = new KeyValueTable(KeyValueKind.UrlEncoded) { X = 0, Y = 1 };
        _urlEncoded.Changed += (_, _) => RaiseChanged();

        // A file body: only its path is kept. The file is read when the request is sent, and checked first when typed.
        _fileType = new OptionSelector { X = 0, Y = 1, Orientation = Orientation.Horizontal, Labels = ["Binary", .. LanguageLabels], Value = 0 };
        _fileType.ValueChanged += (_, e) =>
        {
            if (!_loading && e.NewValue is int index)
            {
                _body.FileLanguage = FileLanguages[index];
                RaiseChanged();
                CheckChosenFile();
            }
        };
        _filePanel = new View { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _filePath = new TextField { X = 7, Y = 0, Width = Dim.Fill(13) };
        _filePath.TextChanged += (_, _) =>
        {
            if (!_loading)
            {
                _fileCheck++;
                _body.FileSrc = _filePath.Text;
                RaiseChanged();
                DescribeFile();
            }
        };
        var browse = new Button { Text = "Browse…", X = Pos.AnchorEnd(12), Y = 0 };
        browse.Accepted += (_, _) =>
        {
            var current = string.IsNullOrWhiteSpace(_body.FileSrc) || _body.FileSrc.Contains("{{", StringComparison.Ordinal) ? null : ResolveFile(_body.FileSrc.Trim());
            if (App is not null && Ui.PickFile(App, "Choose the file to send as the body", Path.GetDirectoryName(current) ?? BaseDirectory?.Invoke()) is { } path)
            {
                ChooseFile(path);
            }
        };
        _fileStatus = new Label { X = 0, Y = 2, Width = Dim.Fill(), Text = "" };
        _recordingNotice = new Label
        {
            X = 0,
            Y = 5,
            Width = Dim.Fill(),
            Text = "● Recording is on: it keeps this file's path, not its content.",
            SchemeName = ThemeCatalog.WarningScheme,
            Visible = false,
        };
        _filePanel.Add(
            new Label { Text = "File:", X = 0, Y = 0 },
            _filePath,
            browse,
            _fileStatus,
            new Label { Text = "Read from disk each time the request is sent, never loaded into memory. A typed file is checked first.", X = 0, Y = 3, Width = Dim.Fill() },
            _recordingNotice);

        _graphQLPanel = new View { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _graphQLQuery = new TextArea { X = 0, Y = 1, Width = Dim.Percent(60), Height = Dim.Fill() };
        _graphQLQuery.UserEdited += (_, _) =>
        {
            _body.GraphQLQuery = _graphQLQuery.GetText();
            RaiseChanged();
        };
        _graphQLVariables = new TextArea { X = Pos.Right(_graphQLQuery) + 1, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };
        _graphQLVariables.UserEdited += (_, _) =>
        {
            _body.GraphQLVariables = _graphQLVariables.GetText();
            RaiseChanged();
        };
        _graphQLPanel.Add(
            new Label { Text = "Query", X = 0, Y = 0 },
            new Label { Text = "Variables (JSON)", X = Pos.Left(_graphQLVariables), Y = 0 },
            _graphQLQuery,
            _graphQLVariables);

        _noneLabel = new Label { Text = "This request does not have a body.", X = 0, Y = 2 };

        Add(_mode, _language, _fileType, _beautify, _raw, _formData, _urlEncoded, _filePanel, _graphQLPanel, _noneLabel);
        ShowMode();
    }

    public event EventHandler? Changed;

    /// <summary>Folder that relative file paths start from: the collection's folder, as when the request is sent.</summary>
    public Func<string>? BaseDirectory { get; set; }

    /// <summary>Whether requests are being recorded: the file panel then says that recordings keep the path, not the content.</summary>
    public bool Recording
    {
        get => _recordingNotice.Visible;
        set => _recordingNotice.Visible = value;
    }

    /// <summary>What the file panel says about the chosen file (for tests).</summary>
    internal string FileStatus => _fileStatus.Text;

    public void Load(RequestBody body)
    {
        _loading = true;
        try
        {
            _body = body;
            _fileCheck++;
            _mode.Value = Array.IndexOf(Modes, body.Mode) is var index && index >= 0 ? index : 0;
            _language.Value = Array.IndexOf(Languages, body.RawLanguage.ToLowerInvariant()) is var language && language >= 0 ? language : 2;
            _raw.SetText(body.Raw);
            _formData.SetEntries(body.FormData);
            _urlEncoded.SetEntries(body.UrlEncoded);
            _fileType.Value = Math.Max(0, Array.IndexOf(FileLanguages, BodyFile.Normalize(body.FileLanguage)));
            _filePath.Text = body.FileSrc ?? "";
            _graphQLQuery.SetText(body.GraphQLQuery);
            _graphQLVariables.SetText(body.GraphQLVariables);
            DescribeFile();
            ShowMode();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Uses <paramref name="path"/> as the body file. With a type, the file must fit it first: a JSON body takes only
    /// a valid JSON file. Anything else is turned down and the current file stays.
    /// </summary>
    internal void ChooseFile(string path)
    {
        if (_body.FileLanguage is not { } language)
        {
            _filePath.Text = path;
            return;
        }

        CheckFile(path, language, problem =>
        {
            if (problem is null)
            {
                _filePath.Text = path;
                ShowChecked(path, language);
                return;
            }

            DescribeFile();
            if (App is not null)
            {
                Ui.Error(App, "File not used", $"{problem}\n\nChoose another file, or set the type to Binary to send this one unchecked.");
            }
        });
    }

    private void ShowMode()
    {
        var mode = _body.Mode;
        _language.Visible = mode == BodyMode.Raw;
        _beautify.Visible = mode == BodyMode.Raw;
        _raw.Visible = mode == BodyMode.Raw;
        _formData.Visible = mode == BodyMode.FormData;
        _urlEncoded.Visible = mode == BodyMode.UrlEncoded;
        _fileType.Visible = mode == BodyMode.File;
        _filePanel.Visible = mode == BodyMode.File;
        _graphQLPanel.Visible = mode == BodyMode.GraphQL;
        _noneLabel.Visible = mode == BodyMode.None;
        SetNeedsLayout();
    }

    private string ResolveFile(string path)
    {
        try
        {
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(BaseDirectory?.Invoke() ?? Environment.CurrentDirectory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>What is known without reading the file: whether it is there, and its size.</summary>
    private void DescribeFile()
    {
        var src = _body.FileSrc?.Trim() ?? "";
        if (src.Length == 0)
        {
            ShowFileStatus("Choose the file to send, or type its path (a relative path starts at the collection's folder).");
            return;
        }

        if (src.Contains("{{", StringComparison.Ordinal))
        {
            ShowFileStatus("The path has variables: the file is found, and checked, when the request is sent.");
            return;
        }

        var file = new FileInfo(ResolveFile(src));
        if (!file.Exists)
        {
            ShowFileStatus($"✗ {file.FullName} does not exist.", "Error");
            return;
        }

        var type = TypeLabel(_body.FileLanguage);
        ShowFileStatus($"{file.Name} · {HttpExchange.FormatSize(file.Length)} · {(type is null ? "sent as it is" : $"checked as {type} when sent")}");
    }

    /// <summary>After the type changed: checks the chosen file right away, so that a mismatch shows before sending.</summary>
    private void CheckChosenFile()
    {
        var src = _body.FileSrc?.Trim() ?? "";
        var path = src.Length == 0 || src.Contains("{{", StringComparison.Ordinal) ? null : ResolveFile(src);
        if (_body.FileLanguage is not { } language || path is null || !File.Exists(path))
        {
            _fileCheck++;
            DescribeFile();
            return;
        }

        CheckFile(path, language, problem =>
        {
            if (problem is null)
            {
                ShowChecked(path, language);
            }
            else
            {
                ShowFileStatus($"✗ {problem} It is not sent until it is fixed, or the type is Binary.", "Error");
            }
        });
    }

    /// <summary>Checks a file in the background (it can be large); <paramref name="done"/> gets the problem, or null.</summary>
    private void CheckFile(string path, string language, Action<string?> done)
    {
        var check = ++_fileCheck;
        var app = App;
        ShowFileStatus($"Checking {Path.GetFileName(path)}…");
        _ = Task.Run(() => BodyFile.Check(path, language)).ContinueWith(
            task =>
            {
                var problem = task.IsCompletedSuccessfully ? task.Result : task.Exception?.GetBaseException().Message ?? "The check was cancelled.";
                app?.AddTimeout(TimeSpan.Zero, () =>
                {
                    if (check == _fileCheck)
                    {
                        done(problem);
                    }

                    return false;
                });
            },
            TaskScheduler.Default);
    }

    private void ShowChecked(string path, string language)
    {
        var file = new FileInfo(path);
        ShowFileStatus($"✓ {file.Name} · {HttpExchange.FormatSize(file.Exists ? file.Length : 0)} · checked as {TypeLabel(language)}", ThemeCatalog.SuccessScheme);
    }

    private void ShowFileStatus(string text, string? scheme = null)
    {
        _fileStatus.Text = text;
        _fileStatus.SchemeName = scheme;
    }

    private static string? TypeLabel(string? language) =>
        Array.IndexOf(Languages, language ?? "") is var index and >= 0 ? LanguageLabels[index] : null;

    protected override void Dispose(bool disposing)
    {
        _fileCheck++; // a check finishing later must not touch the disposed views
        base.Dispose(disposing);
    }

    private void Beautify()
    {
        if (_body.RawLanguage == "json" && PostmanJson.TryPrettyPrint(_raw.GetText()) is { } pretty)
        {
            _raw.SetText(pretty);
            _body.Raw = pretty;
            RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        if (!_loading)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
