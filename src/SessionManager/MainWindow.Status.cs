using System.Windows;
using System.Windows.Threading;

namespace SessionManager;

/// <summary>Online / offline dots: the sessions of the shown tab are checked about once a minute (a quick TCP connect to their port).</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _reachTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private CancellationTokenSource? _reachCancel;
    private DateTime _lastReachCheck;

    /// <summary>Checks a host; replaced in tests.</summary>
    internal static Func<string, int, CancellationToken, Task<(bool Online, int Milliseconds, string? Error)>> Probe =
        (host, port, cancel) => HostProbe.CheckAsync(host, port, TimeSpan.FromSeconds(2.5), cancel);

    private void InitReachability()
    {
        _reachTimer.Tick += (_, _) => CheckReachability();
        if (!_store.Data.ShowStatus) return;
        _reachTimer.Start();
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => CheckReachability(), DispatcherPriority.ApplicationIdle);
    }

    private void SetShowStatus(bool on)
    {
        _store.Data.ShowStatus = on;
        if (on)
        {
            _reachTimer.Start();
            CheckReachability(force: true);
        }
        else
        {
            _reachTimer.Stop();
            _reachCancel?.Cancel();
            foreach (var n in _store.Data.Roots.SelectMany(r => r.Descendants())) n.Reach = Reachability.Unknown;
        }
        Save();
    }

    /// <summary>Checks the sessions of the shown tab (not more often than every few seconds unless forced).</summary>
    private async void CheckReachability(bool force = false)
    {
        if (!_store.Data.ShowStatus || WindowState == WindowState.Minimized) return;
        if (!force && DateTime.UtcNow - _lastReachCheck < TimeSpan.FromSeconds(5)) return;
        _lastReachCheck = DateTime.UtcNow;
        _reachCancel?.Cancel();
        var cts = _reachCancel = new CancellationTokenSource();
        var sessions = ActiveRoot.Descendants().Where(n => n.IsSession && n.Settings is { Host.Length: > 0 }).ToList();
        var gate = new SemaphoreSlim(12);
        try
        {
            await Task.WhenAll(sessions.Select(async n =>
            {
                var s = n.Settings!;
                if (s.JumpHostId != null)
                {
                    // Only the jump host can reach it; a direct check would always say offline.
                    n.Reach = Reachability.Unknown;
                    return;
                }
                await gate.WaitAsync(cts.Token);
                try
                {
                    var (online, ms, error) = await Probe(s.Host, s.Port, cts.Token);
                    if (cts.IsCancellationRequested) return;
                    n.Reach = online ? Reachability.Online : Reachability.Offline;
                    n.ReachText = online ? $"Online: port {s.Port} answered in {ms} ms" : $"Not reachable: {error} (port {s.Port})";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    n.Reach = Reachability.Offline;
                    n.ReachText = "Not reachable: " + ex.Message;
                }
                finally { gate.Release(); }
            }));
        }
        catch (OperationCanceledException) { /* a newer check started */ }
    }

    /// <summary>A connection that worked says more than the last check.</summary>
    private static void MarkConnected(Node node)
    {
        node.Reach = Reachability.Online;
        node.ReachText = "Online: connected";
    }
}
