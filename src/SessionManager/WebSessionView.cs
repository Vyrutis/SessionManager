using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SessionManager;

/// <summary>
/// A device's web page (router, switch, iLO, printer…) in a tab, using the Edge (WebView2) engine. Self-signed certificates
/// can be trusted once per session and are pinned by fingerprint; logins come from the session's saved credentials.
/// </summary>
public sealed class WebSessionView : Grid, ISessionView
{
    private readonly ConnectRequest _request;
    private readonly Action<string> _certificateTrusted;
    private readonly WebView2 _web = new();
    private readonly Border _toolbar;
    private readonly Button _back, _forward, _reload, _fill;
    private readonly TextBlock _lock = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
    private readonly TextBox _address = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Border _overlay;
    private readonly TextBlock _overlayText = new() { FontSize = 15, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _retry = new() { Content = "Try again", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };

    private SshRoute? _route;
    private string _home = "";
    private string _targetHost = "";
    private bool _ready, _disposed, _connecting, _loadedOnce, _authTried, _certRefused;
    private TabStatus _status = TabStatus.Connecting;

    public FrameworkElement Element => this;
    public TabStatus Status => _status;
    public event Action<TabStatus>? StatusChanged;

    /// <summary>Asks whether to trust a certificate; replaced in tests.</summary>
    internal static Func<Window?, string, System.Security.Cryptography.X509Certificates.X509Certificate2, string, string, string?, bool> AskTrust =
        Dialogs.TrustCertificate;

    /// <param name="certificateTrusted">Called with the fingerprint when the user trusts a certificate, so the session can remember it.</param>
    public WebSessionView(ConnectRequest request, Action<string> certificateTrusted)
    {
        _request = request;
        _certificateTrusted = certificateTrusted;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        Button Tool(string glyph, string tip, Action click, string? label = null)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("Icons"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            if (label != null) content.Children.Add(new TextBlock { Text = label, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = content, ToolTip = tip, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(1, 0, 1, 0) };
            b.SetResourceReference(StyleProperty, "ToolButton");
            b.Click += (_, _) => click();
            return b;
        }
        _back = Tool("", "Back (Alt+Left)", () => { if (_web.CanGoBack) _web.GoBack(); });
        _forward = Tool("", "Forward (Alt+Right)", () => { if (_web.CanGoForward) _web.GoForward(); });
        _reload = Tool("", "Reload (F5)", () => _web.Reload());
        var home = Tool("", "Go to the session's start page", () => Navigate(_home));
        _fill = Tool("", "Fill in the username and password saved for this session", () => _ = FillLogin(), "Fill login");
        var open = Tool("", "Open this page in your web browser", OpenInBrowser, "Open in browser");
        _address.SetResourceReference(Control.BackgroundProperty, "InputBrush");
        _address.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _address.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        _address.ToolTip = "Address of this page. Type another one and press Enter to go there.";
        _address.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var text = _address.Text.Trim();
            Navigate(text.Contains("://") ? text : "https://" + text);
            e.Handled = true;
        };
        _lock.FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("Icons");

        var bar = new DockPanel { LastChildFill = true };
        foreach (var b in new UIElement[] { _back, _forward, _reload, home }) { DockPanel.SetDock(b, Dock.Left); bar.Children.Add(b); }
        DockPanel.SetDock(open, Dock.Right);
        bar.Children.Add(open);
        DockPanel.SetDock(_fill, Dock.Right);
        bar.Children.Add(_fill);
        DockPanel.SetDock(_lock, Dock.Left);
        bar.Children.Add(_lock);
        bar.Children.Add(_address);
        _toolbar = new Border { Child = bar, Padding = new Thickness(4, 3, 4, 3), BorderThickness = new Thickness(0, 0, 0, 1) };
        _toolbar.SetResourceReference(Border.BackgroundProperty, "ToolbarBrush");
        _toolbar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Children.Add(_toolbar);

        SetRow(_web, 1);
        Children.Add(_web);

        _retry.Click += (_, _) => Reconnect();
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 560 };
        stack.Children.Add(_overlayText);
        stack.Children.Add(_retry);
        _overlay = new Border { Child = stack };
        _overlay.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _overlayText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        SetRow(_overlay, 1);
        Children.Add(_overlay);
        UpdateButtons();
    }

    // ───────────── Connection ─────────────

    public void Start() => Dispatcher.BeginInvoke(() => _ = ConnectAsync(), DispatcherPriority.Background);

    public void Reconnect()
    {
        if (_connecting || _disposed) return;
        if (_route != null && _route.Jump?.IsConnected == true && _ready) { Navigate(_home); return; }
        StopRoute();
        _ = ConnectAsync();
    }

    public void FocusView()
    {
        if (_ready && _overlay.Visibility != Visibility.Visible) Dispatcher.BeginInvoke(() => _web.Focus(), DispatcherPriority.Input);
    }

    private void SetStatus(TabStatus s, string? text)
    {
        _overlayText.Text = text ?? "";
        _overlay.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        _retry.Visibility = s == TabStatus.Disconnected ? Visibility.Visible : Visibility.Collapsed;
        if (_status == s) return;
        _status = s;
        StatusChanged?.Invoke(s);
    }

    private async Task ConnectAsync()
    {
        if (_disposed) return;
        var s = _request.Settings;
        _connecting = true;
        _authTried = false;
        _targetHost = s.Host;
        SetStatus(TabStatus.Connecting, $"Opening {s.WebUrl()}…");
        try
        {
            string host = s.Host;
            int port = s.Port;
            if (_request.Jump != null)
            {
                SetStatus(TabStatus.Connecting, $"Connecting to jump host {_request.Jump.Settings.Host}…");
                var ui = SshUiWpf.Create(this, (prompt, save) => Dialogs.PromptPassword(Window.GetWindow(this), "Password", prompt, save), (_, _) => { });
                _route = await SshConnector.OpenRouteAsync(s, _request.Jump, ui);
                (host, port) = (_route.Host, _route.Port);
                _targetHost = host;
                if (_disposed) { StopRoute(); return; }
                SetStatus(TabStatus.Connecting, $"Opening {s.WebUrl()} through the jump host…");
            }
            _home = s.WebUrl(host, port);

            if (!_ready)
            {
                try { await _web.EnsureCoreWebView2Async(await WebViewHost.Environment); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("The built-in browser could not start. Make sure the Microsoft Edge WebView2 Runtime is installed.\n\n" + ex.Message);
                }
                if (_disposed) return;
                Setup(_web.CoreWebView2);
                _ready = true;
            }
            Navigate(_home);
        }
        catch (Exception ex)
        {
            StopRoute();
            SetStatus(TabStatus.Disconnected, ex is OperationCanceledException ? "Cancelled." : "Could not open the page.\n\n" + ex.Message);
        }
        finally
        {
            _connecting = false;
        }
    }

    private void Setup(CoreWebView2 core)
    {
        core.Settings.IsStatusBarEnabled = true;
        core.Settings.IsPasswordAutosaveEnabled = false; // passwords live in Credential Manager, not in the browser profile
        core.Settings.IsGeneralAutofillEnabled = false;
        core.ServerCertificateErrorDetected += OnCertificateError;
        core.BasicAuthenticationRequested += OnBasicAuthentication;
        core.NavigationStarting += (_, _) => { _lock.Text = ""; UpdateButtons(); };
        core.NavigationCompleted += OnNavigationCompleted;
        core.SourceChanged += (_, _) => UpdateAddress();
        core.HistoryChanged += (_, _) => UpdateButtons();
        // Links that open a new window stay in this tab.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") core.Navigate(e.Uri);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5) { _web.Reload(); e.Handled = true; }
            else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control) { _address.Focus(); _address.SelectAll(); e.Handled = true; }
        };
    }

    private void Navigate(string url)
    {
        if (!_ready || _disposed) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            MessageBox.Show(Window.GetWindow(this), $"\"{url}\" is not a web address.", "Session Manager");
            return;
        }
        if (!_loadedOnce) SetStatus(TabStatus.Connecting, $"Opening {_request.Settings.WebUrl()}…");
        _certRefused = false;
        _web.CoreWebView2.Navigate(uri.AbsoluteUri);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        UpdateButtons();
        UpdateAddress();
        if (e.IsSuccess)
        {
            _loadedOnce = true;
            _authTried = false;
            SetStatus(TabStatus.Connected, null);
            return;
        }
        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled && _loadedOnce && !_certRefused) return;
        var why = _certRefused ? "The certificate was not trusted, so the page was not opened." : e.WebErrorStatus switch
        {
            CoreWebView2WebErrorStatus.HostNameNotResolved => "The host name could not be found. Check the address.",
            CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ServerUnreachable =>
                "Nothing answered on that address and port. Check that the device is on and that http / https and the port are right.",
            CoreWebView2WebErrorStatus.Timeout => "The device didn't answer in time.",
            CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset or CoreWebView2WebErrorStatus.Disconnected =>
                "The connection was closed by the device. If it only speaks http (or only https), change that in the session settings.",
            CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or CoreWebView2WebErrorStatus.CertificateExpired or
            CoreWebView2WebErrorStatus.CertificateIsInvalid or CoreWebView2WebErrorStatus.CertificateRevoked or
            CoreWebView2WebErrorStatus.ClientCertificateContainsErrors => "The page's certificate was not trusted.",
            CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired => "The login was cancelled or not accepted.",
            CoreWebView2WebErrorStatus.OperationCanceled => "Cancelled.",
            _ => $"The page could not be opened ({e.WebErrorStatus}).",
        };
        SetStatus(TabStatus.Disconnected, why);
    }

    private void UpdateButtons()
    {
        _back.IsEnabled = _ready && _web.CanGoBack;
        _forward.IsEnabled = _ready && _web.CanGoForward;
        _reload.IsEnabled = _ready;
    }

    private void UpdateAddress()
    {
        if (!_ready) return;
        var src = _web.CoreWebView2.Source;
        if (src.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) src = _home; // nothing loaded yet
        if (!_address.IsKeyboardFocused) _address.Text = src;
        bool https = src.StartsWith("https:", StringComparison.OrdinalIgnoreCase);
        _lock.Text = https ? "" : ""; // lock / unlock
        _lock.SetResourceReference(TextBlock.ForegroundProperty, https ? "SshBrush" : "SubTextBrush");
        _lock.ToolTip = https
            ? (_trustedThisTab ? "Encrypted, with a certificate you trusted for this session" : "Encrypted (https)")
            : "Not encrypted (http)";
    }

    // ───────────── Certificates ─────────────

    private bool _trustedThisTab;

    private void OnCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        var host = Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var uri) ? uri.Host.Trim('[', ']') : "";
        if (!host.Equals(_targetHost, StringComparison.OrdinalIgnoreCase))
        {
            e.Action = CoreWebView2ServerCertificateErrorAction.Default; // only the session's own device can be trusted here
            return;
        }
        using var cert = e.ServerCertificate.ToX509Certificate2();
        var fingerprint = CertificateFingerprint.Of(cert.RawData);
        var s = _request.Settings;
        if (CertificateFingerprint.Matches(s.TrustedCertificate, fingerprint))
        {
            e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
            _trustedThisTab = true;
            return;
        }

        var problem = e.ErrorStatus switch
        {
            CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect => "the certificate was issued for a different name",
            CoreWebView2WebErrorStatus.CertificateExpired => "the certificate has expired",
            CoreWebView2WebErrorStatus.CertificateRevoked => "the certificate has been revoked",
            _ => cert.Issuer == cert.Subject ? "the certificate is self-signed (normal for many devices)" : "the certificate isn't trusted by Windows",
        };
        var changedFrom = string.IsNullOrEmpty(s.TrustedCertificate) ? null : s.TrustedCertificate;
        // Shown after the event returns, so the page's network request isn't held up by a modal dialog inside the callback.
        var deferral = e.GetDeferral();
        var x509 = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert.RawData);
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_disposed) { e.Action = CoreWebView2ServerCertificateErrorAction.Cancel; return; }
                bool trust = AskTrust(Window.GetWindow(this), s.Host, x509, problem, fingerprint, changedFrom);
                e.Action = trust ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow : CoreWebView2ServerCertificateErrorAction.Cancel;
                _certRefused = !trust;
                if (trust)
                {
                    s.TrustedCertificate = fingerprint;
                    _trustedThisTab = true;
                    _certificateTrusted(fingerprint);
                }
            }
            finally
            {
                x509.Dispose();
                deferral.Complete();
            }
        });
    }

    // ───────────── Logins ─────────────

    /// <summary>Pages that ask with the browser's own login box (HTTP authentication) get the saved login once; after that the user is asked.</summary>
    private void OnBasicAuthentication(object? sender, CoreWebView2BasicAuthenticationRequestedEventArgs e)
    {
        var s = _request.Settings;
        // The saved login goes only to the session's own device; any other host that asks (a redirect, an embedded page) gets a prompt.
        var askingHost = Uri.TryCreate(e.Uri, UriKind.Absolute, out var asking) ? asking.Host.Trim('[', ']') : "";
        if (!_authTried && !string.IsNullOrEmpty(_request.Password) && askingHost.Equals(_targetHost, StringComparison.OrdinalIgnoreCase))
        {
            _authTried = true;
            e.Response.UserName = s.Username;
            e.Response.Password = _request.Password;
            return;
        }
        var deferral = e.GetDeferral();
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var host = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ? uri.Authority : s.Host;
                var label = (_authTried ? "The saved login was not accepted. " : "") + $"{host} asks for a login" +
                            (string.IsNullOrWhiteSpace(e.Challenge) ? ":" : $" ({e.Challenge.Trim()}):");
                var login = AskLogin(label);
                if (login == null) { e.Cancel = true; return; }
                e.Response.UserName = login.Value.User;
                e.Response.Password = login.Value.Password;
                _authTried = true;
            }
            finally { deferral.Complete(); }
        });
    }

    /// <summary>Asks for a username and password and saves them (if wanted) like other sessions do.</summary>
    private (string User, string Password)? AskLogin(string label)
    {
        var s = _request.Settings;
        (string User, string Password, bool Save)? r;
        if (_authTried && _request.RetryLogin != null)
        {
            var retry = _request.RetryLogin(label, _request.SavePasswordTo != null);
            r = retry == null ? null : (retry.Username ?? s.Username, retry.Password, retry.Save);
        }
        else r = Dialogs.PromptLogin(Window.GetWindow(this), "Log in", label, s.Username, _request.SavePasswordTo != null);
        if (r == null) return null;
        s.Username = r.Value.User;
        _request.Password = r.Value.Password;
        if (r.Value.Save && _request.SavePasswordTo is Guid id)
        {
            try
            {
                CredentialStore.Set(id, r.Value.Password, r.Value.User);
                if (_request.Session.Settings is { CredentialId: null } saved && saved.Username != r.Value.User)
                {
                    saved.Username = r.Value.User;
                    _request.Session.NotifySettingsChanged();
                }
            }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), "The password could not be saved.\n\n" + ex.Message, "Session Manager"); }
        }
        return (r.Value.User, r.Value.Password);
    }

    /// <summary>Puts the username and password into the login form on the page (the first visible password box and the field before it).</summary>
    private async Task FillLogin()
    {
        if (!_ready) return;
        var page = Uri.TryCreate(_web.CoreWebView2.Source, UriKind.Absolute, out var uri) ? uri.Host.Trim('[', ']') : "";
        if (!page.Equals(_targetHost, StringComparison.OrdinalIgnoreCase) &&
            MessageBox.Show(Window.GetWindow(this), $"This page is on {page}, not on {_request.Settings.Host}.\n\nFill in the login for {_request.Session.Name} anyway?",
                "Fill login", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        if (!_web.CoreWebView2.Source.StartsWith("https:", StringComparison.OrdinalIgnoreCase) &&
            MessageBox.Show(Window.GetWindow(this), "This page is not encrypted (http). The password would travel over the network in clear text.\n\nFill in the login anyway?",
                "Fill login", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var user = _request.Settings.Username;
        var password = _request.Password;
        if (string.IsNullOrEmpty(password))
        {
            var login = AskLogin($"Login for {_request.Session.Name}:");
            if (login == null) return;
            (user, password) = login.Value;
        }
        string result;
        try { result = await _web.CoreWebView2.ExecuteScriptAsync(FillScript(user, password)); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), "Could not fill in the login.\n\n" + ex.Message, "Fill login"); return; }
        if (result.Contains("none"))
            MessageBox.Show(Window.GetWindow(this), "There is no password box on this page.\n\nIf the page asks for the login in a pop-up box instead, the saved login is sent automatically.",
                "Fill login", MessageBoxButton.OK, MessageBoxImage.Information);
        _web.Focus();
    }

    internal static string FillScript(string user, string password) => $$"""
        (function (u, p) {
          const visible = e => !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
          const docs = d => { let all = [d]; for (const f of d.querySelectorAll('iframe,frame')) { try { if (f.contentDocument) all = all.concat(docs(f.contentDocument)); } catch (x) {} } return all; };
          const set = (el, v) => {
            Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), 'value').set.call(el, v);
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
          };
          for (const d of docs(document)) {
            const pw = [...d.querySelectorAll('input[type=password]')].find(visible);
            if (!pw) continue;
            let user = null;
            for (const i of d.querySelectorAll('input')) {
              if (i === pw) break;
              if (visible(i) && /^(text|email|tel|)$/i.test(i.getAttribute('type') || '')) user = i;
            }
            if (user && u) set(user, u);
            set(pw, p);
            pw.focus();
            return 'filled';
          }
          return 'none';
        })({{JsonSerializer.Serialize(user)}}, {{JsonSerializer.Serialize(password)}})
        """;

    private void OpenInBrowser()
    {
        var url = _ready && !string.IsNullOrEmpty(_web.CoreWebView2?.Source) && !_web.CoreWebView2.Source.StartsWith("about:")
            ? _web.CoreWebView2.Source : _request.Settings.WebUrl();
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), "Could not open your web browser.\n\n" + ex.Message, "Session Manager"); }
    }

    private void StopRoute()
    {
        _route?.Dispose();
        _route = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRoute();
        try { _web.Dispose(); } catch { }
    }
}
