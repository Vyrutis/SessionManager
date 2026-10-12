using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using SessionManager;

/// <summary>Builds the real windows off-screen and renders them with Skia; with a folder argument the pictures are saved.</summary>
static class RenderTests
{
    public static void Run(Action<string, bool, string> check, string? shots)
    {
        void Check(string n, bool ok, string d = "") => check(n, ok, d);
        if (shots != null) Directory.CreateDirectory(shots);

        // Sample sessions
        var store = new SessionStore();
        store.Load();
        var prod = new Node { Kind = NodeKind.Folder, Name = "Production", IsExpanded = true };
        store.AddChild(store.SshRoot, prod);
        store.AddChild(prod, Session("web01", "10.20.30.40", "deploy", 22));
        store.AddChild(prod, Session("db01", "10.20.30.41", "postgres", 2222));
        store.AddChild(store.SshRoot, Session("router", "192.168.1.1", "admin", 22));
        store.AddChild(store.RdpRoot, new Node { Kind = NodeKind.Session, Name = "Domain controller", Settings = new SessionSettings { Host = "dc01.corp.local", Port = 3389, Username = "jsmith", Domain = "CORP" } });
        store.Save();

        bool Render(Window w, string name)
        {
            try
            {
                w.Show();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                var frame = w.CaptureRenderedFrame();
                if (frame != null && shots != null) frame.Save(Path.Combine(shots, name + ".png"));
                bool ok = frame != null && frame.PixelSize.Width > 100;
                w.Close();
                return ok;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return false;
            }
        }

        foreach (var dark in new[] { false, true })
        {
            store.Data.Theme = dark ? "Dark" : "Light"; // the window applies the saved theme
            store.Save();
            var main = new MainWindow { Width = 1200, Height = 720 };
            Check($"main window renders ({(dark ? "dark" : "light")})", Render(main, dark ? "main-dark" : "main-light"));
        }

        // Terminal tab with typical output: colors, a box drawn with line characters, wide characters.
        UiTheme.Apply(true, new UiSettings());
        var sshNode = Session("web01", "10.20.30.40", "deploy", 22);
        var ssh = new SshTab(new ConnectRequest { Session = sshNode, Settings = sshNode.Settings! }, (_, _) => Task.FromResult<(string Password, bool Save)?>(null), () => null, showFiles: true);
        ssh.Terminal.Feed("\x1b[90mConnecting to deploy@10.20.30.40 on port 22…\x1b[0m\r\n" +
                          "Welcome to Arch Linux!\r\n\r\n" +
                          "\x1b[1;32mdeploy@web01\x1b[0m:\x1b[1;34m~\x1b[0m$ ls --color\r\n" +
                          "\x1b[1;34mbin\x1b[0m  \x1b[1;34mlogs\x1b[0m  \x1b[1;32mstart.sh\x1b[0m  notes.txt  \x1b[1;31mbackup.tar.gz\x1b[0m\r\n" +
                          "\x1b[1;32mdeploy@web01\x1b[0m:\x1b[1;34m~\x1b[0m$ htop\r\n" +
                          "┌─ CPU ──────────────┐┌─ Memory ───────────┐\r\n" +
                          "│\x1b[32m████████\x1b[0m░░░░░░ 58% ││\x1b[34m██████\x1b[0m░░░░░░░ 41% │\r\n" +
                          "└────────────────────┘└────────────────────┘\r\n" +
                          "\x1b[38;2;255;140;0m24-bit color\x1b[0m  \x1b[7m inverse \x1b[0m  \x1b[4munderline\x1b[0m  中文  \x1b[3mitalic\x1b[0m\r\n" +
                          "\x1b[1;32mdeploy@web01\x1b[0m:\x1b[1;34m~\x1b[0m$ ");
        var termWindow = new Window { Width = 1000, Height = 480, Content = ssh };
        Check("SSH tab renders", Render(termWindow, "terminal"));

        var rdpNode = new Node { Kind = NodeKind.Session, Protocol = Protocol.Rdp, Name = "Domain controller", Settings = new SessionSettings { Host = "dc01.corp.local", Port = 3389, Username = "jsmith", Domain = "CORP" } };
        var rdp = new RdpTab(new ConnectRequest { Session = rdpNode, Settings = rdpNode.Settings!, Password = "x" }, (_, _) => Task.FromResult<(string Password, bool Save)?>(null), () => null,
            new LinuxSettings { RdpClient = "xfreerdp3-not-installed" });
        var rdpWindow = new Window { Width = 900, Height = 420, Content = rdp };
        rdp.Start();
        Check("RDP tab renders (FreeRDP missing message)", Render(rdpWindow, "rdp"));

        UiTheme.Apply(false, new UiSettings());
        var dlg = new SessionDialog(Protocol.Ssh, "web01", sshNode.Settings!, [new CredentialProfile { Name = "Ops", Username = "ops" }],
            [(Guid.NewGuid(), "bastion  (ops@bastion.example.com)")], hasSavedPassword: true, []);
        Check("session dialog renders", Render(dlg.CreateWindow(null, "Edit session — web01"), "session-dialog"));

        var review = new ImportReviewWindow("servers.pdf", ScreenshotScan.Extract(
            "Name\tIP\tUser\tPassword\nweb01\t10.20.30.40\tadmin\tBlue#Sky42\nfiles\t10.20.30.41\tbackup\tRiver-77\ndc01 (RDP)\t10.20.30.42\tCORP\\jsmith\tWinter2026"),
            "Name IP User Password\nweb01 10.20.30.40 admin Blue#Sky42\n…");
        Check("import review renders", Render(review.CreateWindow(), "import-review"));
        review.Selected.Clear();
    }

    static Node Session(string name, string host, string user, int port) =>
        new() { Kind = NodeKind.Session, Protocol = Protocol.Ssh, Name = name, Settings = new SessionSettings { Host = host, Username = user, Port = port } };
}
