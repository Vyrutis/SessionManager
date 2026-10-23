using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SessionManager;

/// <summary>How the saved files (sessions, backups, remembered server keys) are stored on disk.</summary>
public enum ProtectionMode : byte
{
    /// <summary>Readable text (Linux, or a file from an older version).</summary>
    None = 0,
    /// <summary>Encrypted with the Windows account's key (DPAPI): unreadable on another PC or for another user. No password to type.</summary>
    Windows = 1,
    /// <summary>Encrypted with a master password (AES-256-GCM, PBKDF2-SHA256), asked for when the app starts.</summary>
    Password = 2,
}

/// <summary>
/// Encrypts and decrypts the app's data files. A protected file starts with "SMENC1" and a mode byte, so older plain
/// files are still read and are protected the next time they are saved.
/// </summary>
public static class DataProtector
{
    private static readonly byte[] Magic = "SMENC1"u8.ToArray();
    private static readonly byte[] Entropy = "SessionManager.Data.v1"u8.ToArray();
    private const int HeaderLength = 7, SaltLength = 16, NonceLength = 12, TagLength = 16, Iterations = 300_000;

    private static byte[]? _key, _salt;

    /// <summary>Set to false to keep every file as plain text (the tests do this).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Asks for the master password; the argument is true when the last one was wrong. Null = cancelled.</summary>
    public static Func<bool, string?>? AskPassword { get; set; }

    /// <summary>How files are written from now on. Follows the file that was loaded; new data starts with the best available.</summary>
    public static ProtectionMode Mode { get; private set; } = DefaultMode;

    private static ProtectionMode DefaultMode => OperatingSystem.IsWindows() ? ProtectionMode.Windows : ProtectionMode.None;

    public static bool IsProtected(byte[] data) => data.Length >= HeaderLength && data.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    /// <summary>Which mode a file uses without decrypting it (None for plain text).</summary>
    public static ProtectionMode ModeOf(byte[] data) => IsProtected(data) ? (ProtectionMode)data[Magic.Length] : ProtectionMode.None;

    /// <summary>True when the file at <paramref name="path"/> needs the master password.</summary>
    public static bool NeedsPassword(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = File.OpenRead(path);
            var head = new byte[HeaderLength];
            return fs.Read(head, 0, HeaderLength) == HeaderLength && ModeOf(head) == ProtectionMode.Password;
        }
        catch (IOException) { return false; }
    }

    /// <summary>Forgets the master password and goes back to the default protection (used by the tests).</summary>
    public static void Reset()
    {
        _key = _salt = null;
        Mode = DefaultMode;
    }

    /// <summary>Starts protecting with a master password (a new salt), or goes back to the Windows account key when <paramref name="password"/> is null.</summary>
    public static void SetPassword(string? password)
    {
        if (password == null)
        {
            _key = _salt = null;
            Mode = DefaultMode;
            return;
        }
        _salt = RandomNumberGenerator.GetBytes(SaltLength);
        _key = Derive(password, _salt);
        Mode = ProtectionMode.Password;
    }

    public static byte[] Protect(byte[] plain)
    {
        if (!Enabled || Mode == ProtectionMode.None) return plain;
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        header[Magic.Length] = (byte)Mode;
        if (Mode == ProtectionMode.Windows)
        {
            var sealedBytes = !OperatingSystem.IsWindows() ? throw new PlatformNotSupportedException() : System.Security.Cryptography.ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            return [.. header, .. sealedBytes];
        }
        if (_key == null || _salt == null) throw new InvalidOperationException("The master password is not set.");
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagLength];
        using (var gcm = new AesGcm(_key, TagLength)) gcm.Encrypt(nonce, plain, cipher, tag, header);
        return [.. header, .. _salt, .. nonce, .. tag, .. cipher];
    }

    /// <summary>Makes new files use the same protection as this (already loaded) file.</summary>
    public static void Adopt(byte[] data)
    {
        var mode = ModeOf(data);
        if (mode != ProtectionMode.None) Mode = mode;
        else if (Enabled) Mode = DefaultMode;
    }

    /// <summary>
    /// Plain data is returned as it is. With <paramref name="allowPrompt"/>, a master password file asks for the password until it is right
    /// (<see cref="OperationCanceledException"/> if cancelled); without it, only a file that matches the password already entered can be read.
    /// </summary>
    /// <exception cref="InvalidDataException">The data can't be decrypted (another Windows user or PC, a damaged or edited file).</exception>
    public static byte[] Unprotect(byte[] data, bool allowPrompt = true)
    {
        if (!IsProtected(data)) return data;
        var mode = ModeOf(data);
        if (mode == ProtectionMode.Windows)
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidDataException("This file is protected with a Windows account and can only be opened on Windows.");
            try
            {
                var plain = System.Security.Cryptography.ProtectedData.Unprotect(data[HeaderLength..], Entropy, DataProtectionScope.CurrentUser);
                return plain;
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException("This file was protected by another Windows user or on another PC, so it can't be opened here.", ex);
            }
        }
        if (mode != ProtectionMode.Password || data.Length < HeaderLength + SaltLength + NonceLength + TagLength)
            throw new InvalidDataException("The file is damaged or from a newer version.");

        var salt = data.AsSpan(HeaderLength, SaltLength).ToArray();
        var nonce = data.AsSpan(HeaderLength + SaltLength, NonceLength);
        var tag = data.AsSpan(HeaderLength + SaltLength + NonceLength, TagLength);
        var body = data.AsSpan(HeaderLength + SaltLength + NonceLength + TagLength);
        var header = data.AsSpan(0, HeaderLength);

        var key = _key != null && _salt != null && _salt.AsSpan().SequenceEqual(salt) ? _key : null;
        bool retry = false;
        while (true)
        {
            if (key == null)
            {
                var ask = allowPrompt ? AskPassword : null;
                if (ask == null) throw new InvalidDataException("This file needs the master password.");
                var password = ask(retry) ?? throw new OperationCanceledException();
                key = Derive(password, salt);
            }
            try
            {
                var plain = new byte[body.Length];
                using (var gcm = new AesGcm(key, TagLength)) gcm.Decrypt(nonce, body, tag, plain, header);
                _key = key;
                _salt = salt;
                return plain;
            }
            catch (CryptographicException)
            {
                // A wrong password and an edited file look the same; keep asking until it is right or cancelled.
                key = null;
                retry = true;
            }
        }
    }

    /// <summary>Writes a file by replacing it atomically.</summary>
    public static void WriteAll(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Protect(Encoding.UTF8.GetBytes(text)));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The text of a plain or protected file.</summary>
    public static string ReadAll(string path) => Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(path)));

    /// <summary>The text of a file without ever asking for a password (null if it needs one or can't be read).</summary>
    public static string? TryReadWithoutPassword(string path)
    {
        try { return Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(path), allowPrompt: false)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Rewrites a plain file in the current protection (checking it reads back the same). False when it was already protected.</summary>
    public static bool ProtectFile(string path)
    {
        if (!Enabled || Mode == ProtectionMode.None) return false;
        var data = File.ReadAllBytes(path);
        if (IsProtected(data)) return false;
        var sealedBytes = Protect(data);
        if (!Unprotect(sealedBytes, allowPrompt: false).AsSpan().SequenceEqual(data)) throw new InvalidDataException("Could not protect " + path);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, sealedBytes);
        File.Move(tmp, path, overwrite: true);
        return true;
    }

    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
}
