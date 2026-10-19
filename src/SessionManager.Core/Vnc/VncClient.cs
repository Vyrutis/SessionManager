using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace SessionManager;

/// <summary>The server rejected the password (the viewer may ask again).</summary>
public sealed class VncAuthException(string message) : Exception(message);

public readonly record struct VncRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public VncRect Union(VncRect o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        int x = Math.Min(X, o.X), y = Math.Min(Y, o.Y);
        return new VncRect(x, y, Math.Max(X + Width, o.X + o.Width) - x, Math.Max(Y + Height, o.Y + o.Height) - y);
    }
}

/// <summary>
/// A VNC (RFB 3.3–3.8) viewer connection: classic VNC password login, screen updates in the Raw, CopyRect, Hextile,
/// Zlib and ZRLE encodings, desktop resizing, keyboard, mouse and clipboard. Works with UltraVNC, TightVNC, TigerVNC,
/// x11vnc, RealVNC (with its VNC password option) and most others.
/// The screen is kept in <see cref="Pixels"/> as 32-bit BGRA (0xAARRGGBB on little-endian machines).
/// </summary>
public sealed class VncClient : IDisposable
{
    public const int DefaultPort = 5900;

    private readonly Stream _stream;
    private readonly BufferedStream _in;
    private readonly TcpClient? _tcp;
    private readonly object _sendLock = new();
    private volatile bool _closed, _userClosed;
    private int _minor;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string DesktopName { get; private set; } = "";
    /// <summary>"3.8" etc.: the protocol version both sides agreed on.</summary>
    public string Version => $"3.{_minor}";
    /// <summary>The screen, Width × Height. Lock <see cref="FrameLock"/> while reading it; it is replaced when the desktop is resized.</summary>
    public uint[] Pixels { get; private set; } = [];
    public object FrameLock { get; } = new();

    /// <summary>Part of the screen changed (raised on the receiving thread, after a whole update).</summary>
    public event Action<VncRect>? FrameUpdated;
    /// <summary>The remote desktop changed size; <see cref="Pixels"/> is a new array.</summary>
    public event Action? Resized;
    public event Action<string>? ClipboardReceived;
    public event Action? Bell;
    /// <summary>The connection ended: null when closed by <see cref="Dispose"/>, otherwise the reason.</summary>
    public event Action<Exception?>? Closed;

    private VncClient(Stream stream, TcpClient? tcp)
    {
        _stream = stream;
        _tcp = tcp;
        _in = new BufferedStream(stream, 1 << 16);
    }

    /// <summary>Connects and logs in. <paramref name="askPassword"/> is only called when the server wants a password; null cancels.</summary>
    /// <exception cref="VncAuthException">Wrong password.</exception>
    /// <exception cref="OperationCanceledException">The password prompt was cancelled.</exception>
    public static async Task<VncClient> ConnectAsync(string host, int port, Func<Task<string?>> askPassword, CancellationToken cancel = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try { await tcp.ConnectAsync(host, port, timeout.Token); }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    throw new TimeoutException($"{host}:{port} did not answer within 15 seconds.");
                }
            }
            return await ConnectAsync(tcp.GetStream(), askPassword, cancel, tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Runs the handshake over an already open stream (used by the tests).</summary>
    public static async Task<VncClient> ConnectAsync(Stream stream, Func<Task<string?>> askPassword, CancellationToken cancel = default, TcpClient? tcp = null)
    {
        var c = new VncClient(stream, tcp);
        try
        {
            await c.HandshakeAsync(askPassword, cancel);
            return c;
        }
        catch
        {
            c._closed = true;
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Starts receiving screen updates on a background thread.</summary>
    public void Start()
    {
        var t = new Thread(ReceiveLoop) { IsBackground = true, Name = "VNC receive" };
        t.Start();
    }

    public void Dispose()
    {
        _userClosed = true;
        if (_closed) return;
        _closed = true;
        try { _stream.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
    }

    // ───────────── Handshake ─────────────

    private async Task HandshakeAsync(Func<Task<string?>> askPassword, CancellationToken cancel)
    {
        var hello = await ReadAsync(12, cancel);
        var text = Encoding.ASCII.GetString(hello);
        if (!text.StartsWith("RFB ", StringComparison.Ordinal) || !int.TryParse(text.AsSpan(4, 3), out int major) || !int.TryParse(text.AsSpan(8, 3), out int minor))
            throw new InvalidDataException($"This is not a VNC server (it answered \"{text.Trim()}\").");
        // Anything unusual (UltraVNC's 3.4/3.6, Apple's 3.889) falls back to the closest version both sides know.
        _minor = major > 3 || minor >= 8 ? 8 : minor == 7 ? 7 : 3;
        await WriteAsync(Encoding.ASCII.GetBytes($"RFB 003.00{_minor}\n"), cancel);

        int type;
        if (_minor == 3)
        {
            type = (int)BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(4, cancel));
            if (type == 0) throw new IOException("The server refused the connection: " + await ReadReasonAsync(cancel));
        }
        else
        {
            int n = (await ReadAsync(1, cancel))[0];
            if (n == 0) throw new IOException("The server refused the connection: " + await ReadReasonAsync(cancel));
            var types = await ReadAsync(n, cancel);
            if (types.Contains((byte)2)) type = 2;
            else if (types.Contains((byte)1)) type = 1;
            else throw new NotSupportedException(Unsupported(types));
            await WriteAsync([(byte)type], cancel);
        }
        if (type is not (1 or 2)) throw new NotSupportedException(Unsupported([(byte)Math.Min(type, 255)]));

        if (type == 2)
        {
            var challenge = await ReadAsync(16, cancel);
            var password = await askPassword() ?? throw new OperationCanceledException("Cancelled.");
            await WriteAsync(VncAuth.Response(password, challenge), cancel);
        }
        if (type == 2 || _minor == 8)
        {
            uint result = BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(4, cancel));
            if (result != 0)
            {
                string reason = "";
                if (_minor == 8) { try { reason = await ReadReasonAsync(cancel); } catch (IOException) { } }
                if (type == 2) throw new VncAuthException("The VNC password was not accepted." + (reason.Length > 0 ? $" ({reason})" : ""));
                throw new IOException("The server refused the connection" + (reason.Length > 0 ? ": " + reason : "."));
            }
        }

        await WriteAsync([1], cancel); // ClientInit: shared, so other viewers stay connected
        var init = await ReadAsync(24, cancel);
        int width = BinaryPrimitives.ReadUInt16BigEndian(init), height = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(2));
        uint nameLength = BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(20));
        if (nameLength > 1 << 16) throw new InvalidDataException("The server sent an invalid desktop name.");
        DesktopName = Encoding.UTF8.GetString(await ReadAsync((int)nameLength, cancel));
        SetSize(width, height);

        // Ask for 32-bit true color, little-endian 0x00RRGGBB, so every server sends the same format.
        var fmt = new byte[20];
        fmt[0] = 0; // SetPixelFormat
        fmt[4] = 32; fmt[5] = 24; fmt[6] = 0; fmt[7] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(fmt.AsSpan(8), 255);
        BinaryPrimitives.WriteUInt16BigEndian(fmt.AsSpan(10), 255);
        BinaryPrimitives.WriteUInt16BigEndian(fmt.AsSpan(12), 255);
        fmt[14] = 16; fmt[15] = 8; fmt[16] = 0;
        int[] encodings = [Encodings.Zrle, Encodings.Hextile, Encodings.Zlib, Encodings.CopyRect, Encodings.Raw, Encodings.DesktopSize, Encodings.LastRect];
        var enc = new byte[4 + 4 * encodings.Length];
        enc[0] = 2; // SetEncodings
        BinaryPrimitives.WriteUInt16BigEndian(enc.AsSpan(2), (ushort)encodings.Length);
        for (int i = 0; i < encodings.Length; i++) BinaryPrimitives.WriteInt32BigEndian(enc.AsSpan(4 + 4 * i), encodings[i]);
        await WriteAsync([.. fmt, .. enc, .. UpdateRequest(false)], cancel);
    }

    private static string Unsupported(byte[] types)
    {
        static string Name(byte t) => t switch
        {
            5 or 6 => "RealVNC encryption",
            16 => "TightVNC login",
            18 or 19 => "TLS / VeNCrypt",
            30 => "macOS Screen Sharing login",
            113 or 114 or 115 => "UltraVNC MS-Logon or encryption plugin",
            _ => $"type {t}",
        };
        return $"The server asks for a login method this app doesn't support yet ({string.Join(", ", types.Select(Name))}). " +
               "Set the VNC server to use a plain VNC password.";
    }

    private async Task<string> ReadReasonAsync(CancellationToken cancel)
    {
        uint n = BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(4, cancel));
        return n > 1 << 16 ? "" : Encoding.UTF8.GetString(await ReadAsync((int)n, cancel)).Trim();
    }

    private async Task<byte[]> ReadAsync(int n, CancellationToken cancel)
    {
        var b = new byte[n];
        await _in.ReadExactlyAsync(b, cancel);
        return b;
    }

    private async Task WriteAsync(byte[] data, CancellationToken cancel)
    {
        await _stream.WriteAsync(data, cancel);
        await _stream.FlushAsync(cancel);
    }

    // ───────────── Sending input ─────────────

    /// <summary>Buttons: 1 left, 2 middle, 4 right, 8 wheel up, 16 wheel down.</summary>
    public void SendPointer(int x, int y, int buttons)
    {
        var m = new byte[6];
        m[0] = 5;
        m[1] = (byte)buttons;
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), (ushort)Math.Clamp(x, 0, Math.Max(0, Width - 1)));
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(4), (ushort)Math.Clamp(y, 0, Math.Max(0, Height - 1)));
        Send(m);
    }

    /// <summary>An X11 keysym, see <see cref="VncKeys"/>.</summary>
    public void SendKey(uint keysym, bool down)
    {
        var m = new byte[8];
        m[0] = 4;
        m[1] = (byte)(down ? 1 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4), keysym);
        Send(m);
    }

    /// <summary>Sends text to the remote clipboard (Latin-1 only, as the protocol defines; other characters become '?').</summary>
    public void SendClipboard(string text)
    {
        var data = Encoding.Latin1.GetBytes(text.Replace("\r\n", "\n"));
        var m = new byte[8 + data.Length];
        m[0] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(4), (uint)data.Length);
        data.CopyTo(m, 8);
        Send(m);
    }

    /// <summary>Asks for the whole screen again (e.g. after the view was hidden).</summary>
    public void RefreshScreen() => Send(UpdateRequest(false));

    private byte[] UpdateRequest(bool incremental)
    {
        var m = new byte[10];
        m[0] = 3;
        m[1] = (byte)(incremental ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(6), (ushort)Width);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(8), (ushort)Height);
        return m;
    }

    private void Send(byte[] data)
    {
        if (_closed) return;
        try
        {
            lock (_sendLock)
            {
                _stream.Write(data);
                _stream.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { /* the receive loop reports the disconnect */ }
    }

    // ───────────── Receiving ─────────────

    private static class Encodings
    {
        public const int Raw = 0, CopyRect = 1, Hextile = 5, Zlib = 6, Zrle = 16, DesktopSize = -223, LastRect = -224;
    }

    private const int MaxPixels = 1 << 26; // 8192 × 8192
    private uint[] _rect = [];
    private byte[] _bytes = [];
    private readonly ZlibChannel _zlib = new(), _zrle = new();

    private void ReceiveLoop()
    {
        Exception? error = null;
        try
        {
            while (!_closed)
            {
                int type = ReadU8();
                switch (type)
                {
                    case 0:
                        ReadUpdate();
                        Send(UpdateRequest(true));
                        break;
                    case 1: // SetColourMapEntries: not used with true color
                        Skip(3); // padding, first color
                        Skip(ReadU16() * 6);
                        break;
                    case 2:
                        Bell?.Invoke();
                        break;
                    case 3:
                        Skip(3);
                        int length = ReadS32();
                        if (length is < 0 or > 1 << 24) throw new InvalidDataException("The server sent invalid clipboard data.");
                        var text = Encoding.Latin1.GetString(ReadBytes(length));
                        ClipboardReceived?.Invoke(text);
                        break;
                    default:
                        throw new InvalidDataException($"The server sent a message this app doesn't understand (type {type}).");
                }
            }
        }
        catch (Exception ex)
        {
            if (!_closed) error = ex;
        }
        _closed = true;
        try { _stream.Dispose(); } catch { }
        Closed?.Invoke(_userClosed ? null : error ?? new IOException("The server closed the connection."));
    }

    private void ReadUpdate()
    {
        Skip(1);
        int count = ReadU16();
        var dirty = default(VncRect);
        for (int i = 0; i < count; i++)
        {
            int x = ReadU16(), y = ReadU16(), w = ReadU16(), h = ReadU16();
            int encoding = ReadS32();
            if (encoding == Encodings.LastRect) break;
            if (encoding == Encodings.DesktopSize)
            {
                SetSize(w, h);
                Resized?.Invoke();
                dirty = new VncRect(0, 0, Width, Height);
                continue;
            }
            if ((long)w * h > MaxPixels) throw new InvalidDataException("The server sent an update larger than the screen.");
            switch (encoding)
            {
                case Encodings.Raw: DecodeRaw(w, h); break;
                case Encodings.CopyRect: CopyRect(ReadU16(), ReadU16(), x, y, w, h); break;
                case Encodings.Hextile: DecodeHextile(w, h); break;
                case Encodings.Zlib: DecodeZlib(w, h); break;
                case Encodings.Zrle: DecodeZrle(w, h); break;
                default: throw new InvalidDataException($"The server used a screen encoding this app didn't ask for ({encoding}).");
            }
            if (encoding != Encodings.CopyRect) Blit(x, y, w, h);
            dirty = dirty.Union(new VncRect(x, y, w, h));
        }
        dirty = Clip(dirty);
        if (!dirty.IsEmpty) FrameUpdated?.Invoke(dirty);
    }

    private VncRect Clip(VncRect r)
    {
        int x = Math.Clamp(r.X, 0, Width), y = Math.Clamp(r.Y, 0, Height);
        return new VncRect(x, y, Math.Min(r.X + r.Width, Width) - x, Math.Min(r.Y + r.Height, Height) - y);
    }

    private void SetSize(int w, int h)
    {
        if ((long)w * h > MaxPixels) throw new InvalidDataException("The remote screen is too large.");
        lock (FrameLock)
        {
            Width = w;
            Height = h;
            var p = new uint[w * h];
            Array.Fill(p, 0xFF000000);
            Pixels = p;
        }
    }

    private uint[] RectBuffer(int n)
    {
        if (_rect.Length < n) _rect = new uint[Math.Max(n, 64 * 64)];
        return _rect;
    }

    private byte[] ByteBuffer(int n)
    {
        if (_bytes.Length < n) _bytes = new byte[Math.Max(n, 1 << 16)];
        return _bytes;
    }

    /// <summary>Copies the decoded rectangle in <see cref="_rect"/> to the screen, cut to the screen's edges.</summary>
    private void Blit(int x, int y, int w, int h)
    {
        var r = Clip(new VncRect(x, y, w, h));
        if (r.IsEmpty) return;
        lock (FrameLock)
        {
            for (int row = 0; row < r.Height; row++)
                Array.Copy(_rect, (r.Y - y + row) * w + (r.X - x), Pixels, (r.Y + row) * Width + r.X, r.Width);
        }
    }

    private void CopyRect(int sx, int sy, int x, int y, int w, int h)
    {
        int dx = sx - x, dy = sy - y;
        // Keep the part where both the source and the destination are on the screen.
        int x0 = Math.Max(Math.Max(x, 0), -dx), y0 = Math.Max(Math.Max(y, 0), -dy);
        int x1 = Math.Min(Math.Min(x + w, Width), Width - dx), y1 = Math.Min(Math.Min(y + h, Height), Height - dy);
        if (x1 <= x0 || y1 <= y0) return;
        lock (FrameLock)
        {
            // Copy rows in the order that doesn't overwrite rows still to be read (Array.Copy handles overlap within a row).
            for (int i = 0; i < y1 - y0; i++)
            {
                int row = dy < 0 ? y1 - 1 - i : y0 + i;
                Array.Copy(Pixels, (row + dy) * Width + x0 + dx, Pixels, row * Width + x0, x1 - x0);
            }
        }
    }

    private void DecodeRaw(int w, int h)
    {
        int n = w * h;
        var bytes = ByteBuffer(n * 4);
        _in.ReadExactly(bytes, 0, n * 4);
        var dst = RectBuffer(n);
        var src = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan(0, n * 4));
        for (int i = 0; i < n; i++) dst[i] = src[i] | 0xFF000000;
    }

    private void DecodeHextile(int w, int h)
    {
        var dst = RectBuffer(w * h);
        uint bg = 0xFF000000, fg = 0xFFFFFFFF;
        for (int ty = 0; ty < h; ty += 16)
        for (int tx = 0; tx < w; tx += 16)
        {
            int tw = Math.Min(16, w - tx), th = Math.Min(16, h - ty);
            int sub = ReadU8();
            if ((sub & 1) != 0)
            {
                for (int yy = 0; yy < th; yy++)
                for (int xx = 0; xx < tw; xx++)
                    dst[(ty + yy) * w + tx + xx] = ReadPixel();
                continue;
            }
            if ((sub & 2) != 0) bg = ReadPixel();
            Fill(dst, w, tx, ty, tw, th, bg);
            if ((sub & 4) != 0) fg = ReadPixel();
            if ((sub & 8) == 0) continue;
            int n = ReadU8();
            for (int i = 0; i < n; i++)
            {
                uint color = (sub & 16) != 0 ? ReadPixel() : fg;
                int xy = ReadU8(), wh = ReadU8();
                int sx = xy >> 4, sy = xy & 15, sw = (wh >> 4) + 1, sh = (wh & 15) + 1;
                if (sx + sw > tw || sy + sh > th) throw new InvalidDataException("Invalid Hextile data.");
                Fill(dst, w, tx + sx, ty + sy, sw, sh, color);
            }
        }
    }

    private static void Fill(uint[] dst, int stride, int x, int y, int w, int h, uint color)
    {
        for (int row = 0; row < h; row++) dst.AsSpan((y + row) * stride + x, w).Fill(color);
    }

    private void DecodeZlib(int w, int h)
    {
        int n = w * h;
        int length = ReadS32();
        if (length < 0 || length > 1 << 28) throw new InvalidDataException("Invalid Zlib data.");
        var data = _zlib.Inflate(ReadBytes(length));
        if (data.Length < n * 4) throw new InvalidDataException("Zlib data is too short.");
        var dst = RectBuffer(n);
        var src = MemoryMarshal.Cast<byte, uint>(data.AsSpan(0, n * 4));
        for (int i = 0; i < n; i++) dst[i] = src[i] | 0xFF000000;
    }

    private void DecodeZrle(int w, int h)
    {
        int length = ReadS32();
        if (length < 0 || length > 1 << 28) throw new InvalidDataException("Invalid ZRLE data.");
        var d = _zrle.Inflate(ReadBytes(length));
        try { Zrle.Decode(d, RectBuffer(w * h), w, h); }
        catch (IndexOutOfRangeException) { throw new InvalidDataException("Invalid ZRLE data."); }
    }

    private int ReadU8()
    {
        int b = _in.ReadByte();
        return b < 0 ? throw new EndOfStreamException("The server closed the connection.") : b;
    }

    private int ReadU16()
    {
        Span<byte> b = stackalloc byte[2];
        _in.ReadExactly(b);
        return BinaryPrimitives.ReadUInt16BigEndian(b);
    }

    private int ReadS32()
    {
        Span<byte> b = stackalloc byte[4];
        _in.ReadExactly(b);
        return BinaryPrimitives.ReadInt32BigEndian(b);
    }

    private uint ReadPixel()
    {
        Span<byte> b = stackalloc byte[4];
        _in.ReadExactly(b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b) | 0xFF000000;
    }

    private byte[] ReadBytes(int n)
    {
        var b = new byte[n];
        _in.ReadExactly(b);
        return b;
    }

    private void Skip(int n)
    {
        var b = ByteBuffer(Math.Max(n, 1));
        _in.ReadExactly(b, 0, n);
    }
}

/// <summary>
/// One zlib stream that continues across messages: each message adds a compressed chunk (ending with a flush),
/// and everything it decompresses to is returned.
/// </summary>
internal sealed class ZlibChannel
{
    private readonly Feed _feed = new();
    private readonly ZLibStream _z;
    private byte[] _out = new byte[1 << 16];

    public ZlibChannel() => _z = new ZLibStream(_feed, CompressionMode.Decompress);

    public byte[] Inflate(byte[] chunk)
    {
        _feed.Set(chunk);
        int total = 0;
        while (true)
        {
            if (total == _out.Length) Array.Resize(ref _out, _out.Length * 2);
            int n = _z.Read(_out, total, _out.Length - total);
            if (n == 0) break;
            total += n;
            if (total > 1 << 28) throw new InvalidDataException("Compressed screen data is too large.");
        }
        return _out.AsSpan(0, total).ToArray();
    }

    /// <summary>Hands the current chunk to the decompressor; reads return 0 when it is used up.</summary>
    private sealed class Feed : Stream
    {
        private byte[] _data = [];
        private int _pos;

        public void Set(byte[] data) { _data = data; _pos = 0; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _data.Length - _pos);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>ZRLE: 64×64 tiles, each raw, solid, a packed palette, or run-length encoded; pixels are 3 bytes (B, G, R).</summary>
public static class Zrle
{
    public static void Decode(byte[] d, uint[] dst, int w, int h)
    {
        int p = 0;
        Span<uint> palette = stackalloc uint[128];
        for (int ty = 0; ty < h; ty += 64)
        for (int tx = 0; tx < w; tx += 64)
        {
            int tw = Math.Min(64, w - tx), th = Math.Min(64, h - ty), count = tw * th;
            int sub = d[p++];
            if (sub == 0) // raw
            {
                for (int yy = 0; yy < th; yy++)
                for (int xx = 0; xx < tw; xx++, p += 3)
                    dst[(ty + yy) * w + tx + xx] = Pixel(d, p);
            }
            else if (sub == 1) // solid
            {
                uint c = Pixel(d, p);
                p += 3;
                for (int yy = 0; yy < th; yy++) dst.AsSpan((ty + yy) * w + tx, tw).Fill(c);
            }
            else if (sub <= 16) // packed palette
            {
                for (int i = 0; i < sub; i++, p += 3) palette[i] = Pixel(d, p);
                int bits = sub == 2 ? 1 : sub <= 4 ? 2 : 4, mask = (1 << bits) - 1;
                for (int yy = 0; yy < th; yy++)
                {
                    int shift = 8;
                    for (int xx = 0; xx < tw; xx++)
                    {
                        shift -= bits;
                        int index = (d[p] >> shift) & mask;
                        if (shift == 0) { shift = 8; p++; }
                        dst[(ty + yy) * w + tx + xx] = index < sub ? palette[index] : throw new InvalidDataException("Invalid ZRLE palette index.");
                    }
                    if (shift != 8) p++; // rows end on a byte boundary
                }
            }
            else if (sub == 128 || sub >= 130) // run-length, plain or with a palette
            {
                int paletteSize = sub == 128 ? 0 : sub - 128;
                for (int i = 0; i < paletteSize; i++, p += 3) palette[i] = Pixel(d, p);
                int done = 0;
                while (done < count)
                {
                    uint c;
                    int run = 1;
                    if (paletteSize == 0)
                    {
                        c = Pixel(d, p);
                        p += 3;
                        int b;
                        do { b = d[p++]; run += b; } while (b == 255);
                    }
                    else
                    {
                        int index = d[p++];
                        if ((index & 128) != 0)
                        {
                            index &= 127;
                            int b;
                            do { b = d[p++]; run += b; } while (b == 255);
                        }
                        c = index < paletteSize ? palette[index] : throw new InvalidDataException("Invalid ZRLE palette index.");
                    }
                    if (run > count - done) throw new InvalidDataException("Invalid ZRLE run length.");
                    for (int i = 0; i < run; i++, done++)
                        dst[(ty + done / tw) * w + tx + done % tw] = c;
                }
            }
            else throw new InvalidDataException($"Invalid ZRLE tile type {sub}.");
        }
    }

    private static uint Pixel(byte[] d, int p) => 0xFF000000 | (uint)(d[p] | d[p + 1] << 8 | d[p + 2] << 16);
}
