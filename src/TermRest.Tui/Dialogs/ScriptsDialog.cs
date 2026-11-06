using System.Globalization;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Scripting;
using TermRest.Core.Workspace;
using TermRest.Tui.Theming;
using TermRest.Tui.Views;

namespace TermRest.Tui.Dialogs;

/// <summary>Scripts of a collection or folder, and the Python that runs scripts (Tools › Scripts).</summary>
internal static class ScriptsDialog
{
    /// <summary>Edits the scripts that run around every request in a collection or folder; null when cancelled.</summary>
    public static ScriptSet? Edit(IApplication app, string owner, ScriptSet scripts)
    {
        using var dialog = new Dialog { Title = $"Scripts of {owner}", Width = Dim.Percent(90), Height = Dim.Percent(80) };
        dialog.Add(new Label { X = 1, Y = 0, Width = Dim.Fill(1), Text = "They run for every request inside, before the scripts of its folders and of the request itself." });
        var editor = new ScriptsEditor { X = 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(1) };
        editor.Load(scripts);
        dialog.Add(editor);
        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "OK" });
        editor.FocusPreRequest();
        app.Run(dialog);
        return dialog.Canceled ? null : editor.Scripts;
    }

    /// <summary>
    /// Chooses the Python that runs this workspace's scripts (kept in the user's settings, not in the workspace), and
    /// shows what scripts left behind: variables set in this session, and whether Python is running.
    /// </summary>
    public static void ShowPython(IApplication app, ApiWorkspace workspace, RequestRunner runner, string? userDirectory = null)
    {
        using var dialog = new Dialog { Title = "Scripts", Width = Dim.Percent(80) };
        var y = 1;
        var current = UserSettings.Load(userDirectory).GetPythonInterpreter(workspace.RootPath) ?? "";
        var python = Ui.AddRow(dialog, "Python", new TextField { Text = current, Width = Dim.Fill(13) }, y, 12);
        var browse = new Button { Text = "Browse…", X = Pos.AnchorEnd(12), Y = y };
        dialog.Add(browse);
        y += 1;
        dialog.Add(new Label
        {
            X = 14,
            Y = y,
            Width = Dim.Fill(1),
            Height = 2,
            Text = "A command (python3) or a path, relative to the workspace (.venv/bin/python): its packages are yours to use.\n"
                   + "Kept in your settings for this workspace only, not in the workspace.",
        });
        y += 3;
        var find = new Button { Text = "Find", X = 14, Y = y };
        var check = new Button { Text = "Check", X = Pos.Right(find) + 1, Y = y };
        var status = new Label { X = Pos.Right(check) + 2, Y = y, Width = Dim.Fill(1), Text = current.Length == 0 ? "No Python chosen: scripts do not run." : "" };
        dialog.Add(find, check, status);
        y += 2;
        dialog.Add(new Label
        {
            X = 1,
            Y = y,
            Width = Dim.Fill(1),
            Text = "⚠ Scripts run as you, with your permissions: choose Python only for workspaces whose collections you trust.",
            SchemeName = ThemeCatalog.WarningScheme,
        });
        y += 2;
        var session = new Label { X = 14, Y = y, Width = Dim.Fill(22) };
        var clear = new Button { Text = "Clear values", X = Pos.AnchorEnd(18), Y = y };
        dialog.Add(new Label { Text = "This session", X = 1, Y = y }, session, clear);
        y += 1;
        var running = new Label { X = 14, Y = y, Width = Dim.Fill(22) };
        var stop = new Button { Text = "Stop Python", X = Pos.AnchorEnd(18), Y = y };
        dialog.Add(running, stop);

        void Refresh()
        {
            var count = runner.Session.Count;
            session.Text = count == 0 ? "Scripts have set no variables." : $"{count} variable value(s) set by scripts (until TermRest closes).";
            running.Text = runner.ScriptsRunning
                ? $"Python is running; it stops after {runner.ScriptIdleTimeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} idle minutes."
                : "Python is not running (it starts for the next script).";
        }

        var checking = 0;
        void Run(Func<Task<string>> work)
        {
            var version = ++checking;
            status.Text = "Checking…";
            status.SchemeName = null;
            _ = Task.Run(work).ContinueWith(
                task => app.AddTimeout(TimeSpan.Zero, () =>
                {
                    if (version == checking)
                    {
                        status.Text = task.IsCompletedSuccessfully ? task.Result : task.Exception?.GetBaseException().Message ?? "";
                    }

                    return false;
                }),
                TaskScheduler.Default);
        }

        browse.Accepted += (_, _) =>
        {
            if (Ui.PickFile(app, "Choose the Python interpreter", workspace.RootPath) is { } path)
            {
                var relative = Path.GetRelativePath(workspace.RootPath, path);
                python.Text = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
                Run(() => Describe(runner, workspace, python.Text));
            }
        };
        check.Accepted += (_, _) =>
        {
            var text = python.Text.Trim();
            if (text.Length == 0)
            {
                status.Text = "No Python chosen: scripts do not run.";
                return;
            }

            Run(() => Describe(runner, workspace, text));
        };
        find.Accepted += (_, _) => Run(async () =>
        {
            if (await PythonLocator.FindAsync(workspace.RootPath, runner.ResolveInterpreter).ConfigureAwait(false) is not { } found)
            {
                return "No Python found in the workspace (.venv, venv, env) or on PATH.";
            }

            app.AddTimeout(TimeSpan.Zero, () =>
            {
                python.Text = found.Interpreter;
                return false;
            });
            return "✓ " + found.Description;
        });
        clear.Accepted += (_, _) =>
        {
            runner.Session.Clear();
            Refresh();
        };
        stop.Accepted += (_, _) =>
        {
            runner.StopScripts();
            Refresh();
        };
        Refresh();

        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "Save" });
        app.Run(dialog);
        if (dialog.Canceled)
        {
            return;
        }

        var user = UserSettings.Load(userDirectory);
        user.SetPythonInterpreter(workspace.RootPath, python.Text);
        user.Save(userDirectory);
        runner.SetPythonInterpreter(user.GetPythonInterpreter(workspace.RootPath));
    }

    private static async Task<string> Describe(RequestRunner runner, ApiWorkspace workspace, string interpreter)
    {
        var (ok, message) = await PythonLocator.CheckAsync(runner.ResolveInterpreter(interpreter), workspace.RootPath).ConfigureAwait(false);
        return (ok ? "✓ " : "✗ ") + message;
    }
}
