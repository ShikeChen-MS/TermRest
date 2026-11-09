using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Scripting;

namespace TermRest.Tui.Views;

/// <summary>
/// Pre-request and post-response scripts with their language: the Scripts tab of a request, and the scripts of a
/// collection or folder (<see cref="Dialogs.ScriptsDialog"/>).
/// </summary>
internal sealed class ScriptsEditor : View
{
    private readonly OptionSelector _language;
    private readonly Label _hint;
    private readonly TextArea _preRequest;
    private readonly TextArea _test;
    private bool _loading;

    public ScriptsEditor()
    {
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        var languageLabel = new Label { Text = "Language", X = 0, Y = 0 };
        _language = new OptionSelector
        {
            X = Pos.Right(languageLabel) + 1,
            Y = 0,
            Orientation = Orientation.Horizontal,
            Labels = [.. ScriptLanguage.All.Select(ScriptLanguage.Label)],
            Value = 0,
        };
        _language.ValueChanged += (_, e) =>
        {
            if (!_loading && e.NewValue is int)
            {
                UpdateHint();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        };
        _hint = new Label { X = 0, Y = 1, Width = Dim.Fill(), Text = "" };
        _preRequest = new TextArea { X = 0, Y = 3, Width = Dim.Percent(50), Height = Dim.Fill() };
        _test = new TextArea { X = Pos.Right(_preRequest) + 1, Y = 3, Width = Dim.Fill(), Height = Dim.Fill() };
        _preRequest.UserEdited += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _test.UserEdited += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        Add(
            languageLabel,
            _language,
            _hint,
            new Label { Text = "Pre-request (before sending)", X = 0, Y = 2 },
            new Label { Text = "Post-response (tests)", X = Pos.Left(_test), Y = 2 },
            _preRequest,
            _test);
        UpdateHint();
    }

    /// <summary>Raised when the user edits a script or changes the language.</summary>
    public event EventHandler? Changed;

    public ScriptSet Scripts => new(_preRequest.GetText(), _test.GetText(), Language);

    /// <summary>The hint under the language (for tests).</summary>
    internal string Hint => _hint.Text;

    private string Language => ScriptLanguage.All[Math.Clamp(_language.Value ?? 0, 0, ScriptLanguage.All.Count - 1)];

    public void Load(ScriptSet scripts)
    {
        _loading = true;
        try
        {
            _language.Value = Math.Max(0, ScriptLanguage.All.ToList().IndexOf(scripts.Language));
            _preRequest.SetText(scripts.PreRequest);
            _test.SetText(scripts.Test);
            UpdateHint();
        }
        finally
        {
            _loading = false;
        }
    }

    public void FocusPreRequest() => _preRequest.SetFocus();

    private void UpdateHint() => _hint.Text = Language switch
    {
        ScriptLanguage.Python => "Run by your Python (Tools › Scripts…) with the tr object: tr.request, tr.response, tr.environment, tr.test… (Help › Scripting)",
        ScriptLanguage.CSharp => "Built and run by your .NET 10 SDK (Tools › Scripts…) with the tr object: tr.Request, tr.Response, tr.Test… (Help › Scripting)",
        _ => "Run by your Node.js or Bun (Tools › Scripts…) with Postman's pm object: pm.test, pm.expect, pm.response… (Help › Scripting)",
    };
}
