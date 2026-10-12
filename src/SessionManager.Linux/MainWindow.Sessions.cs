using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace SessionManager;

public sealed partial class MainWindow
{
    // ───────────── Opening sessions ─────────────

    private void HandleArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--open" && i + 1 < args.Length)
            {
                if (Guid.TryParse(args[++i], out var id) && _store.FindById(id) is { IsSession: true } node) OpenSession(node);
                else _ = Info("The session for this shortcut no longer exists.");
            }
            else if (!args[i].StartsWith('-') && File.Exists(args[i])) _ = ImportPath(args[i]);
        }
    }

    private void OpenSession(Node node, (string Domain, string User, string Password)? connectAs = null) =>
        _ = OpenSessionAsync(node, connectAs);

    private async Task OpenSessionAsync(Node node, (string Domain, string User, string Password)? connectAs)
    {
        if (node.Settings == null) return;
        var settings = node.Settings.Clone();
        string? password;
        Guid? saveTo = null;

        if (connectAs != null)
        {
            var (domain, user, pw) = connectAs.Value;
            settings.Username = node.Protocol == Protocol.Ssh ? DomainName.Combine(domain, user) : user;
            settings.Domain = node.Protocol == Protocol.Ssh ? "" : domain;
            settings.CredentialId = null;
            password = pw;
        }
        else
        {
            if (ProfileFor(settings) is { } profile)
            {
                settings.Username = profile.Username;
                settings.Domain = profile.Domain;
            }
            var passwordId = PasswordId(node);
            password = await Task.Run(() => CredentialStore.Get(passwordId)); // the keyring may show an unlock prompt
            if (node.Parent != null) saveTo = passwordId; // quick-connect sessions don't save passwords
            if (node.Protocol == Protocol.Ssh && string.IsNullOrEmpty(settings.Username))
            {
                var user = await Dialogs.PromptText(this, "Connect", $"User name for {settings.Host}:");
                if (user == null) return;
                settings.Username = user;
            }
        }

        SshLogin? jump = null;
        if (settings.JumpHostId is Guid jumpId)
        {
            var j = _store.FindById(jumpId);
            if (j is { IsSession: true, Protocol: Protocol.Ssh, Settings: not null } && j != node)
            {
                jump = await LoginFor(j, "jump host ");
                if (string.IsNullOrEmpty(jump.Settings.Username))
                {
                    var user = await Dialogs.PromptText(this, "Connect", $"User name for the jump host {jump.Settings.Host}:");
                    if (user == null) return;
                    jump.Settings.Username = user;
                }
            }
            else if (!await Dialogs.Confirm(this, $"The jump host for \"{node.Name}\" no longer exists.\n\nConnect directly instead?", defaultNo: true))
                return;
        }

        AddTab(node, new ConnectRequest
        {
            Session = node,
            Settings = settings,
            Password = password,
            IsOverride = connectAs != null,
            SavePasswordTo = saveTo,
            Jump = jump,
        });
    }

    private async Task<SshLogin> LoginFor(Node n, string role)
    {
        var s = n.Settings!.Clone();
        if (ProfileFor(s) is { } p) s.Username = p.Username;
        var id = PasswordId(n);
        return new SshLogin { Settings = s, Password = await Task.Run(() => CredentialStore.Get(id)), SaveId = id, Role = role };
    }

    private CredentialProfile? ProfileFor(SessionSettings s) =>
        s.CredentialId is Guid id ? _store.Data.Credentials.FirstOrDefault(c => c.Id == id) : null;

    /// <summary>Where a session's password is stored: its credential profile, or the session itself.</summary>
    private Guid PasswordId(Node n) => n.Settings != null && ProfileFor(n.Settings) is { } p ? p.Id : n.Id;

    private Task<(string Password, bool Save)?> AskPassword(string prompt, bool offerSave) =>
        Dialogs.PromptPassword(this, "Password", prompt, offerSave && (OperatingSystem.IsWindows() || SecretService.IsAvailable()));

    private bool QuickConnect(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return false;
        var protocol = Protocol.Ssh;
        if (text.StartsWith("rdp://", StringComparison.OrdinalIgnoreCase)) { protocol = Protocol.Rdp; text = text[6..]; }
        else if (text.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) text = text[6..];
        text = text.TrimEnd('/');

        string user = "";
        int at = text.LastIndexOf('@');
        if (at >= 0) { user = text[..at]; text = text[(at + 1)..]; }

        string host = text;
        string? portText = null;
        if (text.StartsWith('[') && text.IndexOf(']') is var close and > 0)
        {
            host = text[1..close];
            if (text.Length > close + 1 && text[close + 1] == ':') portText = text[(close + 2)..];
        }
        else if (text.Count(c => c == ':') == 1)
        {
            host = text[..text.IndexOf(':')];
            portText = text[(text.IndexOf(':') + 1)..];
        }

        var settings = _store.PresetFor(protocol);
        if (portText != null)
        {
            if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            {
                _ = Info($"\"{portText}\" is not a valid port.");
                return false;
            }
            settings.Port = port;
        }
        if (host.Length == 0)
        {
            _ = Info("Type a host to connect to, for example admin@10.0.0.5 or rdp://server01.");
            return false;
        }
        settings.Host = host;
        if (user.Length > 0)
        {
            if (protocol == Protocol.Rdp) (settings.Domain, user) = DomainName.Split(user);
            settings.Username = user;
            settings.CredentialId = null;
        }

        var name = (user.Length > 0 ? user + "@" : "") + host;
        OpenSession(new Node { Kind = NodeKind.Session, Protocol = protocol, Name = name, Settings = settings });
        return true;
    }

    private async Task SaveTabSession(OpenTab tab)
    {
        var node = tab.Session;
        if (node.Parent != null || node.Settings == null) return;
        var name = await Dialogs.PromptText(this, "Save session", "Session name:", node.Name);
        if (name == null) return;
        node.Name = name;
        if (string.IsNullOrEmpty(node.Settings.Username)) node.Settings.Username = tab.Request.Settings.Username;
        _store.AddChild(_store.RootFor(node.Protocol), node);
        if (tab.Request.Password != null && await Dialogs.Confirm(this, "Save the password too?", "Save session"))
        {
            try { CredentialStore.Set(node.Id, tab.Request.Password, node.Settings.Username); }
            catch (Exception ex) { await Error("Could not save the password", ex); }
        }
        node.IsSelected = true;
        Save();
    }

    // ───────────── Context menu ─────────────

    private void Tree_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var node = NodeFrom(e.Source);
        e.Handled = true;
        if (node == null) return;
        node.IsSelected = true;
        var items = new List<object>();
        MenuItem Item(string header, Action action, string? gesture = null, bool enabled = true)
        {
            var m = new MenuItem { Header = header, IsEnabled = enabled };
            if (gesture != null) m.InputGesture = KeyGesture.Parse(gesture);
            m.Click += (_, _) => action();
            items.Add(m);
            return m;
        }
        void Sep() => items.Add(new Separator());

        if (node.IsSession)
        {
            Item("Connect", () => OpenSession(node), "Enter");
            Item("Connect as…", () => _ = ConnectAs(node));
            Sep();
            Item("Edit…", () => _ = EditSession(node));
            Item("Rename…", () => _ = Rename(node), "F2");
            Item("Duplicate", () => _ = Duplicate(node));
            Item("Set or remove password…", () => _ = ChangePassword(node));
            Sep();
            Item("Check connection (ping)", () => _ = Ping(node));
            Item("Add to the application menu", () => _ = AddToMenu(node));
            Item("Use these settings for new sessions", () => SaveAsPreset(node));
            Item("Export to file…", () => _ = Export(node));
            Sep();
            Item("Delete", () => _ = Delete(node), "Delete");
        }
        else
        {
            Item("New SSH session here…", () => { node.IsSelected = true; _ = NewSession(Protocol.Ssh); }, enabled: node.Protocol == Protocol.Ssh);
            Item("New Remote Desktop session here…", () => { node.IsSelected = true; _ = NewSession(Protocol.Rdp); }, enabled: node.Protocol == Protocol.Rdp);
            Item("New folder…", () => _ = NewFolder(node));
            Sep();
            Item("Expand all", () => SetExpanded(node, true));
            Item("Collapse all", () => SetExpanded(node, false));
            Sep();
            Item("Export this folder…", () => _ = ExportList(node));
            Item("Import sessions here…", () => _ = ImportPicked(node));
            if (!node.IsRoot)
            {
                Sep();
                Item("Rename…", () => _ = Rename(node), "F2");
                Item("Delete", () => _ = Delete(node), "Delete");
            }
        }
        new ContextMenu { ItemsSource = items }.Open(_tree);
    }

    private static void SetExpanded(Node node, bool expanded)
    {
        node.IsExpanded = expanded || node.IsRoot;
        foreach (var c in node.Children.Where(c => c.IsFolder)) SetExpanded(c, expanded);
    }

    private List<(Guid Id, string Label)> JumpHostChoices(Node? exclude) =>
        _store.SshRoot.Descendants()
            .Where(n => n.IsSession && n != exclude && n.Settings?.JumpHostId != exclude?.Id)
            .Select(n => (n.Id, $"{n.Name}  ({n.Subtitle})"))
            .OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // ───────────── Actions ─────────────

    private async Task NewSession(Protocol protocol)
    {
        var folder = TargetFolder(protocol);
        var kind = protocol == Protocol.Ssh ? "SSH" : "Remote Desktop";
        var dlg = new SessionDialog(protocol, "", _store.PresetFor(protocol), _store.Data.Credentials, JumpHostChoices(null), false, _store.Data.Domains);
        if (!await dlg.ShowFor(this, $"New {kind} session")) return;

        var node = new Node { Kind = NodeKind.Session, Name = dlg.SessionName, Settings = dlg.Result };
        _store.AddChild(folder, node);
        RememberDomain(dlg.Result);
        await ApplyPassword(node, dlg);
        node.IsSelected = true;
        Save();
    }

    private async Task EditSession(Node node)
    {
        bool has = await Task.Run(() => CredentialStore.Has(node.Id));
        var dlg = new SessionDialog(node.Protocol, node.Name, node.Settings!, _store.Data.Credentials, JumpHostChoices(node), has, _store.Data.Domains);
        if (!await dlg.ShowFor(this, $"Edit session — {node.Name}")) return;
        node.Name = dlg.SessionName;
        node.Settings = dlg.Result;
        node.NotifySettingsChanged();
        SessionStore.Sort(node.Parent!);
        RememberDomain(dlg.Result);
        await ApplyPassword(node, dlg);
        Save();
    }

    private void RememberDomain(SessionSettings s)
    {
        var d = DomainName.Clean(s.Domain);
        if (d.Length > 0 && !_store.Data.Domains.Contains(d, StringComparer.OrdinalIgnoreCase))
        {
            _store.Data.Domains.Add(d);
            _store.Data.Domains.Sort(StringComparer.CurrentCultureIgnoreCase);
        }
    }

    private async Task ApplyPassword(Node node, SessionDialog dlg)
    {
        try
        {
            if (dlg.NewPassword != null) CredentialStore.Set(node.Id, dlg.NewPassword, node.Settings!.Username);
            else if (dlg.ForgetPassword) CredentialStore.Delete(node.Id);
        }
        catch (Exception ex) { await Error("Could not update the saved password", ex); }
    }

    private async Task ConnectAs(Node node)
    {
        var s = node.Settings!;
        var domain = new TextBox { Text = node.Protocol == Protocol.Rdp ? s.Domain : "", Watermark = "Domain (optional)" };
        var user = new TextBox { Text = ProfileFor(s)?.Username ?? s.Username, Watermark = "User name" };
        var pw = new TextBox { PasswordChar = '•', Watermark = "Password" };
        var body = new StackPanel { Spacing = 8 };
        if (node.Protocol == Protocol.Rdp) body.Children.Add(domain);
        body.Children.Add(user);
        body.Children.Add(pw);
        body.Children.Add(new TextBlock { Text = "Used for this connection only; nothing is saved.", Classes = { "sub" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var d = new Dialogs.DialogWindow($"Connect to {node.Name} as", body);
        d.AddButton("Connect", isDefault: true, accent: true);
        d.AddButton("Cancel", isCancel: true);
        d.Validate = () => !string.IsNullOrWhiteSpace(user.Text);
        d.Opened += (_, _) => (string.IsNullOrEmpty(user.Text) ? user : pw).Focus();
        if (await d.ShowFor(this) != 0) return;
        OpenSession(node, (DomainName.Clean(domain.Text), user.Text!.Trim(), pw.Text ?? ""));
    }

    private async Task ChangePassword(Node node)
    {
        var id = node.Id;
        var name = node.Name;
        if (ProfileFor(node.Settings!) is { } profile)
        {
            int count = _store.Data.Roots.SelectMany(r => r.Descendants()).Count(n => n.Settings?.CredentialId == profile.Id);
            if (!await Dialogs.Confirm(this, $"\"{node.Name}\" uses the credential profile \"{profile.Name}\".\n\n" +
                    $"Changing the password changes it for all {count} session{(count == 1 ? "" : "s")} using that profile. Continue?",
                    "Change password", "Continue", "Cancel")) return;
            id = profile.Id;
            name = profile.Name;
        }
        bool has = await Task.Run(() => CredentialStore.Has(id));
        var pw = new TextBox { PasswordChar = '•', Watermark = "New password" };
        var body = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = has ? $"A password is saved for \"{name}\"." : $"No password is saved for \"{name}\".", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, pw } };
        var d = new Dialogs.DialogWindow("Password", body);
        d.AddButton("Save", isDefault: true, accent: true);
        if (has) d.AddButton("Remove saved password");
        d.AddButton("Cancel", isCancel: true);
        d.Opened += (_, _) => pw.Focus();
        var r = await d.ShowFor(this);
        try
        {
            if (r == 0 && !string.IsNullOrEmpty(pw.Text))
            {
                CredentialStore.Set(id, pw.Text, node.Settings!.Username);
                await Info("Password saved.");
            }
            else if (r == 1 && has)
            {
                CredentialStore.Delete(id);
                await Info("Saved password removed. You'll be asked for it when connecting.");
            }
        }
        catch (Exception ex) { await Error("Could not change the password", ex); }
    }

    private async Task NewFolder(Node parent)
    {
        var name = await Dialogs.PromptText(this, "New folder", "Folder name:");
        if (name == null) return;
        var folder = new Node { Kind = NodeKind.Folder, Name = name, IsExpanded = true };
        _store.AddChild(parent, folder);
        folder.IsSelected = true;
        Save();
    }

    private async Task Rename(Node node)
    {
        var what = node.IsFolder ? "folder" : "session";
        var name = await Dialogs.PromptText(this, $"Rename {what}", "New name:", node.Name);
        if (name == null || name == node.Name) return;
        node.Name = name;
        SessionStore.Sort(node.Parent!);
        Save();
    }

    private async Task Delete(Node node)
    {
        string question;
        if (node.IsSession) question = $"Delete the session \"{node.Name}\"?";
        else
        {
            int count = node.Descendants().Count(n => n.IsSession);
            question = count == 0
                ? $"Delete the folder \"{node.Name}\"?"
                : $"Delete the folder \"{node.Name}\" and the {count} session{(count == 1 ? "" : "s")} in it?";
        }
        var deleted = node.Descendants().Prepend(node).Where(n => n.IsSession).ToList();
        var deletedIds = deleted.Select(n => n.Id).ToHashSet();
        var dependents = _store.Data.Roots.SelectMany(r => r.Descendants())
            .Where(n => n.Settings?.JumpHostId is Guid j && deletedIds.Contains(j) && !deletedIds.Contains(n.Id))
            .ToList();
        if (dependents.Count > 0)
            question += $"\n\n{dependents.Count} other session{(dependents.Count == 1 ? " uses" : "s use")} it as a jump host " +
                        "and will connect directly afterwards.";
        if (!await Dialogs.Confirm(this, question + "\n\nSaved passwords are removed too.", "Delete", "Delete", "Cancel", defaultNo: true)) return;

        await Task.Run(() =>
        {
            foreach (var s in deleted)
            {
                try { CredentialStore.Delete(s.Id); } catch { }
                DesktopEntry.Remove(s.Id);
            }
        });
        foreach (var d in dependents) d.Settings!.JumpHostId = null;
        _store.Remove(node);
        Save();
    }

    private async Task Duplicate(Node node)
    {
        var copy = new Node { Kind = NodeKind.Session, Name = node.Name + " (copy)", Settings = node.Settings!.Clone() };
        _store.AddChild(node.Parent!, copy);
        var password = await Task.Run(() => CredentialStore.Get(node.Id));
        if (password != null)
        {
            try { CredentialStore.Set(copy.Id, password, copy.Settings.Username); } catch { }
        }
        copy.IsSelected = true;
        Save();
    }

    private void SaveAsPreset(Node node)
    {
        var preset = node.Settings!.Clone();
        preset.Host = "";
        preset.Username = "";
        preset.Domain = "";
        preset.CredentialId = null;
        preset.JumpHostId = null;
        preset.PrivateKeyPath = "";
        preset.Tunnels = new();
        _store.Data.Presets[node.Protocol] = preset;
        Save();
        _ = Info($"New {(node.Protocol == Protocol.Ssh ? "SSH" : "Remote Desktop")} sessions will start with these settings (port, display and terminal options).");
    }

    private async Task AddToMenu(Node node)
    {
        try
        {
            var path = DesktopEntry.Create(node);
            await Info($"\"{node.Name}\" is now in your application menu (launcher search finds it too).\n\n{path}");
        }
        catch (Exception ex) { await Error("Could not create the menu entry", ex); }
    }

    private async Task Ping(Node node)
    {
        var s = node.Settings!;
        var lines = new List<string>();
        var ping = Task.Run(async () =>
        {
            try
            {
                using var p = new Ping();
                var reply = await p.SendPingAsync(s.Host, 2000);
                return reply.Status == IPStatus.Success ? $"Ping: reply in {reply.RoundtripTime} ms" : $"Ping: {reply.Status}";
            }
            catch (Exception ex) { return "Ping: " + (ex.InnerException?.Message ?? ex.Message); }
        });
        var tcp = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var c = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await c.ConnectAsync(s.Host, s.Port, cts.Token);
                return $"Port {s.Port}: open ({sw.ElapsedMilliseconds} ms)";
            }
            catch (OperationCanceledException) { return $"Port {s.Port}: no answer (timed out)"; }
            catch (Exception ex) { return $"Port {s.Port}: {ex.Message}"; }
        });
        lines.Add(await ping);
        lines.Add(await tcp);
        if (s.JumpHostId != null) lines.Add("\nThis session goes through a jump host, so it may not be reachable directly from here.");
        await Info($"{s.Host}\n\n{string.Join("\n", lines)}");
    }
}
