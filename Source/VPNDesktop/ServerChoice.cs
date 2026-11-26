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
    private bool isConnected;

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
        ServerCheckState.Available => $"{delay} мс",
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
