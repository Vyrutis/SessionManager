using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SessionManager;

public enum Protocol { Ssh, Rdp, Vnc, Web }

public enum NodeKind { Folder, Session }

/// <summary>Whether a session's host answered on its port the last time it was checked.</summary>
public enum Reachability { Unknown, Online, Offline }

/// <summary>Connection settings for a session. Passwords are never stored here (see <see cref="CredentialStore"/>).</summary>
public sealed class SessionSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    /// <summary>Shared credential profile; when set, Username/Domain/password come from it.</summary>
    public Guid? CredentialId { get; set; }
    /// <summary>SSH session used as a gateway (jump host) to reach this host.</summary>
    public Guid? JumpHostId { get; set; }

    // SSH
    public string PrivateKeyPath { get; set; } = "";
    public int FontSize { get; set; } = 14;
    public string FontFamily { get; set; } = "";
    /// <summary>Terminal color scheme name; "Auto" follows the app theme.</summary>
    public string ColorScheme { get; set; } = "Auto";
    /// <summary>Port forwards, one per line: "L 8080:db:5432", "R 9000:localhost:3000", "D 1080".</summary>
    public List<string> Tunnels { get; set; } = new();

    // RDP
    public string Domain { get; set; } = "";
    /// <summary>0 = fit the remote desktop to the tab.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public int ColorDepth { get; set; } = 32;
    public bool RedirectClipboard { get; set; } = true;
    public bool RedirectDrives { get; set; }
    public bool RedirectPrinters { get; set; }
    /// <summary>RDP: scale the remote desktop to the tab. VNC: scale to fit (off = actual size with scroll bars).</summary>
    public bool SmartSizing { get; set; }

    // VNC
    /// <summary>Watch only: keyboard and mouse are not sent.</summary>
    public bool ViewOnly { get; set; }

    // Web (a device's web page, e.g. a router or switch)
    /// <summary>https (true) or http.</summary>
    public bool WebHttps { get; set; } = true;
    /// <summary>Page to open, e.g. "/admin"; empty = the start page.</summary>
    public string WebPath { get; set; } = "";
    /// <summary>SHA-256 fingerprint of a certificate the user chose to trust for this device (self-signed etc.); empty = none.</summary>
    public string TrustedCertificate { get; set; } = "";

    /// <summary>Network card address for Wake-on-LAN, e.g. "AA:BB:CC:DD:EE:FF"; empty = not set.</summary>
    public string MacAddress { get; set; } = "";

    /// <summary>The address of a web session, e.g. https://10.0.0.1:8443/admin.</summary>
    public string WebUrl(string? host = null, int? port = null)
    {
        host ??= Host;
        int p = port ?? Port;
        if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]"; // IPv6
        bool defaultPort = p == (WebHttps ? 443 : 80);
        var path = string.IsNullOrEmpty(WebPath) ? "/" : WebPath.StartsWith('/') ? WebPath : "/" + WebPath;
        return $"{(WebHttps ? "https" : "http")}://{host}{(defaultPort ? "" : ":" + p)}{path}";
    }

    /// <summary>Reads "https://host:port/path", "http://host" or just "host"; false when it isn't a usable address.</summary>
    public static bool TryParseWebUrl(string text, out bool https, out string host, out int port, out string path)
    {
        https = true; host = ""; port = 443; path = "";
        text = text.Trim();
        if (text.Length == 0) return false;
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return false;
        https = uri.Scheme == "https";
        host = uri.Host.Trim('[', ']');
        port = uri.Port;
        path = uri.PathAndQuery == "/" ? "" : uri.PathAndQuery + uri.Fragment;
        return true;
    }

    public SessionSettings Clone()
    {
        var c = (SessionSettings)MemberwiseClone();
        c.Tunnels = new List<string>(Tunnels);
        return c;
    }

    public static SessionSettings DefaultsFor(Protocol p) => p switch
    {
        Protocol.Ssh => new() { Port = 22 },
        Protocol.Vnc => new() { Port = 5900, SmartSizing = true },
        Protocol.Web => new() { Port = 443 },
        _ => new() { Port = 3389 },
    };

    public static int DefaultPort(Protocol p) => p switch { Protocol.Ssh => 22, Protocol.Vnc => 5900, Protocol.Web => 443, _ => 3389 };
}

/// <summary>A folder or a session in the tree.</summary>
public sealed class Node : INotifyPropertyChanged
{
    private string _name = "";
    private bool _isExpanded;
    private bool _isSelected;

    public Guid Id { get; set; } = Guid.NewGuid();
    public NodeKind Kind { get; set; }
    public Protocol Protocol { get; set; }
    public bool IsRoot { get; set; }
    public SessionSettings? Settings { get; set; }
    public ObservableCollection<Node> Children { get; set; } = new();

    public string Name
    {
        get => _name;
        set { _name = value; OnChanged(); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnChanged(); }
    }

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnChanged(); }
    }

    private bool _isPicked;

    /// <summary>Part of a multiple selection (Ctrl/Shift+click).</summary>
    [JsonIgnore]
    public bool IsPicked
    {
        get => _isPicked;
        set { if (_isPicked == value) return; _isPicked = value; OnChanged(); }
    }

    private bool _isFavorite;

    /// <summary>Marked with a star; the filter box can show only these.</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite == value) return; _isFavorite = value; OnChanged(); }
    }

    /// <summary>When the session was last opened (for the recent list in quick connect).</summary>
    public DateTime? LastUsed { get; set; }

    private Reachability _reach;
    private string _reachText = "";

    [JsonIgnore]
    public Reachability Reach
    {
        get => _reach;
        set { if (_reach == value) return; _reach = value; OnChanged(); }
    }

    /// <summary>"Online: port 22 answered in 12 ms" etc., for the tooltip.</summary>
    [JsonIgnore]
    public string ReachText
    {
        get => _reachText;
        set { if (_reachText == value) return; _reachText = value; OnChanged(); }
    }

    private bool _isVisible = true;

    /// <summary>False when hidden by the filter box.</summary>
    [JsonIgnore]
    public bool IsVisible
    {
        get => _isVisible;
        set { _isVisible = value; OnChanged(); }
    }

    [JsonIgnore] public Node? Parent { get; set; }
    [JsonIgnore] public bool IsFolder => Kind == NodeKind.Folder;
    [JsonIgnore] public bool IsSession => Kind == NodeKind.Session;

    [JsonIgnore]
    public string Glyph => IsFolder && !IsRoot
        ? "\uE8B7" // folder
        : Protocol switch { Protocol.Ssh => "\uE756", Protocol.Web => "\uE774", _ => "\uE7F4" }; // command prompt / globe / monitor

    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            if (Settings == null) return "";
            var s = Settings;
            if (Protocol == Protocol.Web) return s.WebUrl().TrimEnd('/');
            var user = string.IsNullOrEmpty(s.Username) ? "" : s.Username + "@";
            var port = s.Port == SessionSettings.DefaultPort(Protocol) ? "" : ":" + s.Port;
            return $"{user}{s.Host}{port}";
        }
    }

    public void NotifySettingsChanged() => OnChanged(nameof(Subtitle));

    public IEnumerable<Node> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    public bool IsAncestorOf(Node other)
    {
        for (var p = other.Parent; p != null; p = p.Parent)
            if (p == this) return true;
        return false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A command for SSH terminals, sent from the tab's menu.</summary>
public sealed class SavedCommand
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Press Enter after the text (run it); off = only type it.</summary>
    public bool Run { get; set; } = true;
}

/// <summary>A username/password shared by several sessions. The password is in Credential Manager under <see cref="Id"/>.</summary>
public sealed class CredentialProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";
}

public sealed class AppData
{
    public int Version { get; set; } = 1;
    public ObservableCollection<Node> Roots { get; set; } = new();
    public Dictionary<Protocol, SessionSettings> Presets { get; set; } = new();
    /// <summary>"Light" (Silver) or "Dark" (Midnight).</summary>
    public string Theme { get; set; } = "Light";
    public List<CredentialProfile> Credentials { get; set; } = new();
    /// <summary>Whether new SSH tabs open with the SFTP file panel.</summary>
    public bool ShowFiles { get; set; } = true;
    /// <summary>Colors, background picture, size and animations (the palette menu in the title bar).</summary>
    public UiSettings Ui { get; set; } = new();
    /// <summary>Domains offered in the domain drop-down before the username, e.g. "CORP".</summary>
    public List<string> Domains { get; set; } = new();
    /// <summary>The SSH / RDP / VNC tab shown above the session list.</summary>
    public Protocol ActiveTab { get; set; }
    /// <summary>Tabs the user has hidden; at least one tab always stays visible.</summary>
    public List<Protocol> HiddenTabs { get; set; } = new();
    /// <summary>True once the user has arranged the list by dragging; false = folders first, then sessions, A to Z.</summary>
    public bool ManualOrder { get; set; }
    /// <summary>Green / red dots on sessions whose host answers / doesn't answer (checked about once a minute).</summary>
    public bool ShowStatus { get; set; } = true;
    /// <summary>Look for a newer GitHub release when the app starts (at most once a day).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>"en", "lt", or empty to follow the Windows language.</summary>
    public string Language { get; set; } = "";
    /// <summary>When the last automatic update check ran (UTC).</summary>
    public DateTime LastUpdateCheck { get; set; }
    /// <summary>SSH commands that can be sent to a terminal (and to all terminals with multi-exec).</summary>
    public List<SavedCommand> Commands { get; set; } = new();
}

public sealed class UiSettings
{
    /// <summary>Accent color as #RRGGBB; null = the theme's own.</summary>
    public string? Accent { get; set; }
    /// <summary>Main color that tints the window surfaces, as #RRGGBB; null = the theme's own (Silver / Midnight).</summary>
    public string? Main { get; set; }
    /// <summary>Custom background picture (a copy in the data folder); null = none.</summary>
    public string? BackgroundImage { get; set; }
    /// <summary>How much the background picture shows through the panels, 0.1 to 0.85.</summary>
    public double BackgroundVisibility { get; set; } = 0.35;
    public bool BackgroundBlur { get; set; }
    /// <summary>Interface size, 0.8 to 1.5.</summary>
    public double Scale { get; set; } = 1.0;
    /// <summary>Window close animation and other motion.</summary>
    public bool Animations { get; set; } = true;

    public UiSettings Clone() => (UiSettings)MemberwiseClone();

    public const double MinScale = 0.8, MaxScale = 1.5;
    public const double MinVisibility = 0.1, MaxVisibility = 0.85;

    /// <summary>Replaces unreadable colors and out-of-range numbers (e.g. from a hand-edited file) with defaults.</summary>
    public static UiSettings Normalize(UiSettings? ui)
    {
        ui ??= new UiSettings();
        if (!IsColor(ui.Accent)) ui.Accent = null;
        if (!IsColor(ui.Main)) ui.Main = null;
        if (string.IsNullOrWhiteSpace(ui.BackgroundImage)) ui.BackgroundImage = null;
        ui.BackgroundVisibility = Clamp(ui.BackgroundVisibility, MinVisibility, MaxVisibility, 0.35);
        ui.Scale = Clamp(ui.Scale, MinScale, MaxScale, 1.0);
        return ui;
    }

    /// <summary>#RRGGBB (the # is optional).</summary>
    public static bool IsColor(string? hex) =>
        hex != null && System.Text.RegularExpressions.Regex.IsMatch(hex.Trim(), "^#?[0-9A-Fa-f]{6}$");

    private static double Clamp(double v, double min, double max, double fallback) =>
        double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
}

public static class ProtocolExtensions
{
    /// <summary>"SSH", "Remote Desktop", "VNC" or "Web".</summary>
    public static string DisplayName(this Protocol p) => p switch { Protocol.Ssh => "SSH", Protocol.Vnc => "VNC", Protocol.Web => "Web", _ => "Remote Desktop" };
}
