using System.IO;
using System.Text.Json;

namespace SessionManager;

/// <summary>Remembers SSH host key fingerprints so a changed key (possible man-in-the-middle) is detected.</summary>
public static class KnownHosts
{
    internal static readonly string FilePath = Path.Combine(SessionStore.DataDir, "known_hosts.json");
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
            DataProtector.WriteAll(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <exception cref="InvalidDataException">The file exists but can't be read. Connections are refused rather than starting over with no remembered keys.</exception>
    private static Dictionary<string, string> Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(DataProtector.ReadAll(FilePath)) ?? throw new JsonException("The file is empty.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new InvalidDataException($"The remembered server keys ({FilePath}) can't be read: {ex.Message}\nRepair or delete that file to connect again.", ex);
        }
    }
}
