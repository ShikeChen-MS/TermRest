using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using TermRest.Core.Scripting;
using TermRest.Core.Tests.Infrastructure;
using TermRest.Core.Workspace;
using TermRest.Tui.Dialogs;
using Xunit;

namespace TermRest.Tui.Tests;

/// <summary>Tools › Scripts, driven headlessly through Terminal.Gui's ANSI driver as a user would.</summary>
[Collection("TUI")]
public sealed class ScriptsDialogTests
{
    [Fact]
    public void A_runtime_chosen_with_browse_in_the_workspace_folder_is_that_file_not_a_command_on_path()
    {
        var directory = TestFiles.NewTempDirectory();
        var settings = TestFiles.NewTempDirectory();
        var name = "python-" + Guid.NewGuid().ToString("N")[..8] + (OperatingSystem.IsWindows() ? ".cmd" : "");
        var runtime = Path.Combine(directory, name);
        File.WriteAllText(runtime, OperatingSystem.IsWindows() ? "@echo Python 3.99.0\r\n@echo %~f0\r\n" : "#!/bin/sh\necho Python 3.99.0\necho \"$0\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        TuiApp.ConfigureKeyBindings();
        var workspace = ApiWorkspace.Open(directory);
        using var runner = new RequestRunner(workspace) { RequireScriptRuntimes = false };
        using var app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(150, 45);
        using var window = TuiApp.CreateWindow(workspace, runner);
        string Screen()
        {
            app.LayoutAndDraw(forceRedraw: true);
            return app.Driver!.ToString() ?? "";
        }

        void Click(string text)
        {
            var lines = Screen().Split('\n');
            var y = Array.FindIndex(lines, line => line.Contains(text, StringComparison.Ordinal));
            Assert.True(y >= 0, $"'{text}' is not on screen:\n{string.Join('\n', lines)}");
            app.InjectSequence(InputInjectionExtensions.LeftButtonClick(new Point(lines[y].IndexOf(text, StringComparison.Ordinal), y)));
        }

        // The file picker opens in the workspace's folder, where the user picks the file: types its name, presses Enter.
        var checkedRuntime = "";
        (Func<string, bool> Ready, Action<string> Act)[] steps =
        [
            (screen => screen.Contains("Stop scripts", StringComparison.Ordinal), _ => Click("Browse…")),
            (screen => screen.Contains("Choose Python", StringComparison.Ordinal), _ =>
            {
                app.InjectKey(Key.End);
                foreach (var c in name)
                {
                    app.InjectKey(new Key(c));
                }

                app.InjectKey(Key.Enter);
            }),
            (screen => screen.Contains("✓ ", StringComparison.Ordinal) || screen.Contains("✗ ", StringComparison.Ordinal), screen =>
            {
                checkedRuntime = screen;
                Click("► Save ◄");
            }),
        ];
        var deadline = DateTime.UtcNow.AddSeconds(30);

        // Each step waits for its screen, then acts; the next one is scheduled first, as an action that opens a dialog returns
        // only once that is closed.
        void Next(int index)
        {
            if (index < steps.Length)
            {
                app.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
                {
                    var screen = Screen();
                    if (app.TopRunnable is { StopRequested: true } || !steps[index].Ready(screen))
                    {
                        return DateTime.UtcNow < deadline;
                    }

                    Next(index + 1);
                    steps[index].Act(screen);
                    return false;
                });
            }
        }

        // After 30 s, whatever is open is closed, so that the test fails instead of hanging.
        var done = false;
        app.AddTimeout(TimeSpan.FromSeconds(1), () =>
        {
            if (!done && DateTime.UtcNow > deadline)
            {
                app.RequestStop();
            }

            return !done;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            Next(0);
            ScriptsDialog.ShowRuntimes(app, workspace, runner, settings);
            done = true;
            app.RequestStop();
            return false;
        });

        app.Run(window);

        // The file was checked (no Python on PATH is 3.99.0), and is what scripts run.
        var chosen = "." + Path.DirectorySeparatorChar + name;
        Assert.True(checkedRuntime.Contains("✓ Python 3.99.0 · ", StringComparison.Ordinal), checkedRuntime);
        Assert.Contains(chosen, checkedRuntime, StringComparison.Ordinal);
        Assert.Equal(chosen, UserSettings.Load(settings).GetScriptRuntime(ScriptLanguage.Python, workspace.RootPath));
        Assert.Equal(runtime, runner.PythonInterpreter);
    }
}
