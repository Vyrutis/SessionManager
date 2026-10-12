using System.Windows;

namespace SessionManager;

/// <summary>Builds the <see cref="SshUi"/> callbacks for a WPF element: dialogs and log messages go to its window.</summary>
public static class SshUiWpf
{
    public static SshUi Create(FrameworkElement anchor, Func<string, bool, (string Password, bool Save)?> askPassword, Action<string, LogKind> log) => new()
    {
        AskPassword = askPassword,
        Log = log,
        Post = a => anchor.Dispatcher.InvokeAsync(a),
        ConfirmHostKey = q => anchor.Dispatcher.Invoke(() =>
        {
            var owner = Window.GetWindow(anchor);
            var icon = q.Changed ? MessageBoxImage.Warning : MessageBoxImage.Question;
            var answer = owner != null
                ? MessageBox.Show(owner, q.Message, "Verify server key", MessageBoxButton.YesNo, icon,
                    q.Changed ? MessageBoxResult.No : MessageBoxResult.Yes)
                : MessageBox.Show(q.Message, "Verify server key", MessageBoxButton.YesNo, icon, MessageBoxResult.No);
            return answer == MessageBoxResult.Yes;
        }),
    };
}
