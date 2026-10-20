using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SessionManager;

/// <summary>
/// The main window's session list: SSH / RDP / VNC tabs, hiding tabs, Ctrl/Shift+click selection and deleting
/// several items. Runs in a child process with its own data folder (never the real sessions.json); the window is
/// placed off-screen and rendered to %TEMP%\sm-list-wpf.png. Clicks and keys are raised as WPF events, not as
/// real input.
/// </summary>
static class SessionListTests
{
    public const string Flag = "--list-wpf";

    public static void RunIsolated(Action<string, bool, string> check)
    {
        var data = Path.Combine(Path.GetTempPath(), "sm-list-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(data);
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, Flag) { RedirectStandardOutput = true, UseShellExecute = false };
        psi.Environment["SESSIONMANAGER_DATA"] = data;
        using var child = System.Diagnostics.Process.Start(psi)!;
        int reported = 0;
        bool done = false;
        var until = DateTime.UtcNow.AddSeconds(90);
        while (!done && DateTime.UtcNow < until && child.StandardOutput.ReadLine() is { } line)
        {
            if (line == "DONE") { done = true; break; }
            if (!line.StartsWith("PASS  ") && !line.StartsWith("FAIL  ")) continue;
            reported++;
            var text = line[6..];
            int arrow = text.IndexOf("  -> ", StringComparison.Ordinal);
            check(arrow < 0 ? text : text[..arrow], line.StartsWith("PASS"), arrow < 0 ? "" : text[(arrow + 5)..]);
        }
        if (!done) check("Session list (WPF) finishes", false, $"{reported} results before it stopped");
        try { child.Kill(); child.WaitForExit(5000); } catch { }
        try { Directory.Delete(data, true); } catch { }
    }

    public static void Run(Action<string, bool, string> check)
    {
        if (!SessionStore.DataDir.Contains("sm-list-test-"))
        {
            check("Session list (WPF) uses a test data folder", false, SessionStore.DataDir);
            return;
        }
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var app = new App();
            app.InitializeComponent();
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try { await Body(check); }
                catch (Exception ex) { error = ex; }
                finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));
        if (error != null) check("Session list (WPF)", false, error.ToString());
    }

    private static Node Session(string name, Protocol p) =>
        new() { Kind = NodeKind.Session, Name = name, Protocol = p, Settings = new SessionSettings { Host = name.ToLowerInvariant() + ".lan", Port = SessionSettings.DefaultPort(p) } };

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static T Get<T>(object o, string name) =>
        (T)(o.GetType().GetField(name, Any)?.GetValue(o) ?? o.GetType().GetProperty(name, Any)!.GetValue(o)!);
    private static object? Call(object o, string name, params object?[] args) =>
        o.GetType().GetMethods(Any).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(o, args);

    private static async Task WaitUntil(Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(50);
    }

    private static object? FindInTemplate(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var n } fe && n == name) return fe;
            if (FindInTemplate(child, name) is { } found) return found;
        }
        return null;
    }

    private static async Task Settle()
    {
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task Body(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);
        // A list like a real one: an SSH folder with two sessions, three SSH sessions, one RDP session, no VNC.
        var store = new SessionStore();
        store.Load();
        store.Data.Ui.Animations = false;
        var prod = new Node { Kind = NodeKind.Folder, Name = "Prod", IsExpanded = false };
        store.AddChild(store.SshRoot, prod);
        store.AddChild(prod, Session("Api", Protocol.Ssh));
        store.AddChild(prod, Session("Db", Protocol.Ssh));
        var alpha = Session("Alpha", Protocol.Ssh);
        var bravo = Session("Bravo", Protocol.Ssh);
        var charlie = Session("Charlie", Protocol.Ssh);
        foreach (var n in new[] { alpha, bravo, charlie }) store.AddChild(store.SshRoot, n);
        var desk = Session("Desk", Protocol.Rdp);
        store.AddChild(store.RdpRoot, desk);
        prod.IsExpanded = false;
        store.Save();

        // Ctrl/Shift state and the delete question are replaced; nothing touches the real keyboard or shows a dialog.
        var modifiers = ModifierKeys.None;
        typeof(MainWindow).GetField("ModifierState", Any)!.SetValue(null, (Func<ModifierKeys>)(() => modifiers));
        string? asked = null;
        typeof(MainWindow).GetField("AskDelete", Any)!.SetValue(null, (Func<Window, string, bool>)((_, q) => { asked = q; return true; }));

        var w = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 1000, Height = 640,
            ShowActivated = false, ShowInTaskbar = false,
        };
        w.Show();
        await Settle();

        var tree = Get<TreeView>(w, "Tree");
        var root = (Func<Protocol, Node>)(p => Get<SessionStore>(w, "_store").RootFor(p));
        // The window loaded its own copy of the list; use its nodes.
        Node Named(string name) => Enum.GetValues<Protocol>().SelectMany(p => root(p).Descendants()).First(n => n.Name == name);
        (prod, alpha, bravo, charlie, desk) = (Named("Prod"), Named("Alpha"), Named("Bravo"), Named("Charlie"), Named("Desk"));

        // ── Tabs ──
        check("list shows the SSH tab's items (no group rows)", ReferenceEquals(tree.ItemsSource, root(Protocol.Ssh).Children) && tree.Items.Count == 4,
            $"{tree.Items.Count} rows");
        check("tab shows the SSH session count", Get<TextBlock>(w, "SshCount").Text == "5", Get<TextBlock>(w, "SshCount").Text);

        // A narrow sidebar shows only the tab icons; widening it brings the names back.
        var listTabs = Get<System.Windows.Controls.Primitives.UniformGrid>(w, "ListTabs");
        DependencyObject up = listTabs;
        while (up is not Grid { ColumnDefinitions.Count: 3 }) up = VisualTreeHelper.GetParent(up);
        var sidebar = ((Grid)up).ColumnDefinitions;
        bool Named_() => Get<TextBlock>(w, "SshLabel").Visibility == Visibility.Visible && Get<TextBlock>(w, "SshCount").Visibility == Visibility.Visible;
        check("a wide sidebar shows the tab names", Named_(), $"{listTabs.ActualWidth:0}");
        var sidebarWidth = sidebar[0].Width;
        sidebar[0].Width = new GridLength(170);
        await Settle();
        check("a narrow sidebar shows only the tab icons", !Named_() && Get<TextBlock>(w, "WebLabel").Visibility == Visibility.Collapsed
            && Get<RadioButton>(w, "SshTab").ToolTip is string t && t.Contains("SSH"), $"{listTabs.ActualWidth:0}");
        sidebar[0].Width = sidebarWidth;
        await Settle();
        check("widening the sidebar brings the names back", Named_(), $"{listTabs.ActualWidth:0}");
        Get<RadioButton>(w, "RdpTab").IsChecked = true;
        await Settle();
        check("RDP tab switches the list", ReferenceEquals(tree.ItemsSource, root(Protocol.Rdp).Children) && tree.Items.Count == 1);
        Get<RadioButton>(w, "VncTab").IsChecked = true;
        await Settle();
        check("empty VNC tab shows a hint", Get<TextBlock>(w, "EmptyTabHint").Visibility == Visibility.Visible,
            Get<TextBlock>(w, "EmptyTabHint").Text);

        Call(w, "SetTabHidden", Protocol.Vnc, true);
        await Settle();
        check("hiding the shown tab hides it and switches to another",
            Get<RadioButton>(w, "VncTab").Visibility == Visibility.Collapsed && Get<RadioButton>(w, "SshTab").IsChecked == true
            && ReferenceEquals(tree.ItemsSource, root(Protocol.Ssh).Children));
        Call(w, "SetTabHidden", Protocol.Rdp, true);
        Call(w, "SetTabHidden", Protocol.Web, true);
        Call(w, "SetTabHidden", Protocol.Ssh, true);
        check("the last visible tab can't be hidden", Get<RadioButton>(w, "SshTab").Visibility == Visibility.Visible
            && Get<RadioButton>(w, "RdpTab").Visibility == Visibility.Collapsed);
        var saved = File.ReadAllText(Path.Combine(SessionStore.DataDir, "sessions.json"));
        check("hidden tabs are saved", saved.Contains("\"HiddenTabs\"") && saved.Contains("\"Rdp\"") && saved.Contains("\"Vnc\""));
        Call(w, "Select", desk);
        await Settle();
        check("selecting a session of a hidden tab shows that tab again",
            Get<RadioButton>(w, "RdpTab").Visibility == Visibility.Visible && Get<RadioButton>(w, "RdpTab").IsChecked == true && desk.IsSelected);
        Call(w, "SetTabHidden", Protocol.Rdp, false);
        Call(w, "SetTabHidden", Protocol.Vnc, false);
        Call(w, "SetTabHidden", Protocol.Web, false);
        Get<RadioButton>(w, "SshTab").IsChecked = true;
        await Settle();

        // ── Ctrl+click / Shift+click ──
        TreeViewItem Row(Node n)
        {
            var item = (TreeViewItem?)Call(w, "FindContainer", tree, n) ?? throw new Exception($"no row for {n.Name}: window {w.ActualWidth}x{w.ActualHeight} visible={w.IsVisible} tree {tree.ActualWidth}x{tree.ActualHeight} items={tree.Items.Count} gen={tree.ItemContainerGenerator.Status} c0={tree.ItemContainerGenerator.ContainerFromIndex(0)}");
            return item;
        }
        async Task Click(Node n, ModifierKeys mods)
        {
            modifiers = mods;
            var row = Row(n);
            var target = (FrameworkElement)row.Template.FindName("Bd", row);
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = target };
            target.RaiseEvent(down);
            if (!down.Handled) n.IsSelected = true; // what the TreeView itself does on a plain click
            target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseUpEvent, Source = target });
            modifiers = ModifierKeys.None;
            await Settle();
        }
        List<Node> Targets() => (List<Node>)Call(w, "Targets")!;
        string Names(IEnumerable<Node> ns) => string.Join(",", ns.Select(n => n.Name));

        await Click(alpha, ModifierKeys.None);
        check("plain click selects one item", Names(Targets()) == "Alpha", Names(Targets()));
        await Click(charlie, ModifierKeys.Control);
        check("Ctrl+click adds an item", Names(Targets()) == "Alpha,Charlie" && alpha.IsPicked && charlie.IsPicked && !bravo.IsPicked, Names(Targets()));
        await Click(alpha, ModifierKeys.Control);
        check("Ctrl+click on a picked item removes it", Names(Targets()) == "Charlie", Names(Targets()));
        await Click(prod, ModifierKeys.Shift);
        check("Shift+click picks the range from the last clicked item", Names(Targets()) == "Prod,Alpha", Names(Targets()));
        await Click(bravo, ModifierKeys.None);
        check("plain click clears the multiple selection", Names(Targets()) == "Bravo" && !charlie.IsPicked, Names(Targets()));

        // Context menu for several items.
        await Click(alpha, ModifierKeys.Control);
        Call(w, "Tree_ContextMenuOpening", tree, null);
        var headers = tree.ContextMenu!.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
        check("menu for several items offers Execute and Delete", headers.Contains("Execute 2 sessions") && headers.Contains("Delete 2 items"),
            string.Join(" | ", headers));

        // ── Delete several (Del key) ──
        await Click(prod, ModifierKeys.Control);
        modifiers = ModifierKeys.None;
        tree.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(tree)!, 0, Key.Delete) { RoutedEvent = Keyboard.KeyDownEvent });
        await Settle();
        check("Del deletes all picked items after one question",
            asked != null && asked.Contains("2 sessions and 1 folder (with 2 sessions in them)")
            && Names(root(Protocol.Ssh).Children) == "Charlie", $"{asked?.Replace("\n", " / ")} => {Names(root(Protocol.Ssh).Children)}");
        var reloaded = new SessionStore();
        reloaded.Load();
        check("deletion is saved", Names(reloaded.SshRoot.Children) == "Charlie" && reloaded.RdpRoot.Children.Count == 1);

        // Ctrl+A picks everything in the tab; the empty-space menu can delete the rest.
        modifiers = ModifierKeys.Control;
        tree.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(tree)!, 0, Key.A) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        modifiers = ModifierKeys.None;
        check("Ctrl+A picks every row in the tab", Names(Targets()) == "Charlie", Names(Targets()));
        Call(w, "ClearPicks");

        // ── Arranging by hand: dropping above / below a row or onto a folder ──
        int notes = 0;
        typeof(MainWindow).GetField("Note", Any)!.SetValue(null, (Action<Window, string>)((_, _) => notes++));
        var st = Get<SessionStore>(w, "_store");
        var sshRoot = root(Protocol.Ssh);
        var lab = new Node { Kind = NodeKind.Folder, Name = "Lab", IsExpanded = true };
        st.AddChild(sshRoot, lab);
        st.AddChild(lab, Session("Inner", Protocol.Ssh));
        foreach (var nm in new[] { "Delta", "Echo" }) st.AddChild(sshRoot, Session(nm, Protocol.Ssh));
        await Settle();
        check("new items are sorted while the list is A to Z", Names(sshRoot.Children) == "Lab,Charlie,Delta,Echo", Names(sshRoot.Children));
        object? Plan(Node over, double fraction, params Node[] dragged)
        {
            var row = (FrameworkElement)Call(w, "RowOf", Row(over))!;
            return Call(w, "PlanDrop", row, (Func<IInputElement, Point>)(_ => new Point(20, row.ActualHeight * fraction)), dragged);
        }
        async Task DropOn(Node over, double fraction, params Node[] dragged)
        {
            var plan = Plan(over, fraction, dragged) ?? throw new Exception($"no drop for {Names(dragged)} on {over.Name}");
            Call(w, "Drop", plan, (IReadOnlyList<Node>)dragged);
            await Settle();
        }
        var echo = Named("Echo");
        var plan1 = Plan(charlie, 0.1, echo);
        Call(w, "ShowDropMarker", plan1);
        check("top of a row: a line above it", plan1 != null && Get<Node>(plan1, "Folder") == sshRoot && Get<int?>(plan1, "Index") == 1
            && Get<Border>(w, "DropLine").Visibility == Visibility.Visible && Get<Border>(w, "DropBox").Visibility == Visibility.Collapsed);
        await DropOn(charlie, 0.1, echo);
        check("dropping above a row puts it there and keeps your own order", Names(sshRoot.Children) == "Lab,Echo,Charlie,Delta" && st.Data.ManualOrder && notes == 1,
            $"{Names(sshRoot.Children)} manual={st.Data.ManualOrder} notes={notes}");
        var plan2 = Plan(lab, 0.5, Named("Delta"));
        Call(w, "ShowDropMarker", plan2);
        check("middle of a folder: into it, with a frame", plan2 != null && Get<Node>(plan2, "Folder") == lab && Get<int?>(plan2, "Index") == null
            && Get<Border>(w, "DropBox").Visibility == Visibility.Visible);
        await DropOn(lab, 0.5, Named("Delta"));
        check("dropping onto a folder moves it inside (at the end)", Names(lab.Children) == "Inner,Delta", Names(lab.Children));
        await DropOn(lab, 0.9, charlie);
        check("just below an open folder's name: the top of that folder", Names(lab.Children) == "Charlie,Inner,Delta", Names(lab.Children));
        check("dropping on itself does nothing", Plan(echo, 0.2, echo) == null);
        check("a folder can't go inside itself", Plan(Named("Inner"), 0.2, lab) == null);
        await DropOn(Named("Delta"), 0.9, Named("Inner"));
        check("below a row inside a folder", Names(lab.Children) == "Charlie,Delta,Inner", Names(lab.Children));
        Call(w, "Select", echo);
        Call(w, "MoveSelected", -1);
        await Settle();
        check("Alt+Up moves the selected item up", Names(sshRoot.Children) == "Echo,Lab" && notes == 1, Names(sshRoot.Children));
        reloaded = new SessionStore();
        reloaded.Load();
        check("your own order is saved", reloaded.Data.ManualOrder && Names(reloaded.SshRoot.Children) == "Echo,Lab"
            && Names(reloaded.SshRoot.Children[1].Children) == "Charlie,Delta,Inner", Names(reloaded.SshRoot.Children));
        Call(w, "Select", echo);
        echo.IsSelected = false;
        Call(w, "Tree_ContextMenuOpening", tree, null);
        var keep = tree.ContextMenu!.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header?.ToString() == "Keep sorted A to Z");
        check("the list menu has 'Keep sorted A to Z' (off now)", keep is { IsCheckable: true, IsChecked: false },
            string.Join(" | ", tree.ContextMenu.Items.OfType<MenuItem>().Select(m => m.Header)));
        keep?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Settle();
        check("'Keep sorted A to Z' sorts again", !st.Data.ManualOrder && Names(sshRoot.Children) == "Lab,Echo" && Names(lab.Children) == "Charlie,Delta,Inner",
            Names(sshRoot.Children));

        // ── Favourites ──
        var delta = Named("Delta");
        Call(w, "SetFavorite", new[] { echo, delta }, true);
        var favOnly = Get<System.Windows.Controls.Primitives.ToggleButton>(w, "FavoritesOnly");
        favOnly.IsChecked = true;
        Call(w, "FavoritesOnly_Click", favOnly, new RoutedEventArgs());
        await Settle();
        bool Shown(Node n) => n.IsVisible && (n.Parent == null || n.Parent.IsRoot || Shown(n.Parent));
        check("the star shows only favourites (and the folders they're in)", Shown(echo) && Shown(delta) && Shown(lab) && !Shown(charlie) && !Shown(Named("Inner")),
            $"echo={Shown(echo)} delta={Shown(delta)} charlie={Shown(charlie)}");
        check("the tab counts favourites while the star is on", Get<TextBlock>(w, "SshCount").Text == "2", Get<TextBlock>(w, "SshCount").Text);
        Get<RadioButton>(w, "RdpTab").IsChecked = true;
        await Settle();
        check("a tab without favourites says so", Get<TextBlock>(w, "EmptyTabHint").Text.Contains("favourites"), Get<TextBlock>(w, "EmptyTabHint").Text);
        Get<RadioButton>(w, "SshTab").IsChecked = true;
        favOnly.IsChecked = false;
        Call(w, "FavoritesOnly_Click", favOnly, new RoutedEventArgs());
        await Settle();
        check("turning the star off shows everything again", Shown(charlie) && Shown(Named("Inner")));
        reloaded = new SessionStore();
        reloaded.Load();
        check("favourites are saved", reloaded.SshRoot.Descendants().Where(n => n.IsFavorite).Select(n => n.Name).OrderBy(x => x).SequenceEqual(new[] { "Delta", "Echo" }));

        // ── Session menu: everyday actions first, the rest under More ──
        Call(w, "ClearPicks");
        Call(w, "Select", charlie);
        Call(w, "Tree_ContextMenuOpening", tree, null);
        var items = tree.ContextMenu!.Items.OfType<MenuItem>().ToList();
        var top = items.Select(m => m.Header?.ToString()).ToList();
        var more = items.FirstOrDefault(m => m.Header?.ToString() == "More");
        var moreHeaders = more?.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList() ?? new();
        check("session menu: everyday actions on top, the rest in More", top.Contains("Add to favourites") && top.Contains("Edit session") && top.Contains("Delete session")
            && new[] { "Rename session", "Duplicate session", "Change password...", "Ping host", "Wake up (Wake-on-LAN)...", "Create a desktop shortcut" }.All(h => moreHeaders.Contains(h))
            && !new[] { "Rename session", "Duplicate session", "Change password...", "Ping host", "Wake up (Wake-on-LAN)...", "Create a desktop shortcut" }.Any(top.Contains)
            && top.Count <= 8, string.Join(" | ", top) + "  //  " + string.Join(" | ", moreHeaders));

        // The More entry really opens its submenu (the menu template needs a popup for it), and checked items show a tick.
        more!.ApplyTemplate();
        var subPopup = more.Template.FindName("SubPopup", more) as System.Windows.Controls.Primitives.Popup;
        check("More has a submenu popup with its entries", subPopup != null && more.Items.Count >= 5 && more.Template.FindName("Arrow", more) is TextBlock { Visibility: Visibility.Visible },
            $"popup={subPopup != null} open={subPopup?.IsOpen} sub={more.IsSubmenuOpen} items={more.Items.Count} arrow={(more.Template.FindName("Arrow", more) as TextBlock)?.Visibility} role={more.Role}");
        var tickItem = new MenuItem { Header = "x", IsCheckable = true, IsChecked = true };
        tickItem.ApplyTemplate();
        check("a checked menu item shows a tick", tickItem.Template.FindName("Tick", tickItem) is TextBlock { Visibility: Visibility.Visible });

        // ── The tabs option is one row of checkboxes ──
        var optionsMenu = new ContextMenu();
        Call(w, "AddTabChoices", optionsMenu);
        var tabRow = optionsMenu.Items.OfType<MenuItem>().Single();
        var boxes = ((StackPanel)tabRow.Header).Children.OfType<CheckBox>().ToList();
        check("tabs: one row with a checkbox per tab, all ticked", optionsMenu.Items.Count == 1 && boxes.Select(b => b.Content?.ToString()).SequenceEqual(new[] { "SSH", "RDP", "VNC", "Web" })
            && boxes.All(b => b.IsChecked == true));
        boxes[2].IsChecked = false; boxes[2].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        check("unticking a tab hides it", Get<RadioButton>(w, "VncTab").Visibility == Visibility.Collapsed && wStoreHidden());
        boxes[2].IsChecked = true; boxes[2].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        check("ticking it shows it again", Get<RadioButton>(w, "VncTab").Visibility == Visibility.Visible);
        bool wStoreHidden() => Get<SessionStore>(w, "_store").Data.HiddenTabs.Contains(Protocol.Vnc);

        // ── Connect as a user from the credentials list ──
        var wStore = Get<SessionStore>(w, "_store");
        check("no saved credentials: Connect as is the one-time prompt", top.Contains("Connect as...") && !items.Any(m => m.Header?.ToString() == "Connect as"), string.Join(" | ", top));
        var admin = new CredentialProfile { Name = "Admin", Username = "root", Domain = "" };
        var corp = new CredentialProfile { Name = "Corp", Username = "svc", Domain = "CORP" };
        wStore.Data.Credentials.AddRange(new[] { admin, corp });
        Call(w, "Tree_ContextMenuOpening", tree, null);
        var connectAs = tree.ContextMenu!.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header?.ToString() == "Connect as");
        var asHeaders = connectAs?.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList() ?? new();
        check("Connect as lists the credentials, then a one-time user", asHeaders.Count == 3 && asHeaders[0]!.StartsWith("Admin") && asHeaders[1]!.Contains(@"CORP\svc")
            && asHeaders[2] == "Another user, this time only...", string.Join(" | ", asHeaders));
        check("a session with its own login keeps Change password", tree.ContextMenu.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "More").Items.OfType<MenuItem>().Any(m => m.Header?.ToString() == "Change password..."));
        Call(w, "AssignProfile", charlie, admin);
        check("the chosen credential is assigned to the session", charlie.Settings!.CredentialId == admin.Id && charlie.Settings.Username == "root");
        reloaded = new SessionStore();
        reloaded.Load();
        check("and saved", reloaded.SshRoot.Descendants().First(n => n.Name == "Charlie").Settings!.CredentialId == admin.Id);
        Call(w, "Tree_ContextMenuOpening", tree, null);
        var afterItems = tree.ContextMenu!.Items.OfType<MenuItem>().ToList();
        check("a session using a credential has no Change password", !afterItems.First(m => m.Header?.ToString() == "More").Items.OfType<MenuItem>().Any(m => m.Header?.ToString() == "Change password..."),
            string.Join(" | ", afterItems.Select(m => m.Header)));
        check("the current credential is marked", afterItems.First(m => m.Header?.ToString() == "Connect as").Items.OfType<MenuItem>()
            .First(m => m.Header!.ToString()!.StartsWith("Admin")).Icon is TextBlock { Text: "" });
        charlie.Settings.CredentialId = null;
        wStore.Data.Credentials.Clear();
        wStore.Save();

        // ── Online dots ──
        typeof(MainWindow).GetField("Probe", Any)!.SetValue(null, (Func<string, int, CancellationToken, Task<(bool, int, string?)>>)((host, port, _) =>
            Task.FromResult(host == "echo.lan" ? (true, 7, (string?)null) : (false, 0, (string?)"no answer"))));
        Named("Inner").Settings!.JumpHostId = echo.Id;
        Call(w, "CheckReachability", true);
        await WaitUntil(() => echo.Reach != Reachability.Unknown && charlie.Reach != Reachability.Unknown);
        check("hosts that answer get a green dot, others a red one", echo.Reach == Reachability.Online && echo.ReachText.Contains("7 ms")
            && charlie.Reach == Reachability.Offline && charlie.ReachText.Contains("no answer"), $"{echo.Reach} {echo.ReachText} / {charlie.Reach}");
        check("sessions behind a jump host aren't checked directly", Named("Inner").Reach == Reachability.Unknown);
        var dot = (FrameworkElement?)FindInTemplate(Row(echo), "ReachDot");
        check("the dot is shown on the row", dot?.Visibility == Visibility.Visible, dot?.Visibility.ToString() ?? "no dot");
        Call(w, "SetShowStatus", false);
        check("turning the dots off clears them", echo.Reach == Reachability.Unknown && !st.Data.ShowStatus);
        Call(w, "SetShowStatus", true);
        Named("Inner").Settings!.JumpHostId = null;

        // ── Quick connect: recent sessions and search ──
        echo.LastUsed = DateTime.Now.AddMinutes(-5);
        charlie.LastUsed = DateTime.Now.AddMinutes(-1);
        desk.LastUsed = DateTime.Now.AddMinutes(-30);
        List<Node> Quick(string text) => (List<Node>)Call(w, "QuickMatches", text)!;
        check("empty quick connect lists recent sessions, newest first", Names(Quick("")) == "Charlie,Echo,Desk", Names(Quick("")));
        check("typing searches saved sessions by name", Names(Quick("ech")) == "Echo", Names(Quick("ech")));
        check("…and by address", Quick("desk.lan").FirstOrDefault() == desk, Names(Quick("desk.lan")));

        // ── Several sessions at once ──
        var change = new Dialogs.BulkChange(false, null, "deploy", null, 2222, false, null);
        Call(w, "ApplyToMany", new List<Node> { charlie, delta }, change);
        check("editing several sessions changes the ticked settings only", charlie.Settings!.Username == "deploy" && delta.Settings!.Port == 2222
            && charlie.Settings.Host == "charlie.lan" && echo.Settings!.Port == 22, $"{charlie.Settings.Username} {delta.Settings!.Port}");
        Call(w, "ApplyToMany", new List<Node> { charlie, delta }, new Dialogs.BulkChange(false, null, null, null, null, true, echo.Id));
        check("…including the jump host", charlie.Settings.JumpHostId == echo.Id && delta.Settings.JumpHostId == echo.Id);

        // ── Duplicates ──
        string? duplicateQuestion = null;
        typeof(MainWindow).GetField("AskDuplicate", Any)!.SetValue(null, (Func<Window, string, bool>)((_, q) => { duplicateQuestion = q; return false; }));
        var sameAddress = new SessionSettings { Host = "ECHO.lan", Port = 22, Username = "" };
        bool add = (bool)Call(w, "ConfirmNotDuplicate", Protocol.Ssh, sameAddress, null)!;
        check("a second session for the same address is asked about", !add && duplicateQuestion != null && duplicateQuestion.Contains("Echo") && echo.IsSelected,
            duplicateQuestion ?? "not asked");
        duplicateQuestion = null;
        check("another user on the same host is not a duplicate",
            (bool)Call(w, "ConfirmNotDuplicate", Protocol.Ssh, new SessionSettings { Host = "echo.lan", Port = 22, Username = "root" }, null)! && duplicateQuestion == null);

        // ── Saved commands ──
        check("a saved command is typed line by line and run with Enter",
            (string)Call(w, "CommandInput", new SavedCommand { Text = "cd /tmp\nls", Run = true })! == "cd /tmp\rls\r"
            && (string)Call(w, "CommandInput", new SavedCommand { Text = "sudo -i", Run = false })! == "sudo -i");

        // Render for a look (no screen capture).
        await Click(charlie, ModifierKeys.None);
        Get<RadioButton>(w, "SshTab").IsChecked = true;
        await Settle();
        w.UpdateLayout();
        var content = (FrameworkElement)w.Content;
        var bmp = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(content);
        var path = Path.Combine(Path.GetTempPath(), "sm-list-wpf.png");
        using (var f = File.Create(path))
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            enc.Save(f);
        }
        check("session list rendered", File.Exists(path), path);

        // ── Window icon follows the main and accent colors ──
        bool dark = Theme.IsDark;
        var ui = Theme.Ui.Clone();
        check("default colors draw the icon from the theme", w.Icon is DrawingImage && ReferenceEquals(w.Icon, AppIcon.Current), w.Icon?.GetType().Name ?? "null");
        var before = AppIcon.Current;
        Theme.Apply(!dark, ui);
        check("switching light/dark redraws the window icon", ReferenceEquals(w.Icon, AppIcon.Current) && !ReferenceEquals(w.Icon, before)
            && Theme.Hex(Theme.Parse(Theme.DefaultMain(!dark))) != Theme.Hex(Theme.Parse(Theme.DefaultMain(dark))));
        Theme.Apply(dark, ui);
        Theme.Apply(dark, new UiSettings { Main = "#7A1F3D", Accent = "#F5C542" });
        check("custom colors redraw the window icon", w.Icon is DrawingImage && ReferenceEquals(w.Icon, AppIcon.Current), w.Icon?.GetType().Name ?? "null");
        var probe = new Window { Content = new TextBlock() };
        Theme.ApplyTo(probe);
        check("dialogs get the colored icon too", ReferenceEquals(probe.Icon, AppIcon.Current));
        probe.Close();
        var same = AppIcon.Readable(Theme.Parse("#2A4378"), Theme.Parse("#2A4378"));
        check("an accent the same as the main color is made readable",
            Math.Abs(Theme.Luminance(same) - Theme.Luminance(Theme.Parse("#2A4378"))) >= 0.3, Theme.Hex(same));
        check("a readable accent is kept", Theme.Hex(AppIcon.Readable(Theme.Parse("#F5C542"), Theme.Parse("#7A1F3D"))) == "#F5C542");
        Theme.Apply(dark, ui);
        check("going back to default colors redraws the icon", w.Icon is DrawingImage && ReferenceEquals(w.Icon, AppIcon.Current));

        // Samples to look at: %TEMP%\sm-icon.png (default, a few main/accent pairs, and the 16 px title bar size).
        var pairs = new[] { ("#2A4378", "#5B8CFF"), ("#7A1F3D", "#F5C542"), ("#1F5A3A", "#4CC38A"), ("#AEB5BF", "#5A6472"), ("#E8E8E8", "#FFFFFF"), ("#000000", "#FF3B30") };
        var sheet = new DrawingVisual();
        using (var dc = sheet.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 80 * (pairs.Length + 1), 120));
            dc.DrawImage(BitmapFrame.Create(new Uri("pack://application:,,,/SessionManager;component/Assets/app.ico")), new Rect(8, 8, 64, 64));
            for (int i = 0; i < pairs.Length; i++)
            {
                var img = AppIcon.Draw(Theme.Parse(pairs[i].Item1), Theme.Parse(pairs[i].Item2));
                dc.DrawImage(img, new Rect(88 + 80 * i, 8, 64, 64));
                dc.DrawImage(img, new Rect(88 + 80 * i + 24, 88, 16, 16));
            }
        }
        var sheetBmp = new RenderTargetBitmap(80 * (pairs.Length + 1), 120, 96, 96, PixelFormats.Pbgra32);
        sheetBmp.Render(sheet);
        var iconPath = Path.Combine(Path.GetTempPath(), "sm-icon.png");
        using (var f = File.Create(iconPath))
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(sheetBmp));
            enc.Save(f);
        }

        w.Close();
    }
}
