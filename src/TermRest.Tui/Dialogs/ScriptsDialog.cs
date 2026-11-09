using System.Globalization;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Scripting;
using TermRest.Core.Workspace;
using TermRest.Tui.Theming;
using TermRest.Tui.Views;

namespace TermRest.Tui.Dialogs;

/// <summary>Scripts of a collection or folder, and the runtimes that run scripts (Tools › Scripts).</summary>
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
    /// Chooses the runtimes that run this workspace's scripts, Python, Node.js or Bun, and the .NET SDK (kept in the
    /// user's settings, not in the workspace), and shows what scripts left behind: variables set in this session, and
    /// running runtimes.
    /// </summary>
    public static void ShowRuntimes(IApplication app, ApiWorkspace workspace, RequestRunner runner, string? userDirectory = null)
    {
        using var dialog = new Dialog { Title = "Scripts", Width = Dim.Percent(80) };
        var settings = UserSettings.Load(userDirectory);
        var y = 1;
        var fields = ScriptLanguage.All.ToDictionary(language => language, language => AddRuntime(app, dialog, workspace, runner, language, settings, ref y));
        dialog.Add(new Label
        {
            X = 14,
            Y = y,
            Width = Dim.Fill(1),
            Height = 3,
            Text = "A command (python3, node, bun, dotnet) or a path, relative to the workspace (.venv/bin/python).\n"
                   + "The packages installed for it (pip, npm; NuGet with #:package in C#) are yours to use.\n"
                   + "Kept in your settings, for this workspace only: never in the workspace.",
        });
        y += 4;
        dialog.Add(new Label
        {
            X = 1,
            Y = y,
            Width = Dim.Fill(1),
            Text = "⚠ Scripts run as you, with your permissions: choose runtimes only for workspaces whose collections you trust.",
            SchemeName = ThemeCatalog.WarningScheme,
        });
        y += 2;
        var session = new Label { X = 14, Y = y, Width = Dim.Fill(22) };
        var clear = new Button { Text = "Clear values", X = Pos.AnchorEnd(18), Y = y };
        dialog.Add(new Label { Text = "This session", X = 1, Y = y }, session, clear);
        y += 1;
        var running = new Label { X = 14, Y = y, Width = Dim.Fill(22) };
        var stop = new Button { Text = "Stop scripts", X = Pos.AnchorEnd(18), Y = y };
        dialog.Add(running, stop);

        void Refresh()
        {
            var count = runner.Session.Count;
            session.Text = count == 0 ? "Scripts have set no variables." : $"{count} variable value(s) set by scripts (until TermRest closes).";
            var names = runner.RunningRuntimes;
            running.Text = names.Count == 0
                ? "No runtime is running (each starts for its next script)."
                : $"Running: {string.Join(", ", names)}; each stops after {runner.ScriptIdleTimeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} idle minutes.";
        }

        Ui.OnPress(clear, () =>
        {
            runner.Session.Clear();
            Refresh();
        });
        Ui.OnPress(stop, () =>
        {
            runner.StopScripts(compilerServer: true);
            Refresh();
        });
        Refresh();

        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "Save" });
        app.Run(dialog);
        if (dialog.Canceled)
        {
            return;
        }

        var user = UserSettings.Load(userDirectory);
        foreach (var (language, field) in fields)
        {
            user.SetScriptRuntime(language, workspace.RootPath, field.Text);
        }

        user.Save(userDirectory);
        foreach (var language in ScriptLanguage.All)
        {
            runner.SetRuntime(language, user.GetScriptRuntime(language, workspace.RootPath));
        }
    }

    /// <summary>A runtime's row: its path with Browse…, then Find and Check with what they found.</summary>
    private static TextField AddRuntime(IApplication app, Dialog dialog, ApiWorkspace workspace, RequestRunner runner, string language,
        UserSettings settings, ref int y)
    {
        var name = language switch
        {
            ScriptLanguage.Python => "Python",
            ScriptLanguage.CSharp => "dotnet (.NET SDK 10 or later)",
            _ => "Node.js or Bun",
        };
        var current = settings.GetScriptRuntime(language, workspace.RootPath) ?? "";
        var field = Ui.AddRow(dialog, ScriptLanguage.Label(language), new TextField { Text = current, Width = Dim.Fill(13) }, y, 12);
        var browse = new Button { Text = "Browse…", X = Pos.AnchorEnd(12), Y = y };
        dialog.Add(browse);
        y += 1;
        var find = new Button { Text = "Find", X = 14, Y = y };
        var check = new Button { Text = "Check", X = Pos.Right(find) + 1, Y = y };
        var status = new Label
        {
            X = Pos.Right(check) + 2,
            Y = y,
            Width = Dim.Fill(1),
            Text = current.Length == 0 ? $"None chosen: {ScriptLanguage.Label(language)} scripts do not run." : "",
        };
        dialog.Add(find, check, status);
        y += 2;

        var checking = 0;

        // The UI thread waits for the work: it finds a runtime, or checks one, by starting it (a moment, in the background).
        // Results are taken in by a timer of the UI thread itself, as a timer added from another thread can wait for the
        // UI's timers to finish.
        void Run(Func<Task<(string Message, string? Runtime)>> work)
        {
            var version = ++checking;
            status.Text = "Checking…";
            var task = Task.Run(work);
            app.AddTimeout(TimeSpan.FromMilliseconds(50), () =>
            {
                if (!task.IsCompleted)
                {
                    return true;
                }

                if (version == checking)
                {
                    status.Text = task.IsCompletedSuccessfully ? task.Result.Message : task.Exception?.GetBaseException().Message ?? "";
                    if (task.IsCompletedSuccessfully && task.Result.Runtime is { } runtime)
                    {
                        field.Text = runtime;
                    }
                }

                return false;
            });
        }

        async Task<(string, string?)> Describe(string runtime)
        {
            var (ok, message) = await ScriptRuntimeLocator.CheckAsync(language, runner.ResolveRuntime(runtime), workspace.RootPath).ConfigureAwait(false);
            return ((ok ? "✓ " : "✗ ") + message, null);
        }

        Ui.OnPress(browse, () =>
        {
            if (Ui.PickFile(app, $"Choose {name}", workspace.RootPath) is { } path)
            {
                var relative = Path.GetRelativePath(workspace.RootPath, path);
                field.Text = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
                var chosen = field.Text;
                Run(() => Describe(chosen));
            }
        });
        Ui.OnPress(check, () =>
        {
            var text = field.Text.Trim();
            if (text.Length == 0)
            {
                status.Text = $"None chosen: {ScriptLanguage.Label(language)} scripts do not run.";
                return;
            }

            Run(() => Describe(text));
        });
        Ui.OnPress(find, () => Run(async () =>
        {
            if (await ScriptRuntimeLocator.FindAsync(language, workspace.RootPath, runner.ResolveRuntime).ConfigureAwait(false) is not { } found)
            {
                return (language switch
                {
                    ScriptLanguage.Python => "No Python found in the workspace (.venv, venv, env) or on PATH.",
                    ScriptLanguage.CSharp => "No .NET SDK 10 or later found (dotnet on PATH, ~/.dotnet).",
                    _ => "Neither node nor bun was found on PATH.",
                }, null);
            }

            return ("✓ " + found.Description, found.Runtime);
        }));
        return field;
    }
}
