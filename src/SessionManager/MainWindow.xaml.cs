using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shell;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace SessionManager;

public partial class MainWindow : Window
{
    private readonly SessionStore _store = new();
    private readonly ObservableCollection<OpenTab> _tabs = new();

    private SessionSettings? _copiedSettings;
    private Protocol _copiedProtocol;

    private Point _dragStart;
    private Node? _dragCandidate;

    private bool _initializing = true;
    private bool _multiExec;

    public MainWindow()
    {
        var warning = _store.Load();
        bool dark = _store.Data.Theme == "Dark";
        Theme.Apply(dark, _store.Data.Ui);
        DomainPicker.Saved = _store.Data.Domains;
        DomainPicker.SavedChanged = Save;

        InitializeComponent();
        ThemeToggle.IsChecked = dark;
        Theme.ApplyTo(this, scaleContent: false); // the main window scales RootGrid itself
        Theme.Changed += OnThemeChanged;
        OnThemeChanged();
        StateChanged += (_, _) => UpdateMaximizedLayout();
        ThemePopup.CustomPopupPlacementCallback = (popup, target, _) =>
            new[] { new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height + 6), PopupPrimaryAxis.Horizontal) };
        _themeApply.Tick += (_, _) => { _themeApply.Stop(); ApplyPendingTheme(); };
        _initializing = false;

        Tree.ItemsSource = _store.Data.Roots;
        TabStrip.ItemsSource = _tabs;
        TabStrip.Visibility = Visibility.Collapsed;
        _tabs.CollectionChanged += (_, _) =>
        {
            EmptyHint.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TabStrip.Visibility = _tabs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        };

        PreviewKeyDown += Window_PreviewKeyDown;
        Closing += Window_Closing;

        if (warning != null)
            Loaded += (_, _) => MessageBox.Show(this, warning, "Session Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private Node? SelectedNode => Tree.SelectedItem as Node;

    // ───────────── Title bar ─────────────

    private void ThemeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        bool dark = ThemeToggle.IsChecked == true;
        if (dark == Theme.IsDark) return;
        Theme.Apply(dark);
        _store.Data.Theme = dark ? "Dark" : "Light";
        _themeMenu?.Load(dark, Theme.Ui);
        Save();
    }

    // ───────────── Appearance (palette menu) ─────────────

    private ThemeMenu? _themeMenu;
    private readonly System.Windows.Threading.DispatcherTimer _themeApply = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private (bool Dark, UiSettings Ui)? _pendingTheme;

    private void ThemePopup_Opened(object? sender, EventArgs e)
    {
        if (_themeMenu == null)
        {
            _themeMenu = new ThemeMenu(this, ThemePopup);
            _themeMenu.Changed += ThemeMenu_Changed;
            ThemePopup.Child = _themeMenu;
        }
        // Never taller than the window; the menu scrolls instead.
        _themeMenu.MaxHeight = Math.Max(200, (ActualHeight - 60) / Theme.Scale);
        _themeMenu.Load(Theme.IsDark, Theme.Ui);
        PaletteButton.IsHitTestVisible = false; // a click on the button while open just closes the menu
    }

    private void ThemePopup_Closed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => PaletteButton.IsHitTestVisible = true, System.Windows.Threading.DispatcherPriority.Input);
        if (_pendingTheme != null) ApplyPendingTheme();
        Save();
    }

    /// <summary>Live preview: changes while dragging are applied at most every 40 ms; final ones at once and saved.</summary>
    private void ThemeMenu_Changed(bool dark, UiSettings ui, bool final)
    {
        _pendingTheme = (dark, ui);
        if (final)
        {
            _themeApply.Stop();
            ApplyPendingTheme();
            Save();
        }
        else if (!_themeApply.IsEnabled) _themeApply.Start();
    }

    private void ApplyPendingTheme()
    {
        if (_pendingTheme is not { } p) return;
        _pendingTheme = null;
        Theme.Apply(p.Dark, p.Ui);
        _store.Data.Ui = Theme.Ui.Clone();
        _store.Data.Theme = p.Dark ? "Dark" : "Light";
        if (ThemeToggle.IsChecked != p.Dark)
        {
            _initializing = true; // the toggle only mirrors the menu here
            ThemeToggle.IsChecked = p.Dark;
            _initializing = false;
        }
        _themeMenu?.RefreshPicture();
    }

    /// <summary>Background picture, interface size and the caption height that goes with it.</summary>
    private void OnThemeChanged()
    {
        var ui = Theme.Ui;
        BackgroundLayer.Source = Theme.BackgroundImage;
        BackgroundLayer.Visibility = Theme.BackgroundImage == null ? Visibility.Collapsed : Visibility.Visible;
        if (Theme.BackgroundImage != null && ui.BackgroundBlur)
        {
            BackgroundLayer.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 24, KernelType = System.Windows.Media.Effects.KernelType.Gaussian };
            BackgroundLayer.Margin = new Thickness(-30); // hide the blurred, faded edges
            BackgroundLayer.CacheMode = new BitmapCache();
        }
        else
        {
            BackgroundLayer.Effect = null;
            BackgroundLayer.Margin = new Thickness(0);
            BackgroundLayer.CacheMode = null;
        }

        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome != null) chrome.CaptionHeight = 38 * ui.Scale;
        MinWidth = 640 * ui.Scale;
        MinHeight = 400 * ui.Scale;
        UpdateToolbarLayout();
    }

    // ───────────── Toolbar: icons only when the window is narrow ─────────────

    private double _toolbarFullWidth;
    private bool _toolbarCompact;

    private void Toolbar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateToolbarLayout();

    private void UpdateToolbarLayout()
    {
        if (!IsLoaded || ToolbarPanel == null) return;
        if (_toolbarFullWidth == 0)
        {
            if (_toolbarCompact) return;
            // Width of everything with labels, measured once (unscaled units, so the interface size doesn't matter).
            foreach (UIElement c in ToolbarPanel.Children) c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _toolbarFullWidth = ToolbarPanel.Children.Cast<UIElement>().Sum(c => c.DesiredSize.Width);
            if (_toolbarFullWidth == 0) return;
        }
        double available = ToolbarBorder.ActualWidth - ToolbarBorder.Padding.Left - ToolbarBorder.Padding.Right - 4;
        bool compact = available < _toolbarFullWidth;
        if (compact != _toolbarCompact)
        {
            _toolbarCompact = compact;
            Resources["ToolLabelVisibility"] = compact ? Visibility.Collapsed : Visibility.Visible;
            Resources["ToolIconMargin"] = compact ? new Thickness(0) : new Thickness(0, 0, 6, 0);
        }
        // In compact mode the quick-connect box takes what is left, between 150 and 270.
        QuickPanel.Width = compact ? Math.Clamp(available - (_toolbarFullWidth - 280) * 0.45, 150, 270) : 270;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>A maximized borderless window extends past the screen edges; pad it so nothing is cut off.</summary>
    private void UpdateMaximizedLayout()
    {
        bool max = WindowState == WindowState.Maximized;
        var t = SystemParameters.WindowResizeBorderThickness;
        RootBorder.Margin = max ? new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4) : new Thickness(0);
        RootBorder.BorderThickness = new Thickness(max ? 0 : 1);
        MaxButton.Content = max ? "" : "";
        MaxButton.ToolTip = max ? "Restore" : "Maximize";
    }

    private void Save()
    {
        try { _store.Save(); }
        catch (Exception ex) { Error("Could not save sessions", ex); }
    }

    private void Error(string what, Exception ex) =>
        MessageBox.Show(this, $"{what}.\n\n{ex.Message}", "Session Manager", MessageBoxButton.OK, MessageBoxImage.Error);

    private void Info(string text) =>
        MessageBox.Show(this, text, "Session Manager", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>The folder new items go into: the selected folder, or the selected session's folder, if it matches the protocol.</summary>
    private Node TargetFolder(Protocol protocol)
    {
        var n = SelectedNode;
        var folder = n == null ? null : n.IsFolder ? n : n.Parent;
        return folder != null && folder.Protocol == protocol ? folder : _store.RootFor(protocol);
    }

    // ───────────── Opening sessions ─────────────

    /// <summary>Called for desktop shortcuts (--open id).</summary>
    public void OpenById(Guid id)
    {
        var node = _store.FindById(id);
        if (node is { IsSession: true }) OpenSession(node);
        else Info("The session for this shortcut no longer exists.");
    }

    private void OpenSession(Node node, (string Domain, string User, string Password)? connectAs = null)
    {
        if (node.Settings == null) return;
        var settings = node.Settings.Clone();
        string? password;
        Guid? saveTo = null;

        if (connectAs != null)
        {
            var (domain, user, pw) = connectAs.Value;
            settings.Username = node.Protocol == Protocol.Ssh ? DomainPicker.Combine(domain, user) : user;
            settings.Domain = node.Protocol == Protocol.Ssh ? "" : domain;
            password = pw;
        }
        else
        {
            if (ProfileFor(settings) is { } profile)
            {
                settings.Username = profile.Username;
                settings.Domain = profile.Domain;
            }
            var passwordId = PasswordId(node);
            password = CredentialStore.Get(passwordId);
            if (node.Parent != null) saveTo = passwordId; // quick-connect sessions don't save passwords
            if (node.Protocol == Protocol.Ssh && string.IsNullOrEmpty(settings.Username))
            {
                var user = Dialogs.PromptText(this, "Connect", $"Username for {settings.Host}:");
                if (user == null) return;
                settings.Username = user;
            }
        }

        SshLogin? jump = null;
        if (settings.JumpHostId is Guid jumpId)
        {
            var j = _store.FindById(jumpId);
            if (j is { IsSession: true, Protocol: Protocol.Ssh, Settings: not null } && j != node)
            {
                jump = LoginFor(j, "jump host ");
                if (string.IsNullOrEmpty(jump.Settings.Username))
                {
                    var user = Dialogs.PromptText(this, "Connect", $"Username for the jump host {jump.Settings.Host}:");
                    if (user == null) return;
                    jump.Settings.Username = user;
                }
            }
            else if (MessageBox.Show(this, $"The jump host for \"{node.Name}\" no longer exists.\n\nConnect directly instead?",
                         "Session Manager", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
        }

        AddTab(node, new ConnectRequest
        {
            Session = node,
            Settings = settings,
            Password = password,
            IsOverride = connectAs != null,
            SavePasswordTo = saveTo,
            Jump = jump,
        });
    }

    /// <summary>Login details of a saved SSH session (used for jump hosts).</summary>
    private SshLogin LoginFor(Node n, string role)
    {
        var s = n.Settings!.Clone();
        if (ProfileFor(s) is { } p) s.Username = p.Username;
        var id = PasswordId(n);
        return new SshLogin { Settings = s, Password = CredentialStore.Get(id), SaveId = id, Role = role };
    }

    private CredentialProfile? ProfileFor(SessionSettings s) =>
        s.CredentialId is Guid id ? _store.Data.Credentials.FirstOrDefault(c => c.Id == id) : null;

    /// <summary>Where a session's password is stored: its credential profile, or the session itself.</summary>
    private Guid PasswordId(Node n) => n.Settings != null && ProfileFor(n.Settings) is { } p ? p.Id : n.Id;

    private void AddTab(Node node, ConnectRequest request)
    {
        ISessionView view;
        if (node.Protocol == Protocol.Ssh)
        {
            var terminal = new SshTerminalView(request, AskPassword, _store.Data.ShowFiles);
            terminal.Typed += OnTerminalTyped;
            view = terminal;
        }
        else view = new RdpView(request, AskPassword);

        var tab = new OpenTab(node, request, view);
        view.Element.Visibility = Visibility.Collapsed;
        TabHost.Children.Add(view.Element);
        _tabs.Add(tab);
        tab.IsBroadcasting = _multiExec && view is SshTerminalView;
        TabStrip.SelectedItem = tab;
        view.Start();
    }

    private (string Password, bool Save)? AskPassword(string prompt, bool offerSave) =>
        Dialogs.PromptPassword(this, "Password", prompt, offerSave);

    // ───────────── Tabs ─────────────

    private OpenTab? SelectedTab => TabStrip.SelectedItem as OpenTab;

    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedTab;
        foreach (var t in _tabs)
            t.View.Element.Visibility = t == selected ? Visibility.Visible : Visibility.Collapsed;
        if (selected != null)
        {
            Title = $"{selected.Title} - Session Manager";
            Dispatcher.BeginInvoke(selected.View.FocusView, System.Windows.Threading.DispatcherPriority.Input);
        }
        else Title = "Session Manager";
        UpdateTabTools();
    }

    /// <summary>Files / Find only apply to SSH tabs.</summary>
    private void UpdateTabTools()
    {
        var terminal = SelectedTab?.View as SshTerminalView;
        FilesToggle.IsEnabled = FindButton.IsEnabled = terminal != null;
        FilesToggle.IsChecked = terminal?.ShowFiles == true;
    }

    private void FilesToggle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTab?.View is not SshTerminalView terminal) return;
        SetShowFiles(terminal, FilesToggle.IsChecked == true);
    }

    private void SetShowFiles(SshTerminalView terminal, bool show)
    {
        terminal.ShowFiles = show;
        FilesToggle.IsChecked = show;
        _store.Data.ShowFiles = show; // new SSH tabs follow the last choice
        Save();
    }

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTab?.View is not SshTerminalView terminal) return;
        terminal.ShowFind();
        terminal.FocusView();
    }

    // ───────────── Multi-exec ─────────────

    private void MultiExecToggle_Click(object sender, RoutedEventArgs e)
    {
        _multiExec = MultiExecToggle.IsChecked == true;
        UpdateBroadcast();
        SelectedTab?.View.FocusView();
    }

    private void UpdateBroadcast()
    {
        foreach (var t in _tabs)
            t.IsBroadcasting = _multiExec && t.IncludeInBroadcast && t.View is SshTerminalView;
    }

    /// <summary>Copies keystrokes from one terminal to every other included, connected terminal.</summary>
    private void OnTerminalTyped(SshTerminalView source, string data)
    {
        if (!_multiExec || _tabs.FirstOrDefault(t => t.View == source) is not { IsBroadcasting: true }) return;
        foreach (var t in _tabs)
        {
            if (t.IsBroadcasting && t.View is SshTerminalView other && other != source && other.Status == TabStatus.Connected)
                other.SendInput(data);
        }
    }

    // ───────────── Quick connect ─────────────

    private void QuickBox_TextChanged(object sender, TextChangedEventArgs e) =>
        QuickHint.Visibility = QuickBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void QuickBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (QuickConnect(QuickBox.Text)) QuickBox.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            QuickBox.Clear();
            e.Handled = true;
        }
    }

    /// <summary>Parses "user@host:port" (SSH) or "rdp://user@host:port" and opens a tab without saving a session.</summary>
    private bool QuickConnect(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return false;
        var protocol = Protocol.Ssh;
        if (text.StartsWith("rdp://", StringComparison.OrdinalIgnoreCase)) { protocol = Protocol.Rdp; text = text[6..]; }
        else if (text.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) text = text[6..];
        text = text.TrimEnd('/');

        string user = "";
        int at = text.LastIndexOf('@');
        if (at >= 0) { user = text[..at]; text = text[(at + 1)..]; }

        string host = text;
        string? portText = null;
        if (text.StartsWith('[') && text.IndexOf(']') is var close and > 0)
        {
            host = text[1..close];
            if (text.Length > close + 1 && text[close + 1] == ':') portText = text[(close + 2)..];
        }
        else if (text.Count(c => c == ':') == 1)
        {
            host = text[..text.IndexOf(':')];
            portText = text[(text.IndexOf(':') + 1)..];
        }

        var settings = _store.PresetFor(protocol);
        if (portText != null)
        {
            if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            {
                Info($"\"{portText}\" is not a valid port.");
                return false;
            }
            settings.Port = port;
        }
        if (host.Length == 0)
        {
            Info("Type a host to connect to, for example admin@10.0.0.5 or rdp://server01.");
            return false;
        }
        settings.Host = host;
        if (user.Length > 0)
        {
            settings.Username = user;
            settings.CredentialId = null;
        }

        var name = (user.Length > 0 ? user + "@" : "") + host;
        OpenSession(new Node { Kind = NodeKind.Session, Protocol = protocol, Name = name, Settings = settings });
        return true;
    }

    /// <summary>Adds a quick-connect tab's session to the list.</summary>
    private void SaveTabSession(OpenTab tab)
    {
        var node = tab.Session;
        if (node.Parent != null || node.Settings == null) return;
        var name = Dialogs.PromptText(this, "Save session", "Session name:", node.Name);
        if (name == null) return;
        node.Name = name;
        if (string.IsNullOrEmpty(node.Settings.Username)) node.Settings.Username = tab.Request.Settings.Username;
        _store.AddChild(_store.RootFor(node.Protocol), node);
        if (tab.Request.Password != null && MessageBox.Show(this, "Save the password too?", "Save session",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            try { CredentialStore.Set(node.Id, tab.Request.Password, node.Settings.Username); }
            catch (Exception ex) { Error("Could not save the password", ex); }
        }
        node.IsSelected = true;
        Save();
    }

    private void CloseTab(OpenTab tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0) return;
        bool wasSelected = SelectedTab == tab;
        _tabs.Remove(tab);
        TabHost.Children.Remove(tab.View.Element);
        tab.Detach();
        tab.View.Dispose();
        if (wasSelected && _tabs.Count > 0)
            TabStrip.SelectedItem = _tabs[Math.Min(index, _tabs.Count - 1)];
    }

    private static OpenTab? TabFrom(object sender) => (sender as FrameworkElement)?.DataContext as OpenTab;

    private void TabCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (TabFrom(sender) is { } tab) CloseTab(tab);
    }

    private void TabItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (TabFrom(sender) is not { } tab) return;
        if (e.ChangedButton == MouseButton.Middle)
        {
            CloseTab(tab);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            TabStrip.SelectedItem = tab; // the context menu acts on the selected tab
        }
    }

    private void TabStrip_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = TabStrip.ContextMenu!;
        menu.Items.Clear();
        if (SelectedTab is not { } tab)
        {
            e.Handled = true;
            return;
        }
        menu.Items.Add(Item("Reconnect", "", tab.View.Reconnect));
        menu.Items.Add(Item("Duplicate tab", "", () => DuplicateTab(tab)));
        if (tab.View is SshTerminalView terminal)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(terminal.ShowFiles ? "Hide files (SFTP)" : "Show files (SFTP)", "",
                () => SetShowFiles(terminal, !terminal.ShowFiles), color: "FolderBrush"));
            menu.Items.Add(Item("Find…", "", () => { terminal.ShowFind(); terminal.FocusView(); }, gesture: "Ctrl+Shift+F"));
            if (_multiExec)
                menu.Items.Add(Item(tab.IncludeInBroadcast ? "Exclude from multi-exec" : "Include in multi-exec", "", () =>
                {
                    tab.IncludeInBroadcast = !tab.IncludeInBroadcast;
                    UpdateBroadcast();
                }));
        }
        if (tab.Request.IsTransient)
            menu.Items.Add(Item("Save to session list…", "", () => SaveTabSession(tab), color: "SshBrush"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Close", "", () => CloseTab(tab), gesture: "Ctrl+Shift+W"));
        menu.Items.Add(Item("Close other tabs", "", () => CloseOtherTabs(tab), enabled: _tabs.Count > 1));
    }

    private void DuplicateTab(OpenTab tab) =>
        AddTab(tab.Session, new ConnectRequest
        {
            Session = tab.Session,
            Settings = tab.Request.Settings.Clone(),
            Password = tab.Request.Password,
            IsOverride = tab.Request.IsOverride,
            SavePasswordTo = tab.Request.SavePasswordTo,
            Jump = tab.Request.Jump,
        });

    private void CloseOtherTabs(OpenTab keep)
    {
        foreach (var t in _tabs.Where(t => t != keep).ToList()) CloseTab(t);
        TabStrip.SelectedItem = keep;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && shift && e.Key == Key.W && SelectedTab is { } tab)
        {
            CloseTab(tab);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Tab && _tabs.Count > 1)
        {
            int i = TabStrip.SelectedIndex;
            TabStrip.SelectedIndex = shift ? (i - 1 + _tabs.Count) % _tabs.Count : (i + 1) % _tabs.Count;
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.F && !(Keyboard.FocusedElement is DependencyObject d && IsInsideTab(d)))
        {
            FilterBox.Focus();
            e.Handled = true;
        }
        // Scan a screenshot from the clipboard (not inside a session: terminals use Ctrl+Shift+V to paste).
        else if (ctrl && shift && e.Key == Key.V && !(Keyboard.FocusedElement is DependencyObject d2 && IsInsideTab(d2)) && ClipboardHasScannable())
        {
            ScanClipboard();
            e.Handled = true;
        }
    }

    private bool IsInsideTab(DependencyObject d)
    {
        for (var p = d; p != null; p = VisualTreeHelper.GetParent(p))
            if (p == TabHost) return true;
        return false;
    }

    private bool _closeAnimationDone;

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeAnimationDone) return;
        int open = _tabs.Count(t => t.View.Status != TabStatus.Disconnected);
        if (open > 0 && !App.IsSessionEnding && MessageBox.Show(this, $"Close {open} open connection{(open == 1 ? "" : "s")} and exit?", "Session Manager",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            e.Cancel = true;
            return;
        }
        foreach (var t in _tabs.ToList()) CloseTab(t);
        ThemePopup.IsOpen = false;

        // Fade out and sink a little, then close for real (skipped at Windows shutdown, when minimized, or with animations off).
        if (Theme.Animations && !App.IsSessionEnding && WindowState != WindowState.Minimized && IsVisible)
        {
            e.Cancel = true;
            _closeAnimationDone = true;
            CloseAnimation.Run(this, () => Dispatcher.BeginInvoke(Close));
        }
    }

    // ───────────── Tree: mouse, keyboard, drag and drop ─────────────

    private static Node? NodeFrom(object source)
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TreeViewItem item) return item.DataContext as Node;
        return null;
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (NodeFrom(e.OriginalSource) is { IsSession: true } node)
        {
            OpenSession(node);
            e.Handled = true;
        }
    }

    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-click selects the item under the mouse, so the context menu acts on it.
        var node = NodeFrom(e.OriginalSource);
        if (node != null) node.IsSelected = true;
        else if (SelectedNode is { } sel) sel.IsSelected = false;
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (SelectedNode is not { } node) return;
        switch (e.Key)
        {
            case Key.Enter when node.IsSession: OpenSession(node); e.Handled = true; break;
            case Key.F2 when !node.IsRoot: Rename(node); e.Handled = true; break;
            case Key.Delete when !node.IsRoot: Delete(node); e.Handled = true; break;
        }
    }

    private void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Tree);
        _dragCandidate = NodeFrom(e.OriginalSource);
    }

    private void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is not { IsRoot: false } node) return;
        var pos = e.GetPosition(Tree);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragCandidate = null;
        DragDrop.DoDragDrop(Tree, new DataObject(typeof(Node), node), DragDropEffects.Move);
    }

    private Node? DropTarget(DragEventArgs e, out Node? dragged)
    {
        dragged = e.Data.GetData(typeof(Node)) as Node;
        var over = NodeFrom(e.OriginalSource);
        if (dragged == null || over == null) return null;
        var folder = over.IsFolder ? over : over.Parent;
        if (folder == null || folder.Protocol != dragged.Protocol) return null;
        if (folder == dragged || dragged.IsAncestorOf(folder) || folder == dragged.Parent) return null;
        return folder;
    }

    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropTarget(e, out _) != null ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Tree_Drop(object sender, DragEventArgs e)
    {
        var folder = DropTarget(e, out var dragged);
        if (folder == null || dragged == null) return;
        _store.Remove(dragged);
        _store.AddChild(folder, dragged);
        dragged.IsSelected = true;
        Save();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = FilterBox.Text.Trim();
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var root in _store.Data.Roots)
        {
            ApplyFilter(root, text);
            root.IsVisible = true;
        }
    }

    private static bool ApplyFilter(Node node, string text)
    {
        bool selfMatch = text.Length == 0
            || node.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
            || node.Subtitle.Contains(text, StringComparison.CurrentCultureIgnoreCase);
        bool childMatch = false;
        foreach (var c in node.Children)
            childMatch |= ApplyFilter(c, selfMatch && node.IsFolder && !node.IsRoot ? "" : text);
        node.IsVisible = selfMatch || childMatch;
        if (text.Length > 0 && childMatch) node.IsExpanded = true;
        return node.IsVisible;
    }

    // ───────────── Context menu ─────────────

    private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = Tree.ContextMenu!;
        menu.Items.Clear();
        var node = SelectedNode;

        if (node == null)
        {
            menu.Items.Add(Item("New SSH session", "", () => NewSession(Protocol.Ssh), color: "SshBrush"));
            menu.Items.Add(Item("New RDP session", "", () => NewSession(Protocol.Rdp), color: "RdpBrush"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("New SSH folder", "", () => NewFolder(_store.SshRoot), color: "FolderBrush"));
            menu.Items.Add(Item("New RDP folder", "", () => NewFolder(_store.RdpRoot), color: "FolderBrush"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Import session from file...", "", () => Import(null)));
            menu.Items.Add(Item("Import session list...", "", ImportListPicked, color: "SshBrush"));
            menu.Items.Add(Item("Export all sessions...", "", () => ExportList(null), color: "RdpBrush"));
            return;
        }

        if (node.IsSession)
        {
            var execute = Item("Execute", "", () => OpenSession(node), gesture: "Enter", color: "SshBrush");
            execute.FontWeight = FontWeights.SemiBold;
            menu.Items.Add(execute);
            menu.Items.Add(Item("Connect as...", "", () => ConnectAs(node)));
            menu.Items.Add(Item("Ping host", "", () => new PingWindow(this, node.Settings!.Host, node.Settings.Port).Show(), color: "RdpBrush"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Rename session", "", () => Rename(node), gesture: "F2"));
            menu.Items.Add(Item("Edit session", "", () => EditSession(node), color: "RdpBrush"));
            menu.Items.Add(Item("Change password...", "", () => ChangePassword(node), color: "FolderBrush"));
            menu.Items.Add(Item("Delete session", "", () => Delete(node), gesture: "Del", color: "DangerBrush"));
            menu.Items.Add(Item("Duplicate session", "", () => Duplicate(node)));
            menu.Items.Add(Item("Save session to file", "", () => Export(node), color: "DangerBrush"));
            menu.Items.Add(Item("Create a desktop shortcut", "", () => CreateShortcut(node), color: "FolderBrush"));
            menu.Items.Add(Item("New folder", "", () => NewFolder(node.Parent!), color: "FolderBrush"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Save session settings as default presets", "", () => SaveAsPreset(node)));
            menu.Items.Add(Item("Copy session settings", "", () => CopySettings(node), color: "RdpBrush"));
            menu.Items.Add(Item("Paste session settings", "", () => PasteSettings(node), color: "RdpBrush",
                enabled: _copiedSettings != null && _copiedProtocol == node.Protocol));
        }
        else
        {
            var kind = node.Protocol == Protocol.Ssh ? "SSH" : "RDP";
            menu.Items.Add(Item($"New {kind} session", node.Protocol == Protocol.Ssh ? "" : "", () => NewSession(node.Protocol),
                color: node.Protocol == Protocol.Ssh ? "SshBrush" : "RdpBrush"));
            menu.Items.Add(Item("New folder", "", () => NewFolder(node), color: "FolderBrush"));
            menu.Items.Add(Item("Import session from file...", "", () => Import(node)));
            menu.Items.Add(Item(node.IsRoot ? $"Export all {kind} sessions..." : "Export folder...", "", () => ExportList(node), color: "RdpBrush"));
            if (!node.IsRoot)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Rename folder", "", () => Rename(node), gesture: "F2"));
                menu.Items.Add(Item("Delete folder", "", () => Delete(node), gesture: "Del", color: "DangerBrush"));
            }
            if (node.Children.Count > 0)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Expand all", "", () => SetExpanded(node, true)));
                menu.Items.Add(Item("Collapse all", "", () => SetExpanded(node, false)));
            }
        }
    }

    private MenuItem Item(string header, string glyph, Action action, string? gesture = null, string? color = null, bool enabled = true)
    {
        var item = new MenuItem
        {
            Header = header,
            InputGestureText = gesture ?? "",
            IsEnabled = enabled,
            Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)FindResource("Icons"),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        // color is a theme brush key (SshBrush, DangerBrush, ...) so icons follow light/dark mode.
        ((TextBlock)item.Icon).SetResourceReference(TextBlock.ForegroundProperty, color ?? "SubTextBrush");
        item.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Error($"{header.TrimEnd('.')} failed", ex); }
        };
        return item;
    }

    private static void SetExpanded(Node node, bool expanded)
    {
        foreach (var f in node.Descendants().Where(n => n.IsFolder)) f.IsExpanded = expanded;
        node.IsExpanded = expanded || node.IsRoot;
    }

    // ───────────── Toolbar ─────────────

    private void NewSsh_Click(object sender, RoutedEventArgs e) => NewSession(Protocol.Ssh);
    private void NewRdp_Click(object sender, RoutedEventArgs e) => NewSession(Protocol.Rdp);

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var n = SelectedNode;
        NewFolder(n == null ? _store.SshRoot : n.IsFolder ? n : n.Parent!);
    }

    private void Credentials_Click(object sender, RoutedEventArgs e) =>
        new CredentialsWindow(this, _store, SyncProfiles).ShowDialog();

    /// <summary>Copies profile usernames into the sessions that use them (so the list shows the right user).</summary>
    private void SyncProfiles()
    {
        foreach (var n in _store.Data.Roots.SelectMany(r => r.Descendants()).Where(n => n.IsSession && n.Settings != null))
        {
            if (ProfileFor(n.Settings!) is not { } p) continue;
            n.Settings!.Username = p.Username;
            n.Settings.Domain = p.Domain;
            n.NotifySettingsChanged();
        }
        Save();
    }

    /// <summary>SSH sessions that can serve as a jump host, labelled with their folder path.</summary>
    private List<(Guid Id, string Label)> JumpHostChoices(Node? exclude)
    {
        static string PathOf(Node n)
        {
            var parts = new List<string>();
            for (var p = n; p != null && !p.IsRoot; p = p.Parent) parts.Insert(0, p.Name);
            return string.Join(" / ", parts);
        }
        return _store.SshRoot.Descendants()
            .Where(n => n.IsSession && n != exclude)
            .Select(n => (n.Id, $"{PathOf(n)}   ({n.Subtitle})"))
            .OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void ImportExport_Click(object sender, RoutedEventArgs e)
    {
        var n = SelectedNode;
        var folder = n == null ? null : n.IsFolder ? n : n.Parent;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(Item("Export all sessions…", "", () => ExportList(null), color: "RdpBrush"));
        if (folder is { IsRoot: false })
            menu.Items.Add(Item($"Export folder \"{folder.Name}\"…", "", () => ExportList(folder), color: "FolderBrush"));
        menu.Items.Add(Item("Import session list…", "", ImportListPicked, color: "SshBrush"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Import single sessions (.smsession, .rdp)…", "", () => Import(folder)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Import from PuTTY", "\uE756", () => Review(ConnectionImport.FromPutty, "PuTTY"), enabled: ConnectionImport.HasPutty()));
        menu.Items.Add(Item("Import from OpenSSH config (~/.ssh/config)", "\uE943", () => Review(() => ConnectionImport.FromSshConfig(ConnectionImport.SshConfigPath), "SSH config"),
            enabled: File.Exists(ConnectionImport.SshConfigPath)));
        menu.Items.Add(Item("Import Remote Desktop history (mstsc)", "\uE7F4", () => Review(ConnectionImport.FromMstscHistory, "Remote Desktop"),
            enabled: ConnectionImport.HasMstscHistory()));
        menu.Items.Add(Item("Find .rdp files on this PC…", "\uE721", FindRdpFiles));
        menu.Items.Add(Item("Import from mRemoteNG, MobaXterm or CSV…", "\uE8E5", ImportConnectionFile));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Scan picture or PDF for connections…", "\uE8B3", ScanFilePicked, color: "AccentBrush"));
        menu.Items.Add(Item("Scan screenshot from clipboard", "\uE77F", ScanClipboard, gesture: "Ctrl+Shift+V", color: "AccentBrush",
            enabled: ClipboardHasScannable()));
        menu.IsOpen = true;
    }

    // ───────────── Importing connections from other tools / pictures ─────────────

    private void ImportConnectionFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import connections",
            Filter = "Connection lists (*.xml;*.mxtsessions;*.ini;*.csv;*.tsv;config)|*.xml;*.mxtsessions;*.ini;*.csv;*.tsv;config|" +
                     "mRemoteNG (confCons.xml)|*.xml|MobaXterm sessions (*.mxtsessions)|*.mxtsessions;MobaXterm.ini|" +
                     "CSV - KeePass, Bitwarden, spreadsheet (*.csv)|*.csv;*.tsv|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        string source = "Imported";
        Review(() => { var (items, src) = ConnectionImport.FromFile(dlg.FileName); source = src; return items; }, () => source);
    }

    private async void FindRdpFiles()
    {
        if (MessageBox.Show(this, "Look for .rdp files in your Desktop, Documents, Downloads and OneDrive folders?\n\n" +
                "Only the connection details are read and nothing leaves this PC. Online-only OneDrive files are skipped.",
                "Find .rdp files", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var preset = _store.PresetFor(Protocol.Rdp);
        Mouse.OverrideCursor = Cursors.Wait;
        List<ImportedConnection> items;
        try { items = await Task.Run(() => ConnectionImport.FindRdpFiles(preset)); }
        catch (Exception ex) { Mouse.OverrideCursor = null; Error("Could not search for .rdp files", ex); return; }
        finally { Mouse.OverrideCursor = null; }
        if (items.Count == 0) { Info("No .rdp files were found in those folders."); return; }
        ReviewAndAdd(items, ".rdp files", null);
    }

    private void Review(Func<List<ImportedConnection>> read, string source) => Review(read, () => source);

    private void Review(Func<List<ImportedConnection>> read, Func<string> source)
    {
        List<ImportedConnection> items;
        try { items = read(); }
        catch (Exception ex) { Error("Could not read the connections", ex); return; }
        if (items.Count == 0) { Info("No SSH or Remote Desktop connections were found there."); return; }
        ReviewAndAdd(items, source(), null);
    }

    private static readonly string[] ScanExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".pdf"];

    private static bool ClipboardHasScannable()
    {
        try
        {
            return Clipboard.ContainsImage() || (Clipboard.ContainsFileDropList() &&
                Clipboard.GetFileDropList().Cast<string>().Any(f => ScanExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())));
        }
        catch { return false; } // another program holds the clipboard
    }

    private void ScanFilePicked()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Scan a picture or PDF for connections",
            Filter = "Pictures and PDFs|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.pdf|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true) ScanPath(dlg.FileName);
    }

    private void ScanPath(string path) =>
        _ = ScanAsync(() => ScreenshotScan.ScanFileAsync(path), Path.GetFileNameWithoutExtension(path));

    private void ScanClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
            {
                _ = ScanAsync(() => ScreenshotScan.ScanImageAsync(img), "Screenshot");
                return;
            }
            var file = Clipboard.ContainsFileDropList()
                ? Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(f => ScanExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                : null;
            if (file != null) ScanPath(file);
            else Info("The clipboard has no picture. Take a screenshot (Win+Shift+S) or copy a picture or PDF file, then try again.");
        }
        catch (Exception ex) { Error("Could not read the clipboard", ex); }
    }

    private bool _scanning;

    private async Task ScanAsync(Func<Task<ScreenshotScan.Result>> scan, string source)
    {
        if (_scanning) return;
        _scanning = true;
        Mouse.OverrideCursor = Cursors.Wait;
        ScreenshotScan.Result result;
        try { result = await scan(); }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            Error("Could not scan for connections", ex);
            return;
        }
        finally { Mouse.OverrideCursor = null; _scanning = false; }
        ReviewAndAdd(result.Items, source, result.Text);
    }

    private void ReviewAndAdd(List<ImportedConnection> items, string source, string? recognizedText)
    {
        var dlg = new ImportReviewWindow(this, source, items, recognizedText);
        if (dlg.ShowDialog() != true || dlg.Selected.Count == 0) return;
        AddImported(dlg.Selected, dlg.FolderName, dlg.SkipExisting, dlg.ShareLogins, source);
    }

    private void AddImported(List<ImportedConnection> items, string? folderName, bool skipExisting, bool shareLogins, string source)
    {
        static string Key(Protocol p, string host, int port, string user) => $"{p}|{host.ToLowerInvariant()}|{port}|{user.ToLowerInvariant()}";
        var existing = _store.Data.Roots.SelectMany(r => r.Descendants()).Where(n => n.IsSession && n.Settings != null)
            .Select(n => Key(n.Protocol, n.Settings!.Host, n.Settings.Port, ProfileFor(n.Settings)?.Username ?? n.Settings.Username)).ToHashSet();

        // Identical logins used by several connections become one shared credential profile.
        var profiles = new Dictionary<(string User, string Domain, string Password), CredentialProfile>();
        if (shareLogins)
        {
            foreach (var g in items.Where(i => i.Password != null && i.Username.Length > 0)
                         .GroupBy(i => (i.Username, i.Domain, Password: i.Password!)).Where(g => g.Count() > 1))
            {
                var profile = _store.Data.Credentials.FirstOrDefault(c =>
                    c.Username.Equals(g.Key.Username, StringComparison.OrdinalIgnoreCase) && c.Domain.Equals(g.Key.Domain, StringComparison.OrdinalIgnoreCase)
                    && CredentialStore.Get(c.Id) == g.Key.Password);
                if (profile == null)
                {
                    var baseName = (g.Key.Domain.Length > 0 ? g.Key.Domain + "\\" : "") + g.Key.Username;
                    var name = baseName;
                    for (int n = 2; _store.Data.Credentials.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++) name = $"{baseName} ({n})";
                    profile = new CredentialProfile { Name = name, Username = g.Key.Username, Domain = g.Key.Domain };
                    try { CredentialStore.Set(profile.Id, g.Key.Password, profile.Username); }
                    catch (Exception ex) { Error("Could not save a password", ex); continue; }
                    _store.Data.Credentials.Add(profile);
                }
                profiles[g.Key] = profile;
            }
        }

        int added = 0, skipped = 0, failedPasswords = 0;
        Node? last = null;
        foreach (var c in items)
        {
            var user = c.Protocol == Protocol.Ssh ? DomainPicker.Combine(c.Domain, c.Username) : c.Username;
            var key = Key(c.Protocol, c.Host, c.Port, user);
            if (skipExisting && existing.Contains(key)) { skipped++; continue; }
            existing.Add(key);

            var folder = _store.RootFor(c.Protocol);
            if (folderName != null) folder = FolderNamed(folder, folderName);
            foreach (var part in c.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                folder = FolderNamed(folder, part);

            var settings = c.Template?.Clone() ?? _store.PresetFor(c.Protocol);
            settings.Host = c.Host;
            settings.Port = c.Port > 0 ? c.Port : ConnectionImport.DefaultPort(c.Protocol);
            settings.Username = user;
            settings.Domain = c.Protocol == Protocol.Rdp ? c.Domain : "";
            settings.CredentialId = null;
            settings.JumpHostId = null;
            if (c.Protocol == Protocol.Ssh && c.PrivateKeyPath.Length > 0 && File.Exists(c.PrivateKeyPath)) settings.PrivateKeyPath = c.PrivateKeyPath;
            if (c.Protocol == Protocol.Rdp && c.Domain.Length > 0) DomainPicker.Remember(c.Domain);
            if (c.Password != null && profiles.TryGetValue((c.Username, c.Domain, c.Password), out var shared))
                settings.CredentialId = shared.Id;

            var node = new Node { Kind = NodeKind.Session, Name = c.Name.Length > 0 ? c.Name : c.Host, Protocol = c.Protocol, Settings = settings };
            _store.AddChild(folder, node);
            if (c.Password != null && settings.CredentialId == null)
            {
                try { CredentialStore.Set(node.Id, c.Password, settings.Username); }
                catch { failedPasswords++; }
            }
            added++;
            last = node;
        }

        if (last != null) last.IsSelected = true;
        Save();
        var msg = $"Added {added} connection{(added == 1 ? "" : "s")} from {source}.";
        if (profiles.Count > 0) msg += $"\n{profiles.Count} shared login{(profiles.Count == 1 ? "" : "s")} saved as credential profile{(profiles.Count == 1 ? "" : "s")}.";
        if (skipped > 0) msg += $"\n{skipped} skipped because you already have them.";
        if (failedPasswords > 0) msg += $"\n{failedPasswords} password{(failedPasswords == 1 ? "" : "s")} could not be saved; you'll be asked when connecting.";
        Info(msg);
    }

    /// <summary>The child folder with this name, created if it doesn't exist yet.</summary>
    private Node FolderNamed(Node parent, string name)
    {
        var f = parent.Children.FirstOrDefault(n => n.IsFolder && n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (f != null) return f;
        f = new Node { Kind = NodeKind.Folder, Name = name, IsExpanded = true };
        _store.AddChild(parent, f);
        return f;
    }

    // ───────────── Actions ─────────────

    private void NewSession(Protocol protocol)
    {
        var folder = TargetFolder(protocol);
        var kind = protocol == Protocol.Ssh ? "SSH" : "Remote Desktop";
        var dlg = new SessionDialog(this, protocol, $"New {kind} session", "", _store.PresetFor(protocol), hasSavedPassword: false,
            _store.Data.Credentials, JumpHostChoices(null));
        if (dlg.ShowDialog() != true) return;

        var node = new Node { Kind = NodeKind.Session, Name = dlg.SessionName, Settings = dlg.Settings };
        _store.AddChild(folder, node);
        ApplyPassword(node, dlg);
        node.IsSelected = true;
        Save();
    }

    private void EditSession(Node node)
    {
        var dlg = new SessionDialog(this, node.Protocol, $"Edit session - {node.Name}", node.Name, node.Settings!, CredentialStore.Has(node.Id),
            _store.Data.Credentials, JumpHostChoices(node));
        if (dlg.ShowDialog() != true) return;
        node.Name = dlg.SessionName;
        node.Settings = dlg.Settings;
        node.NotifySettingsChanged();
        SessionStore.Sort(node.Parent!);
        ApplyPassword(node, dlg);
        Save();
    }

    private void ApplyPassword(Node node, SessionDialog dlg)
    {
        try
        {
            if (dlg.SavePassword && dlg.NewPassword != null) CredentialStore.Set(node.Id, dlg.NewPassword, node.Settings!.Username);
            else if (!dlg.SavePassword && dlg.NewPassword == "") CredentialStore.Delete(node.Id);
        }
        catch (Exception ex) { Error("Could not update the saved password", ex); }
    }

    private void ConnectAs(Node node)
    {
        var s = node.Settings!;
        var creds = Dialogs.PromptCredentials(this, $"Connect to {node.Name} as", node.Protocol == Protocol.Rdp ? s.Domain : "", s.Username);
        if (creds != null) OpenSession(node, creds);
    }

    private void ChangePassword(Node node)
    {
        var id = node.Id;
        var name = node.Name;
        if (ProfileFor(node.Settings!) is { } profile)
        {
            int count = _store.Data.Roots.SelectMany(r => r.Descendants()).Count(n => n.Settings?.CredentialId == profile.Id);
            if (MessageBox.Show(this, $"\"{node.Name}\" uses the credential profile \"{profile.Name}\".\n\n" +
                    $"Changing the password changes it for all {count} session{(count == 1 ? "" : "s")} using that profile. Continue?",
                    "Change password", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            id = profile.Id;
            name = profile.Name;
        }
        var (action, password) = Dialogs.ChangePassword(this, name, CredentialStore.Has(id));
        if (action == Dialogs.PasswordChange.Set)
        {
            CredentialStore.Set(id, password, node.Settings!.Username);
            Info("Password saved.");
        }
        else if (action == Dialogs.PasswordChange.Remove)
        {
            CredentialStore.Delete(id);
            Info("Saved password removed. You'll be asked for it when connecting.");
        }
    }

    private void NewFolder(Node parent)
    {
        var name = Dialogs.PromptText(this, "New folder", "Folder name:");
        if (name == null) return;
        var folder = new Node { Kind = NodeKind.Folder, Name = name, IsExpanded = true };
        _store.AddChild(parent, folder);
        folder.IsSelected = true;
        Save();
    }

    private void Rename(Node node)
    {
        var what = node.IsFolder ? "folder" : "session";
        var name = Dialogs.PromptText(this, $"Rename {what}", "New name:", node.Name);
        if (name == null || name == node.Name) return;
        node.Name = name;
        SessionStore.Sort(node.Parent!);
        Save();
    }

    private void Delete(Node node)
    {
        string question;
        if (node.IsSession) question = $"Delete the session \"{node.Name}\"?";
        else
        {
            int count = node.Descendants().Count(n => n.IsSession);
            question = count == 0
                ? $"Delete the folder \"{node.Name}\"?"
                : $"Delete the folder \"{node.Name}\" and the {count} session{(count == 1 ? "" : "s")} in it?";
        }
        var deleted = node.Descendants().Prepend(node).Where(n => n.IsSession).ToList();
        var deletedIds = deleted.Select(n => n.Id).ToHashSet();
        var dependents = _store.Data.Roots.SelectMany(r => r.Descendants())
            .Where(n => n.Settings?.JumpHostId is Guid j && deletedIds.Contains(j) && !deletedIds.Contains(n.Id))
            .ToList();
        if (dependents.Count > 0)
            question += $"\n\n{dependents.Count} other session{(dependents.Count == 1 ? " uses" : "s use")} it as a jump host / SSH gateway " +
                        "and will connect directly afterwards.";
        if (MessageBox.Show(this, question + "\n\nSaved passwords are removed too.", "Delete", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        foreach (var s in deleted)
        {
            try { CredentialStore.Delete(s.Id); } catch { /* already gone */ }
        }
        foreach (var d in dependents) d.Settings!.JumpHostId = null;
        _store.Remove(node);
        Save();
    }

    private void Duplicate(Node node)
    {
        var copy = new Node { Kind = NodeKind.Session, Name = node.Name + " (copy)", Settings = node.Settings!.Clone() };
        _store.AddChild(node.Parent!, copy);
        var password = CredentialStore.Get(node.Id);
        if (password != null) CredentialStore.Set(copy.Id, password, copy.Settings.Username);
        copy.IsSelected = true;
        Save();
    }

    private void Export(Node node)
    {
        var safeName = string.Concat(node.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var filter = "Session Manager session (*.smsession)|*.smsession";
        if (node.Protocol == Protocol.Rdp) filter += "|Remote Desktop file (*.rdp)|*.rdp";
        var dlg = new SaveFileDialog { Title = "Save session to file", FileName = safeName, Filter = filter, DefaultExt = SessionFile.Extension };
        if (dlg.ShowDialog(this) != true) return;

        if (Path.GetExtension(dlg.FileName).Equals(".rdp", StringComparison.OrdinalIgnoreCase))
        {
            SessionFile.ExportRdp(dlg.FileName, node);
            Info("Saved. Passwords are not stored in .rdp files; Remote Desktop will ask for it.");
            return;
        }

        var password = CredentialStore.Get(PasswordId(node));
        string? passphrase = null;
        if (password != null)
        {
            var answer = MessageBox.Show(this,
                "Include the saved password in the file?\n\n" +
                "If you choose Yes, you'll pick a password to protect the file. It will be needed when the session is imported.",
                "Save session to file", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                passphrase = Dialogs.PromptNewPassphrase(this, "Protect the password", "Choose a password to protect the exported file:");
                if (passphrase == null) return;
            }
            else password = null;
        }

        SessionFile.Export(dlg.FileName, node, password, passphrase);
        Info(password != null ? "Session saved, including the encrypted password." : "Session saved (without password).");
    }

    private void Import(Node? folder)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import sessions",
            Filter = "Session files (*.smsession;*.rdp;*.smlist)|*.smsession;*.rdp;*.smlist|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        Node? last = null;
        foreach (var file in dlg.FileNames)
        {
            if (Path.GetExtension(file).Equals(SessionList.Extension, StringComparison.OrdinalIgnoreCase))
            {
                ImportList(file); // a whole list has its own import flow
                continue;
            }
            try
            {
                Node node;
                string? password = null;
                if (Path.GetExtension(file).Equals(".rdp", StringComparison.OrdinalIgnoreCase))
                {
                    var (name, settings) = SessionFile.ImportRdp(file, _store.PresetFor(Protocol.Rdp));
                    node = new Node { Kind = NodeKind.Session, Name = name, Protocol = Protocol.Rdp, Settings = settings };
                }
                else
                {
                    var model = SessionFile.Read(file);
                    node = new Node { Kind = NodeKind.Session, Name = model.Name, Protocol = model.Protocol, Settings = model.Settings };
                    if (model.Password != null) password = AskImportPassphrase(model);
                    // A file from someone else could forward ports into this PC; only keep tunnels the user agrees to.
                    if (node.Settings.Tunnels.Count > 0 && MessageBox.Show(this,
                            $"\"{model.Name}\" opens these port forwards when it connects:\n\n" +
                            string.Join("\n", node.Settings.Tunnels.Select(t => "  " + t)) +
                            "\n\nKeep them? Choose No to import the session without them.",
                            "Import session", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                        node.Settings.Tunnels.Clear();
                }

                // Profiles and jump hosts from another PC don't exist here.
                if (node.Settings!.CredentialId is Guid cid && _store.Data.Credentials.All(c => c.Id != cid)) node.Settings.CredentialId = null;
                if (node.Settings.JumpHostId is Guid jid && _store.FindById(jid) == null) node.Settings.JumpHostId = null;
                var target = folder != null && folder.Protocol == node.Protocol ? folder : _store.RootFor(node.Protocol);
                _store.AddChild(target, node);
                if (password != null) CredentialStore.Set(node.Id, password, node.Settings!.Username);
                last = node;
            }
            catch (Exception ex)
            {
                Error($"Could not import {Path.GetFileName(file)}", ex);
            }
        }
        if (last != null)
        {
            last.IsSelected = true;
            Save();
        }
    }

    /// <summary>Saves the whole list (folder == null), one group (a root) or one folder to a .smlist file.</summary>
    private void ExportList(Node? folder)
    {
        List<Node> items = folder == null ? _store.Data.Roots.SelectMany(r => r.Children).ToList()
            : folder.IsRoot ? folder.Children.ToList()
            : new List<Node> { folder };
        var sessions = SessionList.AllNodes(items).Where(n => n.IsSession).ToList();
        if (sessions.Count == 0)
        {
            Info("There are no sessions to export here.");
            return;
        }

        // A full export takes every profile; a partial one only the profiles its sessions use.
        var used = sessions.Select(s => s.Settings?.CredentialId).OfType<Guid>().ToHashSet();
        var profiles = _store.Data.Credentials.Where(c => folder == null || used.Contains(c.Id)).ToList();

        var date = DateTime.Now.ToString("yyyy-MM-dd");
        var baseName = folder == null ? "Sessions" : folder.Name;
        var safeName = string.Concat($"{baseName} {date}".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dlg = new SaveFileDialog
        {
            Title = folder == null ? "Export all sessions" : $"Export \"{folder.Name}\"",
            FileName = safeName,
            Filter = "Session Manager session list (*.smlist)|*.smlist",
            DefaultExt = SessionList.Extension,
        };
        if (dlg.ShowDialog(this) != true) return;

        var passwords = new Dictionary<Guid, string>();
        foreach (var id in sessions.Select(s => s.Id).Concat(profiles.Select(p => p.Id)))
            if (CredentialStore.Get(id) is { } pw) passwords[id] = pw;

        string? passphrase = null;
        if (passwords.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"{passwords.Count} saved password{(passwords.Count == 1 ? "" : "s")} can be included in the file.\n\n" +
                "Include them? If you choose Yes, you'll pick a password that protects the file. It will be needed to import them.",
                "Export sessions", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                passphrase = Dialogs.PromptNewPassphrase(this, "Protect the passwords", "Choose a password to protect the exported file:");
                if (passphrase == null) return;
            }
        }

        var model = SessionList.Build(items, profiles, folder == null ? _store.Data.Presets : null,
            passphrase != null ? passwords : null, passphrase);
        passwords.Clear();
        SessionList.Write(dlg.FileName, model);

        int folders = SessionList.AllNodes(items).Count(n => n.IsFolder);
        Info($"Exported {sessions.Count} session{(sessions.Count == 1 ? "" : "s")}" +
             (folders > 0 ? $" in {folders} folder{(folders == 1 ? "" : "s")}" : "") +
             (passphrase != null ? ", including the encrypted passwords." : " (without passwords).") +
             $"\n\n{dlg.FileName}");
    }

    private void ImportListPicked()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import session list",
            Filter = "Session Manager session list (*.smlist)|*.smlist|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true) ImportList(dlg.FileName);
    }

    private void ImportList(string file)
    {
        SessionList.FileModel model;
        try { model = SessionList.Read(file); }
        catch (Exception ex)
        {
            Error($"Could not read {Path.GetFileName(file)}", ex);
            return;
        }

        var all = SessionList.AllNodes(model.Items).ToList();
        int sessionCount = all.Count(n => n.IsSession), folderCount = all.Count(n => n.IsFolder);
        if (sessionCount == 0)
        {
            Info("This list doesn't contain any sessions.");
            return;
        }
        var description = $"\"{Path.GetFileName(file)}\" contains {sessionCount} session{(sessionCount == 1 ? "" : "s")}" +
                          (folderCount > 0 ? $" in {folderCount} folder{(folderCount == 1 ? "" : "s")}" : "") +
                          (model.Credentials.Count > 0 ? $" and {model.Credentials.Count} credential profile{(model.Credentials.Count == 1 ? "" : "s")}" : "") +
                          (model.Passwords != null ? ", with protected passwords" : "") + ".";
        var replace = Dialogs.ChooseImportMode(this, description);
        if (replace == null) return;

        Dictionary<Guid, string>? passwords = null;
        if (model.Passwords != null)
        {
            var label = "Enter the password that was chosen when the list was exported:";
            while (true)
            {
                var r = Dialogs.PromptPassword(this, "Import session list", label, offerSave: false);
                if (r == null)
                {
                    Info("The sessions will be imported without their passwords.");
                    break;
                }
                passwords = SessionList.DecryptPasswords(model, r.Value.Password);
                if (passwords != null) break;
                label = "Wrong password, try again:";
            }
        }

        // A list from someone else could forward ports into this PC; only keep tunnels the user agrees to.
        var withTunnels = all.Where(n => n.IsSession && n.Settings!.Tunnels.Count > 0).ToList();
        if (withTunnels.Count > 0)
        {
            var names = string.Join("\n", withTunnels.Take(8).Select(n => $"  {n.Name}: {string.Join(", ", n.Settings!.Tunnels)}"));
            if (withTunnels.Count > 8) names += $"\n  … and {withTunnels.Count - 8} more";
            if (MessageBox.Show(this, $"These sessions open port forwards when they connect:\n\n{names}\n\n" +
                    "Keep the port forwards? Choose No to import the sessions without them.",
                    "Import session list", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                foreach (var n in withTunnels) n.Settings!.Tunnels.Clear();
        }

        try
        {
            if (replace == true)
            {
                // Saved passwords of sessions that aren't in the new list are removed with them.
                var keep = all.Select(n => n.Id).ToHashSet();
                foreach (var old in SessionList.AllNodes(_store.Data.Roots).Where(n => n.IsSession && !keep.Contains(n.Id)))
                {
                    try { CredentialStore.Delete(old.Id); } catch { /* already gone */ }
                }
                SessionList.Clear(_store);
                if (model.Presets != null) _store.Data.Presets = model.Presets;
            }

            var result = SessionList.Merge(_store, model);
            int savedPasswords = 0;
            if (passwords != null)
            {
                foreach (var n in result.AddedSessions)
                    if (passwords.TryGetValue(n.Id, out var pw)) { CredentialStore.Set(n.Id, pw, n.Settings!.Username); savedPasswords++; }
                foreach (var p in model.Credentials)
                    if (passwords.TryGetValue(p.Id, out var pw) && !CredentialStore.Has(p.Id)) { CredentialStore.Set(p.Id, pw, p.Username); savedPasswords++; }
                passwords.Clear();
            }

            FilterBox_TextChanged(FilterBox, null!);
            if (result.AddedSessions.FirstOrDefault() is { } first) first.IsSelected = true;
            Save();
            Info($"Imported {result.Added} session{(result.Added == 1 ? "" : "s")}" +
                 (result.Folders > 0 ? $" and {result.Folders} new folder{(result.Folders == 1 ? "" : "s")}" : "") + "." +
                 (result.Skipped > 0 ? $"\n\n{result.Skipped} session{(result.Skipped == 1 ? " was" : "s were")} already in your list and {(result.Skipped == 1 ? "was" : "were")} skipped." : "") +
                 (savedPasswords > 0 ? $"\n\n{savedPasswords} password{(savedPasswords == 1 ? "" : "s")} saved in Windows Credential Manager." : ""));
        }
        catch (Exception ex)
        {
            Save();
            Error("The import did not finish", ex);
        }
    }

    private string? AskImportPassphrase(SessionFile.FileModel model)
    {
        var label = $"\"{model.Name}\" includes a protected password.\nEnter the password that was chosen when it was exported:";
        while (true)
        {
            var r = Dialogs.PromptPassword(this, "Import session", label, offerSave: false);
            if (r == null)
            {
                Info($"\"{model.Name}\" will be imported without its password.");
                return null;
            }
            var pw = SessionFile.Decrypt(model.Password!, r.Value.Password);
            if (pw != null) return pw;
            label = "Wrong password, try again:";
        }
    }

    private void CreateShortcut(Node node)
    {
        var path = Shortcut.CreateOnDesktop(node);
        Info($"Shortcut created on your desktop:\n{Path.GetFileName(path)}");
    }

    private void SaveAsPreset(Node node)
    {
        var preset = node.Settings!.Clone();
        preset.Host = "";
        _store.Data.Presets[node.Protocol] = preset;
        Save();
        var kind = node.Protocol == Protocol.Ssh ? "SSH" : "RDP";
        Info($"New {kind} sessions will start with the settings of \"{node.Name}\" (everything except the host).");
    }

    private void CopySettings(Node node)
    {
        _copiedSettings = node.Settings!.Clone();
        _copiedProtocol = node.Protocol;
    }

    private void PasteSettings(Node node)
    {
        if (_copiedSettings == null || _copiedProtocol != node.Protocol) return;
        var s = _copiedSettings.Clone();
        s.Host = node.Settings!.Host;
        s.Port = node.Settings.Port;
        if (s.JumpHostId == node.Id) s.JumpHostId = null; // a session can't be its own jump host
        node.Settings = s;
        node.NotifySettingsChanged();
        Save();
    }
}
