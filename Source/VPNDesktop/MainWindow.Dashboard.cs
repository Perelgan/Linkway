using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VPNDesktop;

public partial class MainWindow
{
    private readonly Stopwatch connectedFor = new();
    private bool readingDashboard;
    private int? runtimeLatency;
    private (long Download, long Upload, double Seconds)? lastTraffic;

    private void ThemeChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsInitialized)
            return;
        var updated = clientSettings with { LightTheme = LightThemeToggle.IsChecked == true };
        try
        {
            updated.Save();
            clientSettings = updated;
            ApplyTheme(updated.LightTheme);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            Message.Text = "Не удалось сохранить оформление.";
            settingsInitialized = false;
            LightThemeToggle.IsChecked = clientSettings.LightTheme;
            settingsInitialized = true;
        }
    }

    internal void ApplyTheme(bool light)
    {
        Resources.MergedDictionaries[1] = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/VPNDesktop;component/Theme{(light ? "Light" : "Dark")}.xaml")
        };
        if (session is not null)
            UpdateControls();
    }

    private void AnimatePage(object sender)
    {
        if (!SystemParameters.ClientAreaAnimation)
            return;
        FrameworkElement page = sender == ConnectionNav ? ConnectionPage : sender == ProfilesNav ? ProfilesPage
            : sender == CabinetNav ? CabinetPage : sender == RoutingNav ? RoutingPage : sender == DiagnosticsNav ? DiagnosticsPage : SettingsPage;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(170))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void DashboardModeChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsInitialized || !ProxyMode.IsEnabled)
            return;
        (sender == DashboardTun ? TunMode : ProxyMode).IsChecked = true;
    }

    private void SyncDashboardMode(bool tun, bool editable)
    {
        DashboardProxy.IsEnabled = DashboardTun.IsEnabled = editable;
        (tun ? DashboardTun : DashboardProxy).IsChecked = true;
    }

    private void UpdateDashboardDetails()
    {
        var server = activeServer ?? SelectedServer;
        DashboardSubscription.Text = server is null ? "Источник не выбран"
            : $"{(server.Source.Subscription is null ? "Ключ" : "Подписка")} · {server.Source.Name}";
        if (server is not null)
            DashboardSubscription.Text += clientSettings.Network?.Strategy switch
            {
                ServerStrategy.Fastest => " · Автовыбор",
                ServerStrategy.Fallback => " · Резервирование",
                ServerStrategy.Balance => " · Балансировка",
                _ => ""
            };
        DashboardSubscription.ToolTip = DashboardSubscription.Text;
        UpdateDashboardClock(server);
    }

    private void UpdateDashboardClock(ServerChoice? server)
    {
        string delay = runtimeLatency is int milliseconds ? $"{milliseconds} мс" : server?.StatusText ?? "—";
        DashboardSession.Text = connectedFor.IsRunning
            ? $"Задержка {delay}   ·   {connectedFor.Elapsed:hh\\:mm\\:ss}"
            : $"Задержка {delay}";
    }

    private void StartDashboardSession()
    {
        runtimeLatency = null;
        connectedFor.Restart();
        lastTraffic = null;
        DashboardChart.Clear();
        ResetSessionTraffic();
    }

    private void EndDashboardSession()
    {
        runtimeLatency = null;
        connectedFor.Reset();
        lastTraffic = null;
        DashboardChart.Clear();
        DashboardSpeed.Text = "↓ 0 Б/с   ↑ 0 Б/с";
        UpdateDashboardDetails();
    }

    private async Task SampleDashboardAsync(Mihomo core)
    {
        UpdateDashboardClock(activeServer);
        bool dashboardVisible = IsVisible && ConnectionPage.Visibility == Visibility.Visible;
        if (readingDashboard || (!dashboardVisible && DateTimeOffset.UtcNow < nextTrafficSample))
        {
            return;
        }

        readingDashboard = true;
        nextTrafficSample = DateTimeOffset.UtcNow.AddSeconds(3);
        try
        {
            var (download, upload, connections) = await core.GetTrafficSnapshotAsync(lifetime.Token);
            if (session != core)
                return;
            AccumulateTraffic(download, upload, connections);
            double seconds = connectedFor.Elapsed.TotalSeconds;
            if (dashboardVisible && lastTraffic is { } previous && seconds > previous.Seconds)
            {
                double down = Math.Max(0, download - previous.Download) / (seconds - previous.Seconds);
                double up = Math.Max(0, upload - previous.Upload) / (seconds - previous.Seconds);
                DashboardSpeed.Text = $"↓ {FormatSpeed(down)}   ↑ {FormatSpeed(up)}";
                DashboardChart.Add(down, up);
            }
            lastTraffic = (download, upload, seconds);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or ObjectDisposedException or KeyNotFoundException)
        {
            lastTraffic = null;
        }
        finally { readingDashboard = false; }
    }

    private static string FormatSpeed(double bytes) => bytes switch
    {
        >= 1048576 => $"{bytes / 1048576:0.0} МиБ/с",
        >= 1024 => $"{bytes / 1024:0.0} КиБ/с",
        _ => $"{bytes:0} Б/с"
    };
}
