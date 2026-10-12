using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace SessionManager;

/// <summary>
/// The domain in front of a username ("CORP" \ "admin"): an editable drop-down of saved domains with a star button that
/// saves the typed domain to the list (or removes it again).
/// </summary>
public sealed class DomainPicker : Grid
{
    /// <summary>The saved domains (AppData.Domains). Set by the main window at startup.</summary>
    public static List<string> Saved { get; set; } = new();
    /// <summary>Called after the list changes so it can be written to disk.</summary>
    public static Action? SavedChanged { get; set; }

    private readonly ComboBox _box;
    private readonly Button _star;
    private readonly TextBlock _starGlyph;
    private readonly TextBlock _hint;

    public DomainPicker()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _box = new ComboBox
        {
            IsEditable = true,
            IsTextSearchEnabled = true,
            StaysOpenOnEdit = true,
            ToolTip = "Windows domain added in front of the username (DOMAIN\\user). Pick a saved one or type a new one.",
        };
        _box.DropDownOpened += (_, _) => Refill();
        _box.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Update()));
        _box.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(Update);
        Children.Add(_box);

        _hint = new TextBlock
        {
            Text = "Domain",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        Children.Add(_hint);

        _starGlyph = new TextBlock { FontSize = 13 };
        _starGlyph.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
        _star = new Button { Content = _starGlyph, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(4, 0, 0, 0), Focusable = false };
        _star.SetResourceReference(StyleProperty, "ToolButton");
        _star.Click += (_, _) => ToggleSaved();
        SetColumn(_star, 1);
        Children.Add(_star);

        Refill();
        Update();
    }

    /// <summary>The domain without spaces or a trailing backslash; "" = none.</summary>
    public string Domain
    {
        get => Clean(_box.Text);
        set { _box.Text = Clean(value); Update(); }
    }

    public ComboBox Box => _box;

    public static string Clean(string? domain) => (domain ?? "").Trim().TrimEnd('\\').Trim();

    /// <summary>"CORP" + "admin" → "CORP\admin".</summary>
    public static string Combine(string domain, string user)
    {
        domain = Clean(domain);
        return domain.Length == 0 || user.Length == 0 ? user : $"{domain}\\{user}";
    }

    /// <summary>"CORP\admin" → ("CORP", "admin"); a name without a domain is returned unchanged.</summary>
    public static (string Domain, string User) Split(string? user)
    {
        user ??= "";
        int i = user.IndexOf('\\');
        return i > 0 && i < user.Length - 1 ? (user[..i], user[(i + 1)..]) : ("", user);
    }

    /// <summary>Remembers a domain without the star (e.g. one used in a saved session). Returns true if it was new.</summary>
    public static bool Remember(string? domain)
    {
        domain = Clean(domain);
        if (domain.Length == 0 || IsSaved(domain)) return false;
        Saved.Add(domain);
        Saved.Sort(StringComparer.CurrentCultureIgnoreCase);
        return true;
    }

    private static bool IsSaved(string domain) => Saved.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase));

    private void Refill()
    {
        var text = _box.Text;
        _box.Items.Clear();
        foreach (var d in Saved.OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase)) _box.Items.Add(d);
        _box.Text = text;
    }

    private void ToggleSaved()
    {
        var d = Domain;
        if (d.Length == 0)
        {
            _box.Focus();
            _box.IsDropDownOpen = Saved.Count > 0;
            return;
        }
        if (IsSaved(d)) Saved.RemoveAll(x => x.Equals(d, StringComparison.OrdinalIgnoreCase));
        else Remember(d);
        SavedChanged?.Invoke();
        Refill();
        Update();
    }

    private void Update()
    {
        var d = Domain;
        _hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool saved = d.Length > 0 && IsSaved(d);
        _starGlyph.Text = saved ? "" : ""; // filled / outline star
        _starGlyph.SetResourceReference(TextBlock.ForegroundProperty, saved ? "FolderBrush" : "SubTextBrush");
        _star.ToolTip = d.Length == 0
            ? (Saved.Count > 0 ? "Show saved domains" : "Type a domain, then click to save it to the list")
            : saved ? $"Remove \"{d}\" from the saved domains" : $"Save \"{d}\" to the domain list";
    }
}
