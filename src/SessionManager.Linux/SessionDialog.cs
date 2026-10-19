using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SessionManager.Terminal;

namespace SessionManager;

/// <summary>New / edit session: address, login (own or a shared credential profile), and SSH, RDP or VNC options.</summary>
public sealed class SessionDialog
{
    private readonly Protocol _protocol;
    private readonly List<CredentialProfile> _profiles;
    private readonly bool _hasSavedPassword;

    private readonly TextBox _name = new();
    private readonly TextBox _host = new() { Watermark = "server.example.com or 10.0.0.5" };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, FormatString = "0", Increment = 1, Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _profile = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly AutoCompleteBox _domain = new() { Watermark = "Domain (optional)", MinimumPrefixLength = 0, FilterMode = AutoCompleteFilterMode.StartsWith };
    private readonly TextBox _user = new() { Watermark = "User name" };
    private readonly TextBox _password = new() { PasswordChar = '•' };
    private readonly CheckBox _savePassword = new() { Content = "Save password in the keyring", IsChecked = true };
    private readonly CheckBox _forget = new() { Content = "Forget the saved password" };
    private readonly ComboBox _jump = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly List<Guid?> _jumpIds = new();

    // SSH
    private readonly TextBox _key = new() { Watermark = "Default keys in ~/.ssh" };
    private readonly NumericUpDown _fontSize = new() { Minimum = 6, Maximum = 72, FormatString = "0", Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly AutoCompleteBox _font = new() { Watermark = "Default monospace font", MinimumPrefixLength = 0, FilterMode = AutoCompleteFilterMode.Contains };
    private readonly ComboBox _scheme = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _tunnels = new() { AcceptsReturn = true, Height = 80, Watermark = "L 8080:db:5432\nR 9000:localhost:3000\nD 1080", FontFamily = new FontFamily("DejaVu Sans Mono, Consolas, monospace") };

    // RDP
    private static readonly (string Label, int W, int H)[] Sizes =
        [("Fit to the window", 0, 0), ("1280 × 720", 1280, 720), ("1366 × 768", 1366, 768), ("1600 × 900", 1600, 900), ("1920 × 1080", 1920, 1080), ("2560 × 1440", 2560, 1440), ("Custom", -1, -1)];
    private readonly ComboBox _size = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _width = new() { Minimum = 640, Maximum = 8192, FormatString = "0", Width = 110 };
    private readonly NumericUpDown _height = new() { Minimum = 480, Maximum = 8192, FormatString = "0", Width = 110 };
    private readonly ComboBox _depth = new() { ItemsSource = new[] { 32, 24, 16, 15 }, Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _clipboard = new() { Content = "Share the clipboard" };
    private readonly CheckBox _drives = new() { Content = "Share my home folder" };
    private readonly CheckBox _printers = new() { Content = "Share printers" };
    private readonly CheckBox _smart = new() { Content = "Scale a fixed size to the window" };

    // VNC
    private readonly CheckBox _vncFit = new() { Content = "Scale the remote screen to the tab" };
    private readonly CheckBox _viewOnly = new() { Content = "View only (don't send keyboard and mouse)" };

    private readonly SessionSettings _original;
    private Window? _window;

    public string SessionName => (_name.Text ?? "").Trim();
    public SessionSettings Result { get; private set; } = new();
    /// <summary>A password typed in the dialog to save; null = keep what is saved.</summary>
    public string? NewPassword { get; private set; }
    public bool ForgetPassword { get; private set; }

    public SessionDialog(Protocol protocol, string name, SessionSettings settings, List<CredentialProfile> profiles,
        IReadOnlyList<(Guid Id, string Label)> jumpHosts, bool hasSavedPassword, IEnumerable<string> domains)
    {
        _protocol = protocol;
        _profiles = profiles;
        _hasSavedPassword = hasSavedPassword;
        _original = settings;
        var s = settings;

        _name.Text = name;
        _host.Text = s.Host;
        _port.Value = s.Port > 0 ? s.Port : ConnectionImport.DefaultPort(protocol);
        _user.Text = s.Username;
        _domain.Text = s.Domain;
        _domain.ItemsSource = domains.ToList();
        _password.Watermark = hasSavedPassword ? "Saved — leave empty to keep it" : "Asked when connecting if empty";
        _forget.IsVisible = hasSavedPassword;

        _profile.ItemsSource = new[] { protocol == Protocol.Vnc ? "None — use the password below" : "None — use the user name and password below" }.Concat(profiles.Select(p => $"{p.Name}  ({(p.Domain.Length > 0 ? p.Domain + "\\" : "")}{p.Username})")).ToList();
        int pi = s.CredentialId is Guid cid ? profiles.FindIndex(p => p.Id == cid) : -1;
        _profile.SelectedIndex = pi + 1;
        _profile.SelectionChanged += (_, _) => UpdateLogin();

        _jumpIds.Add(null);
        var jumpLabels = new List<string> { "None — connect directly" };
        foreach (var (id, label) in jumpHosts) { _jumpIds.Add(id); jumpLabels.Add(label); }
        _jump.ItemsSource = jumpLabels;
        _jump.SelectedIndex = Math.Max(0, _jumpIds.IndexOf(s.JumpHostId));

        _key.Text = s.PrivateKeyPath;
        _fontSize.Value = s.FontSize;
        _font.Text = s.FontFamily;
        _font.ItemsSource = MonospaceFonts();
        var schemes = TerminalScheme.Names.ToList();
        _scheme.ItemsSource = schemes;
        _scheme.SelectedIndex = Math.Max(0, schemes.IndexOf(s.ColorScheme));
        _tunnels.Text = string.Join("\n", s.Tunnels);

        _size.ItemsSource = Sizes.Select(x => x.Label).ToList();
        int si = s.Width <= 0 ? 0 : Array.FindIndex(Sizes, x => x.W == s.Width && x.H == s.Height);
        _size.SelectedIndex = si < 0 ? Sizes.Length - 1 : si;
        _width.Value = s.Width > 0 ? s.Width : 1920;
        _height.Value = s.Height > 0 ? s.Height : 1080;
        _size.SelectionChanged += (_, _) => UpdateSize();
        _depth.SelectedItem = s.ColorDepth is 32 or 24 or 16 or 15 ? s.ColorDepth : 32;
        _clipboard.IsChecked = s.RedirectClipboard;
        _drives.IsChecked = s.RedirectDrives;
        _printers.IsChecked = s.RedirectPrinters;
        _smart.IsChecked = s.SmartSizing;
        _vncFit.IsChecked = s.SmartSizing;
        _viewOnly.IsChecked = s.ViewOnly;
    }

    private static List<string> MonospaceFonts()
    {
        try
        {
            return Avalonia.Media.FontManager.Current.SystemFonts.Select(f => f.Name)
                .Where(n => n.Contains("Mono", StringComparison.OrdinalIgnoreCase) || n.Contains("Code", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("Consol", StringComparison.OrdinalIgnoreCase) || n.Contains("Courier", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("Hack", StringComparison.OrdinalIgnoreCase) || n.Contains("Terminus", StringComparison.OrdinalIgnoreCase))
                .Distinct().OrderBy(n => n).ToList();
        }
        catch { return []; }
    }

    private CredentialProfile? SelectedProfile => _profile.SelectedIndex > 0 ? _profiles[_profile.SelectedIndex - 1] : null;

    private void UpdateLogin()
    {
        var p = SelectedProfile;
        bool own = p == null;
        _user.IsEnabled = _domain.IsEnabled = _password.IsEnabled = _savePassword.IsEnabled = own;
        _forget.IsVisible = own && _hasSavedPassword;
        if (p != null)
        {
            _user.Text = p.Username;
            _domain.Text = p.Domain;
        }
    }

    private void UpdateSize()
    {
        bool custom = _size.SelectedIndex == Sizes.Length - 1;
        _width.IsVisible = _height.IsVisible = custom;
        _smart.IsEnabled = _size.SelectedIndex != 0;
    }

    private static Control Row(Grid grid, string label, Control field)
    {
        int r = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        if (label.Length > 0)
        {
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            Grid.SetRow(t, r);
            grid.Children.Add(t);
        }
        field.Margin = new Thickness(0, 4);
        Grid.SetRow(field, r);
        Grid.SetColumn(field, 1);
        grid.Children.Add(field);
        return field;
    }

    private static Grid Section() => new() { ColumnDefinitions = new ColumnDefinitions("140,*") };

    private Control Build()
    {
        var root = new StackPanel();

        root.Children.Add(new TextBlock { Text = "Connection", Classes = { "h" } });
        var conn = Section();
        Row(conn, "Name", _name);
        Row(conn, "Host", _host);
        Row(conn, "Port", _port);
        Row(conn, "Jump host", _jump);
        root.Children.Add(conn);

        root.Children.Add(new TextBlock { Text = "Login", Classes = { "h" } });
        var login = Section();
        Row(login, "Credentials", _profile);
        if (_protocol == Protocol.Rdp) Row(login, "Domain", _domain);
        if (_protocol != Protocol.Vnc) Row(login, "User name", _user); // VNC servers ask for a password only
        Row(login, "Password", _password);
        Row(login, "", new StackPanel { Children = { _savePassword, _forget } });
        root.Children.Add(login);

        if (_protocol == Protocol.Ssh)
        {
            root.Children.Add(new TextBlock { Text = "SSH", Classes = { "h" } });
            var ssh = Section();
            var browse = new Button { Content = "Browse…" };
            browse.Click += async (_, _) =>
            {
                if (_window?.StorageProvider is not { } sp) return;
                var start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
                var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Private key",
                    SuggestedStartLocation = Directory.Exists(start) ? await sp.TryGetFolderFromPathAsync(start) : null,
                });
                if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) _key.Text = path;
            };
            var keyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            keyRow.Children.Add(_key);
            Grid.SetColumn(browse, 1);
            browse.Margin = new Thickness(6, 0, 0, 0);
            keyRow.Children.Add(browse);
            Row(ssh, "Private key", keyRow);
            Row(ssh, "Font", _font);
            Row(ssh, "Font size", _fontSize);
            Row(ssh, "Colors", _scheme);
            Row(ssh, "Port forwards", _tunnels);
            ssh.Children.Add(new TextBlock
            {
                Text = "One per line: L local:host:port, R remote:host:port, D socks-port.",
                Classes = { "sub" }, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                [Grid.RowProperty] = ssh.RowDefinitions.Count, [Grid.ColumnProperty] = 1,
            });
            ssh.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            root.Children.Add(ssh);
        }
        else if (_protocol == Protocol.Vnc)
        {
            root.Children.Add(new TextBlock { Text = "VNC", Classes = { "h" } });
            var vnc = Section();
            Row(vnc, "", new StackPanel { Children = { _vncFit, _viewOnly, _clipboard } });
            root.Children.Add(vnc);
        }
        else
        {
            root.Children.Add(new TextBlock { Text = "Remote Desktop", Classes = { "h" } });
            var rdp = Section();
            Row(rdp, "Screen size", _size);
            Row(rdp, "", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _width, new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center }, _height } });
            Row(rdp, "Colors", _depth);
            Row(rdp, "", new StackPanel { Children = { _clipboard, _drives, _printers, _smart } });
            root.Children.Add(rdp);
        }

        UpdateLogin();
        UpdateSize();
        root.Margin = new Thickness(0, 0, 16, 0);
        return new ScrollViewer { Content = root, MaxHeight = 620, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    public async Task<bool> ShowFor(Window owner, string title) => await CreateWindow(owner, title).ShowFor(owner) == 0;

    /// <summary>The dialog window, not shown yet (also used by the screenshot tests).</summary>
    public Dialogs.DialogWindow CreateWindow(Window? owner, string title)
    {
        var d = new Dialogs.DialogWindow(title, Build(), width: 560);
        _window = d;
        d.AddButton("Save", isDefault: true, accent: true);
        d.AddButton("Cancel", isCancel: true);
        d.AcceptIndex = 0;
        d.Validate = Validate;
        d.Opened += (_, _) => (_host.Text?.Length > 0 ? _name : _host).Focus();
        return d;
    }

    private bool Validate()
    {
        var host = (_host.Text ?? "").Trim();
        if (host.Length == 0 || host.Contains(' '))
        {
            _host.Focus();
            _host.Watermark = "Enter a host name or IP address";
            return false;
        }
        var s = _original.Clone();
        s.Host = host;
        s.Port = (int)(_port.Value ?? ConnectionImport.DefaultPort(_protocol));
        var profile = SelectedProfile;
        s.CredentialId = profile?.Id;
        s.Username = _protocol == Protocol.Vnc ? "" : (profile?.Username ?? _user.Text ?? "").Trim();
        s.Domain = _protocol == Protocol.Rdp ? DomainName.Clean(profile?.Domain ?? _domain.Text) : "";
        s.JumpHostId = _jump.SelectedIndex > 0 ? _jumpIds[_jump.SelectedIndex] : null;

        if (_protocol == Protocol.Ssh)
        {
            s.PrivateKeyPath = (_key.Text ?? "").Trim();
            s.FontFamily = (_font.Text ?? "").Trim();
            s.FontSize = (int)(_fontSize.Value ?? 14);
            s.ColorScheme = _scheme.SelectedItem as string ?? TerminalScheme.Auto;
            var tunnels = (_tunnels.Text ?? "").Split('\n').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            foreach (var t in tunnels)
            {
                try { SshConnector.ParseTunnel(t); }
                catch (FormatException ex)
                {
                    _ = Dialogs.Info(_window, ex.Message, "Port forwards");
                    return false;
                }
            }
            s.Tunnels = tunnels;
        }
        else if (_protocol == Protocol.Vnc)
        {
            s.SmartSizing = _vncFit.IsChecked == true;
            s.ViewOnly = _viewOnly.IsChecked == true;
            s.RedirectClipboard = _clipboard.IsChecked == true;
        }
        else
        {
            var size = Sizes[Math.Max(_size.SelectedIndex, 0)];
            (s.Width, s.Height) = size.W < 0 ? ((int)(_width.Value ?? 1920), (int)(_height.Value ?? 1080)) : (size.W, size.H);
            s.ColorDepth = _depth.SelectedItem is int d ? d : 32;
            s.RedirectClipboard = _clipboard.IsChecked == true;
            s.RedirectDrives = _drives.IsChecked == true;
            s.RedirectPrinters = _printers.IsChecked == true;
            s.SmartSizing = _smart.IsChecked == true && s.Width > 0;
        }

        if (SessionName.Length == 0) _name.Text = host;
        Result = s;
        var typed = _password.Text ?? "";
        NewPassword = profile == null && typed.Length > 0 && _savePassword.IsChecked == true ? typed : null;
        ForgetPassword = profile == null && _forget.IsChecked == true && typed.Length == 0;
        TypedPassword = profile == null && typed.Length > 0 ? typed : null;
        return true;
    }

    /// <summary>A password typed but not to be saved (used for this connection only).</summary>
    public string? TypedPassword { get; private set; }
}
