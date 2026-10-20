using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SessionManager;

/// <summary>Web session addresses, certificate fingerprints, importing HTTP(S) connections and arranging the list by hand.</summary>
static class WebAndOrderTests
{
    public static void Run(Action<string, bool, string> check, string dir)
    {
        // ── Web addresses ──
        var s = SessionSettings.DefaultsFor(Protocol.Web);
        check("web defaults: https on 443", s.WebHttps && s.Port == 443 && SessionSettings.DefaultPort(Protocol.Web) == 443, "");
        s.Host = "10.0.0.1";
        check("web url hides the usual port", s.WebUrl() == "https://10.0.0.1/", s.WebUrl());
        s.Port = 8443;
        s.WebPath = "admin/index.html";
        check("web url with port and page", s.WebUrl() == "https://10.0.0.1:8443/admin/index.html", s.WebUrl());
        s.WebHttps = false;
        s.Port = 80;
        s.WebPath = "";
        check("http on 80", s.WebUrl() == "http://10.0.0.1/", s.WebUrl());
        check("web url through a forwarded port", s.WebUrl("127.0.0.1", 50123) == "http://127.0.0.1:50123/", s.WebUrl("127.0.0.1", 50123));
        s.Host = "fe80::1";
        check("web url with IPv6", s.WebUrl() == "http://[fe80::1]/", s.WebUrl());

        check("parse full https address",
            SessionSettings.TryParseWebUrl("https://switch01.lan:8443/cgi-bin/login?x=1", out var https, out var host, out var port, out var path)
            && https && host == "switch01.lan" && port == 8443 && path == "/cgi-bin/login?x=1", $"{https} {host} {port} {path}");
        check("parse http address without port",
            SessionSettings.TryParseWebUrl("http://192.168.1.1", out https, out host, out port, out path) && !https && host == "192.168.1.1" && port == 80 && path == "",
            $"{https} {host} {port} {path}");
        check("parse a bare host as https",
            SessionSettings.TryParseWebUrl("printer.local", out https, out host, out port, out path) && https && host == "printer.local" && port == 443, $"{host} {port}");
        check("parse [IPv6]:port", SessionSettings.TryParseWebUrl("https://[fe80::5]:444/", out _, out host, out port, out _) && host == "fe80::5" && port == 444, $"{host} {port}");
        check("ftp and empty are not web addresses",
            !SessionSettings.TryParseWebUrl("ftp://x", out _, out _, out _, out _) && !SessionSettings.TryParseWebUrl("  ", out _, out _, out _, out _), "");

        var node = new Node { Kind = NodeKind.Session, Protocol = Protocol.Web, Settings = new SessionSettings { Host = "nas", Port = 5001, WebPath = "/" } };
        check("web session subtitle is its address", node.Subtitle == "https://nas:5001", node.Subtitle);
        check("web session icon is a globe", node.Glyph == "", "");
        check("Web display name", Protocol.Web.DisplayName() == "Web", "");

        // ── Certificate fingerprints ──
        using (var key = RSA.Create(2048))
        {
            var req = new CertificateRequest("CN=router.lan", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30));
            var fp = CertificateFingerprint.Of(cert.RawData);
            check("fingerprint is SHA-256 hex", fp.Length == 64 && fp == cert.GetCertHashString(HashAlgorithmName.SHA256), fp);
            var shown = CertificateFingerprint.Format(fp);
            check("fingerprint shown as AB:CD in two lines", shown.Split('\n').Length == 2 && shown.Replace("\n", ":").Replace(":", "") == fp, shown);
            check("fingerprint matching ignores case and colons",
                CertificateFingerprint.Matches(shown.Replace("\n", ":").ToLowerInvariant(), fp) && !CertificateFingerprint.Matches("", fp) && !CertificateFingerprint.Matches(null, fp)
                && !CertificateFingerprint.Matches(new string('0', 64), fp), "");
        }

        // ── Store: Web tab, old files ──
        var store = new SessionStore();
        check("a new list has a Web tab", NewLoaded(dir, "{}", out store) && store.Data.Roots.Select(r => r.Protocol).SequenceEqual(new[] { Protocol.Ssh, Protocol.Rdp, Protocol.Vnc, Protocol.Web }),
            string.Join(",", store.Data.Roots.Select(r => r.Protocol)));
        check("a file without the new settings loads with defaults",
            NewLoaded(dir, """{"Roots":[{"Kind":"Folder","Protocol":"Web","IsRoot":true,"Name":"Web sessions","Children":[{"Kind":"Session","Name":"r","Settings":{"Host":"r","Port":443,"WebPath":null,"TrustedCertificate":null}}]}]}""", out store)
            && store.RootFor(Protocol.Web).Children[0] is { Protocol: Protocol.Web, Settings: { WebHttps: true, WebPath: "", TrustedCertificate: "" } }
            && !store.Data.ManualOrder, "");

        // ── Import from mRemoteNG with HTTP / HTTPS connections ──
        var xml = Path.Combine(dir, "confCons-web.xml");
        File.WriteAllText(xml, """
            <Connections Name="Connections" Export="false" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="1000" FullFileEncryption="false" ConfVersion="2.7">
              <Node Name="Lab" Type="Container">
                <Node Name="Switch" Type="Connection" Protocol="HTTPS" Hostname="10.1.1.2" Port="8443" Username="admin" />
                <Node Name="Printer" Type="Connection" Protocol="HTTP" Hostname="printer.lan" Port="80" />
                <Node Name="NAS" Type="Connection" Protocol="HTTPS" Hostname="https://nas.lan:5001/webman" />
              </Node>
            </Connections>
            """);
        var items = ConnectionImport.FromMRemoteNG(xml);
        var sw = items.FirstOrDefault(i => i.Name == "Switch");
        var pr = items.FirstOrDefault(i => i.Name == "Printer");
        var nas = items.FirstOrDefault(i => i.Name == "NAS");
        check("mRemoteNG HTTPS becomes a web session", sw is { Protocol: Protocol.Web, Host: "10.1.1.2", Port: 8443, Username: "admin", Folder: "Lab" } && sw.Template?.WebHttps == true,
            $"{sw?.Protocol} {sw?.Host} {sw?.Port}");
        check("mRemoteNG HTTP keeps http", pr is { Protocol: Protocol.Web, Port: 80 } && pr.Template?.WebHttps == false, $"{pr?.Port} {pr?.Template?.WebHttps}");
        check("mRemoteNG full address is split", nas is { Host: "nas.lan" } && nas.Template?.WebPath == "/webman", $"{nas?.Host} {nas?.Template?.WebPath}");

        // ── Arranging by hand ──
        store = new SessionStore();
        NewLoaded(dir, "{}", out store);
        var root = store.SshRoot;
        Node S(string n) => new() { Kind = NodeKind.Session, Name = n, Settings = new SessionSettings { Host = n, Port = 22 } };
        foreach (var n in new[] { "c", "a", "b" }) store.AddChild(root, S(n));
        var f1 = new Node { Kind = NodeKind.Folder, Name = "zeta" };
        store.AddChild(root, f1);
        string Names(Node f) => string.Join(",", f.Children.Select(x => x.Name));
        check("sorted by name by default", Names(root) == "zeta,a,b,c", Names(root));

        store.Data.ManualOrder = true;
        store.MoveTo(new[] { root.Children[3] }, root, 0); // c to the top
        check("move to the top", Names(root) == "c,zeta,a,b", Names(root));
        store.MoveTo(new[] { root.Children[0] }, root, 3); // c to before b (index counted before removing it)
        check("move down counts the item's own old place", Names(root) == "zeta,a,c,b", Names(root));
        store.AddChild(root, S("new"));
        var f2 = new Node { Kind = NodeKind.Folder, Name = "alpha" };
        store.AddChild(root, f2);
        check("in your own order: new session at the end, new folder after the folders", Names(root) == "zeta,alpha,a,c,b,new", Names(root));
        store.Resort(root);
        check("renaming doesn't re-sort your own order", Names(root) == "zeta,alpha,a,c,b,new", Names(root));
        store.MoveTo(new[] { root.Children[2], root.Children[3] }, f1, 0);
        check("several items move into a folder in their order", Names(f1) == "a,c" && Names(root) == "zeta,alpha,b,new" && f1.Children.All(x => x.Parent == f1), Names(f1));
        store.Save();
        var reloaded = new SessionStore();
        reloaded.Load();
        check("your own order is saved", reloaded.Data.ManualOrder && Names(reloaded.SshRoot) == "zeta,alpha,b,new" && Names(reloaded.SshRoot.Children[0]) == "a,c", Names(reloaded.SshRoot));
        store.Data.ManualOrder = false;
        store.SortAll();
        check("back to A to Z sorts every folder", Names(root) == "alpha,zeta,b,new" && Names(f1) == "a,c", Names(root));
    }

    /// <summary>Loads a sessions.json with this content from the test data folder (the tests run with SESSIONMANAGER_DATA set).</summary>
    private static bool NewLoaded(string dir, string json, out SessionStore store)
    {
        if (!SessionStore.DataDir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to write sessions.json outside the temp folder: " + SessionStore.DataDir);
        Directory.CreateDirectory(SessionStore.DataDir);
        File.WriteAllText(Path.Combine(SessionStore.DataDir, "sessions.json"), json);
        store = new SessionStore();
        return store.Load() == null;
    }
}
