using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace SessionManager;

public sealed class App : Application
{
    public override void Initialize()
    {
        Name = "Session Manager";
        Styles.Add(new FluentTheme());
        UiTheme.AddStyles(Styles);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            var window = new MainWindow(Program.Args);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => SingleInstance.Stop();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
