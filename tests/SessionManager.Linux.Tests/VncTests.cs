using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SessionManager;

/// <summary>The VNC client against a scripted server on a local port: logins, every screen encoding, input and clipboard.</summary>
static class VncTests
{
    public static void Run(Action<string, bool, string> check)
    {
        void Check(string n, bool ok, string d = "") => check(n, ok, d);

        // ── DES and the VNC password response ──
        var keys = Des.Schedule(0x133457799BBCDFF1);
        Check("DES: FIPS test vector", Des.Encrypt(0x0123456789ABCDEF, keys) == 0x85E813540F0AB405, Des.Encrypt(0x0123456789ABCDEF, keys).ToString("X16"));
        var challenge = Enumerable.Range(0, 16).Select(i => (byte)(i * 17 + 3)).ToArray();
        if (ReferenceResponse("secret", challenge) is { } reference)
        {
            Check("VNC password response matches the system's DES", VncAuth.Response("secret", challenge).SequenceEqual(reference));
            Check("VNC password: only the first 8 characters count", VncAuth.Response("12345678xyz", challenge).SequenceEqual(ReferenceResponse("12345678", challenge)!));
        }
        else Console.WriteLine("SKIP  VNC password vs. system DES (DES not available here)");

        // ── Full session: login, every encoding, resize, clipboard, input ──
        try { FullSession(Check).GetAwaiter().GetResult(); }
        catch (Exception ex) { Check("VNC session against the test server", false, ex.ToString()); }

        // ── Login variants ──
        Check("Wrong password → VncAuthException", Throws<VncAuthException>(() => Login("RFB 003.008\n", [2], acceptPassword: false)));
        Check("RFB 3.3 server with a password", Login("RFB 003.003\n", [2], acceptPassword: true) == null);
        Check("RFB 3.7 server without a password", Login("RFB 003.007\n", [1], acceptPassword: true) == null);
        Check("UltraVNC's version 3.6 is treated as 3.3", Login("RFB 003.006\n", [2], acceptPassword: true) == null);
        var unsupported = Login("RFB 003.008\n", [19, 30], acceptPassword: true);
        Check("Unsupported login method: clear message", unsupported is NotSupportedException && unsupported.Message.Contains("VeNCrypt") && unsupported.Message.Contains("VNC password"),
            unsupported?.Message ?? "no error");
        var cancelled = Login("RFB 003.008\n", [2], acceptPassword: true, password: null);
        Check("Cancelled password prompt", cancelled is OperationCanceledException, cancelled?.GetType().Name ?? "no error");
        var http = Login("HTTP/1.1 400\n", [2], acceptPassword: true);
        Check("Not a VNC server: clear message", http is InvalidDataException && http.Message.Contains("not a VNC server"), http?.Message ?? "");
    }

    private static byte[]? ReferenceResponse(string password, byte[] challenge)
    {
        try
        {
            var key = new byte[8];
            var pw = Encoding.Latin1.GetBytes(password);
            for (int i = 0; i < 8 && i < pw.Length; i++)
            {
                int b = pw[i], r = 0;
                for (int k = 0; k < 8; k++) r |= ((b >> k) & 1) << (7 - k);
                key[i] = (byte)r;
            }
#pragma warning disable SYSLIB0021 // single DES on purpose: it is what VNC uses
            using var des = DES.Create();
#pragma warning restore SYSLIB0021
            des.Key = key;
            return des.EncryptEcb(challenge, PaddingMode.None);
        }
        catch (CryptographicException) { return null; }
        catch (PlatformNotSupportedException) { return null; }
    }

    private static bool Throws<T>(Func<Exception?> f) where T : Exception => f() is T;

    /// <summary>A server without a password that shows one picture (for the screenshot tests); returns its port.</summary>
    public static int StartPictureServer(int w, int h, Func<int, int, uint> picture)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            listener.Stop();
            var s = socket.GetStream();
            await s.WriteAsync("RFB 003.008\n"u8.ToArray());
            await Read(s, 12);
            await s.WriteAsync(new byte[] { 1, 1 });
            await Read(s, 1);
            await s.WriteAsync(BE32(0));
            await Read(s, 1);
            await s.WriteAsync(ServerInit(w, h, "Reception PC"));
            var setup = await Read(s, 24);
            await Read(s, 4 * BinaryPrimitives.ReadUInt16BigEndian(setup.AsSpan(22)) + 10);
            var msg = new List<byte> { 0, 0, 0, 1 };
            msg.AddRange(Rect(0, 0, w, h, 0));
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) msg.AddRange(Px(picture(x, y)));
            await s.WriteAsync(msg.ToArray());
            var buf = new byte[256];
            try { while (await s.ReadAsync(buf) > 0) { } } catch { }
        });
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // ───────────── Test server plumbing ─────────────

    private static async Task<(VncClient Client, NetworkStream Server, TcpClient ServerSocket)> Connect(
        Func<NetworkStream, Task> handshake, Func<Task<string?>> password)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        var connect = VncClient.ConnectAsync("127.0.0.1", port, password);
        var socket = await accept;
        listener.Stop();
        var server = socket.GetStream();
        var script = handshake(server);
        var done = await Task.WhenAny(connect, script);
        if (done == script) await script; // surfaces server-side failures
        var client = await connect.WaitAsync(TimeSpan.FromSeconds(10));
        await script.WaitAsync(TimeSpan.FromSeconds(10));
        return (client, server, socket);
    }

    /// <summary>Runs just the login; returns the exception the client threw, or null when it connected.</summary>
    private static Exception? Login(string version, byte[] types, bool acceptPassword, string? password = "secret")
    {
        try
        {
            var (client, _, socket) = Connect(async s =>
            {
                await s.WriteAsync(Encoding.ASCII.GetBytes(version));
                if (!version.StartsWith("RFB")) { await s.FlushAsync(); return; }
                var reply = await Read(s, 12);
                int minor = int.Parse(Encoding.ASCII.GetString(reply, 8, 3));
                int type;
                if (minor == 3)
                {
                    type = types[0];
                    await s.WriteAsync(BE32(type));
                }
                else
                {
                    await s.WriteAsync((byte[])[(byte)types.Length, .. types]);
                    var chosen = new byte[1];
                    if (await s.ReadAsync(chosen) == 0) return; // the client gave up (unsupported types)
                    type = chosen[0];
                }
                if (type == 2)
                {
                    var challenge = RandomNumberGenerator.GetBytes(16);
                    await s.WriteAsync(challenge);
                    var response = new byte[16];
                    if (await s.ReadAtLeastAsync(response, 16, throwOnEndOfStream: false) < 16) return; // cancelled
                    bool ok = acceptPassword && response.SequenceEqual(VncAuth.Response("secret", challenge));
                    await s.WriteAsync(BE32(ok ? 0 : 1));
                    if (!ok)
                    {
                        if (minor == 8) await s.WriteAsync((byte[])[.. BE32(14), .. "Authentication"u8]);
                        return;
                    }
                }
                else if (minor == 8) await s.WriteAsync(BE32(0));
                await Read(s, 1);
                await s.WriteAsync(ServerInit(32, 16, "login test"));
            }, () => Task.FromResult(password)).GetAwaiter().GetResult();
            client.Dispose();
            socket.Dispose();
            return null;
        }
        catch (Exception ex) { return ex; }
    }

    private static async Task<byte[]> Read(Stream s, int n)
    {
        var b = new byte[n];
        await s.ReadExactlyAsync(b);
        return b;
    }

    private static byte[] BE32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }
    private static byte[] BE16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); return b; }

    private static byte[] ServerInit(int w, int h, string name)
    {
        var n = Encoding.UTF8.GetBytes(name);
        return [.. BE16(w), .. BE16(h), 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0, .. BE32(n.Length), .. n];
    }

    private static byte[] Rect(int x, int y, int w, int h, int encoding) => [.. BE16(x), .. BE16(y), .. BE16(w), .. BE16(h), .. BE32(encoding)];
    private static byte[] Px(uint c) => [(byte)c, (byte)(c >> 8), (byte)(c >> 16), 0];
    private static byte[] CPx(uint c) => [(byte)c, (byte)(c >> 8), (byte)(c >> 16)];

    // ───────────── Full session ─────────────

    private const int W = 160, H = 80;

    private static async Task FullSession(Action<string, bool, string> check)
    {
        void Check(string n, bool ok, string d = "") => check(n, ok, d);
        var model = new uint[W * H];
        Array.Fill(model, 0xFF000000);
        void Paint(int x, int y, int w, int h, Func<int, int, uint> f)
        {
            for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
                model[(y + yy) * W + x + xx] = f(xx, yy);
        }

        byte[] clientSetup = [];
        var (client, s, socket) = await Connect(async s =>
        {
            await s.WriteAsync("RFB 003.008\n"u8.ToArray());
            await Read(s, 12);
            await s.WriteAsync(new byte[] { 2, 1, 2 });
            await Read(s, 1);
            var challenge = RandomNumberGenerator.GetBytes(16);
            await s.WriteAsync(challenge);
            var response = await Read(s, 16);
            if (!response.SequenceEqual(VncAuth.Response("secret", challenge))) throw new Exception("wrong response");
            await s.WriteAsync(BE32(0));
            var shared = await Read(s, 1);
            if (shared[0] != 1) throw new Exception("not shared");
            await s.WriteAsync(ServerInit(W, H, "Test desktop"));
            clientSetup = await Read(s, 20 + 4);
            int n = BinaryPrimitives.ReadUInt16BigEndian(clientSetup.AsSpan(22));
            clientSetup = [.. clientSetup, .. await Read(s, 4 * n + 10)];
        }, () => Task.FromResult<string?>("secret"));

        Check("VNC: desktop name and size", client.DesktopName == "Test desktop" && client.Width == W && client.Height == H && client.Version == "3.8",
            $"{client.DesktopName} {client.Width}x{client.Height} {client.Version}");
        Check("VNC: asks for 32-bit true color", clientSetup[0] == 0 && clientSetup[4] == 32 && clientSetup[7] == 1 && clientSetup[14] == 16 && clientSetup[16] == 0);
        Check("VNC: offers ZRLE first and asks for the full screen", clientSetup[20] == 2
            && BinaryPrimitives.ReadInt32BigEndian(clientSetup.AsSpan(24)) == 16 && clientSetup[^10] == 3 && clientSetup[^9] == 0);

        var updates = new List<VncRect>();
        var updated = new SemaphoreSlim(0);
        var resized = new TaskCompletionSource();
        var clip = new TaskCompletionSource<string>();
        var closed = new TaskCompletionSource<Exception?>();
        client.FrameUpdated += r => { lock (updates) updates.Add(r); updated.Release(); };
        client.Resized += () => resized.TrySetResult();
        client.ClipboardReceived += t => clip.TrySetResult(t);
        client.Closed += e => closed.TrySetResult(e);

        // Collect what the client sends (skipping its update requests).
        var received = new List<byte[]>();
        var gotInput = new SemaphoreSlim(0);
        var reader = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var type = await Read(s, 1);
                    byte[] body = type[0] switch
                    {
                        3 => await Read(s, 9),
                        4 => await Read(s, 7),
                        5 => await Read(s, 5),
                        6 => await Read(s, 7) is var h ? [.. h, .. await Read(s, BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(3)))] : [],
                        _ => throw new Exception("unexpected message " + type[0]),
                    };
                    if (type[0] == 3) continue;
                    lock (received) received.Add([type[0], .. body]);
                    gotInput.Release();
                }
            }
            catch { }
        });

        client.Start();

        // One update with every encoding.
        var msg = new List<byte> { 0, 0, 0, 0 };
        int rects = 0;
        void Add(byte[] b) => msg.AddRange(b);

        // ZRLE 100×70: four tiles (raw, packed palette, plain RLE, palette RLE) of different sizes.
        var zrleOut = new MemoryStream();
        var zrle = new ZLibStream(zrleOut, CompressionLevel.Fastest, leaveOpen: true);
        byte[] ZrleChunk(byte[] tiles)
        {
            zrleOut.SetLength(0);
            zrle.Write(tiles);
            zrle.Flush();
            return [.. BE32((int)zrleOut.Length), .. zrleOut.ToArray()];
        }
        uint Grad(int x, int y) => 0xFF000000 | (uint)(x * 2 << 16 | y * 3 << 8 | (x + y));
        uint[] Pal3 = [0xFFFF0000, 0xFF00FF00, 0xFF0000FF];
        Paint(0, 0, 64, 64, Grad);
        Paint(64, 0, 36, 64, (x, y) => Pal3[(x + y) % 3]);
        Paint(0, 64, 64, 6, (x, y) => (y * 64 + x) < 300 ? 0xFF123456 : 0xFFABCDEF);
        Paint(64, 64, 36, 6, (x, y) => x < 10 ? Pal3[0] : x < 30 ? Pal3[1] : Pal3[2]);
        var t = new List<byte>();
        t.Add(0); // raw
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) t.AddRange(CPx(model[y * W + x]));
        t.Add(3); // packed palette, 3 colors → 2 bits per pixel, rows padded to whole bytes
        foreach (var c in Pal3) t.AddRange(CPx(c));
        for (int y = 0; y < 64; y++)
        {
            int acc = 0, bits = 0;
            for (int x = 0; x < 36; x++)
            {
                acc = (acc << 2) | ((x + y) % 3);
                bits += 2;
                if (bits == 8) { t.Add((byte)acc); acc = 0; bits = 0; }
            }
            if (bits > 0) t.Add((byte)(acc << (8 - bits)));
        }
        t.Add(128); // plain RLE: 300 + 84 pixels
        t.AddRange(CPx(0xFF123456)); t.AddRange([255, 44]); // 1 + 255 + 44 = 300 pixels
        t.AddRange(CPx(0xFFABCDEF)); t.Add(83);              // 84 pixels
        t.Add(128 + 3); // palette RLE, rows of 36: run 10, run 20, single, run 5 ... per row
        foreach (var c in Pal3) t.AddRange(CPx(c));
        for (int y = 0; y < 6; y++)
        {
            t.AddRange([128 | 0, 9]);   // index 0 × 10
            t.AddRange([128 | 1, 19]);  // index 1 × 20
            t.Add(2);                   // index 2 × 1
            t.AddRange([128 | 2, 4]);   // index 2 × 5
        }
        Add(Rect(0, 0, 100, 70, 16)); Add(ZrleChunk([.. t])); rects++;

        // A second ZRLE rectangle continues the same compressed stream: one solid tile.
        Paint(0, 0, 20, 10, (_, _) => 0xFF808080);
        Add(Rect(0, 0, 20, 10, 16)); Add(ZrleChunk([1, .. CPx(0xFF808080)])); rects++;

        // Hextile 20×20 at (100,0): bg+fg+subrect, raw, background only, colored subrects.
        Paint(100, 0, 16, 16, (x, y) => x >= 2 && x < 6 && y >= 3 && y < 8 ? 0xFFFFFF00 : 0xFF000080);
        Paint(116, 0, 4, 16, (x, y) => Grad(x * 9, y));
        Paint(100, 16, 16, 4, (_, _) => 0xFF00AA00);
        Paint(116, 16, 4, 4, (x, y) => x == 1 && y == 1 ? 0xFFFF00FF : x == 3 ? 0xFF00FFFF : 0xFF00AA00);
        var hx = new List<byte>();
        hx.Add(2 | 4 | 8); hx.AddRange(Px(0xFF000080)); hx.AddRange(Px(0xFFFFFF00)); hx.AddRange([1, (2 << 4) | 3, (3 << 4) | 4]);
        hx.Add(1); for (int y = 0; y < 16; y++) for (int x = 0; x < 4; x++) hx.AddRange(Px(Grad(x * 9, y)));
        hx.Add(2); hx.AddRange(Px(0xFF00AA00));
        hx.Add(8 | 16); hx.Add(2);
        hx.AddRange(Px(0xFFFF00FF)); hx.AddRange([(1 << 4) | 1, 0]);
        hx.AddRange(Px(0xFF00FFFF)); hx.AddRange([(3 << 4) | 0, 3]);
        Add(Rect(100, 0, 20, 20, 5)); Add([.. hx]); rects++;

        // Raw 8×8 at (120,0).
        Paint(120, 0, 8, 8, (x, y) => 0xFF000000 | (uint)(x * 30 << 16 | y * 30));
        Add(Rect(120, 0, 8, 8, 0));
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) Add(Px(model[y * W + 120 + x]));
        rects++;

        // Zlib 10×10 at (130,10).
        Paint(130, 10, 10, 10, (x, y) => 0xFF000000 | (uint)((x * 25) << 8 | y * 25 << 16));
        var raw = new List<byte>();
        for (int y = 0; y < 10; y++) for (int x = 0; x < 10; x++) raw.AddRange(Px(model[(10 + y) * W + 130 + x]));
        var zo = new MemoryStream();
        using (var z = new ZLibStream(zo, CompressionLevel.Fastest, leaveOpen: true)) { z.Write([.. raw]); z.Flush(); }
        Add(Rect(130, 10, 10, 10, 6)); Add([.. BE32((int)zo.Length), .. zo.ToArray()]); rects++;

        // CopyRect: (120,0) 8×8 → (140,40), and an overlapping move of the top-left corner by (+2,+2).
        void Copy(int sx, int sy, int dx, int dy, int w, int h)
        {
            var snap = (uint[])model.Clone();
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) model[(dy + y) * W + dx + x] = snap[(sy + y) * W + sx + x];
        }
        Copy(120, 0, 140, 40, 8, 8);
        Add(Rect(140, 40, 8, 8, 1)); Add([.. BE16(120), .. BE16(0)]); rects++;
        Copy(0, 0, 2, 2, 30, 30);
        Add(Rect(2, 2, 30, 30, 1)); Add([.. BE16(0), .. BE16(0)]); rects++;
        Copy(10, 10, 5, 5, 20, 20); // and upwards (source below the destination)
        Add(Rect(5, 5, 20, 20, 1)); Add([.. BE16(10), .. BE16(10)]); rects++;

        msg[2] = (byte)(rects >> 8);
        msg[3] = (byte)rects;
        await s.WriteAsync(msg.ToArray());
        bool gotUpdate = await updated.WaitAsync(TimeSpan.FromSeconds(10));
        Check("VNC: update received", gotUpdate);
        uint[] pixels;
        lock (client.FrameLock) pixels = (uint[])client.Pixels.Clone();
        int wrong = Enumerable.Range(0, W * H).Count(i => pixels[i] != model[i]);
        int first = Enumerable.Range(0, W * H).FirstOrDefault(i => pixels[i] != model[i], -1);
        Check("VNC: ZRLE, Hextile, Raw, Zlib and CopyRect draw the expected picture", wrong == 0,
            first < 0 ? "" : $"{wrong} wrong pixels, first at ({first % W},{first / W}): {pixels[first]:X8} instead of {model[first]:X8}");
        lock (updates) Check("VNC: dirty area covers the update", updates.Count == 1 && updates[0] == new VncRect(0, 0, 148, 70), string.Join(" ", updates));

        // Resize, clipboard, bell.
        await s.WriteAsync((byte[])[0, 0, 0, 1, .. Rect(0, 0, 200, 100, -223)]);
        await s.WriteAsync((byte[])[3, 0, 0, 0, .. BE32(6), .. Encoding.Latin1.GetBytes("héllo\n")]);
        Check("VNC: desktop resize", await Task.WhenAny(resized.Task, Task.Delay(5000)) == resized.Task && client.Width == 200 && client.Height == 100 && client.Pixels.Length == 200 * 100);
        Check("VNC: clipboard from the server (Latin-1)", await Task.WhenAny(clip.Task, Task.Delay(5000)) == clip.Task && clip.Task.Result == "héllo\n");

        // Input from the viewer.
        client.SendKey(VncKeys.FromChar('a'), true);
        client.SendKey(VncKeys.FromChar('€'), false);
        client.SendPointer(150, 60, 5);
        client.SendPointer(5000, -3, 0); // clamped to the screen
        client.SendClipboard("x\r\ny");
        for (int i = 0; i < 5; i++) await gotInput.WaitAsync(TimeSpan.FromSeconds(5));
        lock (received)
        {
            Check("VNC: key events", received.Count >= 2 && received[0].SequenceEqual(new byte[] { 4, 1, 0, 0, 0, 0, 0, 0x61 })
                && received[1].SequenceEqual(new byte[] { 4, 0, 0, 0, 0x01, 0, 0x20, 0xAC }), received.Count > 1 ? Convert.ToHexString(received[1]) : "");
            Check("VNC: pointer events (clamped)", received.Count >= 4 && received[2].SequenceEqual(new byte[] { 5, 5, 0, 150, 0, 60 })
                && received[3].SequenceEqual(new byte[] { 5, 0, 0, 199, 0, 0 }), received.Count > 3 ? Convert.ToHexString(received[3]) : "");
            Check("VNC: clipboard to the server", received.Count >= 5 && received[4].SequenceEqual(new byte[] { 6, 0, 0, 0, 0, 0, 0, 3, (byte)'x', (byte)'\n', (byte)'y' }),
                received.Count > 4 ? Convert.ToHexString(received[4]) : "");
        }

        // A broken message ends the session with an error; closing ends it quietly.
        await s.WriteAsync(new byte[] { 99 });
        var reason = await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check("VNC: unknown message closes with a reason", reason is InvalidDataException, reason?.Message ?? "null");
        client.Dispose();
        socket.Dispose();
        await reader.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
