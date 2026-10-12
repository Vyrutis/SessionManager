using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessionManager;

/// <summary>Loads and saves the session tree to sessions.json in <see cref="DataDir"/>.</summary>
public sealed class SessionStore
{
    /// <summary>
    /// %APPDATA%\SessionManager on Windows, $XDG_CONFIG_HOME/SessionManager (~/.config/SessionManager) on Linux,
    /// or SESSIONMANAGER_DATA when set (for a separate test copy).
    /// </summary>
    public static readonly string DataDir = Environment.GetEnvironmentVariable("SESSIONMANAGER_DATA") is { Length: > 0 } custom
        ? Path.GetFullPath(custom)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SessionManager");

    private static readonly string FilePath = Path.Combine(DataDir, "sessions.json");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppData Data { get; private set; } = new();

    public Node SshRoot => Data.Roots.First(r => r.Protocol == Protocol.Ssh);
    public Node RdpRoot => Data.Roots.First(r => r.Protocol == Protocol.Rdp);

    public Node RootFor(Protocol p) => p == Protocol.Ssh ? SshRoot : RdpRoot;

    /// <summary>Returns a warning message if the existing file could not be read.</summary>
    public string? Load()
    {
        string? warning = null;
        if (File.Exists(FilePath))
        {
            try
            {
                Data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(FilePath), JsonOptions) ?? new AppData();
            }
            catch (Exception ex)
            {
                var backup = FilePath + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(FilePath, backup, overwrite: true);
                warning = $"Your sessions file could not be read and was backed up to:\n{backup}\n\n{ex.Message}";
                Data = new AppData();
            }
        }

        Data.Roots ??= new();
        Data.Credentials ??= new();
        Data.Presets ??= new();
        Data.Ui = UiSettings.Normalize(Data.Ui);
        Data.Domains = (Data.Domains ?? new()).Select(DomainName.Clean).Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var key in Data.Presets.Keys.ToList()) Data.Presets[key] = SessionFile.Normalize(Data.Presets[key]);

        EnsureRoot(Protocol.Ssh, "SSH sessions");
        EnsureRoot(Protocol.Rdp, "RDP sessions");

        // Keep SSH first, RDP second.
        var ordered = Data.Roots.Where(r => r.IsRoot).OrderBy(r => r.Protocol).ToList();
        Data.Roots.Clear();
        foreach (var r in ordered) Data.Roots.Add(r);

        foreach (var root in Data.Roots) LinkParents(root);
        return warning;
    }

    private void EnsureRoot(Protocol p, string name)
    {
        if (Data.Roots.Any(r => r.IsRoot && r.Protocol == p)) return;
        Data.Roots.Add(new Node { Kind = NodeKind.Folder, Protocol = p, IsRoot = true, Name = name, IsExpanded = true });
    }

    private static void LinkParents(Node node)
    {
        node.Children ??= new();
        foreach (var c in node.Children)
        {
            c.Parent = node;
            c.Protocol = node.Protocol;
            c.Name ??= "";
            if (c.IsSession) c.Settings = SessionFile.Normalize(c.Settings);
            LinkParents(c);
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Data, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public Node? FindById(Guid id) =>
        Data.Roots.SelectMany(r => r.Descendants()).FirstOrDefault(n => n.Id == id);

    public SessionSettings PresetFor(Protocol p) =>
        Data.Presets.TryGetValue(p, out var s) ? s.Clone() : SessionSettings.DefaultsFor(p);

    public void AddChild(Node folder, Node child)
    {
        child.Parent = folder;
        child.Protocol = folder.Protocol;
        folder.Children.Add(child);
        Sort(folder);
        folder.IsExpanded = true;
    }

    public void Remove(Node node)
    {
        node.Parent?.Children.Remove(node);
        node.Parent = null;
    }

    /// <summary>Folders first, then sessions, each alphabetically.</summary>
    public static void Sort(Node folder)
    {
        var sorted = folder.Children
            .OrderBy(n => n.IsFolder ? 0 : 1)
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = folder.Children.IndexOf(sorted[i]);
            if (cur != i) folder.Children.Move(cur, i);
        }
    }
}
