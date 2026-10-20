using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SessionManager;

public partial class SessionDialog : Window
{
    private static readonly (string Label, int W, int H)[] Resolutions =
    {
        ("Fit to tab", 0, 0), ("1024 × 768", 1024, 768), ("1280 × 720", 1280, 720), ("1280 × 1024", 1280, 1024),
        ("1366 × 768", 1366, 768), ("1600 × 900", 1600, 900), ("1920 × 1080", 1920, 1080), ("2560 × 1440", 2560, 1440),
    };

    private static readonly (string Label, int Bpp)[] ColorDepths =
        { ("True color (32 bit)", 32), ("24 bit", 24), ("High color (16 bit)", 16), ("15 bit", 15) };

    private readonly Protocol _protocol;
    private readonly bool _hasSavedPassword;
    private readonly IReadOnlyList<CredentialProfile> _profiles;
    private string _ownUser = "", _ownDomain = "";

    public string SessionName { get; private set; } = "";
    public SessionSettings Settings { get; private set; }

    /// <summary>Null = keep whatever is stored; "" with SavePassword=false = delete.</summary>
    public string? NewPassword { get; private set; }
    public bool SavePassword { get; private set; }

    /// <param name="jumpHosts">SSH sessions that can be used as a jump host (excluding this one).</param>
    public SessionDialog(Window owner, Protocol protocol, string title, string name, SessionSettings settings, bool hasSavedPassword,
        IReadOnlyList<CredentialProfile> profiles, IReadOnlyList<(Guid Id, string Label)> jumpHosts)
    {
        InitializeComponent();
        Theme.ApplyTo(this);
        Owner = owner;
        Title = title;
        _protocol = protocol;
        _hasSavedPassword = hasSavedPassword;
        _profiles = profiles;
        Settings = settings.Clone();

        NameBox.Text = name;
        HostBox.Text = settings.Host;
        PortBox.Text = settings.Port.ToString();
        SavePasswordBox.IsChecked = true;
        PasswordHint.Visibility = hasSavedPassword ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.PasswordChanged += (_, _) =>
            PasswordHint.Visibility = hasSavedPassword && PasswordBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        SshGroup.Visibility = protocol == Protocol.Ssh ? Visibility.Visible : Visibility.Collapsed;
        RdpGroup.Visibility = protocol == Protocol.Rdp ? Visibility.Visible : Visibility.Collapsed;
        VncGroup.Visibility = protocol == Protocol.Vnc ? Visibility.Visible : Visibility.Collapsed;
        WebGroup.Visibility = protocol == Protocol.Web ? Visibility.Visible : Visibility.Collapsed;
        // VNC servers ask for a password only.
        UserLabel.Visibility = UserRow.Visibility = protocol == Protocol.Vnc ? Visibility.Collapsed : Visibility.Visible;
        if (protocol == Protocol.Web)
        {
            // Web logins have no Windows domain.
            DomainBox.Visibility = UserSlash.Visibility = Visibility.Collapsed;
            Grid.SetColumn(UserBox, 0);
            Grid.SetColumnSpan(UserBox, 3);
            HostBox.ToolTip = "Host name or IP address, or a whole address such as https://10.0.0.1:8443/admin";
            HostBox.LostFocus += (_, _) => SplitWebAddress();
            WebPathBox.Text = settings.WebPath;
            WebHttpsBox.IsChecked = settings.WebHttps;
            ForgetCertBox.Visibility = string.IsNullOrEmpty(settings.TrustedCertificate) ? Visibility.Collapsed : Visibility.Visible;
        }

        // Credential profiles
        CredentialBox.Items.Add(new ComboBoxItem { Content = protocol == Protocol.Vnc ? "This session only (enter the password below)" : "This session only (enter below)" });
        foreach (var p in profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            CredentialBox.Items.Add(new ComboBoxItem { Content = $"{p.Name}  ({p.Username})", Tag = p });
        // SSH keeps the domain inside the username (DOMAIN\user); Remote Desktop stores it separately.
        (_ownDomain, _ownUser) = SplitUser(settings.Username, settings.Domain);
        CredentialBox.SelectedIndex = Math.Max(0, CredentialBox.Items.Cast<ComboBoxItem>()
            .ToList().FindIndex(i => i.Tag is CredentialProfile p && p.Id == settings.CredentialId));

        // Jump host (SSH) / SSH gateway (RDP, VNC)
        var jumpBox = JumpBoxFor(protocol);
        jumpBox.Items.Add(new ComboBoxItem { Content = "None — connect directly" });
        foreach (var (id, label) in jumpHosts) jumpBox.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        jumpBox.SelectedIndex = Math.Max(0, jumpBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => i.Tag is Guid g && g == settings.JumpHostId));

        foreach (var f in TerminalSchemes.Fonts) FontBox.Items.Add(f);
        if (!string.IsNullOrWhiteSpace(settings.FontFamily) && !FontBox.Items.Contains(settings.FontFamily)) FontBox.Items.Add(settings.FontFamily);
        FontBox.SelectedItem = string.IsNullOrWhiteSpace(settings.FontFamily) ? TerminalSchemes.Fonts[0] : settings.FontFamily;
        foreach (var n in TerminalSchemes.Names) SchemeBox.Items.Add(n);
        SchemeBox.SelectedItem = SchemeBox.Items.Contains(settings.ColorScheme) ? settings.ColorScheme : TerminalSchemes.Auto;
        TunnelsBox.Text = string.Join(Environment.NewLine, settings.Tunnels);

        KeyBox.Text = settings.PrivateKeyPath;
        var sizes = new SortedSet<int> { 10, 11, 12, 13, 14, 15, 16, 18, 20, 24, settings.FontSize };
        foreach (var size in sizes) FontSizeBox.Items.Add(size.ToString());
        FontSizeBox.SelectedItem = settings.FontSize.ToString();

        foreach (var r in Resolutions) ResolutionBox.Items.Add(r.Label);
        int ri = Array.FindIndex(Resolutions, r => r.W == settings.Width && r.H == settings.Height);
        if (ri < 0)
        {
            ResolutionBox.Items.Add($"{settings.Width} × {settings.Height}");
            ri = ResolutionBox.Items.Count - 1;
        }
        ResolutionBox.SelectedIndex = ri;

        foreach (var c in ColorDepths) ColorBox.Items.Add(c.Label);
        ColorBox.SelectedIndex = Math.Max(0, Array.FindIndex(ColorDepths, c => c.Bpp == settings.ColorDepth));
        ClipboardBox.IsChecked = settings.RedirectClipboard;
        DrivesBox.IsChecked = settings.RedirectDrives;
        PrintersBox.IsChecked = settings.RedirectPrinters;
        SmartSizingBox.IsChecked = settings.SmartSizing;
        VncFitBox.IsChecked = settings.SmartSizing;
        ViewOnlyBox.IsChecked = settings.ViewOnly;
        VncClipboardBox.IsChecked = settings.RedirectClipboard;

        CredentialBox_SelectionChanged(CredentialBox, null!);
        Loaded += (_, _) => (string.IsNullOrEmpty(HostBox.Text) ? HostBox : NameBox).Focus();
    }

    private CredentialProfile? SelectedProfile => (CredentialBox.SelectedItem as ComboBoxItem)?.Tag as CredentialProfile;

    private void CredentialBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || UserBox == null) return;
        var p = SelectedProfile;
        bool own = p == null;
        if (own)
        {
            UserBox.Text = _ownUser;
            DomainBox.Domain = _ownDomain;
        }
        else
        {
            if (UserBox.IsEnabled) { _ownUser = UserBox.Text; _ownDomain = DomainBox.Domain; }
            // A profile's separate domain is only used by Remote Desktop.
            var (domain, user) = SplitUser(p!.Username, _protocol == Protocol.Rdp ? p.Domain : "");
            UserBox.Text = user;
            DomainBox.Domain = domain;
        }
        UserBox.IsEnabled = DomainBox.IsEnabled = PasswordBox.IsEnabled = SavePasswordBox.IsEnabled = own;
        PasswordHint.Text = own ? "(saved — leave empty to keep it)" : "(from the credential profile)";
        PasswordHint.Visibility = !own || (_hasSavedPassword && PasswordBox.Password.Length == 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Domain and user from a username that may contain "DOMAIN\user"; otherwise the separate domain is used.</summary>
    private static (string Domain, string User) SplitUser(string username, string domain)
    {
        var (d, u) = DomainPicker.Split(username);
        return d.Length > 0 ? (d, u) : (DomainPicker.Clean(domain), username);
    }

    private static Guid? SelectedId(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as Guid?;

    private ComboBox JumpBoxFor(Protocol p) => p switch { Protocol.Ssh => JumpBox, Protocol.Vnc => VncJumpBox, Protocol.Web => WebJumpBox, _ => RdpJumpBox };

    /// <summary>A pasted "https://host:port/page" is split into Host, Port, Page and https.</summary>
    private void SplitWebAddress()
    {
        var text = HostBox.Text.Trim();
        if (!text.Contains("://") && !text.Contains('/')) return;
        if (!SessionSettings.TryParseWebUrl(text, out var https, out var host, out var port, out var path)) return;
        HostBox.Text = host;
        PortBox.Text = port.ToString();
        WebHttpsBox.IsChecked = https;
        if (path.Length > 0) WebPathBox.Text = path;
    }

    /// <summary>Switching http / https moves the port along when it is the usual one.</summary>
    private void WebHttpsBox_Click(object sender, RoutedEventArgs e)
    {
        bool https = WebHttpsBox.IsChecked == true;
        if (https && PortBox.Text.Trim() == "80") PortBox.Text = "443";
        else if (!https && PortBox.Text.Trim() == "443") PortBox.Text = "80";
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose private key",
            Filter = "All files (*.*)|*.*",
            InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };
        if (dlg.ShowDialog(this) == true) KeyBox.Text = dlg.FileName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_protocol == Protocol.Web) SplitWebAddress();
        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            MessageBox.Show(this, "Enter a host name or IP address.", Title);
            HostBox.Focus();
            return;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port < 1 || port > 65535)
        {
            MessageBox.Show(this, "The port must be a number between 1 and 65535.", Title);
            PortBox.Focus();
            return;
        }

        var tunnels = TunnelsBox.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (_protocol == Protocol.Ssh)
        {
            foreach (var t in tunnels)
            {
                try { SshConnector.ParseTunnel(t); }
                catch (FormatException ex)
                {
                    MessageBox.Show(this, "Check the tunnels:\n\n" + ex.Message, Title);
                    TunnelsBox.Focus();
                    return;
                }
            }
        }

        var s = Settings;
        s.Host = host;
        s.Port = port;
        if (SelectedProfile is { } profile)
        {
            s.Username = profile.Username;
            s.Domain = profile.Domain;
        }
        else
        {
            // A domain typed into the username box ("CORP\admin") wins over the drop-down.
            var (domain, user) = SplitUser(UserBox.Text.Trim(), DomainBox.Domain);
            if (_protocol != Protocol.Web && DomainPicker.Remember(domain)) DomainPicker.SavedChanged?.Invoke();
            s.Username = _protocol switch
            {
                Protocol.Ssh => DomainPicker.Combine(domain, user),
                Protocol.Vnc => "",
                Protocol.Web => UserBox.Text.Trim(), // typed as is, backslashes included
                _ => user,
            };
            s.Domain = _protocol == Protocol.Rdp ? domain : "";
        }
        s.CredentialId = SelectedProfile?.Id;
        s.JumpHostId = SelectedId(JumpBoxFor(_protocol));

        if (_protocol == Protocol.Ssh)
        {
            s.PrivateKeyPath = KeyBox.Text.Trim();
            if (int.TryParse(FontSizeBox.SelectedItem as string, out var fs)) s.FontSize = fs;
            var font = FontBox.SelectedItem as string ?? "";
            s.FontFamily = font == TerminalSchemes.Fonts[0] ? "" : font;
            s.ColorScheme = SchemeBox.SelectedItem as string ?? TerminalSchemes.Auto;
            s.Tunnels = tunnels;
        }
        else if (_protocol == Protocol.Web)
        {
            var path = WebPathBox.Text.Trim();
            s.WebPath = path.Length == 0 || path == "/" ? "" : path.StartsWith('/') ? path : "/" + path;
            s.WebHttps = WebHttpsBox.IsChecked == true;
            if (ForgetCertBox.IsChecked == true) s.TrustedCertificate = "";
        }
        else if (_protocol == Protocol.Vnc)
        {
            s.SmartSizing = VncFitBox.IsChecked == true;
            s.ViewOnly = ViewOnlyBox.IsChecked == true;
            s.RedirectClipboard = VncClipboardBox.IsChecked == true;
        }
        else
        {
            int ri = ResolutionBox.SelectedIndex;
            if (ri < Resolutions.Length) { s.Width = Resolutions[ri].W; s.Height = Resolutions[ri].H; }
            s.ColorDepth = ColorDepths[Math.Max(0, ColorBox.SelectedIndex)].Bpp;
            s.RedirectClipboard = ClipboardBox.IsChecked == true;
            s.RedirectDrives = DrivesBox.IsChecked == true;
            s.RedirectPrinters = PrintersBox.IsChecked == true;
            s.SmartSizing = SmartSizingBox.IsChecked == true;
        }

        SessionName = string.IsNullOrWhiteSpace(NameBox.Text) ? host : NameBox.Text.Trim();
        SavePassword = SavePasswordBox.IsChecked == true;
        if (SelectedProfile != null)
        {
            // The profile's password is used; leave this session's own saved password untouched.
            SavePassword = true;
            NewPassword = null;
        }
        else if (!SavePassword)
            NewPassword = _hasSavedPassword ? "" : null;
        else
            NewPassword = PasswordBox.Password.Length > 0 ? PasswordBox.Password : null;

        DialogResult = true;
    }
}
