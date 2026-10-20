using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SessionManager;

/// <summary>Small code-built dialogs: text prompt, password prompt, credentials prompt, change password.</summary>
public static class Dialogs
{
    public static string? PromptText(Window owner, string title, string label, string initial = "")
    {
        var box = new TextBox { Text = initial, MinWidth = 320 };
        var dlg = Build(owner, title, label, box, null, out _);
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }

    /// <summary>Asks for a password. When <paramref name="offerSave"/> is true, a "Save password" checkbox is shown.</summary>
    public static (string Password, bool Save)? PromptPassword(Window? owner, string title, string label, bool offerSave)
    {
        var box = new PasswordBox { MinWidth = 320 };
        var save = new CheckBox { Content = "Save password", Margin = new Thickness(0, 8, 0, 0), Visibility = offerSave ? Visibility.Visible : Visibility.Collapsed };
        var panel = new StackPanel();
        panel.Children.Add(box);
        panel.Children.Add(save);
        var dlg = Build(owner, title, label, panel, null, out _);
        box.Loaded += (_, _) => box.Focus();
        if (dlg.ShowDialog() != true) return null;
        return (box.Password, save.IsChecked == true);
    }

    /// <summary>
    /// The login was refused: use a saved credentials entry, or type the password. Without saved credentials it is just the password prompt.
    /// Returns the chosen profile (null = the typed password) or null if cancelled.
    /// </summary>
    public static (CredentialProfile? Profile, string? User, string Password, bool Save)? PromptRetry(Window? owner, string message, IList<CredentialProfile> profiles, bool offerSave, string? user = null)
    {
        if (profiles.Count == 0)
        {
            if (user != null)
            {
                var login = PromptLogin(owner, "Login failed", message, user, offerSave);
                return login == null ? null : (null, login.Value.User, login.Value.Password, login.Value.Save);
            }
            var only = PromptPassword(owner, "Password", message + "\n\nEnter the password to try again:", offerSave);
            return only == null ? null : (null, null, only.Value.Password, only.Value.Save);
        }

        var list = profiles.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var combo = new ComboBox { MinWidth = 320, Margin = new Thickness(24, 4, 0, 4) };
        foreach (var p in list)
        {
            var who = (p.Domain.Length > 0 ? p.Domain + "\\" : "") + p.Username;
            combo.Items.Add(p.Name.Length > 0 ? $"{p.Name}  ({who})" : who);
        }
        combo.SelectedIndex = 0;
        var useSaved = new RadioButton { Content = "Connect with saved credentials", IsChecked = true, GroupName = "retry" };
        var typed = new RadioButton { Content = "Enter the password", GroupName = "retry", Margin = new Thickness(0, 8, 0, 0) };
        var userBox = new TextBox { Text = user ?? "", MinWidth = 320, Margin = new Thickness(24, 4, 0, 0), IsEnabled = false,
            Visibility = user != null ? Visibility.Visible : Visibility.Collapsed };
        var box = new PasswordBox { MinWidth = 320, Margin = new Thickness(24, 4, 0, 4), IsEnabled = false };
        var save = new CheckBox { Content = "Save password", Margin = new Thickness(24, 4, 0, 0), IsEnabled = false,
            Visibility = offerSave ? Visibility.Visible : Visibility.Collapsed };
        typed.Checked += (_, _) => { userBox.IsEnabled = box.IsEnabled = save.IsEnabled = true; combo.IsEnabled = false; box.Focus(); };
        useSaved.Checked += (_, _) => { userBox.IsEnabled = box.IsEnabled = save.IsEnabled = false; combo.IsEnabled = true; };

        var panel = new StackPanel();
        panel.Children.Add(useSaved);
        panel.Children.Add(combo);
        panel.Children.Add(typed);
        panel.Children.Add(userBox);
        panel.Children.Add(box);
        panel.Children.Add(save);
        var dlg = Build(owner, "Login failed", message, panel, null, out _);
        if (dlg.ShowDialog() != true) return null;
        return typed.IsChecked == true ? (null, user != null ? userBox.Text.Trim() : null, box.Password, save.IsChecked == true) : (list[combo.SelectedIndex], null, "", false);
    }

    /// <summary>Asks for a new passphrase twice. Returns null if cancelled.</summary>
    public static string? PromptNewPassphrase(Window owner, string title, string label)
    {
        var a = new PasswordBox { MinWidth = 320 };
        var b = new PasswordBox { MinWidth = 320, Margin = new Thickness(0, 4, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(a);
        panel.Children.Add(new TextBlock { Text = "Confirm:", Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(b);
        var dlg = Build(owner, title, label, panel, d =>
        {
            if (a.Password.Length == 0) { MessageBox.Show(d, "Enter a password.", title); return false; }
            if (a.Password != b.Password) { MessageBox.Show(d, "The passwords don't match.", title); return false; }
            return true;
        }, out _);
        a.Loaded += (_, _) => a.Focus();
        return dlg.ShowDialog() == true ? a.Password : null;
    }

    /// <summary>"Connect as...": domain, username and password for one connection. Returns null if cancelled.</summary>
    public static (string Domain, string User, string Password)? PromptCredentials(Window owner, string title, string domain, string user)
    {
        var (typedDomain, plainUser) = DomainPicker.Split(user);
        var d = new DomainPicker { Domain = typedDomain.Length > 0 ? typedDomain : domain };
        var u = new TextBox { Text = plainUser, MinWidth = 180 };
        var p = new PasswordBox { MinWidth = 320 };
        var panel = new StackPanel();
        panel.Children.Add(UserRow(d, u));
        panel.Children.Add(new TextBlock { Text = "Password:", Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(p);
        var dlg = Build(owner, title, "Username:", panel, d =>
        {
            if (string.IsNullOrWhiteSpace(u.Text)) { MessageBox.Show(d, "Enter a username.", title); return false; }
            return true;
        }, out _);
        u.Loaded += (_, _) => { if (u.Text.Length == 0) u.Focus(); else p.Focus(); };
        if (dlg.ShowDialog() != true) return null;
        var (dd, uu) = DomainPicker.Split(u.Text.Trim());
        return (dd.Length > 0 ? dd : d.Domain, uu, p.Password);
    }

    /// <summary>Username and password for a web page; <paramref name="offerSave"/> shows "Save password". Returns null if cancelled.</summary>
    public static (string User, string Password, bool Save)? PromptLogin(Window? owner, string title, string label, string user, bool offerSave)
    {
        var u = new TextBox { Text = user, MinWidth = 320 };
        var p = new PasswordBox { MinWidth = 320 };
        var save = new CheckBox { Content = "Save in Windows Credential Manager", IsChecked = true, Margin = new Thickness(0, 8, 0, 0),
            Visibility = offerSave ? Visibility.Visible : Visibility.Collapsed };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Username:", Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(u);
        panel.Children.Add(new TextBlock { Text = "Password:", Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(p);
        panel.Children.Add(save);
        var dlg = Build(owner, title, label, panel, null, out _);
        u.Loaded += (_, _) => { if (u.Text.Length == 0) u.Focus(); else p.Focus(); };
        if (dlg.ShowDialog() != true) return null;
        return (u.Text.Trim(), p.Password, offerSave && save.IsChecked == true);
    }

    /// <summary>
    /// A web page's certificate isn't trusted by Windows (self-signed, wrong name, expired). Shows who it was issued to and
    /// its fingerprint; true = trust it. <paramref name="changedFrom"/> is the fingerprint trusted before, when it changed.
    /// </summary>
    public static bool TrustCertificate(Window? owner, string host, System.Security.Cryptography.X509Certificates.X509Certificate2 cert,
        string problem, string fingerprint, string? changedFrom)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(string label, string value, bool mono = false)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition());
            var l = new TextBlock { Text = label, Margin = new Thickness(0, 2, 12, 2) };
            l.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            var v = new TextBox
            {
                Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 2, 0, 2),
            };
            v.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            if (mono) { v.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"); v.FontSize = 11.5; }
            Grid.SetRow(l, r);
            Grid.SetRow(v, r);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
        }
        Row("Issued to:", cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false) is { Length: > 0 } n ? n : cert.Subject);
        Row("Issued by:", cert.Issuer == cert.Subject ? "itself (self-signed)" : cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, true));
        Row("Valid:", $"{cert.NotBefore:d} to {cert.NotAfter:d}" + (cert.NotAfter < DateTime.Now ? "  (expired)" : ""));
        Row("SHA-256:", CertificateFingerprint.Format(fingerprint), mono: true);

        var panel = new StackPanel();
        if (changedFrom != null)
        {
            var warn = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold,
                Text = "This certificate is different from the one you trusted before. That's expected after a firmware update or a " +
                       "certificate renewal, but it can also mean someone is intercepting the connection. Only continue if you know why it changed.",
            };
            warn.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            panel.Children.Add(warn);
        }
        panel.Children.Add(grid);
        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, MaxWidth = 460, FontSize = 11, Margin = new Thickness(0, 10, 0, 0),
            Text = "If you trust it, this exact certificate is remembered for this session. You'll be asked again if it ever changes.",
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        panel.Children.Add(note);

        var dlg = Build(owner, "Certificate not trusted", $"{host}: {problem}", panel, null, out var buttons);
        var ok = (Button)buttons.Children[0];
        ok.Content = "Trust this certificate";
        ok.IsDefault = changedFrom == null;
        ((Button)buttons.Children[1]).IsDefault = changedFrom != null;
        return dlg.ShowDialog() == true;
    }

    /// <summary>Lists the daily backups (newest first); returns the one to restore, or null.</summary>
    public static SessionBackup.Entry? PickBackup(Window owner, IReadOnlyList<SessionBackup.Entry> backups)
    {
        var list = new ListBox { Height = 260, MinWidth = 380 };
        foreach (var b in backups)
        {
            var when = b.Date == DateTime.Today ? "Today" : b.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : b.Date.ToString("dddd d MMMM yyyy");
            var what = b.Sessions < 0 ? "can't be read" : $"{b.Sessions} session{(b.Sessions == 1 ? "" : "s")}, {b.Folders} folder{(b.Folders == 1 ? "" : "s")}";
            list.Items.Add(new ListBoxItem { Content = $"{when}   —   {what}", Tag = b, IsEnabled = b.Sessions >= 0, Padding = new Thickness(4, 3, 4, 3) });
        }
        list.SelectedIndex = 0;
        var note = new TextBlock
        {
            Text = "A copy of your session list is kept each day you use the app (the last 30 days). Saved passwords are not part of it; " +
                   "they stay in Windows Credential Manager. Your current list is kept as a file before it's replaced.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 420, FontSize = 11, Margin = new Thickness(0, 8, 0, 0),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        var panel = new StackPanel();
        panel.Children.Add(list);
        panel.Children.Add(note);
        var dlg = Build(owner, "Restore a backup", "Choose the day to go back to:", panel, d => list.SelectedItem != null, out var buttons);
        ((Button)buttons.Children[0]).Content = "Restore";
        var open = new Button { Content = "Open backups folder", Margin = new Thickness(0, 0, 8, 0) };
        open.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SessionBackup.Folder) { UseShellExecute = true });
        buttons.Children.Insert(0, open);
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem != null) dlg.DialogResult = true; };
        return dlg.ShowDialog() == true ? (list.SelectedItem as ListBoxItem)?.Tag as SessionBackup.Entry : null;
    }

    /// <summary>Wake-on-LAN: the network card address (pre-filled when known) and whether to connect once it's awake.</summary>
    public static (string Mac, bool Connect)? WakeUp(Window owner, string name, string host, string mac, bool foundAutomatically)
    {
        var box = new TextBox { Text = mac, MinWidth = 320, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas") };
        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, MaxWidth = 400, FontSize = 11, Margin = new Thickness(0, 4, 0, 0),
            Text = foundAutomatically ? $"Found in this PC's network table for {host}. It's saved with the session."
                : mac.Length > 0 ? "Saved with the session."
                : "Network card address, e.g. AA:BB:CC:DD:EE:FF. On Windows, \"ipconfig /all\" shows it as Physical Address; it's saved with the session.",
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        var connect = new CheckBox { Content = "Connect as soon as it's awake (waits up to 3 minutes)", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(box);
        panel.Children.Add(hint);
        panel.Children.Add(connect);
        var dlg = Build(owner, "Wake up", $"Wake up {name} with a Wake-on-LAN packet. Address of its network card:", panel, d =>
        {
            if (WakeOnLan.TryParseMac(box.Text, out _)) return true;
            MessageBox.Show(d, "That isn't a network card address. It has 12 hexadecimal digits, e.g. AA:BB:CC:DD:EE:FF.", "Wake up");
            return false;
        }, out var buttons);
        ((Button)buttons.Children[0]).Content = "Wake up";
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dlg.ShowDialog() == true ? (box.Text.Trim(), connect.IsChecked == true) : null;
    }

    /// <summary>What to change in several sessions at once; null fields stay as they are.</summary>
    public sealed record BulkChange(bool SetProfile, CredentialProfile? Profile, string? Username, string? Password, int? Port, bool SetJump, Guid? Jump);

    public static BulkChange? EditMany(Window owner, int count, Protocol protocol, IReadOnlyList<CredentialProfile> profiles, IReadOnlyList<(Guid Id, string Label)> jumpHosts)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        CheckBox Row(string label, FrameworkElement field)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition());
            var check = new CheckBox { Content = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            field.Margin = new Thickness(0, 4, 0, 4);
            field.IsEnabled = false;
            check.Checked += (_, _) => { field.IsEnabled = true; if (field is Control c) c.Focus(); };
            check.Unchecked += (_, _) => field.IsEnabled = false;
            Grid.SetRow(check, r);
            Grid.SetRow(field, r);
            Grid.SetColumn(field, 1);
            grid.Children.Add(check);
            grid.Children.Add(field);
            return check;
        }

        var profileBox = new ComboBox();
        profileBox.Items.Add(new ComboBoxItem { Content = "None (each session's own login)" });
        foreach (var p in profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            profileBox.Items.Add(new ComboBoxItem { Content = $"{p.Name}  ({p.Username})", Tag = p });
        profileBox.SelectedIndex = 0;
        var userBox = new TextBox();
        var passwordBox = new PasswordBox();
        var portBox = new TextBox { Width = 80, HorizontalAlignment = HorizontalAlignment.Left,
            Text = protocol == Protocol.Web ? "443" : SessionSettings.DefaultPort(protocol).ToString() };
        var jumpBox = new ComboBox();
        jumpBox.Items.Add(new ComboBoxItem { Content = "None — connect directly" });
        foreach (var (id, label) in jumpHosts) jumpBox.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        jumpBox.SelectedIndex = 0;

        var useProfile = profiles.Count > 0 ? Row("Credential profile:", profileBox) : null;
        var useUser = protocol != Protocol.Vnc ? Row("Username:", userBox) : null;
        var usePassword = Row("Password:", passwordBox);
        var usePort = Row("Port:", portBox);
        var useJump = Row(protocol == Protocol.Ssh ? "Jump host:" : "SSH gateway:", jumpBox);

        var note = new TextBlock
        {
            Text = "Only the ticked settings are changed. Passwords are saved in Windows Credential Manager for each session.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 440, FontSize = 11, Margin = new Thickness(0, 8, 0, 0),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(note);

        int? port = null;
        var dlg = Build(owner, $"Edit {count} sessions", $"Change these settings in all {count} selected {protocol.DisplayName()} sessions:", panel, d =>
        {
            if (usePort.IsChecked == true)
            {
                if (!int.TryParse(portBox.Text.Trim(), out var p) || p is < 1 or > 65535)
                {
                    MessageBox.Show(d, "The port must be a number between 1 and 65535.", d.Title);
                    return false;
                }
                port = p;
            }
            if (new[] { useProfile, useUser, usePassword, usePort, useJump }.All(c => c?.IsChecked != true))
            {
                MessageBox.Show(d, "Tick the settings to change.", d.Title);
                return false;
            }
            return true;
        }, out var buttons);
        ((Button)buttons.Children[0]).Content = "Change";
        if (dlg.ShowDialog() != true) return null;
        return new BulkChange(
            useProfile?.IsChecked == true, (profileBox.SelectedItem as ComboBoxItem)?.Tag as CredentialProfile,
            useUser?.IsChecked == true ? userBox.Text.Trim() : null,
            usePassword.IsChecked == true ? passwordBox.Password : null,
            port,
            useJump.IsChecked == true, (jumpBox.SelectedItem as ComboBoxItem)?.Tag as Guid?);
    }

    /// <summary>Adds or edits a saved SSH command; null = cancelled.</summary>
    public static SavedCommand? EditCommand(Window owner, SavedCommand? existing)
    {
        var name = new TextBox { Text = existing?.Name ?? "", MinWidth = 380 };
        var text = new TextBox
        {
            Text = existing?.Text ?? "", AcceptsReturn = true, Height = 90, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
        };
        var run = new CheckBox { Content = "Run it (press Enter after the text)", IsChecked = existing?.Run ?? true, Margin = new Thickness(0, 8, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Name (shown in the menu):", Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "Command (several lines are sent one after another):", Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(text);
        panel.Children.Add(run);
        var dlg = Build(owner, existing == null ? "Save a command" : "Edit command", "Commands are sent from an SSH tab's menu (right-click the tab), " +
            "to all included tabs when multi-exec is on.", panel, d =>
        {
            if (text.Text.Trim().Length > 0) return true;
            MessageBox.Show(d, "Enter the command.", d.Title);
            return false;
        }, out _);
        (existing == null ? (Control)text : name).Loaded += (s, _) => ((Control)s!).Focus();
        if (dlg.ShowDialog() != true) return null;
        var result = existing ?? new SavedCommand();
        result.Name = name.Text.Trim();
        result.Text = text.Text.TrimEnd('\r', '\n');
        result.Run = run.IsChecked == true;
        return result;
    }

    /// <summary>Edit, reorder or remove saved commands. Returns true if anything changed.</summary>
    public static bool ManageCommands(Window owner, List<SavedCommand> commands)
    {
        bool changed = false;
        var list = new ListBox { Height = 220, MinWidth = 420 };
        void Fill(int select)
        {
            list.Items.Clear();
            foreach (var c in commands) list.Items.Add(new ListBoxItem { Content = (c.Name.Length > 0 ? c.Name + "   —   " : "") + c.Text.Replace("\n", " ⏎ "), Tag = c, Padding = new Thickness(4, 3, 4, 3) });
            list.SelectedIndex = Math.Min(select, commands.Count - 1);
        }
        Fill(0);
        Button Btn(string text, Action click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 0, 6), MinWidth = 90 };
            b.Click += (_, _) => click();
            return b;
        }
        SavedCommand? Selected() => (list.SelectedItem as ListBoxItem)?.Tag as SavedCommand;
        var side = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        side.Children.Add(Btn("Add…", () => { if (EditCommand(owner, null) is { } c) { commands.Add(c); changed = true; Fill(commands.Count - 1); } }));
        side.Children.Add(Btn("Edit…", () => { if (Selected() is { } c && EditCommand(owner, c) != null) { changed = true; Fill(commands.IndexOf(c)); } }));
        side.Children.Add(Btn("Move up", () => Move(-1)));
        side.Children.Add(Btn("Move down", () => Move(1)));
        side.Children.Add(Btn("Remove", () => { if (Selected() is { } c) { int i = commands.IndexOf(c); commands.Remove(c); changed = true; Fill(i); } }));
        void Move(int step)
        {
            if (Selected() is not { } c) return;
            int i = commands.IndexOf(c), j = i + step;
            if (j < 0 || j >= commands.Count) return;
            commands.RemoveAt(i);
            commands.Insert(j, c);
            changed = true;
            Fill(j);
        }
        list.MouseDoubleClick += (_, _) => { if (Selected() is { } c && EditCommand(owner, c) != null) { changed = true; Fill(commands.IndexOf(c)); } };
        var row = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        row.Children.Add(side);
        row.Children.Add(list);
        var dlg = Build(owner, "Saved commands", "Commands you can send from an SSH tab's menu:", row, null, out var buttons);
        buttons.Children.RemoveAt(1); // only "Close"
        ((Button)buttons.Children[0]).Content = "Close";
        dlg.ShowDialog();
        return changed;
    }

    /// <summary>[domain ▾] \ [username] on one line.</summary>
    public static Grid UserRow(DomainPicker domain, TextBox user)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star), MinWidth = 120 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7, GridUnitType.Star) });
        var slash = new TextBlock { Text = "\\", FontSize = 14, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        slash.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        Grid.SetColumn(slash, 1);
        Grid.SetColumn(user, 2);
        row.Children.Add(domain);
        row.Children.Add(slash);
        row.Children.Add(user);
        return row;
    }

    public enum PasswordChange { Cancel, Set, Remove }

    /// <summary>Right-click > Change password. Returns the action and the new password (for Set).</summary>
    public static (PasswordChange Action, string Password) ChangePassword(Window owner, string sessionName, bool hasSaved)
    {
        var a = new PasswordBox { MinWidth = 320 };
        var b = new PasswordBox { MinWidth = 320, Margin = new Thickness(0, 4, 0, 0) };
        var show = new CheckBox { Content = "Show password", Margin = new Thickness(0, 8, 0, 0) };
        var plain = new TextBox { MinWidth = 320, Visibility = Visibility.Collapsed };
        show.Checked += (_, _) => { plain.Text = a.Password; plain.Visibility = Visibility.Visible; a.Visibility = Visibility.Collapsed; };
        show.Unchecked += (_, _) => { a.Password = plain.Text; plain.Visibility = Visibility.Collapsed; a.Visibility = Visibility.Visible; };

        var panel = new StackPanel();
        panel.Children.Add(a);
        panel.Children.Add(plain);
        panel.Children.Add(new TextBlock { Text = "Confirm:", Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(b);
        panel.Children.Add(show);
        if (hasSaved)
            panel.Children.Add(new TextBlock { Text = "A password is currently saved for this session.", Margin = new Thickness(0, 8, 0, 0), Foreground = Theme.Get("SubTextBrush") });

        var result = PasswordChange.Cancel;
        var dlg = Build(owner, "Change password", $"New password for \"{sessionName}\":", panel, d =>
        {
            var pw = show.IsChecked == true ? plain.Text : a.Password;
            if (pw != b.Password) { MessageBox.Show(d, "The passwords don't match.", "Change password"); return false; }
            result = PasswordChange.Set;
            return true;
        }, out var buttons);
        if (hasSaved)
        {
            var remove = new Button { Content = "Remove saved password", Margin = new Thickness(0, 0, 8, 0) };
            remove.Click += (_, _) => { result = PasswordChange.Remove; dlg.DialogResult = true; };
            buttons.Children.Insert(0, remove);
        }
        a.Loaded += (_, _) => a.Focus();
        if (dlg.ShowDialog() != true) return (PasswordChange.Cancel, "");
        return (result, result == PasswordChange.Set ? (show.IsChecked == true ? plain.Text : a.Password) : "");
    }

    /// <summary>Add/edit a credential profile. Password: null = keep the saved one.</summary>
    public static (string Name, string User, string Domain, string? Password)? EditCredential(
        Window owner, string title, CredentialProfile? existing, bool hasPassword)
    {
        var name = new TextBox { Text = existing?.Name ?? "", MinWidth = 300 };
        var user = new TextBox { Text = existing?.Username ?? "" };
        var domain = new DomainPicker { Domain = existing?.Domain ?? "" };
        domain.Box.ToolTip = "Optional Windows domain; used by Remote Desktop sessions";
        var pw = new PasswordBox();
        var hint = new TextBlock
        {
            Text = hasPassword ? "A password is saved — leave empty to keep it." : "No password saved yet.",
            Foreground = Theme.Get("SubTextBrush"), FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void AddRow(string label, UIElement field)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition());
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetRow(l, r);
            Grid.SetRow(field, r);
            Grid.SetColumn(field, 1);
            if (field is Control c) { c.Margin = new Thickness(0, 4, 0, 4); }
            else if (field is Panel) ((FrameworkElement)field).Margin = new Thickness(0, 4, 0, 4);
            grid.Children.Add(l);
            grid.Children.Add(field);
        }
        AddRow("Name:", name);
        AddRow("Username:", UserRow(domain, user));
        AddRow("Password:", pw);
        AddRow("", hint);

        var dlg = Build(owner, title, "Sessions that use this profile log in with these credentials.", grid, d =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { MessageBox.Show(d, "Enter a name for the profile.", title); return false; }
            if (string.IsNullOrWhiteSpace(user.Text)) { MessageBox.Show(d, "Enter a username.", title); return false; }
            return true;
        }, out _);
        name.Loaded += (_, _) => (name.Text.Length == 0 ? name : (Control)pw).Focus();
        if (dlg.ShowDialog() != true) return null;
        var (typed, plain) = DomainPicker.Split(user.Text.Trim());
        var dom = typed.Length > 0 ? typed : domain.Domain;
        if (DomainPicker.Remember(dom)) DomainPicker.SavedChanged?.Invoke();
        return (name.Text.Trim(), plain, dom, pw.Password.Length > 0 ? pw.Password : null);
    }

    /// <summary>Importing a session list: add to the current list (false) or replace it (true); null = cancelled.</summary>
    public static bool? ChooseImportMode(Window owner, string description)
    {
        var add = new RadioButton { Content = "Add them to my sessions (ones I already have are skipped)", IsChecked = true, Margin = new Thickness(0, 6, 0, 6) };
        var replace = new RadioButton { Content = "Replace all my sessions with this list", Margin = new Thickness(0, 0, 0, 2) };
        var note = new TextBlock
        {
            Text = "Replacing removes your current sessions and folders, and their saved passwords.",
            Foreground = Theme.Get("SubTextBrush"), FontSize = 11, Margin = new Thickness(23, 0, 0, 0),
        };
        var panel = new StackPanel();
        panel.Children.Add(add);
        panel.Children.Add(replace);
        panel.Children.Add(note);
        var dlg = Build(owner, "Import session list", description, panel, d =>
            replace.IsChecked != true || MessageBox.Show(d, "Remove all your current sessions and replace them with the imported list?",
                "Replace sessions", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes, out _);
        if (dlg.ShowDialog() != true) return null;
        return replace.IsChecked == true;
    }

    private static Window Build(Window? owner, string title, string label, UIElement content, Func<Window, bool>? validate, out StackPanel buttons)
    {
        var okButton = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        root.Children.Add(content);
        root.Children.Add(buttons);

        var dlg = new Window
        {
            Title = title,
            Content = root,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            Owner = owner,
        };
        Theme.ApplyTo(dlg);
        okButton.Click += (_, _) => { if (validate == null || validate(dlg)) dlg.DialogResult = true; };
        return dlg;
    }
}
