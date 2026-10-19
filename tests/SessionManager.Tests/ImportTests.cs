using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SessionManager;

static class ImportTests
{
    public static void Run(Action<string, bool, string> check, string dir)
    {
        void Check(string n, bool ok, string d = "") => check(n, ok, d);
        static string Dump(IEnumerable<ImportedConnection> l) =>
            string.Join(" | ", l.Select(c => $"{c.Protocol} {c.Name} {c.Host}:{c.Port} u={c.Domain}\\{c.Username} p={c.Password}"));

        // ── Scanner text extraction ──
        var r = ScreenshotScan.Extract("Server list\nName\tIP\tUser\tPassword\nWeb\t10.0.0.5\tadmin\tS3cret!\nDB\t10.0.0.6\troot\tdbPass#1\nDC01 RDP\t10.0.0.7\tCORP\\jdoe\tWin#2024");
        Check("table with header: 3 hosts", r.Count == 3, Dump(r));
        Check("table: user/password per row", r.Count == 3 && r[0] is { Host: "10.0.0.5", Username: "admin", Password: "S3cret!", Name: "Web" }
              && r[1] is { Username: "root", Password: "dbPass#1" }, Dump(r));
        Check("table: RDP row with domain", r.Count == 3 && r[2] is { Protocol: Protocol.Rdp, Domain: "CORP", Username: "jdoe", Password: "Win#2024", Port: 3389 }, Dump(r));

        r = ScreenshotScan.Extract("10.1.1.1   admin   pass1\n10.1.1.2   root   pass2");
        Check("no header: values beside the IP", r.Count == 2 && r[0] is { Username: "admin", Password: "pass1" } && r[1] is { Username: "root", Password: "pass2" }, Dump(r));

        r = ScreenshotScan.Extract("Web server: 192.168.1.10\nUsername: deploy\nPassword: abc123\n\nRDP jump: jump.corp.local\nLogin: CORP\\admin\nPassword: Pa55");
        Check("labelled blocks", r.Count == 2 && r[0] is { Host: "192.168.1.10", Username: "deploy", Password: "abc123", Protocol: Protocol.Ssh }
              && r[1] is { Host: "jump.corp.local", Protocol: Protocol.Rdp, Domain: "CORP", Username: "admin", Password: "Pa55" }, Dump(r));

        r = ScreenshotScan.Extract("connect with ssh deploy@build.example.org:2222");
        Check("user@host:port", r.Count == 1 && r[0] is { Host: "build.example.org", Port: 2222, Username: "deploy", Protocol: Protocol.Ssh }, Dump(r));

        r = ScreenshotScan.Extract("Contact: john@company.com for access");
        Check("e-mail address is not a server", r.Count == 0, Dump(r));

        r = ScreenshotScan.Extract("10.2.0.1\tadmin:hunter2\n10.2.0.2\tadmin / hunter3");
        Check("user:pass and user / pass", r.Count == 2 && r[0] is { Username: "admin", Password: "hunter2" } && r[1] is { Username: "admin", Password: "hunter3" }, Dump(r));

        r = ScreenshotScan.Extract("10.3.0.1\tadmin\tone\n10.3.0.1\tbackup\ttwo");
        Check("same IP with two logins = two connections", r.Count == 2 && r[1] is { Username: "backup", Password: "two" }, Dump(r));

        r = ScreenshotScan.Extract("rdp://10.4.0.9:3390");
        Check("rdp:// URL with port", r.Count == 1 && r[0] is { Protocol: Protocol.Rdp, Port: 3390 }, Dump(r));

        r = ScreenshotScan.Extract("Version 1.2.3.4 released; see readme.txt");
        Check("version numbers / file names ignored (or IP-like kept)", r.All(c => c.Host != "readme.txt"), Dump(r));

        // ── ParseAddress ──
        Check("ParseAddress user@host:port", ConnectionImport.ParseAddress("ssh://bob@h.example:2200") is { Scheme: "ssh", Host: "h.example", Port: 2200, User: "bob" });
        Check("ParseAddress IPv6", ConnectionImport.ParseAddress("[fe80::1]:22") is { Host: "fe80::1", Port: 22 });
        Check("ParseAddress rejects junk", ConnectionImport.ParseAddress("not a host") == null && ConnectionImport.ParseAddress("") == null);

        // ── CSV ──
        var csv = Path.Combine(dir, "keepass.csv");
        File.WriteAllText(csv, "﻿\"Group\",\"Title\",\"Username\",\"Password\",\"URL\",\"Notes\"\n" +
            "\"Root/Servers\",\"Web 1\",\"admin\",\"p,w\"\"1\",\"ssh://10.9.0.1:2222\",\"multi\nline\"\n" +
            "\"Root/Windows\",\"DC\",\"CORP\\administrator\",\"x\",\"rdp://dc01.corp.local\",\"\"\n" +
            "\"Root/Web\",\"Bank\",\"me\",\"y\",\"https://bank.example\",\"\"\n");
        var l = ConnectionImport.FromCsv(csv);
        Check("CSV: web logins skipped", l.Count == 2, Dump(l));
        Check("CSV: quoted comma/quote password, port from URL", l.Count == 2 && l[0] is { Name: "Web 1", Host: "10.9.0.1", Port: 2222, Password: "p,w\"1", Folder: "Root/Servers", Protocol: Protocol.Ssh }, Dump(l));
        Check("CSV: rdp with domain", l.Count == 2 && l[1] is { Protocol: Protocol.Rdp, Domain: "CORP", Username: "administrator", Port: 3389 }, Dump(l));
        File.WriteAllText(csv, "host;port;user;protocol\nsrv1;22;root;ssh\nsrv2;3389;;RDP\nsrv3;23;x;telnet\n");
        l = ConnectionImport.FromCsv(csv);
        Check("CSV: semicolons, protocol column, telnet skipped", l.Count == 2 && l[1] is { Host: "srv2", Protocol: Protocol.Rdp } && l[0].Password == null, Dump(l));
        File.WriteAllText(csv, "a,b\n1,2\n");
        Check("CSV without host column is refused", Throws(() => ConnectionImport.FromCsv(csv)));

        // ── ssh config ──
        var cfg = Path.Combine(dir, "config");
        File.WriteAllText(cfg, "Host *\n  User nobody\nHost web web-alias\n  HostName 10.0.0.1\n  Port 2200\n  User deploy\n  IdentityFile ~/.ssh/id_web\n\nHost db\n  HostName=db.internal\nMatch host foo\n  User x\nHost !neg bastion\n  User admin\n");
        l = ConnectionImport.FromSshConfig(cfg);
        Check("ssh config: aliases, patterns skipped", l.Select(c => c.Name).SequenceEqual(["web", "web-alias", "db", "bastion"]), Dump(l));
        Check("ssh config: values", l[0] is { Host: "10.0.0.1", Port: 2200, Username: "deploy" } && l[0].PrivateKeyPath.EndsWith("id_web") && l[2] is { Host: "db.internal", Port: 22 } && l[3] is { Host: "bastion", Username: "admin" }, Dump(l));

        // ── mRemoteNG ──
        var xml = Path.Combine(dir, "confCons.xml");
        File.WriteAllText(xml, """
            <?xml version="1.0" encoding="utf-8"?>
            <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" Protected="x" FullFileEncryption="false">
              <Node Name="Prod" Type="Container">
                <Node Name="Linux" Type="Container">
                  <Node Name="app1" Type="Connection" Protocol="SSH2" Hostname="10.5.0.1" Port="22" Username="ops" Password="ENCRYPTED==" />
                </Node>
                <Node Name="dc" Type="Connection" Protocol="RDP" Hostname="dc.corp" Port="3389" Username="adm" Domain="CORP" />
                <Node Name="vnc" Type="Connection" Protocol="VNC" Hostname="v" />
              </Node>
            </mrng:Connections>
            """);
        l = ConnectionImport.FromMRemoteNG(xml);
        Check("mRemoteNG: folders, protocols, no passwords", l.Count == 3 && l[0] is { Name: "app1", Folder: "Prod/Linux", Username: "ops", Password: null } && l[1] is { Protocol: Protocol.Rdp, Domain: "CORP", Folder: "Prod" }
            && l[2] is { Protocol: Protocol.Vnc, Host: "v", Port: 5900, Username: "" }, Dump(l));

        // ── MobaXterm ──
        var moba = Path.Combine(dir, "x.mxtsessions");
        File.WriteAllText(moba, "[Bookmarks]\nSubRep=\nImgNum=42\nweb=#109#0%10.6.0.1%22%root%%-1%-1%%%22%%0%0%0%%%-1%0%0%0%%1080%%0%0%1#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%-1%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%0%-1#0# #-1\n[Bookmarks_1]\nSubRep=Win\\Servers\nImgNum=41\ndc=#91#4%dc.corp%3389%CORP\\adm%0%-1%-1%-1%-1%0%0%-1%%%%%0%0%%-1%%-1%-1%0%-1%0%-1#MobaFont#0# #-1\n");
        l = ConnectionImport.FromMobaXterm(moba);
        Check("MobaXterm: ssh + rdp with folder and domain", l.Count == 2 && l[0] is { Name: "web", Host: "10.6.0.1", Username: "root", Protocol: Protocol.Ssh }
              && l[1] is { Protocol: Protocol.Rdp, Host: "dc.corp", Domain: "CORP", Username: "adm", Folder: "Win/Servers" }, Dump(l));
        Check("FromFile detects formats", ConnectionImport.FromFile(moba).Source == "MobaXterm" && ConnectionImport.FromFile(xml).Source == "mRemoteNG" && ConnectionImport.FromFile(cfg).Source == "SSH config");

        // ── .rdp search parses files ──
        var rdp = Path.Combine(dir, "Jump box.rdp");
        File.WriteAllText(rdp, "full address:s:jump.corp:3390\nusername:s:CORP\\me\nscreen mode id:i:2\n");
        var (rn, rs) = SessionFile.ImportRdp(rdp, SessionSettings.DefaultsFor(Protocol.Rdp));
        Check(".rdp parse", rn == "Jump box" && rs is { Host: "jump.corp", Port: 3390, Domain: "CORP", Username: "me" });

        // ── OCR end to end (Windows text recognition) ──
        Exception? ocrError = null;
        List<ImportedConnection>? ocr = null, pdf = null;
        string pdfText = "";
        var th = new Thread(() =>
        {
            try
            {
                var img = RenderText(["Server inventory", "", "Name        IP              User        Password", "web01       10.20.30.40     admin       Blue#Sky42",
                                      "files       10.20.30.41     backup      River-77", "dc01 (RDP)  10.20.30.42     CORP\\jsmith  Winter2026"]);
                ocr = WindowsOcr.ScanImageAsync(img).GetAwaiter().GetResult().Items;
                var pdfPath = Path.Combine(dir, "servers.pdf");
                File.WriteAllBytes(pdfPath, MakePdf(["Host: 172.16.5.10", "Username: deploy", "Password: Tr33house", "", "RDP server: 172.16.5.20", "Login: CORP\\ops", "Password: Gr4nite"]));
                var res = WindowsOcr.ScanFileAsync(pdfPath).GetAwaiter().GetResult();
                pdf = res.Items; pdfText = res.Text;
            }
            catch (Exception e) { ocrError = e; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join();
        Check("OCR ran", ocrError == null, ocrError?.ToString() ?? "");
        if (ocr != null)
        {
            Check("OCR image: 3 connections", ocr.Count == 3, Dump(ocr));
            Check("OCR image: credentials assigned per row", ocr.Count == 3 && ocr[0] is { Host: "10.20.30.40", Username: "admin", Password: "Blue#Sky42", Name: "web01" }
                  && ocr[1] is { Username: "backup", Password: "River-77" }, Dump(ocr));
            Check("OCR image: RDP row", ocr.Count == 3 && ocr[2] is { Protocol: Protocol.Rdp, Domain: "CORP", Username: "jsmith", Name: "dc01 (RDP)" } && string.Equals(ocr[2].Password, "Winter2026", StringComparison.OrdinalIgnoreCase), Dump(ocr));
        }
        if (pdf != null)
        {
            Check("OCR PDF: 2 connections with logins", pdf.Count == 2 && pdf[0] is { Host: "172.16.5.10", Username: "deploy", Password: "Tr33house" }
                  && pdf[1] is { Host: "172.16.5.20", Protocol: Protocol.Rdp, Domain: "CORP", Username: "ops", Password: "Gr4nite" }, Dump(pdf) + " // " + pdfText.Replace("\n", " / "));
        }
    }

    private static bool Throws(Action a) { try { a(); return false; } catch { return true; } }

    private static BitmapSource RenderText(string[] lines)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 40 + lines.Length * 30));
            var tf = new Typeface("Consolas");
            for (int i = 0; i < lines.Length; i++)
                dc.DrawText(new FormattedText(lines[i], System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 18, Brushes.Black, 1.0), new Point(20, 20 + i * 30));
        }
        var bmp = new RenderTargetBitmap(900, 40 + lines.Length * 30, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>A minimal one-page PDF with Helvetica text lines.</summary>
    private static byte[] MakePdf(string[] lines)
    {
        var content = new StringBuilder("BT /F1 14 Tf 72 740 Td 20 TL\n");
        foreach (var line in lines) content.Append('(').Append(line.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")).Append(") Tj T*\n");
        content.Append("ET");
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objs.Length; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
