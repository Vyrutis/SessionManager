using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace SessionManager.Terminal;

/// <summary>
/// Draws a <see cref="VtTerminal"/> and turns keys, text, the mouse and the clipboard into bytes for the server.
/// Box-drawing and block characters are drawn as shapes so borders (htop, mc, tmux) join without gaps.
/// </summary>
public sealed class TerminalView : Control
{
    public const string DefaultFonts = "JetBrains Mono, Cascadia Mono, DejaVu Sans Mono, Noto Sans Mono, Hack, Liberation Mono, Consolas, monospace";
    private const double Pad = 4;

    public VtTerminal Term { get; }
    /// <summary>Bytes typed or pasted by the user, to send to the server.</summary>
    public event Action<string>? Input;
    public event Action<int, int>? GridResized;
    /// <summary>Scroll position changed (for the scroll bar).</summary>
    public event Action? ScrollChanged;

    private TerminalScheme _scheme;
    private FontFamily _font;
    private double _fontSize;
    private Typeface _regular, _bold, _italic, _boldItalic;
    private double _cellW = 8, _cellH = 16, _baseline = 12;
    private int _styleVersion;
    private readonly Dictionary<uint, IImmutableSolidColorBrush> _brushes = new();

    private long? _scrollTop; // absolute number of the top visible line; null = following the output
    private int _redrawPending;

    // Selection, in absolute line numbers.
    private (long Line, int Col)? _selStart, _selEnd;
    private bool _selecting;
    private int _selectMode; // 1 = chars, 2 = words, 3 = lines
    private (long Line, int Col) _selAnchorA, _selAnchorB;
    private (long Line, int Start, int End)? _highlight; // find result
    private int _mouseButtonDown = -1;

    public TerminalView(VtTerminal term, TerminalScheme scheme, string? fontFamily, double fontSize)
    {
        Term = term;
        _scheme = scheme;
        _font = new FontFamily(string.IsNullOrWhiteSpace(fontFamily) ? DefaultFonts : fontFamily + ", " + DefaultFonts);
        _fontSize = fontSize;
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        UpdateMetrics();
    }

    // ───────────── Appearance ─────────────

    public TerminalScheme Scheme
    {
        get => _scheme;
        set { _scheme = value; _brushes.Clear(); _styleVersion++; InvalidateVisual(); }
    }

    public void SetFont(string? family, double size)
    {
        _font = new FontFamily(string.IsNullOrWhiteSpace(family) ? DefaultFonts : family + ", " + DefaultFonts);
        _fontSize = Math.Clamp(size, 6, 72);
        UpdateMetrics();
        UpdateGrid();
        InvalidateVisual();
    }

    public double FontSizeValue => _fontSize;

    private void UpdateMetrics()
    {
        _regular = new Typeface(_font);
        _bold = new Typeface(_font, FontStyle.Normal, FontWeight.Bold);
        _italic = new Typeface(_font, FontStyle.Italic);
        _boldItalic = new Typeface(_font, FontStyle.Italic, FontWeight.Bold);
        var probe = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _regular, _fontSize, Brushes.White);
        _cellW = Math.Max(probe.WidthIncludingTrailingWhitespace / 10, 2);
        _cellH = Math.Max(Math.Ceiling(probe.Height), 4);
        _baseline = probe.Baseline;
        _styleVersion++;
    }

    private IImmutableSolidColorBrush Brush(Color c)
    {
        uint key = c.ToUInt32();
        if (!_brushes.TryGetValue(key, out var b)) _brushes[key] = b = new ImmutableSolidColorBrush(c);
        return b;
    }

    // ───────────── Size ─────────────

    public int Cols => Term.Cols;
    public int Rows => Term.Rows;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateGrid();
    }

    private void UpdateGrid()
    {
        if (Bounds.Width < 10 || Bounds.Height < 10) return;
        int cols = Math.Max((int)((Bounds.Width - Pad * 2) / _cellW), 10);
        int rows = Math.Max((int)((Bounds.Height - Pad * 2) / _cellH), 2);
        if (cols == Term.Cols && rows == Term.Rows) return;
        Term.Resize(cols, rows);
        GridResized?.Invoke(cols, rows);
        ScrollChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Called from any thread after new output.</summary>
    public void RequestRedraw()
    {
        if (Interlocked.Exchange(ref _redrawPending, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _redrawPending, 0);
            InvalidateVisual();
            ScrollChanged?.Invoke();
        }, DispatcherPriority.Render);
    }

    // ───────────── Scrolling ─────────────

    /// <summary>Lines above the screen that can be scrolled to.</summary>
    public int ScrollbackLines { get { lock (Term.Lock) return Term.ScrollbackCount; } }

    /// <summary>0 = at the bottom (live), up to <see cref="ScrollbackLines"/>.</summary>
    public int ScrollOffset
    {
        get
        {
            lock (Term.Lock) return Term.ScrollbackCount - TopIndex();
        }
    }

    /// <summary>Buffer index of the first visible line. Call under the lock.</summary>
    private int TopIndex()
    {
        int live = Term.BufferCount - Term.Rows;
        if (_scrollTop is not long top) return live;
        return (int)Math.Clamp(top - Term.FirstLineNumber, 0, live);
    }

    public void ScrollTo(int offset)
    {
        lock (Term.Lock)
        {
            int max = Term.ScrollbackCount;
            offset = Math.Clamp(offset, 0, max);
            _scrollTop = offset == 0 ? null : Term.FirstLineNumber + (Term.BufferCount - Term.Rows - offset);
        }
        InvalidateVisual();
        ScrollChanged?.Invoke();
    }

    public void ScrollBy(int lines) => ScrollTo(ScrollOffset + lines);

    // ───────────── Drawing ─────────────

    private sealed class LineCache
    {
        public int Version, Style, Cols;
        public readonly List<(Rect R, IBrush B)> Rects = new();
        public readonly List<(FormattedText Text, Point P)> Texts = new();
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        var defaultBg = Term.ReverseVideo ? _scheme.Foreground : _scheme.Background;
        ctx.FillRectangle(Brush(defaultBg), bounds);

        lock (Term.Lock)
        {
            int top = TopIndex();
            long topAbs = Term.FirstLineNumber + top;
            int rows = Term.Rows;
            for (int y = 0; y < rows; y++)
            {
                int index = top + y;
                if (index >= Term.BufferCount) break;
                var line = Term.BufferLine(index);
                double py = Pad + y * _cellH;
                var cache = Build(line);
                using (ctx.PushTransform(Matrix.CreateTranslation(Pad, py)))
                {
                    foreach (var (r, b) in cache.Rects) ctx.FillRectangle(b, r);
                    DrawSelection(ctx, topAbs + y, line);
                    foreach (var (t, p) in cache.Texts) ctx.DrawText(t, p);
                }
            }

            // Cursor
            bool live = _scrollTop == null || TopIndex() == Term.BufferCount - Term.Rows;
            if (Term.CursorVisible && live)
            {
                int cx = Math.Min(Term.CursorX, Term.Cols - 1), cy = Term.CursorY;
                var line = Term.ScreenLine(cy);
                var cell = line.Cells[cx];
                bool wide = cx + 1 < Term.Cols && line.Cells[cx + 1].Code == Cell.WideTail;
                var r = new Rect(Pad + cx * _cellW, Pad + cy * _cellH, _cellW * (wide ? 2 : 1), _cellH);
                var color = _scheme.Cursor;
                int style = Term.CursorStyle;
                if (!IsFocused)
                    ctx.DrawRectangle(null, new Pen(Brush(color), 1), r.Deflate(0.5));
                else if (style is 3 or 4)
                    ctx.FillRectangle(Brush(color), new Rect(r.X, r.Bottom - 2, r.Width, 2));
                else if (style is 5 or 6)
                    ctx.FillRectangle(Brush(color), new Rect(r.X, r.Y, 2, r.Height));
                else
                {
                    ctx.FillRectangle(Brush(color), r);
                    if (cell.Code > ' ')
                    {
                        var bg = Resolve(cell, out _, out _);
                        var t = MakeText(char.ConvertFromUtf32(cell.Code), bg.Bg, cell.Attr);
                        ctx.DrawText(t, new Point(r.X, r.Y));
                    }
                }
            }
        }
    }

    private void DrawSelection(DrawingContext ctx, long abs, Line line)
    {
        if (Selection() is { } s && abs >= s.Start.Line && abs <= s.End.Line)
        {
            int from = abs == s.Start.Line ? s.Start.Col : 0;
            int to = abs == s.End.Line ? s.End.Col : Term.Cols - 1;
            if (to >= from)
                ctx.FillRectangle(Brush(_scheme.Selection), new Rect(from * _cellW, 0, (to - from + 1) * _cellW, _cellH));
        }
        if (_highlight is { } h && h.Line == abs)
            ctx.FillRectangle(Brush(Color.FromArgb(150, 0xE3, 0xA0, 0x08)), new Rect(h.Start * _cellW, 0, (h.End - h.Start) * _cellW, _cellH));
    }

    private (Color Fg, Color Bg) Resolve(Cell cell, out bool bold, out bool italic)
    {
        int fgIndex = cell.Fg;
        bold = (cell.Attr & CellAttr.Bold) != 0;
        italic = (cell.Attr & CellAttr.Italic) != 0;
        if (bold && fgIndex is >= 0 and < 8) fgIndex += 8; // bold shows the bright color, like most terminals
        var fg = _scheme.Resolve(fgIndex, true);
        var bg = _scheme.Resolve(cell.Bg, false);
        if (Term.ReverseVideo)
        {
            if (cell.Fg == Cell.DefaultColor) fg = _scheme.Background;
            if (cell.Bg == Cell.DefaultColor) bg = _scheme.Foreground;
        }
        if ((cell.Attr & CellAttr.Inverse) != 0) (fg, bg) = (bg, fg);
        if ((cell.Attr & CellAttr.Faint) != 0) fg = Mix(fg, bg, 0.5);
        if ((cell.Attr & CellAttr.Hidden) != 0) fg = bg;
        return (fg, bg);
    }

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private FormattedText MakeText(string text, Color fg, CellAttr attr)
    {
        bool bold = (attr & CellAttr.Bold) != 0, italic = (attr & CellAttr.Italic) != 0;
        var face = bold ? (italic ? _boldItalic : _bold) : italic ? _italic : _regular;
        return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, _fontSize, Brush(fg));
    }

    /// <summary>Background rectangles and text runs of a line, rebuilt only when the line or the style changed.</summary>
    private LineCache Build(Line line)
    {
        if (line.RenderCache is LineCache c && c.Version == line.Version && c.Style == _styleVersion && c.Cols == line.Cells.Length
            && !Term.ReverseVideo)
            return c;
        c = new LineCache { Version = line.Version, Style = _styleVersion, Cols = line.Cells.Length };
        var cells = line.Cells;
        var defaultBg = Term.ReverseVideo ? _scheme.Foreground : _scheme.Background;

        // Backgrounds, merged into runs.
        int x = 0;
        while (x < cells.Length)
        {
            var (_, bg) = Resolve(cells[x], out _, out _);
            int start = x;
            while (x + 1 < cells.Length && Resolve(cells[x + 1], out _, out _).Bg == bg) x++;
            if (bg != defaultBg)
                c.Rects.Add((new Rect(Math.Floor(start * _cellW), 0, Math.Ceiling((x - start + 1) * _cellW) + 0.5, _cellH), Brush(bg)));
            x++;
        }

        // Text: ASCII runs with the same style are drawn together; other characters one by one at their cell.
        var run = new StringBuilder();
        int runStart = 0;
        Color runFg = default;
        CellAttr runAttr = CellAttr.None;
        void Flush()
        {
            if (run.Length == 0) return;
            var t = run.ToString();
            if (t.Trim().Length > 0) c.Texts.Add((MakeText(t, runFg, runAttr), new Point(runStart * _cellW, 0)));
            run.Clear();
        }

        for (x = 0; x < cells.Length; x++)
        {
            var cell = cells[x];
            var (fg, _) = Resolve(cell, out _, out _);
            var attr = cell.Attr & (CellAttr.Bold | CellAttr.Italic);
            if ((cell.Attr & (CellAttr.Underline | CellAttr.Strike)) != 0 && cell.Code != Cell.WideTail)
            {
                double w = _cellW * (x + 1 < cells.Length && cells[x + 1].Code == Cell.WideTail ? 2 : 1);
                double thick = Math.Max(1, Math.Round(_fontSize / 14));
                if ((cell.Attr & CellAttr.Underline) != 0)
                    c.Rects.Add((new Rect(x * _cellW, Math.Min(_baseline + thick + 1, _cellH - thick), w, thick), Brush(fg)));
                if ((cell.Attr & CellAttr.Strike) != 0)
                    c.Rects.Add((new Rect(x * _cellW, Math.Round(_cellH / 2), w, thick), Brush(fg)));
            }

            int code = cell.Code;
            if (code is >= 0x20 and < 0x7F)
            {
                if (run.Length > 0 && (fg != runFg || attr != runAttr)) Flush();
                if (run.Length == 0) { runStart = x; runFg = fg; runAttr = attr; }
                run.Append((char)code);
                continue;
            }
            Flush();
            if (code <= 0 || code == Cell.WideTail) continue;
            if (BoxDrawing.TryDraw(code, x * _cellW, _cellW, _cellH, _fontSize, Brush(fg), c.Rects)) continue;
            c.Texts.Add((MakeText(char.ConvertFromUtf32(code), fg, attr), new Point(x * _cellW, 0)));
        }
        Flush();
        line.RenderCache = c;
        return c;
    }

    // ───────────── Keyboard ─────────────

    private string? _altText;
    private long _altTime;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        bool ctrl = mods.HasFlag(KeyModifiers.Control), shift = mods.HasFlag(KeyModifiers.Shift), alt = mods.HasFlag(KeyModifiers.Alt);

        // App shortcuts first.
        if (ctrl && shift && e.Key == Key.C) { _ = CopyAsync(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.V) { _ = PasteAsync(); e.Handled = true; return; }
        if (shift && !ctrl && e.Key == Key.Insert) { _ = PasteAsync(); e.Handled = true; return; }
        if (ctrl && !shift && e.Key == Key.Insert) { _ = CopyAsync(); e.Handled = true; return; }
        if (shift && !ctrl && e.Key is Key.PageUp or Key.PageDown)
        {
            ScrollBy(e.Key == Key.PageUp ? Term.Rows - 1 : -(Term.Rows - 1));
            e.Handled = true;
            return;
        }
        if (ctrl && shift && e.Key is Key.Home or Key.End) { ScrollTo(e.Key == Key.Home ? int.MaxValue : 0); e.Handled = true; return; }
        if (ctrl && shift && e.Key is >= Key.A and <= Key.Z) return; // other Ctrl+Shift+letter shortcuts belong to the window (find, tabs...)

        var seq = KeySequence(e.Key, ctrl, shift, alt);
        if (seq != null)
        {
            Send(seq, fromKeyboard: true);
            e.Handled = true;
            return;
        }

        // Alt+character → ESC + character (the text itself also arrives as TextInput; that one is skipped).
        if (alt && !ctrl && KeyChar(e.Key, shift) is { } ch)
        {
            _altText = ch.ToString();
            _altTime = Environment.TickCount64;
            Send("\x1b" + ch, fromKeyboard: true);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        if (_altText != null && e.Text == _altText && Environment.TickCount64 - _altTime < 100) { _altText = null; e.Handled = true; return; }
        _altText = null;
        Send(e.Text, fromKeyboard: true);
        e.Handled = true;
    }

    private static char? KeyChar(Key key, bool shift)
    {
        if (key is >= Key.A and <= Key.Z) return (char)((shift ? 'A' : 'a') + (key - Key.A));
        if (key is >= Key.D0 and <= Key.D9 && !shift) return (char)('0' + (key - Key.D0));
        return key switch
        {
            Key.OemPeriod => shift ? '>' : '.',
            Key.OemComma => shift ? '<' : ',',
            Key.OemMinus => shift ? '_' : '-',
            Key.OemQuestion => shift ? '?' : '/',
            _ => null,
        };
    }

    /// <summary>Escape sequence for special keys and Ctrl+key; null = let TextInput deliver the character.</summary>
    private string? KeySequence(Key key, bool ctrl, bool shift, bool alt)
    {
        int m = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        string Csi(string final, string? num = null) =>
            m == 1 ? (num == null ? (Term.ApplicationCursorKeys ? "\x1bO" : "\x1b[") + final : $"\x1b[{num}~")
                   : num == null ? $"\x1b[1;{m}{final}" : $"\x1b[{num};{m}~";
        string Ss3(string final) => m == 1 ? "\x1bO" + final : $"\x1b[1;{m}{final}";
        string Alt(string s) => alt ? "\x1b" + s : s;

        switch (key)
        {
            case Key.Enter: return Alt("\r");
            case Key.Back: return Alt(ctrl ? "\x08" : "\x7f");
            case Key.Tab: return shift ? "\x1b[Z" : Alt("\t");
            case Key.Escape: return "\x1b";
            case Key.Up: return Csi("A");
            case Key.Down: return Csi("B");
            case Key.Right: return Csi("C");
            case Key.Left: return Csi("D");
            case Key.Home: return Csi("H");
            case Key.End: return Csi("F");
            case Key.Insert: return Csi("", "2");
            case Key.Delete: return Csi("", "3");
            case Key.PageUp: return Csi("", "5");
            case Key.PageDown: return Csi("", "6");
            case Key.F1: return Ss3("P");
            case Key.F2: return Ss3("Q");
            case Key.F3: return Ss3("R");
            case Key.F4: return Ss3("S");
            case Key.F5: return Csi("", "15");
            case Key.F6: return Csi("", "17");
            case Key.F7: return Csi("", "18");
            case Key.F8: return Csi("", "19");
            case Key.F9: return Csi("", "20");
            case Key.F10: return Csi("", "21");
            case Key.F11: return Csi("", "23");
            case Key.F12: return Csi("", "24");
        }

        if (ctrl && !alt || ctrl && alt && key is >= Key.A and <= Key.Z && !OperatingSystem.IsWindows())
        {
            string? c = key switch
            {
                >= Key.A and <= Key.Z => ((char)(key - Key.A + 1)).ToString(),
                Key.Space or Key.D2 => "\0",
                Key.OemOpenBrackets or Key.D3 => "\x1b",
                Key.OemPipe or Key.OemBackslash or Key.D4 => "\x1c",
                Key.OemCloseBrackets or Key.D5 => "\x1d",
                Key.D6 => "\x1e",
                Key.OemMinus or Key.OemQuestion or Key.D7 => "\x1f",
                Key.D8 => "\x7f",
                _ => null,
            };
            if (c != null) return alt ? "\x1b" + c : c;
        }
        return null;
    }

    /// <summary>Sends user input; typing jumps back to the live output.</summary>
    public void Send(string data, bool fromKeyboard = false)
    {
        if (fromKeyboard && _scrollTop != null) ScrollTo(0);
        if (fromKeyboard) ClearSelection();
        Input?.Invoke(data);
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        if (Term.FocusEvents) Input?.Invoke("\x1b[I");
        InvalidateVisual();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (Term.FocusEvents) Input?.Invoke("\x1b[O");
        InvalidateVisual();
    }

    // ───────────── Clipboard ─────────────

    public async Task CopyAsync()
    {
        var text = SelectedText();
        if (string.IsNullOrEmpty(text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(text);
    }

    public async Task PasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        var text = await clipboard.GetTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        Paste(text);
    }

    public void Paste(string text)
    {
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (Term.BracketedPaste)
            text = "\x1b[200~" + text.Replace("\x1b[201~", "") + "\x1b[201~";
        Send(text, fromKeyboard: true);
    }

    // ───────────── Mouse ─────────────

    private (int Col, int Row) CellAt(Point p) =>
        (Math.Clamp((int)((p.X - Pad) / _cellW), 0, Term.Cols - 1), Math.Clamp((int)((p.Y - Pad) / _cellH), 0, Term.Rows - 1));

    private long AbsLine(int row)
    {
        lock (Term.Lock) return Term.FirstLineNumber + TopIndex() + row;
    }

    private bool ReportsMouse(KeyModifiers mods) => Term.MouseTracking != 0 && !mods.HasFlag(KeyModifiers.Shift);

    private void SendMouse(int button, int col, int row, KeyModifiers mods, bool release, bool motion = false)
    {
        int b = button + (motion ? 32 : 0)
                + (mods.HasFlag(KeyModifiers.Shift) ? 4 : 0) + (mods.HasFlag(KeyModifiers.Alt) ? 8 : 0) + (mods.HasFlag(KeyModifiers.Control) ? 16 : 0);
        if (Term.SgrMouse)
            Input?.Invoke($"\x1b[<{b};{col + 1};{row + 1}{(release ? 'm' : 'M')}");
        else if (col < 223 && row < 223)
            Input?.Invoke("\x1b[M" + (char)(32 + (release ? 3 + (b & ~3) : b)) + (char)(33 + col) + (char)(33 + row));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        var (col, row) = CellAt(point.Position);
        int button = point.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => 0,
            PointerUpdateKind.MiddleButtonPressed => 1,
            PointerUpdateKind.RightButtonPressed => 2,
            _ => -1,
        };
        if (button < 0) return;

        if (ReportsMouse(e.KeyModifiers))
        {
            _mouseButtonDown = button;
            SendMouse(button, col, row, e.KeyModifiers, release: false);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (button == 2) { ShowMenu(); e.Handled = true; return; }
        if (button == 1) { _ = PasteAsync(); e.Handled = true; return; }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && LinkAt(col, row) is { } url)
        {
            OpenLink(url);
            e.Handled = true;
            return;
        }

        long abs = AbsLine(row);
        _selectMode = Math.Clamp(e.ClickCount, 1, 3);
        if (_selectMode == 1 && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selStart != null)
        {
            _selEnd = (abs, col);
        }
        else
        {
            _selAnchorA = _selAnchorB = (abs, col);
            if (_selectMode == 2) (_selAnchorA, _selAnchorB) = WordAt(abs, col);
            else if (_selectMode == 3) { _selAnchorA = (abs, 0); _selAnchorB = (abs, Term.Cols - 1); }
            _selStart = _selAnchorA;
            _selEnd = _selectMode == 1 ? null : _selAnchorB;
        }
        _selecting = true;
        _highlight = null;
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetCurrentPoint(this);
        var (col, row) = CellAt(point.Position);

        if (Term.MouseTracking != 0 && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !_selecting)
        {
            if (_mouseButtonDown >= 0 && Term.MouseTracking >= 1002) SendMouse(_mouseButtonDown, col, row, e.KeyModifiers, false, motion: true);
            else if (_mouseButtonDown < 0 && Term.MouseTracking == 1003) SendMouse(3, col, row, e.KeyModifiers, false, motion: true);
            return;
        }
        if (!_selecting) return;

        // Dragging past the edge scrolls.
        if (point.Position.Y < 0) ScrollBy(1);
        else if (point.Position.Y > Bounds.Height) ScrollBy(-1);

        long abs = AbsLine(row);
        if (_selectMode == 1)
        {
            _selEnd = (abs, col);
        }
        else
        {
            var (a, b) = _selectMode == 2 ? WordAt(abs, col) : ((abs, 0), (abs, Term.Cols - 1));
            if (Compare(a, _selAnchorA) < 0) { _selStart = a; _selEnd = _selAnchorB; }
            else { _selStart = _selAnchorA; _selEnd = b; }
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var (col, row) = CellAt(e.GetCurrentPoint(this).Position);
        if (_mouseButtonDown >= 0)
        {
            SendMouse(_mouseButtonDown, col, row, e.KeyModifiers, release: true);
            _mouseButtonDown = -1;
        }
        if (_selecting)
        {
            _selecting = false;
            // A plain click without dragging selects nothing.
            if (_selectMode == 1 && (_selEnd == null || _selEnd == _selStart)) ClearSelection();
        }
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        int lines = (int)Math.Round(e.Delta.Y * 3);
        if (lines == 0) lines = Math.Sign(e.Delta.Y);
        if (lines == 0) return;
        var (col, row) = CellAt(e.GetCurrentPoint(this).Position);
        if (ReportsMouse(e.KeyModifiers))
        {
            for (int i = 0; i < Math.Min(Math.Abs(lines), 5); i++) SendMouse(lines > 0 ? 64 : 65, col, row, e.KeyModifiers, false);
        }
        else if (Term.AltScreen)
        {
            // Full-screen programs without mouse support (less, man): wheel = arrow keys.
            var seq = Term.ApplicationCursorKeys ? (lines > 0 ? "\x1bOA" : "\x1bOB") : (lines > 0 ? "\x1b[A" : "\x1b[B");
            Input?.Invoke(string.Concat(Enumerable.Repeat(seq, Math.Abs(lines))));
        }
        else ScrollBy(lines);
        e.Handled = true;
    }

    private void ShowMenu()
    {
        var copy = new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift), IsEnabled = _selStart != null && _selEnd != null };
        copy.Click += (_, _) => _ = CopyAsync();
        var paste = new MenuItem { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control | KeyModifiers.Shift) };
        paste.Click += (_, _) => _ = PasteAsync();
        var all = new MenuItem { Header = "Select all" };
        all.Click += (_, _) => SelectAll();
        var clear = new MenuItem { Header = "Clear scrollback" };
        clear.Click += (_, _) => { Term.Feed("\x1b[3J"); ClearSelection(); ScrollTo(0); };
        var items = new List<object> { copy, paste, new Separator(), all, clear };
        if (ExtraMenuItems != null)
        {
            items.Add(new Separator());
            items.AddRange(ExtraMenuItems());
        }
        new ContextMenu { ItemsSource = items }.Open(this);
    }

    /// <summary>More context menu entries (find, reconnect...), added by the tab.</summary>
    public Func<IEnumerable<object>>? ExtraMenuItems { get; set; }

    // ───────────── Selection ─────────────

    private static int Compare((long Line, int Col) a, (long Line, int Col) b) =>
        a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Col.CompareTo(b.Col);

    private ((long Line, int Col) Start, (long Line, int Col) End)? Selection()
    {
        if (_selStart is not { } a || _selEnd is not { } b) return null;
        return Compare(a, b) <= 0 ? (a, b) : (b, a);
    }

    public void ClearSelection()
    {
        if (_selStart == null && _selEnd == null) return;
        _selStart = _selEnd = null;
        InvalidateVisual();
    }

    public void SelectAll()
    {
        lock (Term.Lock)
        {
            _selStart = (Term.FirstLineNumber, 0);
            _selEnd = (Term.FirstLineNumber + Term.BufferCount - 1, Term.Cols - 1);
        }
        InvalidateVisual();
    }

    public string SelectedText() =>
        Selection() is { } s ? Term.GetText(s.Start.Line, s.Start.Col, s.End.Line, s.End.Col) : "";

    private ((long, int), (long, int)) WordAt(long abs, int col)
    {
        lock (Term.Lock)
        {
            int i = (int)(abs - Term.FirstLineNumber);
            if (i < 0 || i >= Term.BufferCount) return ((abs, col), (abs, col));
            var cells = Term.BufferLine(i).Cells;
            static bool Word(int c) => c > ' ' && (char.IsLetterOrDigit((char)Math.Min(c, 0xFFFF)) || "-_.:/~@%+=?&#".Contains((char)c) || c > 0x7F);
            if (col >= cells.Length || !Word(cells[col].Code)) return ((abs, col), (abs, col));
            int a = col, b = col;
            while (a > 0 && Word(cells[a - 1].Code)) a--;
            while (b < cells.Length - 1 && (Word(cells[b + 1].Code) || cells[b + 1].Code == Cell.WideTail)) b++;
            return ((abs, a), (abs, b));
        }
    }

    // ───────────── Links ─────────────

    private static readonly Regex UrlRegex = new(@"https?://[^\s<>""'`]+[^\s<>""'`.,;:!?)\]}]", RegexOptions.Compiled);

    private string? LinkAt(int col, int row)
    {
        string text;
        lock (Term.Lock)
        {
            int i = TopIndex() + row;
            if (i >= Term.BufferCount) return null;
            text = Term.BufferLine(i).Text();
        }
        foreach (Match m in UrlRegex.Matches(text))
            if (col >= m.Index && col < m.Index + m.Length) return m.Value;
        return null;
    }

    public static void OpenLink(string url)
    {
        // Only web links: the text comes from the server.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }
                : new System.Diagnostics.ProcessStartInfo("xdg-open", [uri.AbsoluteUri]) { UseShellExecute = false };
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch { }
    }

    // ───────────── Find ─────────────

    /// <summary>Finds the next match above (or below) the current one, scrolls to it and highlights it.</summary>
    public bool Find(string text, bool backwards)
    {
        if (string.IsNullOrEmpty(text)) { _highlight = null; InvalidateVisual(); return false; }
        lock (Term.Lock)
        {
            long first = Term.FirstLineNumber;
            int count = Term.BufferCount;
            int startIndex = _highlight is { } h ? (int)(h.Line - first) : (backwards ? count : -1);
            int startCol = _highlight?.Start ?? 0;
            for (int step = 0; step < count + 1; step++)
            {
                int i = backwards ? startIndex - step : startIndex + step;
                if (i < 0 || i >= count) continue;
                var lineText = Term.BufferLine(i).Text();
                int pos;
                if (backwards)
                {
                    int limit = step == 0 ? startCol - 1 : lineText.Length - 1;
                    pos = limit < 0 ? -1 : lineText.LastIndexOf(text, Math.Min(limit, lineText.Length - 1), StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    int from = step == 0 ? startCol + 1 : 0;
                    pos = from >= lineText.Length ? -1 : lineText.IndexOf(text, from, StringComparison.OrdinalIgnoreCase);
                }
                if (pos < 0) continue;
                _highlight = (first + i, pos, pos + text.Length);
                // Scroll so the match is visible.
                int top = TopIndex();
                if (i < top || i >= top + Term.Rows)
                {
                    int newTop = Math.Clamp(i - Term.Rows / 2, 0, count - Term.Rows);
                    _scrollTop = newTop == count - Term.Rows ? null : first + newTop;
                }
                InvalidateVisual();
                ScrollChanged?.Invoke();
                return true;
            }
        }
        return false;
    }

    public void ClearFind()
    {
        _highlight = null;
        InvalidateVisual();
    }
}
