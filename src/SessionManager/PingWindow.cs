using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SessionManager;

/// <summary>Pings a host (ICMP) and also checks whether the session's TCP port answers.</summary>
public sealed class PingWindow : Window
{
    private readonly string _host;
    private readonly int _port;
    private readonly TextBox _output = new()
    {
        IsReadOnly = true,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 13,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        TextWrapping = TextWrapping.NoWrap,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(8),
    };
    private readonly Button _again = new() { Content = "Ping again", IsEnabled = false };
    private CancellationTokenSource _cts = new();

    public PingWindow(Window owner, string host, int port)
    {
        _host = host;
        _port = port;
        Owner = owner;
        Title = $"Ping {host}";
        Width = 560;
        Height = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme.ApplyTo(this);

        var close = new Button { Content = "Close", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        _again.Click += (_, _) => _ = RunAsync();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
        buttons.Children.Add(_again);
        buttons.Children.Add(close);

        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(_output);
        Content = dock;

        Loaded += (_, _) => _ = RunAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    private void Line(string text)
    {
        _output.AppendText(text + Environment.NewLine);
        _output.ScrollToEnd();
    }

    private async Task RunAsync()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _again.IsEnabled = false;
        _output.Clear();
        Line($"Pinging {_host}:");

        int sent = 0, received = 0;
        long min = long.MaxValue, max = 0, total = 0;
        using var ping = new Ping();
        for (int i = 0; i < 4 && !ct.IsCancellationRequested; i++)
        {
            sent++;
            try
            {
                var reply = await ping.SendPingAsync(_host, TimeSpan.FromSeconds(2), cancellationToken: ct);
                if (reply.Status == IPStatus.Success)
                {
                    received++;
                    min = Math.Min(min, reply.RoundtripTime);
                    max = Math.Max(max, reply.RoundtripTime);
                    total += reply.RoundtripTime;
                    Line($"  Reply from {reply.Address}: time={reply.RoundtripTime} ms TTL={reply.Options?.Ttl}");
                }
                else Line($"  {reply.Status}");
            }
            catch (OperationCanceledException) { return; }
            catch (PingException ex) { Line($"  {ex.InnerException?.Message ?? ex.Message}"); }
            if (i < 3) try { await Task.Delay(1000, ct); } catch { return; }
        }
        Line("");
        Line($"Sent {sent}, received {received}, lost {sent - received} ({(sent - received) * 100 / Math.Max(sent, 1)}% loss)");
        if (received > 0) Line($"Round trip: min {min} ms, max {max} ms, average {total / received} ms");

        // Many servers block ICMP, so also check the session's port.
        Line("");
        Line($"Checking TCP port {_port}…");
        try
        {
            using var tcp = new TcpClient();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(_host, _port, timeout.Token);
            Line($"  Port {_port} is open ({sw.ElapsedMilliseconds} ms).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Line($"  Port {_port}: no answer within 3 seconds."); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Line($"  Port {_port}: {ex.Message}"); }

        _again.IsEnabled = true;
    }
}
