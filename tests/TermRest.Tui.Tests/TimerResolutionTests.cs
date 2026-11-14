using System.Diagnostics;
using Xunit;

namespace TermRest.Tui.Tests;

public sealed class TimerResolutionTests
{
    [Fact]
    public void Finer_timer_resolution_is_requested_while_the_lease_is_held()
    {
        var lease = TimerResolution.Request();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // With Windows' default 15.6 ms timer tick, ten 1 ms sleeps take at least 140 ms (each waits for the next
                // tick), every time; with 1 ms resolution about 15 ms. The best of ten tries, below 120 ms, so that a busy
                // machine (tests and builds running in parallel) does not fail it, while the default tick still does.
                var fastest = long.MaxValue;
                for (var attempt = 0; attempt < 10 && fastest >= 120; attempt++)
                {
                    var clock = Stopwatch.StartNew();
                    for (var i = 0; i < 10; i++)
                    {
                        Thread.Sleep(1);
                    }

                    fastest = Math.Min(fastest, clock.ElapsedMilliseconds);
                }

                Assert.True(fastest < 120, $"10 × Sleep(1) took {fastest} ms");
            }
        }
        finally
        {
            lease.Dispose();
        }

        lease.Dispose(); // releasing twice is harmless
    }
}
