using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

namespace SessionManager;

/// <summary>
/// The window icon: a rounded square in the main color with a "&gt;_" prompt in the accent color. With neither color
/// customised it is the installed icon (Assets/app-256.png). Desktops that take the icon from the .desktop file
/// (GNOME on Wayland) keep showing the installed one.
/// </summary>
public static class AppIcon
{
    /// <summary>The square of the installed icon, used when only the accent color is set.</summary>
    public const string DefaultMain = "#1E3160";

    private static WindowIcon? _default;
    private static WindowIcon? _current;
    private static string? _key;

    public static WindowIcon? Current => _current ?? Default;

    private static WindowIcon? Default
    {
        get
        {
            if (_default != null) return _default;
            try { _default = new WindowIcon(AssetLoader.Open(new Uri("avares://sessionmanager/Assets/app-256.png"))); } catch { }
            return _default;
        }
    }

    /// <summary>Redraws the icon for these settings and sets it on every open window.</summary>
    public static void Update(UiSettings ui)
    {
        var key = ui.Main == null && ui.Accent == null ? null : $"{ui.Main}|{ui.Accent}";
        if (key == _key && (_current != null || key == null)) return;
        _key = key;
        _current = null;
        if (key != null)
        {
            try
            {
                using var bmp = Draw(Color.Parse(Hex(ui.Main) ?? DefaultMain), Color.Parse(Hex(ui.Accent) ?? UiTheme.DefaultAccent));
                using var png = new MemoryStream();
                bmp.Save(png);
                png.Position = 0;
                _current = new WindowIcon(png);
            }
            catch { }
        }
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var w in desktop.Windows) w.Icon = Current;
    }

    private static string? Hex(string? c) => c == null ? null : c.StartsWith('#') ? c : "#" + c;

    public static RenderTargetBitmap Draw(Color main, Color accent)
    {
        var glyph = new ImmutableSolidColorBrush(Readable(accent, main));
        var square = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(UiTheme.Mix(main, Colors.White, 0.12), 0), new GradientStop(UiTheme.Mix(main, Colors.Black, 0.4), 1) },
        };
        var pen = new Pen(glyph, 26, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var chevron = new StreamGeometry();
        using (var g = chevron.Open())
        {
            g.BeginFigure(new Point(66, 70), false);
            g.LineTo(new Point(130, 124));
            g.LineTo(new Point(66, 178));
            g.EndFigure(false);
        }

        var bmp = new RenderTargetBitmap(new PixelSize(256, 256));
        using (var dc = bmp.CreateDrawingContext())
        {
            dc.DrawRectangle(square, null, new RoundedRect(new Rect(8, 8, 240, 240), 44));
            dc.DrawGeometry(null, pen, chevron);
            dc.DrawRectangle(glyph, null, new RoundedRect(new Rect(140, 164, 54, 24), 12));
        }
        return bmp;
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    /// <summary>The accent color, made lighter or darker if it would be hard to see on the square.</summary>
    public static Color Readable(Color accent, Color main)
    {
        double back = Luminance(main);
        var target = back < 0.5 ? Colors.White : Colors.Black;
        for (int i = 0; i < 20 && Math.Abs(Luminance(accent) - back) < 0.3; i++)
            accent = UiTheme.Mix(accent, target, 0.08);
        return accent;
    }
}
