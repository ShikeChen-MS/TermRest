using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Json;
using TermRest.Core.Model;
using TermRest.Tui.Dialogs;

namespace TermRest.Tui.Views;

/// <summary>Body tab: none / raw (JSON, XML, text…) / form-data / x-www-form-urlencoded / binary file / GraphQL.</summary>
internal sealed class BodyEditor : View
{
    private static readonly BodyMode[] Modes = [BodyMode.None, BodyMode.Raw, BodyMode.FormData, BodyMode.UrlEncoded, BodyMode.File, BodyMode.GraphQL];
    private static readonly string[] Languages = ["json", "xml", "text", "html", "javascript"];

    private readonly OptionSelector _mode;
    private readonly OptionSelector _language;
    private readonly Button _beautify;
    private readonly TextArea _raw;
    private readonly KeyValueTable _formData;
    private readonly KeyValueTable _urlEncoded;
    private readonly View _filePanel;
    private readonly TextField _filePath;
    private readonly View _graphQLPanel;
    private readonly TextArea _graphQLQuery;
    private readonly TextArea _graphQLVariables;
    private readonly Label _noneLabel;
    private RequestBody _body = new();
    private bool _loading;

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
            Labels = ["none", "raw", "form-data", "x-www-form-urlencoded", "binary", "GraphQL"],
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

        _language = new OptionSelector { X = 0, Y = 1, Orientation = Orientation.Horizontal, Labels = ["JSON", "XML", "Text", "HTML", "JavaScript"], Value = 0 };
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

        _filePanel = new View { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _filePath = new TextField { X = 7, Y = 0, Width = Dim.Fill(13) };
        _filePath.TextChanged += (_, _) =>
        {
            if (!_loading)
            {
                _body.FileSrc = _filePath.Text;
                RaiseChanged();
            }
        };
        var browse = new Button { Text = "Browse…", X = Pos.AnchorEnd(12), Y = 0 };
        browse.Accepted += (_, _) =>
        {
            if (App is not null && Ui.PickFile(App, "Choose the request body file") is { } path)
            {
                _filePath.Text = path;
            }
        };
        _filePanel.Add(
            new Label { Text = "File:", X = 0, Y = 0 },
            _filePath,
            browse,
            new Label { Text = "The file is sent as the raw request body (Content-Type defaults to application/octet-stream).", X = 0, Y = 2 });

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

        Add(_mode, _language, _beautify, _raw, _formData, _urlEncoded, _filePanel, _graphQLPanel, _noneLabel);
        ShowMode();
    }

    public event EventHandler? Changed;

    public void Load(RequestBody body)
    {
        _loading = true;
        try
        {
            _body = body;
            _mode.Value = Array.IndexOf(Modes, body.Mode) is var index && index >= 0 ? index : 0;
            _language.Value = Array.IndexOf(Languages, body.RawLanguage.ToLowerInvariant()) is var language && language >= 0 ? language : 2;
            _raw.SetText(body.Raw);
            _formData.SetEntries(body.FormData);
            _urlEncoded.SetEntries(body.UrlEncoded);
            _filePath.Text = body.FileSrc ?? "";
            _graphQLQuery.SetText(body.GraphQLQuery);
            _graphQLVariables.SetText(body.GraphQLVariables);
            ShowMode();
        }
        finally
        {
            _loading = false;
        }
    }

    private void ShowMode()
    {
        var mode = _body.Mode;
        _language.Visible = mode == BodyMode.Raw;
        _beautify.Visible = mode == BodyMode.Raw;
        _raw.Visible = mode == BodyMode.Raw;
        _formData.Visible = mode == BodyMode.FormData;
        _urlEncoded.Visible = mode == BodyMode.UrlEncoded;
        _filePanel.Visible = mode == BodyMode.File;
        _graphQLPanel.Visible = mode == BodyMode.GraphQL;
        _noneLabel.Visible = mode == BodyMode.None;
        SetNeedsLayout();
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
