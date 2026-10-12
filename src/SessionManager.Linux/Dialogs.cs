using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SessionManager;

/// <summary>Message boxes and small prompts.</summary>
public static class Dialogs
{
    public const string AppTitle = "Session Manager";

    /// <summary>A themed dialog window with content and a row of buttons; the result is the clicked button's index (-1 = closed).</summary>
    public sealed class DialogWindow : Window
    {
        public int Result { get; private set; } = -1;
        private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };

        public DialogWindow(string title, Control body, double width = 440)
        {
            Title = title;
            Width = width;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            this.Res(BackgroundProperty, "SmWindow");
            Icon = MainWindow.AppIcon;
            var root = new DockPanel { Margin = new Thickness(20, 18, 20, 16) };
            DockPanel.SetDock(_buttons, Dock.Bottom);
            _buttons.Margin = new Thickness(0, 18, 0, 0);
            root.Children.Add(_buttons);
            root.Children.Add(body);
            Content = UiTheme.Scale(root);
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { Result = -1; Close(); } };
        }

        public Button AddButton(string text, bool isDefault = false, bool isCancel = false, bool accent = false)
        {
            int index = _buttons.Children.Count;
            var b = new Button { Content = text, IsDefault = isDefault, IsCancel = isCancel, MinWidth = 84, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (accent) b.Classes.Add("accent");
            b.Click += (_, _) =>
            {
                if (Validate != null && index == AcceptIndex && !Validate()) return;
                Result = index;
                Close();
            };
            _buttons.Children.Add(b);
            return b;
        }

        /// <summary>Checked before the button at <see cref="AcceptIndex"/> closes the dialog.</summary>
        public Func<bool>? Validate { get; set; }
        public int AcceptIndex { get; set; }

        public async Task<int> ShowFor(Window? owner)
        {
            if (owner != null && owner.IsVisible) await ShowDialog(owner);
            else
            {
                var done = new TaskCompletionSource();
                Closed += (_, _) => done.TrySetResult();
                Show();
                await done.Task;
            }
            return Result;
        }
    }

    private static Control Text(string text) => new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };

    public static async Task Info(Window? owner, string text, string title = AppTitle)
    {
        var d = new DialogWindow(title, Text(text));
        d.AddButton("OK", isDefault: true, isCancel: true, accent: true);
        await d.ShowFor(owner);
    }

    public static Task Error(Window? owner, string what, Exception ex) => Info(owner, $"{what}.\n\n{ex.Message}");

    public static async Task<bool> Confirm(Window? owner, string text, string title = AppTitle, string yes = "Yes", string no = "No", bool defaultNo = false)
    {
        var d = new DialogWindow(title, Text(text));
        d.AddButton(yes, isDefault: !defaultNo, accent: !defaultNo);
        d.AddButton(no, isDefault: defaultNo, isCancel: true);
        return await d.ShowFor(owner) == 0;
    }

    /// <summary>Yes / No / Cancel → 0 / 1 / -1.</summary>
    public static async Task<int> Ask3(Window? owner, string text, string yes, string no, string cancel = "Cancel", string title = AppTitle)
    {
        var d = new DialogWindow(title, Text(text));
        d.AddButton(yes, isDefault: true, accent: true);
        d.AddButton(no);
        d.AddButton(cancel, isCancel: true);
        var r = await d.ShowFor(owner);
        return r == 2 ? -1 : r;
    }

    public static async Task<string?> PromptText(Window? owner, string title, string label, string initial = "", bool allowEmpty = false)
    {
        var box = new TextBox { Text = initial };
        var body = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, box } };
        var d = new DialogWindow(title, body);
        d.AddButton("OK", isDefault: true, accent: true);
        d.AddButton("Cancel", isCancel: true);
        d.Validate = () => allowEmpty || !string.IsNullOrWhiteSpace(box.Text);
        d.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return await d.ShowFor(owner) == 0 ? (box.Text ?? "").Trim() : null;
    }

    public static async Task<(string Password, bool Save)?> PromptPassword(Window? owner, string title, string label, bool offerSave)
    {
        var box = new TextBox { PasswordChar = '•', RevealPassword = false };
        var save = new CheckBox { Content = "Save password in the keyring", IsVisible = offerSave };
        var body = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, box, save } };
        var d = new DialogWindow(title, body);
        d.AddButton("Connect", isDefault: true, accent: true);
        d.AddButton("Cancel", isCancel: true);
        d.Opened += (_, _) => box.Focus();
        if (await d.ShowFor(owner) != 0) return null;
        return (box.Text ?? "", save.IsChecked == true);
    }

    /// <summary>Runs a dialog from a background thread and waits for its answer.</summary>
    public static T FromBackground<T>(Func<Task<T>> dialog) =>
        Dispatcher.UIThread.InvokeAsync(dialog).GetAwaiter().GetResult();
}
