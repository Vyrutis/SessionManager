using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SessionManager;

/// <summary>
/// Closing animation: the window fades out and sinks a few pixels. Done on the native window
/// (WS_EX_LAYERED alpha) because AllowsTransparency would break the WebView2 and RDP child windows.
/// </summary>
public static class CloseAnimation
{
    private const int GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000, LWA_ALPHA = 0x2;
    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(190);
    private const double Drop = 14;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint h, int i, nint v);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    public static void Run(Window window, Action onDone)
    {
        bool done = false;
        void Finish() { if (done) return; done = true; onDone(); }
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == 0 || !GetWindowRect(hwnd, out var start)) { Finish(); return; }
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_LAYERED);
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            bool move = window.WindowState == WindowState.Normal;
            var started = DateTime.UtcNow;

            EventHandler? tick = null;
            tick = (_, _) =>
            {
                try
                {
                    double t = Math.Min(1, (DateTime.UtcNow - started) / Duration);
                    double eased = t * t; // ease-in
                    SetLayeredWindowAttributes(hwnd, 0, (byte)Math.Round(255 * (1 - eased)), LWA_ALPHA);
                    if (move) SetWindowPos(hwnd, 0, start.Left, start.Top + (int)Math.Round(Drop * eased), 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                    if (t >= 1) { CompositionTarget.Rendering -= tick; Finish(); }
                }
                catch { CompositionTarget.Rendering -= tick; Finish(); }
            };
            CompositionTarget.Rendering += tick;
            // Safety net: rendering can stall (e.g. display off); close anyway.
            var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            guard.Tick += (_, _) => { guard.Stop(); CompositionTarget.Rendering -= tick; Finish(); };
            guard.Start();
        }
        catch { Finish(); }
    }
}
