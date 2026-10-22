using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SessionManager;

/// <summary>
/// Translates the English texts of the app. The English text itself is the key, so a text that has no
/// translation simply stays English. Texts with parts that change ("{0} sessions") are matched as templates.
/// In a translation, <c>{0}</c> is the captured part, <c>{t0}</c> the captured part translated too, and
/// <c>{0:sesija|sesijos|sesijų}</c> prints the number followed by the form that fits it (Lithuanian has three).
/// </summary>
public static class Loc
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();
    private static Dictionary<string, string> _exact = new();
    private static List<(Regex Pattern, string Target)> _templates = new();
    private static List<(string English, string Target)> _prefixes = new();

    /// <summary>True when texts are translated (the language is not English).</summary>
    public static bool Active { get; private set; }

    /// <summary>"en" or "lt".</summary>
    public static string Language { get; private set; } = "en";

    /// <summary>Chooses the language: "" follows Windows, otherwise "en" or "lt".</summary>
    public static void Init(string? setting)
    {
        Language = Resolve(setting);
        Cache.Clear();
        Active = Language == "lt";
        if (!Active) return;
        Build(Lithuanian.Entries, Lithuanian.Prefixes);
    }

    public static string Resolve(string? setting) =>
        setting is "en" or "lt" ? setting
        : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "lt" ? "lt" : "en";

    /// <summary>Loads a dictionary (used by Init and by tests).</summary>
    public static void Build((string English, string Target)[] entries, (string English, string Target)[] prefixes)
    {
        var exact = new Dictionary<string, string>();
        var templates = new List<(string Source, Regex Pattern, string Target)>();
        foreach (var (en, lt) in entries)
        {
            if (Token.IsMatch(en))
            {
                var pattern = new StringBuilder("^");
                int at = 0;
                foreach (Match t in Token.Matches(en))
                {
                    pattern.Append(Regex.Escape(en[at..t.Index]));
                    var kind = t.Groups[1].Value;
                    pattern.Append($"(?<n{t.Groups[2].Value}>{(kind == "#" ? @"\d+" : kind == "s" ? "s|" : ".*?")})");
                    at = t.Index + t.Length;
                }
                pattern.Append(Regex.Escape(en[at..])).Append('$');
                templates.Add((en, new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), lt));
            }
            else exact[en] = lt;
        }
        _exact = exact;
        // The most specific template (most fixed text) is tried first.
        _templates = templates.OrderByDescending(t => Token.Replace(t.Source, "").Length).Select(t => (t.Pattern, t.Target)).ToList();
        _prefixes = prefixes.OrderByDescending(p => p.English.Length).ToList();
        Active = true;
        Cache.Clear();
    }

    public static string T(string? english)
    {
        if (!Active || string.IsNullOrEmpty(english)) return english ?? "";
        return Cache.GetOrAdd(english, Translate);
    }

    private static string Translate(string s)
    {
        if (_exact.TryGetValue(s, out var hit)) return hit;
        foreach (var (pattern, target) in _templates)
        {
            var m = pattern.Match(s);
            if (m.Success) return Fill(target, m);
        }

        // Surrounding spaces and line breaks.
        var core = s.Trim();
        if (core.Length > 0 && core != s)
        {
            var t = Translate(core);
            if (t != core)
            {
                int start = s.IndexOf(core, StringComparison.Ordinal);
                return s[..start] + t + s[(start + core.Length)..];
            }
        }

        // One line at a time (messages are built from several lines).
        if (s.Contains('\n'))
        {
            var lines = s.Split('\n');
            bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].Length == 0 ? lines[i] : Translate(lines[i]);
                changed |= t != lines[i];
                lines[i] = t;
            }
            if (changed) return string.Join('\n', lines);
        }

        // A known start followed by text that varies ("Not reachable: " + the reason).
        foreach (var (en, target) in _prefixes)
            if (s.Length > en.Length && s.StartsWith(en, StringComparison.Ordinal))
                return target + Translate(s[en.Length..]);

        return s;
    }

    private static readonly Regex Token = new(@"\{([#s]?)(\d+)\}", RegexOptions.Compiled);
    private static readonly Regex Hole = new(@"\{(t?)(\d+)(?::((?:[^{}]|\{\d+\})*))?\}", RegexOptions.Compiled);

    private static string Fill(string target, Match m) =>
        Hole.Replace(target, h =>
        {
            var value = m.Groups["n" + h.Groups[2].Value].Value;
            if (h.Groups[3].Success)
            {
                var forms = Token.Replace(h.Groups[3].Value, f => m.Groups["n" + f.Groups[2].Value].Value).Split('|');
                return value + " " + PluralForm(value, forms);
            }
            return h.Groups[1].Length > 0 ? Translate(value) : value;
        });

    /// <summary>Lithuanian forms: 1 (21, 31…), 2-9 (22-29…) and 0, 10-20, 30… ; with only two forms the second is used for all but 1.</summary>
    public static string PluralForm(string number, string[] forms)
    {
        if (!long.TryParse(number.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return forms[^1];
        n = Math.Abs(n);
        int index = n % 10 == 1 && n % 100 != 11 ? 0
            : n % 10 == 0 || (n % 100 >= 11 && n % 100 <= 19) ? 2
            : 1;
        return forms[Math.Min(index, forms.Length - 1)];
    }

    /// <summary>The numbers of a template, for checking translations: every {n} in the English text must be in the translation.</summary>
    public static IEnumerable<string> HolesOf(string text) =>
        Regex.Matches(text, @"\{[t#s]?(\d+)").Select(x => x.Groups[1].Value).Distinct().OrderBy(x => x);
}
