using System.CommandLine;
using TermRest.Core.Http;

namespace TermRest.Cli;

/// <summary><c>termrest send &lt;collection&gt; &lt;request&gt;</c>: send one saved request and print the response.</summary>
internal static class SendCommand
{
    public static Command Create(Option<bool> insecure)
    {
        var command = new Command("send", "Send one request from a collection and print the response body (status and diagnostics go to stderr).");
        var collection = new Argument<string>("collection") { Description = "Collection file, or a workspace folder" };
        var request = new Argument<string>("request") { Description = "Request path such as \"Users/Get user\", or a unique request name" };
        var include = new Option<bool>("--include", "-i") { Description = "Also print the response status line and headers to stdout" };
        var verbose = new Option<bool>("--verbose", "-v") { Description = "Print the request, timing and TLS details to stderr" };
        var output = new Option<string?>("--output", "-o") { Description = "Write the raw response body to a file" };
        var raw = new Option<bool>("--raw") { Description = "Print the body as received (no JSON/XML pretty printing)" };
        var fail = new Option<bool>("--fail", "-f") { Description = "Exit with code 22 when the HTTP status is 400 or higher" };
        var curl = new Option<bool>("--curl") { Description = "Print the request as a cURL command instead of sending it" };
        var options = new RequestOptions();
        command.Arguments.Add(collection);
        command.Arguments.Add(request);
        options.AddTo(command);
        command.Options.Add(include);
        command.Options.Add(verbose);
        command.Options.Add(output);
        command.Options.Add(raw);
        command.Options.Add(fail);
        command.Options.Add(curl);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            if (options.Open(parseResult, parseResult.GetValue(collection)!, insecure) is not { } opened)
            {
                return 2;
            }

            using var runner = opened.Runner;
            var requestPath = parseResult.GetValue(request)!;
            var candidates = opened.Collection is { } single ? [single] : opened.Workspace.Collections;
            var match = candidates.Select(c => (Collection: c, Node: c.FindRequest(requestPath))).FirstOrDefault(m => m.Node is not null);
            if (match.Node is null)
            {
                Output.Error($"Request '{requestPath}' was not found. Available:\n  " +
                             string.Join("\n  ", candidates.SelectMany(c => c.AllRequests().Select(r => r.PathInCollection))));
                return 2;
            }

            var prepared = runner.Prepare(match.Collection, match.Node, match.Node!.ReadSpec());
            foreach (var warning in prepared.Warnings)
            {
                Output.Warning(warning);
            }

            if (parseResult.GetValue(curl))
            {
                Console.Out.WriteLine(CurlExporter.Export(prepared));
                return 0;
            }

            var exchange = await runner.SendAsync(prepared, cancellationToken);
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

            if (parseResult.GetValue(output) is { } file)
            {
                await File.WriteAllBytesAsync(file, response.Body, cancellationToken);
                Output.Info($"Saved {HttpExchange.FormatSize(response.Body.LongLength)} to {file}");
            }
            else
            {
                Console.Out.WriteLine(BodyFormatter.Format(response.Body, response.ContentType, pretty: !parseResult.GetValue(raw)));
            }

            return parseResult.GetValue(fail) && response.StatusCode >= 400 ? 22 : 0;
        });

        return command;
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
