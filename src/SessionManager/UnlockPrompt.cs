using System.Windows;
using System.Windows.Controls;

namespace SessionManager;

/// <summary>The master password prompt shown before anything else loads (the theme is not applied yet, so it uses plain controls).</summary>
public static class UnlockPrompt
{
    public static string? Ask(bool wrong)
    {
        var box = new PasswordBox { MinWidth = 320, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "Open", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 12, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = wrong ? "Wrong password. Enter the master password:" : "Enter the master password to open your sessions:" });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Title = "Session Manager", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => box.Focus();
        return window.ShowDialog() == true ? box.Password : null;
    }
}
