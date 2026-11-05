using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TermRest.Core.Postman;
using TermRest.Core.Workspace;

namespace TermRest.Tui.Views;

/// <summary>
/// Sidebar tree of collections → folders → requests. Objects in the tree are the live <see cref="PostmanCollection"/> and
/// <see cref="CollectionNode"/> instances, so edits elsewhere only need <see cref="RefreshItem"/>.
/// </summary>
internal sealed class CollectionTree : View
{
    private readonly TreeView<object> _tree;
    private ApiWorkspace _workspace;

    public CollectionTree(ApiWorkspace workspace)
    {
        _workspace = workspace;
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _tree = new TreeView<object>
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            TreeBuilder = new DelegateTreeBuilder<object>(GetChildren, CanExpand),
            AspectGetter = Describe,
        };
        _tree.SelectionChanged += (_, e) => SelectionChanged?.Invoke(this, e.NewValue);
        _tree.Accepted += (_, _) =>
        {
            if (_tree.SelectedObject is { } selected)
            {
                ItemActivated?.Invoke(this, selected);
            }
        };
        _tree.MouseEvent += (_, mouse) =>
        {
            if (mouse.Flags.HasFlag(MouseFlags.RightButtonClicked) && mouse.Position is { } position)
            {
                var item = _tree.GetObjectOnRow(position.Y);
                if (item is not null)
                {
                    _tree.SelectedObject = item;
                }

                ContextMenuRequested?.Invoke(this, mouse.ScreenPosition);
                mouse.Handled = true;
            }
        };
        _tree.KeyDown += (_, key) =>
        {
            if (key == Key.F10.WithShift)
            {
                ContextMenuRequested?.Invoke(this, null);
                key.Handled = true;
            }
            else if (key == Key.Delete && _tree.SelectedObject is { } selected)
            {
                DeleteRequested?.Invoke(this, selected);
                key.Handled = true;
            }
            else if (key == Key.F2 && _tree.SelectedObject is { } toRename)
            {
                RenameRequested?.Invoke(this, toRename);
                key.Handled = true;
            }
        };

        Add(_tree);
        Reload(workspace);
    }

    /// <summary>Selection moved to a collection, folder or request (null when cleared).</summary>
    public event EventHandler<object?>? SelectionChanged;

    /// <summary>Enter or double-click on an item.</summary>
    public event EventHandler<object>? ItemActivated;

    /// <summary>Right-click (screen position) or Shift+F10 (null position) on the tree.</summary>
    public event EventHandler<System.Drawing.Point?>? ContextMenuRequested;

    public event EventHandler<object>? DeleteRequested;

    public event EventHandler<object>? RenameRequested;

    public object? SelectedItem => _tree.SelectedObject;

    public void Reload(ApiWorkspace workspace)
    {
        _workspace = workspace;
        _tree.ClearObjects();
        _tree.AddObjects(workspace.Collections);
        foreach (var collection in workspace.Collections)
        {
            _tree.Expand(collection);
        }
    }

    public void AddCollection(PostmanCollection collection)
    {
        _tree.AddObject(collection);
        _tree.Expand(collection);
        Select(collection);
    }

    public void RemoveCollection(PostmanCollection collection) => _tree.Remove(collection);

    /// <summary>Re-renders an item and rebuilds its children (after rename, add, delete, dirty-state change).</summary>
    public void RefreshItem(object item)
    {
        _tree.RefreshObject(item, startAtTop: false);
        _tree.SetNeedsDraw();
    }

    public void RefreshAll()
    {
        _tree.RebuildTree();
        _tree.SetNeedsDraw();
    }

    public void Select(object item)
    {
        if (item is CollectionNode node)
        {
            _tree.Expand(node.Collection);
            foreach (var ancestor in node.Ancestors().Reverse())
            {
                _tree.Expand(ancestor is CollectionRootNode ? node.Collection : ancestor);
            }
        }

        _tree.SelectedObject = item;

        // Before the first layout the viewport has no height, and scrolling then would hide the top rows.
        if (_tree.Viewport.Height > 0)
        {
            _tree.EnsureVisible(item);
        }

        _tree.SetNeedsDraw();
    }

    public void ExpandItem(object item) => _tree.Expand(item);

    public new bool SetFocus() => _tree.SetFocus();

    private IEnumerable<object> GetChildren(object item) => item switch
    {
        PostmanCollection collection => collection.RootNode.Children,
        FolderNode folder => folder.Children,
        _ => [],
    };

    private static bool CanExpand(object item) => item is PostmanCollection or FolderNode;

    private static string Describe(object item) => item switch
    {
        PostmanCollection collection => $"{(collection.IsDirty ? "● " : "")}{collection.Name}",
        FolderNode folder => folder.Name,
        RequestNode request => $"{ShortMethod(request.Method),-4} {request.Name}",
        _ => item.ToString() ?? "",
    };

    private static string ShortMethod(string method) => method.ToUpperInvariant() switch
    {
        "DELETE" => "DEL",
        "OPTIONS" => "OPT",
        "PATCH" => "PTCH",
        var m when m.Length > 4 => m[..4],
        var m => m,
    };
}
