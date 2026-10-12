using Avalonia.Media;

namespace SessionManager.Terminal;

/// <summary>Terminal colors: background, foreground, selection and the 16 ANSI colors (the same schemes as the Windows app).</summary>
public sealed record TerminalScheme(string Name, Color Background, Color Foreground, Color Selection, Color Cursor, Color[] Ansi)
{
    public const string Auto = "Auto";

    private static Color C(string hex) => Color.Parse(hex);
    private static Color[] A(params string[] hex) => hex.Select(C).ToArray();

    private static readonly Color[] Campbell = A("#0c0c0c", "#c50f1f", "#13a10e", "#c19c00", "#0037da", "#881798", "#3a96dd", "#cccccc",
        "#767676", "#e74856", "#16c60c", "#f9f1a5", "#3b78ff", "#b4009e", "#61d6d6", "#f2f2f2");

    private static readonly Color[] Solarized = A("#073642", "#dc322f", "#859900", "#b58900", "#268bd2", "#d33682", "#2aa198", "#eee8d5",
        "#586e75", "#cb4b16", "#586e75", "#657b83", "#839496", "#6c71c4", "#93a1a1", "#fdf6e3");

    public static readonly IReadOnlyList<TerminalScheme> All =
    [
        new("Campbell", C("#0c0c0c"), C("#cccccc"), C("#3a5a9a"), C("#cccccc"), Campbell),
        new("One Dark", C("#282c34"), C("#abb2bf"), C("#3e4451"), C("#abb2bf"), A("#282c34", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#abb2bf",
            "#5c6370", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#ffffff")),
        new("Dracula", C("#282a36"), C("#f8f8f2"), C("#44475a"), C("#f8f8f2"), A("#21222c", "#ff5555", "#50fa7b", "#f1fa8c", "#bd93f9", "#ff79c6", "#8be9fd", "#f8f8f2",
            "#6272a4", "#ff6e6e", "#69ff94", "#ffffa5", "#d6acff", "#ff92df", "#a4ffff", "#ffffff")),
        new("Solarized Dark", C("#002b36"), C("#839496"), C("#073642"), C("#839496"), Solarized),
        new("Solarized Light", C("#fdf6e3"), C("#657b83"), C("#eee8d5"), C("#586e75"), Solarized),
        new("Gruvbox Dark", C("#282828"), C("#ebdbb2"), C("#504945"), C("#ebdbb2"), A("#282828", "#cc241d", "#98971a", "#d79921", "#458588", "#b16286", "#689d6a", "#a89984",
            "#928374", "#fb4934", "#b8bb26", "#fabd2f", "#83a598", "#d3869b", "#8ec07c", "#ebdbb2")),
        new("Nord", C("#2e3440"), C("#d8dee9"), C("#434c5e"), C("#d8dee9"), A("#3b4252", "#bf616a", "#a3be8c", "#ebcb8b", "#81a1c1", "#b48ead", "#88c0d0", "#e5e9f0",
            "#4c566a", "#bf616a", "#a3be8c", "#ebcb8b", "#81a1c1", "#b48ead", "#8fbcbb", "#eceff4")),
    ];

    public static IEnumerable<string> Names => All.Select(s => s.Name).Prepend(Auto);

    /// <summary>A named scheme; "Auto" (or unknown) is Campbell on the app theme's terminal background.</summary>
    public static TerminalScheme Get(string? name, Color autoBackground) =>
        All.FirstOrDefault(s => s.Name == name)
        ?? All[0] with { Name = Auto, Background = autoBackground, Foreground = C("#d4d4d4"), Cursor = C("#d4d4d4") };

    private static readonly byte[] CubeLevels = [0, 95, 135, 175, 215, 255];

    /// <summary>Palette index (0-255) or packed RGB → color.</summary>
    public Color Resolve(int color, bool foreground)
    {
        if (color == Cell.DefaultColor) return foreground ? Foreground : Background;
        if ((color & Cell.RgbFlag) != 0) return Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);
        if (color < 16) return Ansi[color];
        if (color < 232)
        {
            int i = color - 16;
            return Color.FromRgb(CubeLevels[i / 36], CubeLevels[i / 6 % 6], CubeLevels[i % 6]);
        }
        byte g = (byte)(8 + (color - 232) * 10);
        return Color.FromRgb(g, g, g);
    }
}
