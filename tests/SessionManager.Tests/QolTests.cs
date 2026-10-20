using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SessionManager;

/// <summary>Wake-on-LAN, the online check, daily backups and the new saved fields.</summary>
static class QolTests
{
    public static async Task Run(Action<string, bool, string> check)
    {
        // ── Wake-on-LAN ──
        foreach (var text in new[] { "AA:BB:CC:DD:EE:0F", "aa-bb-cc-dd-ee-0f", "AABB.CCDD.EE0F", "aabbccddee0f", " AA BB CC DD EE 0F " })
            check($"MAC \"{text}\" is read", WakeOnLan.TryParseMac(text, out var m) && WakeOnLan.FormatMac(m) == "AA:BB:CC:DD:EE:0F", "");
        foreach (var bad in new[] { "", "AA:BB:CC:DD:EE", "GG:BB:CC:DD:EE:FF", "00:00:00:00:00:00", "FF:FF:FF:FF:FF:FF", "AA:BB:CC:DD:EE:FF:00" })
            check($"\"{bad}\" is not a MAC", !WakeOnLan.TryParseMac(bad, out _), "");
        WakeOnLan.TryParseMac("01:23:45:67:89:AB", out var mac);
        var packet = WakeOnLan.MagicPacket(mac);
        check("magic packet: 6 x FF, then the address 16 times", packet.Length == 102 && packet.Take(6).All(b => b == 0xFF)
            && Enumerable.Range(0, 16).All(i => packet.Skip(6 + i * 6).Take(6).SequenceEqual(mac)), "");

        // The packet really goes out: listen on the loopback for one sent straight to 127.0.0.1 (port 9 needs no admin rights to bind? use 7 or 9 if free).
        UdpClient? listener = null;
        foreach (var port in new[] { 9, 7 })
        {
            try { listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)); break; } catch (SocketException) { }
        }
        if (listener != null)
        {
            using (listener)
            {
                var receive = listener.ReceiveAsync();
                int sent = await WakeOnLan.SendAsync(mac, "127.0.0.1");
                var got = await Task.WhenAny(receive, Task.Delay(3000)) == receive ? receive.Result.Buffer : null;
                check("wake-up packet is sent (also straight to the host's address)", sent > 0 && got != null && got.SequenceEqual(packet), $"sent={sent}");
            }
        }
        check("the address of an unknown host isn't made up", await WakeOnLan.FindMacAsync("host.invalid") == null, "");

        // ── Online check ──
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int open = ((IPEndPoint)tcp.LocalEndpoint).Port;
        var (online, ms, _) = await HostProbe.CheckAsync("127.0.0.1", open, TimeSpan.FromSeconds(2));
        check("a listening port is online", online && ms >= 0, $"{online} {ms}");
        tcp.Stop();
        var (online2, _, error2) = await HostProbe.CheckAsync("127.0.0.1", open, TimeSpan.FromSeconds(6)); // Windows retries a refused connection for about 2 s
        check("a closed port is offline, with the reason", !online2 && error2 == "the port is closed", error2 ?? "");
        var (online3, _, error3) = await HostProbe.CheckAsync("no-such-host.invalid", 22, TimeSpan.FromSeconds(3));
        check("an unknown name is offline", !online3 && error3 != null, error3 ?? "");

        // ── Saved fields ──
        var node = new Node { Kind = NodeKind.Session, Name = "x", IsFavorite = true, LastUsed = new DateTime(2026, 10, 20, 9, 30, 0), Reach = Reachability.Online,
            Settings = new SessionSettings { Host = "x", MacAddress = "AA:BB:CC:DD:EE:FF" } };
        var json = JsonSerializer.Serialize(node, SessionStore.JsonOptions);
        var back = JsonSerializer.Deserialize<Node>(json, SessionStore.JsonOptions)!;
        check("favourite, last used and MAC are saved; online status isn't", back.IsFavorite && back.LastUsed == node.LastUsed
            && back.Settings!.MacAddress == "AA:BB:CC:DD:EE:FF" && back.Reach == Reachability.Unknown && !json.Contains("Reach"), "");
        check("saved commands keep their order and settings",
            JsonSerializer.Deserialize<AppData>(JsonSerializer.Serialize(new AppData { Commands = { new() { Name = "a", Text = "uptime", Run = true }, new() { Name = "b", Text = "ls -la", Run = false } } }, SessionStore.JsonOptions), SessionStore.JsonOptions)!
                .Commands is [{ Name: "a", Text: "uptime", Run: true }, { Name: "b", Run: false }], "");
        check("online dots are on by default", new AppData().ShowStatus, "");

        // ── Daily backups ── (SESSIONMANAGER_DATA points at a temp folder for the whole test run)
        if (!SessionStore.DataDir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
        {
            check("backups are tested in a temp folder", false, SessionStore.DataDir);
            return;
        }
        if (Directory.Exists(SessionBackup.Folder)) Directory.Delete(SessionBackup.Folder, true);
        File.Delete(SessionStore.FilePath);
        var store = new SessionStore();
        store.Load();
        store.Data.Theme = "Dark";
        void Add(string name)
        {
            store.AddChild(store.SshRoot, new Node { Kind = NodeKind.Session, Name = name, Settings = new SessionSettings { Host = name, Port = 22 } });
            store.Save();
        }
        Add("first");
        check("nothing to back up before the first save", SessionBackup.List().Count == 0, "");
        Add("second");
        var today = SessionBackup.List();
        check("the first save of the day backs up the list as it was", today is [{ Sessions: 1 }] && today[0].Date == DateTime.Today,
            string.Join(",", today.Select(b => $"{b.Date:d}:{b.Sessions}")));
        Add("third");
        check("one backup a day", SessionBackup.List() is [{ Sessions: 1 }], string.Join(",", SessionBackup.List().Select(b => b.Sessions)));
        for (int d = 1; d <= 40; d++) SessionBackup.MakeDaily(DateTime.Today.AddDays(-d)); // pretend the app was used on 40 earlier days
        SessionBackup.MakeDaily(DateTime.Today.AddDays(1));
        var all = SessionBackup.List();
        check($"only the newest {SessionBackup.Keep} backups are kept", all.Count == SessionBackup.Keep && all[0].Date == DateTime.Today.AddDays(1)
            && all[^1].Date == DateTime.Today.AddDays(2 - SessionBackup.Keep), $"{all.Count}: {all[0].Date:d} .. {all[^1].Date:d}");
        check("a backup knows how many sessions it holds", all[0].Sessions == 3, all[0].Sessions.ToString());

        // Restore an older day: sessions go back, the look stays, the current file is kept.
        var older = Path.Combine(SessionBackup.Folder, $"sessions-{DateTime.Today.AddDays(-3):yyyy-MM-dd}.json");
        File.WriteAllText(older, """{"Theme":"Light","Roots":[{"Kind":"Folder","Protocol":"Ssh","IsRoot":true,"Name":"SSH sessions","Children":[{"Kind":"Session","Name":"old","Settings":{"Host":"old","Port":22}}]}]}""");
        store.Restore(older);
        var reloaded = new SessionStore();
        reloaded.Load();
        check("restoring brings the old sessions back", string.Join(",", reloaded.SshRoot.Children.Select(n => n.Name)) == "old" && reloaded.Data.Roots.Count == 4,
            string.Join(",", reloaded.SshRoot.Children.Select(n => n.Name)));
        check("restoring keeps the current look (theme)", reloaded.Data.Theme == "Dark", reloaded.Data.Theme);
        check("the list from before the restore is kept as a file",
            Directory.GetFiles(SessionStore.DataDir, "sessions.json.before-restore-*").Length == 1, "");
        File.WriteAllText(older, "{ not json");
        check("a broken backup is refused", Throws(() => store.Restore(older)) is InvalidDataException, "");
    }

    private static Exception? Throws(Action a)
    {
        try { a(); return null; } catch (Exception e) { return e; }
    }
}
