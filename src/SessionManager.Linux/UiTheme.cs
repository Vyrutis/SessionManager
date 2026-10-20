using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace SessionManager;

/// <summary>Light / dark theme, accent color and interface size. Custom colors are app resources named Sm*.</summary>
public static class UiTheme
{
    public const string DefaultAccent = "#3B82F6";

    public static readonly string[] AccentPresets =
        ["#3B82F6", "#0EA5E9", "#14B8A6", "#22C55E", "#84CC16", "#EAB308", "#F97316", "#EF4444", "#EC4899", "#A855F7", "#6366F1", "#64748B"];

    public static bool IsDark { get; private set; }
    public static UiSettings Ui { get; private set; } = new();
    public static event Action? Changed;

    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["SmWindow"] = ("#F3F4F6", "#0E1420"),
        ["SmSidebar"] = ("#E9ECF0", "#111927"),
        ["SmToolbar"] = ("#E2E5EA", "#0A0F19"),
        ["SmBorder"] = ("#CDD2D9", "#222D3F"),
        ["SmText"] = ("#1B1F24", "#E6EAF0"),
        ["SmSubText"] = ("#5F6670", "#8D99AB"),
        ["SmTabActive"] = ("#FFFFFF", "#1A2436"),
        ["SmHover"] = ("#D9DEE5", "#1A2333"),
        ["SmCard"] = ("#FFFFFF", "#141C2B"),
    };

    /// <summary>Background of terminals using the "Auto" color scheme.</summary>
    public static Color TerminalBackground => IsDark ? Color.Parse("#0A0F1C") : Color.Parse("#1B1F27");

    public static void Apply(bool dark, UiSettings ui)
    {
        IsDark = dark;
        Ui = UiSettings.Normalize(ui.Clone());
        var app = Application.Current!;
        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var res = app.Resources;
        foreach (var (key, pair) in Palette)
            res[key] = new ImmutableSolidColorBrush(Color.Parse(dark ? pair.Dark : pair.Light));

        var accent = Color.Parse(Ui.Accent is { } a ? (a.StartsWith('#') ? a : "#" + a) : DefaultAccent);
        res["SystemAccentColor"] = accent;
        res["SystemAccentColorLight1"] = Mix(accent, Colors.White, 0.2);
        res["SystemAccentColorLight2"] = Mix(accent, Colors.White, 0.4);
        res["SystemAccentColorLight3"] = Mix(accent, Colors.White, 0.6);
        res["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.2);
        res["SystemAccentColorDark2"] = Mix(accent, Colors.Black, 0.4);
        res["SystemAccentColorDark3"] = Mix(accent, Colors.Black, 0.6);
        res["SmAccent"] = new ImmutableSolidColorBrush(accent);
        res["SmSelection"] = new ImmutableSolidColorBrush(Mix(accent, Color.Parse(dark ? "#111927" : "#E9ECF0"), dark ? 0.55 : 0.65));

        foreach (var weak in Scaled.ToList())
        {
            if (weak.TryGetTarget(out var c)) c.LayoutTransform = new ScaleTransform(Ui.Scale, Ui.Scale);
            else Scaled.Remove(weak);
        }
        AppIcon.Update(Ui);
        Changed?.Invoke();
    }

    public static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    // ───────────── Interface size ─────────────

    private static readonly List<WeakReference<LayoutTransformControl>> Scaled = new();

    /// <summary>Wraps window content so it follows the interface size setting.</summary>
    public static Control Scale(Control content)
    {
        var host = new LayoutTransformControl { Child = content, LayoutTransform = new ScaleTransform(Ui.Scale, Ui.Scale) };
        Scaled.Add(new WeakReference<LayoutTransformControl>(host));
        return host;
    }

    // ───────────── Styles ─────────────

    public static void AddStyles(Styles styles)
    {
        // Flat buttons for toolbars ("tool" class).
        styles.Add(new Style(x => x.OfType<Button>().Class("tool"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(9, 5)),
                new Setter(Layoutable.VerticalAlignmentProperty, VerticalAlignment.Center),
            },
        });
        styles.Add(new Style(x => x.OfType<ToggleButton>().Class("tool"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(9, 5)),
                new Setter(Layoutable.VerticalAlignmentProperty, VerticalAlignment.Center),
            },
        });
        styles.Add(new Style(x => x.OfType<ToggleButton>().Class("tool").Not(y => y.Class(":checked")))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent) },
        });
        styles.Add(new Style(x => x.OfType<TextBlock>().Class("sub"))
        {
            Setters = { new Setter(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SmSubText")) },
        });
        styles.Add(new Style(x => x.OfType<TextBlock>().Class("h"))
        {
            Setters =
            {
                new Setter(TextBlock.FontWeightProperty, FontWeight.SemiBold),
                new Setter(TextBlock.MarginProperty, new Thickness(0, 10, 0, 4)),
            },
        });
    }

    /// <summary>Binds a property to one of the Sm* theme brushes.</summary>
    public static T Res<T>(this T control, AvaloniaProperty property, string key) where T : Control
    {
        control[!property] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(key);
        return control;
    }
}
