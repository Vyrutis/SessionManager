namespace SessionManager;

/// <summary>X11 keysyms, the key codes VNC uses.</summary>
public static class VncKeys
{
    public const uint BackSpace = 0xFF08, Tab = 0xFF09, Return = 0xFF0D, Pause = 0xFF13, ScrollLock = 0xFF14, Escape = 0xFF1B;
    public const uint Home = 0xFF50, Left = 0xFF51, Up = 0xFF52, Right = 0xFF53, Down = 0xFF54, PageUp = 0xFF55, PageDown = 0xFF56, End = 0xFF57;
    public const uint Print = 0xFF61, Insert = 0xFF63, Menu = 0xFF67, NumLock = 0xFF7F, Delete = 0xFFFF;
    public const uint KpEnter = 0xFF8D, KpMultiply = 0xFFAA, KpAdd = 0xFFAB, KpSubtract = 0xFFAD, KpDecimal = 0xFFAE, KpDivide = 0xFFAF, Kp0 = 0xFFB0;
    public const uint F1 = 0xFFBE;
    public const uint ShiftL = 0xFFE1, ShiftR = 0xFFE2, ControlL = 0xFFE3, ControlR = 0xFFE4, CapsLock = 0xFFE5;
    public const uint AltL = 0xFFE9, AltR = 0xFFEA, SuperL = 0xFFEB, SuperR = 0xFFEC;

    /// <summary>F1..F24 (n = 1..24).</summary>
    public static uint F(int n) => F1 + (uint)(n - 1);

    /// <summary>The keysym of a typed character: Latin-1 directly, everything else as Unicode (0x01000000 + code point).</summary>
    public static uint FromChar(int codePoint) => codePoint switch
    {
        '\n' or '\r' => Return,
        '\t' => Tab,
        '\b' => BackSpace,
        >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF => (uint)codePoint,
        _ => 0x01000000u + (uint)codePoint,
    };
}
