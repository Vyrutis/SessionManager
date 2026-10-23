using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SessionManager;

/// <summary>The session list: SSH / RDP / VNC / Web tabs, multiple selection, keyboard, drag and drop, filter.</summary>
public partial class MainWindow
{
    private readonly List<Node> _picked = new();
    private Node? _anchor;
    private bool _tabsReady, _switchingTab;
    private Node? _clearPicksOnUp;

    /// <summary>Ctrl/Shift state for clicks; replaced in tests.</summary>
    private static Func<ModifierKeys> ModifierState = () => Keyboard.Modifiers;

    /// <summary>Asks before deleting; replaced in tests.</summary>
    private static Func<Window, string, bool> AskDelete = (owner, question) =>
        MessageBox.Show(owner, question, "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>Shows a short note; replaced in tests.</summary>
    private static Action<Window, string> Note = (owner, text) =>
        MessageBox.Show(owner, text, "Session Manager", MessageBoxButton.OK, MessageBoxImage.Information);

    private Node ActiveRoot => _store.RootFor(_store.Data.ActiveTab);

    private RadioButton TabFor(Protocol p) => p switch { Protocol.Ssh => SshTab, Protocol.Rdp => RdpTab, Protocol.Web => WebTab, _ => VncTab };
    private TextBlock CountFor(Protocol p) => p switch { Protocol.Ssh => SshCount, Protocol.Rdp => RdpCount, Protocol.Web => WebCount, _ => VncCount };
    private TextBlock LabelFor(Protocol p) => p switch { Protocol.Ssh => SshLabel, Protocol.Rdp => RdpLabel, Protocol.Web => WebLabel, _ => VncLabel };
    private static Protocol TabProtocol(object sender) => Enum.Parse<Protocol>((string)((FrameworkElement)sender).Tag);
    private static string Kind(Protocol p) => p switch { Protocol.Ssh => "SSH", Protocol.Vnc => "VNC", Protocol.Web => "web", _ => "RDP" };
    /// <summary>"SSH", "RDP", "VNC", "Web" for labels at the start of a sentence or on their own.</summary>
    private static string Label(Protocol p) => p == Protocol.Web ? "Web" : Kind(p);

    // ───────────── Tabs ─────────────

    private void InitListTabs()
    {
        Tree.PreviewKeyDown += Tree_PreviewKeyDown;
        Tree.PreviewMouseLeftButtonUp += Tree_PreviewMouseLeftButtonUp;
        TabFor(_store.Data.ActiveTab).IsChecked = true;
        _tabsReady = true;
        ShowTab(_store.Data.ActiveTab, save: false);
        ListTabs.SizeChanged += (_, _) => FitListTabs();
        UpdateListTabs();
    }

    private void ListTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_tabsReady) ShowTab(TabProtocol(sender));
    }

    private void ShowTab(Protocol p, bool save = true)
    {
        if (_switchingTab) return;
        _switchingTab = true;
        try
        {
            ClearPicks();
            if (SelectedNode is { } sel) sel.IsSelected = false;
            bool changed = _store.Data.ActiveTab != p;
            _store.Data.ActiveTab = p;
            TabFor(p).IsChecked = true;
            Tree.ItemsSource = _store.RootFor(p).Children;
            UpdateEmptyHint();
            if (save && changed) Save();
        }
        finally { _switchingTab = false; }
    }

    /// <summary>Visibility of the tabs and the number of sessions (or filter matches) on each.</summary>
    private void UpdateListTabs()
    {
        if (!_tabsReady) return;
        bool filtering = FilterBox.Text.Trim().Length > 0 || FavoritesOnly.IsChecked == true;
        foreach (var p in Enum.GetValues<Protocol>())
        {
            var tab = TabFor(p);
            tab.Visibility = _store.Data.HiddenTabs.Contains(p) ? Visibility.Collapsed : Visibility.Visible;
            var sessions = _store.RootFor(p).Descendants().Where(n => n.IsSession).ToList();
            int count = filtering ? sessions.Count(n => n.IsVisible) : sessions.Count;
            CountFor(p).Text = count > 0 ? count.ToString() : "";
            tab.ToolTip = filtering
                ? $"{count} {Kind(p)} session{(count == 1 ? "" : "s")} match the filter"
                : $"{count} {Kind(p)} session{(count == 1 ? "" : "s")}. Right-click to hide this tab.";
        }
        FitListTabs();
        UpdateEmptyHint();
    }

    /// <summary>When the list is too narrow for "SSH 12" etc. on every tab, the tabs show only their icons (the tooltip still names them).</summary>
    private void FitListTabs()
    {
        if (!_tabsReady || ListTabs.ActualWidth <= 0) return;
        var shown = Enum.GetValues<Protocol>().Where(p => TabFor(p).Visibility == Visibility.Visible).ToList();
        if (shown.Count == 0) return;
        // Width each tab needs with its name and count, measured bold (the picked tab is) so switching tabs doesn't flip it.
        double needed = 0;
        foreach (var p in shown)
        {
            var bold = new Typeface(LabelFor(p).FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            double Text(TextBlock t) => t.Text.Length == 0 ? 0 : new FormattedText(t.Text, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, bold, t.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
            var count = CountFor(p);
            var icon = (FrameworkElement)((Panel)LabelFor(p).Parent).Children[0];
            icon.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            needed = Math.Max(needed, icon.DesiredSize.Width - icon.Margin.Right + 5 + Text(LabelFor(p)) + (count.Text.Length > 0 ? 5 + Text(count) : 0) + 8 + 2); // icon, gap, name, count, padding, slack
        }
        bool compact = ListTabs.ActualWidth / shown.Count < needed;
        foreach (var p in Enum.GetValues<Protocol>())
        {
            var visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            LabelFor(p).Visibility = visibility;
            CountFor(p).Visibility = visibility;
            ((FrameworkElement)((Panel)LabelFor(p).Parent).Children[0]).Margin = compact ? new Thickness(0) : new Thickness(0, 0, 5, 0);
        }
    }

    private void UpdateEmptyHint()
    {
        var root = ActiveRoot;
        var kind = Kind(root.Protocol);
        string? text = root.Children.Count == 0
            ? $"No {kind} sessions yet.\nRight-click here or use New in the toolbar."
            : root.Children.All(c => !c.IsVisible)
                ? FavoritesOnly.IsChecked == true && FilterBox.Text.Trim().Length == 0
                    ? $"No {kind} favourites yet.\nRight-click a session and choose Add to favourites."
                    : $"No {kind} sessions match the filter."
                : null;
        EmptyTabHint.Text = text ?? "";
        EmptyTabHint.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetTabHidden(Protocol p, bool hidden)
    {
        var list = _store.Data.HiddenTabs;
        if (hidden)
        {
            if (list.Contains(p) || list.Count >= Enum.GetValues<Protocol>().Length - 1) return; // keep at least one
            list.Add(p);
            if (_store.Data.ActiveTab == p) ShowTab(Enum.GetValues<Protocol>().First(x => !list.Contains(x)), save: false);
        }
        else if (!list.Remove(p)) return;
        UpdateListTabs();
        Save();
    }

    /// <summary>One line with a checkbox per tab; the last visible tab can't be unticked.</summary>
    private void AddTabChoices(ItemsControl menu)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var label = new TextBlock { Text = "Tabs", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        row.Children.Add(label);
        var boxes = new List<(Protocol P, CheckBox Box)>();
        void Refresh()
        {
            bool onlyOne = _store.Data.HiddenTabs.Count >= Enum.GetValues<Protocol>().Length - 1;
            foreach (var (_, box) in boxes) box.IsEnabled = box.IsChecked != true || !onlyOne; // the last visible tab can't be hidden
        }
        foreach (var p in Enum.GetValues<Protocol>())
        {
            var proto = p;
            var box = new CheckBox { Content = Label(p), IsChecked = !_store.Data.HiddenTabs.Contains(p), Margin = new Thickness(0, 0, 10, 0),
                VerticalContentAlignment = VerticalAlignment.Center, ToolTip = $"Show the {Label(p)} tab" };
            box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            box.Click += (_, _) => { SetTabHidden(proto, box.IsChecked != true); Refresh(); };
            boxes.Add((p, box));
            row.Children.Add(box);
        }
        Refresh();
        // A plain row in the menu: clicking a checkbox keeps the menu open, and the row itself isn't highlighted or clickable.
        menu.Items.Add(new MenuItem { Header = row, StaysOpenOnClick = true, Focusable = false, Name = "TabChoices" });
    }

    private void TabsMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        AddTabChoices(menu);
        menu.Items.Add(new Separator());
        var status = new MenuItem
        {
            Header = "Show online status", IsCheckable = true, IsChecked = _store.Data.ShowStatus,
            ToolTip = "A green or red dot on each session: whether its host answers on the session's port (checked about once a minute)",
        };
        status.Click += (_, _) => SetShowStatus(status.IsChecked);
        menu.Items.Add(status);
        var sorted = new MenuItem { Header = "Keep sorted A to Z", IsCheckable = true, IsChecked = !_store.Data.ManualOrder,
            ToolTip = "On: folders first, then sessions, by name. Off: the order you arrange by dragging (or Alt+Up / Alt+Down)." };
        sorted.Click += (_, _) => SetSortedByName(sorted.IsChecked);
        menu.Items.Add(sorted);
        menu.Items.Add(new Separator());
        var updates = new MenuItem { Header = "Check for updates on startup", IsCheckable = true, IsChecked = _store.Data.CheckForUpdates,
            ToolTip = "Looks for a newer release on GitHub when the app starts (at most once a day)" };
        updates.Click += (_, _) => { _store.Data.CheckForUpdates = updates.IsChecked; Save(); };
        menu.Items.Add(updates);
        if (OperatingSystem.IsWindows())
        {
            var master = new MenuItem { Header = "Master password...",
                ToolTip = "Your sessions are always encrypted for your Windows account. A master password adds a password prompt at startup, so the files stay unreadable even if someone uses your account." };
            master.Click += (_, _) => ChangeMasterPassword();
            menu.Items.Add(master);
        }
        var checkNow = new MenuItem { Header = "Check for updates now" };
        checkNow.Click += async (_, _) => await CheckForUpdatesAsync(manual: true);
        menu.Items.Add(checkNow);
        menu.Items.Add(new Separator());
        var language = new MenuItem { Header = "Language" };
        foreach (var (code, label) in new[] { ("en", "English"), ("lt", "Lietuvių") })
        {
            var choice = new MenuItem { Header = label, IsCheckable = true, IsChecked = Loc.Resolve(_store.Data.Language) == code };
            choice.Click += (_, _) =>
            {
                if (Loc.Resolve(_store.Data.Language) == code) { choice.IsChecked = true; return; }
                _store.Data.Language = code;
                Save();
                MessageBox.Show(this, "The language changes the next time you start Session Manager.", "Session Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            };
            language.Items.Add(choice);
        }
        menu.Items.Add(language);
        menu.IsOpen = true;
    }

    private void ListTab_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var p = TabProtocol(sender);
        var menu = ((FrameworkElement)sender).ContextMenu!;
        menu.Items.Clear();
        menu.Items.Add(Item($"New {Kind(p)} session", "", () => NewSession(p), color: BrushFor(p)));
        menu.Items.Add(Item($"New {Kind(p)} folder", "", () => NewFolder(_store.RootFor(p)), color: "FolderBrush"));
        menu.Items.Add(new Separator());
        AddTabChoices(menu);
    }

    private static string BrushFor(Protocol p) => p switch { Protocol.Ssh => "SshBrush", Protocol.Vnc => "VncBrush", Protocol.Web => "WebBrush", _ => "RdpBrush" };
    private static string GlyphFor(Protocol p) => p switch { Protocol.Ssh => "\uE756", Protocol.Vnc => "\uE8AF", Protocol.Web => "\uE774", _ => "\uE7F4" };

    /// <summary>Selects a node, switching to (and if needed showing) its tab and opening its folders.</summary>
    private void Select(Node node)
    {
        var p = node.Protocol;
        if (_store.Data.HiddenTabs.Remove(p)) UpdateListTabs();
        if (_store.Data.ActiveTab != p) ShowTab(p);
        ClearPicks();
        for (var f = node.Parent; f != null; f = f.Parent) f.IsExpanded = true;
        node.IsSelected = true;
    }

    // ───────────── Multiple selection (Ctrl+click, Shift+click, Ctrl+A) ─────────────

    private void ClearPicks()
    {
        foreach (var n in _picked) n.IsPicked = false;
        _picked.Clear();
    }

    private void SetPicked(Node n, bool on)
    {
        if (n.IsPicked == on) return;
        n.IsPicked = on;
        if (on) _picked.Add(n); else _picked.Remove(n);
    }

    /// <summary>The rows shown in the list, top to bottom (expanded folders and filter matches).</summary>
    private static IEnumerable<Node> VisibleRows(Node folder)
    {
        foreach (var c in folder.Children.Where(c => c.IsVisible))
        {
            yield return c;
            if (c.IsFolder && c.IsExpanded)
                foreach (var d in VisibleRows(c)) yield return d;
        }
    }

    /// <summary>What an action applies to: the picked items in list order, or else the selected one.</summary>
    private List<Node> Targets()
    {
        if (_picked.Count > 0)
        {
            var order = ActiveRoot.Descendants().Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
            return _picked.Where(order.ContainsKey).OrderBy(n => order[n]).ToList();
        }
        return SelectedNode is { IsRoot: false } n ? new List<Node> { n } : new List<Node>();
    }

    /// <summary>Drops items whose folder is also in the list (deleting or moving the folder covers them).</summary>
    private static List<Node> Outermost(List<Node> nodes) =>
        nodes.Where(n => !nodes.Any(o => o != n && o.IsAncestorOf(n))).ToList();

    private void PickRange(Node from, Node to, bool add)
    {
        var rows = VisibleRows(ActiveRoot).ToList();
        int a = rows.IndexOf(from), b = rows.IndexOf(to);
        if (a < 0) a = b;
        if (!add) ClearPicks();
        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) SetPicked(rows[i], true);
    }

    private static TreeViewItem? ContainerFrom(object source)
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ToggleButton) return null; // the expander arrow
            if (d is TreeViewItem item) return item;
        }
        return null;
    }

    private void FocusRow(Node node)
    {
        node.IsSelected = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (FindContainer(Tree, node) is { } item) item.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private static TreeViewItem? FindContainer(ItemsControl parent, Node node)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem c) continue;
            if (item == node) return c;
            if (c.IsExpanded && FindContainer(c, node) is { } found) return found;
        }
        return null;
    }

    // ───────────── Mouse, keyboard, drag and drop ─────────────

    private static Node? NodeFrom(object source)
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TreeViewItem item) return item.DataContext as Node;
        return null;
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ModifierState() != ModifierKeys.None) return;
        if (NodeFrom(e.OriginalSource) is { IsSession: true } node)
        {
            OpenSession(node);
            e.Handled = true;
        }
    }

    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-click on a picked item keeps the multiple selection; elsewhere it selects the item under the mouse.
        var node = NodeFrom(e.OriginalSource);
        if (node is { IsPicked: true } && _picked.Count > 1) return;
        ClearPicks();
        if (node != null) node.IsSelected = true;
        else if (SelectedNode is { } sel) sel.IsSelected = false;
    }

    private void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Tree);
        _dragCandidate = NodeFrom(e.OriginalSource);
        _clearPicksOnUp = null;
        var item = ContainerFrom(e.OriginalSource);
        if (item?.DataContext is not Node node) return;

        var mods = ModifierState();
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        if (ctrl || shift)
        {
            // Start the multiple selection from the item that was selected before.
            if (_picked.Count == 0 && SelectedNode is { } current) SetPicked(current, true);
            if (shift)
            {
                PickRange(_anchor ?? SelectedNode ?? node, node, add: ctrl);
            }
            else
            {
                SetPicked(node, !node.IsPicked);
                _anchor = node;
            }
            if (node.IsPicked) FocusRow(node);
            else
            {
                if (node.IsSelected) node.IsSelected = false;
                if (_picked.LastOrDefault() is { } last) FocusRow(last);
            }
            _dragCandidate = null;
            e.Handled = true;
            return;
        }

        _anchor = node;
        // A plain click on a picked item may start dragging them all; it selects just that item if no drag follows.
        if (node.IsPicked && _picked.Count > 1) _clearPicksOnUp = node;
        else ClearPicks();
    }

    private void Tree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_clearPicksOnUp is { } node)
        {
            _clearPicksOnUp = null;
            ClearPicks();
            node.IsSelected = true;
        }
    }

    private void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = ModifierState();
        if (e.Key == Key.A && mods == ModifierKeys.Control)
        {
            ClearPicks();
            foreach (var n in VisibleRows(ActiveRoot)) SetPicked(n, true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _picked.Count > 0)
        {
            ClearPicks();
            e.Handled = true;
        }
        else if (e.Key == Key.System && e.SystemKey is Key.Up or Key.Down && mods == ModifierKeys.Alt)
        {
            MoveSelected(e.SystemKey == Key.Up ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown && mods == ModifierKeys.Shift
                 && SelectedNode is { } from)
        {
            // Shift+arrows extend the selection like in Explorer.
            var rows = VisibleRows(ActiveRoot).ToList();
            int i = rows.IndexOf(from);
            if (i < 0) return;
            int j = e.Key switch
            {
                Key.Up => i - 1, Key.Down => i + 1, Key.Home => 0, Key.End => rows.Count - 1,
                Key.PageUp => i - 10, _ => i + 10,
            };
            j = Math.Clamp(j, 0, rows.Count - 1);
            _anchor ??= from;
            PickRange(_anchor, rows[j], add: false);
            FocusRow(rows[j]);
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            ClearPicks();
            _anchor = null;
        }
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        var targets = Targets();
        if (targets.Count == 0) return;
        switch (e.Key)
        {
            case Key.Enter when targets.Any(n => n.IsSession):
                foreach (var n in targets.Where(n => n.IsSession)) OpenSession(n);
                e.Handled = true;
                break;
            case Key.F2 when targets.Count == 1: Rename(targets[0]); e.Handled = true; break;
            case Key.Delete: Delete(targets); e.Handled = true; break;
        }
    }

    private void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not { IsRoot: false } node) return;
        var pos = e.GetPosition(Tree);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragCandidate = null;
        _clearPicksOnUp = null;
        var dragged = node.IsPicked && _picked.Count > 1 ? Outermost(Targets()) : new List<Node> { node };
        DragDrop.DoDragDrop(Tree, new DataObject(typeof(Node[]), dragged.ToArray()), DragDropEffects.Move);
    }

    /// <summary>
    /// Where dropped items go. Over the middle of a folder: into it. Over the top or bottom part of a row: before or after
    /// it, in the same folder (Index is set; this arranges the list by hand). Over empty space: the end of the tab.
    /// </summary>
    internal sealed record DropPlan(Node Folder, int? Index, FrameworkElement? Row, bool LineBelow);

    private static FrameworkElement RowOf(TreeViewItem item) =>
        item.Template?.FindName("Bd", item) as FrameworkElement ?? item;

    /// <summary>The innermost row under the mouse, including over the expander arrow.</summary>
    private static TreeViewItem? RowItemFrom(object source)
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TreeViewItem item) return item;
        return null;
    }

    private DropPlan? PlanDrop(object source, Func<IInputElement, Point> position, Node[] dragged)
    {
        if (dragged.Length == 0) return null;
        DropPlan plan;
        var item = RowItemFrom(source);
        if (item?.DataContext is not Node node)
        {
            var root = ActiveRoot;
            plan = new DropPlan(root, _store.Data.ManualOrder ? root.Children.Count : null, null, false);
        }
        else
        {
            var row = RowOf(item);
            double y = position(row).Y, h = Math.Max(1, row.ActualHeight);
            if (node.IsFolder && y >= h * 0.25 && y <= h * 0.75)
                plan = new DropPlan(node, null, row, false);
            else if (y < h / 2)
                plan = new DropPlan(node.Parent!, node.Parent!.Children.IndexOf(node), row, false);
            else if (node.IsFolder && node.IsExpanded && node.Children.Any(c => c.IsVisible))
                plan = new DropPlan(node, 0, row, true); // just below an open folder's name: the top of that folder
            else
                plan = new DropPlan(node.Parent!, node.Parent!.Children.IndexOf(node) + 1, row, true);
            if (plan.Index != null && dragged.Contains(node)) return null; // onto itself
        }
        var folder = plan.Folder;
        if (dragged.Any(d => d.Protocol != folder.Protocol || d == folder || d.IsAncestorOf(folder))) return null;
        if (plan.Index == null && dragged.All(d => d.Parent == folder)) return null; // already in that folder
        return plan;
    }

    private DropPlan? PlanDrop(DragEventArgs e, out Node[] dragged)
    {
        dragged = e.Data.GetData(typeof(Node[])) as Node[] ?? Array.Empty<Node>();
        return PlanDrop(e.OriginalSource, e.GetPosition, dragged);
    }

    private void ShowDropMarker(DropPlan? plan)
    {
        DropLine.Visibility = DropBox.Visibility = Visibility.Collapsed;
        if (plan?.Row is not { } row || !row.IsVisible) return;
        var at = row.TranslatePoint(new Point(0, 0), DropLayer);
        if (plan.Index == null)
        {
            Canvas.SetLeft(DropBox, at.X);
            Canvas.SetTop(DropBox, at.Y);
            DropBox.Width = row.ActualWidth;
            DropBox.Height = row.ActualHeight;
            DropBox.Visibility = Visibility.Visible;
        }
        else
        {
            Canvas.SetLeft(DropLine, at.X + (plan.LineBelow && plan.Index == 0 && plan.Folder == (row.DataContext as Node) ? 18 : 0));
            Canvas.SetTop(DropLine, at.Y + (plan.LineBelow ? row.ActualHeight : 0) - 1);
            DropLine.Width = Math.Max(0, row.ActualWidth - (plan.LineBelow && plan.Index == 0 ? 18 : 0));
            DropLine.Visibility = Visibility.Visible;
        }
    }

    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        var plan = PlanDrop(e, out _);
        e.Effects = plan != null ? DragDropEffects.Move : DragDropEffects.None;
        ShowDropMarker(plan);
        e.Handled = true;
    }

    private void Tree_DragLeave(object sender, DragEventArgs e) => ShowDropMarker(null);

    private void Tree_Drop(object sender, DragEventArgs e)
    {
        ShowDropMarker(null);
        var plan = PlanDrop(e, out var dragged);
        if (plan != null) Drop(plan, dragged);
    }

    private void Drop(DropPlan plan, IReadOnlyList<Node> dragged)
    {
        ClearPicks();
        if (plan.Index is int index)
        {
            UseManualOrder();
            _store.MoveTo(dragged, plan.Folder, index);
            plan.Folder.IsExpanded = true;
        }
        else
        {
            foreach (var d in dragged.Where(d => d.Parent != plan.Folder))
            {
                _store.Remove(d);
                _store.AddChild(plan.Folder, d);
            }
        }
        if (dragged.Count == 1) Select(dragged[0]);
        else foreach (var d in dragged) SetPicked(d, true);
        Save();
    }

    /// <summary>Moving items by hand switches off A to Z sorting (once, with a note on how to get it back).</summary>
    private void UseManualOrder()
    {
        if (_store.Data.ManualOrder) return;
        _store.Data.ManualOrder = true;
        Dispatcher.BeginInvoke(() => Note(this, 
            "The list now keeps the order you arrange by dragging.\n\n" +
            "To sort it by name again, right-click an empty part of the list and choose \"Keep sorted A to Z\"."),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SetSortedByName(bool sorted)
    {
        _store.Data.ManualOrder = !sorted;
        if (sorted) _store.SortAll();
        Save();
    }

    /// <summary>Alt+Up / Alt+Down moves the selected item one place up or down within its folder.</summary>
    private void MoveSelected(int step)
    {
        if (Targets() is not [var node] || node.Parent is not { } folder) return;
        int i = folder.Children.IndexOf(node), j = i + step;
        if (j < 0 || j >= folder.Children.Count) return;
        UseManualOrder();
        _store.MoveTo(new[] { node }, folder, step > 0 ? j + 1 : j);
        FocusRow(node);
        Save();
    }

    private void FavoritesOnly_Click(object sender, RoutedEventArgs e)
    {
        bool on = FavoritesOnly.IsChecked == true;
        FavoritesOnlyStar.Text = on ? "\uE735" : "\uE734"; // filled / outline star
        FavoritesOnlyStar.SetResourceReference(TextBlock.ForegroundProperty, on ? "FolderBrush" : "SubTextBrush");
        FilterBox_TextChanged(FilterBox, null!);
    }

    private void SetFavorite(IEnumerable<Node> nodes, bool on)
    {
        foreach (var n in nodes.Where(n => n.IsSession)) n.IsFavorite = on;
        if (FavoritesOnly.IsChecked == true) FilterBox_TextChanged(FilterBox, null!);
        Save();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = FilterBox.Text.Trim();
        bool favoritesOnly = FavoritesOnly.IsChecked == true;
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearPicks(); // never act on rows the filter hides
        foreach (var root in _store.Data.Roots)
        {
            ApplyFilter(root, text, favoritesOnly);
            root.IsVisible = true;
        }
        UpdateListTabs();
    }

    /// <summary>Shows what matches the text (a folder that matches shows all of its items) and, if asked, only favourites.</summary>
    private static bool ApplyFilter(Node node, string text, bool favoritesOnly = false)
    {
        bool textMatch = text.Length == 0
            || node.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
            || node.Subtitle.Contains(text, StringComparison.CurrentCultureIgnoreCase);
        bool selfMatch = textMatch && (!favoritesOnly || node.IsFavorite);
        bool childMatch = false;
        foreach (var c in node.Children)
            childMatch |= ApplyFilter(c, textMatch && node.IsFolder && !node.IsRoot ? "" : text, favoritesOnly);
        node.IsVisible = selfMatch || childMatch;
        if (text.Length > 0 && childMatch) node.IsExpanded = true;
        return node.IsVisible;
    }
}
