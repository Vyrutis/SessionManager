using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SessionManager;

/// <summary>Appearance and Remote Desktop settings.</summary>
public static class SettingsWindow
{
    /// <summary>Returns true when the user saved; theme changes preview live and are undone on cancel.</summary>
    public static async Task<bool> Show(Window owner, AppData data, LinuxSettings linux)
    {
        bool wasDark = UiTheme.IsDark;
        var oldUi = UiTheme.Ui.Clone();
        var ui = oldUi.Clone();
        bool dark = wasDark;

        var root = new StackPanel { Spacing = 4 };
        void Preview() => UiTheme.Apply(dark, ui);

        // Theme
        root.Children.Add(new TextBlock { Text = "Appearance", Classes = { "h" } });
        var light = new RadioButton { Content = "Light", GroupName = "theme", IsChecked = !dark };
        var darkBtn = new RadioButton { Content = "Dark", GroupName = "theme", IsChecked = dark };
        light.IsCheckedChanged += (_, _) => { if (light.IsChecked == true) { dark = false; Preview(); } };
        darkBtn.IsCheckedChanged += (_, _) => { if (darkBtn.IsChecked == true) { dark = true; Preview(); } };
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { light, darkBtn } });

        // Accent
        root.Children.Add(new TextBlock { Text = "Accent color", Margin = new Thickness(0, 8, 0, 2) });
        var hex = new TextBox { Width = 110, Text = ui.Accent ?? UiTheme.DefaultAccent };
        var swatches = new WrapPanel();
        foreach (var color in UiTheme.AccentPresets)
        {
            var b = new Button
            {
                Width = 28, Height = 28, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.Parse(color)), CornerRadius = new CornerRadius(14),
            };
            ToolTip.SetTip(b, color);
            b.Click += (_, _) => { hex.Text = color; };
            swatches.Children.Add(b);
        }
        hex.TextChanged += (_, _) =>
        {
            var t = (hex.Text ?? "").Trim();
            if (!UiSettings.IsColor(t)) return;
            ui.Accent = t.StartsWith('#') ? t.ToUpperInvariant() : "#" + t.ToUpperInvariant();
            Preview();
        };
        root.Children.Add(swatches);
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Hex", VerticalAlignment = VerticalAlignment.Center }, hex } });

        // Scale
        var scaleText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Width = 50 };
        var scale = new Slider { Minimum = UiSettings.MinScale * 100, Maximum = UiSettings.MaxScale * 100, Value = ui.Scale * 100, Width = 260, TickFrequency = 5, IsSnapToTickEnabled = true };
        scaleText.Text = $"{scale.Value:0} %";
        scale.ValueChanged += (_, _) => { scaleText.Text = $"{scale.Value:0} %"; ui.Scale = scale.Value / 100; Preview(); };
        root.Children.Add(new TextBlock { Text = "Interface size", Margin = new Thickness(0, 8, 0, 0) });
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { scale, scaleText } });

        var files = new CheckBox { Content = "Open new SSH tabs with the files (SFTP) panel", IsChecked = data.ShowFiles };
        root.Children.Add(files);

        // Remote Desktop
        root.Children.Add(new TextBlock { Text = "Remote Desktop (FreeRDP)", Classes = { "h" } });
        var found = FreeRdp.Find(linux.RdpClient);
        root.Children.Add(new TextBlock
        {
            Text = found != null ? $"Using {found.Path} (FreeRDP {found.Major})." : "FreeRDP was not found. Install it with: sudo pacman -S freerdp",
            Classes = { "sub" }, TextWrapping = TextWrapping.Wrap,
        });
        var client = new AutoCompleteBox { Text = linux.RdpClient, Watermark = "Automatic (xfreerdp3, sdl-freerdp3, …)", ItemsSource = FreeRdp.Candidates, MinimumPrefixLength = 0 };
        root.Children.Add(Labeled("Program", client));
        var embedded = new CheckBox { Content = "Show the remote desktop inside the tab (X11 / XWayland, experimental)", IsChecked = linux.RdpEmbedded };
        root.Children.Add(embedded);
        var tofu = new CheckBox { Content = "Trust a server's certificate the first time (needed without a certificate prompt)", IsChecked = linux.RdpTrustOnFirstUse };
        root.Children.Add(tofu);
        var extra = new TextBox { Text = linux.RdpExtraArgs, Watermark = "e.g. /scale:140 /network:lan +fonts" };
        root.Children.Add(Labeled("Extra arguments", extra));

        // Keyring
        root.Children.Add(new TextBlock { Text = "Passwords", Classes = { "h" } });
        root.Children.Add(new TextBlock
        {
            Text = OperatingSystem.IsWindows() ? "Saved in Windows Credential Manager."
                : SecretService.IsAvailable() ? "Saved in the desktop keyring (Secret Service) through secret-tool."
                : "secret-tool was not found, so passwords can't be saved. Install libsecret and a keyring: sudo pacman -S libsecret gnome-keyring",
            Classes = { "sub" }, TextWrapping = TextWrapping.Wrap,
        });

        root.Children.Add(new TextBlock { Text = $"Data folder: {SessionStore.DataDir}", Classes = { "sub" }, FontSize = 12, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock { Text = $"Session Manager {AppInfo.Version}", Classes = { "sub" }, FontSize = 12 });

        var d = new Dialogs.DialogWindow("Settings", new ScrollViewer { Content = root, MaxHeight = 640, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, width: 560);
        d.AddButton("Save", isDefault: true, accent: true);
        d.AddButton("Cancel", isCancel: true);
        if (await d.ShowFor(owner) != 0)
        {
            UiTheme.Apply(wasDark, oldUi);
            return false;
        }

        data.Theme = dark ? "Dark" : "Light";
        data.Ui.Accent = ui.Accent;
        data.Ui.Scale = ui.Scale;
        data.ShowFiles = files.IsChecked == true;
        linux.RdpClient = (client.Text ?? "").Trim();
        linux.RdpEmbedded = embedded.IsChecked == true;
        linux.RdpTrustOnFirstUse = tofu.IsChecked == true;
        linux.RdpExtraArgs = (extra.Text ?? "").Trim();
        FreeRdp.ClearCache();
        UiTheme.Apply(dark, data.Ui);
        return true;
    }

    private static Control Labeled(string label, Control field)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*"), Margin = new Thickness(0, 4) };
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(field, 1);
        g.Children.Add(field);
        return g;
    }
}
