using System.Text;

namespace SessionManager.Terminal;

[Flags]
public enum CellAttr : ushort
{
    None = 0,
    Bold = 1,
    Faint = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Hidden = 64,
    Strike = 128,
}

/// <summary>
/// One character cell. <see cref="Code"/> is the Unicode code point (0 = empty, <see cref="WideTail"/> = right half of a
/// wide character). Colors: <see cref="DefaultColor"/>, a palette index 0-255, or <see cref="RgbFlag"/> | 0xRRGGBB.
/// </summary>
public struct Cell
{
    public const int DefaultColor = -1;
    public const int RgbFlag = 1 << 24;
    public const int WideTail = -1;

    public int Code;
    public int Fg;
    public int Bg;
    public CellAttr Attr;

    public static Cell Blank(int bg = DefaultColor) => new() { Code = 0, Fg = DefaultColor, Bg = bg };
}

/// <summary>A row of cells. <see cref="Wrapped"/> marks that the text continues on the next row (for copying).</summary>
public sealed class Line
{
    public Cell[] Cells;
    public bool Wrapped;
    /// <summary>Bumped on every change, so the view can cache what it drew.</summary>
    public int Version;
    public object? RenderCache;

    public Line(int cols, int bg = Cell.DefaultColor)
    {
        Cells = new Cell[cols];
        Clear(0, cols, bg);
    }

    public void Clear(int from, int to, int bg)
    {
        var blank = Cell.Blank(bg);
        for (int i = Math.Max(0, from); i < Math.Min(to, Cells.Length); i++) Cells[i] = blank;
        Version++;
    }

    public void Resize(int cols)
    {
        if (cols == Cells.Length) return;
        var old = Cells;
        Cells = new Cell[cols];
        Array.Copy(old, Cells, Math.Min(cols, old.Length));
        for (int i = old.Length; i < cols; i++) Cells[i] = Cell.Blank();
        Version++;
    }

    public string Text(int from = 0, int to = int.MaxValue)
    {
        var sb = new StringBuilder();
        for (int i = Math.Max(0, from); i < Math.Min(to, Cells.Length); i++)
        {
            int c = Cells[i].Code;
            if (c == Cell.WideTail) continue;
            if (c == 0) sb.Append(' ');
            else sb.Append(char.ConvertFromUtf32(c));
        }
        return sb.ToString();
    }
}

/// <summary>Lines in a ring: the scrollback followed by the visible screen (the last <c>rows</c> lines).</summary>
public sealed class LineRing
{
    private Line[] _buf;
    private int _start;

    public LineRing(int capacity) => _buf = new Line[Math.Max(capacity, 2)];

    public int Count { get; private set; }
    public int Capacity => _buf.Length;
    /// <summary>How many lines fell off the top since the start; keeps absolute line numbers stable.</summary>
    public long Dropped { get; private set; }

    public Line this[int i]
    {
        get => _buf[(_start + i) % _buf.Length];
        set => _buf[(_start + i) % _buf.Length] = value;
    }

    public void Add(Line line)
    {
        if (Count < _buf.Length) { _buf[(_start + Count) % _buf.Length] = line; Count++; }
        else { _buf[_start] = line; _start = (_start + 1) % _buf.Length; Dropped++; }
    }

    public void RemoveLast() { if (Count > 0) Count--; }

    public void Clear() { Count = 0; _start = 0; }
}

/// <summary>
/// An xterm-compatible terminal: parses the byte stream from the server (VT100/VT220/xterm control sequences, UTF-8,
/// 256 and 24-bit colors, scroll regions, alternate screen, mouse and bracketed-paste modes) into a grid of cells.
/// Thread-safe: <see cref="Feed"/> runs on the reader thread and the view reads under <see cref="Lock"/>.
/// </summary>
public sealed class VtTerminal
{
    public readonly object Lock = new();

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int ScrollbackLimit { get; }

    private readonly LineRing _normal;
    private Line[]? _alt;
    public bool AltScreen => _alt != null;

    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    private bool _wrapNext;

    // Current drawing attributes
    private int _fg = Cell.DefaultColor, _bg = Cell.DefaultColor;
    private CellAttr _attr;

    // Modes
    public bool CursorVisible { get; private set; } = true;
    public bool ApplicationCursorKeys { get; private set; }
    public bool ApplicationKeypad { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool ReverseVideo { get; private set; }
    public bool FocusEvents { get; private set; }
    /// <summary>0 = off, 1000 = clicks, 1002 = clicks and drags, 1003 = all motion.</summary>
    public int MouseTracking { get; private set; }
    public bool SgrMouse { get; private set; }
    /// <summary>DECSCUSR: 0/1/2 block, 3/4 underline, 5/6 bar.</summary>
    public int CursorStyle { get; private set; }
    private bool _autoWrap = true, _originMode, _insertMode, _newLineMode;

    private int _top, _bottom; // scroll region, inclusive
    private bool[] _tabs = [];

    // Character sets: G0..G3, 'B' = ASCII, '0' = DEC special graphics.
    private readonly char[] _charsets = ['B', 'B', 'B', 'B'];
    private int _gl; // 0 = G0 active, 1 = G1 (after SO)

    private struct Saved
    {
        public int X, Y, Fg, Bg, Gl;
        public CellAttr Attr;
        public bool Origin, WrapNext;
        public char[] Charsets;
    }
    private Saved? _savedNormal, _savedAlt;

    /// <summary>Bytes to send back to the server (answers to status queries). Called on the feeding thread.</summary>
    public event Action<string>? Reply;
    public event Action<string>? TitleChanged;
    public event Action? Bell;

    /// <summary>Bumped whenever anything visible changes.</summary>
    public int Version { get; private set; }

    public VtTerminal(int cols, int rows, int scrollback = 10000)
    {
        Cols = Math.Max(cols, 2);
        Rows = Math.Max(rows, 1);
        ScrollbackLimit = scrollback;
        _normal = new LineRing(scrollback + 500);
        for (int i = 0; i < Rows; i++) _normal.Add(new Line(Cols));
        _top = 0;
        _bottom = Rows - 1;
        ResetTabs();
    }

    // ───────────── Lines ─────────────

    /// <summary>Scrollback lines above the screen (0 on the alternate screen).</summary>
    public int ScrollbackCount => _alt != null ? 0 : _normal.Count - Rows;

    /// <summary>Absolute number of the first scrollback line, so a selection survives lines falling off the top.</summary>
    public long FirstLineNumber => _alt != null ? 0 : _normal.Dropped;

    /// <summary>A screen line, 0 = top of the screen.</summary>
    public Line ScreenLine(int y) => _alt != null ? _alt[y] : _normal[_normal.Count - Rows + y];

    private void SetScreenLine(int y, Line line)
    {
        if (_alt != null) _alt[y] = line;
        else _normal[_normal.Count - Rows + y] = line;
    }

    /// <summary>Line by index in scrollback + screen (0 = oldest scrollback line).</summary>
    public Line BufferLine(int i) => _alt != null ? _alt[i] : _normal[i];

    public int BufferCount => _alt != null ? Rows : _normal.Count;

    // ───────────── Input ─────────────

    private enum State { Ground, Escape, EscapeIntermediate, Csi, Osc, OscEscape, String, StringEscape, Charset }

    private State _state;
    private readonly StringBuilder _osc = new();
    private readonly List<int> _params = new();
    private readonly List<List<int>?> _subParams = new();
    private int _curParam = -1;
    private bool _paramHasValue;
    private char _prefix, _intermediate;
    private int _charsetSlot;
    private char _pendingHigh;

    public void Feed(string text)
    {
        lock (Lock)
        {
            foreach (char ch in text)
            {
                int code = ch;
                if (char.IsHighSurrogate(ch)) { _pendingHigh = ch; continue; }
                if (char.IsLowSurrogate(ch))
                {
                    if (_pendingHigh == 0) continue;
                    code = char.ConvertToUtf32(_pendingHigh, ch);
                }
                _pendingHigh = '\0';
                Step(code);
            }
            Version++;
        }
    }

    private void Step(int c)
    {
        // CAN / SUB abort any sequence; ESC starts a new one (except where it may end a string).
        if (c is 0x18 or 0x1A) { _state = State.Ground; return; }

        switch (_state)
        {
            case State.Ground:
                if (c == 0x1B) { _state = State.Escape; _intermediate = '\0'; return; }
                if (c < 0x20 || c == 0x7F) { Control(c); return; }
                if (c is >= 0x80 and < 0xA0) return; // C1 controls: not used in UTF-8 streams
                Print(c);
                return;

            case State.Escape:
                if (c < 0x20) { if (c == 0x1B) return; Control(c); return; }
                Escape(c);
                return;

            case State.EscapeIntermediate:
                // ESC # 8 etc.
                if (_intermediate == '#' && c == '8') AlignmentTest();
                _state = State.Ground;
                return;

            case State.Charset:
                _charsets[_charsetSlot] = (char)c;
                _state = State.Ground;
                return;

            case State.Csi:
                CsiStep(c);
                return;

            case State.Osc:
                if (c == 0x07) { OscDone(); return; }
                if (c == 0x1B) { _state = State.OscEscape; return; }
                if (_osc.Length < 8192) _osc.Append(char.ConvertFromUtf32(c));
                return;

            case State.OscEscape:
                if (c == '\\') OscDone();
                else { _state = State.Escape; Step(c); }
                return;

            case State.String: // DCS / SOS / PM / APC: ignored up to ST
                if (c == 0x1B) _state = State.StringEscape;
                else if (c == 0x07) _state = State.Ground;
                return;

            case State.StringEscape:
                _state = c == '\\' ? State.Ground : State.String;
                return;
        }
    }

    private void Control(int c)
    {
        switch (c)
        {
            case 0x07: Bell?.Invoke(); break;
            case 0x08: // BS
                if (_wrapNext) _wrapNext = false;
                else if (CursorX > 0) CursorX--;
                break;
            case 0x09: TabForward(1); break;
            case 0x0A: case 0x0B: case 0x0C:
                Index();
                if (_newLineMode) CursorX = 0;
                break;
            case 0x0D: CursorX = 0; _wrapNext = false; break;
            case 0x0E: _gl = 1; break; // SO
            case 0x0F: _gl = 0; break; // SI
        }
    }

    private void Escape(int c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[':
                _state = State.Csi;
                _params.Clear();
                _subParams.Clear();
                _curSubs = null;
                _curParam = -1;
                _paramHasValue = false;
                _prefix = '\0';
                _intermediate = '\0';
                break;
            case ']': _state = State.Osc; _osc.Clear(); break;
            case 'P': case 'X': case '^': case '_': _state = State.String; break;
            case '(': case ')': case '*': case '+':
                _charsetSlot = c switch { '(' => 0, ')' => 1, '*' => 2, _ => 3 };
                _state = State.Charset;
                break;
            case '#': case ' ': case '%':
                _intermediate = (char)c;
                _state = State.EscapeIntermediate;
                break;
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': Index(); break;
            case 'E': CursorX = 0; _wrapNext = false; Index(); break;
            case 'M': ReverseIndex(); break;
            case 'H': if (CursorX < Cols) _tabs[CursorX] = true; break;
            case 'c': FullReset(); break;
            case '=': ApplicationKeypad = true; break;
            case '>': ApplicationKeypad = false; break;
            case '\\': break; // ST on its own
        }
    }

    // ───────────── CSI ─────────────

    private void CsiStep(int c)
    {
        if (c is >= '0' and <= '9')
        {
            if (_curParam < 0) _curParam = 0;
            _curParam = Math.Min(_curParam * 10 + (c - '0'), 100000);
            _paramHasValue = true;
            return;
        }
        if (c == ';') { PushParam(); return; }
        if (c == ':')
        {
            // Sub-parameters (e.g. 38:2::r:g:b) stay with their parameter.
            if (_curSubs == null) { _curSubs = new List<int>(); _curMain = _paramHasValue ? _curParam : -1; }
            else _curSubs.Add(_paramHasValue ? _curParam : -1);
            _curParam = -1;
            _paramHasValue = false;
            return;
        }
        if (c is '?' or '>' or '<' or '=') { _prefix = (char)c; return; }
        if (c is >= 0x20 and <= 0x2F) { _intermediate = (char)c; return; }
        if (c < 0x20) { if (c == 0x1B) { _state = State.Escape; _intermediate = '\0'; } else Control(c); return; }
        if (c is >= 0x40 and <= 0x7E)
        {
            PushParam();
            _state = State.Ground;
            try { Csi((char)c); } catch (ArgumentOutOfRangeException) { }
            return;
        }
        _state = State.Ground; // anything else: malformed
    }

    private List<int>? _curSubs;
    private int _curMain;

    private void PushParam()
    {
        int value = _paramHasValue ? _curParam : -1;
        if (_curSubs != null)
        {
            _curSubs.Add(value);
            _params.Add(_curMain);
            _subParams.Add(_curSubs);
            _curSubs = null;
        }
        else if (_params.Count < 32)
        {
            _params.Add(value);
            _subParams.Add(null);
        }
        _curParam = -1;
        _paramHasValue = false;
    }

    private int P(int i, int def) => i < _params.Count && _params[i] > 0 ? _params[i] : def;
    private int P0(int i) => i < _params.Count && _params[i] >= 0 ? _params[i] : 0;

    private void Csi(char final)
    {
        if (_prefix == '?') { PrivateCsi(final); return; }
        if (_prefix == '>')
        {
            if (final == 'c') Reply?.Invoke("\x1b[>0;276;0c"); // DA2
            return;
        }
        if (_prefix != '\0') return;

        if (_intermediate == ' ' && final == 'q') { CursorStyle = P0(0); return; }
        if (_intermediate == '!' && final == 'p') { SoftReset(); return; }
        if (_intermediate != '\0') return;

        switch (final)
        {
            case '@': InsertChars(P(0, 1)); break;
            case 'A': MoveCursor(CursorX, Math.Max(CursorY - P(0, 1), CursorY >= _top ? _top : 0)); break;
            case 'B': case 'e': MoveCursor(CursorX, Math.Min(CursorY + P(0, 1), CursorY <= _bottom ? _bottom : Rows - 1)); break;
            case 'C': case 'a': MoveCursor(CursorX + P(0, 1), CursorY); break;
            case 'D': MoveCursor(CursorX - P(0, 1), CursorY); break;
            case 'E': MoveCursor(0, Math.Min(CursorY + P(0, 1), CursorY <= _bottom ? _bottom : Rows - 1)); break;
            case 'F': MoveCursor(0, Math.Max(CursorY - P(0, 1), CursorY >= _top ? _top : 0)); break;
            case 'G': case '`': MoveCursor(P(0, 1) - 1, CursorY); break;
            case 'H': case 'f': SetCursor(P(1, 1) - 1, P(0, 1) - 1); break;
            case 'I': TabForward(P(0, 1)); break;
            case 'J': EraseDisplay(P0(0)); break;
            case 'K': EraseLine(P0(0)); break;
            case 'L': InsertLines(P(0, 1)); break;
            case 'M': DeleteLines(P(0, 1)); break;
            case 'P': DeleteChars(P(0, 1)); break;
            case 'S': ScrollUp(_top, _bottom, P(0, 1)); break;
            case 'T': if (_params.Count <= 1) ScrollDown(_top, _bottom, P(0, 1)); break;
            case 'X': EraseChars(P(0, 1)); break;
            case 'Z': TabBackward(P(0, 1)); break;
            case 'b': Repeat(P(0, 1)); break;
            case 'c': if (P0(0) == 0) Reply?.Invoke("\x1b[?62;22c"); break; // DA1: VT220 with ANSI color
            case 'd': SetCursor(CursorX, P(0, 1) - 1); break;
            case 'g':
                if (P0(0) == 0 && CursorX < Cols) _tabs[CursorX] = false;
                else if (P0(0) == 3) Array.Clear(_tabs);
                break;
            case 'h': case 'l':
                bool on = final == 'h';
                foreach (var p in _params)
                    if (p == 4) _insertMode = on;
                    else if (p == 20) _newLineMode = on;
                break;
            case 'm': Sgr(); break;
            case 'n':
                if (P0(0) == 5) Reply?.Invoke("\x1b[0n");
                else if (P0(0) == 6) Reply?.Invoke($"\x1b[{CursorY - (_originMode ? _top : 0) + 1};{Math.Min(CursorX, Cols - 1) + 1}R");
                break;
            case 'r':
                int top = P(0, 1) - 1, bottom = P(1, Rows) - 1;
                bottom = Math.Min(bottom, Rows - 1);
                if (top < bottom)
                {
                    _top = top;
                    _bottom = bottom;
                    SetCursor(0, 0);
                }
                break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 't':
                if (P0(0) == 18) Reply?.Invoke($"\x1b[8;{Rows};{Cols}t");
                break;
        }
    }

    private void PrivateCsi(char final)
    {
        switch (final)
        {
            case 'h': case 'l':
                bool on = final == 'h';
                foreach (var p in _params) SetPrivateMode(p, on);
                break;
            case 'J': EraseDisplay(P0(0)); break;
            case 'K': EraseLine(P0(0)); break;
            case 'n':
                if (P0(0) == 6) Reply?.Invoke($"\x1b[?{CursorY - (_originMode ? _top : 0) + 1};{Math.Min(CursorX, Cols - 1) + 1}R");
                break;
        }
    }

    private void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: ApplicationCursorKeys = on; break;
            case 5: ReverseVideo = on; break;
            case 6: _originMode = on; SetCursor(0, 0); break;
            case 7: _autoWrap = on; if (!on) _wrapNext = false; break;
            case 25: CursorVisible = on; break;
            case 47: case 1047:
                if (on) EnterAlt(clear: mode == 1047); else LeaveAlt();
                break;
            case 1048: if (on) SaveCursor(); else RestoreCursor(); break;
            case 1049:
                if (on) { SaveCursor(); EnterAlt(clear: true); }
                else { LeaveAlt(); RestoreCursor(); }
                break;
            case 9: case 1000: case 1002: case 1003:
                MouseTracking = on ? (mode == 9 ? 1000 : mode) : 0;
                break;
            case 1004: FocusEvents = on; break;
            case 1006: SgrMouse = on; break;
            case 2004: BracketedPaste = on; break;
        }
    }

    // ───────────── SGR (colors and attributes) ─────────────

    private void Sgr()
    {
        if (_params.Count == 0) { ResetAttrs(); return; }
        for (int i = 0; i < _params.Count; i++)
        {
            int p = _params[i] < 0 ? 0 : _params[i];
            var sub = _subParams[i];
            switch (p)
            {
                case 0: ResetAttrs(); break;
                case 1: _attr |= CellAttr.Bold; break;
                case 2: _attr |= CellAttr.Faint; break;
                case 3: _attr |= CellAttr.Italic; break;
                case 4:
                    if (sub is { Count: > 0 } && sub[0] == 0) _attr &= ~CellAttr.Underline;
                    else _attr |= CellAttr.Underline;
                    break;
                case 5: case 6: _attr |= CellAttr.Blink; break;
                case 7: _attr |= CellAttr.Inverse; break;
                case 8: _attr |= CellAttr.Hidden; break;
                case 9: _attr |= CellAttr.Strike; break;
                case 21: _attr |= CellAttr.Underline; break;
                case 22: _attr &= ~(CellAttr.Bold | CellAttr.Faint); break;
                case 23: _attr &= ~CellAttr.Italic; break;
                case 24: _attr &= ~CellAttr.Underline; break;
                case 25: _attr &= ~CellAttr.Blink; break;
                case 27: _attr &= ~CellAttr.Inverse; break;
                case 28: _attr &= ~CellAttr.Hidden; break;
                case 29: _attr &= ~CellAttr.Strike; break;
                case >= 30 and <= 37: _fg = p - 30; break;
                case 39: _fg = Cell.DefaultColor; break;
                case >= 40 and <= 47: _bg = p - 40; break;
                case 49: _bg = Cell.DefaultColor; break;
                case >= 90 and <= 97: _fg = p - 90 + 8; break;
                case >= 100 and <= 107: _bg = p - 100 + 8; break;
                case 38: case 48: case 58:
                    int color = ExtendedColor(ref i, sub);
                    if (p == 38 && color != int.MinValue) _fg = color;
                    else if (p == 48 && color != int.MinValue) _bg = color;
                    break;
            }
        }
    }

    /// <summary>38;5;n / 38;2;r;g;b and the colon forms 38:5:n / 38:2::r:g:b / 38:2:r:g:b.</summary>
    private int ExtendedColor(ref int i, List<int>? sub)
    {
        if (sub is { Count: > 0 })
        {
            if (sub[0] == 5 && sub.Count >= 2) return Math.Clamp(sub[1], 0, 255);
            if (sub[0] == 2)
            {
                var rgb = sub.Count >= 5 ? sub.Skip(sub.Count - 3).ToList() : sub.Skip(1).ToList();
                if (rgb.Count >= 3) return Rgb(rgb[0], rgb[1], rgb[2]);
            }
            return int.MinValue;
        }
        if (i + 1 >= _params.Count) return int.MinValue;
        int kind = _params[i + 1];
        if (kind == 5 && i + 2 < _params.Count) { int n = _params[i + 2]; i += 2; return Math.Clamp(n, 0, 255); }
        if (kind == 2 && i + 4 < _params.Count) { int r = _params[i + 2], g = _params[i + 3], b = _params[i + 4]; i += 4; return Rgb(r, g, b); }
        i = _params.Count;
        return int.MinValue;
    }

    private static int Rgb(int r, int g, int b) =>
        Cell.RgbFlag | (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);

    private void ResetAttrs()
    {
        _fg = _bg = Cell.DefaultColor;
        _attr = CellAttr.None;
    }

    // ───────────── OSC ─────────────

    private void OscDone()
    {
        _state = State.Ground;
        var s = _osc.ToString();
        int semi = s.IndexOf(';');
        if (semi <= 0) return;
        var kind = s[..semi];
        if (kind is "0" or "2") TitleChanged?.Invoke(s[(semi + 1)..]);
        // OSC 52 (clipboard) and color queries are ignored on purpose: the server must not read or set the clipboard.
    }

    // ───────────── Printing ─────────────

    private static readonly string DecGraphics = "◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";

    private void Print(int c)
    {
        if (c < 0x80 && _charsets[_gl] == '0')
        {
            if (c is >= 0x60 and <= 0x7E) c = DecGraphics[c - 0x60];
            else if (c == 0x5F) c = ' ';
        }
        int width = CharWidth.Of(c);
        if (width == 0)
        {
            // Combining mark: attach to the previous cell isn't supported; zero-width chars are dropped.
            return;
        }

        if (_wrapNext && _autoWrap)
        {
            ScreenLine(CursorY).Wrapped = true;
            CursorX = 0;
            Index();
        }
        _wrapNext = false;

        if (width == 2 && CursorX >= Cols - 1)
        {
            if (!_autoWrap) { CursorX = Math.Max(Cols - 2, 0); }
            else
            {
                var l = ScreenLine(CursorY);
                l.Clear(CursorX, Cols, _bg);
                l.Wrapped = true;
                CursorX = 0;
                Index();
            }
        }

        var line = ScreenLine(CursorY);
        if (_insertMode) ShiftRight(line, CursorX, width);
        FixWideNeighbours(line, CursorX, width);
        line.Cells[CursorX] = new Cell { Code = c, Fg = _fg, Bg = _bg, Attr = _attr };
        if (width == 2 && CursorX + 1 < Cols) line.Cells[CursorX + 1] = new Cell { Code = Cell.WideTail, Fg = _fg, Bg = _bg, Attr = _attr };
        line.Version++;
        _lastPrinted = c;

        CursorX += width;
        if (CursorX >= Cols)
        {
            CursorX = Cols - 1;
            _wrapNext = _autoWrap;
        }
    }

    private int _lastPrinted = ' ';

    private void Repeat(int n)
    {
        n = Math.Min(n, Cols * Rows);
        for (int i = 0; i < n; i++) Print(_lastPrinted);
    }

    /// <summary>Overwriting half of a wide character blanks its other half.</summary>
    private static void FixWideNeighbours(Line line, int x, int width)
    {
        var cells = line.Cells;
        if (x < cells.Length && cells[x].Code == Cell.WideTail && x > 0) cells[x - 1].Code = 0;
        int end = x + width - 1;
        if (end + 1 < cells.Length && cells[end + 1].Code == Cell.WideTail) cells[end + 1].Code = 0;
    }

    private void ShiftRight(Line line, int x, int n)
    {
        var cells = line.Cells;
        for (int i = cells.Length - 1; i >= x + n; i--) cells[i] = cells[i - n];
        for (int i = x; i < Math.Min(x + n, cells.Length); i++) cells[i] = Cell.Blank(_bg);
        line.Version++;
    }

    // ───────────── Cursor ─────────────

    private void MoveCursor(int x, int y)
    {
        CursorX = Math.Clamp(x, 0, Cols - 1);
        CursorY = Math.Clamp(y, 0, Rows - 1);
        _wrapNext = false;
    }

    /// <summary>CUP: relative to the scroll region in origin mode.</summary>
    private void SetCursor(int x, int y)
    {
        if (_originMode) MoveCursor(x, Math.Clamp(y + _top, _top, _bottom));
        else MoveCursor(x, y);
    }

    private void TabForward(int n)
    {
        for (int k = 0; k < n && CursorX < Cols - 1; k++)
        {
            int x = CursorX + 1;
            while (x < Cols - 1 && !_tabs[x]) x++;
            CursorX = x;
        }
    }

    private void TabBackward(int n)
    {
        for (int k = 0; k < n && CursorX > 0; k++)
        {
            int x = CursorX - 1;
            while (x > 0 && !_tabs[x]) x--;
            CursorX = x;
        }
        _wrapNext = false;
    }

    private void ResetTabs()
    {
        _tabs = new bool[Cols];
        for (int i = 8; i < Cols; i += 8) _tabs[i] = true;
    }

    private void SaveCursor()
    {
        var s = new Saved
        {
            X = CursorX, Y = CursorY, Fg = _fg, Bg = _bg, Attr = _attr, Origin = _originMode, WrapNext = _wrapNext,
            Gl = _gl, Charsets = (char[])_charsets.Clone(),
        };
        if (_alt != null) _savedAlt = s; else _savedNormal = s;
    }

    private void RestoreCursor()
    {
        var saved = _alt != null ? _savedAlt : _savedNormal;
        if (saved is not { } s)
        {
            MoveCursor(0, 0);
            ResetAttrs();
            return;
        }
        CursorX = Math.Clamp(s.X, 0, Cols - 1);
        CursorY = Math.Clamp(s.Y, 0, Rows - 1);
        _fg = s.Fg; _bg = s.Bg; _attr = s.Attr; _originMode = s.Origin; _wrapNext = s.WrapNext; _gl = s.Gl;
        Array.Copy(s.Charsets, _charsets, 4);
    }

    // ───────────── Scrolling ─────────────

    private void Index()
    {
        if (CursorY == _bottom) ScrollUp(_top, _bottom, 1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    private void ReverseIndex()
    {
        if (CursorY == _top) ScrollDown(_top, _bottom, 1);
        else if (CursorY > 0) CursorY--;
    }

    private void ScrollUp(int top, int bottom, int n)
    {
        n = Math.Clamp(n, 0, bottom - top + 1);
        if (n == 0) return;
        if (_alt == null && top == 0 && bottom == Rows - 1)
        {
            // Whole screen: the top lines go into the scrollback.
            for (int i = 0; i < n; i++) _normal.Add(new Line(Cols, _bg));
            return;
        }
        for (int y = top; y <= bottom; y++)
            SetScreenLine(y, y + n <= bottom ? ScreenLine(y + n) : new Line(Cols, _bg));
        for (int y = top; y <= bottom; y++) ScreenLine(y).Version++;
    }

    private void ScrollDown(int top, int bottom, int n)
    {
        n = Math.Clamp(n, 0, bottom - top + 1);
        if (n == 0) return;
        for (int y = bottom; y >= top; y--)
            SetScreenLine(y, y - n >= top ? ScreenLine(y - n) : new Line(Cols, _bg));
        for (int y = top; y <= bottom; y++) ScreenLine(y).Version++;
    }

    private void InsertLines(int n)
    {
        if (CursorY < _top || CursorY > _bottom) return;
        ScrollDown(CursorY, _bottom, n);
        CursorX = 0;
        _wrapNext = false;
    }

    private void DeleteLines(int n)
    {
        if (CursorY < _top || CursorY > _bottom) return;
        ScrollUpRegionOnly(CursorY, _bottom, n);
        CursorX = 0;
        _wrapNext = false;
    }

    /// <summary>Like <see cref="ScrollUp"/> but never moves lines into the scrollback.</summary>
    private void ScrollUpRegionOnly(int top, int bottom, int n)
    {
        n = Math.Clamp(n, 0, bottom - top + 1);
        for (int y = top; y <= bottom; y++)
            SetScreenLine(y, y + n <= bottom ? ScreenLine(y + n) : new Line(Cols, _bg));
        for (int y = top; y <= bottom; y++) ScreenLine(y).Version++;
    }

    // ───────────── Erasing / editing ─────────────

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (int y = CursorY + 1; y < Rows; y++) ResetLine(y);
                break;
            case 1:
                EraseLine(1);
                for (int y = 0; y < CursorY; y++) ResetLine(y);
                break;
            case 2:
                // Like xterm: the old screen goes into the scrollback when it isn't empty, so "clear" keeps history.
                if (_alt == null && Enumerable.Range(0, Rows).Any(y => !IsBlank(ScreenLine(y))))
                {
                    int last = Rows - 1;
                    while (last > 0 && IsBlank(ScreenLine(last))) last--;
                    for (int i = 0; i <= last; i++) _normal.Add(new Line(Cols, _bg));
                    for (int y = 0; y < Rows; y++) ResetLine(y);
                }
                else for (int y = 0; y < Rows; y++) ResetLine(y);
                break;
            case 3:
                if (_alt == null)
                {
                    var screen = Enumerable.Range(0, Rows).Select(ScreenLine).ToList();
                    _normal.Clear();
                    foreach (var l in screen) _normal.Add(l);
                }
                break;
        }
    }

    private void ResetLine(int y)
    {
        var l = ScreenLine(y);
        l.Clear(0, Cols, _bg);
        l.Wrapped = false;
    }

    private static bool IsBlank(Line l)
    {
        foreach (var c in l.Cells)
            if ((c.Code != 0 && c.Code != ' ') || c.Bg != Cell.DefaultColor) return false;
        return true;
    }

    private void EraseLine(int mode)
    {
        var l = ScreenLine(CursorY);
        switch (mode)
        {
            case 0: l.Clear(CursorX, Cols, _bg); l.Wrapped = false; break;
            case 1: l.Clear(0, CursorX + 1, _bg); break;
            case 2: l.Clear(0, Cols, _bg); l.Wrapped = false; break;
        }
    }

    private void EraseChars(int n)
    {
        ScreenLine(CursorY).Clear(CursorX, CursorX + n, _bg);
        _wrapNext = false;
    }

    private void InsertChars(int n)
    {
        ShiftRight(ScreenLine(CursorY), CursorX, Math.Min(n, Cols - CursorX));
        _wrapNext = false;
    }

    private void DeleteChars(int n)
    {
        var l = ScreenLine(CursorY);
        var cells = l.Cells;
        n = Math.Min(n, Cols - CursorX);
        for (int i = CursorX; i < Cols; i++) cells[i] = i + n < Cols ? cells[i + n] : Cell.Blank(_bg);
        l.Version++;
        _wrapNext = false;
    }

    private void AlignmentTest()
    {
        for (int y = 0; y < Rows; y++)
        {
            var l = ScreenLine(y);
            for (int x = 0; x < Cols; x++) l.Cells[x] = new Cell { Code = 'E', Fg = Cell.DefaultColor, Bg = Cell.DefaultColor };
            l.Version++;
        }
        MoveCursor(0, 0);
    }

    // ───────────── Screens and reset ─────────────

    private void EnterAlt(bool clear)
    {
        if (_alt == null)
        {
            _alt = new Line[Rows];
            for (int i = 0; i < Rows; i++) _alt[i] = new Line(Cols);
        }
        else if (clear)
            for (int i = 0; i < Rows; i++) _alt[i] = new Line(Cols);
        _top = 0;
        _bottom = Rows - 1;
    }

    private void LeaveAlt()
    {
        if (_alt == null) return;
        _alt = null;
        _top = 0;
        _bottom = Rows - 1;
        for (int y = 0; y < Rows; y++) ScreenLine(y).Version++;
    }

    private void SoftReset()
    {
        CursorVisible = true;
        _insertMode = _originMode = false;
        _autoWrap = true;
        ApplicationCursorKeys = false;
        _top = 0;
        _bottom = Rows - 1;
        ResetAttrs();
        _gl = 0;
        for (int i = 0; i < 4; i++) _charsets[i] = 'B';
        _savedNormal = _savedAlt = null;
    }

    private void FullReset()
    {
        SoftReset();
        LeaveAlt();
        _normal.Clear();
        for (int i = 0; i < Rows; i++) _normal.Add(new Line(Cols));
        MoveCursor(0, 0);
        ResetTabs();
        MouseTracking = 0;
        SgrMouse = BracketedPaste = FocusEvents = ReverseVideo = ApplicationKeypad = false;
        CursorStyle = 0;
    }

    // ───────────── Resize ─────────────

    /// <summary>Changes the screen size. Lines are not re-wrapped; the screen grows into / shrinks into the scrollback.</summary>
    public void Resize(int cols, int rows)
    {
        cols = Math.Max(cols, 2);
        rows = Math.Max(rows, 1);
        lock (Lock)
        {
            if (cols == Cols && rows == Rows) return;

            if (cols != Cols)
            {
                for (int i = 0; i < _normal.Count; i++) _normal[i].Resize(cols);
                if (_alt != null) foreach (var l in _alt) l.Resize(cols);
                var oldTabs = _tabs;
                Cols = cols;
                ResetTabs();
                Array.Copy(oldTabs, _tabs, Math.Min(oldTabs.Length, _tabs.Length));
            }

            if (rows != Rows)
            {
                bool alt = _alt != null;
                // The normal screen's cursor; on the alternate screen the normal one is the saved cursor.
                int cursor = alt ? (_savedNormal?.Y ?? Rows - 1) : CursorY;
                int shift = 0;
                if (rows < Rows)
                {
                    // Drop empty lines below the cursor first, then let the top of the screen become scrollback.
                    int remove = Rows - rows;
                    int count = Rows;
                    while (remove > 0 && count - 1 > cursor && IsBlank(_normal[_normal.Count - 1]))
                    {
                        _normal.RemoveLast();
                        count--;
                        remove--;
                    }
                    shift = -remove;
                }
                else
                {
                    // Bring lines back from the scrollback, then add empty ones at the bottom.
                    int add = rows - Rows;
                    int fromHistory = Math.Min(add, Math.Max(_normal.Count - Rows, 0));
                    shift = fromHistory;
                    for (int i = fromHistory; i < add; i++) _normal.Add(new Line(Cols));
                }

                if (alt)
                {
                    var lines = new Line[rows];
                    for (int i = 0; i < rows; i++) lines[i] = i < _alt!.Length ? _alt[i] : new Line(Cols);
                    _alt = lines;
                    if (_savedNormal is { } s) { s.Y = Math.Clamp(s.Y + shift, 0, rows - 1); _savedNormal = s; }
                }
                else CursorY += shift;
                Rows = rows;
            }

            CursorX = Math.Min(CursorX, Cols - 1);
            CursorY = Math.Clamp(CursorY, 0, Rows - 1);
            _top = 0;
            _bottom = Rows - 1;
            _wrapNext = false;
            Version++;
        }
    }

    /// <summary>Text of the whole buffer between two positions (absolute line numbers), joining wrapped lines.</summary>
    public string GetText(long startLine, int startCol, long endLine, int endCol)
    {
        lock (Lock)
        {
            var sb = new StringBuilder();
            long first = FirstLineNumber;
            for (long n = startLine; n <= endLine; n++)
            {
                int i = (int)(n - first);
                if (i < 0 || i >= BufferCount) continue;
                var l = BufferLine(i);
                int from = n == startLine ? startCol : 0;
                int to = n == endLine ? endCol + 1 : Cols;
                var text = l.Text(from, to);
                if (!(l.Wrapped && n != endLine)) text = text.TrimEnd();
                sb.Append(text);
                if (n != endLine && !l.Wrapped) sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
