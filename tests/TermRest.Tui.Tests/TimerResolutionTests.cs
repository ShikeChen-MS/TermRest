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
                // With Windows' default 15.6 ms timer tick, ten 1 ms sleeps take about 156 ms.
                var clock = Stopwatch.StartNew();
                for (var i = 0; i < 10; i++)
                {
                    Thread.Sleep(1);
                }

                Assert.True(clock.ElapsedMilliseconds < 100, $"10 × Sleep(1) took {clock.ElapsedMilliseconds} ms");
            }
        }
        finally
        {
            lease.Dispose();
        }

        lease.Dispose(); // releasing twice is harmless
    }
}
