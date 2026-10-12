using SessionManager.Terminal;

static class TerminalTests
{
    static string Row(VtTerminal t, int y) => t.ScreenLine(y).Text().TrimEnd();

    public static void Run(Action<string, bool, string> check)
    {
        void Check(string n, bool ok, string d = "") => check(n, ok, d);

        var t = new VtTerminal(20, 5);
        t.Feed("hello\r\nworld");
        Check("text and CRLF", Row(t, 0) == "hello" && Row(t, 1) == "world" && t.CursorX == 5 && t.CursorY == 1, $"{Row(t, 0)}|{Row(t, 1)}");

        t = new VtTerminal(20, 5);
        t.Feed("\x1b[31mR\x1b[0m\x1b[38;5;200mX\x1b[38;2;1;2;3mY\x1b[38:2::10:20:30mZ\x1b[1;4;44mB");
        var c = t.ScreenLine(0).Cells;
        Check("SGR 16 colors", c[0].Fg == 1);
        Check("SGR 256 colors", c[1].Fg == 200);
        Check("SGR truecolor ;", c[2].Fg == (Cell.RgbFlag | 0x010203), c[2].Fg.ToString("X"));
        Check("SGR truecolor colon form", c[3].Fg == (Cell.RgbFlag | 0x0A141E), c[3].Fg.ToString("X"));
        Check("SGR bold+underline+bg", c[4].Attr.HasFlag(CellAttr.Bold) && c[4].Attr.HasFlag(CellAttr.Underline) && c[4].Bg == 4);

        t = new VtTerminal(20, 6);
        t.Feed("\x1b[5;10HX\x1b[2;3H\x1b[2CY\x1b[AZ");
        Check("CUP / CUF / CUU", Row(t, 4) == "         X" && Row(t, 1) == "    Y" && Row(t, 0) == "     Z", $"[{Row(t, 0)}][{Row(t, 1)}][{Row(t, 4)}]");

        t = new VtTerminal(20, 3);
        t.Feed("abcdefghij\x1b[5D\x1b[K");
        Check("EL erases to end of line", Row(t, 0) == "abcde");
        t.Feed("\x1b[2J");
        Check("ED 2 clears the screen", Row(t, 0) == "" && t.ScrollbackCount == 1, $"scrollback {t.ScrollbackCount}");

        t = new VtTerminal(10, 3);
        t.Feed("0123456789AB");
        Check("autowrap", Row(t, 0) == "0123456789" && Row(t, 1) == "AB" && t.ScreenLine(0).Wrapped);
        Check("wrapped lines copy as one", t.GetText(t.FirstLineNumber, 0, t.FirstLineNumber + 1, 9) == "0123456789AB", t.GetText(t.FirstLineNumber, 0, t.FirstLineNumber + 1, 9));

        t = new VtTerminal(10, 3);
        t.Feed("1\r\n2\r\n3\r\n4\r\n5");
        Check("scrollback", t.ScrollbackCount == 2 && Row(t, 0) == "3" && Row(t, 2) == "5" && t.BufferLine(0).Text().Trim() == "1");

        t = new VtTerminal(10, 5);
        t.Feed("a\r\nb\r\nc\r\nd\r\ne\x1b[2;4r\x1b[4;1H\n");
        Check("scroll region", Row(t, 0) == "a" && Row(t, 1) == "c" && Row(t, 2) == "d" && Row(t, 3) == "" && Row(t, 4) == "e" && t.ScrollbackCount == 0,
            string.Join("|", Enumerable.Range(0, 5).Select(y => Row(t, y))));

        t = new VtTerminal(10, 3);
        t.Feed("shell$ ");
        t.Feed("\x1b[?1049h\x1b[2J\x1b[Hvim here");
        bool inAlt = t.AltScreen && Row(t, 0) == "vim here";
        t.Feed("\x1b[?1049l");
        Check("alternate screen restores the shell", inAlt && !t.AltScreen && Row(t, 0) == "shell$" && t.CursorX == 7);

        t = new VtTerminal(20, 5);
        string? reply = null;
        t.Reply += r => reply = r;
        t.Feed("\x1b[3;7H\x1b[6n");
        Check("cursor position report", reply == "\x1b[3;7R", reply ?? "null");
        t.Feed("\x1b[c");
        Check("device attributes answer", reply?.StartsWith("\x1b[?") == true);

        t = new VtTerminal(10, 2);
        t.Feed("中a");
        c = t.ScreenLine(0).Cells;
        Check("wide characters take two cells", c[0].Code == '中' && c[1].Code == Cell.WideTail && c[2].Code == 'a' && t.CursorX == 3);

        t = new VtTerminal(10, 2);
        t.Feed("\x1b(0lqk\x1b(Bq");
        Check("DEC line drawing", Row(t, 0) == "┌─┐q", Row(t, 0));

        t = new VtTerminal(10, 2);
        t.Feed("abcdef\x1b[1;3H\x1b[2P");
        Check("DCH deletes characters", Row(t, 0) == "abef");
        t.Feed("\x1b[2@");
        Check("ICH inserts blanks", Row(t, 0) == "ab  ef");

        t = new VtTerminal(10, 4);
        t.Feed("1\r\n2\r\n3\r\n4");
        t.Resize(12, 2);
        Check("shrinking keeps the cursor line", Row(t, 1) == "4" && t.CursorY == 1 && t.ScrollbackCount == 2, $"{Row(t, 0)}|{Row(t, 1)} y={t.CursorY}");
        t.Resize(12, 4);
        Check("growing brings lines back", Row(t, 0) == "1" && Row(t, 3) == "4" && t.CursorY == 3);

        t = new VtTerminal(10, 2);
        string? title = null;
        t.TitleChanged += s => title = s;
        t.Feed("\x1b]0;user@host: ~\x07\x1b[?2004h\x1b[?1h\x1b[?1000h\x1b[?1006h\x1b[?25l");
        Check("OSC title", title == "user@host: ~");
        Check("modes: paste, cursor keys, mouse, cursor", t.BracketedPaste && t.ApplicationCursorKeys && t.MouseTracking == 1000 && t.SgrMouse && !t.CursorVisible);

        t = new VtTerminal(10, 2);
        t.Feed("\x1bP1$r0m\x1b\\ok\x1b]52;c;SGVsbG8=\x07!");
        Check("DCS / OSC 52 are ignored", Row(t, 0) == "ok!", Row(t, 0));

        t = new VtTerminal(8, 2);
        t.Feed("a\tb");
        Check("tab stops", t.ScreenLine(0).Cells[8 - 1].Code == 'b' || Row(t, 0) == "a      b", Row(t, 0));

        // Robustness: random bytes must never throw.
        var rnd = new Random(42);
        var junk = new string(Enumerable.Range(0, 200_000).Select(_ => (char)rnd.Next(0, 0x3000)).ToArray());
        try
        {
            t = new VtTerminal(80, 24);
            t.Feed(junk);
            t.Resize(40, 10);
            t.Feed(junk[..5000]);
            Check("random input doesn't crash", true);
        }
        catch (Exception ex) { Check("random input doesn't crash", false, ex.ToString()); }
    }
}
