using System.CommandLine;
using System.CommandLine.Invocation;
using System.Runtime.InteropServices;
using TermRest.Tui;

namespace TermRest.Cli;

/// <summary>Command-line surface: the TUI by default, plus headless commands for scripts and CI.</summary>
internal static class CliApp
{
    /// <summary>Cancelled by the first Ctrl+C or SIGTERM while <c>send</c> or <c>run</c> works (see <see cref="Stop"/>).</summary>
    private static readonly CancellationTokenSource Stopping = new();

    public static async Task<int> RunAsync(string[] args)
    {
        var insecure = new Option<bool>("--insecure", "-k") { Description = "Do not verify server TLS certificates", Recursive = true };

        var root = new RootCommand(
            "TermRest — a terminal REST client with Postman-compatible collections and client-certificate (mTLS) support.\n" +
            "Run without a command to open the full-screen UI in the given folder (default: current directory).");
        var workspace = new Argument<string?>("workspace") { Description = "Workspace folder or Postman collection file", Arity = ArgumentArity.ZeroOrOne };
        var driver = new Option<string?>("--driver") { Description = "Terminal driver: windows, unix, dotnet or ansi (default: best for this OS)" };
        var theme = new Option<string?>("--theme") { Description = "Color theme for this session (see 'termrest themes')" };
        root.Arguments.Add(workspace);
        root.Options.Add(driver);
        root.Options.Add(theme);
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
                Theme = parseResult.GetValue(theme),
            });
        });

        root.Subcommands.Add(SendCommand.Create(insecure));
        root.Subcommands.Add(RunCommand.Create(insecure));
        root.Subcommands.Add(ImportCurlCommand.Create());
        root.Subcommands.Add(HistoryCommand.Create());
        root.Subcommands.Add(ConvertCommand.Create());
        root.Subcommands.Add(ValidateCommand.Create());
        root.Subcommands.Add(CertsCommand.Create());
        root.Subcommands.Add(ThemesCommand.Create());
        root.Subcommands.Add(CreateSnapshotCommand());

        var parseResult = root.Parse(args);
        if (parseResult.Action != root.Action)
        {
            ConsoleText.UseUnicode(); // a command (the full-screen UI drives the console itself)
        }

        if (parseResult.Action is not AsynchronousCommandLineAction)
        {
            return await parseResult.InvokeAsync();
        }

        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
        return await parseResult.InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = null }, Stopping.Token);
    }

    /// <summary>
    /// The first Ctrl+C (or SIGTERM: a cancelled CI job) stops <c>send</c> and <c>run</c>, which still save their recording
    /// and write their report, however long that takes (System.CommandLine would end the process 2 seconds after the
    /// signal); another one ends TermRest at once.
    /// </summary>
    private static void Stop(PosixSignalContext context)
    {
        context.Cancel = !Stopping.IsCancellationRequested;
        Stopping.Cancel();
    }

    private static Command CreateSnapshotCommand()
    {
        var command = new Command("snapshot", "Render the UI once without a terminal and print it (smoke test for builds and CI).");
        var workspace = new Argument<string?>("workspace") { Description = "Workspace folder or collection file", Arity = ArgumentArity.ZeroOrOne };
        var width = new Option<int>("--width") { Description = "Screen width in columns", DefaultValueFactory = _ => 120 };
        var height = new Option<int>("--height") { Description = "Screen height in rows", DefaultValueFactory = _ => 36 };
        var request = new Option<string?>("--request") { Description = "Open this request (path or unique name) before rendering" };
        var send = new Option<bool>("--send") { Description = "Send the request and render the response too" };
        var theme = new Option<string?>("--theme") { Description = "Color theme (checks that it loads; the text output has no colors)" };
        command.Arguments.Add(workspace);
        command.Options.Add(width);
        command.Options.Add(height);
        command.Options.Add(request);
        command.Options.Add(send);
        command.Options.Add(theme);
        command.SetAction(parseResult =>
        {
            string screen;
            try
            {
                screen = TuiApp.Snapshot(
                    parseResult.GetValue(workspace) ?? Environment.CurrentDirectory,
                    parseResult.GetValue(width),
                    parseResult.GetValue(height),
                    parseResult.GetValue(request),
                    parseResult.GetValue(send),
                    parseResult.GetValue(theme));
            }
            catch (ArgumentException ex)
            {
                Output.Error(ex.Message.Split(" (Parameter", StringSplitOptions.None)[0]);
                return 2;
            }

            Console.Out.WriteLine(screen.TrimEnd());
            return 0;
        });
        return command;
    }
}
