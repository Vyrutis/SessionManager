using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace SessionManager;

/// <summary>A VNC session drawn inside the tab, with a small toolbar (Ctrl+Alt+Del, scaling, view only).</summary>
public sealed class VncTab : Grid, ISessionView
{
    private readonly ConnectRequest _request;
    private readonly Func<string, bool, Task<(string Password, bool Save)?>> _askPassword;
    private readonly Func<Window?> _owner;
    private readonly VncScreen _screen;
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0), Classes = { "sub" } };
    private readonly ToggleButton _fit = new() { Content = "Scale to fit" };
    private readonly ToggleButton _viewOnly = new() { Content = "View only" };
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _reconnect = new() { Content = "Reconnect", Classes = { "accent" } };
    private readonly Border _card;
    private readonly Border _toolbar;

    private VncClient? _client;
    private SshRoute? _route;
    private bool _disposed, _connecting;
    private TabStatus _status = TabStatus.Connecting;

    public Control Element => this;
    public TabStatus Status => _status;
    public event Action<TabStatus>? StatusChanged;

    public VncTab(ConnectRequest request, Func<string, bool, Task<(string Password, bool Save)?>> askPassword, Func<Window?> owner)
    {
        _request = request;
        _askPassword = askPassword;
        _owner = owner;
        this.Res(BackgroundProperty, "SmWindow");
        RowDefinitions = new RowDefinitions("Auto,*");

        _screen = new VncScreen { ViewOnly = request.Settings.ViewOnly, ShareClipboard = request.Settings.RedirectClipboard };
        _scroller = new ScrollViewer { Content = _screen, Background = Brushes.Black };
        _fit.IsChecked = request.Settings.SmartSizing;
        _viewOnly.IsChecked = request.Settings.ViewOnly;
        _fit.IsCheckedChanged += (_, _) => ApplyScaling();
        _viewOnly.IsCheckedChanged += (_, _) => { _screen.ViewOnly = _viewOnly.IsChecked == true; _screen.Focus(); };
        ApplyScaling();

        Button Tool(string text, string tip, Action click)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 3) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => { click(); _screen.Focus(); };
            return b;
        }
        foreach (var t in new[] { _fit, _viewOnly }) t.Padding = new Thickness(10, 3);
        ToolTip.SetTip(_fit, "Shrink or stretch the remote screen to the tab (off: actual size, with scroll bars)");
        ToolTip.SetTip(_viewOnly, "Watch only: your keyboard and mouse are not sent");
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            Children =
            {
                _info,
                Tool("Ctrl+Alt+Del", "Send Ctrl+Alt+Del to the remote computer", () => _screen.SendCtrlAltDel()),
                Tool("Win", "Press the Windows / Super key on the remote computer", () => _screen.SendTap(VncKeys.SuperL)),
                _fit, _viewOnly,
                Tool("Refresh", "Ask the server for the whole screen again", () => _client?.RefreshScreen()),
                Tool("Disconnect", "Close the connection (the tab stays open)", () => Stop()),
            },
        };
        _toolbar = new Border { Child = tools, Padding = new Thickness(6, 3), BorderThickness = new Thickness(0, 0, 0, 1), IsVisible = false }
            .Res(Border.BackgroundProperty, "SmToolbar").Res(Border.BorderBrushProperty, "SmBorder");
        Children.Add(_toolbar);
        SetRow(_scroller, 1);
        Children.Add(_scroller);

        _reconnect.Click += (_, _) => Reconnect();
        _card = new Border
        {
            MaxWidth = 560,
            Padding = new Thickness(24),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Children =
                {
                    _title,
                    _state.Res(TextBlock.ForegroundProperty, "SmSubText"),
                    new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Children = { _reconnect } },
                },
            },
        }.Res(Border.BackgroundProperty, "SmCard");
        SetRow(_card, 0);
        SetRowSpan(_card, 2);
        Children.Add(_card);
        _title.Text = $"VNC: {request.Session.Name}";
        _scroller.IsVisible = false;
    }

    private void ApplyScaling()
    {
        bool fit = _fit.IsChecked == true;
        _screen.Fit = fit;
        _scroller.HorizontalScrollBarVisibility = fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        _scroller.VerticalScrollBarVisibility = fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    // ───────────── Connection ─────────────

    public void Start() => Dispatcher.UIThread.Post(() => _ = ConnectAsync(), DispatcherPriority.Background);

    public void Reconnect()
    {
        if (_connecting || _disposed) return;
        Stop(silent: true);
        _ = ConnectAsync();
    }

    public void FocusView()
    {
        if (_status == TabStatus.Connected) Dispatcher.UIThread.Post(() => _screen.Focus(), DispatcherPriority.Background);
    }

    private void SetStatus(TabStatus s, string text)
    {
        _state.Text = text;
        bool connected = s == TabStatus.Connected;
        _card.IsVisible = !connected;
        _scroller.IsVisible = connected;
        _toolbar.IsVisible = connected;
        _reconnect.IsVisible = s == TabStatus.Disconnected;
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
                var ui = SshUiAvalonia.Create(_owner, _askPassword, (_, _) => { });
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
                    _client = await VncClient.ConnectAsync(host, port, async () =>
                    {
                        asked = true;
                        return _request.Password ?? await AskPassword(null);
                    });
                    break;
                }
                catch (VncAuthException ex) when (attempt < 4 && asked)
                {
                    _request.Password = null;
                    if (await AskPassword(ex.Message) == null) throw new OperationCanceledException("Cancelled.");
                }
            }
            if (_disposed) { Stop(silent: true); return; }

            var client = _client;
            client.Closed += error => Dispatcher.UIThread.Post(() =>
            {
                if (_client != client) return;
                Stop(silent: true);
                SetStatus(TabStatus.Disconnected, error == null ? "Disconnected." : "The connection was closed.\n\n" + error.Message);
            });
            client.ClipboardReceived += text => Dispatcher.UIThread.Post(() => _ = _screen.ReceiveClipboard(text));
            _screen.Attach(client);
            _info.Text = $"{(client.DesktopName.Length > 0 ? client.DesktopName : s.Host)} · {client.Width}×{client.Height}";
            client.Resized += () => Dispatcher.UIThread.Post(() => _info.Text = $"{(client.DesktopName.Length > 0 ? client.DesktopName : s.Host)} · {client.Width}×{client.Height}");
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

    /// <summary>Asks for the VNC password (after a failed attempt, with the reason); null = cancelled.</summary>
    private async Task<string?> AskPassword(string? failure)
    {
        var s = _request.Settings;
        var prompt = (failure != null ? failure + "\n\n" : "") + $"VNC password for {s.Host}:";
        var result = await Dispatcher.UIThread.InvokeAsync(() => _askPassword(prompt, _request.SavePasswordTo != null));
        if (result == null) return null;
        _request.Password = result.Value.Password;
        if (result.Value.Save && _request.SavePasswordTo is Guid id)
        {
            try { CredentialStore.Set(id, result.Value.Password, ""); }
            catch (Exception ex) { await Dialogs.Info(_owner(), ex.Message); }
        }
        return result.Value.Password;
    }

    private void Stop(bool silent = false)
    {
        var client = _client;
        _client = null;
        _screen.Detach();
        client?.Dispose();
        _route?.Dispose();
        _route = null;
        if (!silent) SetStatus(TabStatus.Disconnected, "Disconnected.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop(silent: true);
    }
}

/// <summary>Draws the remote screen and sends keyboard and mouse input to it.</summary>
public sealed class VncScreen : Control
{
    private VncClient? _client;
    private WriteableBitmap? _bitmap;
    private readonly object _dirtyLock = new();
    private VncRect _dirty;
    private bool _redrawPosted;
    private int _buttons, _lastX = -1, _lastY = -1;
    private readonly Dictionary<Key, uint> _pressed = new();
    private bool _suppressText;
    private string? _lastClipboard;

    public bool Fit { get => _fit; set { _fit = value; InvalidateMeasure(); InvalidateVisual(); } }
    private bool _fit = true;
    public bool ViewOnly { get; set; }
    public bool ShareClipboard { get; set; } = true;

    public VncScreen()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public void Attach(VncClient client)
    {
        Detach();
        _client = client;
        client.FrameUpdated += OnFrameUpdated;
        client.Resized += OnResized;
        Dispatcher.UIThread.Post(() => { RebuildBitmap(); InvalidateMeasure(); });
    }

    public void Detach()
    {
        if (_client != null)
        {
            _client.FrameUpdated -= OnFrameUpdated;
            _client.Resized -= OnResized;
        }
        _client = null;
        _pressed.Clear();
        _buttons = 0;
    }

    private void OnResized() => Dispatcher.UIThread.Post(() => { RebuildBitmap(); InvalidateMeasure(); });

    private void OnFrameUpdated(VncRect r)
    {
        lock (_dirtyLock)
        {
            _dirty = _dirty.Union(r);
            if (_redrawPosted) return;
            _redrawPosted = true;
        }
        Dispatcher.UIThread.Post(CopyDirty, DispatcherPriority.Render);
    }

    private void RebuildBitmap()
    {
        var c = _client;
        if (c == null || c.Width == 0 || c.Height == 0) return;
        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(new PixelSize(c.Width, c.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        lock (_dirtyLock) _dirty = new VncRect(0, 0, c.Width, c.Height);
        CopyDirty();
    }

    private unsafe void CopyDirty()
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
            if (bmp.PixelSize.Width != c.Width || bmp.PixelSize.Height != c.Height) return; // a resize is on its way
            using var fb = bmp.Lock();
            int right = Math.Min(r.X + r.Width, c.Width), bottom = Math.Min(r.Y + r.Height, c.Height);
            for (int y = r.Y; y < bottom; y++)
            {
                var src = c.Pixels.AsSpan(y * c.Width + r.X, right - r.X);
                src.CopyTo(new Span<uint>((byte*)fb.Address + (long)y * fb.RowBytes + r.X * 4, right - r.X));
            }
        }
        InvalidateVisual();
    }

    // ───────────── Layout and drawing ─────────────

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_fit || _bitmap == null) return default;
        // Actual size: one remote pixel per screen pixel.
        return new Size(_bitmap.PixelSize.Width / Scaling, _bitmap.PixelSize.Height / Scaling);
    }

    /// <summary>Where the remote screen is drawn, in this control's coordinates.</summary>
    private Rect ScreenRect()
    {
        if (_bitmap == null) return default;
        double w = _bitmap.PixelSize.Width, h = _bitmap.PixelSize.Height;
        if (!_fit) return new Rect(0, 0, w / Scaling, h / Scaling);
        var b = Bounds;
        double scale = Math.Min(b.Width / w, b.Height / h);
        if (!double.IsFinite(scale) || scale <= 0) return default;
        double dw = w * scale, dh = h * scale;
        return new Rect((b.Width - dw) / 2, (b.Height - dh) / 2, dw, dh);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (_bitmap == null) return;
        var dest = ScreenRect();
        if (dest.Width <= 0) return;
        bool exact = Math.Abs(dest.Width * Scaling - _bitmap.PixelSize.Width) < 0.5;
        RenderOptions.SetBitmapInterpolationMode(this, exact ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height), dest);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    // ───────────── Mouse ─────────────

    private (int X, int Y)? RemotePoint(Point p)
    {
        var dest = ScreenRect();
        if (_bitmap == null || dest.Width <= 0) return null;
        int x = (int)((p.X - dest.X) * _bitmap.PixelSize.Width / dest.Width);
        int y = (int)((p.Y - dest.Y) * _bitmap.PixelSize.Height / dest.Height);
        return (Math.Clamp(x, 0, _bitmap.PixelSize.Width - 1), Math.Clamp(y, 0, _bitmap.PixelSize.Height - 1));
    }

    private void SendPointer(PointerEventArgs e, int extra = 0)
    {
        if (_client == null || ViewOnly) return;
        var point = e.GetCurrentPoint(this);
        var pr = point.Properties;
        _buttons = (pr.IsLeftButtonPressed ? 1 : 0) | (pr.IsMiddleButtonPressed ? 2 : 0) | (pr.IsRightButtonPressed ? 4 : 0);
        if (RemotePoint(point.Position) is not { } p) return;
        if (extra == 0 && p.X == _lastX && p.Y == _lastY && e is not PointerPressedEventArgs and not PointerReleasedEventArgs) return;
        (_lastX, _lastY) = p;
        if (extra != 0)
        {
            _client.SendPointer(p.X, p.Y, _buttons | extra);
        }
        _client.SendPointer(p.X, p.Y, _buttons);
    }

    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); SendPointer(e); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        e.Pointer.Capture(this);
        SendPointer(e);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        SendPointer(e);
        if (_buttons == 0) e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_client == null || ViewOnly) return;
        int steps = (int)Math.Round(Math.Abs(e.Delta.Y));
        if (steps == 0 && e.Delta.Y != 0) steps = 1;
        for (int i = 0; i < Math.Min(steps, 10); i++) SendPointer(e, e.Delta.Y > 0 ? 8 : 16);
        if (e.Delta.X != 0) SendPointer(e, e.Delta.X > 0 ? 64 : 32);
        e.Handled = true;
    }

    // ───────────── Keyboard ─────────────

    public void SendTap(uint keysym)
    {
        if (_client == null || ViewOnly) return;
        _client.SendKey(keysym, true);
        _client.SendKey(keysym, false);
    }

    public void SendCtrlAltDel()
    {
        if (_client == null || ViewOnly) return;
        _client.SendKey(VncKeys.ControlL, true);
        _client.SendKey(VncKeys.AltL, true);
        _client.SendKey(VncKeys.Delete, true);
        _client.SendKey(VncKeys.Delete, false);
        _client.SendKey(VncKeys.AltL, false);
        _client.SendKey(VncKeys.ControlL, false);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _suppressText = false;
        if (_client == null || ViewOnly) return;
        // Held modifiers as tracked here: right Alt is AltGr on many layouts, so it doesn't count (its characters come as text).
        bool ctrl = _pressed.ContainsKey(Key.LeftCtrl) || _pressed.ContainsKey(Key.RightCtrl), alt = _pressed.ContainsKey(Key.LeftAlt);
        uint? sym = SpecialKey(e.Key);
        // With Ctrl or Alt held, letters and digits don't arrive as text: send the key itself (Ctrl+C, Alt+F...).
        if (sym == null && (ctrl || alt) && !(ctrl && alt) && PlainKey(e.Key) is { } plain) sym = plain;
        if (sym == null) return; // the character arrives as TextInput

        if (!_pressed.ContainsKey(e.Key))
        {
            _pressed[e.Key] = sym.Value;
        }
        _client.SendKey(sym.Value, true);
        _suppressText = true;
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_client == null) return;
        if (_pressed.Remove(e.Key, out var sym))
        {
            _client.SendKey(sym, false);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_client == null || ViewOnly || string.IsNullOrEmpty(e.Text)) return;
        if (_suppressText) { _suppressText = false; e.Handled = true; return; }
        foreach (var rune in e.Text.EnumerateRunes())
        {
            if (rune.Value < 0x20) continue;
            uint sym = VncKeys.FromChar(rune.Value);
            _client.SendKey(sym, true);
            _client.SendKey(sym, false);
        }
        e.Handled = true;
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        _ = SendClipboard();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        // Release keys still held, or they would stay pressed on the remote computer.
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

    /// <summary>Text copied on this computer goes to the remote clipboard when the screen gets the focus.</summary>
    private async Task SendClipboard()
    {
        if (!ShareClipboard || ViewOnly || _client == null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        string? text;
        try { text = await clipboard.GetTextAsync(); } catch { return; }
        if (string.IsNullOrEmpty(text) || text == _lastClipboard) return;
        _lastClipboard = text;
        _client?.SendClipboard(text);
    }

    /// <summary>Text copied on the remote computer.</summary>
    public async Task ReceiveClipboard(string text)
    {
        if (!ShareClipboard || text.Length == 0) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        text = text.Replace("\r\n", "\n");
        _lastClipboard = text;
        try { await clipboard.SetTextAsync(text); } catch { }
    }
}
