using TermRest.Core.Http;

namespace TermRest.Cli;

/// <summary>Prints what scripts did, to stderr: printed lines, tests, the error with its traceback, and notes.</summary>
internal static class ScriptOutput
{
    /// <param name="indent">Prefix of every line (the collection runner indents them under the request).</param>
    /// <param name="notesShown">Notes printed already: each is printed once. Null prints them all.</param>
    public static void Print(HttpExchange exchange, string indent = "", ISet<string>? notesShown = null)
    {
        if (exchange.Scripts is not { } results)
        {
            return;
        }

        foreach (var log in results.Logs)
        {
            Output.Info($"{indent}[{log.Phase} · {log.Script}] {log.Text}");
        }

        foreach (var test in results.Tests)
        {
            Output.Status($"{indent}{(test.Passed ? "✓" : "✗")} {test.Name}{(test.Error is { Length: > 0 } error ? " — " + error : "")}", test.Passed);
        }

        if (results.Error is { } failure)
        {
            // A request that was not sent has its error printed already ("Not sent: …"): only the traceback is new.
            if (exchange.Response is null)
            {
                foreach (var line in (failure.Traceback ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    Output.Info($"{indent}  {line.TrimEnd()}");
                }
            }
            else
            {
                Output.Status($"{indent}{(failure.Script.Length == 0 ? "Scripts did not run" : $"Script error in {failure.Script}")}:", false);
                foreach (var line in failure.Details.Split('\n'))
                {
                    Output.Info($"{indent}  {line.TrimEnd()}");
                }
            }
        }

        foreach (var note in results.Notes.Concat(results.NotRunNotes))
        {
            if (notesShown?.Add(note) != false)
            {
                Output.Info($"{indent}note: {note}");
            }
        }
    }
}
