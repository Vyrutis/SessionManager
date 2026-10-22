using System.Windows;
using System.Windows.Controls;
using SessionManager;

/// <summary>Lithuanian translation: exact texts, templates with numbers, plural forms, multi-line messages and the WPF hooks.</summary>
static class LocTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Loc.Build(Lithuanian.Entries, Lithuanian.Prefixes);
        check("loc: exact text", Loc.T("Cancel") == "Atšaukti", Loc.T("Cancel"));
        check("loc: unknown text stays English", Loc.T("Some user session name") == "Some user session name", "");
        check("loc: a number and its form (1)", Loc.T("1 session") == "1 sesija", Loc.T("1 session"));
        check("loc: a number and its form (3)", Loc.T("3 sessions") == "3 sesijos", Loc.T("3 sessions"));
        check("loc: a number and its form (10)", Loc.T("10 sessions") == "10 sesijų", Loc.T("10 sessions"));
        check("loc: a number and its form (21)", Loc.T("21 sessions") == "21 sesija", Loc.T("21 sessions"));
        check("loc: a number and its form (111)", Loc.T("111 sessions") == "111 sesijų", Loc.T("111 sessions"));
        check("loc: an ordinary name is not taken for a count", Loc.T("my session") == "my session", Loc.T("my session"));
        check("loc: text with a name inside", Loc.T("Delete the session \"web-01\"?") == "Ištrinti sesiją „web-01“?", Loc.T("Delete the session \"web-01\"?"));
        check("loc: the name is kept as typed", Loc.T("Delete the folder \"Cancel\"?") == "Ištrinti aplanką „Cancel“?", "");
        check("loc: a message of several lines", Loc.T("Could not save the password.\n\nAccess denied") == "Nepavyko išsaugoti slaptažodžio.\n\nAccess denied", Loc.T("Could not save the password.\n\nAccess denied"));
        check("loc: a start followed by a reason", Loc.T("Not reachable: timed out (port 22)").StartsWith("Nepasiekiamas: "), Loc.T("Not reachable: timed out (port 22)"));
        check("loc: spaces around a text are kept", Loc.T(" (copy)") == " (kopija)" && Loc.T("Cancel ") == "Atšaukti ", "");
        check("loc: a translated part inside a text", Loc.T("Uploading failed.") == "Nepavyko: Keliama.", Loc.T("Uploading failed."));
        check("loc: plural with a text in it", Loc.T("5 SSH sessions match the filter") == "Filtrą atitinka 5 SSH sesijos", Loc.T("5 SSH sessions match the filter"));
        check("loc: the same texts in the English setting are unchanged", Loc.Resolve("en") == "en" && Loc.Resolve("lt") == "lt", "");

        check("loc: no empty translations", Lithuanian.Entries.All(e => e.Target.Length > 0), "");
        check("loc: no duplicate texts", Lithuanian.Entries.GroupBy(e => e.English).All(g => g.Count() == 1), string.Join(", ", Lithuanian.Entries.GroupBy(e => e.English).Where(g => g.Count() > 1).Select(g => g.Key)));

        // The WPF hooks: texts set on controls come out translated.
        string? tb2 = null, tb = null, button = null, menu = null, tip = null, title = null;
        var thread = new Thread(() =>
        {
            LocWpf.Hook();
            var block = new TextBlock { Text = "Cancel" };
            block.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            tb = block.Text;
            block.Text = "Close";
            tb2 = block.Text;
            var btn = new Button { Content = "Close" };
            btn.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            button = (string?)btn.Content;
            var mi = new MenuItem { Header = "Rename folder" };
            mi.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            menu = (string?)mi.Header;
            var f = new Border();
            f.ToolTip = "Minimize";
            tip = (string?)f.ToolTip;
            var win = new Window { Title = "Appearance" };
            win.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            title = win.Title;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        check("loc: TextBlock is translated", tb == "Atšaukti", tb ?? "null");
        check("loc: TextBlock text set later is translated", tb2 == "Uždaryti", tb2 ?? "null");
        check("loc: Button content is translated", button == "Uždaryti", button ?? "null");
        check("loc: MenuItem header is translated", menu == "Pervadinti aplanką", menu ?? "null");
        check("loc: ToolTip is translated", tip == "Sumažinti", tip ?? "null");
        check("loc: Window title is translated", title == "Išvaizda", title ?? "null");

        Loc.Init("en");
        check("loc: English leaves texts alone", Loc.T("Cancel") == "Cancel", "");
    }
}
