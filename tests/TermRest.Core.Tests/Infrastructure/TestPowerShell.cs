using TermRest.Core.Scripting;
using Xunit;

namespace TermRest.Core.Tests.Infrastructure;

/// <summary>
/// Finds PowerShell (7 or later) for the PowerShell scripting tests: <c>$TERMREST_TEST_POWERSHELL</c>, else <c>pwsh</c>
/// on PATH, else where its installer puts it.
/// </summary>
internal static class TestPowerShell
{
    private static readonly Lazy<string?> Found = new(Find);

    /// <summary>The <c>pwsh</c> to use; skips the test when there is none.</summary>
    public static string Require()
    {
        var pwsh = Found.Value;
        if (pwsh is null)
        {
            Assert.Skip("PowerShell 7 or later was not found: set TERMREST_TEST_POWERSHELL, or put pwsh on PATH.");
        }

        return pwsh;
    }

    private static string? Find()
    {
        var candidates = ScriptRuntimeLocator.Candidates(ScriptLanguage.PowerShell, Path.GetTempPath()).ToList();
        if (Environment.GetEnvironmentVariable("TERMREST_TEST_POWERSHELL") is { Length: > 0 } configured)
        {
            candidates.Insert(0, configured);
        }

        return candidates.FirstOrDefault(candidate =>
            ScriptRuntimeLocator.CheckAsync(ScriptLanguage.PowerShell, candidate, Path.GetTempPath()).GetAwaiter().GetResult().Ok);
    }
}
