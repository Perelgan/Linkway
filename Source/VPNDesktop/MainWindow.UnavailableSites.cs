using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace VPNDesktop;

public partial class MainWindow
{
    private sealed record UnavailableSitesChoice(RouteServer? Server, string Label);
    private bool showingUnavailableSitesChoices;

    private void RefreshUnavailableSitesServerChoices()
    {
        showingUnavailableSitesChoices = true;
        try
        {
            var choices = ServerRouteOptions()
                .Select(option => new UnavailableSitesChoice(option.Value.Server, option.Label))
                .Prepend(new UnavailableSitesChoice(null, "Основной сервер"))
                .ToArray();
            UnavailableSitesServerBox.ItemsSource = choices;
            UnavailableSitesServerBox.SelectedItem = choices.FirstOrDefault(choice =>
                choice.Server == clientSettings.Network?.UnavailableSitesServer) ?? choices[0];
        }
        finally { showingUnavailableSitesChoices = false; }
    }

    private void UpdateUnavailableSitesPicker(bool editable)
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
        UnavailableSitesPicker.Visibility = supportsGroup ? Visibility.Visible : Visibility.Collapsed;
        UnavailableSitesServerBox.IsEnabled = editable && supportsGroup;
    }

    private void UnavailableSitesServerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (showingUnavailableSitesChoices || !settingsInitialized || busy || closing || session is not null ||
            UnavailableSitesServerBox.SelectedItem is not UnavailableSitesChoice choice)
            return;
        var settings = clientSettings with
        {
            Network = (clientSettings.Network ?? new()) with { UnavailableSitesServer = choice.Server }
        };
        try
        {
            settings.Save();
            clientSettings = settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            RefreshUnavailableSitesServerChoices();
            Message.Text = "Не удалось сохранить сервер для недоступных сайтов.";
        }
    }

    private string DashboardUnavailableServerLabel() =>
        UnavailableSitesServerBox.SelectedItem is UnavailableSitesChoice choice ? choice.Label : "Основной сервер";
}
