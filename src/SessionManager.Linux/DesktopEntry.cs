namespace SessionManager;

/// <summary>Application-menu entries (~/.local/share/applications) that open one saved session directly.</summary>
public static class DesktopEntry
{
    private static string Dir
    {
        get
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
                ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(data, "applications");
        }
    }

    private static string FileFor(Guid id) => Path.Combine(Dir, $"sessionmanager-{id:N}.desktop");

    /// <summary>The installed launcher when there is one, otherwise this program.</summary>
    private static string Command =>
        File.Exists("/usr/bin/sessionmanager") ? "sessionmanager"
        : Environment.ProcessPath is { } p ? Quote(p) : "sessionmanager";

    private static string Quote(string s) => s.Contains(' ') || s.Contains('"') ? "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : s;

    /// <summary>Removes characters that would break the .desktop format.</summary>
    private static string Clean(string s) => new string(s.Where(c => c >= ' ' && c != '\x7f').ToArray()).Trim();

    public static string Create(Node node)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Menu entries are for Linux desktops.");
        Directory.CreateDirectory(Dir);
        var path = FileFor(node.Id);
        var kind = node.Protocol.DisplayName();
        File.WriteAllText(path, $"""
            [Desktop Entry]
            Type=Application
            Name={Clean(node.Name)}
            GenericName={kind} connection
            Comment={kind}: {Clean(node.Subtitle)} (Session Manager)
            Exec={Command} --open {node.Id}
            Icon=sessionmanager
            Terminal=false
            Categories=Network;RemoteAccess;
            StartupWMClass=sessionmanager

            """);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return path;
    }

    public static void Remove(Guid id)
    {
        try { if (!OperatingSystem.IsWindows() && File.Exists(FileFor(id))) File.Delete(FileFor(id)); } catch { }
    }
}
