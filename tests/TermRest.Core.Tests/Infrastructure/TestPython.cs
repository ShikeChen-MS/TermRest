using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace TermRest.Core.Tests.Infrastructure;

/// <summary>Finds a Python for the scripting tests: <c>$TERMREST_TEST_PYTHON</c>, else <c>python3</c> or <c>python</c> on PATH.</summary>
internal static class TestPython
{
    private static readonly Lazy<string?> Found = new(Find);

    /// <summary>The Python to use; skips the test when there is none.</summary>
    public static string Require()
    {
        var python = Found.Value;
        if (python is null)
        {
            Assert.Skip("Python 3.8+ was not found: set TERMREST_TEST_PYTHON, or put python3 or python on PATH.");
        }

        return python;
    }

    private static string? Find()
    {
        List<string> candidates = ["python3", "python"];
        if (Environment.GetEnvironmentVariable("TERMREST_TEST_PYTHON") is { Length: > 0 } configured)
        {
            candidates.Insert(0, configured);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var info = new ProcessStartInfo(candidate, ["-c", "import sys; print(sys.version_info >= (3, 8))"])
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

                if (process.ExitCode == 0 && output.Result.Trim() == "True")
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
