using System.CommandLine;
using System.Text;
using System.Text.Json;
using TermRest.Core.Http;
using TermRest.Core.Postman;
using TermRest.Core.Workspace;

namespace TermRest.Cli;

/// <summary><c>termrest run &lt;collection&gt;</c>: send every request in order (a lightweight collection runner).</summary>
internal static class RunCommand
{
    public static Command Create(Option<bool> insecure)
    {
        var command = new Command("run", "Run all requests in a collection (or one folder) in order, with their scripts, and report status codes, tests and timings.");
        var collection = new Argument<string>("collection") { Description = "Collection file or v3 collection folder" };
        var folder = new Option<string?>("--folder") { Description = "Only run requests inside this folder path" };
        var bail = new Option<bool>("--bail") { Description = "Stop at the first failure" };
        var allowHttpErrors = new Option<bool>("--allow-http-errors") { Description = "Do not treat HTTP 4xx/5xx responses as failures" };
        var json = new Option<string?>("--report-json") { Description = "Also write a JSON report to this file" };
        var options = new RequestOptions();
        command.Arguments.Add(collection);
        command.Options.Add(folder);
        options.AddTo(command);
        command.Options.Add(bail);
        command.Options.Add(allowHttpErrors);
        command.Options.Add(json);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(collection)!;
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Output.Error($"'{path}' is not a collection file or v3 collection folder.");
                return 2;
            }

            if (options.Open(parseResult, path, insecure) is not { Collection: { } target } opened)
            {
                if (Directory.Exists(path))
                {
                    Output.Error($"'{path}' is not a Postman v3 collection folder; pass the collection itself.");
                }

                return 2;
            }

            using var runner = opened.Runner;
            FolderNode root = target.RootNode;
            if (parseResult.GetValue(folder) is { } folderPath)
            {
                if (target.FindFolder(folderPath) is not { } found)
                {
                    Output.Error($"Folder '{folderPath}' was not found.");
                    return 2;
                }

                root = found;
            }

            var requests = root is CollectionRootNode
                ? target.AllRequests().ToList()
                : target.AllRequests().Where(r => r.Ancestors().Contains(root)).ToList();
            if (!options.StartRecording(parseResult, opened.Workspace, runner, out var recorder))
            {
                return 2;
            }

            Output.Info($"Running {requests.Count} request(s) from '{target.Name}'{(root is CollectionRootNode ? "" : " / " + root.PathInCollection)}" +
                        $"{(runner.Environment is { } env ? $" with environment '{env.Name}'" : "")}\n");

            var results = new List<(RequestNode Node, HttpExchange Exchange, bool Passed)>();
            var notesShown = new HashSet<string>(StringComparer.Ordinal);
            var cancelled = false;
            foreach (var node in requests)
            {
                var exchange = await SendSafelyAsync(runner, target, node, cancellationToken);
                if (exchange.Cancelled || cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                // Passing means a response (not an HTTP error, unless allowed), passing tests and no script errors.
                var passed = exchange.Response is { } r && (parseResult.GetValue(allowHttpErrors) || r.StatusCode < 400) && exchange.Scripts?.Succeeded != false;
                results.Add((node, exchange, passed));
                var status = exchange.Response is { } response ? response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "ERR";
                Output.Status($"{(passed ? "✓" : "✗")} {status,-4} {HttpExchange.Format(exchange.Timing.Total),9}  {exchange.Request.Method,-7} {node.PathInCollection}", passed);
                if (exchange.Response is null)
                {
                    Output.Info($"      {exchange.ErrorMessage}");
                }

                foreach (var warning in exchange.Request.Warnings)
                {
                    Output.Warning($"  {warning}");
                }

                ScriptOutput.Print(exchange, "      ", notesShown);
                if (!passed && parseResult.GetValue(bail))
                {
                    break;
                }
            }

            var failed = results.Count(r => !r.Passed);
            var tests = results.SelectMany(r => r.Exchange.Scripts?.Tests ?? []).ToList();
            var testSummary = tests.Count == 0 ? "" : $" (tests: {tests.Count(t => t.Passed)} passed, {tests.Count(t => !t.Passed)} failed)";
            Output.Info($"\n{(cancelled ? "Run cancelled. " : "")}{results.Count - failed} passed, {failed} failed, {requests.Count - results.Count} skipped{testSummary}");

            // A cancelled run still saves what it recorded.
            var recorded = options.SaveRecording(parseResult, runner, recorder);
            if (parseResult.GetValue(json) is { } reportPath)
            {
                // Not tied to the cancellation token: a cancelled run still reports what it did.
                await File.WriteAllTextAsync(reportPath, BuildReport(target, results), CancellationToken.None);
                Output.Info($"Report written to {reportPath}");
            }

            return cancelled ? 130 : failed == 0 && recorded ? 0 : 1;
        });

        return command;
    }

    /// <summary>Sends one request with its scripts; a request that cannot even be prepared is recorded as a failure instead of ending the run.</summary>
    private static async Task<HttpExchange> SendSafelyAsync(RequestRunner runner, PostmanCollection collection, RequestNode node, CancellationToken cancellationToken)
    {
        PreparedRequest? prepared = null;
        try
        {
            return await runner.SendAsync(collection, node, node.ReadSpec(), p => prepared = p, cancellationToken);
        }
        catch (Exception ex)
        {
            return new HttpExchange(prepared ?? new PreparedRequest { Url = node.PathInCollection })
            {
                Exception = ex,
                Cancelled = cancellationToken.IsCancellationRequested,
                ErrorMessage = ex.Message,
            };
        }
    }

    private static string BuildReport(PostmanCollection collection, List<(RequestNode Node, HttpExchange Exchange, bool Passed)> results)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("collection", collection.Name);
            writer.WriteString("finishedAt", DateTimeOffset.Now);
            writer.WriteNumber("passed", results.Count(r => r.Passed));
            writer.WriteNumber("failed", results.Count(r => !r.Passed));
            writer.WriteStartArray("results");
            foreach (var (node, exchange, passed) in results)
            {
                writer.WriteStartObject();
                writer.WriteString("request", node.PathInCollection);
                writer.WriteString("method", exchange.Request.Method);
                writer.WriteString("url", exchange.Request.Url);
                writer.WriteBoolean("passed", passed);
                if (exchange.Response is { } response)
                {
                    writer.WriteNumber("status", response.StatusCode);
                    writer.WriteNumber("bytes", response.Body.LongLength);
                }
                else
                {
                    writer.WriteString("error", exchange.ErrorMessage);
                }

                if (exchange.Scripts is { } scripts)
                {
                    writer.WriteStartArray("tests");
                    foreach (var test in scripts.Tests)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", test.Name);
                        writer.WriteBoolean("passed", test.Passed);
                        if (test.Error is not null)
                        {
                            writer.WriteString("error", test.Error);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    if (scripts.Error is { } error)
                    {
                        writer.WriteString("scriptError", error.ToString());
                    }
                }

                writer.WriteNumber("totalMs", exchange.Timing.Total?.TotalMilliseconds ?? 0);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
