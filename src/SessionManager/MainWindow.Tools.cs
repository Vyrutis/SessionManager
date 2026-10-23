using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SessionManager;

/// <summary>Wake-on-LAN, editing several sessions at once, saved SSH commands and the duplicate check.</summary>
public partial class MainWindow
{
    // ───────────── Wake-on-LAN ─────────────

    private async void WakeUp(Node node)
    {
        var s = node.Settings!;
        string mac = s.MacAddress;
        bool found = false;
        if (!WakeOnLan.TryParseMac(mac, out _))
        {
            // Look in this PC's address table: works when the computer is on the same network and was reachable lately.
            Mouse.OverrideCursor = Cursors.Wait;
            try { mac = await WakeOnLan.FindMacAsync(s.Host) ?? ""; found = mac.Length > 0; }
            catch { mac = ""; }
            finally { Mouse.OverrideCursor = null; }
        }
        if (Dialogs.WakeUp(this, node.Name, s.Host, mac, found) is not { } answer) return;
        WakeOnLan.TryParseMac(answer.Mac, out var bytes);
        var formatted = WakeOnLan.FormatMac(bytes);
        if (s.MacAddress != formatted && node.Parent != null)
        {
            s.MacAddress = formatted;
            Save();
        }

        int sent;
        try { sent = await WakeOnLan.SendAsync(bytes, s.Host); }
        catch (Exception ex) { Error("Could not send the wake-up packet", ex); return; }
        if (sent == 0)
        {
            Info("The wake-up packet could not be sent on any network of this PC.");
            return;
        }
        if (!answer.Connect)
        {
            Info($"Wake-up sent to {formatted}.\n\nIt can take a minute or two for {node.Name} to start.");
            return;
        }

        // Connect as soon as the session's port answers (up to 3 minutes); the list shows that it's waking up.
        node.Reach = Reachability.Unknown;
        var until = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < until && IsLoaded)
        {
            node.ReachText = "Waking up…";
            var (online, _, _) = await Probe(s.Host, s.Port, CancellationToken.None);
            if (online)
            {
                MarkConnected(node);
                OpenSession(node);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        if (IsLoaded)
            Info($"{node.Name} did not answer within 3 minutes.\n\nWake-on-LAN must be turned on in its BIOS / network card settings, " +
                 "and it usually only works on the same network (or through a router that forwards it).");
    }

    // ───────────── Several sessions at once ─────────────

    private void EditMany(List<Node> sessions)
    {
        sessions = sessions.Where(n => n.IsSession && n.Settings != null).ToList();
        if (sessions.Count == 0) return;
        var protocol = sessions[0].Protocol;
        var exclude = sessions.Select(n => n.Id).ToHashSet();
        var jumps = protocol is Protocol.Ssh or Protocol.Rdp or Protocol.Vnc or Protocol.Web
            ? JumpHostChoices(null).Where(j => !exclude.Contains(j.Id)).ToList() : new();
        if (Dialogs.EditMany(this, sessions.Count, protocol, _store.Data.Credentials, jumps) is not { } change) return;
        int passwordsFailed = ApplyToMany(sessions, change);
        Info($"Changed {sessions.Count} sessions." +
             (passwordsFailed > 0 ? $"\n\n{passwordsFailed} password{(passwordsFailed == 1 ? "" : "s")} could not be saved." : "") +
             (change.Password != null && sessions.Any(n => n.Settings!.CredentialId != null)
                 ? "\n\nSessions that use a credential profile keep the profile's password." : ""));
    }

    /// <summary>Applies the ticked settings to every session; returns how many passwords could not be saved.</summary>
    private int ApplyToMany(List<Node> sessions, Dialogs.BulkChange change)
    {
        var protocol = sessions[0].Protocol;
        int passwordsFailed = 0;
        foreach (var n in sessions)
        {
            var s = n.Settings!;
            if (change.SetProfile)
            {
                s.CredentialId = change.Profile?.Id;
                if (change.Profile is { } p) { s.Username = p.Username; s.Domain = protocol == Protocol.Rdp ? p.Domain : ""; }
            }
            if (change.Username != null && s.CredentialId == null)
            {
                var (domain, user) = DomainPicker.Split(change.Username);
                if (protocol == Protocol.Rdp) { s.Username = user; if (domain.Length > 0) s.Domain = domain; }
                else s.Username = protocol == Protocol.Vnc ? "" : change.Username;
            }
            if (change.Password != null && s.CredentialId == null)
            {
                try { CredentialStore.Set(n.Id, change.Password, s.Username); }
                catch { passwordsFailed++; }
            }
            if (change.Port is int port) s.Port = port;
            if (change.SetJump) s.JumpHostId = change.Jump == n.Id ? null : change.Jump;
            n.NotifySettingsChanged();
        }
        Save();
        CheckReachability(force: true);
        return passwordsFailed;
    }

    // ───────────── Saved SSH commands ─────────────

    /// <summary>"Send command" in an SSH tab's menu: the saved commands, then saving and managing them.</summary>
    private MenuItem CommandsMenu(OpenTab tab, SshTerminalView terminal)
    {
        var menu = Item("Send command", "", () => { }, color: "SshBrush");
        foreach (var c in _store.Data.Commands)
        {
            var item = Item(c.Name.Length > 0 ? c.Name : c.Text, "", () => SendCommand(tab, terminal, c));
            item.ToolTip = c.Text + (c.Run ? "" : "\n(typed only; Enter is not pressed)");
            menu.Items.Add(item);
        }
        if (_store.Data.Commands.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(Item("Save a new command…", "", () => { if (Dialogs.EditCommand(this, null) is { } c) { _store.Data.Commands.Add(c); Save(); } }));
        menu.Items.Add(Item("Manage saved commands…", "", () => { if (Dialogs.ManageCommands(this, _store.Data.Commands)) Save(); },
            enabled: _store.Data.Commands.Count > 0));
        return menu;
    }

    /// <summary>Types the command (and Enter) into the terminal; with multi-exec on, into every included terminal.</summary>
    private void SendCommand(OpenTab tab, SshTerminalView terminal, SavedCommand command)
    {
        var text = CommandInput(command);
        var tabs = _multiExec && tab.IsBroadcasting
            ? _tabs.Where(t => t.IsBroadcasting && t.View is SshTerminalView { Status: TabStatus.Connected }).ToList()
            : new List<OpenTab> { tab };
        var targets = tabs.Select(t => (SshTerminalView)t.View).ToList();
        if (targets.Count > 1 && command.Run)
        {
            var list = string.Join("\n", tabs.Take(10).Select(t => "  " + t.Title)) + (tabs.Count > 10 ? $"\n  … and {tabs.Count - 10} more" : "");
            if (MessageBox.Show(this, $"Run \"{command.Name}\" on {targets.Count} terminals at once?\n\n{list}", "Multi-exec",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
        }
        foreach (var t in targets) t.SendInput(text);
        terminal.FocusView();
    }

    /// <summary>What is typed for a command: lines end with Enter (CR) like a keyboard sends; Run adds the last Enter.</summary>
    internal static string CommandInput(SavedCommand command) =>
        command.Text.Replace("\r\n", "\r").Replace('\n', '\r') + (command.Run ? "\r" : "");

    // ───────────── Duplicates ─────────────

    /// <summary>Another saved session for the same address (and the same user, or for web sessions the same page); null if none.</summary>
    private Node? FindDuplicate(Protocol p, SessionSettings s, Node? except)
    {
        string User(SessionSettings x) => ProfileFor(x)?.Username ?? x.Username;
        return _store.RootFor(p).Descendants().FirstOrDefault(n => n.IsSession && n != except && n.Settings is { } o
            && o.Host.Equals(s.Host, StringComparison.OrdinalIgnoreCase) && o.Port == s.Port
            && (p == Protocol.Web
                ? o.WebHttps == s.WebHttps && string.Equals(o.WebPath.TrimEnd('/'), s.WebPath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                : User(o).Equals(User(s), StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Asks before adding a second session for the same address; false = don't add (the existing one is selected).</summary>
    private bool ConfirmNotDuplicate(Protocol p, SessionSettings s, Node? except)
    {
        if (FindDuplicate(p, s, except) is not { } dup) return true;
        var where = dup.Parent is { IsRoot: false } f ? $" in the folder \"{f.Name}\"" : "";
        if (AskDuplicate(this, $"You already have \"{dup.Name}\"{where} for {dup.Subtitle}.\n\nAdd another session for it anyway?")) return true;
        Select(dup);
        return false;
    }

    /// <summary>Asks about a duplicate; replaced in tests.</summary>
    private static Func<Window, string, bool> AskDuplicate = (owner, text) =>
        MessageBox.Show(owner, text, "Session Manager", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
