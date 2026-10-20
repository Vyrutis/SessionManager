using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using SessionManager;

/// <summary>
/// The main window's session list: SSH / RDP / VNC tabs, hiding tabs, Ctrl/Shift+click selection and deleting several
/// items. Mouse and keys go through Avalonia's headless input (inside this process, not the real desktop).
/// </summary>
static class SessionListTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static T Get<T>(object o, string name) =>
        (T)(o.GetType().GetField(name, Any)?.GetValue(o) ?? o.GetType().GetProperty(name, Any)!.GetValue(o)!);
    private static object? Call(object o, string name, params object?[] args) =>
        o.GetType().GetMethods(Any).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(o, args);

    private static Node Session(string name, Protocol p) =>
        new() { Kind = NodeKind.Session, Name = name, Protocol = p, Settings = new SessionSettings { Host = name.ToLowerInvariant() + ".lan", Port = SessionSettings.DefaultPort(p) } };

    private static void Pump()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    public static void Run(Action<string, bool, string> report, string? shots)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var store = new SessionStore();
        store.Load();
        foreach (var r in store.Data.Roots) r.Children.Clear();
        store.Data.HiddenTabs.Clear();
        store.Data.ActiveTab = Protocol.Ssh;
        var prod = new Node { Kind = NodeKind.Folder, Name = "Prod" };
        store.AddChild(store.SshRoot, prod);
        store.AddChild(prod, Session("Api", Protocol.Ssh));
        store.AddChild(prod, Session("Db", Protocol.Ssh));
        prod.IsExpanded = false;
        foreach (var n in new[] { "Alpha", "Bravo", "Charlie" }) store.AddChild(store.SshRoot, Session(n, Protocol.Ssh));
        store.AddChild(store.RdpRoot, Session("Desk", Protocol.Rdp));
        store.Save();

        string? asked = null;
        typeof(MainWindow).GetField("AskDelete", Any)!.SetValue(null, (Func<Window, string, Task<bool>>)((_, q) => { asked = q; return Task.FromResult(true); }));

        var w = new MainWindow { Width = 1000, Height = 640 };
        w.Show();
        Pump();
        var tree = Get<TreeView>(w, "_tree");
        var winStore = Get<SessionStore>(w, "_store");
        Node Named(string name) => winStore.Data.Roots.SelectMany(r => r.Descendants()).First(n => n.Name == name);
        var tabs = Get<Dictionary<Protocol, Border>>(w, "_listTabs");
        var counts = Get<Dictionary<Protocol, TextBlock>>(w, "_listCounts");
        string Names(IEnumerable<Node> ns) => string.Join(",", ns.Select(n => n.Name));
        List<Node> Targets() => (List<Node>)Call(w, "Targets")!;

        void ClickAt(Control c, RawInputModifiers mods = RawInputModifiers.None, double x = -1)
        {
            var p = c.TranslatePoint(new Point(x < 0 ? c.Bounds.Width / 2 : x, Math.Min(12, c.Bounds.Height / 2)), w)!.Value;
            w.MouseDown(p, MouseButton.Left, mods);
            w.MouseUp(p, MouseButton.Left, mods);
            Pump();
        }
        void Click(Node n, RawInputModifiers mods = RawInputModifiers.None)
        {
            var item = tree.ContainerFromItem(n) as Control ?? throw new Exception("no row for " + n.Name);
            ClickAt(item, mods, 60);
        }

        // ── Tabs ──
        check("list shows the SSH tab's items (no group rows)",
            ReferenceEquals(tree.ItemsSource, winStore.SshRoot.Children) && tree.ItemCount == 4, $"{tree.ItemCount} rows");
        check("tab shows the SSH session count", counts[Protocol.Ssh].Text == "5", counts[Protocol.Ssh].Text ?? "");
        ClickAt(tabs[Protocol.Rdp]);
        check("clicking the RDP tab switches the list", ReferenceEquals(tree.ItemsSource, winStore.RdpRoot.Children) && tabs[Protocol.Rdp].Classes.Contains("active"));
        ClickAt(tabs[Protocol.Vnc]);
        var hint = Get<TextBlock>(w, "_emptyHint");
        check("empty VNC tab shows a hint", hint.IsVisible, hint.Text ?? "");

        Call(w, "SetTabHidden", Protocol.Vnc, true);
        Pump();
        check("hiding the shown tab hides it and switches to another",
            !tabs[Protocol.Vnc].IsVisible && tabs[Protocol.Ssh].Classes.Contains("active") && ReferenceEquals(tree.ItemsSource, winStore.SshRoot.Children));
        Call(w, "SetTabHidden", Protocol.Rdp, true);
        Call(w, "SetTabHidden", Protocol.Ssh, true);
        check("the last visible tab can't be hidden", tabs[Protocol.Ssh].IsVisible && !tabs[Protocol.Rdp].IsVisible);
        var reloaded = new SessionStore();
        reloaded.Load();
        check("hidden tabs are saved", reloaded.Data.HiddenTabs.Contains(Protocol.Rdp) && reloaded.Data.HiddenTabs.Contains(Protocol.Vnc));
        Call(w, "Select", Named("Desk"));
        Pump();
        check("selecting a session of a hidden tab shows that tab again",
            tabs[Protocol.Rdp].IsVisible && tabs[Protocol.Rdp].Classes.Contains("active") && Names(Targets()) == "Desk", Names(Targets()));
        Call(w, "SetTabHidden", Protocol.Rdp, false);
        Call(w, "SetTabHidden", Protocol.Vnc, false);
        ClickAt(tabs[Protocol.Ssh]);

        // ── Ctrl+click / Shift+click ──
        Click(Named("Alpha"));
        check("click selects one item", Names(Targets()) == "Alpha", Names(Targets()));
        Click(Named("Charlie"), RawInputModifiers.Control);
        check("Ctrl+click adds an item", Names(Targets()) == "Alpha,Charlie", Names(Targets()));
        Click(Named("Alpha"), RawInputModifiers.Control);
        check("Ctrl+click on a selected item removes it", Names(Targets()) == "Charlie", Names(Targets()));
        Click(Named("Prod"), RawInputModifiers.Shift);
        check("Shift+click selects a range", Names(Targets()) == "Prod,Alpha,Bravo,Charlie", Names(Targets()));
        Click(Named("Bravo"));
        check("plain click goes back to one item", Names(Targets()) == "Bravo", Names(Targets()));

        // ── Delete several (Del key) ──
        Click(Named("Alpha"), RawInputModifiers.Control);
        Click(Named("Prod"), RawInputModifiers.Control);
        w.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        var until = DateTime.UtcNow.AddSeconds(10); // passwords are removed in the background first
        while (winStore.SshRoot.Children.Count > 1 && DateTime.UtcNow < until) { Pump(); Thread.Sleep(20); }
        check("Del deletes all selected items after one question",
            asked != null && asked.Contains("2 sessions and 1 folder (with 2 sessions in them)") && Names(winStore.SshRoot.Children) == "Charlie",
            $"{asked?.Replace("\n", " / ")} => {Names(winStore.SshRoot.Children)}");
        reloaded = new SessionStore();
        reloaded.Load();
        check("deletion is saved", Names(reloaded.SshRoot.Children) == "Charlie" && reloaded.RdpRoot.Children.Count == 1);

        if (shots != null)
        {
            Click(Named("Charlie"));
            var frame = w.CaptureRenderedFrame();
            frame?.Save(Path.Combine(shots, "session-list.png"));
        }

        // ── Window icon follows the main and accent colors ──
        var original = w.Icon;
        bool dark = UiTheme.IsDark;
        var ui = UiTheme.Ui.Clone();
        UiTheme.Apply(dark, new UiSettings { Accent = "#F97316" });
        check("a custom accent redraws the window icon", w.Icon != null && !ReferenceEquals(w.Icon, original) && ReferenceEquals(w.Icon, AppIcon.Current));
        UiTheme.Apply(dark, ui);
        check("default colors restore the installed icon", ReferenceEquals(w.Icon, original));
        if (shots != null)
        {
            using var icon = AppIcon.Draw(Avalonia.Media.Color.Parse(AppIcon.DefaultMain), Avalonia.Media.Color.Parse("#F97316"));
            icon.Save(Path.Combine(shots, "app-icon-orange.png"));
        }
        w.Close();
    }
}
