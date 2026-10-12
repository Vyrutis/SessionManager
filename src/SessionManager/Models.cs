using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SessionManager;

public enum Protocol { Ssh, Rdp }

public enum NodeKind { Folder, Session }

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
    public bool SmartSizing { get; set; }

    public SessionSettings Clone()
    {
        var c = (SessionSettings)MemberwiseClone();
        c.Tunnels = new List<string>(Tunnels);
        return c;
    }

    public static SessionSettings DefaultsFor(Protocol p) => new() { Port = p == Protocol.Ssh ? 22 : 3389 };
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
        : (Protocol == Protocol.Ssh ? "\uE756" : "\uE7F4"); // command prompt / monitor

    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            if (Settings == null) return "";
            var s = Settings;
            var user = string.IsNullOrEmpty(s.Username) ? "" : s.Username + "@";
            var defaultPort = Protocol == Protocol.Ssh ? 22 : 3389;
            var port = s.Port == defaultPort ? "" : ":" + s.Port;
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
}
