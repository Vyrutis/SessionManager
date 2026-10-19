using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SessionManager;

/// <summary>
/// Shows what an import or scan found as editable rows: tick what to add, fix misread values, add passwords,
/// and choose a folder, skipping of duplicates and shared logins.
/// </summary>
public sealed class ImportReviewWindow
{
    private sealed class Row
    {
        public required CheckBox Check;
        public required ComboBox Proto;
        public required TextBox Name, Host, Port, Domain, User, Password;
        public required ImportedConnection Source;
    }

    private const string Columns = "28,78,1.2*,1.3*,64,0.8*,*,*";

    private readonly string _source;
    private readonly IReadOnlyList<ImportedConnection> _items;
    private readonly string? _recognizedText;
    private readonly List<Row> _rows = new();
    private readonly StackPanel _rowPanel = new() { Spacing = 2 };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _useFolder = new() { Content = "Put them in a new folder:", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _folderName = new() { Width = 180, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _skipExisting = new() { Content = "Skip connections I already have", IsChecked = true };
    private readonly CheckBox _shareLogins = new() { Content = "Save identical logins as one shared profile", IsChecked = true };
    private readonly CheckBox _showPasswords = new() { Content = "Show passwords" };
    private Window? _window;

    public List<ImportedConnection> Selected { get; } = new();
    public string? FolderName => _useFolder.IsChecked == true && (_folderName.Text ?? "").Trim().Length > 0 ? _folderName.Text!.Trim() : null;
    public bool SkipExisting => _skipExisting.IsChecked == true;
    public bool ShareLogins => _shareLogins.IsChecked == true;

    public ImportReviewWindow(string source, IReadOnlyList<ImportedConnection> items, string? recognizedText = null)
    {
        _source = source;
        _items = items;
        _recognizedText = recognizedText;
        _folderName.Text = source;
        _useFolder.IsChecked = recognizedText != null;
        // A scan's passwords are what the user needs to check most.
        _showPasswords.IsChecked = recognizedText != null && items.Any(i => i.Password != null);
        _showPasswords.IsCheckedChanged += (_, _) => { foreach (var r in _rows) r.Password.RevealPassword = _showPasswords.IsChecked == true; };
    }

    public async Task<bool> ShowFor(Window owner) => await CreateWindow().ShowFor(owner) == 0;

    /// <summary>The dialog window, not shown yet (also used by the screenshot tests).</summary>
    public Dialogs.DialogWindow CreateWindow()
    {
        var root = new DockPanel();

        var intro = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        intro.Text = _items.Count == 0
            ? "No connections were found. You can add them by hand with \"Add row\"."
            : _recognizedText != null
                ? "Text recognition can misread characters, so check each address, user name and password. Untick what you don't want; passwords go to the desktop keyring."
                : "Untick what you don't want, fix anything that's wrong, and add passwords if you like (they go to the desktop keyring).";
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);

        // Options and the recognized text at the bottom.
        var bottom = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
        var all = new Button { Content = "All", Classes = { "tool" } };
        all.Click += (_, _) => { foreach (var r in _rows) r.Check.IsChecked = true; };
        var none = new Button { Content = "None", Classes = { "tool" } };
        none.Click += (_, _) => { foreach (var r in _rows) r.Check.IsChecked = false; };
        var addRow = new Button { Content = "+ Add row", Classes = { "tool" } };
        addRow.Click += (_, _) => AddRow(new ImportedConnection { Protocol = Protocol.Ssh, Port = 22 }).Host.Focus();
        bottom.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { all, none, addRow, _count } });
        bottom.Children.Add(new WrapPanel
        {
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 18, 0), Children = { _useFolder, _folderName } },
                Spaced(_skipExisting), Spaced(_shareLogins), Spaced(_showPasswords),
            },
        });
        if (!string.IsNullOrWhiteSpace(_recognizedText))
        {
            bottom.Children.Add(new Expander
            {
                Header = "Recognized text",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new TextBox
                {
                    Text = _recognizedText, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 120,
                    FontFamily = new FontFamily("DejaVu Sans Mono, Consolas, monospace"), FontSize = 12,
                },
            });
        }
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        // Header + rows
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Margin = new Thickness(0, 0, 14, 4) };
        string[] titles = ["", "Type", "Name", "Host", "Port", "Domain", "User", "Password"];
        for (int i = 0; i < titles.Length; i++)
            header.Children.Add(new TextBlock { Text = titles[i], FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new Thickness(3, 0), [Grid.ColumnProperty] = i });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = _rowPanel, Padding = new Thickness(0, 0, 14, 0) });

        foreach (var c in _items) AddRow(c);
        UpdateCount();

        var d = new Dialogs.DialogWindow($"Import connections — {_source}", root, width: 1000)
        {
            SizeToContent = SizeToContent.Manual,
            Height = 600,
            CanResize = true,
            MinWidth = 700,
            MinHeight = 360,
        };
        _window = d;
        d.AddButton("Add connections", isDefault: true, accent: true);
        d.AddButton("Cancel", isCancel: true);
        d.AcceptIndex = 0;
        d.Validate = Accept;
        return d;
    }

    private static Control Spaced(Control c)
    {
        c.Margin = new Thickness(0, 0, 18, 0);
        return c;
    }

    private Row AddRow(ImportedConnection c)
    {
        var m = new Thickness(2, 1);
        var check = new CheckBox { IsChecked = true, Margin = m };
        var proto = new ComboBox { Margin = m, ItemsSource = new[] { "SSH", "RDP", "VNC" }, SelectedIndex = (int)c.Protocol, HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Text = c.Name, Margin = m };
        var host = new TextBox { Text = c.Host, Margin = m };
        var port = new TextBox { Text = c.Port > 0 ? c.Port.ToString() : "", Margin = m };
        var domain = new TextBox { Text = c.Domain, Margin = m };
        var user = new TextBox { Text = c.Username, Margin = m };
        var pass = new TextBox { Text = c.Password ?? "", PasswordChar = '•', RevealPassword = _showPasswords.IsChecked == true, Margin = m };
        if (c.SourcePath.Length > 0) ToolTip.SetTip(name, c.SourcePath);
        else if (c.Folder.Length > 0) ToolTip.SetTip(name, "Folder: " + c.Folder);

        var row = new Row { Check = check, Proto = proto, Name = name, Host = host, Port = port, Domain = domain, User = user, Password = pass, Source = c };
        void UpdateFields()
        {
            var p = (Protocol)Math.Max(0, proto.SelectedIndex);
            domain.IsEnabled = p == Protocol.Rdp;
            user.IsEnabled = p != Protocol.Vnc; // VNC servers ask for a password only
        }
        proto.SelectionChanged += (_, _) =>
        {
            UpdateFields();
            // Swap the port along with the protocol, unless it was set to something custom.
            if (port.Text is "22" or "3389" or "5900" or "" or null) port.Text = ConnectionImport.DefaultPort((Protocol)Math.Max(0, proto.SelectedIndex)).ToString();
        };
        UpdateFields();
        check.IsCheckedChanged += (_, _) => UpdateCount();
        string? autoName = c.Name == c.Host ? c.Host : null;
        host.TextChanged += (_, _) =>
        {
            if (string.IsNullOrEmpty(name.Text) || name.Text == autoName) name.Text = autoName = host.Text?.Trim();
        };

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns) };
        Control[] cells = [check, proto, name, host, port, domain, user, pass];
        for (int i = 0; i < cells.Length; i++) { Grid.SetColumn(cells[i], i); g.Children.Add(cells[i]); }
        _rowPanel.Children.Add(g);
        _rows.Add(row);
        UpdateCount();
        return row;
    }

    private void UpdateCount()
    {
        int n = _rows.Count(r => r.Check.IsChecked == true);
        _count.Text = $"   {n} of {_rows.Count} selected";
    }

    private bool Accept()
    {
        Selected.Clear();
        foreach (var r in _rows.Where(r => r.Check.IsChecked == true))
        {
            var protocol = (Protocol)Math.Max(0, r.Proto.SelectedIndex);
            var parsed = ConnectionImport.ParseAddress(r.Host.Text ?? "");
            if (parsed == null)
            {
                _ = Dialogs.Info(_window, $"\"{r.Host.Text}\" is not a valid host name or IP address.", "Import");
                r.Host.Focus();
                r.Host.SelectAll();
                return false;
            }
            int port;
            var portText = (r.Port.Text ?? "").Trim();
            if (portText.Length == 0) port = parsed.Value.Port ?? ConnectionImport.DefaultPort(protocol);
            else if (!int.TryParse(portText, out port) || port is < 1 or > 65535)
            {
                _ = Dialogs.Info(_window, $"\"{portText}\" is not a valid port (1-65535).", "Import");
                r.Port.Focus();
                r.Port.SelectAll();
                return false;
            }
            var user = protocol == Protocol.Vnc ? "" : (r.User.Text ?? "").Trim();
            if (user.Length == 0 && protocol != Protocol.Vnc) user = parsed.Value.User;
            var domain = protocol == Protocol.Rdp ? DomainName.Clean(r.Domain.Text) : "";
            if (protocol == Protocol.Rdp && domain.Length == 0 && user.Contains('\\')) (domain, user) = DomainName.Split(user);
            var password = r.Password.Text ?? "";
            Selected.Add(new ImportedConnection
            {
                Protocol = protocol,
                Name = (r.Name.Text ?? "").Trim() is { Length: > 0 } n ? n : parsed.Value.Host,
                Host = parsed.Value.Host, Port = port, Username = user, Domain = domain,
                Password = password.Length > 0 ? password : null,
                Folder = r.Source.Folder, PrivateKeyPath = protocol == Protocol.Ssh ? r.Source.PrivateKeyPath : "",
                Template = r.Source.Protocol == protocol ? r.Source.Template : null,
            });
        }
        return true;
    }
}
