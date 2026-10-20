using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Path = System.IO.Path;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace SessionManager;

/// <summary>The main window: session tree on the left, connection tabs on the right, toolbar on top.</summary>
public sealed partial class MainWindow : Window
{
    public static WindowIcon? AppIcon => SessionManager.AppIcon.Current;

    private readonly SessionStore _store = new();
    private readonly LinuxSettings _settings = LinuxSettings.Load();
    private readonly ObservableCollection<OpenTab> _tabs = new();
    private readonly TreeView _tree = new();
    private readonly TextBox _filter = new() { Watermark = "Filter sessions (Ctrl+F)", Margin = new Thickness(8, 8, 8, 4) };
    private readonly ItemsControl _tabStrip = new();
    private readonly Grid _tabHost = new();
    private readonly Control _welcome;
    private readonly TextBox _quick = new() { Watermark = "Quick connect: user@host, rdp://host, vnc://host", Width = 300, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _filesToggle = new() { Content = "Files", Classes = { "tool" } };
    private readonly ToggleButton _multiExec = new() { Content = "Multi-exec", Classes = { "tool" } };
    private readonly Button _themeButton = new() { Classes = { "tool" }, FontSize = 15 };
    private readonly ColumnDefinition _sidebarColumn;
    private OpenTab? _selected;
    private bool _closing;

    public MainWindow() : this([]) { }

    public MainWindow(string[] args)
    {
        Title = "Session Manager";
        Icon = AppIcon;
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        MinWidth = 640;
        MinHeight = 400;
        if (_settings.Maximized) WindowState = WindowState.Maximized;
        this.Res(BackgroundProperty, "SmWindow");

        bool firstRun = !File.Exists(Path.Combine(SessionStore.DataDir, "sessions.json"));
        var warning = _store.Load();
        if (firstRun) _store.Data.Theme = Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? "Dark" : "Light";
        if (_store.Data.ActiveTab == Protocol.Web) _store.Data.ActiveTab = Protocol.Ssh; // no Web tab in this app yet
        UiTheme.Apply(_store.Data.Theme == "Dark", _store.Data.Ui);
        UiTheme.Changed += UpdateThemeButton;
        UiTheme.Changed += () => Icon = AppIcon;

        _sidebarColumn = new ColumnDefinition(new GridLength(Math.Clamp(_settings.SidebarWidth, 180, 700))) { MinWidth = 180 };
        _welcome = BuildWelcome();
        Content = UiTheme.Scale(BuildLayout());
        BuildTree();
        BuildTabStrip();
        UpdateThemeButton();
        UpdateTabTools();

        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        Closing += Window_Closing;
        Opened += (_, _) =>
        {
            if (warning != null) _ = Dialogs.Info(this, warning);
            HandleArgs(args);
            SingleInstance.Listen(a => Dispatcher.UIThread.Post(() =>
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                HandleArgs(a);
            }));
        };
    }

    // ───────────── Layout ─────────────

    private Control BuildLayout()
    {
        var root = new DockPanel();

        // Toolbar
        var newSsh = ToolButton("+ SSH", "New SSH session", () => _ = NewSession(Protocol.Ssh));
        var newRdp = ToolButton("+ RDP", "New Remote Desktop session", () => _ = NewSession(Protocol.Rdp));
        var newVnc = ToolButton("+ VNC", "New VNC session", () => _ = NewSession(Protocol.Vnc));
        var newFolder = ToolButton("+ Folder", "New folder", () => _ = NewFolder(TargetFolder(SelectedNode?.Protocol ?? _store.Data.ActiveTab)));
        var creds = ToolButton("Credentials", "Shared logins (credential profiles)", () => _ = ManageCredentials());
        var import = ToolButton("Import / Export ▾", "Import connections, scan a screenshot or PDF, export sessions", () => { });
        import.Flyout = BuildImportMenu();
        var connect = ToolButton("Connect", "Connect to the address typed on the left", () => QuickConnect(_quick.Text ?? ""));
        _quick.KeyDown += (_, e) => { if (e.Key == Key.Enter) { if (QuickConnect(_quick.Text ?? "")) _quick.Text = ""; e.Handled = true; } };
        _filesToggle.Click += (_, _) => { if (_selected?.View is SshTab t) t.ShowFiles = _filesToggle.IsChecked == true; };
        ToolTip.SetTip(_filesToggle, "Show or hide the SFTP file panel");
        _multiExec.Click += (_, _) => UpdateBroadcast();
        ToolTip.SetTip(_multiExec, "Send what you type to all SSH tabs at once");
        _themeButton.Click += (_, _) =>
        {
            _store.Data.Theme = UiTheme.IsDark ? "Light" : "Dark";
            UiTheme.Apply(_store.Data.Theme == "Dark", _store.Data.Ui);
            Save();
        };
        var settings = ToolButton("⚙", "Settings", () => _ = ShowSettings());
        settings.FontSize = 15;

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { newSsh, newRdp, newVnc, newFolder, Sep(), creds, import, Sep(), _quick, connect } };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right, Children = { _filesToggle, _multiExec, Sep(), _themeButton, settings } };
        var bar = new DockPanel { Margin = new Thickness(6, 4) };
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(new ScrollViewer { Content = left, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var toolbar = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) }
            .Res(Border.BackgroundProperty, "SmToolbar").Res(Border.BorderBrushProperty, "SmBorder");
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        // Sidebar | splitter | tabs
        var body = new Grid { ColumnDefinitions = { _sidebarColumn, new ColumnDefinition(GridLength.Auto), new ColumnDefinition(1, GridUnitType.Star) } };
        var sidebar = new DockPanel();
        DockPanel.SetDock(_filter, Dock.Top);
        _filter.TextChanged += (_, _) => ApplyFilter();
        sidebar.Children.Add(_filter);
        var listTabs = BuildListTabs();
        DockPanel.SetDock(listTabs, Dock.Top);
        sidebar.Children.Add(listTabs);
        sidebar.Children.Add(new Panel { Children = { _tree, _emptyHint } });
        body.Children.Add(new Border { Child = sidebar }.Res(Border.BackgroundProperty, "SmSidebar"));
        var splitter = new GridSplitter { Width = 4, ResizeDirection = GridResizeDirection.Columns }.Res(BackgroundProperty, "SmBorder");
        Grid.SetColumn(splitter, 1);
        body.Children.Add(splitter);

        var tabsPanel = new DockPanel();
        var strip = new Border
        {
            Child = new ScrollViewer { Content = _tabStrip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled },
            BorderThickness = new Thickness(0, 0, 0, 1),
        }.Res(Border.BackgroundProperty, "SmSidebar").Res(Border.BorderBrushProperty, "SmBorder");
        DockPanel.SetDock(strip, Dock.Top);
        tabsPanel.Children.Add(strip);
        _tabHost.Children.Add(_welcome);
        tabsPanel.Children.Add(_tabHost);
        Grid.SetColumn(tabsPanel, 2);
        body.Children.Add(tabsPanel);
        root.Children.Add(body);
        return root;
    }

    private static Button ToolButton(string text, string tip, Action click)
    {
        var b = new Button { Content = text, Classes = { "tool" } };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    private static Control Sep() => new Border { Width = 1, Margin = new Thickness(6, 6), }.Res(Border.BackgroundProperty, "SmBorder");

    private void UpdateThemeButton()
    {
        _themeButton.Content = UiTheme.IsDark ? "☀" : "☾";
        ToolTip.SetTip(_themeButton, UiTheme.IsDark ? "Switch to the light theme" : "Switch to the dark theme");
    }

    private Control BuildWelcome()
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520, Spacing = 8 };
        panel.Children.Add(new Image { Source = AppIcon != null ? new Avalonia.Media.Imaging.Bitmap(AssetLoader.Open(new Uri("avares://sessionmanager/Assets/app-256.png"))) : null, Width = 72, Height = 72, Opacity = 0.9 });
        panel.Children.Add(new TextBlock { Text = "Session Manager", FontSize = 22, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        foreach (var line in new[]
                 {
                     "Double-click a session on the left to connect.",
                     "Type user@host, rdp://host or vnc://host in Quick connect for a one-off connection.",
                     "Import / Export reads Remmina, PuTTY, ~/.ssh/config, .rdp and .vnc files, CSV exports, and scans screenshots or PDFs (Ctrl+Shift+V pastes a screenshot).",
                 })
            panel.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Classes = { "sub" } });
        return panel;
    }

    // ───────────── Tabs ─────────────

    private void BuildTabStrip()
    {
        _tabStrip.ItemsSource = _tabs;
        _tabStrip.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal });
        _tabStrip.ItemTemplate = new FuncDataTemplate<OpenTab>((tab, _) =>
        {
            if (tab == null) return new Panel();
            var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
                [!Shape.FillProperty] = new Binding(nameof(OpenTab.StatusBrush)) };
            var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis,
                [!TextBlock.TextProperty] = new Binding(nameof(OpenTab.Title)) };
            var close = new Button { Content = "✕", Classes = { "tool" }, FontSize = 11, Padding = new Thickness(5, 1), Margin = new Thickness(8, 0, 0, 0) };
            close.Click += (_, e) => { CloseTab(tab); e.Handled = true; };
            var border = new Border
            {
                Padding = new Thickness(10, 6, 4, 6),
                Margin = new Thickness(2, 4, 0, 0),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, title, close } },
                [!ToolTip.TipProperty] = new Binding(nameof(OpenTab.ToolTip)),
            };
            void Update()
            {
                if (tab.IsSelected) border.Res(Border.BackgroundProperty, "SmTabActive");
                else border.Background = Brushes.Transparent;
                title.FontWeight = tab.IsSelected ? FontWeight.SemiBold : FontWeight.Normal;
            }
            tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(OpenTab.IsSelected)) Update(); };
            Update();
            border.PointerPressed += (_, e) =>
            {
                var props = e.GetCurrentPoint(border).Properties;
                if (props.IsMiddleButtonPressed) { CloseTab(tab); e.Handled = true; }
                else if (props.IsLeftButtonPressed) { SelectTab(tab); e.Handled = true; }
            };
            border.ContextRequested += (_, e) => { ShowTabMenu(tab, border); e.Handled = true; };
            return border;
        });
    }

    private void SelectTab(OpenTab? tab)
    {
        if (_selected != null) { _selected.IsSelected = false; _selected.View.Element.IsVisible = false; }
        _selected = tab;
        _welcome.IsVisible = tab == null;
        if (tab != null)
        {
            tab.IsSelected = true;
            tab.View.Element.IsVisible = true;
            tab.View.FocusView();
        }
        UpdateTabTools();
    }

    private void UpdateTabTools()
    {
        _filesToggle.IsEnabled = _selected?.View is SshTab;
        _filesToggle.IsChecked = _selected?.View is SshTab t && t.ShowFiles;
        _multiExec.IsEnabled = _tabs.Any(t => t.IsSsh);
    }

    private void AddTab(Node node, ConnectRequest request)
    {
        ISessionView view;
        if (node.Protocol == Protocol.Ssh)
        {
            var terminal = new SshTab(request, AskPassword, () => this, _store.Data.ShowFiles);
            terminal.Typed += OnTerminalTyped;
            view = terminal;
        }
        else if (node.Protocol == Protocol.Vnc) view = new VncTab(request, AskPassword, () => this);
        else if (node.Protocol == Protocol.Web)
        {
            // No built-in browser here yet: open the device's page in the default browser.
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", request.Settings.WebUrl()) { UseShellExecute = false }); }
            catch (Exception ex) { _ = Dialogs.Error(this, "Could not open the web browser", ex); }
            return;
        }
        else view = new RdpTab(request, AskPassword, () => this, _settings);

        var tab = new OpenTab(node, request, view);
        view.Element.IsVisible = false;
        _tabHost.Children.Add(view.Element);
        _tabs.Add(tab);
        tab.IsBroadcasting = _multiExec.IsChecked == true && view is SshTab;
        SelectTab(tab);
        view.Start();
    }

    private void CloseTab(OpenTab tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0) return;
        _tabs.RemoveAt(index);
        _tabHost.Children.Remove(tab.View.Element);
        tab.Detach();
        tab.View.Dispose();
        if (_selected == tab)
        {
            _selected = null;
            SelectTab(_tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)]);
        }
        UpdateTabTools();
    }

    private void ShowTabMenu(OpenTab tab, Control anchor)
    {
        var items = new List<object>();
        void Item(string header, Action a, bool enabled = true)
        {
            var m = new MenuItem { Header = header, IsEnabled = enabled };
            m.Click += (_, _) => a();
            items.Add(m);
        }
        Item("Reconnect", () => tab.View.Reconnect());
        Item("Duplicate", () => OpenSession(tab.Session));
        if (tab.Request.IsTransient) Item("Save to session list…", () => _ = SaveTabSession(tab));
        if (tab.IsSsh)
        {
            var include = new MenuItem { Header = "Include in multi-exec", ToggleType = MenuItemToggleType.CheckBox, IsChecked = tab.IncludeInBroadcast };
            include.Click += (_, _) => { tab.IncludeInBroadcast = !tab.IncludeInBroadcast; UpdateBroadcast(); };
            items.Add(include);
        }
        items.Add(new Separator());
        Item("Close", () => CloseTab(tab));
        Item("Close other tabs", () => { foreach (var t in _tabs.Where(t => t != tab).ToList()) CloseTab(t); }, _tabs.Count > 1);
        new ContextMenu { ItemsSource = items }.Open(anchor);
    }

    private void UpdateBroadcast()
    {
        bool on = _multiExec.IsChecked == true;
        foreach (var t in _tabs) t.IsBroadcasting = on && t.IsSsh && t.IncludeInBroadcast;
    }

    private void OnTerminalTyped(SshTab source, string data)
    {
        if (_multiExec.IsChecked != true) return;
        foreach (var t in _tabs)
            if (t.View is SshTab other && other != source && t.IsBroadcasting && other.Status == TabStatus.Connected)
                other.SendInput(data);
    }

    // ───────────── Keyboard ─────────────

    private bool FocusInTabs => FocusManager?.GetFocusedElement() is Visual v && _tabHost.IsVisualAncestorOf(v);

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl && e.Key == Key.Tab && _tabs.Count > 0)
        {
            int i = _selected == null ? 0 : _tabs.IndexOf(_selected);
            SelectTab(_tabs[(i + (shift ? _tabs.Count - 1 : 1)) % _tabs.Count]);
            e.Handled = true;
        }
        else if (ctrl && shift && e.Key == Key.W && _selected != null) { CloseTab(_selected); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.F && _selected?.View is SshTab find) { find.ShowFind(); e.Handled = true; }
        else if (ctrl && shift && e.Key is Key.OemPlus or Key.Add && _selected?.View is SshTab zin) { zin.Zoom(1); e.Handled = true; }
        else if (ctrl && shift && e.Key is Key.OemMinus or Key.Subtract && _selected?.View is SshTab zout) { zout.Zoom(-1); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.F && !FocusInTabs) { _filter.Focus(); _filter.SelectAll(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.V && !FocusInTabs) { _ = ScanClipboard(); e.Handled = true; }
        else if (e.Key == Key.Escape && _filter.IsFocused && (_filter.Text ?? "").Length > 0) { _filter.Text = ""; e.Handled = true; }
    }

    // ───────────── Closing ─────────────

    private async void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing) return;
        int open = _tabs.Count(t => t.View.Status != TabStatus.Disconnected);
        if (open > 0)
        {
            e.Cancel = true;
            if (!await Dialogs.Confirm(this, $"{open} connection{(open == 1 ? " is" : "s are")} still open. Close them and quit?", "Quit", "Quit", "Cancel")) return;
        }
        _closing = true;
        _settings.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) { _settings.WindowWidth = Width; _settings.WindowHeight = Height; }
        _settings.SidebarWidth = _sidebarColumn.ActualWidth;
        _settings.Save();
        Save();
        foreach (var t in _tabs.ToList()) { t.Detach(); t.View.Dispose(); }
        _tabs.Clear();
        if (e.Cancel) Close();
    }

    private void Save()
    {
        try { _store.Save(); }
        catch (Exception ex) { _ = Dialogs.Error(this, "Could not save your sessions", ex); }
        UpdateListTabs();
    }

    private Task Info(string text) => Dialogs.Info(this, text);
    private Task Error(string what, Exception ex) => Dialogs.Error(this, what, ex);

    private async Task ShowSettings()
    {
        if (await SettingsWindow.Show(this, _store.Data, _settings))
        {
            _settings.Save();
            Save();
            UpdateThemeButton();
        }
    }

    private async Task ManageCredentials()
    {
        int Usage(Guid id) => _store.Data.Roots.SelectMany(r => r.Descendants()).Count(n => n.Settings?.CredentialId == id);
        if (await CredentialsWindow.Show(this, _store.Data, Usage))
        {
            foreach (var n in _store.Data.Roots.SelectMany(r => r.Descendants())) n.NotifySettingsChanged();
            Save();
        }
    }
}
