using System.Windows;

namespace SessionManager;

public enum TabStatus { Connecting, Connected, Disconnected }

/// <summary>The content of one tab: an SSH terminal or an RDP desktop.</summary>
public interface ISessionView : IDisposable
{
    FrameworkElement Element { get; }
    TabStatus Status { get; }
    event Action<TabStatus>? StatusChanged;
    void Start();
    void Reconnect();
    void FocusView();
}

/// <summary>Credentials resolved for one connection.</summary>
public sealed class ConnectRequest
{
    public required Node Session { get; init; }
    public required SessionSettings Settings { get; init; }
    public string? Password { get; set; }
    /// <summary>True for "Connect as…": prompted passwords are never saved.</summary>
    public bool IsOverride { get; init; }
    /// <summary>Credential Manager id a prompted password may be saved under (session or credential profile); null = never.</summary>
    public Guid? SavePasswordTo { get; init; }
    /// <summary>Jump host to connect through, if the session uses one.</summary>
    public SshLogin? Jump { get; init; }
    /// <summary>Quick-connect sessions are not in the session list.</summary>
    public bool IsTransient => Session.Parent == null;
}
