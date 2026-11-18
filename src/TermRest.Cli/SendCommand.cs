using System.CommandLine;
using System.Text;
using TermRest.Core.Http;
using TermRest.Core.Json;
using TermRest.Core.Postman;

namespace TermRest.Cli;

/// <summary><c>termrest send &lt;collection&gt; &lt;request&gt;</c>: send one saved request and print the response.</summary>
internal static class SendCommand
{
    public static Command Create(Option<bool> insecure)
    {
        var command = new Command("send", "Send one request from a collection and print the response body (status and diagnostics go to stderr).");
        var collection = new Argument<string>("collection") { Description = "Collection file or v3 collection folder, or a workspace folder" };
        var request = new Argument<string>("request") { Description = "Request path such as \"Users/Get user\", or a unique request name" };
        var include = new Option<bool>("--include", "-i") { Description = "Also print the response status line and headers to stdout" };
        var verbose = new Option<bool>("--verbose", "-v") { Description = "Print the request, timing and TLS details to stderr" };
        var output = new Option<string?>("--output", "-o") { Description = "Write the raw response body (with --filter, the values selected) to a file" };
        var raw = new Option<bool>("--raw") { Description = "Print the body as received (no JSON/XML pretty printing); with --filter, the values on one line" };
        var filter = new Option<string?>("--filter") { Description = "Print only the values a JSONPath query (RFC 9535) selects in a JSON body, as an array: --filter '$.items[?@.price < 10].id'" };
        var fail = new Option<bool>("--fail", "-f") { Description = "Exit with code 22 when the HTTP status is 400 or higher" };
        var curl = new Option<bool>("--curl") { Description = "Print the request as a cURL command instead of sending it (scripts do not run, no OAuth 2.0 token is fetched)" };
        var noHistory = new Option<bool>("--no-history") { Description = "Do not record this request in the workspace history" };
        var options = new RequestOptions();
        command.Arguments.Add(collection);
        command.Arguments.Add(request);
        options.AddTo(command);
        command.Options.Add(include);
        command.Options.Add(verbose);
        command.Options.Add(output);
        command.Options.Add(raw);
        command.Options.Add(filter);
        command.Options.Add(fail);
        command.Options.Add(curl);
        command.Options.Add(noHistory);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // A query that is not valid JSONPath is reported before anything is sent.
            JsonPath? query = null;
            if (parseResult.GetValue(filter) is { } text)
            {
                try
                {
                    query = JsonPath.Parse(text.TrimEnd());
                }
                catch (JsonPathException ex)
                {
                    Output.Error($"--filter: {ex.Message}");
                    return 2;
                }
            }

            if (options.Open(parseResult, parseResult.GetValue(collection)!, insecure) is not { } opened)
            {
                return 2;
            }

            using var runner = opened.Runner;
            if (RequestOptions.ReportUnreadableSettings(opened.Workspace))
            {
                return 1;
            }

            if (!parseResult.GetValue(noHistory))
            {
                runner.ConfigureHistory();
            }
            var requestPath = parseResult.GetValue(request)!;
            var candidates = opened.Collection is { } single ? [single] : opened.Workspace.Collections;
            var matches = FindRequests(candidates, requestPath);
            if (matches.Count == 0)
            {
                Output.Error($"Request '{requestPath}' was not found. Available:\n  " +
                             string.Join("\n  ", candidates.SelectMany(c => c.AllRequests().Select(r => r.PathInCollection))));
                return 2;
            }

            if (matches.Count > 1)
            {
                Output.Error($"Request '{requestPath}' is ambiguous: it matches\n  " +
                             string.Join("\n  ", matches.Select(m => $"{m.Node.PathInCollection}  ({Path.GetFileName(m.Collection.FilePath) ?? m.Collection.Name})")) +
                             (matches.DistinctBy(m => m.Collection).Count() > 1 ? "\nGive its path (Folder/Name), or its collection rather than the workspace." : "\nGive its path (Folder/Name)."));
                return 2;
            }

            var match = matches[0];
            var spec = match.Node.ReadSpec();
            if (parseResult.GetValue(curl))
            {
                // Nothing is sent, so no OAuth 2.0 token is fetched: a warning says the command has none.
                var prepared = runner.Prepare(match.Collection, match.Node, spec);
                runner.AddCachedToken(prepared);
                PrintWarnings(prepared);
                if (prepared.Errors.Count > 0)
                {
                    // Not a request TermRest would send (an invalid method or URL, a missing file): no command for it.
                    foreach (var error in prepared.Errors)
                    {
                        Output.Error(error);
                    }

                    return 2;
                }

                string command;
                try
                {
                    command = CurlExporter.Export(prepared);
                }
                catch (NotSupportedException ex)
                {
                    Output.Error(ex.Message);
                    return 2;
                }

                Console.Out.WriteLine(command);
                return RequestOptions.ReportUnreadableFiles(match.Collection) ? 1 : 0;
            }

            if (!options.StartRecording(parseResult, opened.Workspace, runner, out var recorder))
            {
                return 2;
            }

            var printed = 0;
            var exchange = await runner.SendAsync(match.Collection, match.Node, spec, prepared =>
            {
                PrintWarnings(prepared);
                printed = prepared.Warnings.Count;
            }, cancellationToken);
            var recorded = options.SaveRecording(parseResult, runner, recorder, cancellationToken.IsCancellationRequested);
            var code = await ReportAsync(parseResult, exchange, query, verbose, include, output, raw, fail, cancellationToken);

            // Those raised while it was sent: a redirect not followed, a header left out, a body cut short …
            PrintWarnings(exchange.Request, printed);
            ScriptOutput.Print(exchange);
            if (code == 0 && exchange.Scripts is { Succeeded: false })
            {
                code = 1; // a test failed or a script did not run
            }

            if (RequestOptions.ReportUnreadableFiles(match.Collection) && code == 0)
            {
                code = 1;
            }

            return recorded || code != 0 ? code : 1;
        });

        return command;
    }

    private static void PrintWarnings(PreparedRequest prepared, int from = 0)
    {
        foreach (var warning in prepared.Warnings.Skip(from))
        {
            Output.Warning(warning);
        }
    }

    /// <summary>
    /// The requests <paramref name="pathOrName"/> stands for, found as <see cref="PostmanCollection.FindRequest"/> finds
    /// one (by path, else by name), but in all <paramref name="collections"/>: a request that is not unique is reported
    /// rather than taken for another.
    /// </summary>
    private static List<(PostmanCollection Collection, RequestNode Node)> FindRequests(IEnumerable<PostmanCollection> collections, string pathOrName)
    {
        var requests = collections.SelectMany(c => c.AllRequests().Select(r => (Collection: c, Node: r))).ToList();
        string[] forms = [pathOrName, pathOrName.Trim('/')];
        Func<RequestNode, string>[] keys = [r => r.PathInCollection, r => r.Name];
        foreach (var key in keys)
        {
            foreach (var form in forms)
            {
                var found = requests.Where(r => string.Equals(key(r.Node), form, StringComparison.OrdinalIgnoreCase)).ToList();
                if (found.Count > 0)
                {
                    return found;
                }
            }
        }

        return [];
    }

    private static async Task<int> ReportAsync(
        ParseResult parseResult, HttpExchange exchange, JsonPath? query, Option<bool> verbose, Option<bool> include, Option<string?> output, Option<bool> raw,
        Option<bool> fail, CancellationToken cancellationToken)
    {
        if (parseResult.GetValue(verbose))
        {
            PrintVerbose(exchange);
        }

        if (exchange.Response is not { } response)
        {
            Output.Error(exchange.ErrorMessage ?? "Request failed.");
            return exchange.Cancelled ? 130 : 1;
        }

        Output.Status($"{response.StatusCode} {response.ReasonPhrase} · {HttpExchange.Format(exchange.Timing.Total)} · {HttpExchange.FormatSize(response.Body.LongLength)}", response.StatusCode < 400);
        if (parseResult.GetValue(include))
        {
            Console.Out.WriteLine($"HTTP/{response.Version} {response.StatusCode} {response.ReasonPhrase}");
            foreach (var (key, value) in response.Headers)
            {
                Console.Out.WriteLine($"{key}: {value}");
            }

            Console.Out.WriteLine();
        }

        if (query is not null)
        {
            if (await WriteFilteredAsync(query, response, parseResult.GetValue(raw), parseResult.GetValue(output), cancellationToken) is { } failed)
            {
                return failed;
            }
        }
        else if (parseResult.GetValue(output) is { } file)
        {
            try
            {
                await File.WriteAllBytesAsync(file, response.Body, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Output.Error($"The body could not be saved to {file}: {ex.Message}");
                return 1;
            }

            Output.Info($"Saved {HttpExchange.FormatSize(response.Body.LongLength)} to {file}");
        }
        else if (Console.IsOutputRedirected
                     ? parseResult.GetValue(raw) || BodyFormatter.IsBinary(response.Body, response.ContentType)
                     : parseResult.GetValue(raw) && !BodyFormatter.LooksLikeText(response.Body, response.ContentType))
        {
            // The bytes as received. A pipe or file gets them for binary content (an image, a protobuf message: its declared
            // type says so, it need not have a NUL byte) and for any body with --raw; a terminal shows binary content as a
            // hex dump unless --raw asks for the bytes.
            await using var stdout = Console.OpenStandardOutput();
            await stdout.WriteAsync(response.Body, cancellationToken);
        }
        else
        {
            Console.Out.WriteLine(BodyFormatter.Format(response.Body, response.ContentType, pretty: !parseResult.GetValue(raw)));
        }

        if (parseResult.GetValue(fail) && response.StatusCode >= 400)
        {
            return 22;
        }

        // Only the start of the body was received (a warning says where it was cut): exit as curl does when --max-filesize
        // stops a transfer, so that a script does not take what was written for the whole body.
        return response.BodyTruncated ? 63 : 0;
    }

    /// <summary>The values <paramref name="query"/> selects in the body, printed or saved; the exit code when they cannot be.</summary>
    private static async Task<int?> WriteFilteredAsync(JsonPath query, ResponseSnapshot response, bool compact, string? file, CancellationToken cancellationToken)
    {
        FilteredBody filtered;
        try
        {
            filtered = BodyFilter.Apply(query, response.Body, response.ContentType, indented: !compact, cancellationToken: cancellationToken);
        }
        catch (JsonPathException ex)
        {
            Output.Error($"--filter: {ex.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }

        if (file is null)
        {
            Console.Out.WriteLine(filtered.Text);
            return null;
        }

        try
        {
            await File.WriteAllTextAsync(file, filtered.Text + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Output.Error($"The values could not be saved to {file}: {ex.Message}");
            return 1;
        }

        Output.Info($"Saved {filtered.Count} value{(filtered.Count == 1 ? "" : "s")} to {file}");
        return null;
    }

    private static void PrintVerbose(HttpExchange exchange)
    {
        var request = exchange.Request;
        Output.Info($"> {request.Method} {request.Url}");
        foreach (var (key, value) in exchange.SentHeaders.Count > 0 ? exchange.SentHeaders : request.Headers)
        {
            Output.Info($"> {key}: {value}");
        }

        Output.Info($"* Auth: {request.AuthDescription}");
        Output.Info($"* Client certificate: {(request.ClientCertificate is { } c ? $"{c.Entry.Name} — {c.Summary} ({c.Origin})" : "none")}");
        foreach (var line in exchange.Timing.ToString().Split('\n'))
        {
            Output.Info("* " + line);
        }

        foreach (var tls in exchange.Tls)
        {
            foreach (var line in tls.Describe().Split('\n'))
            {
                Output.Info("* " + line);
            }
        }
    }
}
