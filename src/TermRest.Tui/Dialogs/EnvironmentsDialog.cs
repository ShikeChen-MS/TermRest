using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Model;
using TermRest.Core.Postman;
using TermRest.Core.Workspace;
using TermRest.Tui.Views;

namespace TermRest.Tui.Dialogs;

/// <summary>Environment manager: pick the active environment, edit values, create and import environments.</summary>
internal static class EnvironmentsDialog
{
    /// <summary>Returns true when the active environment changed.</summary>
    public static bool Show(IApplication app, ApiWorkspace workspace)
    {
        var previousActive = workspace.ActiveEnvironment;
        using var dialog = new Dialog { Title = "Environments", Width = Dim.Percent(90), Height = Dim.Percent(85) };

        var names = new ObservableCollection<string>();
        var list = new ListView { X = 1, Y = 1, Width = 30, Height = Dim.Fill(4) };
        list.SetSource(names);
        var values = new KeyValueTable(KeyValueKind.Variables) { X = Pos.Right(list) + 2, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(4) };
        var activeLabel = new Label { X = 1, Y = Pos.AnchorEnd(3), Width = Dim.Fill(1) };

        var scopes = new List<PostmanEnvironment>();
        List<KeyValueEntry>? editing = null;
        PostmanEnvironment? current = null;

        void Refresh(int select)
        {
            scopes.Clear();
            scopes.AddRange(workspace.Environments);
            if (workspace.Globals is { } globals)
            {
                scopes.Add(globals);
            }

            names.Clear();
            foreach (var environment in scopes)
            {
                var marker = environment == workspace.ActiveEnvironment ? "● " : "  ";
                names.Add(environment.IsGlobals ? $"  [globals] {environment.Name}" : marker + environment.Name);
            }

            activeLabel.Text = $"Active environment: {workspace.ActiveEnvironment?.Name ?? "(none)"}   ● marks the active one";
            if (scopes.Count > 0)
            {
                list.Value = Math.Clamp(select, 0, scopes.Count - 1);
                Load(scopes[list.Value ?? 0]);
            }
            else
            {
                current = null;
                values.SetEntries([]);
            }
        }

        void CommitCurrent()
        {
            if (current is not null && editing is not null && !KeyValueEntry.ListEquals(editing, current.Values))
            {
                current.SetValues(editing);
            }
        }

        void Load(PostmanEnvironment environment)
        {
            CommitCurrent();
            current = environment;
            editing = KeyValueEntry.CloneList(environment.Values);
            values.SetEntries(editing);
        }

        list.ValueChanged += (_, e) =>
        {
            if (e.NewValue is int index && index >= 0 && index < scopes.Count && scopes[index] != current)
            {
                Load(scopes[index]);
            }
        };

        var activate = new Button { Text = "Set active", X = 1, Y = Pos.AnchorEnd(1) };
        activate.Accepted += (_, _) =>
        {
            if (current is { IsGlobals: false })
            {
                workspace.ActiveEnvironment = current;
                Refresh(list.Value ?? 0);
            }
        };
        var none = new Button { Text = "No environment", X = Pos.Right(activate) + 1, Y = Pos.AnchorEnd(1) };
        none.Accepted += (_, _) =>
        {
            workspace.ActiveEnvironment = null;
            Refresh(list.Value ?? 0);
        };
        var create = new Button { Text = "New…", X = Pos.Right(none) + 1, Y = Pos.AnchorEnd(1) };
        create.Accepted += (_, _) =>
        {
            if (Ui.PromptText(app, "New environment", "Name") is { Length: > 0 } name)
            {
                CommitCurrent();
                current = null;
                workspace.CreateEnvironment(name.Trim());
                Refresh(workspace.Environments.Count - 1);
            }
        };
        var rename = new Button { Text = "Rename…", X = Pos.Right(create) + 1, Y = Pos.AnchorEnd(1) };
        rename.Accepted += (_, _) =>
        {
            if (current is not null && Ui.PromptText(app, "Rename environment", "Name", current.Name) is { Length: > 0 } name)
            {
                var wasActive = workspace.ActiveEnvironment == current;
                current.Name = name.Trim();
                if (wasActive)
                {
                    workspace.ActiveEnvironment = current;
                }

                Refresh(list.Value ?? 0);
            }
        };
        var import = new Button { Text = "Import…", X = Pos.Right(rename) + 1, Y = Pos.AnchorEnd(1) };
        import.Accepted += (_, _) =>
        {
            if (Ui.PickFile(app, "Import Postman environment", workspace.RootPath, false, ("Postman environment", [".json"])) is { } path)
            {
                try
                {
                    CommitCurrent();
                    current = null;
                    var imported = workspace.ImportEnvironment(path);
                    Refresh(workspace.Environments.IndexOf(imported));
                }
                catch (Exception ex)
                {
                    Ui.Error(app, "Import failed", ex.Message);
                }
            }
        };

        dialog.Add(new Label { Text = "Environments", X = 1, Y = 0 }, new Label { Text = "Values (secret values are masked)", X = Pos.Right(list) + 2, Y = 0 }, list, values, activeLabel, activate, none, create, rename, import);
        dialog.AddButton(new Button { Text = "Close" });
        Refresh(Math.Max(0, workspace.ActiveEnvironment is { } a ? workspace.Environments.IndexOf(a) : 0));
        app.Run(dialog);

        CommitCurrent();
        foreach (var environment in workspace.Environments.Where(e => e.IsDirty))
        {
            environment.Save();
        }

        if (workspace.Globals?.IsDirty == true)
        {
            workspace.Globals.Save();
        }

        workspace.SaveSettings();
        return previousActive != workspace.ActiveEnvironment;
    }
}
