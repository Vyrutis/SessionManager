using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SessionManager;

/// <summary>
/// Shows what an importer or the screenshot scan found, one editable row per connection,
/// so the user can untick, fix misread values and add passwords before anything is added.
/// </summary>
public sealed class ImportReviewWindow : Window
{
    private static readonly GridLength[] Columns =
    [
        new(30), new(84), new(1.1, GridUnitType.Star), new(1.3, GridUnitType.Star), new(62),
        new(0.8, GridUnitType.Star), new(1, GridUnitType.Star), new(1, GridUnitType.Star),
    ];

    private sealed class Row
    {
        public required CheckBox Check;
        public required ComboBox Proto;
        public required TextBox Name, Host, Port, Domain, User;
        public required PasswordBox Password;
        public required TextBox PasswordText;
        public required ImportedConnection Source;
    }

    private readonly List<Row> _rows = new();
    private readonly StackPanel _rowPanel = new();
    private readonly CheckBox _useFolder = new() { Content = "Put them in a new folder:", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _folderName = new() { Width = 200, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _skipExisting = new() { Content = "Skip connections I already have", IsChecked = true, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _ok = new() { Content = "Import", IsDefault = true, MinWidth = 90, Padding = new Thickness(10, 3, 10, 3) };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _showPasswords = new() { Content = "Show passwords", Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _shareLogins = new()
    {
        Content = "Share identical logins", Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "Connections with the same user name and password use one credential profile,\nso changing that password later updates all of them.",
    };

    public List<ImportedConnection> Selected { get; } = new();
    public string? FolderName => _useFolder.IsChecked == true && _folderName.Text.Trim().Length > 0 ? _folderName.Text.Trim() : null;
    public bool SkipExisting => _skipExisting.IsChecked == true;
    public bool ShareLogins => _shareLogins.IsChecked == true;

    public ImportReviewWindow(Window owner, string source, IReadOnlyList<ImportedConnection> items, string? recognizedText = null)
    {
        Owner = owner;
        Title = $"Import connections - {source}";
        Width = 940;
        Height = 580;
        MinWidth = 640;
        MinHeight = 340;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(16) };

        // Intro
        var intro = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        intro.Text = items.Count == 0
            ? "No connections were found. You can add them by hand with \"Add row\"."
            : recognizedText != null
                ? "Text recognition can misread characters, so check each address and user name. Untick what you don't want and add passwords if you like (they go to Windows Credential Manager)."
                : "Untick what you don't want, fix anything that's wrong, and add passwords if you like (they go to Windows Credential Manager).";
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);

        // Options
        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        _folderName.Text = source;
        _useFolder.IsChecked = items.Count > 1;
        _folderName.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(CheckBox.IsChecked)) { Source = _useFolder });
        options.Children.Add(_useFolder);
        options.Children.Add(_folderName);
        options.Children.Add(_skipExisting);
        options.Children.Add(_shareLogins);
        options.Children.Add(_showPasswords);
        _showPasswords.Checked += (_, _) => ShowPasswords(true);
        _showPasswords.Unchecked += (_, _) => ShowPasswords(false);
        // Read from a picture: the user should see what the OCR made of each password.
        _showPasswords.IsChecked = recognizedText != null && items.Any(i => i.Password != null);
        _shareLogins.IsChecked = items.Where(i => i.Password != null).GroupBy(i => (i.Username, i.Password)).Any(g => g.Count() > 1);
        _shareLogins.Visibility = items.Any(i => i.Password != null) || recognizedText != null ? Visibility.Visible : Visibility.Collapsed;
        DockPanel.SetDock(options, Dock.Top);
        root.Children.Add(options);

        // Buttons
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(_ok, Dock.Right);
        bottom.Children.Add(cancel);
        bottom.Children.Add(_ok);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var all = new Button { Content = "All", Padding = new Thickness(10, 3, 10, 3) };
        var none = new Button { Content = "None", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        var add = new Button { Content = "Add row", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 12, 0) };
        all.Click += (_, _) => { foreach (var r in _rows) r.Check.IsChecked = true; };
        none.Click += (_, _) => { foreach (var r in _rows) r.Check.IsChecked = false; };
        add.Click += (_, _) =>
        {
            var row = AddRow(new ImportedConnection { Port = 22 });
            row.Host.Focus();
            row.Host.BringIntoView();
        };
        left.Children.Add(all);
        left.Children.Add(none);
        left.Children.Add(add);
        left.Children.Add(_count);
        bottom.Children.Add(left);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        // Recognized text (screenshot scan), to copy anything the scan missed
        if (recognizedText != null)
        {
            var textBox = new TextBox
            {
                Text = recognizedText.Length > 0 ? recognizedText : "(no text recognized)",
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 110,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            };
            var expander = new Expander { Header = "Recognized text", Content = textBox, Margin = new Thickness(0, 10, 0, 0) };
            expander.SetResourceReference(ForegroundProperty, "TextBrush");
            DockPanel.SetDock(expander, Dock.Bottom);
            root.Children.Add(expander);
        }

        // Table
        var table = new DockPanel();
        var header = MakeGrid();
        string[] titles = ["", "Type", "Name", "Host", "Port", "Domain", "User name", "Password"];
        for (int i = 0; i < titles.Length; i++)
        {
            var t = new TextBlock { Text = titles[i], FontWeight = FontWeights.SemiBold, Margin = new Thickness(3, 0, 3, 4) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            Grid.SetColumn(t, i);
            header.Children.Add(t);
        }
        DockPanel.SetDock(header, Dock.Top);
        table.Children.Add(header);
        var scroller = new ScrollViewer { Content = _rowPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        // Keep the header columns over the rows when the scroll bar appears.
        scroller.ScrollChanged += (_, _) => header.Margin = new Thickness(0, 0, scroller.ActualWidth - scroller.ViewportWidth, 0);
        table.Children.Add(scroller);
        var tableBorder = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6), Child = table };
        tableBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        tableBorder.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        root.Children.Add(tableBorder);

        foreach (var item in items) AddRow(item);
        UpdateCount();

        Content = root;
        Theme.ApplyTo(this);
        _ok.Click += (_, _) => Accept();
    }

    private static Grid MakeGrid()
    {
        var g = new Grid();
        foreach (var w in Columns) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        return g;
    }

    private Row AddRow(ImportedConnection c)
    {
        var g = MakeGrid();
        g.Margin = new Thickness(0, 0, 0, 4);
        var m = new Thickness(3, 0, 3, 0);
        var check = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var proto = new ComboBox { Margin = m, ItemsSource = new[] { "SSH", "RDP" }, SelectedIndex = c.Protocol == Protocol.Rdp ? 1 : 0 };
        var name = new TextBox { Text = c.Name, Margin = m, VerticalContentAlignment = VerticalAlignment.Center };
        var host = new TextBox { Text = c.Host, Margin = m, VerticalContentAlignment = VerticalAlignment.Center };
        var port = new TextBox { Text = c.Port > 0 ? c.Port.ToString() : "", Margin = m, VerticalContentAlignment = VerticalAlignment.Center };
        var domain = new TextBox { Text = c.Domain, Margin = m, VerticalContentAlignment = VerticalAlignment.Center };
        var user = new TextBox { Text = c.Username, Margin = m, VerticalContentAlignment = VerticalAlignment.Center };
        var pass = new PasswordBox { Margin = m, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Optional. Saved to Windows Credential Manager." };
        var passText = new TextBox { Margin = m, VerticalContentAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, FontFamily = new FontFamily("Consolas") };
        if (c.Password != null) pass.Password = passText.Text = c.Password;
        bool syncing = false;
        pass.PasswordChanged += (_, _) => { if (syncing) return; syncing = true; passText.Text = pass.Password; syncing = false; };
        passText.TextChanged += (_, _) => { if (syncing) return; syncing = true; pass.Password = passText.Text; syncing = false; };
        if (_showPasswords.IsChecked == true) { pass.Visibility = Visibility.Collapsed; passText.Visibility = Visibility.Visible; }
        var passCell = new Grid();
        passCell.Children.Add(pass);
        passCell.Children.Add(passText);
        if (c.Folder.Length > 0) name.ToolTip = "Folder: " + c.Folder;
        if (c.SourcePath.Length > 0) name.ToolTip = c.SourcePath;

        var row = new Row { Check = check, Proto = proto, Name = name, Host = host, Port = port, Domain = domain, User = user, Password = pass, PasswordText = passText, Source = c };
        void ProtoChanged()
        {
            bool rdp = proto.SelectedIndex == 1;
            domain.IsEnabled = rdp;
            // Swap the port along with the protocol, unless it was set to something custom.
            if (rdp && (port.Text == "22" || port.Text == "")) port.Text = "3389";
            else if (!rdp && (port.Text == "3389" || port.Text == "")) port.Text = "22";
        }
        proto.SelectionChanged += (_, _) => ProtoChanged();
        domain.IsEnabled = proto.SelectedIndex == 1;
        check.Checked += (_, _) => UpdateCount();
        check.Unchecked += (_, _) => UpdateCount();
        host.TextChanged += (_, _) => { if (name.Text.Length == 0 || name.Text == (string?)name.Tag) name.Text = (string)(name.Tag = host.Text.Trim()); };
        name.Tag = c.Name == c.Host ? c.Host : null;

        UIElement[] cells = [check, proto, name, host, port, domain, user, passCell];
        for (int i = 0; i < cells.Length; i++) { Grid.SetColumn(cells[i], i); g.Children.Add(cells[i]); }
        _rowPanel.Children.Add(g);
        _rows.Add(row);
        UpdateCount();
        return row;
    }

    private void ShowPasswords(bool show)
    {
        foreach (var r in _rows)
        {
            r.Password.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            r.PasswordText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateCount()
    {
        int n = _rows.Count(r => r.Check.IsChecked == true);
        _count.Text = $"{n} of {_rows.Count} selected";
        _count.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        _ok.IsEnabled = n > 0;
    }

    private void Accept()
    {
        Selected.Clear();
        foreach (var r in _rows.Where(r => r.Check.IsChecked == true))
        {
            var protocol = r.Proto.SelectedIndex == 1 ? Protocol.Rdp : Protocol.Ssh;
            var parsed = ConnectionImport.ParseAddress(r.Host.Text);
            if (parsed == null)
            {
                MessageBox.Show(this, $"\"{r.Host.Text}\" is not a valid host name or IP address.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                r.Host.Focus();
                r.Host.SelectAll();
                return;
            }
            int port;
            if (r.Port.Text.Trim().Length == 0) port = parsed.Value.Port ?? ConnectionImport.DefaultPort(protocol);
            else if (!int.TryParse(r.Port.Text.Trim(), out port) || port is < 1 or > 65535)
            {
                MessageBox.Show(this, $"\"{r.Port.Text}\" is not a valid port (1-65535).", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                r.Port.Focus();
                r.Port.SelectAll();
                return;
            }
            var user = r.User.Text.Trim();
            if (user.Length == 0) user = parsed.Value.User;
            var domain = protocol == Protocol.Rdp ? DomainPicker.Clean(r.Domain.Text) : "";
            if (protocol == Protocol.Rdp && domain.Length == 0 && user.Contains('\\')) (domain, user) = DomainPicker.Split(user);
            Selected.Add(new ImportedConnection
            {
                Protocol = protocol,
                Name = r.Name.Text.Trim() is { Length: > 0 } n ? n : parsed.Value.Host,
                Host = parsed.Value.Host, Port = port, Username = user, Domain = domain,
                Password = r.Password.Password.Length > 0 ? r.Password.Password : null,
                Folder = r.Source.Folder, PrivateKeyPath = protocol == Protocol.Ssh ? r.Source.PrivateKeyPath : "",
                Template = r.Source.Protocol == protocol ? r.Source.Template : null,
            });
        }
        DialogResult = true;
    }
}
