using System.Globalization;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Http;
using TermRest.Core.Json;
using TermRest.Core.Model;
using TermRest.Tui.Dialogs;

namespace TermRest.Tui.Views;

/// <summary>
/// Editor of a saved example's response, in the response pane's place while an example is open: its status code and
/// reason, the language of its body, and Body and Headers tabs. <see cref="ApplyTo"/> writes what was edited into an
/// example; what was not edited stays exactly as the example has it.
/// </summary>
internal sealed class ExampleResponseEditor : View
{
    private static readonly string[] LanguageLabels = ["JSON", "XML", "HTML", "JavaScript", "Text"];

    private readonly TextField _code;
    private readonly TextField _status;
    private readonly OptionSelector _language;
    private readonly Button _beautify;
    private readonly Button _wrap;
    private readonly Label _bodyNote;
    private readonly TextArea _body;
    private readonly KeyValueTable _headers;
    private List<KeyValueEntry> _headerEntries = [];

    /// <summary>The body's language as the example says, also one that is not among the choices (yaml, say).</summary>
    private string? _languageValue;

    private Edits _edited;
    private bool _bodyEditable = true;
    private bool _loading;

    public ExampleResponseEditor()
    {
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();

        var statusLabel = new Label { Text = "Status", X = 0, Y = 0 };
        _code = new TextField { X = Pos.Right(statusLabel) + 1, Y = 0, Width = 5 };
        _code.TextChanged += (_, _) => Edited(Edits.Code);
        _status = new TextField { X = Pos.Right(_code) + 1, Y = 0, Width = 22 };
        _status.TextChanged += (_, _) => Edited(Edits.Status);
        _language = new OptionSelector { X = Pos.Right(_status) + 2, Y = 0, Orientation = Orientation.Horizontal, Labels = LanguageLabels, Value = LanguageLabels.Length - 1 };
        _language.ValueChanged += (_, e) =>
        {
            if (!_loading && e.NewValue is int index)
            {
                _languageValue = ExampleSpec.Languages[index];
                ColorBody();
                Edited(Edits.Language);
            }
        };

        var tabs = new TabPanel { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };
        var bodyTab = new View { Title = "Body", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _beautify = new Button { Text = "Beautify", X = 0, Y = 0, NoDecorations = true, ShadowStyle = null };
        _beautify.Accepted += (_, _) => Beautify();
        _wrap = new Button { Text = "Wrap: off", X = Pos.Right(_beautify) + 2, Y = 0, NoDecorations = true, ShadowStyle = null };
        _wrap.Accepted += (_, _) =>
        {
            _body!.WordWrap = !_body.WordWrap;
            _wrap.Text = _body.WordWrap ? "Wrap: on" : "Wrap: off";
        };
        _bodyNote = new Label { HotKeySpecifier = Ui.NoHotKey, X = Pos.Right(_wrap) + 2, Y = 0, Width = Dim.Fill(), Height = 1, Text = "" };
        _body = new TextArea { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };
        _body.UserEdited += (_, _) => Edited(Edits.Body);
        bodyTab.Add(_beautify, _wrap, _bodyNote, _body);

        var headersTab = new View { Title = "Headers", Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = true };
        _headers = new KeyValueTable(KeyValueKind.Headers);
        _headers.Changed += (_, _) => Edited(Edits.Headers);
        headersTab.Add(_headers);

        tabs.AddTabs(bodyTab, headersTab);
        Add(statusLabel, _code, _status, _language, tabs);
    }

    [Flags]
    private enum Edits
    {
        None = 0,
        Code = 1,
        Status = 2,
        Language = 4,
        Headers = 8,
        Body = 16,
    }

    /// <summary>Whether something was edited since <see cref="Load"/> or <see cref="MarkClean"/>.</summary>
    public bool IsModified => _edited != Edits.None;

    /// <summary>Raised after the user edits something.</summary>
    public event EventHandler? Modified;

    /// <summary>The body as shown (for tests).</summary>
    internal TextArea BodyArea => _body;

    /// <summary>The status code field (for tests).</summary>
    internal TextField CodeField => _code;

    public void Load(ExampleSpec spec)
    {
        _loading = true;
        try
        {
            _code.Text = spec.Code?.ToString(CultureInfo.InvariantCulture) ?? "";
            _status.Text = spec.Status;
            _languageValue = spec.Language;
            var index = Array.IndexOf(ExampleSpec.Languages, spec.Language ?? "");
            _language.Value = index >= 0 ? index : LanguageLabels.Length - 1;
            _headerEntries = KeyValueEntry.CloneList(spec.Headers);
            _headers.SetEntries(_headerEntries);

            // The editor takes about 90 bytes of memory for each character: a body longer than the response pane shows is
            // shown read-only, cut, and kept as it is.
            _bodyEditable = spec.Body.Length <= ResponseView.MaxShownLength;
            _body.ReadOnly = !_bodyEditable;
            _body.SetText(_bodyEditable ? spec.Body : spec.Body[..ResponseView.MaxShownLength]);
            _bodyNote.Text = _bodyEditable ? "" : $"⚠ {HttpExchange.FormatSize(spec.Body.Length)}: too long to edit here; shown cut, kept as it is";
            ColorBody();
        }
        finally
        {
            _loading = false;
        }

        _edited = Edits.None;
    }

    /// <summary>Writes what was edited into <paramref name="spec"/>'s response (a status code that is not a number is not taken).</summary>
    public void ApplyTo(ExampleSpec spec)
    {
        if (_edited.HasFlag(Edits.Code))
        {
            var text = _code.Text.Trim();
            if (text.Length == 0)
            {
                spec.Code = null;
            }
            else if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            {
                spec.Code = code;
            }
        }

        if (_edited.HasFlag(Edits.Status))
        {
            spec.Status = _status.Text;
        }

        if (_edited.HasFlag(Edits.Language))
        {
            spec.Language = _languageValue;
        }

        if (_edited.HasFlag(Edits.Headers))
        {
            spec.Headers = KeyValueEntry.CloneList(_headerEntries);
        }

        if (_edited.HasFlag(Edits.Body) && _bodyEditable)
        {
            spec.Body = _body.GetText();
        }
    }

    public void MarkClean() => _edited = Edits.None;

    private void Edited(Edits what)
    {
        if (_loading)
        {
            return;
        }

        _edited |= what;
        Modified?.Invoke(this, EventArgs.Empty);
    }

    private void ColorBody()
    {
        _body.SyntaxLanguage = _languageValue is "json" or "xml" or "html" or "javascript" ? _languageValue : null;
        _beautify.Visible = _languageValue == "json" && _bodyEditable;
    }

    private void Beautify()
    {
        if (_bodyEditable && PostmanJson.TryPrettyPrint(_body.GetText()) is { } pretty)
        {
            _body.SetText(pretty);
            Edited(Edits.Body);
        }
    }
}
