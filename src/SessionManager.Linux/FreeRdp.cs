using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SessionManager;

/// <summary>
/// Finds the FreeRDP client and builds its command line. FreeRDP 3 gets all arguments (including the password) on
/// stdin via /args-from:stdin, so the password never appears in the process list. FreeRDP 2 reads only the password
/// from stdin (/from-stdin).
/// </summary>
public static class FreeRdp
{
    public sealed record Client(string Path, int Major)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public bool IsX11 => Name.StartsWith("xfreerdp", StringComparison.Ordinal);
    }

    /// <summary>Preferred order: the X11 client is the most complete; the SDL client also works on Wayland.</summary>
    public static readonly string[] Candidates = ["xfreerdp3", "sdl-freerdp3", "xfreerdp", "sdl-freerdp", "wlfreerdp3", "wlfreerdp"];

    private static readonly Dictionary<string, Client?> Cache = new();

    public static Client? Find(string configured)
    {
        var key = configured.Trim();
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var c)) return c;
            c = null;
            var names = key.Length > 0 ? [key] : Candidates;
            foreach (var name in names)
            {
                var path = Resolve(name);
                if (path == null) continue;
                c = new Client(path, Version(path));
                break;
            }
            Cache[key] = c;
            return c;
        }
    }

    public static void ClearCache() { lock (Cache) Cache.Clear(); }

    private static string? Resolve(string name)
    {
        if (name.Contains('/')) return File.Exists(name) ? name : null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Major version from "--version" ("This is FreeRDP version 3.10.3 ..."); the name decides when that fails.</summary>
    private static int Version(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(path, ["--version"]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p != null)
            {
                var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(3000);
                var m = Regex.Match(text, @"version\s+(\d+)\.", RegexOptions.IgnoreCase);
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
        }
        catch { }
        return Path.GetFileName(path).EndsWith('3') ? 3 : 2;
    }

    public sealed record Options
    {
        public required string Server { get; init; }
        public required int Port { get; init; }
        public required SessionSettings Settings { get; init; }
        public string? Password { get; init; }
        public required string Title { get; init; }
        /// <summary>Starting size for "fit" sessions (the tab or window size), in pixels.</summary>
        public (int W, int H) FitSize { get; init; } = (1600, 900);
        /// <summary>X11 window to draw into (embedded mode), 0 = FreeRDP's own window.</summary>
        public long ParentWindow { get; init; }
        public bool TrustOnFirstUse { get; init; } = true;
        public string ExtraArgs { get; init; } = "";
    }

    /// <summary>All arguments, including the password (FreeRDP 3 reads them from stdin).</summary>
    public static List<string> Arguments(Client client, Options o)
    {
        var s = o.Settings;
        bool v3 = client.Major >= 3;
        var a = new List<string>();
        var host = o.Server.Contains(':') && !o.Server.StartsWith('[') ? $"[{o.Server}]" : o.Server; // IPv6
        a.Add($"/v:{host}:{o.Port}");
        if (!string.IsNullOrEmpty(s.Username)) a.Add($"/u:{s.Username}");
        if (!string.IsNullOrEmpty(s.Domain)) a.Add($"/d:{s.Domain}");
        if (v3 && o.Password != null) a.Add($"/p:{o.Password}");

        if (s.Width > 0 && s.Height > 0)
        {
            a.Add($"/size:{s.Width}x{s.Height}");
            if (s.SmartSizing) a.Add("/smart-sizing");
        }
        else
        {
            a.Add($"/size:{Math.Max(o.FitSize.W, 640)}x{Math.Max(o.FitSize.H, 480)}");
            a.Add("/dynamic-resolution");
        }
        if (s.ColorDepth is 8 or 15 or 16 or 24 or 32) a.Add($"/bpp:{s.ColorDepth}");
        a.Add(s.RedirectClipboard ? "+clipboard" : "-clipboard");
        if (s.RedirectDrives) a.Add("+home-drive");
        if (s.RedirectPrinters) a.Add("/printer");
        a.Add("+auto-reconnect");
        if (o.TrustOnFirstUse) a.Add(v3 ? "/cert:tofu" : "/cert-tofu");
        a.Add(v3 ? $"/title:{o.Title}" : $"/t:{o.Title}");
        if (client.IsX11)
        {
            a.Add("/wm-class:sessionmanager-rdp");
            if (o.ParentWindow != 0) a.Add($"/parent-window:{o.ParentWindow}");
        }
        a.AddRange(SplitArgs(o.ExtraArgs));
        if (!v3 && o.Password != null) a.Add("/from-stdin:force");
        return a;
    }

    /// <summary>Splits "a b 'c d'" like a shell would (quotes only, no variables).</summary>
    public static List<string> SplitArgs(string text)
    {
        var list = new List<string>();
        var cur = new System.Text.StringBuilder();
        char quote = '\0';
        bool any = false;
        foreach (var ch in text ?? "")
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0'; else cur.Append(ch);
                continue;
            }
            if (ch is '"' or '\'') { quote = ch; any = true; continue; }
            if (char.IsWhiteSpace(ch))
            {
                if (cur.Length > 0 || any) list.Add(cur.ToString());
                cur.Clear();
                any = false;
                continue;
            }
            cur.Append(ch);
        }
        if (cur.Length > 0 || any) list.Add(cur.ToString());
        return list;
    }

    /// <summary>Plain-language reason for FreeRDP's exit code (xfreerdp's XF_EXIT_* values).</summary>
    public static string? ExitReason(int code) => code switch
    {
        0 or 1 or 11 => null, // ended normally / disconnected by the user
        2 => "You were logged off.",
        3 => "The session was disconnected because it was idle.",
        4 => "The session was disconnected because the logon time limit was reached.",
        5 => "Another connection took over this session.",
        6 or 129 => "Out of memory.",
        7 => "The server denied the connection.",
        9 or 144 => "This user is not allowed to log in remotely.",
        10 => "The server requires fresh credentials.",
        128 => "FreeRDP did not accept the command line. Check the extra FreeRDP arguments in Settings.",
        131 or 141 or 147 => "Could not connect. Check the computer name, port and network.",
        132 or 134 or 154 => "Logon failed: wrong user name or password.",
        133 or 143 => "Could not agree on security settings with the server (TLS / NLA). If the server's certificate changed on purpose, remove its entry from ~/.config/freerdp/server/ and connect again.",
        135 => "The account is locked out.",
        139 or 140 => "The computer name could not be found (DNS).",
        145 => "The connection was cancelled.",
        148 or 149 or 152 => "The password has expired and must be changed.",
        150 => "The domain controller (KDC) could not be reached.",
        151 or 157 => "The account is disabled or expired.",
        155 => "Access denied.",
        159 => "No user name or password was given.",
        _ => $"FreeRDP stopped with exit code {code}.",
    };

    /// <summary>Exit codes that mean the password was wrong (so the next attempt asks again).</summary>
    public static bool IsAuthFailure(int code) => code is 132 or 134 or 154 or 159;
}
