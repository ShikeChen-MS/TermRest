using System.Runtime.InteropServices;
using Terminal.Gui.App;
using Terminal.Gui.Input;

namespace TermRest.Tui;

/// <summary>
/// Asks Windows for 1 ms timer resolution while the UI is in use (see <see cref="UiPace"/>). Terminal.Gui paces its main
/// loop and its input polling with <see cref="Task.Delay(int)"/>, and Windows rounds such waits up to its default 15.6 ms
/// timer tick: the 20 ms input poll takes 31 ms and a 16 ms frame 27 ms, which makes the mouse feel sluggish. Since
/// Windows 10 2004 the request affects only this process. Other platforms have fine-grained timers already.
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

/// <summary>
/// Keeps the UI quick while it is in use and cheap while it is not: Terminal.Gui's main loop runs at
/// <see cref="TuiApp.IterationsPerSecond"/>, with 1 ms timer resolution (<see cref="TimerResolution"/>), from keyboard or
/// mouse input until <see cref="ActiveFor"/> after the last one, and while a request is on its way; otherwise at
/// Terminal.Gui's default rate (25 passes a second) and the system's timer resolution. The main loop reads the rate
/// before each pass, so a change applies from the next one. Once the UI is idle, after starting and after use, the memory
/// that what it did left behind is given back (see <see cref="Trim"/>).
/// </summary>
internal sealed class UiPace : IDisposable
{
    /// <summary>
    /// What must have been allocated since the last trim for the next one to run: below it there is little to give back,
    /// and idling costs no collection.
    /// </summary>
    private const long TrimAfter = 8 * 1024 * 1024;

    private readonly IApplication _app;
    private readonly Func<bool> _busy;
    private readonly ushort _idleRate = Application.MaximumIterationsPerSecond;
    private IDisposable? _timerResolution;
    private long _activeUntil;
    private long _trimAt; // when the UI, idle then, gives back memory (0: not due)
    private long _allocatedAtTrim;

    /// <param name="busy">Whether a request is on its way: the UI stays quick until its response is shown.</param>
    public UiPace(IApplication app, Func<bool>? busy = null)
    {
        _app = app;
        _busy = busy ?? (() => false);
        _trimAt = Environment.TickCount64 + 1000; // once the window is shown and the workspace read
        app.Keyboard.KeyDown += OnKeyDown;
        app.Mouse.MouseEvent += OnMouse;
        app.Iteration += OnIteration;
    }

    /// <summary>How long the UI stays quick after the last input (or the end of a request).</summary>
    public TimeSpan ActiveFor { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Whether the UI runs at full pace now.</summary>
    public bool IsActive => _timerResolution is not null;

    public void Dispose()
    {
        _app.Keyboard.KeyDown -= OnKeyDown;
        _app.Mouse.MouseEvent -= OnMouse;
        _app.Iteration -= OnIteration;
        Idle();
    }

    private void OnKeyDown(object? sender, Key key) => Wake();

    private void OnMouse(object? sender, Mouse mouse) => Wake();

    private void OnIteration(object? sender, EventArgs<IApplication?> e)
    {
        if (_timerResolution is null)
        {
            if (_trimAt != 0 && Environment.TickCount64 >= _trimAt && !_busy())
            {
                _trimAt = 0;
                Trim();
            }

            return;
        }

        if (_busy())
        {
            Wake();
        }
        else if (Environment.TickCount64 >= _activeUntil)
        {
            Idle();
            _trimAt = Environment.TickCount64; // the next pass, not when disposed
        }
    }

    private void Wake()
    {
        _activeUntil = Environment.TickCount64 + (long)ActiveFor.TotalMilliseconds;
        if (_timerResolution is null)
        {
            _timerResolution = TimerResolution.Request();
            Application.MaximumIterationsPerSecond = TuiApp.IterationsPerSecond;
        }
    }

    private void Idle()
    {
        if (_timerResolution is not null)
        {
            Application.MaximumIterationsPerSecond = _idleRate;
            _timerResolution.Dispose();
            _timerResolution = null;
        }
    }

    /// <summary>
    /// Gives back the memory the UI is not using: what it allocated meanwhile (reading the workspace, drawing, responses
    /// shown) is garbage now, which the GC would keep until it next runs, as long as the UI stays idle. A full, compacting
    /// collection takes a few milliseconds while nothing happens.
    /// </summary>
    private void Trim()
    {
        var allocated = GC.GetTotalAllocatedBytes();
        if (allocated - _allocatedAtTrim < TrimAfter)
        {
            return;
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        _allocatedAtTrim = GC.GetTotalAllocatedBytes();
    }
}
