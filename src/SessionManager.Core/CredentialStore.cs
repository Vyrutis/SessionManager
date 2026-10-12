using System.Runtime.InteropServices;
using System.Text;

namespace SessionManager;

/// <summary>
/// Stores session passwords outside the app's files: in Windows Credential Manager (encrypted with the user's Windows
/// login; listed under "Windows Credentials" as SessionManager:{session id}), or on Linux in the desktop keyring
/// (GNOME Keyring, KWallet or KeePassXC, through the Secret Service and libsecret's secret-tool).
/// </summary>
public static class CredentialStore
{
    public static string? Get(Guid id) => OperatingSystem.IsWindows() ? WinGet(id) : SecretService.Get(id);

    public static bool Has(Guid id) => Get(id) != null;

    /// <exception cref="InvalidOperationException">The password could not be saved; the message says why.</exception>
    public static void Set(Guid id, string password, string? userName = null)
    {
        if (OperatingSystem.IsWindows()) WinSet(id, password, userName);
        else SecretService.Set(id, password, userName);
    }

    public static void Delete(Guid id)
    {
        if (OperatingSystem.IsWindows()) WinDelete(id);
        else SecretService.Delete(id);
    }

    // ───────────── Windows Credential Manager ─────────────

    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    private static string Target(Guid id) => $"SessionManager:{id}";

    private static string? WinGet(Guid id)
    {
        if (!CredRead(Target(id), CRED_TYPE_GENERIC, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return "";
            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    private static void WinSet(Guid id, string password, string? userName)
    {
        var bytes = Encoding.Unicode.GetBytes(password);
        var blob = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target(id),
                Comment = "Session Manager saved password",
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = userName ?? "",
            };
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException($"Could not save the password (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
        }
    }

    private static void WinDelete(Guid id)
    {
        if (!CredDelete(Target(id), CRED_TYPE_GENERIC, 0) && Marshal.GetLastWin32Error() != ERROR_NOT_FOUND)
            throw new InvalidOperationException($"Could not delete the saved password (error {Marshal.GetLastWin32Error()}).");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
