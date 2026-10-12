using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SessionManager;

/// <summary>Manages shared credential profiles (one username/password used by many sessions).</summary>
public sealed class CredentialsWindow : Window
{
    private readonly SessionStore _store;
    private readonly Action _changed;
    private readonly ListBox _list = new();
    private readonly TextBlock _empty = new()
    {
        Text = "No profiles yet. Click Add… to create one.",
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
    };

    private sealed record Row(CredentialProfile Profile, string Title, string Detail);

    public CredentialsWindow(Window owner, SessionStore store, Action changed)
    {
        _store = store;
        _changed = changed;
        Owner = owner;
        Title = "Credential profiles";
        Width = 520;
        Height = 420;
        MinWidth = 400;
        MinHeight = 280;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme.ApplyTo(this);

        var root = new DockPanel { Margin = new Thickness(14) };

        var intro = new TextBlock
        {
            Text = "A profile holds a username and password that several sessions share. Change the password here once and every session using it picks it up. Choose a profile in a session's Edit dialog.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(MakeButton("Add…", Add));
        buttons.Children.Add(MakeButton("Edit…", Edit));
        buttons.Children.Add(MakeButton("Delete", Delete));
        var close = MakeButton("Close", Close);
        close.IsCancel = true;
        close.Margin = new Thickness(18, 0, 0, 0);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = _list, Padding = new Thickness(0, 3, 0, 3) };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;
        _list.ItemContainerStyle = (Style)Application.Current.FindResource("ListRow");
        _list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <StackPanel Orientation="Horizontal" Margin="0,3">
                    <TextBlock Text="&#xE8D7;" FontFamily="{DynamicResource Icons}" FontSize="15" Foreground="{DynamicResource FolderBrush}"
                               VerticalAlignment="Center" Margin="2,0,10,0" />
                    <StackPanel>
                        <TextBlock Text="{Binding Title}" FontWeight="SemiBold" />
                        <TextBlock Text="{Binding Detail}" FontSize="11.5" Foreground="{DynamicResource SubTextBrush}" />
                    </StackPanel>
                </StackPanel>
            </DataTemplate>
            """);
        _list.MouseDoubleClick += (_, _) => Edit();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Delete) Delete(); else if (e.Key == Key.Enter) Edit(); };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        root.Children.Add(new Grid { Children = { border, _empty } });

        Content = root;
        Refresh();
    }

    private static Button MakeButton(string text, Action action)
    {
        var b = new Button { Content = text, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        b.Click += (_, _) => action();
        return b;
    }

    private IEnumerable<Node> SessionsUsing(Guid id) =>
        _store.Data.Roots.SelectMany(r => r.Descendants()).Where(n => n.IsSession && n.Settings?.CredentialId == id);

    private void Refresh(CredentialProfile? select = null)
    {
        var rows = _store.Data.Credentials
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c =>
            {
                int used = SessionsUsing(c.Id).Count();
                var user = string.IsNullOrEmpty(c.Domain) ? c.Username : $"{c.Domain}\\{c.Username}";
                var pw = CredentialStore.Has(c.Id) ? "password saved" : "no password";
                return new Row(c, c.Name, $"{user} · {pw} · used by {used} session{(used == 1 ? "" : "s")}");
            })
            .ToList();
        _list.ItemsSource = rows;
        _empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.SelectedItem = rows.FirstOrDefault(r => r.Profile == select) ?? rows.FirstOrDefault();
    }

    private CredentialProfile? Selected => (_list.SelectedItem as Row)?.Profile;

    private void Add()
    {
        var r = Dialogs.EditCredential(this, "New credential profile", null, hasPassword: false);
        if (r == null) return;
        var p = new CredentialProfile { Name = r.Value.Name, Username = r.Value.User, Domain = r.Value.Domain };
        if (!TrySetPassword(p, r.Value.Password)) return;
        _store.Data.Credentials.Add(p);
        _changed();
        Refresh(p);
    }

    private void Edit()
    {
        if (Selected is not { } p) return;
        var r = Dialogs.EditCredential(this, "Edit credential profile", p, CredentialStore.Has(p.Id));
        if (r == null) return;
        p.Name = r.Value.Name;
        p.Username = r.Value.User;
        p.Domain = r.Value.Domain;
        if (!TrySetPassword(p, r.Value.Password)) return;
        _changed();
        Refresh(p);
    }

    private bool TrySetPassword(CredentialProfile p, string? password)
    {
        if (password == null) return true;
        try
        {
            CredentialStore.Set(p.Id, password, p.Username);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the password.\n\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void Delete()
    {
        if (Selected is not { } p) return;
        var users = SessionsUsing(p.Id).ToList();
        var extra = users.Count == 0 ? "" :
            $"\n\n{users.Count} session{(users.Count == 1 ? " uses" : "s use")} it. They keep the username and will ask for a password when connecting.";
        if (MessageBox.Show(this, $"Delete the profile \"{p.Name}\"?{extra}", "Delete", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var n in users) n.Settings!.CredentialId = null;
        try { CredentialStore.Delete(p.Id); } catch { /* already gone */ }
        _store.Data.Credentials.Remove(p);
        _changed();
        Refresh();
    }
}
