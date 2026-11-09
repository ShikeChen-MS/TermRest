using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace TermRest.Core.Tests.Infrastructure;

/// <summary>Finds a JavaScript runtime for the scripting tests: <c>$TERMREST_TEST_JAVASCRIPT</c>, else <c>node</c> or <c>bun</c> on PATH.</summary>
internal static class TestJavaScript
{
    private static readonly Lazy<string?> Found = new(Find);

    /// <summary>The runtime to use; skips the test when there is none.</summary>
    public static string Require()
    {
        var runtime = Found.Value;
        if (runtime is null)
        {
            Assert.Skip("Node.js 18+ or Bun was not found: set TERMREST_TEST_JAVASCRIPT, or put node or bun on PATH.");
        }

        return runtime;
    }

    private static string? Find()
    {
        List<string> candidates = ["node", "bun"];
        if (Environment.GetEnvironmentVariable("TERMREST_TEST_JAVASCRIPT") is { Length: > 0 } configured)
        {
            candidates.Insert(0, configured);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var info = new ProcessStartInfo(candidate, ["-e", "console.log(typeof fetch === 'function' && typeof structuredClone === 'function')"])
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(info);
                if (process is null)
                {
                    continue;
                }

                var output = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(20_000))
                {
                    process.Kill(entireProcessTree: true);
                    continue;
                }

                if (process.ExitCode == 0 && output.Result.Trim() == "true")
                {
                    return candidate;
                }
            }
            catch (Win32Exception)
            {
                // not installed
            }
        }

        return null;
    }
}
