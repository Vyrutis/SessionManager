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
        var u = new TextBox { Text = plainUser, MinWidth = 180, Padding = new Thickness(3, 2, 3, 2) };
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
            var remove = new Button { Content = "Remove saved password", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
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
        var user = new TextBox { Text = existing?.Username ?? "", Padding = new Thickness(3, 2, 3, 2) };
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
            if (field is Control c) { c.Margin = new Thickness(0, 3, 0, 3); c.Padding = new Thickness(3, 2, 3, 2); }
            else if (field is Panel) ((FrameworkElement)field).Margin = new Thickness(0, 3, 0, 3);
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
        var okButton = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 3, 10, 3) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
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
