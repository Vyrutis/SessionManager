using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SessionManager;

/// <summary>The list under the quick-connect box: recent sessions when it is empty, saved sessions matching what is typed.</summary>
public partial class MainWindow
{
    private const int QuickListSize = 8;

    private void InitQuickList()
    {
        QuickBox.GotKeyboardFocus += (_, _) => UpdateQuickList();
        QuickBox.PreviewKeyDown += (_, e) =>
        {
            if (!QuickPopup.IsOpen || QuickList.Items.Count == 0 || e.Key is not (Key.Down or Key.Up)) return;
            int i = QuickList.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
            QuickList.SelectedIndex = Math.Clamp(i, -1, QuickList.Items.Count - 1); // -1: back to the typed text
            if (QuickList.SelectedItem != null) QuickList.ScrollIntoView(QuickList.SelectedItem);
            e.Handled = true;
        };
        QuickList.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(QuickList, (DependencyObject)e.OriginalSource) is ListBoxItem { Tag: Node n })
                OpenFromQuickList(n);
        };
    }

    /// <summary>Saved sessions to show: the most recently opened, or those whose name or address contains the text.</summary>
    internal List<Node> QuickMatches(string text)
    {
        var all = _store.Data.Roots.SelectMany(r => r.Descendants()).Where(n => n.IsSession && n.Settings != null);
        text = text.Trim();
        if (text.Length == 0)
            return all.Where(n => n.LastUsed != null).OrderByDescending(n => n.LastUsed).Take(QuickListSize).ToList();
        return all
            .Select(n => (Node: n, Rank: n.Name.StartsWith(text, StringComparison.CurrentCultureIgnoreCase) ? 0
                : n.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ? 1
                : n.Subtitle.Contains(text, StringComparison.CurrentCultureIgnoreCase) ? 2 : 3))
            .Where(x => x.Rank < 3)
            .OrderBy(x => x.Rank).ThenByDescending(x => x.Node.IsFavorite).ThenByDescending(x => x.Node.LastUsed ?? DateTime.MinValue)
            .ThenBy(x => x.Node.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(QuickListSize).Select(x => x.Node).ToList();
    }

    private void UpdateQuickList()
    {
        var text = QuickBox.Text;
        var matches = QuickMatches(text);
        QuickList.Items.Clear();
        foreach (var n in matches) QuickList.Items.Add(QuickRow(n));
        QuickList.SelectedIndex = -1;
        QuickListTitle.Text = text.Trim().Length == 0 ? "Recent" : "Saved sessions  (↓ to pick, Enter to connect)";
        QuickPopup.IsOpen = matches.Count > 0 && QuickBox.IsKeyboardFocused;
    }

    private ListBoxItem QuickRow(Node n)
    {
        var icon = new TextBlock { Text = n.Glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, BrushFor(n.Protocol));
        var sub = new TextBlock { Text = n.Subtitle, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = n.Name, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(sub);
        var item = new ListBoxItem { Content = row, Tag = n, Padding = new Thickness(6, 4, 6, 4), Cursor = Cursors.Hand };
        item.SetResourceReference(ForegroundProperty, "TextBrush");
        return item;
    }

    private void OpenFromQuickList(Node n)
    {
        QuickPopup.IsOpen = false;
        QuickBox.Clear();
        Select(n);
        OpenSession(n);
    }
}
