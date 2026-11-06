using System.Text;

namespace TermRest.Core.Scripting;

/// <summary>
/// Languages of Postman scripts. TermRest runs Python scripts (in the user's own interpreter); Postman's JavaScript
/// scripts are kept as they are but not run. The language is stored as Postman's <c>script.type</c>.
/// </summary>
public static class ScriptLanguage
{
    public const string Python = "python";

    public const string JavaScript = "javascript";

    /// <summary><c>script.type</c> of a Python script (Postman's own scripts are <c>text/javascript</c>).</summary>
    public const string PythonType = "text/x-python";

    public const string JavaScriptType = "text/javascript";

    public static string FromType(string? type) =>
        type?.Contains("python", StringComparison.OrdinalIgnoreCase) == true ? Python : JavaScript;

    public static string ToType(string language) => language == Python ? PythonType : JavaScriptType;

    public static string Label(string language) => language == Python ? "Python" : "JavaScript (Postman)";
}

/// <summary>The scripts of a collection, folder or request: run before the request is sent, and after the response.</summary>
public sealed record ScriptSet(string PreRequest, string Test, string Language)
{
    public static ScriptSet Empty { get; } = new("", "", ScriptLanguage.Python);

    public bool IsEmpty => PreRequest.Trim().Length == 0 && Test.Trim().Length == 0;

    public bool IsPython => Language == ScriptLanguage.Python;

    /// <summary>Same code and language; the language of a set without code does not matter.</summary>
    public bool SameAs(ScriptSet other) =>
        PreRequest == other.PreRequest && Test == other.Test && (Language == other.Language || IsEmpty && other.IsEmpty);
}

/// <summary>One script to run: where it comes from (shown in errors and output) and its code.</summary>
public sealed record ScriptCode(string Name, string Code);

/// <summary>A test from <c>tr.test(name, fn)</c>.</summary>
public sealed record ScriptTest(string Name, bool Passed, string? Error, string Script);

/// <summary>A line printed by a script.</summary>
public sealed record ScriptLog(string Phase, string Script, string Text);

/// <summary>
/// Why scripts stopped: the script that failed and Python's traceback, or (with no <see cref="Script"/>) why Python
/// could not run them at all.
/// </summary>
public sealed record ScriptError(string Script, string Message, string? Traceback = null)
{
    /// <summary>What Python printed: the traceback, or the message alone.</summary>
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
    /// Variables the scripts set or unset, as <c>scope.name</c> with the scope named as in the <c>tr</c> object
    /// (values are not kept here: they may be secrets).
    /// </summary>
    public List<string> VariablesSet { get; } = [];

    public List<string> Notes { get; } = [];

    /// <summary>Where JavaScript (Postman) scripts were found and not run, such as <c>request 'Users/Get'</c>.</summary>
    public List<string> NotRun { get; } = [];

    /// <summary>The note about <see cref="NotRun"/>, or null.</summary>
    public string? NotRunNote => NotRun.Count == 0 ? null
        : $"Not run: the JavaScript (Postman) scripts of {string.Join(", ", NotRun)}. TermRest runs scripts written in Python.";

    /// <summary>Scripts that ran (pre-request and test).</summary>
    public int Ran { get; set; }

    /// <summary>The first error. A failed pre-request script stops the request: it is not sent.</summary>
    public ScriptError? Error { get; set; }

    public int Passed => Tests.Count(t => t.Passed);

    public int Failed => Tests.Count(t => !t.Passed);

    /// <summary>Whether every test passed and no script failed.</summary>
    public bool Succeeded => Error is null && Failed == 0;

    /// <summary>A short summary for status lines ("tests 3/4 passed", "✗ script error"); empty when no script ran.</summary>
    public string Summary => Error is not null ? "✗ script error"
        : Tests.Count > 0 ? Failed == 0 ? $"tests {Passed}/{Tests.Count} passed" : $"✗ {Failed} of {Tests.Count} tests failed"
        : Ran > 0 ? $"{Ran} script{(Ran == 1 ? "" : "s")} ran" : "";

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
                text.Append("  ").Append(name).Append(name.StartsWith("variables.", StringComparison.Ordinal) ? "  (this request only)\n" : "\n");
            }

            text.Append('\n');
        }

        foreach (var note in Notes.Append(NotRunNote).OfType<string>())
        {
            text.Append(note).Append('\n');
        }

        return text.Length == 0 ? $"{Ran} script{(Ran == 1 ? "" : "s")} ran, without output." : text.ToString().TrimEnd();
    }

    private static string Indent(string text) => "  " + text.TrimEnd().Replace("\n", "\n  ", StringComparison.Ordinal);
}

/// <summary>Scripts could not run: no interpreter, Python could not start or stopped, or a script took too long.</summary>
public sealed class ScriptException(string message, Exception? inner = null) : Exception(message, inner);
