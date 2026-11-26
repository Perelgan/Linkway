using System.ComponentModel;

namespace VPNDesktop;

internal enum ServerCheckState
{
    None,
    Queued,
    Testing,
    Available,
    Unavailable,
    Error
}

internal sealed class ServerChoice(Profile source, Node node) : INotifyPropertyChanged
{
    public Profile Source { get; set; } = source;
    public Node Node { get; } = node;
    public string ProtocolLabel => Node.Type switch
    {
        "Vless" => "VLESS",
        "Vmess" => "VMess",
        "Tuic" => "TUIC",
        "Socks5" => "SOCKS5",
        _ => Node.Type
    };
    public ServerCheckState CheckState { get; private set; }
    private int delay;
    private bool isConnected, showLatency;
    public int? Latency => CheckState == ServerCheckState.Available ? delay : null;
    public bool SupportsUnavailableSites { get; private set; }
    public bool CanAssignUnavailableSites { get; private set; }
    public bool IsUnavailableSitesServer { get; private set; }

    public void SetUnavailableSitesRole(bool supported, bool canAssign, bool assigned)
    {
        if (SupportsUnavailableSites != supported)
        {
            SupportsUnavailableSites = supported;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SupportsUnavailableSites)));
        }
        if (CanAssignUnavailableSites != canAssign)
        {
            CanAssignUnavailableSites = canAssign;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAssignUnavailableSites)));
        }
        if (IsUnavailableSitesServer != assigned)
        {
            IsUnavailableSitesServer = assigned;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUnavailableSitesServer)));
        }
    }

    public bool ShowLatency
    {
        get => showLatency;
        set
        {
            if (showLatency == value)
                return;
            showLatency = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }

    public bool IsConnected
    {
        get => isConnected;
        set
        {
            if (isConnected == value)
                return;
            isConnected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnected)));
        }
    }

    public string StatusText => CheckState switch
    {
        ServerCheckState.Queued => "В очереди",
        ServerCheckState.Testing => "Проверка…",
        ServerCheckState.Available => ShowLatency ? $"{delay} мс" : "Доступен",
        ServerCheckState.Unavailable => "Нет ответа",
        ServerCheckState.Error => "Ошибка проверки",
        _ => "—"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetCheckState(ServerCheckState state, int delayMs = 0)
    {
        CheckState = state;
        delay = delayMs;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckState)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
    }
}
