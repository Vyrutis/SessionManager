using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace SessionManager;

/// <summary>Hosts the Windows Remote Desktop ActiveX control (mstscax.dll) in a tab.</summary>
public sealed class RdpView : Grid, ISessionView
{
    // Newest first: "Microsoft RDP Client Control" versions 11, 10, 9, 7, 2.
    private static readonly string[] ControlClsids =
    {
        "A0C63C30-F08D-4AB4-907C-34905D770C7D",
        "8B918B82-7985-4C24-89DF-C33AD2BBFBCD",
        "A3BC03A0-041D-42E3-AD22-882B7865C9C5",
        "D2EA46A7-C2BF-426B-AF24-E19C44456399",
        "7CACBD7B-0D99-468F-AC33-22E495C0AFE5",
    };

    private readonly ConnectRequest _request;
    private readonly Func<string, bool, (string Password, bool Save)?> _askPassword;
    private readonly WinForms.Panel _panel = new() { BackColor = System.Drawing.Color.Black, Dock = WinForms.DockStyle.Fill };
    private readonly WindowsFormsHost _host;
    private readonly Border _overlay;
    private readonly Button _reconnectButton;
    private readonly TextBlock _overlayText = new() { Foreground = Brushes.White, FontSize = 15, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _resizeDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private RdpAxHost? _ax;
    private dynamic? _ocx;
    private SshRoute? _route;
    private bool _started, _disposed, _everConnected, _passwordAsked, _connecting;
    /// <summary>Size the remote desktop currently has (device pixels).</summary>
    private (int W, int H) _remoteSize;
    private int _resizeRetries;
    private bool _dynamicResizeUnsupported;
    private TabStatus _status = TabStatus.Connecting;

    public FrameworkElement Element => this;
    public TabStatus Status => _status;
    public event Action<TabStatus>? StatusChanged;

    public RdpView(ConnectRequest request, Func<string, bool, (string Password, bool Save)?> askPassword)
    {
        _request = request;
        _askPassword = askPassword;
        Background = Brushes.Black;

        _host = new WindowsFormsHost { Child = _panel };
        Children.Add(_host);

        var reconnect = _reconnectButton = new Button { Content = "Reconnect", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        reconnect.Click += (_, _) => Reconnect();
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 520 };
        stack.Children.Add(_overlayText);
        stack.Children.Add(reconnect);
        _overlay = new Border { Child = stack, Visibility = Visibility.Collapsed };
        _overlay.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _overlayText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        Children.Add(_overlay);

        _poll.Tick += (_, _) => PollState();
        _resizeDebounce.Tick += (_, _) => { _resizeDebounce.Stop(); UpdateRemoteSize(); };
        _panel.Resize += (_, _) => ScheduleResize();
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        // Wait until the panel has a real size so the remote desktop matches the tab.
        if (_panel.IsHandleCreated && _panel.ClientSize.Width > 50) Connect();
        else
        {
            void OnSize(object? s, EventArgs e)
            {
                if (_panel.ClientSize.Width <= 50 || _panel.ClientSize.Height <= 50) return;
                _panel.SizeChanged -= OnSize;
                Dispatcher.BeginInvoke(Connect);
            }
            _panel.SizeChanged += OnSize;
        }
    }

    private static string? FindControlClsid() =>
        ControlClsids.FirstOrDefault(id => Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{id}}}") != null);

    private async void Connect()
    {
        if (_disposed || _connecting) return;
        DisposeControl();
        var s = _request.Settings;

        var clsid = FindControlClsid();
        if (clsid == null)
        {
            ShowOverlay("The Windows Remote Desktop control (mstscax.dll) is not available on this PC.");
            SetStatus(TabStatus.Disconnected);
            return;
        }

        if (_request.Password == null && !_passwordAsked)
        {
            _passwordAsked = true;
            var user = string.IsNullOrEmpty(s.Domain) ? s.Username : $"{s.Domain}\\{s.Username}";
            var result = _askPassword($"Password for {(string.IsNullOrEmpty(user) ? "" : user + " on ")}{s.Host}:", _request.SavePasswordTo != null);
            if (result != null)
            {
                _request.Password = result.Value.Password;
                if (result.Value.Save && _request.SavePasswordTo is Guid saveId)
                {
                    try { CredentialStore.Set(saveId, result.Value.Password, s.Username); }
                    catch (Exception ex) { MessageBox.Show(ex.Message, "Session Manager"); }
                }
            }
        }

        SetStatus(TabStatus.Connecting);

        // Through a jump host: RDP connects to a local port forwarded over SSH.
        string server = s.Host;
        int port = s.Port;
        if (_request.Jump != null)
        {
            _connecting = true;
            ShowOverlay($"Connecting to jump host {_request.Jump.Settings.Host}…", showButton: false);
            try
            {
                var ui = new SshUi { AskPassword = _askPassword, Anchor = this, Log = (text, kind) => { if (kind != LogKind.Info) _overlayText.Text = text; } };
                _route = await SshConnector.OpenRouteAsync(s, _request.Jump, ui);
                server = _route.Host;
                port = _route.Port;
            }
            catch (Exception ex)
            {
                ShowOverlay("Could not connect through the jump host.\n\n" + ex.Message);
                SetStatus(TabStatus.Disconnected);
                return;
            }
            finally { _connecting = false; }
            if (_disposed) { DisposeControl(); return; }
        }

        HideOverlay();
        try
        {
            _ax = new RdpAxHost(clsid) { Dock = WinForms.DockStyle.Fill };
            _panel.Controls.Add(_ax);
            _ax.CreateControl();
            _ocx = _ax.Ocx ?? throw new InvalidOperationException("The Remote Desktop control did not load.");
            var ocx = _ocx;

            ocx.Server = server;
            if (!string.IsNullOrEmpty(s.Username)) ocx.UserName = s.Username;
            if (!string.IsNullOrEmpty(s.Domain)) ocx.Domain = s.Domain;

            int w = s.Width, h = s.Height;
            if (w <= 0 || h <= 0) (w, h) = PanelSize();
            ocx.DesktopWidth = w;
            ocx.DesktopHeight = h;
            _remoteSize = (w, h);
            _resizeRetries = 0;
            ocx.ColorDepth = s.ColorDepth;

            Try(() => ocx.AdvancedSettings2.RDPPort = port);
            // A fixed resolution is scaled to fit the tab; "Fit to tab" resizes the remote desktop itself.
            Try(() => ocx.AdvancedSettings2.SmartSizing = s.SmartSizing || s.Width > 0);
            Try(() => ocx.AdvancedSettings2.RedirectDrives = s.RedirectDrives);
            Try(() => ocx.AdvancedSettings2.RedirectPrinters = s.RedirectPrinters);
            Try(() => ocx.AdvancedSettings6.RedirectClipboard = s.RedirectClipboard);
            Try(() => ocx.AdvancedSettings7.EnableCredSspSupport = true);
            Try(() => ocx.AdvancedSettings5.AuthenticationLevel = 2); // warn if the server can't be authenticated
            Try(() => ocx.AdvancedSettings2.EnableAutoReconnect = true);
            Try(() => ocx.AdvancedSettings2.allowBackgroundInput = 1);
            Try(() => ocx.SecuredSettings3.KeyboardHookMode = 1); // Alt+Tab, Win key etc. go to the remote PC
            Try(() => ocx.AdvancedSettings9.BitmapPersistence = 1);

            if (!string.IsNullOrEmpty(_request.Password))
                SetPassword(ocx, _request.Password);

            ocx.Connect();
            _poll.Start();
        }
        catch (Exception ex)
        {
            ShowOverlay("Could not start the Remote Desktop connection.\n\n" + ex.Message);
            SetStatus(TabStatus.Disconnected);
        }
    }

    private static void SetPassword(object ocx, string password)
    {
        try
        {
            ((IMsTscNonScriptable)ocx).put_ClearTextPassword(password);
        }
        catch
        {
            Try(() => ((dynamic)ocx).AdvancedSettings2.ClearTextPassword = password);
        }
    }

    private void PollState()
    {
        if (_ocx == null) return;
        int connected;
        try { connected = Convert.ToInt32(_ocx.Connected); }
        catch { connected = 0; }

        if (connected == 1)
        {
            if (_status != TabStatus.Connected)
            {
                // Once logged in, apply the display scaling (DPI) and any resize that happened while connecting.
                _resizeRetries = 0;
                _resizeDebounce.Interval = TimeSpan.FromSeconds(2);
                _resizeDebounce.Stop();
                _resizeDebounce.Start();
            }
            _everConnected = true;
            SetStatus(TabStatus.Connected);
        }
        else if (connected == 0)
        {
            _poll.Stop();
            string detail = "";
            try
            {
                int ext = Convert.ToInt32(_ocx.ExtendedDisconnectReason);
                if (ext != 0) detail = $"\n\n(Extended reason code {ext})";
            }
            catch { }
            ShowOverlay((_everConnected ? "Disconnected from " : "Could not connect to ") + _request.Settings.Host + "." + detail);
            SetStatus(TabStatus.Disconnected);
            DisposeControl();
        }
    }

    private void ScheduleResize()
    {
        if (_ocx == null) return;
        _resizeRetries = 0;
        _resizeDebounce.Interval = TimeSpan.FromMilliseconds(400);
        _resizeDebounce.Stop();
        _resizeDebounce.Start();
    }

    /// <summary>The tab's size in device pixels, as the remote desktop accepts it (even width, 200–8192).</summary>
    private (int W, int H) PanelSize()
    {
        int w = Math.Clamp(_panel.ClientSize.Width, 200, 8192);
        int h = Math.Clamp(_panel.ClientSize.Height, 200, 8192);
        return (w - w % 2, h); // the RDP display protocol rejects odd widths
    }

    private double Dpi => VisualTreeHelper.GetDpi(this).PixelsPerInchX;

    /// <summary>Makes the remote desktop match the tab ("Fit to tab" sessions).</summary>
    private void UpdateRemoteSize()
    {
        if (_ocx is not { } ocx || _status != TabStatus.Connected) return;
        var s = _request.Settings;
        if (s.SmartSizing || s.Width > 0) return; // the control scales the picture itself
        if (_panel.ClientSize.Width < 100 || _panel.ClientSize.Height < 100) return; // minimized or hidden tab

        var (w, h) = PanelSize();
        double dpi = Dpi;
        uint scale = (uint)Math.Clamp(Math.Round(dpi / 96 * 100), 100, 500);
        uint deviceScale = scale >= 180 ? 180u : scale >= 140 ? 140u : 100u;
        bool sizeChanged = (w, h) != _remoteSize;
        if (!sizeChanged && scale == 100) return;

        if (!_dynamicResizeUnsupported)
        {
            try
            {
                uint mmW = (uint)Math.Round(w * 25.4 / dpi), mmH = (uint)Math.Round(h * 25.4 / dpi);
                ocx.UpdateSessionDisplaySettings((uint)w, (uint)h, mmW, mmH, 0u, scale, deviceScale);
                _remoteSize = (w, h);
                return;
            }
            catch (Exception ex)
            {
                // Fails until the login has fully completed; retry a few times before giving up.
                if (++_resizeRetries <= 5)
                {
                    _resizeDebounce.Interval = TimeSpan.FromSeconds(1);
                    _resizeDebounce.Start();
                    return;
                }
                _dynamicResizeUnsupported = true;
                App.Log(new InvalidOperationException("Remote Desktop live resize is not supported; falling back.", ex));
            }
        }
        if (!sizeChanged) return;

        // Older servers can't resize a live session: reconnect at the new size (what mstsc does),
        // and if even that isn't possible, scale the picture instead.
        try
        {
            ocx.Reconnect((uint)w, (uint)h);
            _remoteSize = (w, h);
        }
        catch
        {
            Try(() => ocx.AdvancedSettings2.SmartSizing = true);
        }
    }

    private void ShowOverlay(string text, bool showButton = true)
    {
        _overlayText.Text = text;
        _reconnectButton.Visibility = showButton ? Visibility.Visible : Visibility.Collapsed;
        _overlay.Visibility = Visibility.Visible;
        _host.Visibility = Visibility.Hidden;
    }

    private void HideOverlay()
    {
        _overlay.Visibility = Visibility.Collapsed;
        _host.Visibility = Visibility.Visible;
    }

    private void SetStatus(TabStatus s)
    {
        if (_status == s) return;
        _status = s;
        StatusChanged?.Invoke(s);
    }

    public void Reconnect()
    {
        if (_disposed) return;
        _everConnected = false;
        Connect();
    }

    public void FocusView()
    {
        try { _ax?.Focus(); } catch { }
    }

    private void DisposeControl()
    {
        _poll.Stop();
        _route?.Dispose();
        _route = null;
        var ax = _ax;
        object? ocx = _ocx;
        _ax = null;
        _ocx = null;
        if (ax == null) return;
        if (ocx is not null)
        {
            dynamic d = ocx;
            try { if (Convert.ToInt32(d.Connected) != 0) d.Disconnect(); } catch { }
        }
        _panel.Controls.Remove(ax);
        try { ax.Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _resizeDebounce.Stop();
        DisposeControl();
        _host.Dispose();
    }

    private static void Try(Action a)
    {
        try { a(); } catch { /* setting not supported by this control version */ }
    }
}

/// <summary>Minimal ActiveX wrapper; the control is driven late-bound through IDispatch.</summary>
internal sealed class RdpAxHost : WinForms.AxHost
{
    public RdpAxHost(string clsid) : base(clsid) { }
    public object? Ocx => GetOcx();
}

/// <summary>Non-scriptable interface of the RDP control, needed to pass a password.</summary>
[ComImport, Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsTscNonScriptable
{
    void put_ClearTextPassword([MarshalAs(UnmanagedType.BStr)] string password);
}
