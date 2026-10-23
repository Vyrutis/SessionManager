using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SessionManager;

/// <summary>Makes WPF show translated text: texts set on common controls pass through <see cref="Loc.T"/>.</summary>
public static class LocWpf
{
    /// <summary>Reads the language from the settings file, then (if not English) hooks the text properties. Call before any window exists.</summary>
    public static void Start()
    {
        string? setting = null;
        try
        {
            if (File.Exists(SessionStore.FilePath))
            {
                if (DataProtector.TryReadWithoutPassword(SessionStore.FilePath) is { } text)
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("Language", out var l) && l.ValueKind == JsonValueKind.String) setting = l.GetString();
                }
            }
        }
        catch { /* unreadable settings: follow Windows */ }
        Loc.Init(setting);
        if (Loc.Active) Hook();
    }

    /// <summary>
    /// Tooltips are translated when set; buttons, labels, menu headers, text and window titles when they load (TextBlock text also when it changes later). A TextBlock (and a Window title)
    /// owns its property, so those are translated when they load and whenever the text changes after that.
    /// </summary>
    public static void Hook()
    {
        ToolTipService.ToolTipProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(null, null, CoerceObject));
        Window.TitleProperty.OverrideMetadata(typeof(MainWindow), new FrameworkPropertyMetadata(string.Empty, null, CoerceObject));

        EventManager.RegisterClassHandler(typeof(ContentControl), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => TranslateContent((ContentControl)s)));
        EventManager.RegisterClassHandler(typeof(HeaderedItemsControl), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => TranslateHeader((HeaderedItemsControl)s)));
        EventManager.RegisterClassHandler(typeof(HeaderedContentControl), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => TranslateHeader((HeaderedContentControl)s)));
        EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.LoadedEvent, new RoutedEventHandler(TextBlockLoaded));
        EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.UnloadedEvent, new RoutedEventHandler(TextBlockUnloaded));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => ((Window)s).Title = Loc.T(((Window)s).Title)));
    }

    private static readonly System.ComponentModel.DependencyPropertyDescriptor TextChanges =
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    private static void TranslateContent(ContentControl c)
    {
        if (c.Content is string t && !System.Windows.Data.BindingOperations.IsDataBound(c, ContentControl.ContentProperty) && Loc.T(t) != t) c.Content = Loc.T(t);
    }

    private static void TranslateHeader(HeaderedItemsControl c)
    {
        if (c.Header is string t && !System.Windows.Data.BindingOperations.IsDataBound(c, HeaderedItemsControl.HeaderProperty) && Loc.T(t) != t) c.Header = Loc.T(t);
    }

    private static void TranslateHeader(HeaderedContentControl c)
    {
        if (c.Header is string t && !System.Windows.Data.BindingOperations.IsDataBound(c, HeaderedContentControl.HeaderProperty) && Loc.T(t) != t) c.Header = Loc.T(t);
    }

    private static void TextBlockLoaded(object sender, RoutedEventArgs e)
    {
        var tb = (TextBlock)sender;
        Translate(tb);
        TextChanges.RemoveValueChanged(tb, TextChanged);
        TextChanges.AddValueChanged(tb, TextChanged);
    }

    private static void TextBlockUnloaded(object sender, RoutedEventArgs e) => TextChanges.RemoveValueChanged(sender, TextChanged);

    private static void TextChanged(object? sender, EventArgs e) => Translate((TextBlock)sender!);

    private static void Translate(TextBlock tb)
    {
        if (System.Windows.Data.BindingOperations.IsDataBound(tb, TextBlock.TextProperty)) return;
        if (tb.Inlines.Count > 1)
        {
            // Formatted text: translate each plain run.
            foreach (var run in tb.Inlines.OfType<System.Windows.Documents.Run>())
                if (!System.Windows.Data.BindingOperations.IsDataBound(run, System.Windows.Documents.Run.TextProperty)) run.Text = Loc.T(run.Text);
            return;
        }
        var text = tb.Text;
        var translated = Loc.T(text);
        if (translated != text) tb.Text = translated;
    }

    private static object CoerceObject(DependencyObject d, object value) => value is string s ? Loc.T(s) : value;
}

/// <summary>Same calls as <see cref="System.Windows.MessageBox"/>, with the text translated (the files in this namespace pick this one).</summary>
internal static class MessageBox
{
    public static MessageBoxResult Show(string text) => System.Windows.MessageBox.Show(Loc.T(text));
    public static MessageBoxResult Show(string text, string caption) => System.Windows.MessageBox.Show(Loc.T(text), Loc.T(caption));
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button) =>
        System.Windows.MessageBox.Show(Loc.T(text), Loc.T(caption), button);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        System.Windows.MessageBox.Show(Loc.T(text), Loc.T(caption), button, icon);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult) =>
        System.Windows.MessageBox.Show(Loc.T(text), Loc.T(caption), button, icon, defaultResult);
    public static MessageBoxResult Show(Window? owner, string text) => System.Windows.MessageBox.Show(owner!, Loc.T(text));
    public static MessageBoxResult Show(Window? owner, string text, string caption) => System.Windows.MessageBox.Show(owner!, Loc.T(text), Loc.T(caption));
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button) =>
        System.Windows.MessageBox.Show(owner!, Loc.T(text), Loc.T(caption), button);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        System.Windows.MessageBox.Show(owner!, Loc.T(text), Loc.T(caption), button, icon);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult) =>
        System.Windows.MessageBox.Show(owner!, Loc.T(text), Loc.T(caption), button, icon, defaultResult);
}
