using System.Net.Http;
using System.Windows;

namespace VPNDesktop;

public partial class MainWindow
{
    private AutomaticRouteStatus? automaticRoute;
    private string? lastAutomaticServer, automaticSwitchReason;
    private bool automaticStatusUnavailable;
    private DateTimeOffset nextAutomaticStatus;
    private int automaticStatusRevision;

    private bool AutomaticServerEnabled => clientSettings.Network?.Strategy == ServerStrategy.Fastest;

    private async void AutomaticServerChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsInitialized || !AutomaticServerToggle.IsEnabled)
            return;
        bool enabled = AutomaticServerToggle.IsChecked == true;
        if (enabled == AutomaticServerEnabled)
            return;
        await RunAsync(async () =>
        {
            var network = (clientSettings.Network ?? new()) with
            {
                Strategy = enabled ? ServerStrategy.Fastest : ServerStrategy.Manual
            };
            await ApplyNetworkSettingsAsync(clientSettings with { Network = network });
            ServerStrategyBox.SelectedIndex = (int)network.Strategy;
        });
    }

    private void UpdateAutomaticServerControls(bool idle)
    {
        AutomaticServerToggle.IsChecked = AutomaticServerEnabled;
        AutomaticServerToggle.IsEnabled = idle && !backgroundUnavailable && (session is not null || SelectedServer is not null);
        UpdateAutomaticServerStatus();
    }

    private void ResetAutomaticRouteStatus()
    {
        automaticStatusRevision++;
        nextAutomaticStatus = default;
        automaticRoute = null;
        lastAutomaticServer = automaticSwitchReason = null;
        automaticStatusUnavailable = false;
    }

    private async Task PollAutomaticRouteAsync(VpnSessionClient core)
    {
        if (!AutomaticServerEnabled || !core.AutomaticStatusSupported || DateTimeOffset.UtcNow < nextAutomaticStatus)
            return;

        nextAutomaticStatus = DateTimeOffset.UtcNow.AddSeconds(2);
        int revision = automaticStatusRevision;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        AutomaticRouteStatus? status;
        try { status = await core.ReadAutomaticRouteAsync(deadline.Token); }
        catch (Exception error) when (IsHostCommunicationError(error) || error is HttpRequestException)
        {
            status = null;
        }
        if (session != core || closing || busy || revision != automaticStatusRevision)
            return;

        automaticRoute = status;
        automaticStatusUnavailable = status is null;
        if (status?.Server is { } current && lastAutomaticServer != current)
        {
            if (lastAutomaticServer is { } previous)
            {
                automaticSwitchReason = status.Failed.Contains(previous, StringComparer.Ordinal)
                    ? "Резервный сервер · предыдущий сервер не ответил."
                    : "Активный сервер изменился.";
                RecordEvent(automaticSwitchReason);
            }
            lastAutomaticServer = current;
        }
        UpdateAutomaticServerStatus();
        UpdateConnectionDetail(connected: true, NodesList.HasItems);
        UpdateActiveServerRow();
    }

    private void UpdateAutomaticServerStatus()
    {
        AutomaticServerStatus.Visibility = AutomaticServerEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (!AutomaticServerEnabled)
            return;
        string status = session switch
        {
            null => "Сервер будет выбран из текущего источника.",
            { AutomaticStatusSupported: false } => "Переподключитесь, чтобы увидеть состояние автовыбора.",
            _ when routingEmergency => "Примените исправленный профиль для восстановления связи.",
            _ when automaticStatusUnavailable => "Не удалось получить состояние серверов.",
            _ => automaticRoute switch
            {
                { Server: not null, Delay: int delay } route => $"{delay} мс · доступно {route.Available} из {route.Total}" +
                    (automaticSwitchReason is { } reason ? $"\n{reason}" : ""),
                { Total: > 0 } route when route.Failed.Length == route.Total =>
                    "Серверы не отвечают. Проверяем снова…",
                _ => "Проверяем серверы выбранного источника…"
            }
        };
        AutomaticServerStatus.Text = status;
    }
}
