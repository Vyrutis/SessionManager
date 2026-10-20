using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;

namespace SessionManager;

/// <summary>The session list: SSH / RDP / VNC tabs, multiple selection (Ctrl/Shift+click), keyboard, drag and drop, filter.</summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<Protocol, Border> _listTabs = new();
    private readonly Dictionary<Protocol, TextBlock> _listCounts = new();
    private readonly TextBlock _emptyHint = new()
    {
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(16, 24),
        VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, IsVisible = false, Classes = { "sub" },
    };
    private bool _switchingTab;

    private Node ActiveRoot => _store.RootFor(_store.Data.ActiveTab);
    private static string Kind(Protocol p) => p switch { Protocol.Ssh => "SSH", Protocol.Vnc => "VNC", _ => "RDP" };
    private static string ColorFor(Protocol p) => p switch { Protocol.Ssh => "#2DA44E", Protocol.Vnc => "#9A6BE0", _ => "#4F8DF5" };
    private static Geometry IconFor(Protocol p) => p switch { Protocol.Ssh => SshIcon, Protocol.Vnc => VncIcon, _ => RdpIcon };
    /// <summary>The kinds this app shows as tabs. Web sessions (made in the Windows app) are opened in the default browser.</summary>
    private static readonly Protocol[] Protocols = [Protocol.Ssh, Protocol.Rdp, Protocol.Vnc];
    private static int ProtocolCount => Protocols.Length;
    private int HiddenCount => _store.Data.HiddenTabs.Count(Protocols.Contains);

    private static readonly Geometry FolderIcon = Geometry.Parse("M2 5.5C2 4.7 2.7 4 3.5 4H9l2 2h9.5c.8 0 1.5.7 1.5 1.5v11c0 .8-.7 1.5-1.5 1.5h-17C2.7 20 2 19.3 2 18.5Z");
    private static readonly Geometry SshIcon = Geometry.Parse("M2 4H22V20H2Z M4 8V18H20V8Z M6 10L10 13L6 16V14.4L7.9 13L6 11.6Z M11 15.2H17V16.8H11Z");
    private static readonly Geometry RdpIcon = Geometry.Parse("M2 3H22V17H2Z M4 5V15H20V5Z M8 19H16V21H8Z M11 17H13V19H11Z");
    private static readonly Geometry VncIcon = Geometry.Parse("M2 3H22V17H2Z M4 5V15H20V5Z M8 19H16V21H8Z M11 17H13V19H11Z M8 7L14 10L11.2 10.9L13 13.4L11.9 14.2L10.1 11.7L8 13.5Z");

    // ───────────── Tabs ─────────────

    /// <summary>The SSH / RDP / VNC row above the list, with a ⋯ menu to show or hide tabs.</summary>
    private Control BuildListTabs()
    {
        var row = new UniformGrid { Rows = 1 };
        foreach (var p in Protocols)
        {
            var count = new TextBlock { FontSize = 11, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Classes = { "sub" } };
            var tab = new Border
            {
                Classes = { "listtab" },
                Padding = new Thickness(4, 6, 4, 5),
                BorderThickness = new Thickness(0, 0, 0, 2),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new PathIcon { Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), Data = IconFor(p), Foreground = new SolidColorBrush(Color.Parse(ColorFor(p))) },
                        new TextBlock { Text = Kind(p), VerticalAlignment = VerticalAlignment.Center },
                        count,
                    },
                },
            };
            var proto = p;
            tab.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(tab).Properties.IsLeftButtonPressed) return;
                ShowTab(proto);
                e.Handled = true;
            };
            tab.ContextRequested += (_, e) =>
            {
                e.Handled = true;
                new ContextMenu { ItemsSource = TabMenu(proto) }.Open(tab);
            };
            _listTabs[p] = tab;
            _listCounts[p] = count;
            row.Children.Add(tab);
        }

        var more = new Button { Content = "⋯", Classes = { "tool" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 2) };
        ToolTip.SetTip(more, "Show or hide the SSH, RDP and VNC tabs");
        more.Click += (_, _) => new ContextMenu { ItemsSource = TabChoices() }.Open(more);

        var dock = new DockPanel { Margin = new Thickness(6, 0, 4, 0) };
        DockPanel.SetDock(more, Dock.Right);
        dock.Children.Add(more);
        dock.Children.Add(row);
        var border = new Border { Child = dock, BorderThickness = new Thickness(0, 0, 0, 1) }.Res(Border.BorderBrushProperty, "SmBorder");
        // Set by styles, not on the tabs themselves, so hover and the active underline can override them.
        border.Styles.Add(new Style(x => x.OfType<Border>().Class("listtab"))
        {
            Setters = { new Setter(Border.BackgroundProperty, Brushes.Transparent), new Setter(Border.BorderBrushProperty, Brushes.Transparent) },
        });
        border.Styles.Add(new Style(x => x.OfType<Border>().Class("listtab").Class(":pointerover"))
        {
            Setters = { new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SmHover")) },
        });
        border.Styles.Add(new Style(x => x.OfType<Border>().Class("listtab").Class("active"))
        {
            Setters = { new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("SmAccent")) },
        });
        border.Styles.Add(new Style(x => x.OfType<Border>().Class("listtab").Class("active").Descendant().OfType<TextBlock>().Not(y => y.Class("sub")))
        {
            Setters = { new Setter(TextBlock.FontWeightProperty, FontWeight.SemiBold) },
        });
        return border;
    }

    private List<object> TabMenu(Protocol p)
    {
        var items = new List<object>
        {
            MenuItemFor($"New {Kind(p)} session…", () => { ShowTab(p); ClearSelection(); _ = NewSession(p); }),
            MenuItemFor($"New {Kind(p)} folder…", () => _ = NewFolder(_store.RootFor(p))),
            new Separator(),
            MenuItemFor($"Hide {Kind(p)} tab", () => SetTabHidden(p, true), enabled: HiddenCount < ProtocolCount - 1),
            new Separator(),
        };
        items.AddRange(TabChoices());
        return items;
    }

    private List<object> TabChoices()
    {
        var hidden = _store.Data.HiddenTabs;
        return Protocols.Select(p =>
        {
            bool shown = !hidden.Contains(p);
            var item = MenuItemFor($"Show {Kind(p)} tab", () => SetTabHidden(p, shown),
                enabled: !shown || HiddenCount < ProtocolCount - 1); // the last visible tab can't be hidden
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = shown;
            return (object)item;
        }).ToList();
    }

    private static MenuItem MenuItemFor(string header, Action action, bool enabled = true)
    {
        var m = new MenuItem { Header = header, IsEnabled = enabled };
        m.Click += (_, _) => action();
        return m;
    }

    private void ShowTab(Protocol p, bool save = true)
    {
        if (_switchingTab) return;
        _switchingTab = true;
        try
        {
            bool changed = _store.Data.ActiveTab != p || !ReferenceEquals(_tree.ItemsSource, _store.RootFor(p).Children);
            if (changed) ClearSelection();
            _store.Data.ActiveTab = p;
            foreach (var (proto, tab) in _listTabs) tab.Classes.Set("active", proto == p);
            if (changed) _tree.ItemsSource = _store.RootFor(p).Children;
            UpdateEmptyHint();
            if (save && changed) Save();
        }
        finally { _switchingTab = false; }
    }

    /// <summary>Visibility of the tabs and the number of sessions (or filter matches) on each.</summary>
    private void UpdateListTabs()
    {
        if (_listTabs.Count == 0) return;
        bool filtering = (_filter.Text ?? "").Trim().Length > 0;
        foreach (var p in Protocols)
        {
            var sessions = _store.RootFor(p).Descendants().Where(n => n.IsSession).ToList();
            int count = filtering ? sessions.Count(n => n.IsVisible) : sessions.Count;
            _listTabs[p].IsVisible = !_store.Data.HiddenTabs.Contains(p);
            _listCounts[p].Text = count > 0 ? count.ToString() : "";
            ToolTip.SetTip(_listTabs[p], filtering
                ? $"{count} {Kind(p)} session{(count == 1 ? "" : "s")} match the filter"
                : $"{count} {Kind(p)} session{(count == 1 ? "" : "s")}. Right-click to hide this tab.");
        }
        UpdateEmptyHint();
    }

    private void UpdateEmptyHint()
    {
        var root = ActiveRoot;
        var kind = Kind(root.Protocol);
        string? text = root.Children.Count == 0
            ? $"No {kind} sessions yet.\nRight-click here or use + {kind} in the toolbar."
            : root.Children.All(c => !c.IsVisible) ? $"No {kind} sessions match the filter." : null;
        _emptyHint.Text = text ?? "";
        _emptyHint.IsVisible = text != null;
    }

    private void SetTabHidden(Protocol p, bool hidden)
    {
        var list = _store.Data.HiddenTabs;
        if (hidden)
        {
            if (list.Contains(p) || HiddenCount >= ProtocolCount - 1) return; // keep at least one
            list.Add(p);
            if (_store.Data.ActiveTab == p) ShowTab(Protocols.First(x => !list.Contains(x)), save: false);
        }
        else if (!list.Remove(p)) return;
        UpdateListTabs();
        Save();
    }

    /// <summary>Selects only this node, switching to (and if needed showing) its tab and opening its folders.</summary>
    private void Select(Node node)
    {
        var p = node.Protocol;
        if (_store.Data.HiddenTabs.Remove(p)) UpdateListTabs();
        ShowTab(p);
        ClearSelection();
        for (var f = node.Parent; f != null; f = f.Parent) f.IsExpanded = true;
        node.IsSelected = true;
    }

    private void ClearSelection()
    {
        _tree.SelectedItems.Clear();
        foreach (var root in _store.Data.Roots)
        foreach (var n in root.Descendants().Where(n => n.IsSelected))
            n.IsSelected = false;
    }

    // ───────────── The tree ─────────────

    private void BuildTree()
    {
        _tree.SelectionMode = SelectionMode.Multiple; // Ctrl+click and Shift+click pick several items
        _tree.Background = Brushes.Transparent;
        _tree.ItemTemplate = new FuncTreeDataTemplate<Node>((node, _) =>
        {
            var icon = new PathIcon
            {
                Width = 15, Height = 15, Margin = new Thickness(0, 0, 7, 0),
                Data = node.IsFolder ? FolderIcon : IconFor(node.Protocol),
                Foreground = new SolidColorBrush(Color.Parse(node.IsFolder ? "#D9A33B" : ColorFor(node.Protocol))),
            };
            var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, [!TextBlock.TextProperty] = new Binding(nameof(Node.Name)) };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Children = { icon, name } };
            if (node.IsSession)
            {
                var sub = new TextBlock { FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Classes = { "sub" },
                    [!TextBlock.TextProperty] = new Binding(nameof(Node.Subtitle)) };
                panel.Children.Add(sub);
                panel.DoubleTapped += (_, e) =>
                {
                    if (e.KeyModifiers != KeyModifiers.None) return;
                    OpenSession(node);
                    e.Handled = true;
                };
            }
            AttachDrag(panel, node);
            return panel;
        }, n => n.Children);

        _tree.Styles.Add(new Style(x => x.OfType<TreeViewItem>())
        {
            Setters =
            {
                new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(Node.IsExpanded), BindingMode.TwoWay)),
                new Setter(Visual.IsVisibleProperty, new Binding(nameof(Node.IsVisible))),
                new Setter(TreeViewItem.IsSelectedProperty, new Binding(nameof(Node.IsSelected), BindingMode.TwoWay)),
            },
        });

        _tree.KeyDown += Tree_KeyDown;
        _tree.ContextRequested += Tree_ContextRequested;
        DragDrop.SetAllowDrop(_tree, true);
        _tree.AddHandler(DragDrop.DragOverEvent, Tree_DragOver);
        _tree.AddHandler(DragDrop.DropEvent, Tree_Drop);

        ShowTab(_store.Data.ActiveTab, save: false);
        UpdateListTabs();
    }

    private Node? SelectedNode => Targets() is [var only] ? only : null;

    /// <summary>What an action applies to: the selected items, in list order.</summary>
    private List<Node> Targets()
    {
        var selected = _tree.SelectedItems.OfType<Node>().Where(n => !n.IsRoot).ToHashSet();
        return ActiveRoot.Descendants().Where(selected.Contains).ToList();
    }

    /// <summary>Drops items whose folder is also in the list (deleting or moving the folder covers them).</summary>
    private static List<Node> Outermost(List<Node> nodes) =>
        nodes.Where(n => !nodes.Any(o => o != n && o.IsAncestorOf(n))).ToList();

    private static Node? NodeFrom(object? source)
    {
        for (var v = source as StyledElement; v != null; v = v.Parent)
            if (v.DataContext is Node n) return n;
        return null;
    }

    private Node TargetFolder(Protocol protocol)
    {
        var n = SelectedNode;
        var folder = n == null ? null : n.IsFolder ? n : n.Parent;
        return folder != null && folder.Protocol == protocol ? folder : _store.RootFor(protocol);
    }

    private void Tree_KeyDown(object? sender, KeyEventArgs e)
    {
        var targets = Targets();
        if (targets.Count == 0) return;
        switch (e.Key)
        {
            case Key.Enter when targets.Any(n => n.IsSession):
                foreach (var n in targets.Where(n => n.IsSession)) OpenSession(n);
                break;
            case Key.F2 when targets.Count == 1: _ = Rename(targets[0]); break;
            case Key.Delete: _ = Delete(targets); break;
            case Key.Escape when targets.Count > 1: ClearSelection(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void ApplyFilter()
    {
        var text = (_filter.Text ?? "").Trim();
        ClearSelection(); // never act on rows the filter hides
        foreach (var root in _store.Data.Roots) Filter(root, text);
        UpdateListTabs();
    }

    private static bool Filter(Node node, string text)
    {
        bool any = false;
        foreach (var c in node.Children) any |= Filter(c, text);
        bool self = text.Length == 0 || node.IsRoot
                    || node.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                    || (node.Settings?.Host.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (node.Settings?.Username.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);
        node.IsVisible = self || any;
        if (text.Length > 0 && any) node.IsExpanded = true;
        return node.IsVisible && (self && !node.IsRoot || any);
    }

    // Drag and drop to move sessions and folders (several at once when several are selected).

    private Point? _dragStart;
    private Node? _dragNode;
    private List<Node>? _dragGroup;

    private void AttachDrag(Control c, Node node)
    {
        c.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) return;
            _dragStart = e.GetPosition(this);
            _dragNode = node;
            // A plain press on one of several selected items lets the TreeView select just that item;
            // remember the group so a drag that follows still moves all of them.
            var targets = Targets();
            _dragGroup = e.KeyModifiers == KeyModifiers.None && targets.Count > 1 && targets.Contains(node) ? Outermost(targets) : null;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        c.PointerReleased += (_, _) => { _dragStart = null; _dragNode = null; _dragGroup = null; };
        c.PointerMoved += async (_, e) =>
        {
            if (_dragStart is not { } start || _dragNode != node || !e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
            _dragStart = null;
            var group = _dragGroup ?? [node];
            _dragGroup = null;
            var data = new DataObject();
            data.Set("sessionmanager/node", string.Join(",", group.Select(n => n.Id)));
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
            _dragNode = null;
        };
    }

    private Node? DropTarget(DragEventArgs e, out List<Node> dragged)
    {
        dragged = e.Data.Get("sessionmanager/node") is string ids
            ? ids.Split(',').Select(s => Guid.TryParse(s, out var g) ? _store.FindById(g) : null).OfType<Node>().ToList()
            : [];
        if (dragged.Count == 0) return null;
        var over = NodeFrom(e.Source);
        var folder = over == null ? ActiveRoot : over.IsFolder ? over : over.Parent; // empty space: the top of the tab
        if (folder == null) return null;
        foreach (var d in dragged)
            if (d.IsRoot || d.Protocol != folder.Protocol || d == folder || d.IsAncestorOf(folder)) return null;
        return dragged.All(d => d.Parent == folder) ? null : folder;
    }

    private void Tree_DragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files)) { e.DragEffects = DragDropEffects.Copy; return; }
        e.DragEffects = DropTarget(e, out _) != null ? DragDropEffects.Move : DragDropEffects.None;
    }

    private void Tree_Drop(object? sender, DragEventArgs e)
    {
        if (e.Data.GetFiles() is { } files)
        {
            foreach (var f in files.Select(f => f.TryGetLocalPath()).OfType<string>()) _ = ImportPath(f);
            return;
        }
        var folder = DropTarget(e, out var dragged);
        if (folder == null) return;
        ClearSelection();
        foreach (var d in dragged.Where(d => d.Parent != folder))
        {
            _store.Remove(d);
            _store.AddChild(folder, d);
        }
        folder.IsExpanded = true;
        foreach (var d in dragged) d.IsSelected = true;
        Save();
    }
}
