using System.Runtime.InteropServices;

namespace TermRest.Tui;

/// <summary>
/// Asks Windows for 1 ms timer resolution while the UI runs. Terminal.Gui paces its main loop and its input polling
/// with <see cref="Task.Delay(int)"/>, and Windows rounds such waits up to its default 15.6 ms timer tick: the 20 ms
/// input poll takes 31 ms and a 16 ms frame 27 ms, which makes the mouse feel sluggish. Since Windows 10 2004 the
/// request affects only this process. Other platforms have fine-grained timers already.
/// </summary>
internal static partial class TimerResolution
{
    private const uint Period = 1;

    /// <summary>Requests the finer resolution until the returned object is disposed.</summary>
    public static IDisposable Request()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (TimeBeginPeriod(Period) == 0)
                {
                    return new Lease();
                }
            }
            catch (DllNotFoundException)
            {
                // Windows editions without multimedia timers keep the default resolution.
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        return new NoLease();
    }

    private sealed class Lease : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _ = TimeEndPeriod(Period);
            }
        }
    }

    private sealed class NoLease : IDisposable
    {
        public void Dispose()
        {
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint TimeEndPeriod(uint period);
}
