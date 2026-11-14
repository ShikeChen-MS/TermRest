using TermRest.Core.Scripting;
using Xunit;

namespace TermRest.Core.Tests.Infrastructure;

/// <summary>Finds a .NET SDK (10 or later) for the C# scripting tests: <c>$TERMREST_TEST_DOTNET</c>, else <c>dotnet</c> on PATH.</summary>
internal static class TestCSharp
{
    private static readonly Lazy<string?> Found = new(Find);

    /// <summary>The <c>dotnet</c> to use; skips the test when there is none.</summary>
    public static string Require()
    {
        var dotnet = Found.Value;
        if (dotnet is null)
        {
            Assert.Skip("The .NET SDK 10 or later was not found: set TERMREST_TEST_DOTNET, or put dotnet on PATH.");
        }

        return dotnet;
    }

    private static string? Find()
    {
        List<string> candidates = ["dotnet"];
        if (Environment.GetEnvironmentVariable("TERMREST_TEST_DOTNET") is { Length: > 0 } configured)
        {
            candidates.Insert(0, configured);
        }

        return candidates.FirstOrDefault(candidate =>
            ScriptRuntimeLocator.CheckAsync(ScriptLanguage.CSharp, candidate).GetAwaiter().GetResult().Ok);
    }
}
