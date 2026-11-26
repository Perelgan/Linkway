using System.IO;
using System.Text.Json;

namespace VPNDesktop;

public partial class MainWindow
{
    private bool backgroundUnavailable, pollingSession;

    private HostedConfiguration SessionConfiguration(ServerChoice selected, RoutingProfile? profile) =>
        new(HostedSource.From(selected.Source), selected.Node.Name, profiles.Select(HostedSource.From).ToArray(),
            profile, clientSettings.Network, BypassLan.IsChecked == true,
            TunMode.IsChecked == true, KillSwitchToggle.IsChecked == true);

    private async Task RestoreBackgroundSessionAsync()
    {
        if (HostProtocol.Exists())
        {
            await TryAttachAfterFailureAsync();
            if (session is not null)
                Message.Text = "Фоновое подключение VPN активно.";
            return;
        }

        WindowsProxy.Restore();
        await Task.Run(() =>
        {
            HostRuntime.Reap();
            foreach (string directory in Directory.EnumerateDirectories(ProfileStore.DirectoryPath, "session-*"))
            {
                if (Guid.TryParseExact(Path.GetFileName(directory).AsSpan("session-".Length), "N", out _) &&
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                    Mihomo.DeleteSession(directory);
            }
        }, lifetime.Token);
    }

    private async Task TryAttachAfterFailureAsync()
    {
        try { AcceptBackgroundSession(await VpnSessionClient.AttachAsync(lifetime.Token)); }
        catch (Exception error) when (IsHostCommunicationError(error))
        {
            if (!closing && HostProtocol.Exists())
            {
                backgroundUnavailable = true;
                Message.Text = "Фоновый VPN запущен. Откройте Linkway с теми же правами, чтобы управлять подключением.";
            }
        }
    }

    private void AcceptBackgroundSession(HostedAttachment result)
    {
        killSwitch = result.Status.Guard;
        if (!result.Status.Connected || result.Connection is not { } connection)
            return;

        var profile = connection.Source.Restore();
        var selected = profile.Nodes.First(node => node.Name == connection.Node);
        var visible = NodesList.Items.OfType<ServerChoice>().FirstOrDefault(server =>
            RouteServer.From(server) == new RouteServer(RouteServer.SourceKey(profile), selected.Name));
        activeServer = visible?.Source.Content == profile.Content ? visible : new ServerChoice(profile, selected);
        changingList = true;
        try
        {
            NodesList.SelectedItem = visible;
            (connection.Tun ? TunMode : ProxyMode).IsChecked = true;
        }
        finally { changingList = false; }

        session = new VpnSessionClient(connection);
        StartDashboardSession();
        BeginRecovery();
        UpdateActiveServerRow();
        RefreshRoutingSourceView();
    }

    private void ClearBackgroundSession(bool interrupted)
    {
        if (session is not null)
        {
            RecordSessionEnd();
            RecordEvent(interrupted ? "Сеанс прерван" : "Сеанс завершён");
        }
        session = null;
        activeServer = null;
        EndDashboardSession();
        UpdateActiveServerRow();
        RefreshRoutingSourceView();
    }

    private async Task PollBackgroundSessionAsync()
    {
        if (session is not { } core)
            return;

        HostedStatus status;
        try
        {
            if (HostProtocol.Exists())
                status = await VpnSessionClient.StatusAsync(lifetime.Token);
            else
            {
                WindowsProxy.Restore();
                status = new(false, false);
            }
        }
        catch (Exception error) when (IsHostCommunicationError(error) || error is System.ComponentModel.Win32Exception)
        {
            return;
        }
        if (closing || busy || session != core)
            return;

        killSwitch = status.Guard;
        if (status.Connected)
        {
            await SampleDashboardAsync(core);
            return;
        }

        ScheduleRecovery();
        ClearBackgroundSession(interrupted: true);
        UpdateControls();
        NotifyTray("Mihomo завершился.");
        Message.Text = killSwitch ? "Mihomo завершился. Kill Switch удерживает блокировку сети."
            : "Mihomo завершился. Подключение закрыто.";
    }

    private static bool IsHostCommunicationError(Exception error) => error is
        IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException or JsonException;
}
