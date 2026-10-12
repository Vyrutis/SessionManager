using System.IO;
using System.Text.Json;

namespace SessionManager;

/// <summary>
/// Export/import of many sessions at once (the whole list or one folder), with their folders and credential profiles.
/// Saved passwords can be included, encrypted together with one passphrase (same scheme as <see cref="SessionFile"/>).
/// </summary>
public static class SessionList
{
    public const string Extension = ".smlist";
    private const string Format = "SessionManager.List";

    public sealed class FileModel
    {
        public string Format { get; set; } = SessionList.Format;
        public int Version { get; set; } = 1;
        public DateTime Exported { get; set; }
        /// <summary>Top-level folders and sessions; each carries its protocol.</summary>
        public List<Node> Items { get; set; } = new();
        public List<CredentialProfile> Credentials { get; set; } = new();
        /// <summary>Default settings for new sessions; only in a full export.</summary>
        public Dictionary<Protocol, SessionSettings>? Presets { get; set; }
        /// <summary>JSON object {id: password} for sessions and profiles, encrypted with the export passphrase.</summary>
        public SessionFile.EncryptedPassword? Passwords { get; set; }
    }

    public sealed record ImportResult(int Added, int Skipped, int Folders, List<Node> AddedSessions);

    public static IEnumerable<Node> AllNodes(IEnumerable<Node> items) => items.SelectMany(i => i.Descendants().Prepend(i));

    /// <summary>Builds the file content. <paramref name="passwords"/> (id → password) is encrypted when a passphrase is given.</summary>
    public static FileModel Build(IEnumerable<Node> items, IEnumerable<CredentialProfile> profiles,
        Dictionary<Protocol, SessionSettings>? presets, IReadOnlyDictionary<Guid, string>? passwords, string? passphrase)
    {
        var model = new FileModel
        {
            Exported = DateTime.Now,
            Items = items.ToList(),
            Credentials = profiles.ToList(),
            Presets = presets,
        };
        if (passwords is { Count: > 0 } && passphrase != null)
            model.Passwords = SessionFile.Encrypt(JsonSerializer.Serialize(passwords), passphrase);
        return model;
    }

    public static void Write(string path, FileModel model) =>
        File.WriteAllText(path, JsonSerializer.Serialize(model, SessionStore.JsonOptions));

    public static FileModel Read(string path)
    {
        var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), SessionStore.JsonOptions);
        if (model == null || model.Format != Format)
            throw new InvalidDataException("This is not a Session Manager session list.");
        SessionFile.Validate(model.Passwords);
        model.Items = (model.Items ?? new()).Where(n => n != null).ToList();
        model.Credentials = (model.Credentials ?? new()).Where(c => c != null && c.Name != null && c.Username != null).ToList();
        foreach (var c in model.Credentials) c.Domain ??= "";
        foreach (var n in model.Items) Normalize(n);
        if (model.Presets != null)
            foreach (var key in model.Presets.Keys.ToList()) model.Presets[key] = SessionFile.Normalize(model.Presets[key]);
        return model;
    }

    private static void Normalize(Node n)
    {
        n.IsRoot = false;
        n.Name ??= "";
        n.Children = new((n.Children ?? new()).Where(c => c != null));
        if (n.IsSession)
        {
            n.Settings = SessionFile.Normalize(n.Settings);
            n.Children.Clear();
        }
        else n.Settings = null;
        foreach (var c in n.Children) Normalize(c);
    }

    /// <summary>Returns null if the passphrase is wrong.</summary>
    public static Dictionary<Guid, string>? DecryptPasswords(FileModel model, string passphrase)
    {
        var json = SessionFile.Decrypt(model.Passwords!, passphrase);
        return json == null ? null : JsonSerializer.Deserialize<Dictionary<Guid, string>>(json) ?? new();
    }

    /// <summary>Removes every session and folder from the list (for "replace").</summary>
    public static void Clear(SessionStore store)
    {
        foreach (var root in store.Data.Roots) root.Children.Clear();
    }

    /// <summary>
    /// Adds the file's folders and sessions to the list. Folders with the same name are merged; sessions that are
    /// already in the list (same id, e.g. re-importing a backup) are skipped. Links to profiles or jump hosts that
    /// don't exist afterwards are cleared.
    /// </summary>
    public static ImportResult Merge(SessionStore store, FileModel model)
    {
        var existing = AllNodes(store.Data.Roots).Select(n => n.Id).ToHashSet();
        int added = 0, skipped = 0, folders = 0;
        var addedSessions = new List<Node>();

        void Add(Node parent, Node item)
        {
            if (item.IsFolder)
            {
                var kids = item.Children.ToList();
                var same = parent.Children.FirstOrDefault(c => c.IsFolder && string.Equals(c.Name, item.Name, StringComparison.CurrentCultureIgnoreCase));
                if (same == null)
                {
                    if (!existing.Add(item.Id)) item.Id = Guid.NewGuid();
                    item.Children = new();
                    store.AddChild(parent, item);
                    folders++;
                    same = item;
                }
                foreach (var k in kids) Add(same, k);
                return;
            }
            if (!existing.Add(item.Id)) { skipped++; return; }
            store.AddChild(parent, item);
            addedSessions.Add(item);
            added++;
        }

        foreach (var p in model.Credentials)
            if (store.Data.Credentials.All(c => c.Id != p.Id)) store.Data.Credentials.Add(p);
        foreach (var item in model.Items) Add(store.RootFor(item.Protocol), item);

        foreach (var n in addedSessions)
        {
            var s = n.Settings!;
            if (s.CredentialId is Guid cid && store.Data.Credentials.All(c => c.Id != cid)) s.CredentialId = null;
            if (s.JumpHostId is Guid jid && (jid == n.Id || store.FindById(jid) is not { IsSession: true, Protocol: Protocol.Ssh }))
                s.JumpHostId = null;
        }
        return new ImportResult(added, skipped, folders, addedSessions);
    }
}
