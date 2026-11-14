using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TermRest.Core.Tests.Infrastructure;
using Xunit;

namespace TermRest.Cli.Tests.Infrastructure;

/// <summary>What a command did: its exit code, standard output (as bytes) and standard error.</summary>
internal sealed record CommandResult(int ExitCode, byte[] Output, string Error)
{
    public string Text => Encoding.UTF8.GetString(Output);

    public override string ToString() => $"exit code {ExitCode}\nstdout: {Text}\nstderr: {Error}";
}

/// <summary>
/// Runs the termrest built next to the tests as a separate process, as scripts and CI run it: standard output and
/// error are pipes. Each run gets its own user configuration and state folders, so it never touches the user's.
/// </summary>
internal static class Termrest
{
    private static readonly string Dotnet = Path.GetFullPath(Path.Combine(
        RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));

    /// <param name="environment">Extra environment variables; <c>TERMREST_CONFIG_DIR</c> replaces the run's own configuration folder.</param>
    /// <param name="input">Bytes for its standard input (closed right away when null).</param>
    /// <param name="whileRunning">What to do while the command runs (signal it…); its exit is awaited afterwards.</param>
    /// <param name="workingDirectory">The command's current folder (the tests' when null).</param>
    public static async Task<CommandResult> RunAsync(IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null, byte[]? input = null,
        Func<Process, Task>? whileRunning = null, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(Dotnet)
        {
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,

            // No console: on Windows, what the command writes does not depend on the code page of the console the tests
            // run in (UTF-8 in some terminals, which would hide text written in another one).
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "termrest.dll"));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["TERMREST_CONFIG_DIR"] = TestFiles.NewTempDirectory();
        start.Environment["TERMREST_STATE_DIR"] = TestFiles.NewTempDirectory();
        start.Environment["NO_COLOR"] = "1";
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{Dotnet} did not start.");
        if (input is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token);
        }

        process.StandardInput.Close();
        using var output = new MemoryStream();
        var copying = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            if (whileRunning is not null)
            {
                await whileRunning(process);
            }

            await process.WaitForExitAsync(timeout.Token);
            await copying;
            return new CommandResult(process.ExitCode, output.ToArray(), await error);
        }
        catch (Exception) when (!process.HasExited)
        {
            process.Kill(entireProcessTree: true); // timed out, or whileRunning failed
            throw;
        }
    }
}
