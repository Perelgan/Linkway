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

    private async void SaveNetworkSettings(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (backgroundUnavailable)
            return;
        var network = new NetworkOptions((ServerStrategy)ServerStrategyBox.SelectedIndex,
            FakeIpToggle.IsChecked == true, GeoRefreshToggle.IsChecked == true,
            ResolversBox.Text.Trim(), DomainResolversBox.Text.Trim(), RuleSetsBox.Text.Trim());
        _ = network.ParseDns("https://1.1.1.1/dns-query", RoutingConfig.Main);
        _ = network.ParseRuleSets(RoutingConfig.Main).ToArray();
        await ApplyNetworkSettingsAsync(clientSettings with { Network = network });
    });

    private async Task ApplyNetworkSettingsAsync(ClientSettings updated)
    {
        if (session is { } core && activeServer is { } primary)
        {
            Message.Text = "Применяем сетевые настройки…";
            await ApplyLiveRoutingAsync(core, SessionConfiguration(primary, routing.Active, updated), updated.Save);
            ResetConnectionHealth();
        }
        else
            updated.Save();
        clientSettings = updated;
        Message.Text = session is null ? "Сетевые настройки сохранены для следующего подключения."
            : "Сетевые настройки применены к текущему подключению.";
    }

    private async void BypassLanChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsInitialized || changingList || busy || closing || backgroundUnavailable)
            return;
        var updated = clientSettings with { DirectLan = BypassLan.IsChecked == true };
        if (updated == clientSettings)
            return;
        await RunAsync(async () =>
        {
            try { await ApplyNetworkSettingsAsync(updated); }
            finally { BypassLan.IsChecked = clientSettings.DirectLan; }
        });
    }
}
