using System.IO;
using System.Text;
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

    public static readonly string FilePath = Path.Combine(DataDir, "sessions.json");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppData Data { get; private set; } = new();

    public Node SshRoot => Data.Roots.First(r => r.Protocol == Protocol.Ssh);
    public Node RdpRoot => Data.Roots.First(r => r.Protocol == Protocol.Rdp);

    public Node VncRoot => Data.Roots.First(r => r.Protocol == Protocol.Vnc);

    public Node RootFor(Protocol p) => Data.Roots.First(r => r.Protocol == p);

    /// <summary>
    /// Returns a warning message if the existing file could not be read.
    /// Throws <see cref="OperationCanceledException"/> when the master password prompt is cancelled (nothing is changed on disk).
    /// </summary>
    public string? Load()
    {
        string? warning = null;
        if (File.Exists(FilePath))
        {
            try
            {
                var raw = File.ReadAllBytes(FilePath);
                var text = Encoding.UTF8.GetString(DataProtector.Unprotect(raw));
                Data = JsonSerializer.Deserialize<AppData>(text, JsonOptions) ?? new AppData();
                DataProtector.Adopt(raw);
                if (!DataProtector.IsProtected(raw) && DataProtector.Mode != ProtectionMode.None)
                {
                    // A file from an older version: protect it (and the backups) now. The check that it reads back happens before it replaces anything.
                    try { Fill(); Save(); ProtectOldFiles(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Security.Cryptography.CryptographicException) { warning = "Your sessions could not be encrypted: " + ex.Message; }
                }
                try { SessionBackup.MakeDaily(); } catch { /* a backup must never stop the app from starting */ }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var backup = FilePath + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(FilePath, backup, overwrite: true);
                warning = $"Your sessions file could not be read and was backed up to:\n{backup}\n\n{ex.Message}";
                Data = new AppData();
            }
        }

        Fill();
        return warning;
    }

    /// <summary>Replaces the list with a backup (the current file is kept as sessions.json.before-restore-…).</summary>
    /// <exception cref="InvalidDataException">The backup can't be read.</exception>
    public void Restore(string backupPath)
    {
        AppData restored;
        try { restored = JsonSerializer.Deserialize<AppData>(DataProtector.ReadAll(backupPath), JsonOptions) ?? throw new InvalidDataException("The backup is empty."); }
        catch (JsonException ex) { throw new InvalidDataException("The backup can't be read: " + ex.Message); }
        if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + $".before-restore-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
        // Only the sessions go back in time; the look of the app stays as it is now.
        restored.Theme = Data.Theme;
        restored.Ui = Data.Ui;
        restored.Language = Data.Language;
        Data = restored;
        Fill();
        Save();
    }

    /// <summary>Fills in what an old or hand-edited file may lack and links parents.</summary>
    private void Fill()
    {
        if (Data.Version < 2)
        {
            Data.CheckForUpdates = false; // version 2: no automatic contact with GitHub unless the user turns it on
            Data.Version = 2;
        }
        Data.Roots ??= new();
        Data.Roots = new(Data.Roots.Where(r => r != null));
        Data.Credentials ??= new();
        Data.Presets ??= new();
        Data.Commands = (Data.Commands ?? new()).Where(c => c != null && !string.IsNullOrEmpty(c.Text)).ToList();
        foreach (var c in Data.Commands) c.Name ??= "";
        Data.HiddenTabs = (Data.HiddenTabs ?? new()).Where(Enum.IsDefined).Distinct().ToList();
        if (Data.HiddenTabs.Count >= Enum.GetValues<Protocol>().Length) Data.HiddenTabs.Clear();
        if (!Enum.IsDefined(Data.ActiveTab) || Data.HiddenTabs.Contains(Data.ActiveTab))
            Data.ActiveTab = Enum.GetValues<Protocol>().First(p => !Data.HiddenTabs.Contains(p));
        Data.Ui = UiSettings.Normalize(Data.Ui);
        Data.Domains = (Data.Domains ?? new()).Select(DomainName.Clean).Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var key in Data.Presets.Keys.ToList()) Data.Presets[key] = SessionFile.Normalize(Data.Presets[key]);

        EnsureRoot(Protocol.Ssh, "SSH sessions");
        EnsureRoot(Protocol.Rdp, "RDP sessions");
        EnsureRoot(Protocol.Vnc, "VNC sessions");
        EnsureRoot(Protocol.Web, "Web sessions");

        // Keep SSH first, then RDP, VNC and Web.
        var ordered = Data.Roots.Where(r => r.IsRoot).OrderBy(r => r.Protocol).ToList();
        Data.Roots.Clear();
        foreach (var r in ordered) Data.Roots.Add(r);

        foreach (var root in Data.Roots) LinkParents(root);
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
        try { SessionBackup.MakeDaily(); } catch { } // the first save of a new day (the app was left open overnight)
        DataProtector.WriteAll(FilePath, JsonSerializer.Serialize(Data, JsonOptions));
    }

    /// <summary>
    /// Switches to a master password (or back to the Windows account key when null): the sessions file, the backups and the remembered server keys
    /// are all rewritten. Each file is read with the old protection first, so a failure leaves it as it was.
    /// </summary>
    public void ChangeProtection(string? masterPassword)
    {
        var files = new List<(string Path, string Text)>();
        if (Directory.Exists(SessionBackup.Folder))
            foreach (var f in Directory.GetFiles(SessionBackup.Folder, "sessions-*.json"))
            {
                var text = DataProtector.TryReadWithoutPassword(f);
                if (text != null) files.Add((f, text)); // one that can't be read with the current password is left alone
            }
        if (File.Exists(KnownHosts.FilePath) && DataProtector.TryReadWithoutPassword(KnownHosts.FilePath) is { } keys) files.Add((KnownHosts.FilePath, keys));

        DataProtector.SetPassword(masterPassword);
        Save();
        foreach (var (path, text) in files) DataProtector.WriteAll(path, text);
    }

    /// <summary>Protects the backups and the remembered server keys that an older version left as plain text.</summary>
    private static void ProtectOldFiles()
    {
        if (Directory.Exists(SessionBackup.Folder))
            foreach (var f in Directory.GetFiles(SessionBackup.Folder, "sessions-*.json")) DataProtector.ProtectFile(f);
        if (File.Exists(KnownHosts.FilePath)) DataProtector.ProtectFile(KnownHosts.FilePath);
    }

    public Node? FindById(Guid id) =>
        Data.Roots.SelectMany(r => r.Descendants()).FirstOrDefault(n => n.Id == id);

    public SessionSettings PresetFor(Protocol p) =>
        Data.Presets.TryGetValue(p, out var s) ? s.Clone() : SessionSettings.DefaultsFor(p);

    public void AddChild(Node folder, Node child)
    {
        child.Parent = folder;
        child.Protocol = folder.Protocol;
        if (Data.ManualOrder)
        {
            // In the user's own order, new folders go after the other folders and new sessions at the end.
            int at = child.IsFolder ? folder.Children.TakeWhile(n => n.IsFolder).Count() : folder.Children.Count;
            folder.Children.Insert(at, child);
        }
        else
        {
            folder.Children.Add(child);
            Sort(folder);
        }
        folder.IsExpanded = true;
    }

    /// <summary>Sorts a folder again after a rename or edit, unless the user arranges the list by hand.</summary>
    public void Resort(Node folder)
    {
        if (!Data.ManualOrder) Sort(folder);
    }

    /// <summary>Puts <paramref name="nodes"/> into <paramref name="folder"/> at <paramref name="index"/>, keeping their order.</summary>
    public void MoveTo(IReadOnlyList<Node> nodes, Node folder, int index)
    {
        foreach (var n in nodes)
            if (n.Parent == folder && folder.Children.IndexOf(n) < index) index--;
        foreach (var n in nodes) Remove(n);
        index = Math.Clamp(index, 0, folder.Children.Count);
        foreach (var n in nodes)
        {
            n.Parent = folder;
            n.Protocol = folder.Protocol;
            folder.Children.Insert(index++, n);
        }
    }

    /// <summary>Sorts every folder of every tab (going back from the user's own order to A to Z).</summary>
    public void SortAll()
    {
        foreach (var f in Data.Roots.SelectMany(r => r.Descendants().Where(n => n.IsFolder).Prepend(r))) Sort(f);
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
