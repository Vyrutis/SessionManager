using System.IO;
using System.Text.Json;

namespace SessionManager;

/// <summary>Remembers SSH host key fingerprints so a changed key (possible man-in-the-middle) is detected.</summary>
public static class KnownHosts
{
    private static readonly string FilePath = Path.Combine(SessionStore.DataDir, "known_hosts.json");
    private static readonly object Lock = new();

    public enum Result { Match, Unknown, Changed }

    private static string Key(string host, int port) => $"{host.ToLowerInvariant()}:{port}";

    public static Result Check(string host, int port, string keyType, string fingerprint, out string? stored)
    {
        lock (Lock)
        {
            var all = Load();
            if (!all.TryGetValue(Key(host, port), out stored)) return Result.Unknown;
            return stored == $"{keyType} {fingerprint}" ? Result.Match : Result.Changed;
        }
    }

    public static void Trust(string host, int port, string keyType, string fingerprint)
    {
        lock (Lock)
        {
            var all = Load();
            all[Key(host, port)] = $"{keyType} {fingerprint}";
            Directory.CreateDirectory(SessionStore.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            // Treat an unreadable file as empty; the user will be asked to confirm keys again.
        }
        return new();
    }
}
