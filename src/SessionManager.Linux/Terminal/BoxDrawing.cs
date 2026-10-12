using Avalonia;
using Avalonia.Media;

namespace SessionManager.Terminal;

/// <summary>
/// Draws box-drawing lines (U+2500…U+257F) and block elements (U+2580…U+259F) as rectangles that fill the whole cell,
/// so frames drawn by full-screen programs join up regardless of the font's line height.
/// </summary>
public static class BoxDrawing
{
    // Arms: left, right, up, down; 0 = none, 1 = light, 2 = heavy, 3 = double.
    private static readonly Dictionary<int, (byte L, byte R, byte U, byte D)> Lines = new()
    {
        [0x2500] = (1, 1, 0, 0), [0x2501] = (2, 2, 0, 0), [0x2502] = (0, 0, 1, 1), [0x2503] = (0, 0, 2, 2),
        [0x2504] = (1, 1, 0, 0), [0x2505] = (2, 2, 0, 0), [0x2506] = (0, 0, 1, 1), [0x2507] = (0, 0, 2, 2),
        [0x2508] = (1, 1, 0, 0), [0x2509] = (2, 2, 0, 0), [0x250A] = (0, 0, 1, 1), [0x250B] = (0, 0, 2, 2),
        [0x250C] = (0, 1, 0, 1), [0x250F] = (0, 2, 0, 2), [0x2510] = (1, 0, 0, 1), [0x2513] = (2, 0, 0, 2),
        [0x2514] = (0, 1, 1, 0), [0x2517] = (0, 2, 2, 0), [0x2518] = (1, 0, 1, 0), [0x251B] = (2, 0, 2, 0),
        [0x251C] = (0, 1, 1, 1), [0x2523] = (0, 2, 2, 2), [0x2524] = (1, 0, 1, 1), [0x252B] = (2, 0, 2, 2),
        [0x252C] = (1, 1, 0, 1), [0x2533] = (2, 2, 0, 2), [0x2534] = (1, 1, 1, 0), [0x253B] = (2, 2, 2, 0),
        [0x253C] = (1, 1, 1, 1), [0x254B] = (2, 2, 2, 2),
        [0x254C] = (1, 1, 0, 0), [0x254D] = (2, 2, 0, 0), [0x254E] = (0, 0, 1, 1), [0x254F] = (0, 0, 2, 2),
        [0x2550] = (3, 3, 0, 0), [0x2551] = (0, 0, 3, 3), [0x2554] = (0, 3, 0, 3), [0x2557] = (3, 0, 0, 3),
        [0x255A] = (0, 3, 3, 0), [0x255D] = (3, 0, 3, 0), [0x2560] = (0, 3, 3, 3), [0x2563] = (3, 0, 3, 3),
        [0x2566] = (3, 3, 0, 3), [0x2569] = (3, 3, 3, 0), [0x256C] = (3, 3, 3, 3),
        [0x256D] = (0, 1, 0, 1), [0x256E] = (1, 0, 0, 1), [0x256F] = (1, 0, 1, 0), [0x2570] = (0, 1, 1, 0),
        [0x2574] = (1, 0, 0, 0), [0x2575] = (0, 0, 1, 0), [0x2576] = (0, 1, 0, 0), [0x2577] = (0, 0, 0, 1),
        [0x2578] = (2, 0, 0, 0), [0x2579] = (0, 0, 2, 0), [0x257A] = (0, 2, 0, 0), [0x257B] = (0, 0, 0, 2),
    };

    public static bool TryDraw(int code, double x, double w, double h, double fontSize, IBrush brush, List<(Rect, IBrush)> output)
    {
        if (code is >= 0x2580 and <= 0x259F) return Block(code, x, w, h, brush, output);
        if (!Lines.TryGetValue(code, out var arms)) return false;

        double light = Math.Max(1, Math.Round(fontSize / 14));
        double heavy = light * 2;
        double cx = Math.Floor(x + w / 2), cy = Math.Floor(h / 2);
        double x0 = Math.Floor(x), x1 = Math.Floor(x + w) + 0.5, y1 = h + 0.5; // a little overlap avoids hairline gaps

        void H(double from, double to, byte kind)
        {
            if (kind == 0) return;
            if (kind == 3)
            {
                output.Add((new Rect(from, cy - light * 1.5, to - from, light), brush));
                output.Add((new Rect(from, cy + light * 0.5, to - from, light), brush));
                return;
            }
            double t = kind == 2 ? heavy : light;
            output.Add((new Rect(from, cy - Math.Floor(t / 2), to - from, t), brush));
        }

        void V(double from, double to, byte kind)
        {
            if (kind == 0) return;
            if (kind == 3)
            {
                output.Add((new Rect(cx - light * 1.5, from, light, to - from), brush));
                output.Add((new Rect(cx + light * 0.5, from, light, to - from), brush));
                return;
            }
            double t = kind == 2 ? heavy : light;
            output.Add((new Rect(cx - Math.Floor(t / 2), from, t, to - from), brush));
        }

        double join = Math.Max(arms.U, arms.D) == 3 || Math.Max(arms.L, arms.R) == 3 ? light * 1.5 : (Math.Max(Math.Max(arms.U, arms.D), Math.Max(arms.L, arms.R)) == 2 ? heavy / 2 : light / 2);
        H(x0, cx + join, arms.L);
        H(cx - join, x1, arms.R);
        V(0, cy + join, arms.U);
        V(cy - join, y1, arms.D);
        return true;
    }

    private static bool Block(int code, double x, double w, double h, IBrush brush, List<(Rect, IBrush)> output)
    {
        x = Math.Floor(x);
        w = Math.Ceiling(w) + 0.5;
        h += 0.5;
        Rect? r = code switch
        {
            0x2580 => new Rect(x, 0, w, h / 2),
            >= 0x2581 and <= 0x2588 => new Rect(x, h - h * (code - 0x2580) / 8, w, h * (code - 0x2580) / 8),
            >= 0x2589 and <= 0x258F => new Rect(x, 0, w * (0x2590 - code) / 8, h),
            0x2590 => new Rect(x + w / 2, 0, w / 2, h),
            0x2594 => new Rect(x, 0, w, h / 8),
            0x2595 => new Rect(x + w * 7 / 8, 0, w / 8, h),
            _ => null,
        };
        if (r != null) { output.Add((r.Value, brush)); return true; }

        if (code is >= 0x2591 and <= 0x2593 && brush is ISolidColorBrush solid)
        {
            byte alpha = code switch { 0x2591 => 64, 0x2592 => 128, _ => 192 };
            var c = solid.Color;
            output.Add((new Rect(x, 0, w, h), new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B))));
            return true;
        }

        // Quadrants
        var (tl, tr, bl, br) = code switch
        {
            0x2596 => (false, false, true, false),
            0x2597 => (false, false, false, true),
            0x2598 => (true, false, false, false),
            0x2599 => (true, false, true, true),
            0x259A => (true, false, false, true),
            0x259B => (true, true, true, false),
            0x259C => (true, true, false, true),
            0x259D => (false, true, false, false),
            0x259E => (false, true, true, false),
            0x259F => (false, true, true, true),
            _ => (false, false, false, false),
        };
        if (!(tl || tr || bl || br)) return false;
        if (tl) output.Add((new Rect(x, 0, w / 2, h / 2), brush));
        if (tr) output.Add((new Rect(x + w / 2, 0, w / 2, h / 2), brush));
        if (bl) output.Add((new Rect(x, h / 2, w / 2, h / 2), brush));
        if (br) output.Add((new Rect(x + w / 2, h / 2, w / 2, h / 2), brush));
        return true;
    }
}
