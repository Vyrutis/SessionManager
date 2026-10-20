using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SessionManager;

/// <summary>Wakes a sleeping or switched-off computer by sending a "magic packet" to its network card.</summary>
public static class WakeOnLan
{
    /// <summary>Reads AA:BB:CC:DD:EE:FF, AA-BB-CC-DD-EE-FF, AABB.CCDD.EEFF or AABBCCDDEEFF.</summary>
    public static bool TryParseMac(string? text, out byte[] mac)
    {
        mac = Array.Empty<byte>();
        var hex = new string((text ?? "").Where(c => !":-. ".Contains(c)).ToArray());
        if (hex.Length != 12 || !hex.All(Uri.IsHexDigit)) return false;
        mac = Enumerable.Range(0, 6).Select(i => byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber)).ToArray();
        return mac.Any(b => b != 0) && mac.Any(b => b != 0xFF);
    }

    public static string FormatMac(byte[] mac) => string.Join(":", mac.Select(b => b.ToString("X2")));

    /// <summary>6 × FF, then the address 16 times.</summary>
    public static byte[] MagicPacket(byte[] mac)
    {
        var packet = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 0; i < 16; i++) Buffer.BlockCopy(mac, 0, packet, 6 + i * 6, 6);
        return packet;
    }

    /// <summary>
    /// Sends the packet to every local network's broadcast address and 255.255.255.255 (ports 9 and 7), and straight to
    /// the host's address when it is known (some routers pass that on). Returns how many packets went out.
    /// </summary>
    public static async Task<int> SendAsync(byte[] mac, string? host)
    {
        var packet = MagicPacket(mac);
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            foreach (var u in nic.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                if (u.IPv4Mask == null || u.IPv4Mask.Equals(IPAddress.Any)) continue;
                var ip = u.Address.GetAddressBytes();
                var mask = u.IPv4Mask.GetAddressBytes();
                targets.Add(new IPAddress(ip.Select((b, i) => (byte)(b | ~mask[i])).ToArray()));
            }
        }
        if (host != null && IPAddress.TryParse(host, out var direct) && direct.AddressFamily == AddressFamily.InterNetwork) targets.Add(direct);

        int sent = 0;
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        foreach (var target in targets)
        {
            foreach (var port in new[] { 9, 7 })
            {
                try { await udp.SendAsync(packet, packet.Length, new IPEndPoint(target, port)); sent++; }
                catch (SocketException) { /* a network that can't broadcast */ }
            }
        }
        return sent;
    }

    /// <summary>The network card address of a host on the local network, from this computer's address table (ARP); null if unknown.</summary>
    public static async Task<string?> FindMacAsync(string host)
    {
        IPAddress? ip;
        try
        {
            ip = IPAddress.TryParse(host, out var parsed) ? parsed
                : (await Dns.GetHostAddressesAsync(host)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (SocketException) { return null; }
        if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork) return null;

        if (OperatingSystem.IsWindows())
        {
            // SendARP asks the network if needed, so this also works for hosts not contacted recently (when they are awake).
            return await Task.Run(() =>
            {
                var mac = new byte[6];
                int len = mac.Length;
#pragma warning disable CS0618 // Address is the simplest way to pass the IPv4 address as SendARP expects it
                uint dest = (uint)ip.Address;
#pragma warning restore CS0618
                return SendARP(dest, 0, mac, ref len) == 0 && len == 6 && TryParseMac(FormatMac(mac), out _) ? FormatMac(mac) : null;
            });
        }
        if (File.Exists("/proc/net/arp"))
        {
            // IP address  HW type  Flags  HW address  Mask  Device
            foreach (var line in (await File.ReadAllLinesAsync("/proc/net/arp")).Skip(1))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 4 && f[0] == ip.ToString() && TryParseMac(f[3], out var mac)) return FormatMac(mac);
            }
        }
        return null;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref int physicalAddrLen);
}

/// <summary>Checks whether a host answers on a TCP port (used for the online / offline dots).</summary>
public static class HostProbe
{
    /// <summary>Connects and disconnects at once; true with the time taken, false on timeout or refusal.</summary>
    public static async Task<(bool Online, int Milliseconds, string? Error)> CheckAsync(string host, int port, TimeSpan timeout, CancellationToken cancel = default)
    {
        var watch = Stopwatch.StartNew();
        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeout);
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
            return (true, (int)watch.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return (false, 0, "no answer"); }
        catch (SocketException ex)
        {
            return (false, 0, ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "the port is closed",
                SocketError.HostNotFound or SocketError.NoData => "the name can't be found",
                SocketError.HostUnreachable or SocketError.NetworkUnreachable => "the network can't reach it",
                _ => ex.SocketErrorCode.ToString(),
            });
        }
    }
}
