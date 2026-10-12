using System.Text.Json;

namespace SessionManager;

/// <summary>Settings only the Linux app uses, kept in linux.json next to sessions.json (which stays compatible with Windows).</summary>
public sealed class LinuxSettings
{
    /// <summary>FreeRDP program to use; empty = find xfreerdp3 / sdl-freerdp3 / xfreerdp automatically.</summary>
    public string RdpClient { get; set; } = "";
    /// <summary>Show the remote desktop inside the tab (X11 / XWayland) instead of in FreeRDP's own window.</summary>
    public bool RdpEmbedded { get; set; }
    /// <summary>Extra FreeRDP arguments, e.g. "/scale:140 /network:lan".</summary>
    public string RdpExtraArgs { get; set; } = "";
    /// <summary>Trust a server's RDP certificate the first time and warn when it changes (FreeRDP /cert:tofu).</summary>
    public bool RdpTrustOnFirstUse { get; set; } = true;

    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool Maximized { get; set; }
    public double SidebarWidth { get; set; } = 290;

    private static string FilePath => Path.Combine(SessionStore.DataDir, "linux.json");

    public static LinuxSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<LinuxSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SessionStore.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { CrashLog.Write(ex); }
    }
}
