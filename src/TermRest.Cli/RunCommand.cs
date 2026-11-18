using System.CommandLine;
using System.Text.Json;
using TermRest.Core.Http;
using TermRest.Core.Postman;
using TermRest.Core.Recording;
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
        var iterationData = new Option<string?>("--iteration-data", "-d")
        {
            Description = "Run the requests once for each row of this data file: CSV with a header row, or JSON (an array of objects); " +
                          "its values are data variables ({{name}}, pm.iterationData)",
        };
        var iterationCount = new Option<int?>("--iteration-count", "-n")
        {
            Description = "Run the requests this many times (default: once, or once for each row of --iteration-data; past its last row, that row is used again)",
        };
        var options = new RequestOptions();
        command.Arguments.Add(collection);
        command.Options.Add(folder);
        options.AddTo(command);
        command.Options.Add(bail);
        command.Options.Add(allowHttpErrors);
        command.Options.Add(json);
        command.Options.Add(iterationData);
        command.Options.Add(iterationCount);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(collection)!;
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Output.Error($"'{path}' is not a collection file or v3 collection folder.");
                return 2;
            }

            if (options.Open(parseResult, path, insecure) is not { } opened)
            {
                return 2;
            }

            using var runner = opened.Runner;
            if (opened.Collection is not { } target)
            {
                // A workspace folder: send finds a request in any of its collections, but run needs one.
                Output.Error($"'{path}' is not a Postman v3 collection folder; pass the collection itself.");
                return 2;
            }

            if (RequestOptions.ReportUnreadableSettings(opened.Workspace))
            {
                return 1;
            }

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

            // The data file is read through once first: a row that isn't valid stops the run before anything is sent.
            IterationDataFile? data = null;
            var rows = 0;
            if (parseResult.GetValue(iterationData) is { } dataPath)
            {
                try
                {
                    data = IterationDataFile.Open(dataPath);
                    rows = await data.CountAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Output.Error($"--iteration-data: {ex.Message}");
                    return 2;
                }

                if (rows == 0)
                {
                    Output.Error($"--iteration-data: {dataPath} has no rows{(data.IsJson ? "" : " (the first row of a CSV file names the values)")}.");
                    return 2;
                }
            }

            var iterations = parseResult.GetValue(iterationCount) ?? Math.Max(1, rows);
            if (iterations < 1)
            {
                Output.Error("--iteration-count: give 1 or more.");
                return 2;
            }

            if (!options.StartRecording(parseResult, opened.Workspace, runner, out var recorder))
            {
                return 2;
            }

            Output.Info($"Running {requests.Count} request(s){(iterations > 1 ? $" × {iterations} iterations" : "")} from '{target.Name}'" +
                        $"{(root is CollectionRootNode ? "" : " / " + root.PathInCollection)}" +
                        $"{(runner.Environment is { } env ? $" with environment '{env.Name}'" : "")}" +
                        $"{(data is null ? "" : $", data from {Path.GetFileName(data.Path)} ({rows} row(s))")}\n");

            var reportPath = parseResult.GetValue(json);
            var results = new RunResults(reportPath is null ? null : new Redactor(opened.Workspace.Settings.RecordingRedact));
            var notesShown = new HashSet<string>(StringComparer.Ordinal);
            var cancelled = false;
            var stopped = false;
            var dataFailed = false;
            await using var dataRows = data?.ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            System.Text.Json.Nodes.JsonObject? row = null;
            runner.IterationCount = iterations;
            for (var iteration = 0; iteration < iterations && !stopped; iteration++)
            {
                try
                {
                    // Past the last row, that row again, as in Postman.
                    if (dataRows is not null && await dataRows.MoveNextAsync())
                    {
                        row = dataRows.Current;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Output.Error($"--iteration-data: {ex.Message}");
                    dataFailed = true;
                    break;
                }

                runner.Iteration = iteration;
                runner.IterationData = row;
                if (iterations > 1)
                {
                    Output.Info($"{(iteration > 0 ? "\n" : "")}Iteration {iteration + 1}/{iterations}");
                }

                foreach (var node in requests)
                {
                    var exchange = await SendSafelyAsync(runner, target, node, cancellationToken);
                    if (exchange.Cancelled || cancellationToken.IsCancellationRequested)
                    {
                        cancelled = stopped = true;
                        break;
                    }

                    // Passing means a response (not an HTTP error, unless allowed), passing tests and no script errors.
                    var passed = exchange.Response is { } r && (parseResult.GetValue(allowHttpErrors) || r.StatusCode < 400) && exchange.Scripts?.Succeeded != false;
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
                    results.Add(node, exchange, passed, iteration + 1);
                    if (!passed && parseResult.GetValue(bail))
                    {
                        stopped = true;
                        break;
                    }
                }
            }

            var testSummary = results.TestsPassed + results.TestsFailed == 0 ? "" : $" (tests: {results.TestsPassed} passed, {results.TestsFailed} failed)";
            Output.Info($"\n{(cancelled ? "Run cancelled. " : "")}{results.Count - results.Failed} passed, {results.Failed} failed, {(requests.Count * iterations) - results.Count} skipped{testSummary}");
            var unreadable = RequestOptions.ReportUnreadableFiles(target);

            // A cancelled run still saves what it recorded, and reports what it did (not tied to the cancellation token).
            var recorded = options.SaveRecording(parseResult, runner, recorder, cancellationToken.IsCancellationRequested);
            var reported = reportPath is null || await WriteReportAsync(reportPath, results.ToJson(target, iterations));
            return cancelled ? 130 : results.Failed == 0 && recorded && reported && !unreadable && !dataFailed ? 0 : 1;
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

    /// <summary>
    /// What a run did: counts and, for <c>--report-json</c>, what the report says of each request. Exchanges are not kept:
    /// each holds its response body (up to the maximum response size), and a run's memory must not grow with every
    /// response it receives.
    /// </summary>
    /// <param name="secrets">What the report redacts; null when there is no report.</param>
    private sealed class RunResults(Redactor? secrets)
    {
        private readonly List<ReportEntry> _entries = [];

        public int Count { get; private set; }

        public int Failed { get; private set; }

        public int TestsPassed { get; private set; }

        public int TestsFailed { get; private set; }

        public void Add(RequestNode node, HttpExchange exchange, bool passed, int iteration)
        {
            Count++;
            Failed += passed ? 0 : 1;
            TestsPassed += exchange.Scripts?.Passed ?? 0;
            TestsFailed += exchange.Scripts?.Failed ?? 0;
            if (secrets is null)
            {
                return;
            }

            // Secrets are redacted as in recordings, since reports are kept as CI artifacts: auth credentials, secret
            // variables and variables named like credentials, URL passwords, and the values a recording redacts by name
            // (a token in a response, a cookie), wherever they appear, also in the requests that follow.
            var redact = secrets.ForReport(exchange);
            var response = exchange.Response;
            var scripts = exchange.Scripts;
            _entries.Add(new ReportEntry(
                node.PathInCollection,
                iteration,
                exchange.Request.Method,
                redact.Url(exchange.Request.Url, "url"),
                passed,
                response?.StatusCode,
                response?.Body.LongLength ?? 0,
                response is null && exchange.ErrorMessage is { } message ? redact.Text(message, "error") : null,
                scripts?.Tests.Select(t => new ReportTest(redact.Text(t.Name, "tests"), t.Passed, t.Error is null ? null : redact.Text(t.Error, "tests"))).ToList(),
                scripts?.Error is { } error ? redact.Text(error.ToString(), "scripts") : null,
                exchange.Timing.Total?.TotalMilliseconds ?? 0));
        }

        /// <summary>The JSON report (UTF-8).</summary>
        public byte[] ToJson(PostmanCollection collection, int iterations)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("collection", collection.Name);
                writer.WriteString("finishedAt", DateTimeOffset.Now);
                writer.WriteNumber("iterations", iterations);
                writer.WriteNumber("passed", Count - Failed);
                writer.WriteNumber("failed", Failed);
                writer.WriteStartArray("results");
                foreach (var entry in _entries)
                {
                    writer.WriteStartObject();
                    writer.WriteString("request", entry.Request);
                    writer.WriteNumber("iteration", entry.Iteration);
                    writer.WriteString("method", entry.Method);
                    writer.WriteString("url", entry.Url);
                    writer.WriteBoolean("passed", entry.Passed);
                    if (entry.Status is { } status)
                    {
                        writer.WriteNumber("status", status);
                        writer.WriteNumber("bytes", entry.Bytes);
                    }
                    else
                    {
                        writer.WriteString("error", entry.Error);
                    }

                    if (entry.Tests is { } tests)
                    {
                        writer.WriteStartArray("tests");
                        foreach (var test in tests)
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
                        if (entry.ScriptError is { } scriptError)
                        {
                            writer.WriteString("scriptError", scriptError);
                        }
                    }

                    writer.WriteNumber("totalMs", entry.TotalMs);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return stream.ToArray();
        }
    }

    /// <summary>What the report says of one request in one iteration (from 1), redacted. <see cref="Status"/> is null when there was no response (see <see cref="Error"/>).</summary>
    private sealed record ReportEntry(
        string Request, int Iteration, string Method, string Url, bool Passed, int? Status, long Bytes, string? Error, List<ReportTest>? Tests, string? ScriptError, double TotalMs);

    private sealed record ReportTest(string Name, bool Passed, string? Error);

    /// <summary>
    /// Writes the report, readable only by the user on Unix as recordings are; missing folders are created. Returns false
    /// when it could not be written (reported).
    /// </summary>
    private static async Task<bool> WriteReportAsync(string path, byte[] report)
    {
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var file = new FileStream(path, options))
            {
                await file.WriteAsync(report);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // a file that existed keeps its mode otherwise
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Output.Error($"The report could not be written to {path}: {ex.Message}");
            return false;
        }

        Output.Info($"Report written to {path}");
        return true;
    }
}
