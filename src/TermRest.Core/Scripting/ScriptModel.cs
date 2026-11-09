using System.Text;

namespace TermRest.Core.Scripting;

/// <summary>
/// Languages of scripts, stored as Postman's <c>script.type</c>: Python, run by the user's Python, JavaScript
/// (Postman's own scripts, with the <c>pm</c> object), run by the user's Node.js or Bun, and C#, built and run by the
/// user's .NET SDK (10 or later).
/// </summary>
public static class ScriptLanguage
{
    public const string Python = "python";

    public const string JavaScript = "javascript";

    public const string CSharp = "csharp";

    /// <summary><c>script.type</c> of a Python script (Postman's own scripts are <c>text/javascript</c>).</summary>
    public const string PythonType = "text/x-python";

    public const string JavaScriptType = "text/javascript";

    public const string CSharpType = "text/x-csharp";

    public static IReadOnlyList<string> All { get; } = [Python, JavaScript, CSharp];

    public static string FromType(string? type) =>
        type?.Contains("python", StringComparison.OrdinalIgnoreCase) == true ? Python
        : type?.Contains("csharp", StringComparison.OrdinalIgnoreCase) == true ? CSharp
        : JavaScript;

    public static string ToType(string language) => language switch
    {
        Python => PythonType,
        CSharp => CSharpType,
        _ => JavaScriptType,
    };

    public static string Label(string language) => language switch
    {
        Python => "Python",
        CSharp => "C#",
        _ => "JavaScript",
    };

    /// <summary>What runs scripts in <paramref name="language"/>, for messages: "Python", "JavaScript runtime (Node.js or Bun)"…</summary>
    public static string RuntimeName(string language) => language switch
    {
        Python => "Python",
        CSharp => ".NET SDK (10 or later)",
        _ => "JavaScript runtime (Node.js or Bun)",
    };

    /// <summary>The command-line option that chooses the runtime: <c>--python</c>, <c>--javascript</c> or <c>--csharp</c>.</summary>
    public static string Option(string language) => language switch
    {
        Python => "--python",
        CSharp => "--csharp",
        _ => "--javascript",
    };

    /// <summary>Node.js, Bun, or the file name of another JavaScript runtime.</summary>
    public static string JavaScriptRuntimeName(string runtime) => Path.GetFileNameWithoutExtension(runtime.Trim().Trim('"')).ToLowerInvariant() switch
    {
        "node" or "nodejs" => "Node.js",
        "bun" => "Bun",
        var name => name.Length == 0 ? "The JavaScript runtime" : name,
    };
}

/// <summary>The scripts of a collection, folder or request: run before the request is sent, and after the response.</summary>
public sealed record ScriptSet(string PreRequest, string Test, string Language)
{
    public static ScriptSet Empty { get; } = new("", "", ScriptLanguage.Python);

    public bool IsEmpty => PreRequest.Trim().Length == 0 && Test.Trim().Length == 0;

    /// <summary>Same code and language; the language of a set without code does not matter.</summary>
    public bool SameAs(ScriptSet other) =>
        PreRequest == other.PreRequest && Test == other.Test && (Language == other.Language || IsEmpty && other.IsEmpty);
}

/// <summary>One script to run: where it comes from (shown in errors and output), its code and its language.</summary>
public sealed record ScriptCode(string Name, string Code, string Language = ScriptLanguage.Python);

/// <summary>A script that did not run because no runtime is chosen for its language.</summary>
public sealed record ScriptSkip(string Script, string Language);

/// <summary>A test from <c>tr.test(name, fn)</c> (Python), <c>pm.test(name, fn)</c> (JavaScript) or <c>tr.Test(name, …)</c> (C#).</summary>
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
