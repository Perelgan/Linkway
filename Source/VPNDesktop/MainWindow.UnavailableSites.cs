using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace VPNDesktop;

public partial class MainWindow
{
    private void UpdateUnavailableSitesRows(bool editable)
    {
        bool supportsGroup = false;
        if (RoutingSource is { } source)
        {
            try
            {
                supportsGroup = SubscriptionRouting.Read(source)?.Groups.Keys
                    .Any(SubscriptionRouting.IsUnavailableSitesGroup) == true;
            }
            catch (IOException) { }
        }

        var assigned = clientSettings.Network?.UnavailableSitesServer;
        foreach (var server in NodesList.Items.OfType<ServerChoice>())
        {
            bool supported = supportsGroup && RoutingConfig.IsRoutable(server.Node);
            server.SetUnavailableSitesRole(supported, supported && editable, supported && assigned == RouteServer.From(server));
        }
    }

    private void AssignUnavailableSitesServer(object sender, RoutedEventArgs e)
    {
        if (!settingsInitialized || busy || closing || session is not null ||
            sender is not Button { DataContext: ServerChoice { CanAssignUnavailableSites: true } server })
            return;

        var target = RouteServer.From(server);
        var network = clientSettings.Network ?? new();
        var settings = clientSettings with
        {
            Network = network with { UnavailableSitesServer = network.UnavailableSitesServer == target ? null : target }
        };
        try
        {
            settings.Save();
            clientSettings = settings;
            UpdateUnavailableSitesRows(editable: true);
            RefreshSubscriptionGroups();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Message.Text = "Не удалось сохранить сервер для недоступных сайтов.";
        }
    }

    private string UnavailableSitesServerLabel() =>
        ServerRouteOptions().FirstOrDefault(option => option.Value.Server == clientSettings.Network?.UnavailableSitesServer)?.Label
        ?? "Основной сервер";
}
