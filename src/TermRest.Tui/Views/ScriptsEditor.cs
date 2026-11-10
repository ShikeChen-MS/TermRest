using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Scripting;

namespace TermRest.Tui.Views;

/// <summary>
/// Pre-request and post-response scripts in each language: the Scripts tab of a request, and the scripts of a collection
/// or folder (<see cref="Dialogs.ScriptsDialog"/>). <c>Language</c> chooses the scripts shown (a • marks the languages
/// that have some). A request's tab also says how it runs them: in which language (<c>Run in</c>: Auto is the first of
/// the user's order with all of them), and whether its pre-request and post-response scripts run, its collection's and
/// folders' too (the boxes above them).
/// </summary>
internal sealed class ScriptsEditor : View
{
    private readonly OptionSelector _language;
    private readonly Label _hint;
    private readonly OptionSelector? _runIn;
    private readonly Label? _status;
    private readonly CheckBox? _runPreRequest;
    private readonly CheckBox? _runPostResponse;
    private readonly TextArea _preRequest;
    private readonly TextArea _test;
    private readonly Dictionary<string, LanguageScripts> _scripts = new(StringComparer.Ordinal);
    private ScriptSettings _settings = ScriptSettings.Auto;
    private string _editing = ScriptLanguage.All[0];
    private bool _loading;

    /// <param name="forRequest">A request's Scripts tab: with how the request runs its scripts.</param>
    public ScriptsEditor(bool forRequest = false)
    {
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        _language = new OptionSelector { X = 9, Y = 0, Orientation = Orientation.Horizontal, Labels = LanguageLabels(), Value = 0 };
        _language.ValueChanged += (_, e) =>
        {
            if (!_loading && e.NewValue is int index)
            {
                Edit(ScriptLanguage.All[index]);
            }
        };
        _hint = new Label { X = 0, Y = 1, Width = Dim.Fill(), Height = 1, Text = "" };
        Add(new Label { Text = "Language", X = 0, Y = 0 }, _language, _hint);
        var top = 2;
        if (forRequest)
        {
            _runIn = new OptionSelector
            {
                X = 9,
                Y = 2,
                Orientation = Orientation.Horizontal,
                Labels = ["Auto", .. ScriptLanguage.All.Select(ScriptLanguage.Label)],
                Value = 0,
            };
            _runIn.ValueChanged += (_, e) =>
            {
                if (!_loading && e.NewValue is int index)
                {
                    _settings = _settings with { Language = index == 0 ? null : ScriptLanguage.All[index - 1] };
                    if (_settings.Language is { } chosen)
                    {
                        Edit(chosen);
                    }

                    OnChanged();
                }
            };
            _status = new Label { X = Pos.Right(_runIn) + 2, Y = 2, Width = Dim.Fill(), Height = 1, Text = "" };
            Add(new Label { Text = "Run in", X = 0, Y = 2, Width = 9 }, _runIn, _status);
            top = 3;
        }

        _preRequest = new TextArea { X = 0, Y = top + 1, Width = Dim.Percent(50), Height = Dim.Fill() };
        _test = new TextArea { X = Pos.Right(_preRequest) + 1, Y = top + 1, Width = Dim.Fill(), Height = Dim.Fill() };
        _preRequest.UserEdited += (_, _) => OnEdited();
        _test.UserEdited += (_, _) => OnEdited();
        if (forRequest)
        {
            _runPreRequest = new CheckBox { Text = "Pre-request (before sending)", X = 0, Y = top };
            _runPostResponse = new CheckBox { Text = "Post-response (tests)", X = Pos.Left(_test), Y = top };
            _runPreRequest.ValueChanged += (_, _) => OnToggled(ScriptPhase.PreRequest, _runPreRequest);
            _runPostResponse.ValueChanged += (_, _) => OnToggled(ScriptPhase.PostResponse, _runPostResponse);
            Add(_runPreRequest, _runPostResponse);
        }
        else
        {
            Add(new Label { Text = "Pre-request (before sending)", X = 0, Y = top }, new Label { Text = "Post-response (tests)", X = Pos.Left(_test), Y = top });
        }

        Add(_preRequest, _test);
        UpdateHint();
    }

    /// <summary>Raised when the user edits a script, or how the request runs them.</summary>
    public event EventHandler? Changed;

    /// <summary>The scripts, in every language.</summary>
    public ScriptSet Scripts => new(_scripts.Values);

    /// <summary>How the request runs its scripts (a request's tab).</summary>
    public ScriptSettings Settings => _settings;

    /// <summary>What would run with these scripts and settings (a request's tab: it knows the collection and folders).</summary>
    public Func<ScriptSet, ScriptSettings, ScriptPlan?>? Planner { get; set; }

    /// <summary>The language shown when there are no scripts yet: the first of the user's order.</summary>
    public string DefaultLanguage { get; set; } = ScriptLanguage.All[0];

    /// <summary>The language whose scripts are shown (for tests).</summary>
    internal string EditedLanguage => _editing;

    /// <summary>The hint under the language (for tests).</summary>
    internal string Hint => _hint.Text;

    /// <summary>What the request runs, next to Run in (for tests).</summary>
    internal string Status => _status?.Text ?? "";

    public void Load(ScriptSet scripts, ScriptSettings? settings = null)
    {
        _loading = true;
        try
        {
            _scripts.Clear();
            foreach (var language in scripts.Languages)
            {
                _scripts[language.Language] = language;
            }

            _settings = settings ?? ScriptSettings.Auto;
            if (_runIn is not null)
            {
                _runIn.Value = _settings.Language is { } chosen ? ScriptLanguage.Rank(chosen) + 1 : 0;
            }
        }
        finally
        {
            _loading = false;
        }

        // The scripts that run, else the first there are, else the user's first language.
        var first = scripts.Languages.Count > 0 ? scripts.Languages[0].Language : DefaultLanguage;
        Edit(Planner?.Invoke(scripts, _settings)?.Language ?? first);
        Refresh();
    }

    public void FocusPreRequest() => _preRequest.SetFocus();

    /// <summary>Shows the scripts of <paramref name="language"/>.</summary>
    internal void Edit(string language)
    {
        _loading = true;
        try
        {
            _editing = language;
            _language.Labels = LanguageLabels();
            _language.Value = ScriptLanguage.Rank(language);
            var scripts = _scripts.GetValueOrDefault(language);
            _preRequest.SetText(scripts?.PreRequest ?? "");
            _test.SetText(scripts?.Test ?? "");
        }
        finally
        {
            _loading = false;
        }

        UpdateHint();
    }

    /// <summary>Shows what the request runs, as things stand (its collection's scripts or the user's order may have changed).</summary>
    public void Refresh()
    {
        if (_status is null || _runPreRequest is null || _runPostResponse is null)
        {
            return;
        }

        var scripts = Scripts;
        var plan = Planner?.Invoke(scripts, _settings);
        _loading = true;
        try
        {
            _runPreRequest.Value = Runs(ScriptPhase.PreRequest) ? CheckState.Checked : CheckState.UnChecked;
            _runPostResponse.Value = Runs(ScriptPhase.PostResponse) ? CheckState.Checked : CheckState.UnChecked;
        }
        finally
        {
            _loading = false;
        }

        _status.Text = plan is null ? ""
            : plan.Problem is { } problem ? $"✗ {problem}"
            : plan.Language is { } language ? $"→ runs {ScriptLanguage.Label(language)}"
            : "→ runs no scripts";

        bool Runs(ScriptPhase phase) => plan?.Runs(phase) ?? _settings.Runs(phase) ?? scripts.Has(phase);
    }

    private void OnEdited()
    {
        var had = _scripts.TryGetValue(_editing, out var before) && !before.IsEmpty;
        _scripts[_editing] = new LanguageScripts(_editing, _preRequest.GetText(), _test.GetText());
        if (had == _scripts[_editing].IsEmpty)
        {
            _loading = true;
            try
            {
                _language.Labels = LanguageLabels();
                _language.Value = ScriptLanguage.Rank(_editing);
            }
            finally
            {
                _loading = false;
            }
        }

        OnChanged();
    }

    /// <summary>A phase's box: kept only when it differs from what the request does by default (run when there are scripts).</summary>
    private void OnToggled(ScriptPhase phase, CheckBox box)
    {
        if (_loading)
        {
            return;
        }

        var runs = box.Value == CheckState.Checked;
        var byDefault = Planner?.Invoke(Scripts, _settings)?.Has(phase) ?? Scripts.Has(phase);
        _settings = _settings.WithRuns(phase, runs == byDefault ? null : runs);
        OnChanged();
    }

    private void OnChanged()
    {
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string[] LanguageLabels() =>
        [.. ScriptLanguage.All.Select(l => _scripts.TryGetValue(l, out var s) && !s.IsEmpty ? ScriptLanguage.Label(l) + " •" : ScriptLanguage.Label(l))];

    private void UpdateHint() => _hint.Text = _editing switch
    {
        ScriptLanguage.Python => "Run by your Python (Tools › Scripts…) with the tr object: tr.request, tr.response, tr.environment, tr.test… (Help › Scripting)",
        ScriptLanguage.CSharp => "Built and run by your .NET 10 SDK (Tools › Scripts…) with the tr object: tr.Request, tr.Response, tr.Test… (Help › Scripting)",
        ScriptLanguage.PowerShell => "Run by your PowerShell 7 (Tools › Scripts…) with the $tr object: $tr.Request, $tr.Response, $tr.Test… (Help › Scripting)",
        _ => "Run by your Node.js or Bun (Tools › Scripts…) with Postman's pm object: pm.test, pm.expect, pm.response… (Help › Scripting)",
    };
}
