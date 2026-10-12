using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace SessionManager;

/// <summary>
/// Single instance: a second launch (e.g. from a desktop shortcut) hands its "--open {id}" to the
/// running window over a named pipe and exits.
/// </summary>
public partial class App : Application
{
    // A copy using its own data folder (SESSIONMANAGER_DATA) runs as a separate instance.
    private static readonly string InstanceName =
        "SessionManager-" + string.Concat(Environment.UserName.Where(char.IsLetterOrDigit)) +
        (Environment.GetEnvironmentVariable("SESSIONMANAGER_DATA") is { Length: > 0 } ? "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(SessionStore.DataDir.ToLowerInvariant())))[..12] : "");
    private Mutex? _mutex;

    /// <summary>True once Windows is logging off or shutting down (no prompts, no close animation).</summary>
    public static bool IsSessionEnding;

    protected override void OnStartup(StartupEventArgs e)
    {
        var openId = ParseOpenArg(e.Args);

        _mutex = new Mutex(true, InstanceName, out bool isFirst);
        if (!isFirst)
        {
            SendToRunningInstance(openId);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log(ex.Exception);
            MessageBox.Show(ex.Exception.Message, "Session Manager - unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        SessionEnding += (_, _) => IsSessionEnding = true;
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (openId != null) window.OpenById(openId.Value);
        _ = ListenAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static Guid? ParseOpenArg(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--open", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(args[i + 1], out var id))
                return id;
        return null;
    }

    private static void SendToRunningInstance(Guid? openId)
    {
        try
        {
            AllowSetForegroundWindow(-1);
            using var client = new NamedPipeClientStream(".", InstanceName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            using var writer = new StreamWriter(client);
            writer.WriteLine(openId?.ToString() ?? "activate");
        }
        catch
        {
            // The other instance is shutting down or not responding; nothing useful to do.
        }
    }

    private async Task ListenAsync()
    {
        while (true)
        {
            try
            {
                await using var server = new NamedPipeServerStream(InstanceName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server);
                var line = (await reader.ReadLineAsync())?.Trim();
                await Dispatcher.InvokeAsync(() => HandleMessage(line));
            }
            catch (Exception ex)
            {
                Log(ex);
                await Task.Delay(500);
            }
        }
    }

    private void HandleMessage(string? line)
    {
        if (MainWindow is not MainWindow window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        if (Guid.TryParse(line, out var id)) window.OpenById(id);
    }

    /// <summary>Appends unexpected errors to %APPDATA%\SessionManager\error.log.</summary>
    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(SessionStore.DataDir);
            File.AppendAllText(Path.Combine(SessionStore.DataDir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
