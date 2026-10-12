using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace SessionManager;

/// <summary>Shared logins (credential profiles): one user name and password used by many sessions.</summary>
public static class CredentialsWindow
{
    /// <summary>Shows the list; returns true when something changed (the caller saves).</summary>
    public static async Task<bool> Show(Window owner, AppData data, Func<Guid, int> usage)
    {
        bool changed = false;
        Window? host = null;
        var items = new ObservableCollection<CredentialProfile>(data.Credentials.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase));
        var list = new ListBox
        {
            ItemsSource = items,
            Height = 300,
            ItemTemplate = new FuncDataTemplate<CredentialProfile>((p, _) => new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = p?.Name, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = p == null ? "" : $"{(p.Domain.Length > 0 ? p.Domain + "\\" : "")}{p.Username} · used by {usage(p.Id)} session{(usage(p.Id) == 1 ? "" : "s")}",
                        Classes = { "sub" }, FontSize = 12,
                    },
                },
            }),
        };

        async Task Edit(CredentialProfile? existing)
        {
            var name = new TextBox { Text = existing?.Name };
            var domain = new AutoCompleteBox { Text = existing?.Domain, ItemsSource = data.Domains, MinimumPrefixLength = 0, Watermark = "Only for Remote Desktop (optional)" };
            var user = new TextBox { Text = existing?.Username };
            var pw = new TextBox { PasswordChar = '•', Watermark = existing != null && CredentialStore.Has(existing.Id) ? "Saved — leave empty to keep it" : "" };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto") };
            void Add(int row, string label, Control c)
            {
                grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, [Grid.RowProperty] = row });
                c.Margin = new Thickness(0, 4);
                c[Grid.RowProperty] = row;
                c[Grid.ColumnProperty] = 1;
                grid.Children.Add(c);
            }
            Add(0, "Name", name);
            Add(1, "Domain", domain);
            Add(2, "User name", user);
            Add(3, "Password", pw);
            var d = new Dialogs.DialogWindow(existing == null ? "New credential profile" : "Edit credential profile", grid);
            d.AddButton("Save", isDefault: true, accent: true);
            d.AddButton("Cancel", isCancel: true);
            d.Validate = () => !string.IsNullOrWhiteSpace(user.Text);
            if (await d.ShowFor(host ?? owner) != 0) return;

            var p = existing ?? new CredentialProfile();
            p.Username = user.Text!.Trim();
            p.Domain = DomainName.Clean(domain.Text);
            p.Name = string.IsNullOrWhiteSpace(name.Text) ? (p.Domain.Length > 0 ? p.Domain + "\\" : "") + p.Username : name.Text.Trim();
            if (!string.IsNullOrEmpty(pw.Text))
            {
                try { CredentialStore.Set(p.Id, pw.Text, p.Username); }
                catch (Exception ex) { await Dialogs.Error(host ?? owner, "Could not save the password", ex); }
            }
            if (existing == null)
            {
                data.Credentials.Add(p);
                items.Add(p);
            }
            else
            {
                int i = items.IndexOf(existing);
                items[i] = p; // refresh the row
            }
            changed = true;
        }

        var add = new Button { Content = "New…" };
        add.Click += async (_, _) => await Edit(null);
        var edit = new Button { Content = "Edit…" };
        edit.Click += async (_, _) => { if (list.SelectedItem is CredentialProfile p) await Edit(p); };
        list.DoubleTapped += async (_, _) => { if (list.SelectedItem is CredentialProfile p) await Edit(p); };
        var delete = new Button { Content = "Delete" };
        delete.Click += async (_, _) =>
        {
            if (list.SelectedItem is not CredentialProfile p) return;
            int used = usage(p.Id);
            var text = used > 0
                ? $"Delete \"{p.Name}\"? {used} session{(used == 1 ? " uses" : "s use")} it; they will keep the user name and ask for the password."
                : $"Delete \"{p.Name}\"?";
            if (!await Dialogs.Confirm(host ?? owner, text, "Delete credential profile", "Delete", "Cancel", defaultNo: true)) return;
            foreach (var n in data.Roots.SelectMany(r => r.Descendants()).Where(n => n.Settings?.CredentialId == p.Id))
            {
                n.Settings!.CredentialId = null;
                n.Settings.Username = p.Username;
                if (n.Protocol == Protocol.Rdp) n.Settings.Domain = p.Domain;
            }
            try { CredentialStore.Delete(p.Id); } catch { }
            data.Credentials.Remove(p);
            items.Remove(p);
            changed = true;
        };

        var body = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0), Children = { add, edit, delete } };
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(buttons);
        var hint = new TextBlock
        {
            Text = "A profile's user name and password are used by every session that picks it, so a password change is made once. Passwords are kept in the desktop keyring.",
            TextWrapping = TextWrapping.Wrap, Classes = { "sub" }, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(hint, Dock.Top);
        body.Children.Add(hint);
        body.Children.Add(list);

        var w = new Dialogs.DialogWindow("Credential profiles", body, width: 520);
        host = w;
        w.AddButton("Close", isDefault: true, isCancel: true);
        await w.ShowFor(owner);
        return changed;
    }
}
