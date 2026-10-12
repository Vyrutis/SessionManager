using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SessionManager;

/// <summary>
/// One window per data folder: a second launch (e.g. an application-menu shortcut with --open) passes its arguments
/// over a Unix socket in $XDG_RUNTIME_DIR to the running app and exits.
/// </summary>
public static class SingleInstance
{
    private static Socket? _listener;

    private static string SocketPath
    {
        get
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SessionStore.DataDir)))[..12].ToLowerInvariant();
            var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } r && Directory.Exists(r) ? r : SessionStore.DataDir;
            return Path.Combine(dir, $"sessionmanager-{hash}.sock");
        }
    }

    public static bool TrySendToRunning(string[] args)
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(SocketPath));
            s.Send(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args) + "\n"));
            s.Shutdown(SocketShutdown.Send);
            var buf = new byte[16];
            s.ReceiveTimeout = 3000;
            return s.Receive(buf) > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Starts listening; <paramref name="onArgs"/> is called on a background thread.</summary>
    public static void Listen(Action<string[]> onArgs)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var path = SocketPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) File.Delete(path); // stale: nobody answered above
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _listener.Listen(4);
            var listener = _listener;
            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using var client = listener.Accept();
                        client.ReceiveTimeout = 3000;
                        var data = new List<byte>();
                        var buf = new byte[4096];
                        int n;
                        while (data.Count < 65536 && (n = client.Receive(buf)) > 0) data.AddRange(buf.AsSpan(0, n).ToArray());
                        var args = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(data.ToArray()).Trim()) ?? [];
                        client.Send("ok"u8.ToArray());
                        onArgs(args);
                    }
                    catch (ObjectDisposedException) { return; }
                    catch (Exception ex) { CrashLog.Write(ex); }
                }
            }) { IsBackground = true, Name = "single-instance" }.Start();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    public static void Stop()
    {
        try
        {
            _listener?.Dispose();
            if (!OperatingSystem.IsWindows() && File.Exists(SocketPath)) File.Delete(SocketPath);
        }
        catch { }
    }
}
