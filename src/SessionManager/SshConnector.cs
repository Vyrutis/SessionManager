using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;

namespace SessionManager;

public enum LogKind { Info, Warning, Error }

/// <summary>Callbacks a connection uses to talk to the user. AskPassword is called on the UI thread.</summary>
public sealed class SshUi
{
    public required Func<string, bool, (string Password, bool Save)?> AskPassword { get; init; }
    public required Action<string, LogKind> Log { get; init; }
    /// <summary>Any element in the window; used to show the host-key dialog on the UI thread.</summary>
    public required FrameworkElement Anchor { get; init; }
}

/// <summary>Who to log in as. A password typed at a prompt is kept here (and saved when the user asks).</summary>
public sealed class SshLogin
{
    public required SessionSettings Settings { get; init; }
    public string? Password { get; set; }
    /// <summary>Credential Manager id a typed password is saved under; null = don't offer saving.</summary>
    public Guid? SaveId { get; init; }
    /// <summary>Prefix for prompts, e.g. "jump host ".</summary>
    public string Role { get; init; } = "";
}

/// <summary>How to reach the target: directly, or through a local port forwarded by a jump host.</summary>
public sealed class SshRoute : IDisposable
{
    public SshClient? Jump { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }

    public void Dispose()
    {
        var jump = Jump;
        if (jump == null) return;
        Task.Run(() =>
        {
            try { foreach (var p in jump.ForwardedPorts.ToList()) p.Stop(); } catch { }
            try { if (jump.IsConnected) jump.Disconnect(); } catch { }
            try { jump.Dispose(); } catch { }
        });
    }
}

public static class SshConnector
{
    /// <summary>Connects and authenticates, prompting for a password when needed. Must be called on the UI thread.</summary>
    /// <exception cref="OperationCanceledException">The user cancelled a prompt.</exception>
    public static async Task<T> ConnectAsync<T>(SshLogin login, string host, int port, SshUi ui, Func<ConnectionInfo, T> create)
        where T : BaseClient
    {
        var s = login.Settings;
        var target = $"{(string.IsNullOrEmpty(s.Username) ? "" : s.Username + "@")}{s.Host}";
        for (int attempt = 0; ; attempt++)
        {
            var methods = BuildAuthMethods(s, login.Password, out var keyError);
            if (keyError != null) ui.Log(keyError, LogKind.Warning);

            if (methods.Count == 0)
            {
                if (!Prompt(login, target, ui)) throw new OperationCanceledException("Cancelled.");
                continue;
            }

            var info = new ConnectionInfo(host, port, s.Username, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(15) };
            var client = create(info);
            // Verify against the real host name, even when connecting through a forwarded local port.
            client.HostKeyReceived += (_, e) => e.CanTrust = VerifyHostKey(ui.Anchor, s.Host, s.Port, e);
            try
            {
                await Task.Run(client.Connect);
                return client;
            }
            catch (SshAuthenticationException)
            {
                client.Dispose();
                ui.Log($"Authentication failed for {login.Role}{target}.", LogKind.Error);
                if (attempt >= 4) throw;
                if (!Prompt(login, target, ui)) throw new OperationCanceledException("Cancelled.");
            }
            catch (SshConnectionException ex) when (ex.DisconnectReason == DisconnectReason.HostKeyNotVerifiable)
            {
                client.Dispose();
                throw new InvalidOperationException("Host key was not accepted.");
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    /// <summary>Connects to the jump host (if any) and forwards a local port to the target.</summary>
    public static async Task<SshRoute> OpenRouteAsync(SessionSettings target, SshLogin? jump, SshUi ui)
    {
        if (jump == null) return new SshRoute { Host = target.Host, Port = target.Port };

        var j = jump.Settings;
        ui.Log($"Connecting to jump host {j.Host}…", LogKind.Info);
        var client = await ConnectAsync(jump, j.Host, j.Port, ui, i => new SshClient(i) { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        try
        {
            int localPort = FreeLocalPort();
            var forward = new ForwardedPortLocal("127.0.0.1", (uint)localPort, target.Host, (uint)target.Port);
            client.AddForwardedPort(forward);
            forward.Start();
            ui.Log($"Jump host connected, forwarding to {target.Host}:{target.Port}.", LogKind.Info);
            return new SshRoute { Jump = client, Host = "127.0.0.1", Port = localPort };
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static int FreeLocalPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ───────────── Tunnels ─────────────

    public sealed record Tunnel(char Kind, string BindHost, uint BindPort, string Host, uint Port)
    {
        public override string ToString() => Kind switch
        {
            'L' => $"localhost:{BindPort} → {Host}:{Port} (local)",
            'R' => $"server port {BindPort} → {Host}:{Port} on this PC (remote)",
            _ => $"SOCKS proxy on localhost:{BindPort} (dynamic)",
        };
    }

    /// <summary>Parses "L [bind:]8080:host:5432", "R 9000:host:3000" or "D [bind:]1080".</summary>
    /// <exception cref="FormatException">With a message suitable for the user.</exception>
    public static Tunnel ParseTunnel(string line)
    {
        var text = line.Trim();
        if (text.Length < 3 || text[1] != ' ')
            throw new FormatException($"\"{line}\": start with L, R or D followed by a space.");
        char kind = char.ToUpperInvariant(text[0]);
        var parts = text[2..].Trim().Split(':');

        static uint Port(string p, string line) =>
            uint.TryParse(p, out var n) && n is >= 1 and <= 65535 ? n : throw new FormatException($"\"{line}\": \"{p}\" is not a valid port.");

        switch (kind)
        {
            case 'L' or 'R' when parts.Length is 3 or 4:
                var bind = parts.Length == 4 ? parts[0] : "127.0.0.1";
                int o = parts.Length - 3;
                if (parts[o + 1].Length == 0) throw new FormatException($"\"{line}\": missing destination host.");
                return new Tunnel(kind, bind, Port(parts[o], line), parts[o + 1], Port(parts[o + 2], line));
            case 'D' when parts.Length is 1 or 2:
                return new Tunnel('D', parts.Length == 2 ? parts[0] : "127.0.0.1", Port(parts[^1], line), "", 0);
            case 'L' or 'R':
                throw new FormatException($"\"{line}\": use {kind} port:host:port, e.g. {kind} 8080:localhost:80.");
            case 'D':
                throw new FormatException($"\"{line}\": use D port, e.g. D 1080.");
            default:
                throw new FormatException($"\"{line}\": the type must be L (local), R (remote) or D (dynamic/SOCKS).");
        }
    }

    /// <summary>Starts the session's port forwards; failures are logged, not thrown.</summary>
    public static void StartTunnels(SshClient client, IEnumerable<string> lines, SshUi ui)
    {
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            try
            {
                var t = ParseTunnel(line);
                ForwardedPort port = t.Kind switch
                {
                    'L' => new ForwardedPortLocal(t.BindHost, t.BindPort, t.Host, t.Port),
                    'R' => new ForwardedPortRemote(t.BindHost, t.BindPort, t.Host, t.Port),
                    _ => new ForwardedPortDynamic(t.BindHost, t.BindPort),
                };
                port.Exception += (_, e) => ui.Anchor.Dispatcher.InvokeAsync(() => ui.Log($"Tunnel {line.Trim()}: {e.Exception.Message}", LogKind.Warning));
                client.AddForwardedPort(port);
                port.Start();
                ui.Log($"Tunnel: {t}", LogKind.Info);
            }
            catch (Exception ex)
            {
                ui.Log($"Tunnel {line.Trim()} failed: {ex.Message}", LogKind.Warning);
            }
        }
    }

    // ───────────── Authentication ─────────────

    private static bool Prompt(SshLogin login, string target, SshUi ui)
    {
        var result = ui.AskPassword($"Password for {login.Role}{target}:", login.SaveId != null);
        if (result == null) return false;
        login.Password = result.Value.Password;
        if (result.Value.Save && login.SaveId is Guid id)
        {
            try { CredentialStore.Set(id, result.Value.Password, login.Settings.Username); }
            catch (Exception ex) { ui.Log(ex.Message, LogKind.Warning); }
        }
        return true;
    }

    private static List<AuthenticationMethod> BuildAuthMethods(SessionSettings s, string? password, out string? keyError)
    {
        keyError = null;
        var methods = new List<AuthenticationMethod>();
        var user = s.Username;

        // Explicit key file first, then the usual keys in %USERPROFILE%\.ssh.
        var keys = new List<IPrivateKeySource>();
        if (!string.IsNullOrWhiteSpace(s.PrivateKeyPath))
        {
            try { keys.Add(LoadKey(s.PrivateKeyPath, password)); }
            catch (Exception ex) { keyError = $"Could not load private key {s.PrivateKeyPath}: {ex.Message}"; }
        }
        else
        {
            var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            foreach (var name in new[] { "id_ed25519", "id_ecdsa", "id_rsa" })
            {
                var path = Path.Combine(sshDir, name);
                if (!File.Exists(path)) continue;
                try { keys.Add(LoadKey(path, password)); } catch { /* encrypted with another passphrase, or unsupported */ }
            }
        }
        if (keys.Count > 0) methods.Add(new PrivateKeyAuthenticationMethod(user, keys.ToArray()));

        if (password != null)
        {
            methods.Add(new PasswordAuthenticationMethod(user, password));
            var ki = new KeyboardInteractiveAuthenticationMethod(user);
            ki.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts) p.Response = password;
            };
            methods.Add(ki);
        }
        return methods;
    }

    private static PrivateKeyFile LoadKey(string path, string? passphrase)
    {
        try { return new PrivateKeyFile(path); }
        catch (SshPassPhraseNullOrEmptyException) when (!string.IsNullOrEmpty(passphrase))
        {
            return new PrivateKeyFile(path, passphrase);
        }
    }

    /// <summary>Called on a background thread by SSH.NET.</summary>
    private static bool VerifyHostKey(FrameworkElement anchor, string host, int port, HostKeyEventArgs e)
    {
        var fp = "SHA256:" + e.FingerPrintSHA256;
        var result = KnownHosts.Check(host, port, e.HostKeyName, fp, out var stored);
        if (result == KnownHosts.Result.Match) return true;

        return anchor.Dispatcher.Invoke(() =>
        {
            var owner = Window.GetWindow(anchor);
            string message;
            MessageBoxImage icon;
            if (result == KnownHosts.Result.Unknown)
            {
                message = $"This is the first connection to {host}:{port}.\n\nServer key ({e.HostKeyName}):\n{fp}\n\n" +
                          "If you trust this server, choose Yes to remember the key and connect.";
                icon = MessageBoxImage.Question;
            }
            else
            {
                message = $"WARNING: the server key for {host}:{port} has CHANGED.\n\n" +
                          $"Remembered key:\n{stored}\n\nKey offered now:\n{e.HostKeyName} {fp}\n\n" +
                          "This can mean the server was reinstalled, or that someone is intercepting the connection.\n\n" +
                          "Replace the remembered key and connect anyway?";
                icon = MessageBoxImage.Warning;
            }
            var answer = owner != null
                ? MessageBox.Show(owner, message, "Verify server key", MessageBoxButton.YesNo, icon,
                    result == KnownHosts.Result.Unknown ? MessageBoxResult.Yes : MessageBoxResult.No)
                : MessageBox.Show(message, "Verify server key", MessageBoxButton.YesNo, icon, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return false;
            KnownHosts.Trust(host, port, e.HostKeyName, fp);
            return true;
        });
    }
}
