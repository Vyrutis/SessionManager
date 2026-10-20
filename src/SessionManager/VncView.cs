using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SessionManager;

/// <summary>A VNC session drawn inside the tab, with a small toolbar (Ctrl+Alt+Del, scaling, view only).</summary>
public sealed class VncView : Grid, ISessionView
{
    private readonly ConnectRequest _request;
    private readonly Func<string, bool, (string Password, bool Save)?> _askPassword;
    private readonly Image _image = new() { Cursor = Cursors.Cross, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _screen;
    private readonly ScrollViewer _scroller;
    private readonly Border _toolbar;
    private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 12, 0) };
    private readonly ToggleButton _fit = new() { Content = "Scale to fit", ToolTip = "Shrink or stretch the remote screen to the tab (off: actual size, with scroll bars)" };
    private readonly ToggleButton _viewOnly = new() { Content = "View only", ToolTip = "Watch only: your keyboard and mouse are not sent" };
    private readonly Border _overlay;
    private readonly TextBlock _overlayText = new() { FontSize = 15, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _reconnect = new() { Content = "Reconnect", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };

    private VncClient? _client;
    private SshRoute? _route;
    private WriteableBitmap? _bitmap;
    private readonly object _dirtyLock = new();
    private VncRect _dirty;
    private bool _redrawPosted, _disposed, _connecting, _suppressText;
    private int _buttons, _lastX = -1, _lastY = -1;
    private readonly Dictionary<Key, uint> _pressed = new();
    private string? _lastClipboard;
    private TabStatus _status = TabStatus.Connecting;

    public FrameworkElement Element => this;
    public TabStatus Status => _status;
    public event Action<TabStatus>? StatusChanged;

    public VncView(ConnectRequest request, Func<string, bool, (string Password, bool Save)?> askPassword)
    {
        _request = request;
        _askPassword = askPassword;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        // Toolbar
        Button Tool(string text, string tip, Action click)
        {
            var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(2, 0, 2, 0) };
            b.SetResourceReference(StyleProperty, "ToolButton");
            b.Click += (_, _) => { click(); _screen?.Focus(); };
            return b;
        }
        foreach (var t in new[] { _fit, _viewOnly })
        {
            t.SetResourceReference(StyleProperty, "ToolToggle");
            t.Padding = new Thickness(10, 4, 10, 4);
            t.Margin = new Thickness(2, 0, 2, 0);
        }
        _info.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(_info);
        tools.Children.Add(Tool("Ctrl+Alt+Del", "Send Ctrl+Alt+Del to the remote computer", SendCtrlAltDel));
        tools.Children.Add(Tool("Win", "Press the Windows key on the remote computer", () => SendTap(VncKeys.SuperL)));
        tools.Children.Add(_fit);
        tools.Children.Add(_viewOnly);
        tools.Children.Add(Tool("Refresh", "Ask the server for the whole screen again", () => _client?.RefreshScreen()));
        tools.Children.Add(Tool("Disconnect", "Close the connection (the tab stays open)", () => Stop()));
        _toolbar = new Border { Child = tools, Padding = new Thickness(4, 3, 4, 3), BorderThickness = new Thickness(0, 0, 0, 1), Visibility = Visibility.Collapsed };
        _toolbar.SetResourceReference(Border.BackgroundProperty, "ToolbarBrush");
        _toolbar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Children.Add(_toolbar);

        // Screen: the focusable border takes the keyboard; the image takes the mouse.
        _screen = new Border { Child = _image, Background = Brushes.Black, Focusable = true, FocusVisualStyle = null };
        _scroller = new ScrollViewer { Content = _screen, Background = Brushes.Black, Visibility = Visibility.Collapsed };
        SetRow(_scroller, 1);
        Children.Add(_scroller);

        _fit.IsChecked = request.Settings.SmartSizing;
        _viewOnly.IsChecked = request.Settings.ViewOnly;
        _fit.Click += (_, _) => { ApplyScaling(); _screen.Focus(); };
        _viewOnly.Click += (_, _) => { ReleaseKeys(); _screen.Focus(); };
        ApplyScaling();

        _image.MouseMove += (_, e) => SendPointer(e);
        _image.MouseDown += (_, e) => { _screen.Focus(); _image.CaptureMouse(); SendPointer(e); e.Handled = true; };
        _image.MouseUp += (_, e) => { SendPointer(e); if (_buttons == 0) _image.ReleaseMouseCapture(); e.Handled = true; };
        _image.MouseWheel += OnWheel;
        _screen.PreviewKeyDown += OnKeyDown;
        _screen.PreviewKeyUp += OnKeyUp;
        _screen.TextInput += OnTextInput;
        _screen.GotKeyboardFocus += (_, _) => SendClipboard();
        _screen.LostKeyboardFocus += (_, _) => ReleaseKeys();
        _screen.SizeChanged += (_, _) => { if (_fit.IsChecked == true) ApplyScaling(); };

        // Connecting / disconnected card
        _reconnect.Click += (_, _) => Reconnect();
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 520 };
        stack.Children.Add(_overlayText);
        stack.Children.Add(_reconnect);
        _overlay = new Border { Child = stack };
        _overlay.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _overlayText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        SetRowSpan(_overlay, 2);
        Children.Add(_overlay);
    }

    private bool ViewOnly => _viewOnly.IsChecked == true;

    private void ApplyScaling()
    {
        bool fit = _fit.IsChecked == true;
        _image.Stretch = fit ? Stretch.Uniform : Stretch.None;
        _scroller.HorizontalScrollBarVisibility = fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        _scroller.VerticalScrollBarVisibility = fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        RenderOptions.SetBitmapScalingMode(_image, fit ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
    }

    // ───────────── Connection ─────────────

    public void Start() => Dispatcher.BeginInvoke(() => _ = ConnectAsync(), DispatcherPriority.Background);

    public void Reconnect()
    {
        if (_connecting || _disposed) return;
        Stop(silent: true);
        _ = ConnectAsync();
    }

    public void FocusView()
    {
        if (_status == TabStatus.Connected) Dispatcher.BeginInvoke(() => _screen.Focus(), DispatcherPriority.Input);
    }

    private void SetStatus(TabStatus s, string text)
    {
        _overlayText.Text = text;
        bool connected = s == TabStatus.Connected;
        _overlay.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        _scroller.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        _toolbar.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        _reconnect.Visibility = s == TabStatus.Disconnected ? Visibility.Visible : Visibility.Collapsed;
        if (_status == s) return;
        _status = s;
        StatusChanged?.Invoke(s);
    }

    private async Task ConnectAsync()
    {
        if (_disposed) return;
        var s = _request.Settings;
        _connecting = true;
        SetStatus(TabStatus.Connecting, $"Connecting to {s.Host}:{s.Port}…");
        try
        {
            string host = s.Host;
            int port = s.Port;
            if (_request.Jump != null)
            {
                SetStatus(TabStatus.Connecting, $"Connecting to jump host {_request.Jump.Settings.Host}…");
                var ui = SshUiWpf.Create(this, _askPassword, (_, _) => { });
                _route = await SshConnector.OpenRouteAsync(s, _request.Jump, ui);
                (host, port) = (_route.Host, _route.Port);
                if (_disposed) { Stop(silent: true); return; }
                SetStatus(TabStatus.Connecting, $"Connecting to {s.Host}:{s.Port} through the jump host…");
            }

            for (int attempt = 0; ; attempt++)
            {
                bool asked = false;
                try
                {
                    _client = await VncClient.ConnectAsync(host, port, () =>
                    {
                        asked = true;
                        return Task.FromResult(_request.Password ?? AskPassword(null));
                    });
                    break;
                }
                catch (VncAuthException ex) when (attempt < 4 && asked)
                {
                    _request.Password = null;
                    if (AskPassword(ex.Message) == null) throw new OperationCanceledException("Cancelled.");
                }
            }
            if (_disposed) { Stop(silent: true); return; }

            var client = _client;
            client.Closed += error => Dispatcher.BeginInvoke(() =>
            {
                if (_client != client) return;
                Stop(silent: true);
                SetStatus(TabStatus.Disconnected, error == null ? "Disconnected." : "The connection was closed.\n\n" + error.Message);
            });
            client.ClipboardReceived += text => Dispatcher.BeginInvoke(() => ReceiveClipboard(text));
            client.FrameUpdated += OnFrameUpdated;
            client.Resized += () => Dispatcher.BeginInvoke(() => { RebuildBitmap(); UpdateInfo(); });
            RebuildBitmap();
            UpdateInfo();
            client.Start();
            SetStatus(TabStatus.Connected, "Connected.");
            FocusView();
        }
        catch (Exception ex)
        {
            Stop(silent: true);
            SetStatus(TabStatus.Disconnected, ex is OperationCanceledException ? "Cancelled." : "Could not connect.\n\n" + ex.Message);
        }
        finally
        {
            _connecting = false;
        }
    }

    private void UpdateInfo()
    {
        if (_client is not { } c) return;
        _info.Text = $"{(c.DesktopName.Length > 0 ? c.DesktopName : _request.Settings.Host)} · {c.Width}×{c.Height}";
    }

    /// <summary>Asks for the VNC password (after a failed attempt, with the reason); null = cancelled. Runs on the UI thread.</summary>
    private string? AskPassword(string? failure)
    {
        return Dispatcher.Invoke(() =>
        {
            var s = _request.Settings;
            (string Password, bool Save)? result;
            if (failure != null && _request.RetryLogin != null)
            {
                var retry = _request.RetryLogin($"{failure}\n\nVNC password for {s.Host}:", _request.SavePasswordTo != null);
                result = retry == null ? null : (retry.Password, retry.Save);
            }
            else result = _askPassword((failure != null ? failure + "\n\n" : "") + $"VNC password for {s.Host}:", _request.SavePasswordTo != null);
            if (result == null) return null;
            _request.Password = result.Value.Password;
            if (result.Value.Save && _request.SavePasswordTo is Guid id)
            {
                try { CredentialStore.Set(id, result.Value.Password, ""); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Session Manager"); }
            }
            return result.Value.Password;
        });
    }

    private void Stop(bool silent = false)
    {
        var client = _client;
        _client = null;
        if (client != null) client.FrameUpdated -= OnFrameUpdated;
        client?.Dispose();
        _route?.Dispose();
        _route = null;
        _pressed.Clear();
        _buttons = 0;
        if (!silent) SetStatus(TabStatus.Disconnected, "Disconnected.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop(silent: true);
    }

    // ───────────── Drawing ─────────────

    private void OnFrameUpdated(VncRect r)
    {
        lock (_dirtyLock)
        {
            _dirty = _dirty.Union(r);
            if (_redrawPosted) return;
            _redrawPosted = true;
        }
        Dispatcher.BeginInvoke(CopyDirty, DispatcherPriority.Render);
    }

    private void RebuildBitmap()
    {
        if (_client is not { Width: > 0, Height: > 0 } c) return;
        // At the screen's DPI, so "actual size" is one remote pixel per screen pixel.
        var dpi = VisualTreeHelper.GetDpi(this);
        _bitmap = new WriteableBitmap(c.Width, c.Height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Bgr32, null);
        _image.Source = _bitmap;
        lock (_dirtyLock) _dirty = new VncRect(0, 0, c.Width, c.Height);
        CopyDirty();
    }

    private void CopyDirty()
    {
        VncRect r;
        lock (_dirtyLock)
        {
            r = _dirty;
            _dirty = default;
            _redrawPosted = false;
        }
        var c = _client;
        var bmp = _bitmap;
        if (c == null || bmp == null || r.IsEmpty) return;
        lock (c.FrameLock)
        {
            if (bmp.PixelWidth != c.Width || bmp.PixelHeight != c.Height) return; // a resize is on its way
            int w = Math.Min(r.Width, c.Width - r.X), h = Math.Min(r.Height, c.Height - r.Y);
            if (w <= 0 || h <= 0) return;
            bmp.WritePixels(new Int32Rect(r.X, r.Y, w, h), c.Pixels, c.Width * 4, r.X, r.Y);
        }
    }

    // ───────────── Mouse ─────────────

    private (int X, int Y)? RemotePoint(Point p)
    {
        if (_bitmap == null || _image.ActualWidth <= 0 || _image.ActualHeight <= 0) return null;
        int x = (int)(p.X * _bitmap.PixelWidth / _image.ActualWidth), y = (int)(p.Y * _bitmap.PixelHeight / _image.ActualHeight);
        return (Math.Clamp(x, 0, _bitmap.PixelWidth - 1), Math.Clamp(y, 0, _bitmap.PixelHeight - 1));
    }

    private void SendPointer(MouseEventArgs e, int extra = 0)
    {
        if (_client == null || ViewOnly) return;
        _buttons = (e.LeftButton == MouseButtonState.Pressed ? 1 : 0) | (e.MiddleButton == MouseButtonState.Pressed ? 2 : 0) | (e.RightButton == MouseButtonState.Pressed ? 4 : 0);
        if (RemotePoint(e.GetPosition(_image)) is not { } p) return;
        if (extra == 0 && e is not MouseButtonEventArgs && p.X == _lastX && p.Y == _lastY) return;
        (_lastX, _lastY) = p;
        if (extra != 0) _client.SendPointer(p.X, p.Y, _buttons | extra);
        _client.SendPointer(p.X, p.Y, _buttons);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_client == null || ViewOnly) return;
        int steps = Math.Max(1, Math.Abs(e.Delta) / 120);
        for (int i = 0; i < Math.Min(steps, 10); i++) SendPointer(e, e.Delta > 0 ? 8 : 16);
    }

    // ───────────── Keyboard ─────────────

    private void SendTap(uint keysym)
    {
        if (_client == null || ViewOnly) return;
        _client.SendKey(keysym, true);
        _client.SendKey(keysym, false);
    }

    private void SendCtrlAltDel()
    {
        if (_client == null || ViewOnly) return;
        foreach (var k in new[] { VncKeys.ControlL, VncKeys.AltL, VncKeys.Delete }) _client.SendKey(k, true);
        foreach (var k in new[] { VncKeys.Delete, VncKeys.AltL, VncKeys.ControlL }) _client.SendKey(k, false);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        _suppressText = false;
        if (_client == null || ViewOnly) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Held modifiers as tracked here: AltGr arrives as Ctrl+right Alt, and its characters come as text.
        bool ctrl = _pressed.ContainsKey(Key.LeftCtrl) || _pressed.ContainsKey(Key.RightCtrl);
        bool alt = _pressed.ContainsKey(Key.LeftAlt) || _pressed.ContainsKey(Key.RightAlt);
        uint? sym = SpecialKey(key);
        // With Ctrl or Alt held, letters and digits don't arrive as text: send the key itself (Ctrl+C, Alt+F...).
        if (sym == null && (ctrl || alt) && !(ctrl && alt) && PlainKey(key) is { } plain) sym = plain;
        if (sym == null) return; // the character arrives as TextInput
        _pressed.TryAdd(key, sym.Value);
        _client.SendKey(sym.Value, true);
        _suppressText = true;
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_client != null && _pressed.Remove(key, out var sym))
        {
            _client.SendKey(sym, false);
            e.Handled = true;
        }
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_client == null || ViewOnly || string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;
        if (_suppressText) { _suppressText = false; return; }
        foreach (var rune in e.Text.EnumerateRunes())
        {
            if (rune.Value < 0x20 || rune.Value == 0x7F) continue;
            SendTap(VncKeys.FromChar(rune.Value));
        }
    }

    /// <summary>Releases keys still held, or they would stay pressed on the remote computer.</summary>
    private void ReleaseKeys()
    {
        if (_client != null)
            foreach (var sym in _pressed.Values) _client.SendKey(sym, false);
        _pressed.Clear();
    }

    private static uint? SpecialKey(Key key) => key switch
    {
        Key.Back => VncKeys.BackSpace,
        Key.Tab => VncKeys.Tab,
        Key.Enter => VncKeys.Return,
        Key.Escape => VncKeys.Escape,
        Key.Insert => VncKeys.Insert,
        Key.Delete => VncKeys.Delete,
        Key.Home => VncKeys.Home,
        Key.End => VncKeys.End,
        Key.PageUp => VncKeys.PageUp,
        Key.PageDown => VncKeys.PageDown,
        Key.Left => VncKeys.Left,
        Key.Up => VncKeys.Up,
        Key.Right => VncKeys.Right,
        Key.Down => VncKeys.Down,
        >= Key.F1 and <= Key.F24 => VncKeys.F(key - Key.F1 + 1),
        Key.LeftShift => VncKeys.ShiftL,
        Key.RightShift => VncKeys.ShiftR,
        Key.LeftCtrl => VncKeys.ControlL,
        Key.RightCtrl => VncKeys.ControlR,
        Key.LeftAlt => VncKeys.AltL,
        Key.RightAlt => VncKeys.AltR,
        Key.LWin => VncKeys.SuperL,
        Key.RWin => VncKeys.SuperR,
        Key.Apps => VncKeys.Menu,
        Key.CapsLock => VncKeys.CapsLock,
        Key.NumLock => VncKeys.NumLock,
        Key.Scroll => VncKeys.ScrollLock,
        Key.PrintScreen => VncKeys.Print,
        Key.Pause => VncKeys.Pause,
        _ => null,
    };

    private static uint? PlainKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (uint)('a' + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (uint)('0' + (key - Key.D0)),
        >= Key.NumPad0 and <= Key.NumPad9 => (uint)('0' + (key - Key.NumPad0)),
        Key.Space => ' ',
        Key.OemMinus => '-',
        Key.OemPlus => '=',
        Key.OemComma => ',',
        Key.OemPeriod => '.',
        Key.OemQuestion => '/',
        Key.OemSemicolon => ';',
        Key.OemQuotes => '\'',
        Key.OemOpenBrackets => '[',
        Key.OemCloseBrackets => ']',
        Key.OemPipe => '\\',
        Key.OemTilde => '`',
        _ => null,
    };

    // ───────────── Clipboard ─────────────

    /// <summary>Text copied on this PC goes to the remote clipboard when the screen gets the focus.</summary>
    private void SendClipboard()
    {
        if (!_request.Settings.RedirectClipboard || ViewOnly || _client == null) return;
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (COMException) { return; } // another program has the clipboard open
        if (text.Length == 0 || text == _lastClipboard) return;
        _lastClipboard = text;
        _client.SendClipboard(text);
    }

    /// <summary>Text copied on the remote computer.</summary>
    private void ReceiveClipboard(string text)
    {
        if (!_request.Settings.RedirectClipboard || text.Length == 0) return;
        text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _lastClipboard = text;
        try { Clipboard.SetText(text); }
        catch (COMException) { }
    }
}
