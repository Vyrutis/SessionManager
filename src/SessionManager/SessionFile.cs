using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SessionManager;

/// <summary>
/// Single-session export/import. The optional password is encrypted with AES-256-GCM using a key
/// derived from a passphrase the user picks at export time (PBKDF2-SHA256).
/// </summary>
public static class SessionFile
{
    public const string Extension = ".smsession";
    private const string Format = "SessionManager.Session";
    private const int Iterations = 300_000;
    /// <summary>A crafted file with a huge count would freeze the app while importing.</summary>
    private const int MaxIterations = 5_000_000;

    public sealed class FileModel
    {
        public string Format { get; set; } = SessionFile.Format;
        public int Version { get; set; } = 1;
        public string Name { get; set; } = "";
        public Protocol Protocol { get; set; }
        public SessionSettings Settings { get; set; } = new();
        public EncryptedPassword? Password { get; set; }
    }

    public sealed class EncryptedPassword
    {
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Data { get; set; } = "";
    }

    public static void Export(string path, Node session, string? password, string? passphrase)
    {
        var model = new FileModel { Name = session.Name, Protocol = session.Protocol, Settings = session.Settings!.Clone() };
        if (password != null && passphrase != null)
            model.Password = Encrypt(password, passphrase);
        File.WriteAllText(path, JsonSerializer.Serialize(model, SessionStore.JsonOptions));
    }

    public static FileModel Read(string path)
    {
        var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), SessionStore.JsonOptions);
        if (model == null || model.Format != Format)
            throw new InvalidDataException("This is not a Session Manager session file.");
        model.Settings = Normalize(model.Settings);
        Validate(model.Password);
        return model;
    }

    /// <exception cref="InvalidDataException">The encrypted block can't be one this app wrote.</exception>
    public static void Validate(EncryptedPassword? p)
    {
        if (p != null && (p.Iterations < 1 || p.Iterations > MaxIterations))
            throw new InvalidDataException("The protected password in this file is not in a supported format.");
    }

    /// <summary>A hand-edited file can contain nulls; fill them in so the rest of the app can rely on them.</summary>
    public static SessionSettings Normalize(SessionSettings? s)
    {
        s ??= new SessionSettings();
        s.Host ??= "";
        s.Username ??= "";
        s.Domain ??= "";
        s.PrivateKeyPath ??= "";
        s.FontFamily ??= "";
        s.ColorScheme ??= TerminalSchemes.Auto;
        s.Tunnels = s.Tunnels?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new();
        if (s.FontSize is < 6 or > 72) s.FontSize = 14;
        return s;
    }

    public static EncryptedPassword Encrypt(string password, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plain = Encoding.UTF8.GetBytes(password);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plain);
        return new EncryptedPassword
        {
            Iterations = Iterations,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Data = Convert.ToBase64String(cipher),
        };
    }

    /// <summary>Returns null if the passphrase is wrong.</summary>
    public static string? Decrypt(EncryptedPassword enc, string passphrase)
    {
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, Convert.FromBase64String(enc.Salt), enc.Iterations,
            HashAlgorithmName.SHA256, 32);
        var cipher = Convert.FromBase64String(enc.Data);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(enc.Nonce), cipher, Convert.FromBase64String(enc.Tag), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Writes a standard .rdp file that Remote Desktop Connection (mstsc) can open. Never contains the password.</summary>
    public static void ExportRdp(string path, Node session)
    {
        var s = session.Settings!;
        var sb = new StringBuilder();
        sb.AppendLine($"full address:s:{s.Host}{(s.Port != 3389 ? ":" + s.Port : "")}");
        if (!string.IsNullOrEmpty(s.Username))
            sb.AppendLine($"username:s:{(string.IsNullOrEmpty(s.Domain) ? "" : s.Domain + "\\")}{s.Username}");
        if (s.Width > 0 && s.Height > 0)
        {
            sb.AppendLine("screen mode id:i:1");
            sb.AppendLine($"desktopwidth:i:{s.Width}");
            sb.AppendLine($"desktopheight:i:{s.Height}");
        }
        else
        {
            sb.AppendLine("screen mode id:i:2");
            sb.AppendLine("dynamic resolution:i:1");
        }
        sb.AppendLine($"session bpp:i:{s.ColorDepth}");
        sb.AppendLine($"redirectclipboard:i:{(s.RedirectClipboard ? 1 : 0)}");
        sb.AppendLine($"redirectprinters:i:{(s.RedirectPrinters ? 1 : 0)}");
        if (s.RedirectDrives) sb.AppendLine("drivestoredirect:s:*");
        sb.AppendLine($"smart sizing:i:{(s.SmartSizing ? 1 : 0)}");
        File.WriteAllText(path, sb.ToString(), Encoding.Unicode);
    }

    /// <summary>Reads host, port, user and display options from an .rdp file.</summary>
    public static (string name, SessionSettings settings) ImportRdp(string path, SessionSettings preset)
    {
        var s = preset.Clone();
        s.Port = 3389;
        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split(':', 3);
            if (parts.Length < 3) continue;
            var key = parts[0].Trim().ToLowerInvariant();
            var val = parts[2].Trim();
            int.TryParse(val, out var num);
            switch (key)
            {
                case "full address":
                    // host, host:port, [ipv6] or [ipv6]:port
                    string? portText = null;
                    if (val.StartsWith('[') && val.IndexOf(']') is var close and > 0)
                    {
                        s.Host = val[1..close];
                        if (val.Length > close + 1 && val[close + 1] == ':') portText = val[(close + 2)..];
                    }
                    else if (val.Count(c => c == ':') == 1)
                    {
                        s.Host = val[..val.IndexOf(':')];
                        portText = val[(val.IndexOf(':') + 1)..];
                    }
                    else s.Host = val;
                    if (int.TryParse(portText, out var port) && port is >= 1 and <= 65535) s.Port = port;
                    break;
                case "server port": if (num is > 0 and <= 65535) s.Port = num; break;
                case "username":
                    var slash = val.IndexOf('\\');
                    if (slash >= 0) { s.Domain = val[..slash]; s.Username = val[(slash + 1)..]; }
                    else s.Username = val;
                    break;
                case "domain": s.Domain = val; break;
                case "desktopwidth": s.Width = num; break;
                case "desktopheight": s.Height = num; break;
                case "screen mode id": if (num == 2) { s.Width = 0; s.Height = 0; } break;
                case "session bpp": if (num > 0) s.ColorDepth = num; break;
                case "redirectclipboard": s.RedirectClipboard = num == 1; break;
                case "redirectprinters": s.RedirectPrinters = num == 1; break;
                case "drivestoredirect": s.RedirectDrives = val.Length > 0; break;
                case "smart sizing": s.SmartSizing = num == 1; break;
            }
        }
        if (string.IsNullOrWhiteSpace(s.Host))
            throw new InvalidDataException("The .rdp file does not contain a computer name (full address).");
        return (Path.GetFileNameWithoutExtension(path), s);
    }
}
