using System.IO;
using System.Text.Json;
using Renci.SshNet;
using SessionManager;

int pass = 0, fail = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) pass++; else fail++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail == "" ? "" : "  -> " + detail)}");
}
T? Throws<T>(Action a) where T : Exception
{
    try { a(); return null; } catch (T e) { return e; } catch (Exception e) { Console.WriteLine("   unexpected " + e.GetType().Name + ": " + e.Message); return null; }
}

// ── Tunnels ──
var t = SshConnector.ParseTunnel("L 8080:db.internal:5432");
Check("L tunnel", t is { Kind: 'L', BindHost: "127.0.0.1", BindPort: 8080, Host: "db.internal", Port: 5432 }, t.ToString());
t = SshConnector.ParseTunnel("l 0.0.0.0:8080:db:5432");
Check("L with bind + lower case", t is { Kind: 'L', BindHost: "0.0.0.0", BindPort: 8080, Host: "db", Port: 5432 });
t = SshConnector.ParseTunnel("R 9000:localhost:3000");
Check("R tunnel", t is { Kind: 'R', BindPort: 9000, Host: "localhost", Port: 3000 });
t = SshConnector.ParseTunnel("  D 1080  ");
Check("D tunnel", t is { Kind: 'D', BindHost: "127.0.0.1", BindPort: 1080 });
t = SshConnector.ParseTunnel("D 0.0.0.0:1080");
Check("D with bind", t is { Kind: 'D', BindHost: "0.0.0.0", BindPort: 1080 });
foreach (var bad in new[] { "", "L", "X 80:a:80", "L 80:a", "L 0:a:80", "L 70000:a:80", "L 80::80", "D", "D a:b:c", "L8080:a:80", "R abc:h:1" })
    Check($"rejects \"{bad}\"", Throws<FormatException>(() => SshConnector.ParseTunnel(bad)) != null);

// ── Settings clone ──
var s = new SessionSettings { Host = "h", Tunnels = { "D 1080" } };
var c = s.Clone();
c.Tunnels.Add("L 1:a:1");
Check("Clone copies the tunnel list", s.Tunnels.Count == 1 && c.Tunnels.Count == 2);

// ── Normalize ──
var n = SessionFile.Normalize(JsonSerializer.Deserialize<SessionSettings>("""{"Host":null,"Tunnels":null,"ColorScheme":null,"FontSize":0}""", SessionStore.JsonOptions));
Check("Normalize fills nulls", n.Host == "" && n.Tunnels != null && n.ColorScheme == "Auto" && n.FontSize == 14);
Check("Normalize(null)", SessionFile.Normalize(null).Tunnels != null);

// ── Session file export/import ──
var dir = Path.Combine(Path.GetTempPath(), "smtest-files");
Directory.CreateDirectory(dir);
var node = new Node { Kind = NodeKind.Session, Name = "web01", Protocol = Protocol.Ssh, Settings = new SessionSettings { Host = "10.0.0.5", Port = 22, Username = "root", Tunnels = { "L 1:a:2" } } };
var file = Path.Combine(dir, "a.smsession");
SessionFile.Export(file, node, "s3cret pässword", "export-pass");
var model = SessionFile.Read(file);
Check("export/read keeps settings", model.Name == "web01" && model.Settings.Host == "10.0.0.5" && model.Settings.Tunnels.Count == 1 && model.Protocol == Protocol.Ssh);
Check("file has no plain password", !File.ReadAllText(file).Contains("s3cret"));
Check("decrypt with right passphrase", SessionFile.Decrypt(model.Password!, "export-pass") == "s3cret pässword");
Check("decrypt with wrong passphrase = null", SessionFile.Decrypt(model.Password!, "nope") == null);

SessionFile.Export(file, node, null, null);
Check("export without password", SessionFile.Read(file).Password == null);

File.WriteAllText(file, """{"Format":"SessionManager.Session","Name":"x","Protocol":"Ssh","Settings":null}""");
Check("null settings in file are tolerated", SessionFile.Read(file).Settings.Tunnels != null);
File.WriteAllText(file, """{"Format":"SessionManager.Session","Name":"x","Protocol":"Ssh","Settings":{"Host":"h"},"Password":{"Iterations":2000000000,"Salt":"AA==","Nonce":"AA==","Tag":"AA==","Data":""}}""");
Check("huge iteration count is rejected (no freeze)", Throws<InvalidDataException>(() => SessionFile.Read(file)) != null);
File.WriteAllText(file, """{"Format":"Other"}""");
Check("foreign JSON rejected", Throws<InvalidDataException>(() => SessionFile.Read(file)) != null);

// ── .rdp import/export ──
var rdp = Path.Combine(dir, "srv.rdp");
var preset = SessionSettings.DefaultsFor(Protocol.Rdp);
void Rdp(string text) => File.WriteAllText(rdp, text);
Rdp("full address:s:server01\r\nusername:s:CORP\\admin\r\nscreen mode id:i:2\r\n");
var (name, rs) = SessionFile.ImportRdp(rdp, preset);
Check(".rdp host/user/domain", name == "srv" && rs.Host == "server01" && rs.Port == 3389 && rs.Domain == "CORP" && rs.Username == "admin" && rs.Width == 0);
Rdp("full address:s:server01:3390\r\ndesktopwidth:i:1920\r\ndesktopheight:i:1080\r\nscreen mode id:i:1\r\n");
(_, rs) = SessionFile.ImportRdp(rdp, preset);
Check(".rdp host:port + size", rs.Host == "server01" && rs.Port == 3390 && rs.Width == 1920 && rs.Height == 1080);
Rdp("full address:s:[fe80::1]:3391\r\n");
(_, rs) = SessionFile.ImportRdp(rdp, preset);
Check(".rdp [ipv6]:port", rs.Host == "fe80::1" && rs.Port == 3391, rs.Host + " " + rs.Port);
Rdp("full address:s:fe80::1\r\n");
(_, rs) = SessionFile.ImportRdp(rdp, preset);
Check(".rdp bare ipv6", rs.Host == "fe80::1" && rs.Port == 3389, rs.Host + " " + rs.Port);
Rdp("full address:s:srv:99999\r\n");
(_, rs) = SessionFile.ImportRdp(rdp, preset);
Check(".rdp invalid port ignored", rs.Port == 3389);
Rdp("audiomode:i:0\r\n");
Check(".rdp without address rejected", Throws<InvalidDataException>(() => SessionFile.ImportRdp(rdp, preset)) != null);

var rnode = new Node { Kind = NodeKind.Session, Name = "r", Protocol = Protocol.Rdp, Settings = new SessionSettings { Host = "srv", Port = 3390, Username = "admin", Domain = "CORP", Width = 1280, Height = 720, ColorDepth = 32 } };
SessionFile.ExportRdp(rdp, rnode);
(_, rs) = SessionFile.ImportRdp(rdp, preset);
Check(".rdp export → import round trip", rs.Host == "srv" && rs.Port == 3390 && rs.Username == "admin" && rs.Domain == "CORP" && rs.Width == 1280 && rs.Height == 720);

// ── Model helpers ──
Check("Subtitle hides default port", node.Subtitle == "root@10.0.0.5");
Check("FormatSize", RemoteItem.FormatSize(512) == "512 B" && RemoteItem.FormatSize(1536) == $"{1.5:0.#} KB" && RemoteItem.FormatSize(5L << 30) == "5 GB");
var root = new Node { Kind = NodeKind.Folder, IsRoot = true };
foreach (var nm in new[] { "b", "A", "c" }) root.Children.Add(new Node { Kind = NodeKind.Session, Name = nm });
root.Children.Add(new Node { Kind = NodeKind.Folder, Name = "z-folder" });
SessionStore.Sort(root);
Check("Sort: folders first, then A b c", string.Join(",", root.Children.Select(x => x.Name)) == "z-folder,A,b,c");

// ── SSH.NET behaviour the app relies on ──
Check("SSH.NET rejects an empty username (why jump hosts now prompt for one)",
    Throws<ArgumentException>(() => new PasswordAuthenticationMethod("", "x")) != null);

// ── Session list export / import ──
SessionStore NewStore()
{
    var st = new SessionStore();
    st.Data.Roots.Add(new Node { Kind = NodeKind.Folder, Protocol = Protocol.Ssh, IsRoot = true, Name = "SSH sessions" });
    st.Data.Roots.Add(new Node { Kind = NodeKind.Folder, Protocol = Protocol.Rdp, IsRoot = true, Name = "RDP sessions" });
    return st;
}
var src = NewStore();
var profile = new CredentialProfile { Name = "Admins", Username = "admin" };
src.Data.Credentials.Add(profile);
var prod = new Node { Kind = NodeKind.Folder, Name = "Production" };
src.AddChild(src.SshRoot, prod);
var bastion = new Node { Kind = NodeKind.Session, Name = "bastion", Settings = new SessionSettings { Host = "jump", Port = 22, Username = "ops" } };
src.AddChild(prod, bastion);
var web = new Node { Kind = NodeKind.Session, Name = "web01", Settings = new SessionSettings { Host = "web01", Port = 22, CredentialId = profile.Id, JumpHostId = bastion.Id, Tunnels = { "L 8080:localhost:80" } } };
src.AddChild(prod, web);
var sub = new Node { Kind = NodeKind.Folder, Name = "DB" };
src.AddChild(prod, sub);
src.AddChild(sub, new Node { Kind = NodeKind.Session, Name = "db01", Settings = new SessionSettings { Host = "db01", Port = 2222 } });
var dc = new Node { Kind = NodeKind.Session, Name = "dc01", Settings = new SessionSettings { Host = "dc01", Port = 3389, Width = 1920, Height = 1080 } };
src.AddChild(src.RdpRoot, dc);

Directory.CreateDirectory(dir);
var listFile = Path.Combine(dir, "all.smlist");
var pws = new Dictionary<Guid, string> { [bastion.Id] = "b-pw", [profile.Id] = "profile-pw" };
SessionList.Write(listFile, SessionList.Build(src.Data.Roots.SelectMany(r => r.Children), src.Data.Credentials, src.Data.Presets, pws, "list-pass"));
var text = File.ReadAllText(listFile);
Check("list file has no plain passwords", !text.Contains("b-pw") && !text.Contains("profile-pw"));
var lm = SessionList.Read(listFile);
Check("list read: 2 top items, 4 sessions, 2 folders, 1 profile",
    lm.Items.Count == 2 && SessionList.AllNodes(lm.Items).Count(n => n.IsSession) == 4 && SessionList.AllNodes(lm.Items).Count(n => n.IsFolder) == 2 && lm.Credentials.Count == 1);
var dec = SessionList.DecryptPasswords(lm, "list-pass");
Check("list passwords decrypt", dec != null && dec[bastion.Id] == "b-pw" && dec[profile.Id] == "profile-pw");
Check("list wrong passphrase = null", SessionList.DecryptPasswords(lm, "x") == null);

// Import into an empty list: same structure, links kept
var dst = NewStore();
var res = SessionList.Merge(dst, lm);
var dWeb = dst.FindById(web.Id);
Check("merge into empty: 4 added, 2 folders", res.Added == 4 && res.Folders == 2 && res.Skipped == 0, $"{res}");
Check("merge keeps folder structure", dWeb?.Parent?.Name == "Production" && dst.FindById(dc.Id)?.Parent == dst.RdpRoot
    && dst.SshRoot.Descendants().Any(n => n.Name == "db01" && n.Parent!.Name == "DB" && n.Parent.Parent!.Name == "Production"));
Check("merge keeps profile + jump host links", dWeb?.Settings?.CredentialId == profile.Id && dWeb.Settings.JumpHostId == bastion.Id && dst.Data.Credentials.Count == 1);
Check("merged nodes have parents and protocols", SessionList.AllNodes(dst.Data.Roots).Where(n => !n.IsRoot).All(n => n.Parent != null && n.Protocol == n.Parent.Protocol));

// Re-import the same file: everything skipped, folders merged by name
var res2 = SessionList.Merge(dst, SessionList.Read(listFile));
Check("re-import skips duplicates", res2.Added == 0 && res2.Skipped == 4 && res2.Folders == 0 && dst.SshRoot.Children.Count(n => n.Name == "Production") == 1, $"{res2}");

// Partial export (only web01's folder 'DB' + web01 alone) into a list without the jump host/profile
var part = Path.Combine(dir, "part.smlist");
SessionList.Write(part, SessionList.Build(new[] { web }, Array.Empty<CredentialProfile>(), null, null, null));
var other = NewStore();
var res3 = SessionList.Merge(other, SessionList.Read(part));
var oWeb = other.FindById(web.Id);
Check("missing jump host/profile links are cleared", res3.Added == 1 && oWeb?.Settings?.JumpHostId == null && oWeb!.Settings!.CredentialId == null);

// Corrupt / hostile list
File.WriteAllText(part, """{"Format":"SessionManager.List","Items":[null,{"Kind":"Session","Name":null,"Protocol":"Ssh","Settings":null,"Children":[{"Kind":"Session","Name":"x"}]}],"Credentials":null}""");
var badList = SessionList.Read(part);
Check("nulls in a list file are tolerated", badList.Items.Count == 1 && badList.Items[0].Settings != null && badList.Items[0].Children.Count == 0 && badList.Credentials.Count == 0);
File.WriteAllText(part, """{"Format":"SessionManager.Session"}""");
Check("single-session file is not taken as a list", Throws<InvalidDataException>(() => SessionList.Read(part)) != null);

ImportTests.Run(Check, dir);

Directory.Delete(dir, true);
Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;

