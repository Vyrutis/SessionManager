using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SessionManager;

/// <summary>
/// The window and taskbar icon: a rounded square in the main color with a "&gt;_" prompt in the accent color.
/// Without custom colors it uses the theme's own (light or dark), so it changes with the theme. The icon built into the exe (Assets/app.ico) is only used before the theme is applied.
/// </summary>
public static class AppIcon
{
    private static ImageSource? _default;
    private static ImageSource Default =>
        _default ??= BitmapFrame.Create(new Uri("pack://application:,,,/SessionManager;component/Assets/app.ico"));

    private static ImageSource? _current;
    public static ImageSource Current => _current ?? Default;

    /// <summary>Redraws the icon for these settings and sets it on every open window.</summary>
    public static void Update(bool dark, UiSettings ui)
    {
        _current = Draw(Theme.ParseOrNull(ui.Main) ?? Theme.Parse(Theme.DefaultMain(dark)),
                        Theme.ParseOrNull(ui.Accent) ?? Theme.Parse(Theme.DefaultAccent(dark)));
        foreach (Window w in Application.Current.Windows) w.Icon = Current;
    }

    /// <summary>A vector icon, so Windows renders it sharp at every size (title bar, taskbar, Alt+Tab).</summary>
    public static ImageSource Draw(Color main, Color accent)
    {
        var glyph = Readable(accent, main);
        var square = new LinearGradientBrush(Theme.Mix(main, Colors.White, 0.88), Theme.Mix(main, Colors.Black, 0.6), 90);
        var pen = new Pen(new SolidColorBrush(glyph), 26) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 256, 256));
            dc.DrawRoundedRectangle(square, null, new Rect(8, 8, 240, 240), 44, 44);
            var chevron = new StreamGeometry();
            using (var g = chevron.Open())
            {
                g.BeginFigure(new Point(66, 70), false, false);
                g.LineTo(new Point(130, 124), true, true);
                g.LineTo(new Point(66, 178), true, true);
            }
            dc.DrawGeometry(null, pen, chevron);
            dc.DrawRoundedRectangle(new SolidColorBrush(glyph), null, new Rect(140, 164, 54, 24), 12, 12);
        }
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>The accent color, made lighter or darker if it would be hard to see on the square.</summary>
    public static Color Readable(Color accent, Color main)
    {
        double back = Theme.Luminance(main);
        if (Math.Abs(Theme.Luminance(accent) - back) >= 0.3) return accent;
        var (h, s, l) = Theme.ToHsl(accent);
        for (int i = 0; i < 20 && Math.Abs(Theme.Luminance(accent) - back) < 0.3; i++)
        {
            l += back < 0.5 ? 0.04 : -0.04;
            accent = Theme.FromHsl(h, s, l);
        }
        return accent;
    }
}
