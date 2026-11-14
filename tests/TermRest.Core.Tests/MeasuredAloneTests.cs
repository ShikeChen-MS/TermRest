using System.Text.Json.Nodes;
using TermRest.Core.Scripting;
using TermRest.Core.Tests.Infrastructure;
using Xunit;

namespace TermRest.Core.Tests;

/// <summary>Tests that measure what the whole process allocates run when no other test runs.</summary>
[CollectionDefinition("Measured alone", DisableParallelization = true)]
public sealed class MeasuredAloneGroup;

[Collection("Measured alone")]
public sealed class ScriptOutputMemoryTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task TermRest_keeps_only_the_end_of_what_programs_that_scripts_start_print_without_a_newline()
    {
        var python = TestPython.Require();
        using var worker = ScriptWorker.For(ScriptLanguage.Python, python, TestFiles.NewTempDirectory());

        // A program that a script starts (curl -s …) prints 64 MB without a newline to Python's stderr; then Python stops.
        var message = new JsonObject
        {
            ["phase"] = "test",
            ["scripts"] = new JsonArray(new JsonObject
            {
                ["name"] = "request",
                ["code"] = """
                    import os, subprocess, sys
                    subprocess.run([sys.executable, "-c", "import sys\nfor _ in range(64): sys.stdout.buffer.write(b'x' * (1 << 20))"])
                    os.write(2, b"\r\nlast words\r\n")
                    os._exit(3)
                    """,
            }),
        };

        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var stopped = await Assert.ThrowsAsync<ScriptException>(() => worker.RunAsync(message, TimeSpan.FromSeconds(60), cancellationToken: _ct));
        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;

        // Its last 4,000 characters are kept, with why Python stopped; the 64 MB were never in memory.
        Assert.StartsWith("Python stopped.\nxxx", stopped.Message, StringComparison.Ordinal);
        Assert.EndsWith("x\nlast words", stopped.Message, StringComparison.Ordinal);
        Assert.Equal("Python stopped.\n".Length + 3999, stopped.Message.Length);
        Assert.True(allocated < 64 << 20, $"{allocated:N0} bytes were allocated");
    }
}
