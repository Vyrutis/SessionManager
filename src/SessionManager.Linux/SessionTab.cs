using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace SessionManager;

public enum TabStatus { Connecting, Connected, Disconnected }

/// <summary>The content of one tab: an SSH terminal or a Remote Desktop connection.</summary>
public interface ISessionView : IDisposable
{
    Control Element { get; }
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
    /// <summary>Keyring id a prompted password may be saved under (session or credential profile); null = never.</summary>
    public Guid? SavePasswordTo { get; init; }
    /// <summary>Jump host to connect through, if the session uses one.</summary>
    public SshLogin? Jump { get; init; }
    /// <summary>Quick-connect sessions are not in the session list.</summary>
    public bool IsTransient => Session.Parent == null;
}

/// <summary>One open connection shown in the tab strip.</summary>
public sealed class OpenTab : INotifyPropertyChanged
{
    private static readonly IBrush ConnectingBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xE3, 0xA0, 0x08));
    private static readonly IBrush ConnectedBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x2D, 0xB0, 0x4B));
    private static readonly IBrush DisconnectedBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));

    public Node Session { get; }
    public ConnectRequest Request { get; }
    public ISessionView View { get; }
    private bool _isSelected, _isBroadcasting;

    public OpenTab(Node session, ConnectRequest request, ISessionView view)
    {
        Session = session;
        Request = request;
        View = view;
        view.StatusChanged += _ => Dispatcher.UIThread.Post(() =>
        {
            OnChanged(nameof(StatusBrush));
            OnChanged(nameof(ToolTip));
        });
        session.PropertyChanged += OnSessionChanged;
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Node.Name)) OnChanged(nameof(Title));
    }

    public void Detach() => Session.PropertyChanged -= OnSessionChanged;

    public string Title => Request.IsOverride ? $"{Session.Name} ({Request.Settings.Username})" : Session.Name;
    public bool IsSsh => Session.Protocol == Protocol.Ssh;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnChanged(); }
    }

    /// <summary>Whether this tab takes part when multi-exec is turned on.</summary>
    public bool IncludeInBroadcast { get; set; } = true;

    public bool IsBroadcasting
    {
        get => _isBroadcasting;
        set
        {
            if (_isBroadcasting == value) return;
            _isBroadcasting = value;
            if (View is SshTab t) t.Broadcast = value;
            OnChanged();
        }
    }

    public IBrush StatusBrush => View.Status switch
    {
        TabStatus.Connected => ConnectedBrush,
        TabStatus.Connecting => ConnectingBrush,
        _ => DisconnectedBrush,
    };

    public string ToolTip
    {
        get
        {
            var s = Request.Settings;
            var user = string.IsNullOrEmpty(s.Username) ? "" : s.Username + "@";
            return $"{user}{s.Host}:{s.Port} — {View.Status}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Builds the <see cref="SshUi"/> callbacks: prompts and host-key questions are shown over the main window.</summary>
public static class SshUiAvalonia
{
    public static SshUi Create(Func<Window?> owner, Func<string, bool, Task<(string Password, bool Save)?>> askPassword, Action<string, LogKind> log) => new()
    {
        AskPasswordAsync = askPassword,
        Log = log,
        Post = a => Dispatcher.UIThread.Post(a),
        ConfirmHostKey = q => Dialogs.FromBackground(() =>
            Dialogs.Confirm(owner(), q.Message, "Verify server key", "Trust and connect", "Cancel", defaultNo: q.Changed)),
    };
}
