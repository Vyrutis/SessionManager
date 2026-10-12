using Avalonia;

namespace SessionManager;

public static class Program
{
    public static string[] Args { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        if (args.Contains("--version"))
        {
            Console.WriteLine($"Session Manager {AppInfo.Version}");
            return 0;
        }
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("""
                Usage: sessionmanager [--open <session id>] [file.smlist | file.smsession | file.rdp | file.remmina]

                  --open <id>   connect to a saved session (used by application-menu shortcuts)
                  --version     print the version
                  file          import a session list, session, .rdp or Remmina file

                Data folder: ~/.config/SessionManager (set SESSIONMANAGER_DATA to use another one).
                """);
            return 0;
        }

        // A second launch hands its arguments to the window that is already open.
        if (SingleInstance.TrySendToRunning(args)) return 0;

        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { CrashLog.Write(e.Exception); e.SetObserved(); };

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new X11PlatformOptions { WmClass = "sessionmanager", EnableIme = true })
            .LogToTrace();
}

public static class AppInfo
{
    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";
}

/// <summary>Unexpected errors are appended to crash.log in the data folder, to help with bug reports.</summary>
public static class CrashLog
{
    public static string FilePath => Path.Combine(SessionStore.DataDir, "crash.log");

    public static void Write(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(SessionStore.DataDir);
            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Session Manager {AppInfo.Version}\n{ex}\n\n");
        }
        catch { }
    }
}
