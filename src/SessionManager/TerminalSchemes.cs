namespace SessionManager;

/// <summary>Terminal color schemes. The palettes live in wwwroot/terminal.html; this lists names and backgrounds.</summary>
public static class TerminalSchemes
{
    public const string Auto = "Auto";

    private static readonly Dictionary<string, string> Backgrounds = new()
    {
        ["Campbell"] = "#0C0C0C",
        ["One Dark"] = "#282C34",
        ["Dracula"] = "#282A36",
        ["Solarized Dark"] = "#002B36",
        ["Solarized Light"] = "#FDF6E3",
        ["Gruvbox Dark"] = "#282828",
        ["Nord"] = "#2E3440",
    };

    /// <summary>"Auto" first, then the fixed schemes.</summary>
    public static IEnumerable<string> Names => Backgrounds.Keys.Prepend(Auto);

    public static readonly string[] Fonts = { "Cascadia Mono", "Consolas", "Lucida Console", "Courier New" };

    /// <summary>Background color of a scheme; "Auto" (or unknown) follows the app theme.</summary>
    public static string Background(string? scheme) =>
        scheme != null && Backgrounds.TryGetValue(scheme, out var bg) ? bg : Theme.TerminalBackground;
}
