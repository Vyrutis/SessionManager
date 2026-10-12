using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Renci.SshNet;
using SessionManager.Terminal;

namespace SessionManager;

/// <summary>An SSH session: the built-in terminal connected through SSH.NET, with an SFTP panel beside it.</summary>
public sealed class SshTab : Grid, ISessionView
{
    private readonly ConnectRequest _request;
    private readonly Func<string, bool, Task<(string Password, bool Save)?>> _askPassword;
    private readonly Func<Window?> _owner;
    private readonly VtTerminal _term;
    private readonly TerminalView _view;
    private readonly ScrollBar _scroll = new() { Orientation = Orientation.Vertical, Minimum = 0, AllowAutoHide = true };
    private readonly SftpPanel _files;
    private readonly GridSplitter _splitter = new() { Width = 5, ResizeDirection = GridResizeDirection.Columns };
    private readonly Border _findBar;
    private readonly TextBox _findBox = new() { Width = 220, Watermark = "Find in output" };
    private readonly Border _broadcastBadge;
    private readonly object _writeLock = new();
    private double _filesWidth = 360;

    private SshRoute? _route;
    private SshClient? _client;
    private ShellStream? _shell;
    private bool _disposed, _connecting, _showFiles, _broadcast, _started, _updatingScroll;
    private TabStatus _status = TabStatus.Connecting;

    public Control Element => this;
    public TabStatus Status => _status;
    public event Action<TabStatus>? StatusChanged;
    /// <summary>Everything the user types (used by multi-exec).</summary>
    public event Action<SshTab, string>? Typed;

    public SshTab(ConnectRequest request, Func<string, bool, Task<(string Password, bool Save)?>> askPassword, Func<Window?> owner, bool showFiles)
    {
        _request = request;
        _askPassword = askPassword;
        _owner = owner;
        var s = request.Settings;
        _term = new VtTerminal(80, 24);
        _view = new TerminalView(_term, SchemeFor(s), s.FontFamily, s.FontSize);
        _term.Reply += SendRaw;
        _view.Input += OnUserInput;
        _view.GridResized += (cols, rows) => { try { _shell?.ChangeWindowSize((uint)cols, (uint)rows, 0, 0); } catch { } };
        _view.ScrollChanged += UpdateScrollBar;
        _view.ExtraMenuItems = MenuItems;
        _scroll.Scroll += (_, e) =>
        {
            if (_updatingScroll) return;
            _view.ScrollTo((int)Math.Round(_scroll.Maximum - e.NewValue));
        };

        // Terminal column: find bar on top, terminal + scroll bar below.
        var findNext = new Button { Content = "↓", Classes = { "tool" } };
        var findPrev = new Button { Content = "↑", Classes = { "tool" } };
        var findClose = new Button { Content = "✕", Classes = { "tool" } };
        findNext.Click += (_, _) => _view.Find(_findBox.Text ?? "", backwards: false);
        findPrev.Click += (_, _) => _view.Find(_findBox.Text ?? "", backwards: true);
        findClose.Click += (_, _) => HideFind();
        _findBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _view.Find(_findBox.Text ?? "", backwards: !e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideFind(); e.Handled = true; }
        };
        _findBox.TextChanged += (_, _) => { _view.ClearFind(); _view.Find(_findBox.Text ?? "", backwards: true); };
        _findBar = new Border
        {
            IsVisible = false,
            Padding = new Thickness(6, 4),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _findBox, findPrev, findNext, findClose } },
        }.Res(Border.BackgroundProperty, "SmToolbar");

        _broadcastBadge = new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(Color.FromRgb(0xE3, 0xA0, 0x08)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2),
            Margin = new Thickness(0, 0, 22, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Child = new TextBlock { Text = "MULTI-EXEC", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x13, 0x00)) },
        };

        var termGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        termGrid.Children.Add(_view);
        Grid.SetColumn(_scroll, 1);
        termGrid.Children.Add(_scroll);
        termGrid.Children.Add(_broadcastBadge);
        var termPanel = new DockPanel();
        DockPanel.SetDock(_findBar, Dock.Top);
        termPanel.Children.Add(_findBar);
        termPanel.Children.Add(termGrid);
        termPanel.Background = new SolidColorBrush(_view.Scheme.Background);

        _files = new SftpPanel(owner);
        ColumnDefinitions = new ColumnDefinitions("*,Auto,0");
        ColumnDefinitions[0].MinWidth = 200;
        Children.Add(termPanel);
        Grid.SetColumn(_splitter, 1);
        Children.Add(_splitter.Res(BackgroundProperty, "SmBorder"));
        Grid.SetColumn(_files, 2);
        Children.Add(_files);
        ShowFiles = showFiles;

        UiTheme.Changed += OnThemeChanged;
    }

    private static TerminalScheme SchemeFor(SessionSettings s) => TerminalScheme.Get(s.ColorScheme, UiTheme.TerminalBackground);

    private void OnThemeChanged()
    {
        _view.Scheme = SchemeFor(_request.Settings);
        if (Children[0] is DockPanel p) p.Background = new SolidColorBrush(_view.Scheme.Background);
    }

    public bool ShowFiles
    {
        get => _showFiles;
        set
        {
            if (_showFiles && ColumnDefinitions[2].ActualWidth > 50) _filesWidth = ColumnDefinitions[2].ActualWidth;
            _showFiles = value;
            _files.IsVisible = _splitter.IsVisible = value;
            ColumnDefinitions[2].Width = new GridLength(value ? _filesWidth : 0);
            ColumnDefinitions[2].MinWidth = value ? 220 : 0;
            if (value) _files.EnsureConnected();
        }
    }

    public bool Broadcast
    {
        get => _broadcast;
        set { _broadcast = value; _broadcastBadge.IsVisible = value; }
    }

    public void ShowFind()
    {
        _findBar.IsVisible = true;
        _findBox.Focus();
        _findBox.SelectAll();
    }

    private void HideFind()
    {
        _findBar.IsVisible = false;
        _view.ClearFind();
        _view.Focus();
    }

    private IEnumerable<object> MenuItems()
    {
        var find = new MenuItem { Header = "Find…", InputGesture = new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Shift) };
        find.Click += (_, _) => ShowFind();
        var files = new MenuItem { Header = ShowFiles ? "Hide files panel" : "Show files panel" };
        files.Click += (_, _) => ShowFiles = !ShowFiles;
        var bigger = new MenuItem { Header = "Larger text", InputGesture = new KeyGesture(Key.OemPlus, KeyModifiers.Control | KeyModifiers.Shift) };
        bigger.Click += (_, _) => Zoom(1);
        var smaller = new MenuItem { Header = "Smaller text", InputGesture = new KeyGesture(Key.OemMinus, KeyModifiers.Control | KeyModifiers.Shift) };
        smaller.Click += (_, _) => Zoom(-1);
        var reconnect = new MenuItem { Header = "Reconnect", IsEnabled = !_connecting };
        reconnect.Click += (_, _) => Reconnect();
        return [find, files, bigger, smaller, reconnect];
    }

    public void Zoom(int delta) => _view.SetFont(_request.Settings.FontFamily, _view.FontSizeValue + delta);

    private void UpdateScrollBar()
    {
        _updatingScroll = true;
        int max = _view.ScrollbackLines;
        _scroll.Maximum = max;
        _scroll.ViewportSize = _view.Rows;
        _scroll.Value = max - _view.ScrollOffset;
        _scroll.IsVisible = max > 0;
        _updatingScroll = false;
    }

    // ───────────── Connection ─────────────

    public void Start()
    {
        if (_started) return;
        _started = true;
        // Wait for the first layout so the server gets the real terminal size.
        Dispatcher.UIThread.Post(() => _ = ConnectAsync(), DispatcherPriority.Background);
    }

    private void OnUserInput(string data)
    {
        if (_status == TabStatus.Disconnected)
        {
            if (data == "\r") Reconnect();
            return;
        }
        SendRaw(data);
        Typed?.Invoke(this, data);
    }

    /// <summary>Sends keystrokes to the server (also used by multi-exec).</summary>
    public void SendInput(string data) => SendRaw(data);

    private void SendRaw(string data)
    {
        var shell = _shell;
        if (shell == null) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            lock (_writeLock)
            {
                shell.Write(bytes, 0, bytes.Length);
                shell.Flush();
            }
        }
        catch
        {
            // The read loop notices the closed connection and updates the status.
        }
    }

    private void Write(string text)
    {
        _term.Feed(text);
        _view.RequestRedraw();
    }

    private void Log(string text, LogKind kind)
    {
        var color = kind switch { LogKind.Warning => "33", LogKind.Error => "31", _ => "90" };
        Write($"\x1b[{color}m{text}\x1b[0m\r\n");
    }

    private SshUi Ui => SshUiAvalonia.Create(_owner, _askPassword, Log);

    private void SetStatus(TabStatus s)
    {
        if (_status == s) return;
        _status = s;
        StatusChanged?.Invoke(s);
    }

    public void Reconnect()
    {
        if (_connecting || _disposed) return;
        Cleanup();
        Write("\r\n");
        _ = ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (_disposed) return;
        _connecting = true;
        SetStatus(TabStatus.Connecting);
        var s = _request.Settings;
        var target = $"{(string.IsNullOrEmpty(s.Username) ? "" : s.Username + "@")}{s.Host}";
        Log($"Connecting to {target} on port {s.Port}…", LogKind.Info);

        try
        {
            var ui = Ui;
            var route = _route = await SshConnector.OpenRouteAsync(s, _request.Jump, ui);
            if (_disposed) { Cleanup(); return; }
            var login = new SshLogin { Settings = s, Password = _request.Password, SaveId = _request.SavePasswordTo };
            var client = await SshConnector.ConnectAsync(login, route.Host, route.Port, ui,
                i => new SshClient(i) { KeepAliveInterval = TimeSpan.FromSeconds(30) });
            _request.Password = login.Password; // reconnects and SFTP reuse what was typed
            _client = client;
            if (_disposed) { Cleanup(); return; }

            var background = SshUiAvalonia.Create(_owner, _askPassword, (t, k) => Dispatcher.UIThread.Post(() => Log(t, k)));
            var (cols, rows) = ((uint)_term.Cols, (uint)_term.Rows);
            var shell = await Task.Run(() =>
            {
                SshConnector.StartTunnels(client, s.Tunnels, background);
                return client.CreateShellStream("xterm-256color", cols, rows, 0, 0, 65536);
            });
            if (_disposed || _client != client) { try { shell.Dispose(); } catch { } return; }
            _shell = shell;
            if ((cols, rows) != ((uint)_term.Cols, (uint)_term.Rows)) try { shell.ChangeWindowSize((uint)_term.Cols, (uint)_term.Rows, 0, 0); } catch { }
            SetStatus(TabStatus.Connected);
            new Thread(() => ReadLoop(shell)) { IsBackground = true, Name = "ssh-read" }.Start();

            _files.Attach(() => SshConnector.ConnectAsync(
                new SshLogin { Settings = s, Password = _request.Password }, route.Host, route.Port, ui, i => new SftpClient(i)));
            if (_showFiles) _files.EnsureConnected();
        }
        catch (Exception ex)
        {
            Cleanup();
            Disconnected(ex is OperationCanceledException ? "Cancelled." : ex.Message);
        }
        finally
        {
            _connecting = false;
        }
    }

    private void ReadLoop(ShellStream shell)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var buffer = new byte[32768];
        var chars = new char[32768 + 4];
        string? error = null;
        try
        {
            while (true)
            {
                int n = shell.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                int c = decoder.GetChars(buffer, 0, n, chars, 0);
                if (c == 0) continue;
                _term.Feed(new string(chars, 0, c));
                _view.RequestRedraw();
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { error = ex.Message; }

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _shell != shell) return;
            Cleanup();
            Disconnected(error);
        });
    }

    private void Disconnected(string? reason)
    {
        if (_disposed) return;
        if (!string.IsNullOrEmpty(reason)) Write($"\r\n\x1b[31m{reason}\x1b[0m");
        Write("\r\n\x1b[90m[Disconnected — press Enter to reconnect]\x1b[0m\r\n");
        SetStatus(TabStatus.Disconnected);
    }

    private void Cleanup()
    {
        _files.Detach();
        var shell = _shell;
        var client = _client;
        var route = _route;
        _shell = null;
        _client = null;
        _route = null;
        Task.Run(() =>
        {
            try { shell?.Dispose(); } catch { }
            try { if (client != null) foreach (var p in client.ForwardedPorts.ToList()) p.Stop(); } catch { }
            try { if (client?.IsConnected == true) client.Disconnect(); } catch { }
            try { client?.Dispose(); } catch { }
            route?.Dispose();
        });
    }

    public void FocusView() => Dispatcher.UIThread.Post(() => _view.Focus(), DispatcherPriority.Input);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UiTheme.Changed -= OnThemeChanged;
        Cleanup();
        _files.Dispose();
    }

    /// <summary>For tests: the terminal behind the view.</summary>
    public VtTerminal Terminal => _term;
}
