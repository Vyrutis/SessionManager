using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace SessionManager;

/// <summary>
/// A Remote Desktop session run by FreeRDP: in FreeRDP's own window (default), or drawn inside the tab on X11.
/// The tab shows the status, FreeRDP's messages and Reconnect / Disconnect buttons.
/// </summary>
public sealed class RdpTab : Grid, ISessionView
{
    private readonly ConnectRequest _request;
    private readonly Func<string, bool, Task<(string Password, bool Save)?>> _askPassword;
    private readonly Func<Window?> _owner;
    private readonly LinuxSettings _settings;
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly SelectableTextBlock _log = new() { FontFamily = new FontFamily("DejaVu Sans Mono, Noto Sans Mono, Consolas, monospace"), FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly Button _reconnect = new() { Content = "Reconnect", Classes = { "accent" } };
    private readonly Button _disconnect = new() { Content = "Disconnect" };
    private readonly Border _card;
    private readonly Border _logBox;
    private readonly EmbedHost? _host;
    private readonly List<string> _lines = new();

    private Process? _process;
    private SshRoute? _route;
    private bool _disposed, _connecting, _passwordAsked, _userStopped;
    private TabStatus _status = TabStatus.Connecting;

    public Control Element => this;
    public TabStatus Status => _status;
    public event Action<TabStatus>? StatusChanged;

    public RdpTab(ConnectRequest request, Func<string, bool, Task<(string Password, bool Save)?>> askPassword, Func<Window?> owner, LinuxSettings settings)
    {
        _request = request;
        _askPassword = askPassword;
        _owner = owner;
        _settings = settings;
        this.Res(BackgroundProperty, "SmWindow");

        _reconnect.Click += (_, _) => Reconnect();
        _disconnect.Click += (_, _) => Stop(byUser: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 14, 0, 0), Children = { _reconnect, _disconnect } };
        _logBox = new Border
        {
            IsVisible = false,
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(4),
            MaxHeight = 220,
            Child = new ScrollViewer { Content = _log },
        }.Res(Border.BackgroundProperty, "SmSidebar");
        _card = new Border
        {
            MaxWidth = 640,
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
                    buttons,
                    _logBox,
                },
            },
        }.Res(Border.BackgroundProperty, "SmCard");
        _title.Text = $"Remote Desktop: {request.Session.Name}";

        if (settings.RdpEmbedded && !OperatingSystem.IsWindows())
        {
            _host = new EmbedHost { IsVisible = false };
            Children.Add(_host);
        }
        Children.Add(_card);
    }

    // ───────────── Connection ─────────────

    public void Start() => Dispatcher.UIThread.Post(() => _ = ConnectAsync(), DispatcherPriority.Background);

    public void Reconnect()
    {
        if (_connecting || _disposed) return;
        Stop(byUser: false);
        _ = ConnectAsync();
    }

    private void SetStatus(TabStatus s, string text)
    {
        _state.Text = text;
        bool connected = s == TabStatus.Connected;
        _reconnect.IsVisible = s == TabStatus.Disconnected;
        _disconnect.IsVisible = s != TabStatus.Disconnected;
        if (_host != null)
        {
            _host.IsVisible = connected;
            _card.IsVisible = !connected;
        }
        if (_status == s) return;
        _status = s;
        StatusChanged?.Invoke(s);
    }

    private async Task ConnectAsync()
    {
        if (_disposed) return;
        var s = _request.Settings;
        _userStopped = false;
        _lines.Clear();
        _log.Text = "";
        _logBox.IsVisible = false;

        var client = FreeRdp.Find(_settings.RdpClient);
        if (client == null)
        {
            SetStatus(TabStatus.Disconnected, _settings.RdpClient.Length > 0
                ? $"The FreeRDP program \"{_settings.RdpClient}\" was not found. Check Settings."
                : "FreeRDP is not installed. Install it with:\n\n    sudo pacman -S freerdp\n\nthen choose Reconnect.");
            return;
        }

        if (_request.Password == null && !_passwordAsked)
        {
            _passwordAsked = true;
            var user = string.IsNullOrEmpty(s.Domain) ? s.Username : $"{s.Domain}\\{s.Username}";
            var result = await _askPassword($"Password for {(string.IsNullOrEmpty(user) ? "" : user + " on ")}{s.Host}:", _request.SavePasswordTo != null);
            if (result != null)
            {
                _request.Password = result.Value.Password;
                if (result.Value.Save && _request.SavePasswordTo is Guid saveId)
                {
                    try { CredentialStore.Set(saveId, result.Value.Password, s.Username); }
                    catch (Exception ex) { await Dialogs.Info(_owner(), ex.Message); }
                }
            }
        }

        _connecting = true;
        SetStatus(TabStatus.Connecting, $"Connecting to {s.Host}…");
        string server = s.Host;
        int port = s.Port;
        try
        {
            if (_request.Jump != null)
            {
                SetStatus(TabStatus.Connecting, $"Connecting to jump host {_request.Jump.Settings.Host}…");
                var ui = SshUiAvalonia.Create(_owner, _askPassword, (text, kind) => { if (kind != LogKind.Info) AddLine(text); });
                _route = await SshConnector.OpenRouteAsync(s, _request.Jump, ui);
                server = _route.Host;
                port = _route.Port;
                if (_disposed) { Stop(byUser: true); return; }
            }

            long parent = 0;
            (int W, int H) fit = FitSize();
            if (_host != null && client.IsX11)
            {
                _host.IsVisible = true;
                await Task.Delay(50); // let the native window be created and laid out
                parent = _host.WindowId;
                fit = _host.PixelSize;
                if (parent == 0) AddLine("Could not create a window inside the tab; using FreeRDP's own window.");
            }

            var args = FreeRdp.Arguments(client, new FreeRdp.Options
            {
                Server = server, Port = port, Settings = s, Password = _request.Password,
                Title = _request.Session.Name, FitSize = fit, ParentWindow = parent,
                TrustOnFirstUse = _settings.RdpTrustOnFirstUse, ExtraArgs = _settings.RdpExtraArgs,
            });
            StartProcess(client, args);
        }
        catch (Exception ex)
        {
            Stop(byUser: true);
            SetStatus(TabStatus.Disconnected, ex is OperationCanceledException ? "Cancelled." : "Could not connect.\n\n" + ex.Message);
        }
        finally
        {
            _connecting = false;
        }
    }

    /// <summary>Size of the area for "fit to window" sessions, in device pixels.</summary>
    private (int W, int H) FitSize()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var b = Bounds;
        return b.Width > 100 ? ((int)(b.Width * scaling), (int)(b.Height * scaling)) : (1600, 900);
    }

    private void StartProcess(FreeRdp.Client client, List<string> args)
    {
        var psi = new ProcessStartInfo(client.Path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        string? stdin;
        if (client.Major >= 3)
        {
            // Everything on stdin, one argument per line: the password is not visible in the process list.
            psi.ArgumentList.Add("/args-from:stdin");
            stdin = string.Join("\n", args) + "\n";
        }
        else
        {
            foreach (var a in args) psi.ArgumentList.Add(a);
            stdin = _request.Password != null ? _request.Password + "\n" : null;
        }

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Dispatcher.UIThread.Post(() => AddLine(e.Data)); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Dispatcher.UIThread.Post(() => AddLine(e.Data)); };
        p.Exited += (_, _) => Dispatcher.UIThread.Post(() => OnExited(p));
        p.Start();
        _process = p;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            if (stdin != null) p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        catch (IOException) { }

        SetStatus(TabStatus.Connecting, $"Connecting to {_request.Settings.Host} with {client.Name}…");
        // FreeRDP doesn't report "connected" in a stable way; still running after a few seconds means the session is up.
        DispatcherTimer.RunOnce(() =>
        {
            if (_process == p && !p.HasExited)
                SetStatus(TabStatus.Connected, _host?.IsVisible == true
                    ? "Connected."
                    : $"Connected. The remote desktop is open in its own window (\"{_request.Session.Name}\").");
        }, TimeSpan.FromSeconds(3));
    }

    private void AddLine(string line)
    {
        // FreeRDP logs look like "[12:00:00:000] [1234:1235] [WARN][com.freerdp...] - message"; keep the message.
        var i = line.IndexOf("] - ", StringComparison.Ordinal);
        var text = i > 0 ? line[(i + 4)..] : line;
        if (text.Contains("/p:", StringComparison.Ordinal)) return; // never echo a password
        _lines.Add(text);
        if (_lines.Count > 60) _lines.RemoveAt(0);
        _log.Text = string.Join("\n", _lines);
        _logBox.IsVisible = _lines.Count > 0;
    }

    private void OnExited(Process p)
    {
        if (_process != p) return;
        int code;
        try { code = p.ExitCode; } catch { code = -1; }
        _process = null;
        p.Dispose();
        _route?.Dispose();
        _route = null;
        if (_disposed) return;

        if (FreeRdp.IsAuthFailure(code))
        {
            _request.Password = null; // ask again on reconnect
            _passwordAsked = false;
        }
        var reason = _userStopped ? "Disconnected." : FreeRdp.ExitReason(code) ?? "The session ended.";
        SetStatus(TabStatus.Disconnected, reason);
    }

    private void Stop(bool byUser)
    {
        _userStopped = byUser;
        var p = _process;
        if (p != null && !p.HasExited)
        {
            try { p.Kill(); } catch { }
        }
        if (!byUser) _process = null; // reconnecting: the old process's exit is ignored
        _route?.Dispose();
        _route = null;
        if (byUser && p == null) SetStatus(TabStatus.Disconnected, "Disconnected.");
    }

    public void FocusView() => _host?.Focus();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop(byUser: true);
        _route?.Dispose();
        _route = null;
    }

    // ───────────── Embedding (X11) ─────────────

    /// <summary>A native X11 child window FreeRDP draws into (/parent-window). FreeRDP's window is resized with the tab.</summary>
    private sealed class EmbedHost : NativeControlHost
    {
        public long WindowId { get; private set; }

        public (int W, int H) PixelSize
        {
            get
            {
                var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
                return ((int)(Bounds.Width * scaling), (int)(Bounds.Height * scaling));
            }
        }

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            var handle = base.CreateNativeControlCore(parent);
            WindowId = handle.HandleDescriptor == "XID" ? handle.Handle.ToInt64() : 0;
            return handle;
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            WindowId = 0;
            base.DestroyNativeControlCore(control);
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            if (WindowId == 0) return;
            var (w, h) = PixelSize;
            X11.ResizeChildren(WindowId, w, h);
        }
    }

    private static class X11
    {
        [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
        [DllImport("libX11.so.6")] private static extern int XQueryTree(IntPtr display, IntPtr window, out IntPtr root, out IntPtr parent, out IntPtr children, out uint count);
        [DllImport("libX11.so.6")] private static extern int XResizeWindow(IntPtr display, IntPtr window, uint width, uint height);
        [DllImport("libX11.so.6")] private static extern int XFree(IntPtr data);
        [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);

        /// <summary>FreeRDP's window is a child of ours; resizing it makes /dynamic-resolution follow the tab size.</summary>
        public static void ResizeChildren(long window, int width, int height)
        {
            if (width < 50 || height < 50) return;
            try
            {
                var d = XOpenDisplay(IntPtr.Zero);
                if (d == IntPtr.Zero) return;
                try
                {
                    if (XQueryTree(d, new IntPtr(window), out _, out _, out var children, out var count) != 0 && children != IntPtr.Zero)
                    {
                        for (int i = 0; i < count; i++)
                            XResizeWindow(d, Marshal.ReadIntPtr(children, i * IntPtr.Size), (uint)width, (uint)height);
                        XFree(children);
                    }
                    XFlush(d);
                }
                finally { XCloseDisplay(d); }
            }
            catch (DllNotFoundException) { }
        }
    }
}
