using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using SessionManager;

/// <summary>
/// The web session tab against a small HTTPS server on 127.0.0.1 with a self-signed certificate: trusting the certificate,
/// a changed certificate, filling a login form and the browser's own login box. Runs in a child process with its own data
/// folder (its own browser profile too); nothing is shown on screen.
/// </summary>
static class WebViewTests
{
    public const string Flag = "--web-wpf";

    public static void RunIsolated(Action<string, bool, string> check)
    {
        var data = Path.Combine(Path.GetTempPath(), "sm-web-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(data);
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, Flag) { RedirectStandardOutput = true, UseShellExecute = false };
        psi.Environment["SESSIONMANAGER_DATA"] = data;
        using var child = System.Diagnostics.Process.Start(psi)!;
        int reported = 0;
        bool done = false;
        var until = DateTime.UtcNow.AddSeconds(120);
        while (!done && DateTime.UtcNow < until && child.StandardOutput.ReadLine() is { } line)
        {
            if (line == "DONE") { done = true; break; }
            if (!line.StartsWith("PASS  ") && !line.StartsWith("FAIL  ")) continue;
            reported++;
            var text = line[6..];
            int arrow = text.IndexOf("  -> ", StringComparison.Ordinal);
            check(arrow < 0 ? text : text[..arrow], line.StartsWith("PASS"), arrow < 0 ? "" : text[(arrow + 5)..]);
        }
        if (!done) check("Web session tab finishes", false, $"{reported} results before it stopped");
        try { child.Kill(true); child.WaitForExit(5000); } catch { }
        for (int i = 0; i < 10 && Directory.Exists(data); i++)
        {
            try { Directory.Delete(data, true); } catch { Thread.Sleep(300); } // the browser process may still be closing
        }
    }

    public static void Run(Action<string, bool, string> check)
    {
        if (!SessionStore.DataDir.Contains("sm-web-test-"))
        {
            check("Web session tab uses a test data folder", false, SessionStore.DataDir);
            return;
        }
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try { await Body(check); }
                catch (Exception ex) { error = ex; }
                finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(100));
        if (error != null) check("Web session tab", false, error.ToString());
    }

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static async Task<bool> WaitFor(Func<bool> condition, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) return false;
            await Task.Delay(100);
        }
        return true;
    }

    private static async Task Body(Action<string, bool, string> check)
    {
        Theme.Apply(false, new UiSettings { Animations = false });
        using var server = new TestServer("router.lan");
        var fingerprint = CertificateFingerprint.Of(server.Certificate.RawData);

        int asked = 0;
        string? changedFrom = "unset";
        bool answer = true;
        typeof(WebSessionView).GetField("AskTrust", Any)!.SetValue(null,
            (Func<Window?, string, X509Certificate2, string, string, string?, bool>)((_, _, cert, _, fp, changed) =>
            {
                asked++;
                changedFrom = changed;
                return answer && fp == CertificateFingerprint.Of(cert.RawData);
            }));

        var folder = new Node { Kind = NodeKind.Folder, Protocol = Protocol.Web, IsRoot = true };
        var node = new Node { Kind = NodeKind.Session, Protocol = Protocol.Web, Name = "Router", Parent = folder,
            Settings = new SessionSettings { Host = "127.0.0.1", Port = server.Port, Username = "admin" } };
        var request = new ConnectRequest { Session = node, Settings = node.Settings.Clone(), Password = "pw-1" };
        string? remembered = null;
        var view = new WebSessionView(request, fp => remembered = fp);
        var w = new Window
        {
            Content = view, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 1000, Height = 700,
            ShowActivated = false, ShowInTaskbar = false,
        };
        w.Show();
        view.Start();

        bool loaded = await WaitFor(() => view.Status != TabStatus.Connecting);
        check("self-signed page opens after trusting its certificate", loaded && view.Status == TabStatus.Connected && asked == 1 && changedFrom == null,
            $"status={view.Status} asked={asked}");
        check("the trusted certificate is remembered by fingerprint", remembered == fingerprint && request.Settings.TrustedCertificate == fingerprint,
            $"{remembered} vs {fingerprint}");

        // Fill login puts the saved username and password into the form.
        var web = (Microsoft.Web.WebView2.Wpf.WebView2)typeof(WebSessionView).GetField("_web", Any)!.GetValue(view)!;
        await (Task)typeof(WebSessionView).GetMethod("FillLogin", Any)!.Invoke(view, null)!;
        var values = await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('u').value + '|' + document.getElementById('p').value");
        check("Fill login fills the username and password boxes", values == "\"admin|pw-1\"", values);
        var tricky = (string)typeof(WebSessionView).GetMethod("FillScript", Any)!.Invoke(null, ["a\"b", "p'</script>\\x"])!;
        check("the login is passed to the page as JSON, not pasted into the script", tricky.Contains("\"a\\u0022b\"") && !tricky.Contains("</script>"), tricky[^60..]);

        // A page that asks with the browser's login box gets the saved login.
        typeof(WebSessionView).GetMethod("Navigate", Any)!.Invoke(view, [$"https://127.0.0.1:{server.Port}/secure"]);
        bool secure = await WaitFor(() => server.SecureHits > 0 && web.CoreWebView2.Source.EndsWith("/secure"));
        await WaitFor(() => view.Status == TabStatus.Connected);
        var body = await web.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
        check("the browser's own login box gets the saved login", secure && server.AcceptedLogin == "admin:pw-1" && body.Contains("welcome"),
            $"login={server.AcceptedLogin} body={body}");

        // A different certificate on the same device: asked again, with a warning; refusing stops the page.
        using var other = new TestServer("router.lan");
        asked = 0;
        answer = false;
        var request2 = new ConnectRequest { Session = node, Settings = node.Settings.Clone(), Password = "pw-1" };
        request2.Settings.Port = other.Port;
        request2.Settings.TrustedCertificate = fingerprint;
        remembered = null;
        var view2 = new WebSessionView(request2, fp => remembered = fp);
        w.Content = view2;
        view2.Start();
        bool done2 = await WaitFor(() => view2.Status == TabStatus.Disconnected);
        check("a changed certificate is asked about again, with the old one shown", done2 && asked == 1 && changedFrom == fingerprint,
            $"status={view2.Status} asked={asked} changedFrom={changedFrom}");
        check("refusing a certificate keeps the old one and doesn't open the page", remembered == null && request2.Settings.TrustedCertificate == fingerprint
            && other.PageHits == 0, $"pageHits={other.PageHits}");
        var message = ((System.Windows.Controls.TextBlock)typeof(WebSessionView).GetField("_overlayText", Any)!.GetValue(view2)!).Text;
        check("refusing says the certificate wasn't trusted", message.Contains("certificate was not trusted"), message);

        // Pictures to look at: the tab (toolbar + message; the page itself is drawn by Edge and isn't captured) and the dialog.
        Render((FrameworkElement)w.Content, Path.Combine(Path.GetTempPath(), "sm-web-tab.png"));
        var dlg = new SessionDialog(w, Protocol.Web, "New web session", "", SessionSettings.DefaultsFor(Protocol.Web), false,
            Array.Empty<CredentialProfile>(), Array.Empty<(Guid, string)>())
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        dlg.Show();
        dlg.UpdateLayout();
        check("web session dialog shows the page and https options",
            ((FrameworkElement)dlg.FindName("WebGroup")).Visibility == Visibility.Visible && ((FrameworkElement)dlg.FindName("RdpGroup")).Visibility == Visibility.Collapsed
            && ((FrameworkElement)dlg.FindName("DomainBox")).Visibility == Visibility.Collapsed, "");
        var hostBox = (System.Windows.Controls.TextBox)dlg.FindName("HostBox");
        hostBox.Text = "http://printer.lan:8080/status";
        typeof(SessionDialog).GetMethod("SplitWebAddress", Any)!.Invoke(dlg, null);
        check("pasting a whole address into Host fills host, port, page and http",
            hostBox.Text == "printer.lan" && ((System.Windows.Controls.TextBox)dlg.FindName("PortBox")).Text == "8080"
            && ((System.Windows.Controls.TextBox)dlg.FindName("WebPathBox")).Text == "/status"
            && ((System.Windows.Controls.CheckBox)dlg.FindName("WebHttpsBox")).IsChecked == false, hostBox.Text);
        hostBox.Text = "10.0.0.1";
        ((System.Windows.Controls.TextBox)dlg.FindName("PortBox")).Text = "443";
        ((System.Windows.Controls.CheckBox)dlg.FindName("WebHttpsBox")).IsChecked = true;
        ((System.Windows.Controls.TextBox)dlg.FindName("WebPathBox")).Text = "";
        dlg.UpdateLayout();
        Render((FrameworkElement)dlg.Content, Path.Combine(Path.GetTempPath(), "sm-web-dialog.png"));
        dlg.Close();

        view.Dispose();
        view2.Dispose();
        w.Close();
    }

    private static void Render(FrameworkElement e, string path)
    {
        e.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)e.ActualWidth, (int)e.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var v = new System.Windows.Media.DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(Theme.Get("WindowBrush"), null, new Rect(0, 0, e.ActualWidth, e.ActualHeight));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(e), null, new Rect(0, 0, e.ActualWidth, e.ActualHeight));
        }
        bmp.Render(v);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }

    /// <summary>A tiny HTTPS server: "/" is a login form, "/secure" asks for a login with the browser's login box.</summary>
    private sealed class TestServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private volatile bool _stopped;
        public X509Certificate2 Certificate { get; }
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int PageHits, SecureHits;
        public volatile string? AcceptedLogin;

        public TestServer(string name)
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(name);
            req.CertificateExtensions.Add(san.Build());
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30));
            Certificate = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!_stopped)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(TcpClient client)
        {
            using var _ = client;
            try
            {
                using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsServerAsync(Certificate);
                while (true)
                {
                    var head = await ReadHead(ssl);
                    if (head == null) return;
                    var lines = head.Split("\r\n");
                    var path = lines[0].Split(' ') is { Length: > 1 } parts ? parts[1] : "/";
                    string status = "200 OK", extra = "", html;
                    if (path == "/secure")
                    {
                        Interlocked.Increment(ref SecureHits);
                        var auth = lines.FirstOrDefault(l => l.StartsWith("Authorization: Basic ", StringComparison.OrdinalIgnoreCase));
                        var login = auth == null ? null : Encoding.UTF8.GetString(Convert.FromBase64String(auth[21..].Trim()));
                        if (login == "admin:pw-1")
                        {
                            AcceptedLogin = login;
                            html = "<html><body><p>welcome</p></body></html>";
                        }
                        else
                        {
                            status = "401 Unauthorized";
                            extra = "WWW-Authenticate: Basic realm=\"lab\"\r\n";
                            html = "<html><body>login needed</body></html>";
                        }
                    }
                    else if (path == "/")
                    {
                        Interlocked.Increment(ref PageHits);
                        html = "<html><head><title>Router</title></head><body><form><input id=u type=text name=user><input id=p type=password name=pass><button>Log in</button></form></body></html>";
                    }
                    else { status = "404 Not Found"; html = ""; }
                    var bytes = Encoding.UTF8.GetBytes(html);
                    var response = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\n{extra}\r\n";
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes(response));
                    await ssl.WriteAsync(bytes);
                    await ssl.FlushAsync();
                }
            }
            catch { /* the browser closed the connection, or refused the certificate */ }
        }

        private static async Task<string?> ReadHead(Stream s)
        {
            var buf = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                if (await s.ReadAsync(one) == 0) return null;
                buf.Add(one[0]);
                int n = buf.Count;
                if (n >= 4 && buf[n - 4] == '\r' && buf[n - 3] == '\n' && buf[n - 2] == '\r' && buf[n - 1] == '\n')
                    return Encoding.ASCII.GetString(buf.ToArray());
                if (n > 65536) return null;
            }
        }

        public void Dispose()
        {
            _stopped = true;
            _listener.Stop();
            Certificate.Dispose();
        }
    }
}
