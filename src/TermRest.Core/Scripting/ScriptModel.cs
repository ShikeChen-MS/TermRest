using System.Text;

namespace TermRest.Core.Scripting;

/// <summary>
/// Languages of scripts, stored as Postman's <c>script.type</c>: Python, run by the user's Python, JavaScript
/// (Postman's own scripts, with the <c>pm</c> object), run by the user's Node.js or Bun, C#, built and run by the user's
/// .NET SDK (10 or later), and PowerShell, run by the user's PowerShell (7 or later).
/// </summary>
public static class ScriptLanguage
{
    public const string Python = "python";

    public const string JavaScript = "javascript";

    public const string CSharp = "csharp";

    public const string PowerShell = "powershell";

    /// <summary><c>script.type</c> of a Python script (Postman's own scripts are <c>text/javascript</c>).</summary>
    public const string PythonType = "text/x-python";

    public const string JavaScriptType = "text/javascript";

    public const string CSharpType = "text/x-csharp";

    public const string PowerShellType = "text/x-powershell";

    public static IReadOnlyList<string> All { get; } = [Python, JavaScript, CSharp, PowerShell];

    public static string FromType(string? type) =>
        type?.Contains("python", StringComparison.OrdinalIgnoreCase) == true ? Python
        : type?.Contains("csharp", StringComparison.OrdinalIgnoreCase) == true ? CSharp
        : type?.Contains("powershell", StringComparison.OrdinalIgnoreCase) == true ? PowerShell
        : JavaScript;

    public static string ToType(string language) => language switch
    {
        Python => PythonType,
        CSharp => CSharpType,
        PowerShell => PowerShellType,
        _ => JavaScriptType,
    };

    public static string Label(string language) => language switch
    {
        Python => "Python",
        CSharp => "C#",
        PowerShell => "PowerShell",
        _ => "JavaScript",
    };

    /// <summary>What runs scripts in <paramref name="language"/>, for messages: "Python", "JavaScript runtime (Node.js or Bun)"…</summary>
    public static string RuntimeName(string language) => language switch
    {
        Python => "Python",
        CSharp => ".NET SDK (10 or later)",
        PowerShell => "PowerShell (7 or later)",
        _ => "JavaScript runtime (Node.js or Bun)",
    };

    /// <summary>The command-line option that chooses the runtime: <c>--python</c>, <c>--javascript</c>, <c>--csharp</c> or <c>--powershell</c>.</summary>
    public static string Option(string language) => language switch
    {
        Python => "--python",
        CSharp => "--csharp",
        PowerShell => "--powershell",
        _ => "--javascript",
    };

    /// <summary>A language from its name, as settings and the command line give it ("python", "PowerShell", "C#", "js"…); null when unknown.</summary>
    public static string? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "python" or "py" => Python,
        "javascript" or "js" => JavaScript,
        "csharp" or "c#" or "cs" => CSharp,
        "powershell" or "pwsh" => PowerShell,
        _ => null,
    };

    /// <summary>
    /// The order in which a request's languages are tried: those of <paramref name="preferred"/> first (unknown and
    /// repeated ones left out), then the others in the order of <see cref="All"/>.
    /// </summary>
    public static IReadOnlyList<string> Order(IEnumerable<string>? preferred)
    {
        var order = new List<string>();
        foreach (var language in (preferred ?? []).Select(Parse).OfType<string>().Concat(All))
        {
            if (!order.Contains(language))
            {
                order.Add(language);
            }
        }

        return order;
    }

    /// <summary>Where <paramref name="language"/> is in <see cref="All"/> (unknown ones last).</summary>
    public static int Rank(string language)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i] == language)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    /// <summary>Node.js, Bun, or the file name of another JavaScript runtime.</summary>
    public static string JavaScriptRuntimeName(string runtime) => Path.GetFileNameWithoutExtension(runtime.Trim().Trim('"')).ToLowerInvariant() switch
    {
        "node" or "nodejs" => "Node.js",
        "bun" => "Bun",
        var name => name.Length == 0 ? "The JavaScript runtime" : name,
    };
}

/// <summary>When scripts run: before the request is sent (pre-request), or once its response is in (post-response, tests).</summary>
public enum ScriptPhase
{
    PreRequest,
    PostResponse,
}

public static class ScriptPhases
{
    public static IReadOnlyList<ScriptPhase> All { get; } = [ScriptPhase.PreRequest, ScriptPhase.PostResponse];

    /// <summary>"pre-request" or "post-response".</summary>
    public static string Label(this ScriptPhase phase) => phase == ScriptPhase.PreRequest ? "pre-request" : "post-response";
}

/// <summary>The scripts of a collection, folder or request in one language: one pre-request and one post-response (test) script.</summary>
public sealed record LanguageScripts(string Language, string PreRequest, string Test)
{
    public bool IsEmpty => !Has(ScriptPhase.PreRequest) && !Has(ScriptPhase.PostResponse);

    public string Code(ScriptPhase phase) => phase == ScriptPhase.PreRequest ? PreRequest : Test;

    public bool Has(ScriptPhase phase) => Code(phase).Trim().Length > 0;
}

/// <summary>
/// The scripts of a collection, folder or request, run before the request is sent and after its response: in each
/// language, one pre-request and one post-response script at most. A request runs the scripts of one language only
/// (see <see cref="ScriptPlan"/>), so those in other languages are alternatives (a Postman JavaScript test and a
/// PowerShell one, say).
/// </summary>
public sealed class ScriptSet : IEquatable<ScriptSet>
{
    private readonly LanguageScripts[] _languages;

    /// <summary>A set with the scripts of one language.</summary>
    public ScriptSet(string preRequest, string test, string language)
        : this([new LanguageScripts(language, preRequest, test)])
    {
    }

    /// <summary>A set with these scripts: a later language's replace an earlier one's, and languages without code are left out.</summary>
    public ScriptSet(IEnumerable<LanguageScripts> languages)
    {
        var byLanguage = new Dictionary<string, LanguageScripts>(StringComparer.Ordinal);
        foreach (var scripts in languages)
        {
            byLanguage[scripts.Language] = scripts;
        }

        _languages = [.. byLanguage.Values.Where(s => !s.IsEmpty).OrderBy(s => ScriptLanguage.Rank(s.Language)).ThenBy(s => s.Language, StringComparer.Ordinal)];
    }

    public static ScriptSet Empty { get; } = new([]);

    /// <summary>The languages that have scripts, in the order of <see cref="ScriptLanguage.All"/>.</summary>
    public IReadOnlyList<LanguageScripts> Languages => _languages;

    public bool IsEmpty => _languages.Length == 0;

    /// <summary>The scripts in <paramref name="language"/>; empty ones when it has none.</summary>
    public LanguageScripts For(string language) => Array.Find(_languages, s => s.Language == language) ?? new LanguageScripts(language, "", "");

    public bool Has(ScriptPhase phase, string language) => For(language).Has(phase);

    /// <summary>Whether there is a script for <paramref name="phase"/>, in any language.</summary>
    public bool Has(ScriptPhase phase) => _languages.Any(s => s.Has(phase));

    /// <summary>This set with <paramref name="scripts"/> instead of the scripts it had in their language.</summary>
    public ScriptSet With(LanguageScripts scripts) => new(_languages.Append(scripts));

    public bool Equals(ScriptSet? other) => other is not null && _languages.SequenceEqual(other._languages);

    public override bool Equals(object? obj) => Equals(obj as ScriptSet);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var scripts in _languages)
        {
            hash.Add(scripts);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(ScriptSet? left, ScriptSet? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(ScriptSet? left, ScriptSet? right) => !(left == right);

    public override string ToString() => IsEmpty ? "no scripts" : string.Join(" | ", _languages.Select(s => s.ToString()));
}

/// <summary>
/// How a request runs its scripts, as its Scripts tab says: in which <see cref="Language"/> (null: the first language of
/// the user's order that has all the scripts it runs), and whether its pre-request and post-response scripts run, its
/// folders' and collection's included (null: when there are some).
/// </summary>
public sealed record ScriptSettings(string? Language = null, bool? PreRequest = null, bool? PostResponse = null)
{
    public static ScriptSettings Auto { get; } = new();

    public bool IsAuto => Language is null && PreRequest is null && PostResponse is null;

    /// <summary>Whether the scripts of <paramref name="phase"/> run; null: when there are some.</summary>
    public bool? Runs(ScriptPhase phase) => phase == ScriptPhase.PreRequest ? PreRequest : PostResponse;

    public ScriptSettings WithRuns(ScriptPhase phase, bool? runs) =>
        phase == ScriptPhase.PreRequest ? this with { PreRequest = runs } : this with { PostResponse = runs };
}

/// <summary>One script to run: where it comes from (shown in errors and output), its code and its language.</summary>
public sealed record ScriptCode(string Name, string Code, string Language = ScriptLanguage.Python);

/// <summary>A script that did not run because no runtime is chosen for its language.</summary>
public sealed record ScriptSkip(string Script, string Language);

/// <summary>A test from <c>tr.test(name, fn)</c> (Python), <c>pm.test(name, fn)</c> (JavaScript) or <c>tr.Test(name, …)</c> (C#, PowerShell).</summary>
public sealed record ScriptTest(string Name, bool Passed, string? Error, string Script);

/// <summary>A line printed by a script.</summary>
public sealed record ScriptLog(string Phase, string Script, string Text);

/// <summary>
/// Why scripts stopped: the script that failed and its traceback, or (with no <see cref="Script"/>) why they could not
/// run at all.
/// </summary>
public sealed record ScriptError(string Script, string Message, string? Traceback = null)
{
    /// <summary>What the runtime printed: the traceback, or the message alone.</summary>
    public string Details => string.IsNullOrWhiteSpace(Traceback) ? Message : Traceback.TrimEnd();

    /// <summary>One line: which script failed and why.</summary>
    public override string ToString() => Script.Length == 0 ? Message : $"{Script}: {Message}";
}

/// <summary>What the scripts around one request did: tests, printed lines, variables they set, and the first error.</summary>
public sealed class ScriptResults
{
    public List<ScriptTest> Tests { get; } = [];

    public List<ScriptLog> Logs { get; } = [];

    /// <summary>
    /// Variables the scripts set or unset, as <c>scope.name</c> with the scope named as in the script's language
    /// (<c>collection_variables</c>, <c>collectionVariables</c> or <c>CollectionVariables</c> …). Values are not kept
    /// here: they may be secrets.
    /// </summary>
    public List<string> VariablesSet { get; } = [];

    public List<string> Notes { get; } = [];

    /// <summary>Scripts that did not run because no runtime is chosen for their language (in the full-screen UI).</summary>
    public List<ScriptSkip> NotRun { get; } = [];

    /// <summary>One note per language with scripts that did not run.</summary>
    public IEnumerable<string> NotRunNotes => NotRun.GroupBy(s => s.Language).Select(g =>
        $"Not run, because no {ScriptLanguage.RuntimeName(g.Key)} is chosen for this workspace (Tools › Scripts…): " +
        $"the {ScriptLanguage.Label(g.Key)} scripts of {string.Join(", ", g.Select(s => s.Script).Distinct(StringComparer.Ordinal))}.");

    /// <summary>Scripts that ran (pre-request and test).</summary>
    public int Ran { get; set; }

    /// <summary>The first error. A failed pre-request script stops the request: it is not sent.</summary>
    public ScriptError? Error { get; set; }

    public int Passed => Tests.Count(t => t.Passed);

    public int Failed => Tests.Count(t => !t.Passed);

    /// <summary>Whether every test passed and no script failed (scripts that did not run are in <see cref="NotRun"/>).</summary>
    public bool Succeeded => Error is null && Failed == 0;

    /// <summary>A short summary for status lines ("tests 3/4 passed", "✗ script error"); empty when no script ran.</summary>
    public string Summary
    {
        get
        {
            var summary = Error is not null ? "✗ script error"
                : Tests.Count > 0 ? Failed == 0 ? $"tests {Passed}/{Tests.Count} passed" : $"✗ {Failed} of {Tests.Count} tests failed"
                : Ran > 0 ? $"{Ran} script{(Ran == 1 ? "" : "s")} ran" : "";
            return NotRun.Count == 0 ? summary : summary.Length == 0 ? "⚠ scripts not run" : summary + " · ⚠ some scripts not run";
        }
    }

    /// <summary>Everything, as text: tests, the error, printed lines, variables set and notes.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        if (Tests.Count > 0)
        {
            text.Append($"Tests: {Passed} passed, {Failed} failed\n");
            foreach (var test in Tests)
            {
                text.Append(test.Passed ? "  ✓ " : "  ✗ ").Append(test.Name);
                if (test.Error is { Length: > 0 } error)
                {
                    text.Append(" — ").Append(error);
                }

                text.Append('\n');
            }

            text.Append('\n');
        }

        if (Error is not null)
        {
            text.Append(Error.Script.Length == 0 ? "Scripts did not run\n" : $"Script error in {Error.Script}\n")
                .Append(Indent(Error.Details)).Append("\n\n");
        }

        if (Logs.Count > 0)
        {
            text.Append("Output\n");
            foreach (var log in Logs)
            {
                text.Append($"  [{log.Phase} · {log.Script}] {log.Text}\n");
            }

            text.Append('\n');
        }

        if (VariablesSet.Count > 0)
        {
            text.Append("Variables set (kept until TermRest closes; files are not changed)\n");
            foreach (var name in VariablesSet.Distinct(StringComparer.Ordinal))
            {
                text.Append("  ").Append(name).Append(name.StartsWith("variables.", StringComparison.OrdinalIgnoreCase) ? "  (this request only)\n" : "\n");
            }

            text.Append('\n');
        }

        foreach (var note in Notes.Concat(NotRunNotes))
        {
            text.Append(note).Append('\n');
        }

        return text.Length == 0 ? $"{Ran} script{(Ran == 1 ? "" : "s")} ran, without output." : text.ToString().TrimEnd();
    }

    private static string Indent(string text) => "  " + text.TrimEnd().Replace("\n", "\n  ", StringComparison.Ordinal);
}

/// <summary>Scripts could not run: no runtime, the runtime could not start or stopped, or a script took too long.</summary>
public sealed class ScriptException(string message, Exception? inner = null) : Exception(message, inner);
