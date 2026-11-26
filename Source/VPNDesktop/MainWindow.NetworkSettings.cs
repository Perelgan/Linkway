using System.Windows;

namespace VPNDesktop;

public partial class MainWindow
{
    private void LoadNetworkSettings()
    {
        var network = clientSettings.Network ?? new();
        ServerStrategyBox.SelectedIndex = Enum.IsDefined(network.Strategy) ? (int)network.Strategy : 0;
        FakeIpToggle.IsChecked = network.FakeIp;
        GeoRefreshToggle.IsChecked = network.RefreshGeoData;
        ResolversBox.Text = network.Resolvers;
        DomainResolversBox.Text = network.DomainResolvers;
        RuleSetsBox.Text = network.RuleSets;
    }

    private async void SaveNetworkSettings(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        if (session is not null || killSwitch || backgroundUnavailable)
            return Task.CompletedTask;
        var network = new NetworkOptions((ServerStrategy)ServerStrategyBox.SelectedIndex,
            FakeIpToggle.IsChecked == true, GeoRefreshToggle.IsChecked == true,
            ResolversBox.Text.Trim(), DomainResolversBox.Text.Trim(), RuleSetsBox.Text.Trim());
        _ = network.ParseDns("https://1.1.1.1/dns-query", RoutingConfig.Main);
        _ = network.ParseRuleSets(RoutingConfig.Main).ToArray();
        var updated = clientSettings with { Network = network };
        updated.Save();
        clientSettings = updated;
        Message.Text = "Сетевые настройки сохранены. Они вступят в силу при подключении.";
        return Task.CompletedTask;
    });
}
