using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using TermRest.Core.History;
using TermRest.Core.Http;
using TermRest.Core.Json;
using TermRest.Core.Workspace;

namespace TermRest.Cli;

/// <summary><c>termrest history</c>: list, show or clear the requests sent from a workspace (newest first).</summary>
internal static class HistoryCommand
{
    public static Command Create()
    {
        var command = new Command("history", "Show the requests sent from a workspace (TUI and 'termrest send'), newest first.");
        var workspace = new Argument<string?>("workspace") { Description = "Workspace folder or collection (default: current directory)", Arity = ArgumentArity.ZeroOrOne };
        var limit = new Option<int>("--limit", "-n") { Description = "Number of entries to list", DefaultValueFactory = _ => 20 };
        var show = new Option<string?>("--show") { Description = "Print one entry in full: request, response headers and body (id or id prefix)" };
        var json = new Option<bool>("--json") { Description = "Print entries as JSON" };
        var clear = new Option<bool>("--clear") { Description = "Delete the workspace's history" };
        command.Arguments.Add(workspace);
        command.Options.Add(limit);
        command.Options.Add(show);
        command.Options.Add(json);
        command.Options.Add(clear);
        command.SetAction(parseResult =>
        {
            var path = parseResult.GetValue(workspace) ?? Environment.CurrentDirectory;
            ApiWorkspace opened;
            try
            {
                opened = ApiWorkspace.Open(path, CollectionLoading.None);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Output.Error(ex.Message);
                return 2;
            }

            var store = HistoryStore.ForWorkspace(opened.RootPath, opened.Settings.HistoryLimit);
            if (parseResult.GetValue(clear))
            {
                Exception? bodiesLeft;
                try
                {
                    bodiesLeft = store.Clear();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another program has the history file open (an antivirus scan, Get-Content -Wait): nothing was deleted.
                    Output.Error($"The history could not be cleared ({store.Folder}): {ex.Message}");
                    return 1;
                }

                if (bodiesLeft is not null)
                {
                    Output.Error($"History cleared, but some response bodies could not be deleted ({store.BodiesFolder}): {bodiesLeft.Message}");
                    return 1;
                }

                Output.Info($"History cleared ({store.Folder}).");
                return 0;
            }

            List<HistoryEntry> entries;
            try
            {
                entries = store.Load();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Output.Error($"The history could not be read ({store.Folder}): {ex.Message}");
                return 1;
            }

            if (parseResult.GetValue(show) is { } id)
            {
                var matches = entries.Where(e => e.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1)
                {
                    Output.Error(matches.Count == 0 ? $"No history entry starts with '{id}'." : $"'{id}' matches {matches.Count} entries; use more characters.");
                    return 2;
                }

                try
                {
                    Show(matches[0], store, parseResult.GetValue(json));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Output.Error($"The response body of {ShortId(matches[0].Id)} could not be read ({store.BodiesFolder}): {ex.Message}");
                    return 1;
                }

                return 0;
            }

            var listed = entries.Take(Math.Max(1, parseResult.GetValue(limit))).ToList();
            if (parseResult.GetValue(json))
            {
                var array = new JsonArray();
                foreach (var entry in listed)
                {
                    array.Add((JsonNode)entry.ToJson());
                }

                Console.Out.WriteLine(PostmanJson.SerializeForDisplay(array));
                return 0;
            }

            if (listed.Count == 0)
            {
                Output.Info(opened.Settings.HistoryEnabled ? "No requests in the history yet." : "History is turned off for this workspace (Tools > Settings).");
                return 0;
            }

            foreach (var entry in listed)
            {
                var status = entry.Status?.ToString(CultureInfo.InvariantCulture) ?? "ERR";
                var where = entry.RequestPath is null ? "" : $"  ({entry.Collection} / {entry.RequestPath})";
                Console.Out.WriteLine($"{ShortId(entry.Id),-8}  {entry.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {status,-4} {HttpExchange.Format(entry.Timing.Total),9}  {entry.Method,-7} {entry.Url}{where}");
            }

            if (entries.Count > listed.Count)
            {
                Output.Info($"… {entries.Count - listed.Count} older entries (use --limit).");
            }

            return 0;
        });
        return command;
    }

    /// <summary>An entry's id as listed: its first 8 characters (an id edited by hand may have fewer).</summary>
    private static string ShortId(string id) => id.Length > 8 ? id[..8] : id;

    private static void Show(HistoryEntry entry, HistoryStore store, bool json)
    {
        if (json)
        {
            Console.Out.WriteLine(PostmanJson.SerializeForDisplay(entry.ToJson()));
            return;
        }

        var exchange = entry.ToExchange(store.ReadBody(entry));
        Console.Out.WriteLine($"{entry.Method} {entry.Url}");
        foreach (var (key, value) in entry.SentHeaders)
        {
            Console.Out.WriteLine($"{key}: {value}");
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"Sent {entry.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}{(entry.Environment is null ? "" : $" with environment '{entry.Environment}'")}" +
                              $"{(entry.RequestPath is null ? "" : $" from {entry.Collection} / {entry.RequestPath}")}");
        if (exchange.Response is not { } response)
        {
            Console.Out.WriteLine($"Error: {entry.Error}");
            return;
        }

        Console.Out.WriteLine($"HTTP/{entry.HttpVersion} {response.StatusCode} {response.ReasonPhrase} · {HttpExchange.Format(entry.Timing.Total)} · {HttpExchange.FormatSize(entry.Size ?? 0)}");
        foreach (var (key, value) in response.Headers)
        {
            Console.Out.WriteLine($"{key}: {value}");
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine(entry.BodySaved
            ? BodyFormatter.Format(response.Body, response.ContentType) + (entry.BodyTruncated ? "\n… (truncated)" : "")
            : "(response body not kept)");
    }
}
