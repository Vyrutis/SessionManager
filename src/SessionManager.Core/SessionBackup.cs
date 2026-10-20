using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SessionManager;

/// <summary>
/// A copy of sessions.json once a day in Backups\ (the newest 30 are kept), so a mistake can be undone.
/// Passwords are in Windows Credential Manager / the keyring, not in these files.
/// </summary>
public static class SessionBackup
{
    public const int Keep = 30;
    private const string Prefix = "sessions-";

    public static string Folder => Path.Combine(SessionStore.DataDir, "Backups");

    public sealed record Entry(string Path, DateTime Date, int Sessions, int Folders);

    /// <summary>Copies the sessions file to today's backup unless there is one already; removes the oldest beyond <see cref="Keep"/>.</summary>
    public static void MakeDaily(DateTime? today = null)
    {
        var source = SessionStore.FilePath;
        if (!File.Exists(source)) return;
        var day = (today ?? DateTime.Now).Date;
        var target = Path.Combine(Folder, $"{Prefix}{day:yyyy-MM-dd}.json");
        if (File.Exists(target)) return;
        Directory.CreateDirectory(Folder);
        File.Copy(source, target);
        foreach (var old in List().Skip(Keep)) TryDelete(old.Path);
    }

    /// <summary>Backups, newest first.</summary>
    public static List<Entry> List()
    {
        if (!Directory.Exists(Folder)) return new();
        var list = new List<Entry>();
        foreach (var f in Directory.GetFiles(Folder, Prefix + "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(f)[Prefix.Length..];
            if (!DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            int sessions = 0, folders = 0;
            try
            {
                var data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(f), SessionStore.JsonOptions);
                var nodes = (data?.Roots ?? new()).Where(r => r != null).SelectMany(r => r.Descendants()).ToList();
                sessions = nodes.Count(n => n.IsSession);
                folders = nodes.Count(n => n.IsFolder);
            }
            catch { sessions = -1; } // unreadable; still listed so it can be looked at
            list.Add(new Entry(f, date, sessions, folders));
        }
        return list.OrderByDescending(e => e.Date).ToList();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
