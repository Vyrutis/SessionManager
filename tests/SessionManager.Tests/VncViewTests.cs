using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SessionManager;

/// <summary>
/// The WPF VNC tab against a local test server: connects, draws the remote picture, and is rendered off-screen
/// (RenderTargetBitmap, no screen capture) to %TEMP%\sm-vnc-wpf.png.
/// </summary>
static class VncViewTests
{
    /// <summary>
    /// Runs the test in a child process (this runner with --vnc-wpf): WPF on a test thread keeps a process from exiting,
    /// so the child is ended once it has reported. The app itself runs WPF on its main thread and exits normally.
    /// </summary>
    public static void RunIsolated(Action<string, bool, string> check)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--vnc-wpf") { RedirectStandardOutput = true, UseShellExecute = false };
        using var child = System.Diagnostics.Process.Start(psi)!;
        int reported = 0;
        var until = DateTime.UtcNow.AddSeconds(90);
        while (reported < 2 && DateTime.UtcNow < until && child.StandardOutput.ReadLine() is { } line)
        {
            if (!line.StartsWith("PASS  ") && !line.StartsWith("FAIL  ")) continue;
            reported++;
            var text = line[6..];
            int arrow = text.IndexOf("  -> ", StringComparison.Ordinal);
            check(arrow < 0 ? text : text[..arrow], line.StartsWith("PASS"), arrow < 0 ? "" : text[(arrow + 5)..]);
        }
        if (reported < 2) check("VNC tab (WPF) reports its results", false, $"{reported} of 2 results");
        try { child.Kill(); } catch { }
    }

    public static void Run(Action<string, bool, string> check)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            // The test runs inside a real dispatcher loop, which is then shut down the way WPF expects.
            var app = new App();
            app.InitializeComponent();
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
        thread.Join(TimeSpan.FromSeconds(60));
        if (error != null) check("VNC tab (WPF)", false, error.ToString());
    }

    private static uint Picture(int x, int y)
    {
        if (y >= 372) return x < 40 ? 0xFF2F6FDF : 0xFF1E2430;
        if (x is >= 120 and < 460 && y is >= 70 and < 300) return y < 96 ? 0xFF3B4A63 : 0xFFF7F8FA;
        return 0xFF000000 | (uint)(20 + y / 5) << 16 | (uint)(70 + y / 6) << 8 | (uint)(120 + x / 8);
    }

    private static async Task Body(Action<string, bool, string> check)
    {
        Theme.Apply(dark: true);
        int port = StartServer(640, 400);
        var node = new Node { Kind = NodeKind.Session, Protocol = Protocol.Vnc, Name = "Reception PC", Settings = new SessionSettings { Host = "127.0.0.1", Port = port, SmartSizing = true, RedirectClipboard = false } }; // never touch the real clipboard
        var view = new VncView(new ConnectRequest { Session = node, Settings = node.Settings! }, (_, _) => null);
        var window = new Window
        {
            Width = 900, Height = 520, Content = view, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
        };
        window.Show();
        view.Start();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (view.Status != TabStatus.Connected && DateTime.UtcNow < until) await Task.Delay(30);
        await Task.Delay(500); // the first picture
        check("VNC tab (WPF) connects to the test server", view.Status == TabStatus.Connected, view.Status.ToString());

        window.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(view);
        var path = Path.Combine(Path.GetTempPath(), "sm-vnc-wpf.png");
        using (var f = File.Create(path))
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            enc.Save(f);
        }

        // The middle of the tab shows the remote window's white area (the picture is scaled to fit).
        var px = new byte[4];
        bmp.CopyPixels(new Int32Rect((int)view.ActualWidth / 2, (int)view.ActualHeight / 2, 1, 1), px, 4, 0);
        bool white = px[0] > 230 && px[1] > 230 && px[2] > 230;
        check("VNC tab (WPF) draws the remote screen", white, $"center pixel {px[2]:X2}{px[1]:X2}{px[0]:X2}, picture saved to {path}");

        view.Dispose();
        window.Close();
    }

    /// <summary>A server without a password that sends one raw picture.</summary>
    private static int StartServer(int w, int h)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            listener.Stop();
            var s = socket.GetStream();
            async Task<byte[]> Read(int n) { var b = new byte[n]; await s.ReadExactlyAsync(b); return b; }
            byte[] BE(int v, int size) { var b = new byte[size]; if (size == 2) BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); else BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }
            await s.WriteAsync("RFB 003.008\n"u8.ToArray());
            await Read(12);
            await s.WriteAsync(new byte[] { 1, 1 });
            await Read(1);
            await s.WriteAsync(BE(0, 4));
            await Read(1);
            var name = "Reception PC"u8.ToArray();
            await s.WriteAsync((byte[])[.. BE(w, 2), .. BE(h, 2), 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0, .. BE(name.Length, 4), .. name]);
            var setup = await Read(24);
            await Read(4 * BinaryPrimitives.ReadUInt16BigEndian(setup.AsSpan(22)) + 10);
            var msg = new List<byte> { 0, 0, 0, 1 };
            msg.AddRange([.. BE(0, 2), .. BE(0, 2), .. BE(w, 2), .. BE(h, 2), .. BE(0, 4)]);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint c = Picture(x, y);
                msg.AddRange([(byte)c, (byte)(c >> 8), (byte)(c >> 16), 0]);
            }
            await s.WriteAsync(msg.ToArray());
            var buf = new byte[256];
            try { while (await s.ReadAsync(buf) > 0) { } } catch { }
        });
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
