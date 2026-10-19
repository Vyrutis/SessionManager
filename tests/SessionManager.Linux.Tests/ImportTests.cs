using SessionManager;

static class ImportTests
{
    public static void Run(Action<string, bool, string> check)
    {
        void Check(string n, bool ok, string d = "") => check(n, ok, d);
        var dir = Path.Combine(Path.GetTempPath(), "sm-import-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // ── Remmina ──
            File.WriteAllText(Path.Combine(dir, "a.remmina"), """
                [remmina]
                name=Domain controller
                protocol=RDP
                server=dc01.corp.local:3390
                username=CORP\jsmith
                group=Office/Servers
                resolution_mode=0
                resolution_width=1600
                resolution_height=900
                colordepth=16
                sharefolder=/home/me
                password=.encrypted-blob.
                """);
            File.WriteAllText(Path.Combine(dir, "b.remmina"), """
                [remmina]
                name=web
                protocol=SSH
                server=10.0.0.5
                username=admin
                ssh_privatekey=/home/me/.ssh/id_ed25519
                """);
            File.WriteAllText(Path.Combine(dir, "c.remmina"), "[remmina]\nname=vnc box\nprotocol=VNC\nserver=10.0.0.9\n");
            var rem = ConnectionImport.FromRemminaFiles(Directory.GetFiles(dir, "*.remmina").OrderBy(f => f));
            var dc = rem.FirstOrDefault(r => r.Name == "Domain controller");
            Check("Remmina: RDP, SSH and VNC found", rem.Count == 3, rem.Count.ToString());
            var rv = rem.FirstOrDefault(r => r.Protocol == Protocol.Vnc);
            Check("Remmina: VNC on the default port", rv is { Name: "vnc box", Host: "10.0.0.9", Port: 5900, Username: "" }, rv == null ? "missing" : $"{rv.Host}:{rv.Port}");
            Check("Remmina: RDP fields", dc is { Protocol: Protocol.Rdp, Host: "dc01.corp.local", Port: 3390, Username: "jsmith", Domain: "CORP", Folder: "Office/Servers" },
                dc == null ? "" : $"{dc.Host}:{dc.Port} {dc.Domain}\\{dc.Username} [{dc.Folder}]");
            Check("Remmina: display options", dc?.Template is { Width: 1600, Height: 900, ColorDepth: 16, RedirectDrives: true });
            Check("Remmina: passwords are not read", rem.All(r => r.Password == null));
            var web = rem.FirstOrDefault(r => r.Name == "web");
            Check("Remmina: SSH with key", web is { Protocol: Protocol.Ssh, Host: "10.0.0.5", Port: 22, Username: "admin", PrivateKeyPath: "/home/me/.ssh/id_ed25519" });
            var (items, source) = ConnectionImport.FromFile(Path.Combine(dir, "a.remmina"));
            Check("Remmina: detected by FromFile", source == "Remmina" && items.Count == 1);

            // ── PuTTY on Linux ──
            var putty = Path.Combine(dir, "putty");
            Directory.CreateDirectory(putty);
            File.WriteAllText(Path.Combine(putty, "My%20router"), "HostName=root@192.168.1.1\nPortNumber=2222\nProtocol=ssh\nPublicKeyFile=/home/me/key.ppk\n");
            File.WriteAllText(Path.Combine(putty, "Default%20Settings"), "HostName=\nProtocol=ssh\n");
            File.WriteAllText(Path.Combine(putty, "serial"), "HostName=/dev/ttyS0\nProtocol=serial\n");
            var p = ConnectionImport.FromPuttyFiles(putty);
            Check("PuTTY files", p.Count == 1 && p[0] is { Name: "My router", Host: "192.168.1.1", Port: 2222, Username: "root", PrivateKeyPath: "/home/me/key.ppk" },
                p.Count == 1 ? $"{p[0].Name} {p[0].Username}@{p[0].Host}:{p[0].Port}" : p.Count.ToString());

            // ── ssh config (shared code, quick check on Linux paths) ──
            var cfg = Path.Combine(dir, "config");
            File.WriteAllText(cfg, "Host web\n  HostName 10.1.1.1\n  User deploy\n  Port 2200\n  IdentityFile ~/.ssh/deploy\nHost *\n  ServerAliveInterval 30\n");
            var s = ConnectionImport.FromSshConfig(cfg);
            Check("ssh config", s.Count == 1 && s[0] is { Name: "web", Host: "10.1.1.1", Username: "deploy", Port: 2200 });

            // ── Tesseract TSV → rows → connections ──
            var tsv = string.Join("\n",
                "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext",
                "1\t1\t0\t0\t0\t0\t0\t0\t800\t200\t-1\t",
                Word(40, 20, "Server"), Word(300, 20, "IP"), Word(480, 20, "User"), Word(640, 20, "Password"),
                Word(40, 60, "web01"), Word(300, 60, "10.20.30.40"), Word(480, 60, "admin"), Word(640, 60, "Blue#Sky42"),
                Word(40, 100, "dc01"), Word(110, 100, "(RDP)"), Word(300, 100, "10.20.30.42"), Word(480, 100, @"CORP\jsmith"), Word(640, 100, "Winter2026"));
            static string Word(int x, int y, string text) => $"5\t1\t1\t1\t1\t1\t{x}\t{y}\t{text.Length * 11}\t22\t91.5\t{text}";
            var words = TesseractOcr.ParseTsv(tsv);
            Check("TSV words", words.Count == 13, words.Count.ToString());
            var found = ScreenshotScan.FromPages([ScreenshotScan.ToRows(words)]).Items;
            var w1 = found.FirstOrDefault(f => f.Host == "10.20.30.40");
            var d1 = found.FirstOrDefault(f => f.Host == "10.20.30.42");
            Check("TSV table → SSH row with login", w1 is { Username: "admin", Password: "Blue#Sky42" }, w1 == null ? "missing" : $"{w1.Username}/{w1.Password}");
            Check("TSV table → RDP row with domain", d1 is { Protocol: Protocol.Rdp, Domain: "CORP", Username: "jsmith", Password: "Winter2026" },
                d1 == null ? "missing" : $"{d1.Protocol} {d1.Domain}\\{d1.Username}/{d1.Password}");

            // ── VNC ──
            Check("VNC address: display number", ConnectionImport.ParseVncAddress("pc01:1") == ("pc01", 5901));
            Check("VNC address: double colon = port", ConnectionImport.ParseVncAddress("10.0.0.7::5999") == ("10.0.0.7", 5999));
            Check("VNC address: port", ConnectionImport.ParseVncAddress("10.0.0.7:5902") == ("10.0.0.7", 5902));
            Check("VNC address: none", ConnectionImport.ParseVncAddress("10.0.0.7") == ("10.0.0.7", 5900));
            var ultra = Path.Combine(dir, "Reception PC.vnc");
            File.WriteAllText(ultra, "[connection]\r\nhost=192.168.5.20\r\nport=5901\r\nproxyhost=\r\nproxyport=5900\r\npassword=a1b2c3d4e5f60718\r\n[options]\r\nviewonly=1\r\nautoScaling=1\r\n");
            var (uv, uvSource) = ConnectionImport.FromFile(ultra);
            Check("UltraVNC .vnc file", uvSource == "VNC viewer file" && uv.Count == 1 && uv[0] is { Protocol: Protocol.Vnc, Name: "Reception PC", Host: "192.168.5.20", Port: 5901, Password: null }
                && uv[0].Template is { ViewOnly: true, SmartSizing: true }, uv.Count == 1 ? $"{uv[0].Host}:{uv[0].Port}" : uv.Count.ToString());
            var real = Path.Combine(dir, "lab.vnc");
            File.WriteAllText(real, "ConnMethod=tcp\nHost=lab-mac:2\nScaling=None\n");
            var rv2 = ConnectionImport.FromVncFile(real);
            Check("RealVNC .vnc file (display number)", rv2.Count == 1 && rv2[0] is { Host: "lab-mac", Port: 5902 } && rv2[0].Template?.SmartSizing == false);
            var vcsv = Path.Combine(dir, "v.csv");
            File.WriteAllText(vcsv, "Name,Host,Port,Protocol,Password\nkiosk,10.9.9.9,,UltraVNC,pw1\nscreen,10.9.9.8,5901,,pw2\n");
            var vc = ConnectionImport.FromCsv(vcsv);
            Check("CSV: VNC by protocol name and by port", vc.Count == 2 && vc.All(c => c.Protocol == Protocol.Vnc) && vc[0].Port == 5900 && vc[1].Port == 5901 && vc[0].Password == "pw1",
                string.Join(" ", vc.Select(c => $"{c.Protocol}:{c.Port}")));
            var scanned = ScreenshotScan.Extract("Reception PC (UltraVNC)\n10.1.2.3\nPassword: Kiosk#1\n\nWindows server\n10.1.2.4 RDP\nUser: admin\n");
            var sv = scanned.FirstOrDefault(c => c.Host == "10.1.2.3");
            Check("Scan: VNC machine with its password", sv is { Protocol: Protocol.Vnc, Port: 5900, Password: "Kiosk#1", Username: "" },
                sv == null ? "missing" : $"{sv.Protocol}:{sv.Port} {sv.Password}");
            Check("Scan: RDP next to it stays RDP", scanned.FirstOrDefault(c => c.Host == "10.1.2.4")?.Protocol == Protocol.Rdp);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

static class FreeRdpTests
{
    public static void Run(Action<string, bool, string> check)
    {
        void Check(string n, bool ok, string d = "") => check(n, ok, d);
        var s = SessionSettings.DefaultsFor(Protocol.Rdp);
        s.Host = "srv";
        s.Username = "jsmith";
        s.Domain = "CORP";
        s.RedirectDrives = true;
        var v3 = new FreeRdp.Client("/usr/bin/xfreerdp3", 3);
        var a = FreeRdp.Arguments(v3, new FreeRdp.Options { Server = "srv", Port = 3389, Settings = s, Password = "p@ss word", Title = "My PC", FitSize = (1920, 1080) });
        Check("v3: address, user, domain, password", a.Contains("/v:srv:3389") && a.Contains("/u:jsmith") && a.Contains("/d:CORP") && a.Contains("/p:p@ss word"), string.Join(" ", a));
        Check("v3: fit to window", a.Contains("/size:1920x1080") && a.Contains("/dynamic-resolution"));
        Check("v3: home drive, clipboard, tofu, title", a.Contains("+home-drive") && a.Contains("+clipboard") && a.Contains("/cert:tofu") && a.Contains("/title:My PC"));

        s.Width = 1280; s.Height = 720; s.SmartSizing = true;
        var v2 = new FreeRdp.Client("/usr/bin/xfreerdp", 2);
        var b = FreeRdp.Arguments(v2, new FreeRdp.Options { Server = "fe80::1", Port = 3390, Settings = s, Password = "x", Title = "t", ExtraArgs = "/scale:140 '/gt:my gw'" });
        Check("v2: password only via stdin", !b.Any(x => x.StartsWith("/p:")) && b.Contains("/from-stdin:force"), string.Join(" ", b));
        Check("IPv6 address in brackets", b.Contains("/v:[fe80::1]:3390"));
        Check("fixed size with scaling", b.Contains("/size:1280x720") && b.Contains("/smart-sizing") && !b.Contains("/dynamic-resolution"));
        Check("extra arguments with quotes", b.Contains("/scale:140") && b.Contains("/gt:my gw"));
        Check("embedding adds parent window", FreeRdp.Arguments(v3, new FreeRdp.Options { Server = "a", Port = 1, Settings = s, Title = "t", ParentWindow = 0x2a00007 }).Contains("/parent-window:44040199"));
        Check("SDL client gets no X11-only options", !FreeRdp.Arguments(new FreeRdp.Client("/usr/bin/sdl-freerdp3", 3), new FreeRdp.Options { Server = "a", Port = 1, Settings = s, Title = "t" }).Any(x => x.StartsWith("/wm-class")));
        Check("exit codes explained", FreeRdp.ExitReason(0) == null && FreeRdp.ExitReason(132)!.Contains("password") && FreeRdp.IsAuthFailure(132));
    }
}
