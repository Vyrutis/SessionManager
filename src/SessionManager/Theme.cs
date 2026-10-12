using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SessionManager;

/// <summary>
/// Silver (light) and Midnight (dark) themes, customised by <see cref="UiSettings"/>: an accent color, a main color
/// that tints the surfaces, a background picture and the interface size. Every themed color in XAML is a
/// DynamicResource with one of the keys below, so applying a theme just replaces the brushes in Application.Resources.
/// </summary>
public static class Theme
{
    public static bool IsDark { get; private set; }
    public static UiSettings Ui { get; private set; } = new();
    public static double Scale => Ui.Scale;
    public static bool Animations => Ui.Animations && SystemParameters.ClientAreaAnimation;

    public static event Action? Changed;

    public const double MinScale = UiSettings.MinScale, MaxScale = UiSettings.MaxScale;
    public const double MinVisibility = UiSettings.MinVisibility, MaxVisibility = UiSettings.MaxVisibility;

    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["WindowBrush"]        = ("#E4E6E9", "#0B1120"),
        ["TitleBarBrush"]      = ("#C5C9CF", "#060A14"),
        ["ToolbarBrush"]       = ("#D5D8DD", "#0E1628"),
        ["PanelBrush"]         = ("#EEF0F2", "#101A2F"),
        ["InputBrush"]         = ("#F8F9FA", "#15203A"),
        ["BorderBrush"]        = ("#9DA3AB", "#26365A"),
        ["TextBrush"]          = ("#1F2329", "#D8E0F0"),
        ["SubTextBrush"]       = ("#5F6670", "#7D8BA8"),
        ["HoverBrush"]         = ("#D0D4DA", "#1B2A4A"),
        ["PressedBrush"]       = ("#BCC1C8", "#23365E"),
        ["SelectionBrush"]     = ("#AEB5BF", "#2A4378"),
        ["SelectionTextBrush"] = ("#111418", "#FFFFFF"),
        ["TabStripBrush"]      = ("#CDD1D6", "#080D1A"),
        ["TabBrush"]           = ("#BDC2C9", "#131E36"),
        ["TabSelectedBrush"]   = ("#EEF0F2", "#1F2E52"),
        ["ButtonBrush"]        = ("#DADDE1", "#18243F"),
        ["AccentBrush"]        = ("#5A6472", "#5B8CFF"),
        ["SshBrush"]           = ("#2D7D46", "#4CC38A"),
        ["RdpBrush"]           = ("#0F6CBD", "#5B9BFF"),
        ["FolderBrush"]        = ("#A07F00", "#E0B341"),
        ["DangerBrush"]        = ("#C42B1C", "#FF6B5E"),
        ["CloseHoverBrush"]    = ("#C42B1C", "#C42B1C"),
        ["ToggleTrackBrush"]   = ("#9DA3AB", "#2A4378"),
        ["ToggleThumbBrush"]   = ("#FFFFFF", "#D8E0F0"),
        ["ScrollThumbBrush"]   = ("#B3B8BF", "#24324F"),
        ["ScrollThumbHoverBrush"] = ("#8E949C", "#3A5080"),
        ["BadgeBrush"]         = ("#D0D4DA", "#1B2A4A"),
        ["OnAccentBrush"]      = ("#FFFFFF", "#FFFFFF"),
    };

    /// <summary>Surfaces recolored by the main color (hue and saturation; lightness is kept so contrast stays the same).</summary>
    private static readonly HashSet<string> Surfaces = new()
    {
        "WindowBrush", "TitleBarBrush", "ToolbarBrush", "PanelBrush", "InputBrush", "BorderBrush", "HoverBrush", "PressedBrush",
        "SelectionBrush", "TabStripBrush", "TabBrush", "TabSelectedBrush", "ButtonBrush", "ToggleTrackBrush", "ScrollThumbBrush",
        "ScrollThumbHoverBrush", "BadgeBrush",
    };
    private static readonly HashSet<string> Texts = new() { "TextBrush", "SubTextBrush", "ToggleThumbBrush" };

    /// <summary>The main color each theme is designed around; a custom main color is measured against it.</summary>
    public static string DefaultMain(bool dark) => dark ? "#2A4378" : "#AEB5BF";
    public static string DefaultAccent(bool dark) => dark ? Palette["AccentBrush"].Dark : Palette["AccentBrush"].Light;

    /// <summary>Main-window surfaces that turn see-through when a background picture is set.</summary>
    private static readonly string[] ShellKeys = { "WindowBrush", "TitleBarBrush", "ToolbarBrush", "PanelBrush", "TabStripBrush" };

    /// <summary>Background of the SSH terminal (also used by xterm.js).</summary>
    public static string TerminalBackground { get; private set; } = "#0C0C0C";

    /// <summary>The background picture, or null.</summary>
    public static ImageSource? BackgroundImage { get; private set; }
    private static string? _loadedImagePath;

    public static void Apply(bool dark) => Apply(dark, Ui);

    public static void Apply(bool dark, UiSettings ui)
    {
        IsDark = dark;
        Ui = Normalize(ui.Clone());
        var res = Application.Current.Resources;

        var main = ParseOrNull(Ui.Main);
        var accent = ParseOrNull(Ui.Accent);
        var colors = new Dictionary<string, Color>();
        foreach (var (key, pair) in Palette)
        {
            var c = Parse(dark ? pair.Dark : pair.Light);
            if (main != null && (Surfaces.Contains(key) || Texts.Contains(key)))
                c = Tint(c, main.Value, Parse(DefaultMain(dark)), Texts.Contains(key) ? 0.3 : 0.85);
            colors[key] = c;
        }
        if (accent != null)
        {
            var a = accent.Value;
            colors["AccentBrush"] = a;
            colors["SelectionBrush"] = Mix(a, colors["PanelBrush"], dark ? 0.5 : 0.35);
            colors["SelectionTextBrush"] = Luminance(colors["SelectionBrush"]) > 0.45 ? Parse("#111418") : Colors.White;
            colors["OnAccentBrush"] = Luminance(a) > 0.6 ? Parse("#111418") : Colors.White;
            if (dark) colors["ToggleTrackBrush"] = colors["SelectionBrush"];
        }
        foreach (var (key, c) in colors) res[key] = Brush(c);

        TerminalBackground = dark
            ? Hex(main != null ? Tint(Parse("#0A0F1C"), main.Value, Parse(DefaultMain(true)), 0.85) : Parse("#0A0F1C"))
            : "#0C0C0C";

        // Background picture: the main window's surfaces become partly see-through.
        if (Ui.BackgroundImage != _loadedImagePath)
        {
            BackgroundImage = LoadImage(Ui.BackgroundImage);
            _loadedImagePath = Ui.BackgroundImage;
        }
        double alpha = BackgroundImage == null ? 1 : 1 - Ui.BackgroundVisibility;
        foreach (var key in ShellKeys)
        {
            var c = colors[key];
            // The title bar and toolbars stay a little more solid than the big panels so controls stay readable.
            double a = key is "TitleBarBrush" or "ToolbarBrush" or "TabStripBrush" ? Math.Min(1, alpha + 0.12) : alpha;
            c.A = (byte)Math.Round(255 * a);
            res["Shell" + key] = Brush(c);
        }

        var scale = new ScaleTransform(Ui.Scale, Ui.Scale);
        scale.Freeze();
        res["UiScale"] = scale;

        // Built-in controls (TreeView selection etc.) read these system keys.
        res[SystemColors.HighlightBrushKey] = res["SelectionBrush"];
        res[SystemColors.HighlightTextBrushKey] = res["SelectionTextBrush"];
        res[SystemColors.InactiveSelectionHighlightBrushKey] = res["SelectionBrush"];
        res[SystemColors.InactiveSelectionHighlightTextBrushKey] = res["SelectionTextBrush"];
        res[SystemColors.WindowBrushKey] = res["PanelBrush"];
        res[SystemColors.WindowTextBrushKey] = res["TextBrush"];
        res[SystemColors.ControlTextBrushKey] = res["TextBrush"];
        res[SystemColors.GrayTextBrushKey] = res["SubTextBrush"];
        res[SystemColors.ControlBrushKey] = res["PanelBrush"]; // e.g. the corner between two scrollbars

        foreach (Window w in Application.Current.Windows) SetDarkTitleBar(w);
        Changed?.Invoke();
    }

    /// <summary>Fills missing values and keeps everything in range (settings files can be edited by hand).</summary>
    public static UiSettings Normalize(UiSettings? ui) => UiSettings.Normalize(ui);

    /// <summary>Themes a dialog: background, text color, interface size and (in dark mode) a dark system title bar.</summary>
    public static void ApplyTo(Window w, bool scaleContent = true)
    {
        w.SetResourceReference(Window.BackgroundProperty, "WindowBrush");
        w.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        w.SourceInitialized += (_, _) => SetDarkTitleBar(w);

        if (scaleContent && w.Content is FrameworkElement content)
        {
            content.SetResourceReference(FrameworkElement.LayoutTransformProperty, "UiScale");
            double s = Ui.Scale;
            if (w.SizeToContent is SizeToContent.Manual or SizeToContent.Height)
            {
                if (!double.IsNaN(w.Width)) w.Width *= s;
                w.MinWidth *= s;
            }
            if (w.SizeToContent is SizeToContent.Manual or SizeToContent.Width)
            {
                if (!double.IsNaN(w.Height)) w.Height *= s;
                w.MinHeight *= s;
            }
            // Never taller than the screen: a large interface size scrolls instead of being cut off.
            if (w.SizeToContent != SizeToContent.Manual && content is not ScrollViewer)
            {
                w.MaxHeight = SystemParameters.WorkArea.Height;
                w.Content = new ScrollViewer
                {
                    Content = content,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Focusable = false,
                };
            }
        }
    }

    public static Brush Get(string key) => (Brush)Application.Current.Resources[key];

    // ───────────── Colors ─────────────

    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static Color? ParseOrNull(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        hex = hex.Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        if (hex.Length != 7 || !hex.Skip(1).All(Uri.IsHexDigit)) return null;
        return Parse(hex);
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Moves <paramref name="c"/> to the main color's hue, scaling its saturation by how colorful the main color is
    /// compared with the theme's own; lightness is kept.</summary>
    private static Color Tint(Color c, Color main, Color reference, double maxSaturation)
    {
        var (_, s, l) = ToHsl(c);
        var (mh, ms, _) = ToHsl(main);
        var (_, rs, _) = ToHsl(reference);
        double k = rs < 0.01 ? ms * 4 : ms / rs;
        return FromHsl(mh, Math.Min(maxSaturation, s * k + (rs < 0.01 ? ms * 0.1 : 0)), l);
    }

    public static Color Mix(Color a, Color b, double amountOfA) => Color.FromRgb(
        (byte)Math.Round(a.R * amountOfA + b.R * (1 - amountOfA)),
        (byte)Math.Round(a.G * amountOfA + b.G * (1 - amountOfA)),
        (byte)Math.Round(a.B * amountOfA + b.B * (1 - amountOfA)));

    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0;
        if (max - min > 1e-9)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        return FromChroma(h, c, l - c / 2);
    }

    /// <summary>HSV (used by the color picker): h 0-360, s and v 0-1.</summary>
    public static Color FromHsv(double h, double s, double v)
    {
        double c = v * s;
        return FromChroma(h, c, v - c);
    }

    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 1e-9)
        {
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
        }
        return (h, max < 1e-9 ? 0 : d / max, max);
    }

    private static Color FromChroma(double h, double c, double m)
    {
        h = ((h % 360) + 360) % 360;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return Color.FromRgb(B(r + m), B(g + m), B(b + m));
    }

    // ───────────── Background picture ─────────────

    private static BitmapImage? LoadImage(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = 2560; // plenty for a window background, and keeps memory use down
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    /// <summary>Copies a picture into the data folder so it keeps working if the original is moved. Returns the copy's path.</summary>
    public static string StoreBackgroundImage(string source)
    {
        // Check it is a picture WPF can read before switching to it.
        using (var s = File.OpenRead(source))
            BitmapDecoder.Create(s, BitmapCreateOptions.None, BitmapCacheOption.None);
        Directory.CreateDirectory(SessionStore.DataDir);
        var target = Path.Combine(SessionStore.DataDir, $"background-{DateTime.Now:yyyyMMddHHmmssfff}{Path.GetExtension(source).ToLowerInvariant()}");
        File.Copy(source, target, overwrite: true);
        return target;
    }

    public static void DeleteBackgroundImages(string? keep = null)
    {
        if (!Directory.Exists(SessionStore.DataDir)) return;
        foreach (var f in Directory.GetFiles(SessionStore.DataDir, "background-*"))
            if (!string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) TryDelete(f);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    // ───────────── Title bar ─────────────

    private static void SetDarkTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
