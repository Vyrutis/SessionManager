using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Renci.SshNet;

namespace SessionManager;

/// <summary>SSH session rendered with xterm.js inside WebView2, connected through SSH.NET, with an SFTP panel beside it.</summary>
public sealed class SshTerminalView : Grid, ISessionView
{
    private const string VirtualHost = "sessionmanager.local";

    private readonly ConnectRequest _request;
    private readonly Func<string, bool, (string Password, bool Save)?> _askPassword;
    private readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.FromArgb(12, 12, 12) };
    private readonly SftpPanel _files = new();
    private readonly ColumnDefinition _filesColumn = new() { Width = new GridLength(0) };
    private readonly GridSplitter _splitter = new() { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
    private double _filesWidth = 360;

    private SshRoute? _route;
    private SshClient? _client;
    private ShellStream? _shell;
    private uint _cols = 80, _rows = 24;
    private bool _ready, _disposed, _connecting, _showFiles, _broadcast;
    private TabStatus _status = TabStatus.Connecting;

    public FrameworkElement Element => this;
    public TabStatus Status => _status;
    public event Action<TabStatus>? StatusChanged;
    /// <summary>Raised for everything the user types (used by multi-exec).</summary>
    public event Action<SshTerminalView, string>? Typed;

    /// <param name="askPassword">(prompt text, offerSave) → password, or null if cancelled.</param>
    public SshTerminalView(ConnectRequest request, Func<string, bool, (string Password, bool Save)?> askPassword, bool showFiles)
    {
        _request = request;
        _askPassword = askPassword;
        Background = new SolidColorBrush(Color.FromRgb(12, 12, 12));

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(_filesColumn);
        Children.Add(_web);
        _splitter.Style = (Style)Application.Current.FindResource("Splitter");
        _splitter.SetResourceReference(BackgroundProperty, "PanelBrush");
        SetColumn(_splitter, 1);
        Children.Add(_splitter);
        SetColumn(_files, 2);
        Children.Add(_files);
        ShowFiles = showFiles;
    }

    /// <summary>Whether the SFTP panel is shown beside the terminal.</summary>
    public bool ShowFiles
    {
        get => _showFiles;
        set
        {
            if (_showFiles && _filesColumn.ActualWidth > 50) _filesWidth = _filesColumn.ActualWidth;
            _showFiles = value;
            _files.Visibility = _splitter.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _filesColumn.Width = new GridLength(value ? _filesWidth : 0);
            _filesColumn.MinWidth = value ? 220 : 0;
        }
    }

    /// <summary>Shows the multi-exec frame in the terminal.</summary>
    public bool Broadcast
    {
        get => _broadcast;
        set { _broadcast = value; if (_ready) Post(new { type = "broadcast", on = value }); }
    }

    public void ShowFind() => Post(new { type = "find" });

    public async void Start()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(await WebViewHost.Environment);
        }
        catch (Exception ex)
        {
            MessageBox.Show("The embedded terminal could not start. Make sure the Microsoft Edge WebView2 Runtime is installed.\n\n" + ex.Message,
                "Session Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (_disposed) return;

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.SetVirtualHostNameToFolderMapping(VirtualHost, Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            CoreWebView2HostResourceAccessKind.Deny);
        core.WebMessageReceived += OnWebMessage;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith($"https://{VirtualHost}/")) e.Cancel = true; };

        var s = _request.Settings;
        var query = $"fs={s.FontSize}&bg={Uri.EscapeDataString(Theme.TerminalBackground)}&scheme={Uri.EscapeDataString(s.ColorScheme ?? "")}" +
                    (string.IsNullOrWhiteSpace(s.FontFamily) ? "" : $"&font={Uri.EscapeDataString(s.FontFamily)}");
        _web.Source = new Uri($"https://{VirtualHost}/terminal.html?{query}");
        Theme.Changed += OnThemeChanged;
        OnThemeChanged();
    }

    private void OnThemeChanged()
    {
        var c = (Color)ColorConverter.ConvertFromString(TerminalSchemes.Background(_request.Settings.ColorScheme));
        Background = new SolidColorBrush(c);
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        if (_ready) Post(new { type = "theme", bg = Theme.TerminalBackground });
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        switch (m.GetProperty("type").GetString())
        {
            case "ready":
                _cols = (uint)Math.Max(m.GetProperty("cols").GetInt32(), 10);
                _rows = (uint)Math.Max(m.GetProperty("rows").GetInt32(), 2);
                if (!_ready)
                {
                    _ready = true;
                    if (_broadcast) Post(new { type = "broadcast", on = true });
                    _ = ConnectAsync();
                }
                break;
            case "resize":
                _cols = (uint)Math.Max(m.GetProperty("cols").GetInt32(), 10);
                _rows = (uint)Math.Max(m.GetProperty("rows").GetInt32(), 2);
                try { _shell?.ChangeWindowSize(_cols, _rows, 0, 0); } catch { }
                break;
            case "input":
                var data = m.GetProperty("data").GetString() ?? "";
                if (_status == TabStatus.Disconnected)
                {
                    if (data == "\r") Reconnect();
                    return;
                }
                SendInput(data);
                Typed?.Invoke(this, data);
                break;
            case "copy":
                try { Clipboard.SetText(m.GetProperty("text").GetString() ?? ""); } catch { }
                break;
            case "paste":
                try
                {
                    if (Clipboard.ContainsText())
                        Post(new { type = "paste", text = Clipboard.GetText().Replace("\r\n", "\n") });
                }
                catch { }
                break;
            case "link":
                // Only web links; the page is untrusted output from the server.
                if (Uri.TryCreate(m.GetProperty("uri").GetString(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                {
                    try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                }
                break;
        }
    }

    /// <summary>Sends keystrokes to the server (also used by multi-exec).</summary>
    public void SendInput(string data)
    {
        var shell = _shell;
        if (shell == null) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            shell.Write(bytes, 0, bytes.Length);
            shell.Flush();
        }
        catch
        {
            // The read loop notices the closed connection and updates the status.
        }
    }

    private void Post(object message)
    {
        if (_disposed || _web.CoreWebView2 == null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void Write(string text) => Post(new { type = "output", data = text });

    private void Log(string text, LogKind kind)
    {
        var color = kind switch { LogKind.Warning => "33", LogKind.Error => "31", _ => "90" };
        Write($"\x1b[{color}m{text}\x1b[0m\r\n");
    }

    private SshUi Ui => SshUiWpf.Create(this, _askPassword, Log);

    private void SetStatus(TabStatus s)
    {
        if (_status == s) return;
        _status = s;
        StatusChanged?.Invoke(s);
    }

    public void Reconnect()
    {
        if (!_ready || _connecting) return;
        Cleanup();
        Write("\r\n");
        _ = ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        _connecting = true;
        SetStatus(TabStatus.Connecting);
        var s = _request.Settings;
        var target = $"{(string.IsNullOrEmpty(s.Username) ? "" : s.Username + "@")}{s.Host}";
        Log($"Connecting to {target} on port {s.Port}…", LogKind.Info);

        try
        {
            var ui = Ui;
            var route = _route = await SshConnector.OpenRouteAsync(s, _request.Jump, ui);
            if (_disposed) { Cleanup(); return; } // tab closed while the jump host connected: don't prompt
            var login = new SshLogin { Settings = s, Password = _request.Password, SaveId = _request.SavePasswordTo };
            var client = await SshConnector.ConnectAsync(login, route.Host, route.Port, ui,
                i => new SshClient(i) { KeepAliveInterval = TimeSpan.FromSeconds(30) });
            _request.Password = login.Password; // reconnects and SFTP reuse what was typed
            _client = client;
            if (_disposed) { Cleanup(); return; }

            // Both wait for the server (a remote forward waits for its reply), so keep them off the UI thread.
            var background = SshUiWpf.Create(this, _askPassword, (t, k) => Dispatcher.InvokeAsync(() => Log(t, k)));
            var (cols, rows) = (_cols, _rows);
            var shell = await Task.Run(() =>
            {
                SshConnector.StartTunnels(client, s.Tunnels, background);
                return client.CreateShellStream("xterm-256color", cols, rows, 0, 0, 65536);
            });
            if (_disposed || _client != client) { try { shell.Dispose(); } catch { } return; } // already cleaned up
            _shell = shell;
            if ((cols, rows) != (_cols, _rows)) try { shell.ChangeWindowSize(_cols, _rows, 0, 0); } catch { }
            SetStatus(TabStatus.Connected);
            _ = Task.Run(() => ReadLoop(shell));

            _files.Attach(() => SshConnector.ConnectAsync(
                new SshLogin { Settings = s, Password = _request.Password }, route.Host, route.Port, ui, i => new SftpClient(i)));
        }
        catch (Exception ex)
        {
            Cleanup();
            Disconnected(ex.Message);
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
                var text = new string(chars, 0, c);
                Dispatcher.InvokeAsync(() => Write(text));
            }
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            error = ex.Message;
        }
        catch (ObjectDisposedException) { }

        Dispatcher.InvokeAsync(() =>
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

    public void FocusView()
    {
        _web.Focus();
        Post(new { type = "focus" });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Theme.Changed -= OnThemeChanged;
        Cleanup();
        _files.Dispose();
        _web.Dispose();
    }
}
