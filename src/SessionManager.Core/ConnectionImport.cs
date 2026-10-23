using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SessionManager;

/// <summary>A connection found by an importer or the screenshot scan, before the user reviews it.</summary>
public sealed class ImportedConnection
{
    public string Name { get; set; } = "";
    public Protocol Protocol { get; set; } = Protocol.Ssh;
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";
    /// <summary>Folder path inside the protocol group, parts separated by '/'.</summary>
    public string Folder { get; set; } = "";
    /// <summary>Only from files the user exported in plain text (CSV); saved to Windows Credential Manager.</summary>
    public string? Password { get; set; }
    public string PrivateKeyPath { get; set; } = "";
    /// <summary>Other settings read from the source (e.g. display options of an .rdp file).</summary>
    public SessionSettings? Template { get; set; }
    /// <summary>Where it was found (shown as a tooltip in the review).</summary>
    public string SourcePath { get; set; } = "";
}

/// <summary>
/// Reads connection lists from other tools. Only hosts, ports, users, domains and folders are taken;
/// passwords other programs keep encrypted are left alone (the user enters them, or imports a CSV they exported).
/// </summary>
public static class ConnectionImport
{
    public const string PuttyKey = @"Software\SimonTatham\PuTTY\Sessions";

    public static string SshConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    /// <summary>PuTTY on Linux keeps one file per saved session here.</summary>
    public static string PuttyDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".putty", "sessions");

    public static bool HasPutty()
    {
        if (!OperatingSystem.IsWindows())
            return Directory.Exists(PuttyDir) && Directory.EnumerateFiles(PuttyDir).Any();
        using var key = Registry.CurrentUser.OpenSubKey(PuttyKey);
        return key != null && key.SubKeyCount > 0;
    }

    // ───────────── PuTTY (registry on Windows, ~/.putty/sessions on Linux) ─────────────

    public static List<ImportedConnection> FromPutty()
    {
        if (!OperatingSystem.IsWindows()) return FromPuttyFiles(PuttyDir);
        var list = new List<ImportedConnection>();
        using var root = Registry.CurrentUser.OpenSubKey(PuttyKey);
        if (root == null) return list;
        foreach (var keyName in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(keyName);
            if (k == null) continue;
            var proto = (k.GetValue("Protocol") as string ?? "ssh").ToLowerInvariant();
            if (proto != "ssh") continue; // telnet, serial, raw: nothing this app can open
            var host = (k.GetValue("HostName") as string ?? "").Trim();
            if (host.Length == 0) continue; // "Default Settings" and empty templates
            var user = (k.GetValue("UserName") as string ?? "").Trim();
            int at = host.LastIndexOf('@');
            if (at > 0) { if (user.Length == 0) user = host[..at]; host = host[(at + 1)..]; }
            int port = k.GetValue("PortNumber") is int p and > 0 and < 65536 ? p : 22;
            var name = Uri.UnescapeDataString(keyName.Replace("%20", " "));
            list.Add(new ImportedConnection
            {
                Name = name, Protocol = Protocol.Ssh, Host = host, Port = port, Username = user,
                PrivateKeyPath = (k.GetValue("PublicKeyFile") as string ?? "").Trim(),
            });
        }
        return list;
    }

    // ───────────── Windows Remote Desktop client (mstsc) history ─────────────

    private const string MstscKey = @"Software\Microsoft\Terminal Server Client";

    public static bool HasMstscHistory()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var servers = Registry.CurrentUser.OpenSubKey(MstscKey + @"\Servers");
        using var mru = Registry.CurrentUser.OpenSubKey(MstscKey + @"\Default");
        return (servers?.SubKeyCount ?? 0) > 0 || (mru?.ValueCount ?? 0) > 0;
    }

    /// <summary>Computers the built-in Remote Desktop client has connected to, with the user name it remembers.</summary>
    public static List<ImportedConnection> FromMstscHistory()
    {
        var list = new List<ImportedConnection>();
        if (!OperatingSystem.IsWindows()) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string address, string userHint)
        {
            if (ParseAddress(address) is not { } a || !seen.Add(address.Trim())) return;
            var (domain, user) = DomainName.Split(userHint.Trim());
            list.Add(new ImportedConnection
            {
                Name = a.Host, Protocol = Protocol.Rdp, Host = a.Host, Port = a.Port ?? 3389, Username = user, Domain = domain,
            });
        }

        // Most recent first (the "Computer" drop-down), then everything else it knows.
        using (var mru = Registry.CurrentUser.OpenSubKey(MstscKey + @"\Default"))
            if (mru != null)
                foreach (var name in mru.GetValueNames().Where(n => n.StartsWith("MRU", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Length).ThenBy(n => n))
                    if (mru.GetValue(name) is string address)
                    {
                        using var hint = Registry.CurrentUser.OpenSubKey(MstscKey + @"\Servers\" + address);
                        Add(address, hint?.GetValue("UsernameHint") as string ?? "");
                    }
        using (var servers = Registry.CurrentUser.OpenSubKey(MstscKey + @"\Servers"))
            if (servers != null)
                foreach (var address in servers.GetSubKeyNames())
                {
                    using var k = servers.OpenSubKey(address);
                    Add(address, k?.GetValue("UsernameHint") as string ?? "");
                }
        return list;
    }

    // ───────────── .rdp files on this PC ─────────────

    public const int MaxRdpFiles = 500;

    /// <summary>
    /// Looks for .rdp files where people keep them: Desktop, Documents, Downloads and OneDrive,
    /// up to 5 folders deep. AppData, system files and online-only OneDrive files are skipped.
    /// </summary>
    public static List<ImportedConnection> FindRdpFiles(SessionSettings preset, CancellationToken cancel = default)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(profile, "Downloads"),
            Environment.GetEnvironmentVariable("OneDrive") ?? "",
            Environment.GetEnvironmentVariable("OneDriveCommercial") ?? "",
        }.Where(d => d.Length > 0 && Directory.Exists(d)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.rdp", options))
                {
                    cancel.ThrowIfCancellationRequested();
                    var rel = f[root.Length..].Replace('\\', '/');
                    if (rel.Contains("/AppData/", StringComparison.OrdinalIgnoreCase) || rel.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!OperatingSystem.IsWindows() && rel.Contains("/.")) continue; // hidden folders (caches, app data)
                    // Online-only cloud files would be downloaded just by reading them.
                    var attrs = (int)File.GetAttributes(f);
                    if ((attrs & 0x400000) != 0 || (attrs & 0x40000) != 0) continue; // RECALL_ON_DATA_ACCESS / RECALL_ON_OPEN
                    files.Add(f);
                    if (files.Count >= MaxRdpFiles) break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (files.Count >= MaxRdpFiles) break;
        }

        var list = new List<ImportedConnection>();
        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(file).Length > 64 * 1024) continue; // too big to be an .rdp file
                var (name, s) = SessionFile.ImportRdp(file, preset);
                list.Add(new ImportedConnection
                {
                    Name = name, Protocol = Protocol.Rdp, Host = s.Host, Port = s.Port, Username = s.Username, Domain = s.Domain,
                    Template = s, SourcePath = file,
                });
            }
            catch { /* unreadable or not a connection: skip */ }
        }
        // The same computer saved in several files: keep one.
        return list.GroupBy(c => (c.Host.ToLowerInvariant(), c.Port, c.Username.ToLowerInvariant())).Select(g => g.First()).ToList();
    }

    // ───────────── OpenSSH config ─────────────

    public static List<ImportedConnection> FromSshConfig(string path)
    {
        var list = new List<ImportedConnection>();
        ImportedConnection? current = null;
        var aliases = new List<string>();

        void Flush()
        {
            if (current == null) return;
            foreach (var alias in aliases)
            {
                var c = new ImportedConnection
                {
                    Name = alias, Protocol = Protocol.Ssh, Host = current.Host.Length > 0 ? current.Host : alias,
                    Port = current.Port > 0 ? current.Port : 22, Username = current.Username, PrivateKeyPath = current.PrivateKeyPath,
                };
                list.Add(c);
            }
            current = null;
            aliases.Clear();
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var m = Regex.Match(line, @"^(\S+?)\s*(?:=|\s)\s*(.+)$");
            if (!m.Success) continue;
            var key = m.Groups[1].Value.ToLowerInvariant();
            var value = m.Groups[2].Value.Trim().Trim('"');
            switch (key)
            {
                case "host":
                    Flush();
                    current = new ImportedConnection();
                    // Patterns (*, ?, !) are defaults for other hosts, not connections.
                    aliases.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(a => a.IndexOfAny(['*', '?', '!']) < 0));
                    break;
                case "match":
                    Flush();
                    break;
                case "hostname" when current != null: current.Host = value; break;
                case "user" when current != null: current.Username = value; break;
                case "port" when current != null: current.Port = int.TryParse(value, out var p) && p is > 0 and < 65536 ? p : 22; break;
                case "identityfile" when current != null && current.PrivateKeyPath.Length == 0:
                    current.PrivateKeyPath = value.StartsWith('~')
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value.TrimStart('~', '/', '\\'))
                        : value;
                    break;
            }
        }
        Flush();
        return list;
    }

    // ───────────── mRemoteNG (confCons.xml) ─────────────

    public static List<ImportedConnection> FromMRemoteNG(string path)
    {
        var list = new List<ImportedConnection>();
        // A file from elsewhere: no DTDs, so no entity tricks.
        using var xml = System.Xml.XmlReader.Create(path, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        var doc = XDocument.Load(xml);
        if (doc.Root?.Attribute("FullFileEncryption")?.Value.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidDataException("This mRemoteNG file is fully encrypted. In mRemoteNG, turn off \"Encrypt complete connection file\" and save it again, or export it as CSV.");
        foreach (var node in doc.Descendants("Node"))
        {
            if (!string.Equals((string?)node.Attribute("Type"), "Connection", StringComparison.OrdinalIgnoreCase)) continue;
            var proto = ((string?)node.Attribute("Protocol") ?? "").ToUpperInvariant();
            Protocol protocol;
            if (proto == "RDP") protocol = Protocol.Rdp;
            else if (proto is "SSH1" or "SSH2") protocol = Protocol.Ssh;
            else if (proto == "VNC") protocol = Protocol.Vnc;
            else if (proto is "HTTP" or "HTTPS") protocol = Protocol.Web;
            else continue;
            var host = ((string?)node.Attribute("Hostname") ?? "").Trim();
            if (host.Length == 0) continue;
            SessionSettings? web = null;
            if (protocol == Protocol.Web)
            {
                // mRemoteNG keeps the scheme in the protocol; the host may still be a full address.
                web = SessionSettings.DefaultsFor(Protocol.Web);
                web.WebHttps = proto == "HTTPS";
                if (SessionSettings.TryParseWebUrl(host.Contains("://") ? host : $"{proto.ToLowerInvariant()}://{host}", out var https, out var h, out _, out var webPath))
                    (web.WebHttps, host, web.WebPath) = (https, h, webPath);
            }
            var folder = string.Join("/", node.Ancestors("Node")
                .Where(a => string.Equals((string?)a.Attribute("Type"), "Container", StringComparison.OrdinalIgnoreCase))
                .Select(a => (string?)a.Attribute("Name") ?? "").Where(n => n.Length > 0).Reverse());
            list.Add(new ImportedConnection
            {
                Name = (string?)node.Attribute("Name") ?? host, Protocol = protocol, Host = host,
                Port = int.TryParse((string?)node.Attribute("Port"), out var p) && p is > 0 and < 65536 ? p
                    : web != null ? (web.WebHttps ? 443 : 80) : DefaultPort(protocol),
                Username = protocol == Protocol.Vnc ? "" : ((string?)node.Attribute("Username") ?? "").Trim(),
                Domain = protocol == Protocol.Rdp ? ((string?)node.Attribute("Domain") ?? "").Trim() : "",
                Folder = folder,
                Template = web,
            });
        }
        return list;
    }

    // ───────────── MobaXterm (.mxtsessions / MobaXterm.ini) ─────────────

    public static List<ImportedConnection> FromMobaXterm(string path)
    {
        var list = new List<ImportedConnection>();
        string folder = "";
        bool inBookmarks = false;
        foreach (var raw in File.ReadAllLines(path, Encoding.Latin1))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inBookmarks = line.StartsWith("[Bookmarks", StringComparison.OrdinalIgnoreCase);
                folder = "";
                continue;
            }
            if (!inBookmarks) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Equals("SubRep", StringComparison.OrdinalIgnoreCase)) { folder = value.Replace('\\', '/'); continue; }
            if (key.Equals("ImgNum", StringComparison.OrdinalIgnoreCase)) continue;

            // name=#icon#type%host%port%user%...
            var hashes = value.Split('#');
            if (hashes.Length < 3) continue;
            var f = hashes[2].Split('%');
            if (f.Length < 4) continue;
            Protocol protocol;
            if (f[0] == "0") protocol = Protocol.Ssh;
            else if (f[0] == "4") protocol = Protocol.Rdp;
            else if (f[0] == "5") protocol = Protocol.Vnc;
            else continue;
            var host = f[1].Trim();
            if (host.Length == 0) continue;
            var user = f[3].Trim();
            string domain = "";
            if (protocol == Protocol.Rdp) (domain, user) = DomainName.Split(user);
            list.Add(new ImportedConnection
            {
                Name = key, Protocol = protocol, Host = host,
                Port = int.TryParse(f[2], out var p) && p is > 0 and < 65536 ? p : DefaultPort(protocol),
                Username = protocol == Protocol.Vnc ? "" : user, Domain = domain, Folder = folder,
            });
        }
        return list;
    }

    // ───────────── CSV (KeePass, Bitwarden, spreadsheets, mRemoteNG CSV export) ─────────────

    private static readonly string[] NameCols = ["name", "title", "account", "session", "connection", "label"];
    private static readonly string[] HostCols = ["host", "hostname", "host name", "address", "ip", "ip address", "server", "computer", "url", "web site", "website", "login_uri", "uri"];
    private static readonly string[] PortCols = ["port"];
    private static readonly string[] UserCols = ["username", "user name", "user", "login", "login name", "login_username", "account name"];
    private static readonly string[] PassCols = ["password", "pass", "login_password"];
    private static readonly string[] DomainCols = ["domain"];
    private static readonly string[] ProtoCols = ["protocol", "type", "kind"];
    private static readonly string[] FolderCols = ["group", "folder", "path", "container", "groups"];

    public static List<ImportedConnection> FromCsv(string path)
    {
        var text = File.ReadAllText(path);
        var firstLine = text.Split('\n')[0];
        char sep = new[] { ',', ';', '\t' }.OrderByDescending(c => firstLine.Count(ch => ch == c)).First();
        var rows = ParseCsv(text, sep);
        if (rows.Count < 2) return [];

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string[] names) { foreach (var n in names) { int i = header.IndexOf(n); if (i >= 0) return i; } return -1; }
        int cName = Col(NameCols), cHost = Col(HostCols), cPort = Col(PortCols), cUser = Col(UserCols),
            cPass = Col(PassCols), cDomain = Col(DomainCols), cProto = Col(ProtoCols), cFolder = Col(FolderCols);
        if (cHost < 0) throw new InvalidDataException("No host column found. The first line should name the columns, e.g. Name,Host,Port,Username,Password,Protocol.");

        var list = new List<ImportedConnection>();
        foreach (var r in rows.Skip(1))
        {
            string Get(int i) => i >= 0 && i < r.Count ? r[i].Trim() : "";
            var parsed = ParseAddress(Get(cHost));
            if (parsed == null) continue;
            var (scheme, host, port, urlUser) = parsed.Value;

            var protoText = Get(cProto).ToLowerInvariant();
            int givenPort = port ?? (int.TryParse(Get(cPort), out var pp) ? pp : 0);
            Protocol protocol =
                protoText.Contains("rdp") || protoText.Contains("remote desktop") || scheme == "rdp" ? Protocol.Rdp :
                protoText.Contains("vnc") || scheme == "vnc" ? Protocol.Vnc :
                protoText.Contains("ssh") || scheme is "ssh" or "sftp" ? Protocol.Ssh :
                givenPort == 3389 ? Protocol.Rdp : givenPort is >= 5900 and <= 5999 ? Protocol.Vnc : Protocol.Ssh;
            if (protoText.Length > 0 && !protoText.Contains("rdp") && !protoText.Contains("ssh") && !protoText.Contains("vnc") && !protoText.Contains("remote desktop")) continue;
            if (scheme is "http" or "https" or "ftp") continue; // web logins from a password manager export

            int finalPort = int.TryParse(Get(cPort), out var p2) && p2 is > 0 and < 65536 ? p2 : port ?? DefaultPort(protocol);
            var user = Get(cUser);
            if (user.Length == 0) user = urlUser;
            var domain = Get(cDomain);
            if (domain.Length == 0 && protocol == Protocol.Rdp) (domain, user) = DomainName.Split(user);
            var pass = Get(cPass);
            list.Add(new ImportedConnection
            {
                Name = Get(cName) is { Length: > 0 } n ? n : host, Protocol = protocol, Host = host, Port = finalPort,
                Username = protocol == Protocol.Vnc ? "" : user, Domain = protocol == Protocol.Rdp ? domain : "",
                Password = pass.Length > 0 ? pass : null,
                Folder = Get(cFolder).Replace('\\', '/').Trim('/'),
            });
        }
        return list;
    }

    /// <summary>RFC 4180-ish: quoted fields, doubled quotes, line breaks inside quotes.</summary>
    private static List<List<string>> ParseCsv(string text, char sep)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == sep) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                if (row.Any(f => f.Length > 0)) rows.Add(row);
                row = new List<string>();
            }
            else field.Append(c);
        }
        row.Add(field.ToString());
        if (row.Any(f => f.Length > 0)) rows.Add(row);
        if (rows.Count > 0 && rows[0].Count > 0) rows[0][0] = rows[0][0].TrimStart('﻿');
        return rows;
    }

    /// <summary>"host", "host:port", "user@host:port", "rdp://host", "ssh://user@host:22", "[::1]:22".</summary>
    public static (string Scheme, string Host, int? Port, string User)? ParseAddress(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        string scheme = "";
        int sch = text.IndexOf("://", StringComparison.Ordinal);
        if (sch > 0) { scheme = text[..sch].ToLowerInvariant(); text = text[(sch + 3)..]; }
        text = text.Split('/', '?')[0];
        string user = "";
        int at = text.LastIndexOf('@');
        if (at > 0) { user = Uri.UnescapeDataString(text[..at].Split(':')[0]); text = text[(at + 1)..]; }
        int? port = null;
        string host = text;
        if (text.StartsWith('['))
        {
            int end = text.IndexOf(']');
            if (end < 0) return null;
            host = text[1..end];
            if (end + 2 < text.Length && text[end + 1] == ':' && int.TryParse(text[(end + 2)..], out var p6)) port = p6;
        }
        else if (text.Count(c => c == ':') == 1)
        {
            int colon = text.IndexOf(':');
            host = text[..colon];
            if (int.TryParse(text[(colon + 1)..], out var p)) port = p;
        }
        if (port is <= 0 or > 65535) port = null;
        if (host.Length == 0 || host.Any(char.IsWhiteSpace)) return null;
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) return null;
        return (scheme, host, port, user);
    }

    public static int DefaultPort(Protocol p) => SessionSettings.DefaultPort(p);

    /// <summary>
    /// A VNC address: "host::5901" (port), "host:1" (display number, = port 5901), "host:5901", or anything
    /// <see cref="ParseAddress"/> reads.
    /// </summary>
    public static (string Host, int Port)? ParseVncAddress(string text)
    {
        text = text.Trim();
        int dbl = text.IndexOf("::", StringComparison.Ordinal);
        if (dbl > 0 && !text.StartsWith('[') && text.Count(c => c == ':') == 2)
            return int.TryParse(text[(dbl + 2)..], out var raw) && raw is > 0 and < 65536 && ParseAddress(text[..dbl]) is { } h
                ? (h.Host, raw) : null;
        if (ParseAddress(text) is not { } a) return null;
        int port = a.Port is int p ? (p < 100 ? VncClient.DefaultPort + p : p) : VncClient.DefaultPort;
        return (a.Host, port);
    }

    /// <summary>Picks the reader for a file by its extension or contents.</summary>
    public static (List<ImportedConnection> Items, string Source) FromFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".csv" || ext == ".tsv") return (FromCsv(path), "CSV");
        if (ext == ".remmina") return (FromRemminaFiles([path]), "Remmina");
        if (ext == ".vnc") return (FromVncFile(path), "VNC viewer file");
        if (ext == ".mxtsessions" || Path.GetFileName(path).Equals("MobaXterm.ini", StringComparison.OrdinalIgnoreCase)) return (FromMobaXterm(path), "MobaXterm");
        if (ext == ".xml") return (FromMRemoteNG(path), "mRemoteNG");

        var head = new string(File.ReadAllText(path).Take(4096).ToArray());
        if (head.TrimStart().StartsWith('<')) return (FromMRemoteNG(path), "mRemoteNG");
        if (head.Contains("[Bookmarks", StringComparison.OrdinalIgnoreCase)) return (FromMobaXterm(path), "MobaXterm");
        if (head.Contains("[remmina]", StringComparison.OrdinalIgnoreCase)) return (FromRemminaFiles([path]), "Remmina");
        if (head.Contains("[connection]", StringComparison.OrdinalIgnoreCase)) return (FromVncFile(path), "VNC viewer file");
        if (Regex.IsMatch(head, @"^\s*Host\s+\S", RegexOptions.Multiline | RegexOptions.IgnoreCase)) return (FromSshConfig(path), "SSH config");
        return (FromCsv(path), "CSV");
    }

    // ───────────── PuTTY on Linux / Remmina ─────────────

    /// <summary>Reads PuTTY's per-session files (key=value lines, as written by PuTTY on Linux).</summary>
    public static List<ImportedConnection> FromPuttyFiles(string dir)
    {
        var list = new List<ImportedConnection>();
        if (!Directory.Exists(dir)) return list;
        foreach (var file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
        {
            var v = ReadKeyValues(file);
            if (!v.GetValueOrDefault("Protocol", "ssh").Equals("ssh", StringComparison.OrdinalIgnoreCase)) continue;
            var host = v.GetValueOrDefault("HostName", "").Trim();
            if (host.Length == 0) continue; // "Default Settings" and empty templates
            var user = v.GetValueOrDefault("UserName", "").Trim();
            int at = host.LastIndexOf('@');
            if (at > 0) { if (user.Length == 0) user = host[..at]; host = host[(at + 1)..]; }
            int port = int.TryParse(v.GetValueOrDefault("PortNumber"), out var p) && p is > 0 and < 65536 ? p : 22;
            list.Add(new ImportedConnection
            {
                Name = Uri.UnescapeDataString(Path.GetFileName(file)), Protocol = Protocol.Ssh, Host = host, Port = port, Username = user,
                PrivateKeyPath = v.GetValueOrDefault("PublicKeyFile", "").Trim(), SourcePath = file,
            });
        }
        return list;
    }

    /// <summary>Where Remmina (the usual Linux remote desktop client) keeps its connection profiles.</summary>
    public static IEnumerable<string> RemminaDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".local", "share");
        return new[]
        {
            Path.Combine(data, "remmina"),
            Path.Combine(home, ".remmina"),
            Path.Combine(home, ".var", "app", "org.remmina.Remmina", "data", "remmina"), // Flatpak
        }.Where(Directory.Exists);
    }

    public static List<string> RemminaFiles() =>
        RemminaDirs().SelectMany(d => Directory.EnumerateFiles(d, "*.remmina")).ToList();

    public static bool HasRemmina() => RemminaFiles().Count > 0;

    /// <summary>
    /// Reads Remmina profiles (.remmina, INI format): RDP and SSH/SFTP connections with server, user, domain, group
    /// and RDP display options. Remmina's saved passwords are encrypted with its own key and are not read.
    /// </summary>
    public static List<ImportedConnection> FromRemminaFiles(IEnumerable<string> files)
    {
        var list = new List<ImportedConnection>();
        foreach (var file in files)
        {
            Dictionary<string, string> v;
            try { v = ReadKeyValues(file); } catch (IOException) { continue; }
            var proto = v.GetValueOrDefault("protocol", "").ToUpperInvariant();
            Protocol protocol;
            if (proto == "RDP") protocol = Protocol.Rdp;
            else if (proto is "SSH" or "SFTP") protocol = Protocol.Ssh;
            else if (proto == "VNC") protocol = Protocol.Vnc;
            else continue; // incoming VNC, SPICE, X2Go...: nothing this app can open
            var server = v.GetValueOrDefault("server", "").Trim();
            if (ParseAddress(server) is not { } a) continue;
            if (protocol == Protocol.Vnc && ParseVncAddress(server) is { } va) a = a with { Port = va.Port };
            var user = v.GetValueOrDefault("username", "").Trim();
            if (user.Length == 0) user = a.User;
            var domain = v.GetValueOrDefault("domain", "").Trim();
            if (protocol == Protocol.Rdp && domain.Length == 0) (domain, user) = DomainName.Split(user);
            if (protocol == Protocol.Vnc) { user = ""; domain = ""; }

            SessionSettings? template = null;
            if (protocol == Protocol.Vnc)
            {
                template = SessionSettings.DefaultsFor(Protocol.Vnc);
                template.ViewOnly = v.GetValueOrDefault("viewonly") == "1";
                template.RedirectClipboard = v.GetValueOrDefault("disableclipboard") != "1";
                if (v.TryGetValue("scale", out var scale)) template.SmartSizing = scale != "0";
            }
            if (protocol == Protocol.Rdp)
            {
                template = SessionSettings.DefaultsFor(Protocol.Rdp);
                // resolution_mode 0 = custom size (1 = this screen, 2 = window size); older files have only the size.
                if (v.GetValueOrDefault("resolution_mode", "0") == "0"
                    && int.TryParse(v.GetValueOrDefault("resolution_width"), out var w) && w > 0
                    && int.TryParse(v.GetValueOrDefault("resolution_height"), out var h) && h > 0)
                { template.Width = w; template.Height = h; }
                if (int.TryParse(v.GetValueOrDefault("colordepth"), out var depth) && depth is 8 or 15 or 16 or 24 or 32) template.ColorDepth = depth;
                template.RedirectDrives = v.GetValueOrDefault("sharefolder", "").Trim().Length > 0;
                template.RedirectPrinters = v.GetValueOrDefault("shareprinter") == "1";
                template.RedirectClipboard = v.GetValueOrDefault("disableclipboard") != "1";
            }
            var name = v.GetValueOrDefault("name", "").Trim();
            list.Add(new ImportedConnection
            {
                Name = name.Length > 0 ? name : a.Host, Protocol = protocol, Host = a.Host, Port = a.Port ?? DefaultPort(protocol),
                Username = user, Domain = domain,
                Folder = v.GetValueOrDefault("group", "").Trim().Trim('/'),
                PrivateKeyPath = protocol == Protocol.Ssh ? v.GetValueOrDefault("ssh_privatekey", "").Trim() : "",
                Template = template, SourcePath = file,
            });
        }
        return list;
    }

    // ───────────── VNC viewer files (.vnc) ─────────────

    /// <summary>
    /// Reads a saved VNC viewer connection: UltraVNC and TightVNC ("[connection] host= port="), and RealVNC ("Host=host:port").
    /// The password stored in the file is not read.
    /// </summary>
    public static List<ImportedConnection> FromVncFile(string path)
    {
        var v = ReadKeyValues(path);
        var host = v.GetValueOrDefault("host", "").Trim();
        if (host.Length == 0) return [];
        (string Host, int Port)? address =
            v.TryGetValue("port", out var portText) && int.TryParse(portText, out var port) && port is > 0 and < 65536
            && ParseAddress(host) is { Port: null } plain
                ? (plain.Host, port)
                : ParseVncAddress(host);
        if (address is not { } a) return [];
        var template = SessionSettings.DefaultsFor(Protocol.Vnc);
        template.ViewOnly = v.GetValueOrDefault("viewonly", "0").Trim() is "1" or "true" or "True";
        if (v.TryGetValue("autoscaling", out var auto)) template.SmartSizing = auto.Trim() == "1";
        else if (v.TryGetValue("scale", out var scale)) template.SmartSizing = scale.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
        else if (v.TryGetValue("Scaling", out var realScale)) template.SmartSizing = !realScale.Trim().Equals("None", StringComparison.OrdinalIgnoreCase);
        var name = Path.GetFileNameWithoutExtension(path);
        return
        [
            new ImportedConnection
            {
                Name = name.Length > 0 ? name : a.Host, Protocol = Protocol.Vnc, Host = a.Host, Port = a.Port,
                Template = template, SourcePath = path,
            },
        ];
    }

    /// <summary>key=value lines; [sections] and comments are ignored, the first value of a key wins.</summary>
    private static Dictionary<string, string> ReadKeyValues(string path)
    {
        var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            var t = line.Trim();
            if (t.Length == 0 || t[0] is '[' or '#' or ';') continue;
            int i = t.IndexOf('=');
            if (i <= 0) continue;
            v.TryAdd(t[..i].Trim(), t[(i + 1)..]);
        }
        return v;
    }
}
