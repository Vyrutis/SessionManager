using System.Diagnostics;
using System.Text;

namespace SessionManager;

/// <summary>
/// Linux password storage in the desktop keyring through the freedesktop Secret Service (GNOME Keyring, KWallet,
/// KeePassXC...). Uses libsecret's <c>secret-tool</c>; the password is passed on stdin, never on the command line.
/// Items are labelled "Session Manager" and found by the attributes application=sessionmanager, id={session id}.
/// </summary>
public static class SecretService
{
    private const string Tool = "secret-tool";
    /// <summary>Unlocking the keyring can show a prompt; give the user time to answer it.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private static string[] Attributes(Guid id) => ["application", "sessionmanager", "id", id.ToString()];

    /// <summary>Null when nothing is saved, or when no keyring is available.</summary>
    public static string? Get(Guid id)
    {
        try
        {
            var (code, output, _) = Run(["lookup", .. Attributes(id)], null);
            return code == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException)
        {
            return null;
        }
    }

    public static void Set(Guid id, string password, string? userName)
    {
        var label = string.IsNullOrEmpty(userName) ? "Session Manager" : $"Session Manager: {userName}";
        (int code, string _, string error) result;
        try
        {
            result = Run(["store", "--label=" + label, .. Attributes(id)], password);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("Could not save the password: secret-tool was not found. Install libsecret (sudo pacman -S libsecret) and a keyring such as gnome-keyring or KWallet.");
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("Could not save the password: the keyring did not answer (was the unlock prompt closed?).");
        }
        if (result.code != 0)
            throw new InvalidOperationException("Could not save the password in the keyring" +
                (result.error.Trim().Length > 0 ? $": {result.error.Trim()}" : ".") +
                "\nMake sure a Secret Service keyring (gnome-keyring, KWallet or KeePassXC) is running and unlocked.");
    }

    public static void Delete(Guid id)
    {
        try { Run(["clear", .. Attributes(id)], null); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException) { }
    }

    /// <summary>True when secret-tool is installed (a keyring may still be missing or locked).</summary>
    public static bool IsAvailable()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, Tool))) return true;
        return false;
    }

    private static (int Code, string Output, string Error) Run(string[] args, string? input)
    {
        var psi = new ProcessStartInfo(Tool)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new System.ComponentModel.Win32Exception("secret-tool did not start.");
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        // secret-tool reads the secret up to end of input; no trailing newline, or it becomes part of the password.
        if (input != null) p.StandardInput.Write(input);
        p.StandardInput.Close();
        if (!p.WaitForExit(Timeout))
        {
            try { p.Kill(); } catch { }
            throw new TimeoutException();
        }
        return (p.ExitCode, output.Result, error.Result);
    }
}
