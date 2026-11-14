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
    /// <summary>
    /// Edits the scripts that run around every request in a collection or folder; null when cancelled. New scripts are
    /// written in <paramref name="defaultLanguage"/> unless another language is chosen.
    /// </summary>
    public static ScriptSet? Edit(IApplication app, string owner, ScriptSet scripts, string defaultLanguage)
    {
        using var dialog = new Dialog { HotKeySpecifier = Ui.NoHotKey, Title = $"Scripts of {owner}", Width = Dim.Percent(90), Height = Dim.Percent(80) };
        dialog.Add(new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Text = "They run for every request inside, before those of its folders and its own, when the request runs scripts in their language.",
        });
        var editor = new ScriptsEditor { X = 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(1), DefaultLanguage = defaultLanguage };
        editor.Load(scripts);
        dialog.Add(editor);
        dialog.AddButton(new Button { Text = "Cancel" });
        dialog.AddButton(new Button { Text = "OK" });
        editor.FocusPreRequest();
        app.Run(dialog);
        return dialog.Canceled ? null : editor.Scripts;
    }

    /// <summary>
    /// Chooses the runtimes that run this workspace's scripts, Python, Node.js or Bun, the .NET SDK and PowerShell (kept
    /// in the user's settings, not in the workspace), and the order in which a request's languages are tried (for every
    /// workspace); shows what scripts left behind: variables set in this session, and running runtimes.
    /// </summary>
    public static void ShowRuntimes(IApplication app, ApiWorkspace workspace, RequestRunner runner, string? userDirectory = null)
    {
        using var dialog = new Dialog { Title = "Scripts", Width = Dim.Percent(80) };
        var settings = UserSettings.Load(userDirectory);
        var rows = settings.GetScriptLanguageOrder().Select(language => AddRuntime(app, dialog, workspace, runner, language, settings)).ToList();
        void Place()
        {
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i].Place(i, rows.Count);
            }

            dialog.SetNeedsLayout();
        }

        foreach (var row in rows)
        {
            Ui.OnPress(row.Up, () => Move(row, -1));
            Ui.OnPress(row.Down, () => Move(row, 1));
        }

        void Move(RuntimeRow row, int by)
        {
            var index = rows.IndexOf(row);
            if (index + by >= 0 && index + by < rows.Count)
            {
                rows.RemoveAt(index);
                rows.Insert(index + by, row);
                Place();
            }
        }

        Place();
        var y = 1 + (rows.Count * 3);
        dialog.Add(new Label
        {
            X = 14,
            Y = y,
            Width = Dim.Fill(1),
            Height = 5,
            Text = "A command (python3, node, bun, dotnet, pwsh) or a path, relative to the\n"
                   + "workspace (.venv/bin/python), kept in your settings for this workspace only.\n"
                   + "Its packages and modules (pip, npm, NuGet, PowerShell modules) are yours.\n"
                   + "▲ ▼ change the order, for all workspaces: a request runs its scripts in the\n"
                   + "first language that has all of them, unless its Scripts tab chooses one.",
        });
        y += 6;
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

        // A runtime may be named after its file (my_node): an underscore must not make a key that presses Stop scripts.
        var running = new Label { X = 14, Y = y, Width = Dim.Fill(22), HotKeySpecifier = Ui.NoHotKey };
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
        foreach (var row in rows)
        {
            user.SetScriptRuntime(row.Language, workspace.RootPath, row.Field.Text);
        }

        user.SetScriptLanguageOrder(rows.Select(r => r.Language));
        Ui.TrySave(app, () => user.Save(userDirectory)); // applied for this session either way
        foreach (var language in ScriptLanguage.All)
        {
            runner.SetRuntime(language, user.GetScriptRuntime(language, workspace.RootPath));
        }

        runner.ScriptLanguageOrder = user.GetScriptLanguageOrder();
    }

    /// <summary>A language's row: its place in the order, its runtime with Browse…, ▲ ▼, then Find and Check with what they found.</summary>
    private sealed record RuntimeRow(string Language, Label Name, TextField Field, Button Browse, Button Up, Button Down, Button Find, Button Check, Label Status)
    {
        /// <summary>Puts the row in place <paramref name="index"/> of <paramref name="count"/>.</summary>
        public void Place(int index, int count)
        {
            var y = 1 + (index * 3);
            Name.Text = $"{index + 1} {ScriptLanguage.Label(Language)}";
            foreach (var view in (View[])[Name, Field, Browse, Up, Down])
            {
                view.Y = y;
            }

            foreach (var view in (View[])[Find, Check, Status])
            {
                view.Y = y + 1;
            }

            Up.Enabled = index > 0;
            Down.Enabled = index < count - 1;
        }
    }

    /// <summary>A runtime's row (see <see cref="RuntimeRow"/>).</summary>
    private static RuntimeRow AddRuntime(IApplication app, Dialog dialog, ApiWorkspace workspace, RequestRunner runner, string language,
        UserSettings settings)
    {
        var name = language switch
        {
            ScriptLanguage.Python => "Python",
            ScriptLanguage.CSharp => "dotnet (.NET SDK 10 or later)",
            ScriptLanguage.PowerShell => "pwsh (PowerShell 7 or later)",
            _ => "Node.js or Bun",
        };
        var current = settings.GetScriptRuntime(language, workspace.RootPath) ?? "";
        var label = new Label { Text = ScriptLanguage.Label(language), X = 1, Width = 12 };
        var field = new TextField { Text = current, X = 14, Width = Dim.Fill(25) };
        var browse = new Button { Text = "Browse…", X = Pos.AnchorEnd(24) };
        var up = new Button { Text = "▲", X = Pos.AnchorEnd(12) };
        var down = new Button { Text = "▼", X = Pos.AnchorEnd(6) };
        dialog.Add(label, field, browse, up, down);
        var find = new Button { Text = "Find", X = 14 };
        var check = new Button { Text = "Check", X = Pos.Right(find) + 1 };
        var status = new Label
        {
            X = Pos.Right(check) + 2,
            Width = Dim.Fill(1),
            HotKeySpecifier = Ui.NoHotKey, // paths and versions
            Text = current.Length == 0 ? $"None chosen: {ScriptLanguage.Label(language)} scripts do not run." : "",
        };
        dialog.Add(find, check, status);

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
            var (ok, message) = await ScriptRuntimeLocator.CheckAsync(language, runner.ResolveRuntime(runtime)).ConfigureAwait(false);
            return ((ok ? "✓ " : "✗ ") + message, null);
        }

        Ui.OnPress(browse, () =>
        {
            if (Ui.PickFile(app, $"Choose {name}", workspace.RootPath) is { } path)
            {
                var relative = Path.GetRelativePath(workspace.RootPath, path);

                // A name alone (bun.exe, in the workspace's own folder) would be a command, looked for on PATH.
                field.Text = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path
                    : relative.IndexOfAny(['/', '\\']) < 0 ? "." + Path.DirectorySeparatorChar + relative
                    : relative;
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
                    ScriptLanguage.PowerShell => "No PowerShell 7 or later found (pwsh on PATH).",
                    _ => "Neither node nor bun was found on PATH.",
                }, null);
            }

            return ((found.Checked ? "✓ " : "") + found.Description, found.Runtime);
        }));
        return new RuntimeRow(language, label, field, browse, up, down, find, check, status);
    }
}
