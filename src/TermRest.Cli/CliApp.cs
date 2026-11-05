using System.CommandLine;
using TermRest.Tui;

namespace TermRest.Cli;

/// <summary>Command-line surface: the TUI by default, plus headless commands for scripts and CI.</summary>
internal static class CliApp
{
    public static async Task<int> RunAsync(string[] args)
    {
        var insecure = new Option<bool>("--insecure", "-k") { Description = "Do not verify server TLS certificates", Recursive = true };

        var root = new RootCommand(
            "TermRest — a terminal REST client with Postman-compatible collections and client-certificate (mTLS) support.\n" +
            "Run without a command to open the full-screen UI in the given folder (default: current directory).");
        var workspace = new Argument<string?>("workspace") { Description = "Workspace folder or Postman collection file", Arity = ArgumentArity.ZeroOrOne };
        var driver = new Option<string?>("--driver") { Description = "Terminal driver: windows, unix, dotnet or ansi (default: best for this OS)" };
        root.Arguments.Add(workspace);
        root.Options.Add(driver);
        root.Options.Add(insecure);
        root.SetAction(parseResult =>
        {
            var path = parseResult.GetValue(workspace) ?? Environment.CurrentDirectory;
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Console.Error.WriteLine($"'{path}' does not exist.");
                return 2;
            }

            return TuiApp.Run(path, new TuiOptions
            {
                Driver = parseResult.GetValue(driver),
                StrictSslOverride = parseResult.GetValue(insecure) ? false : null,
            });
        });

        root.Subcommands.Add(SendCommand.Create(insecure));
        root.Subcommands.Add(RunCommand.Create(insecure));
        root.Subcommands.Add(ValidateCommand.Create());
        root.Subcommands.Add(CertsCommand.Create());
        root.Subcommands.Add(CreateSnapshotCommand());

        return await root.Parse(args).InvokeAsync();
    }

    private static Command CreateSnapshotCommand()
    {
        var command = new Command("snapshot", "Render the UI once without a terminal and print it (smoke test for builds and CI).");
        var workspace = new Argument<string?>("workspace") { Description = "Workspace folder or collection file", Arity = ArgumentArity.ZeroOrOne };
        var width = new Option<int>("--width") { Description = "Screen width in columns", DefaultValueFactory = _ => 120 };
        var height = new Option<int>("--height") { Description = "Screen height in rows", DefaultValueFactory = _ => 36 };
        var request = new Option<string?>("--request") { Description = "Open this request (path or unique name) before rendering" };
        var send = new Option<bool>("--send") { Description = "Send the request and render the response too" };
        command.Arguments.Add(workspace);
        command.Options.Add(width);
        command.Options.Add(height);
        command.Options.Add(request);
        command.Options.Add(send);
        command.SetAction(parseResult =>
        {
            var screen = TuiApp.Snapshot(
                parseResult.GetValue(workspace) ?? Environment.CurrentDirectory,
                parseResult.GetValue(width),
                parseResult.GetValue(height),
                parseResult.GetValue(request),
                parseResult.GetValue(send));
            Console.Out.WriteLine(screen.TrimEnd());
            return 0;
        });
        return command;
    }
}
