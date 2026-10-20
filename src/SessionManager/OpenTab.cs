using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SessionManager;

/// <summary>One open connection shown in the tab strip.</summary>
public sealed class OpenTab : INotifyPropertyChanged
{
    private static readonly Brush ConnectingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE3, 0xA0, 0x08)));
    private static readonly Brush ConnectedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2D, 0xB0, 0x4B)));
    private static readonly Brush DisconnectedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)));

    public Node Session { get; }
    public ConnectRequest Request { get; }
    public ISessionView View { get; }

    public OpenTab(Node session, ConnectRequest request, ISessionView view)
    {
        Session = session;
        Request = request;
        View = view;
        view.StatusChanged += _ =>
        {
            OnChanged(nameof(StatusBrush));
            OnChanged(nameof(ToolTip));
        };
        session.PropertyChanged += OnSessionChanged;
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Node.Name)) OnChanged(nameof(Title));
    }

    /// <summary>Called when the tab closes, so the session node doesn't keep the closed tab alive.</summary>
    public void Detach() => Session.PropertyChanged -= OnSessionChanged;

    private bool _isBroadcasting;

    /// <summary>Whether this tab takes part when multi-exec is turned on.</summary>
    public bool IncludeInBroadcast { get; set; } = true;

    /// <summary>True while multi-exec is on and this tab is included.</summary>
    public bool IsBroadcasting
    {
        get => _isBroadcasting;
        set
        {
            if (_isBroadcasting == value) return;
            _isBroadcasting = value;
            if (View is SshTerminalView t) t.Broadcast = value;
            OnChanged();
        }
    }

    public string Title => Request.IsOverride ? $"{Session.Name} ({Request.Settings.Username})" : Session.Name;
    public string Glyph => Session.Glyph;

    public Brush StatusBrush => View.Status switch
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
            if (Session.Protocol == Protocol.Web) return $"{s.WebUrl()} — {View.Status}";
            var user = string.IsNullOrEmpty(s.Username) ? "" : s.Username + "@";
            return $"{user}{s.Host}:{s.Port} — {View.Status}";
        }
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
