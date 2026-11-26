using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace VPNDesktop;

public partial class MainWindow
{
    private sealed record CoreChoice(MihomoRelease? Release, string Label)
    {
        public Version Version => Release?.Version ?? MihomoVersions.BundledVersion;
    }

    private void ShowCoreBuilds(MihomoRelease[] releases)
    {
        var choices = new List<CoreChoice> { new(null, $"Комплектное ядро · v{MihomoVersions.BundledVersion.ToString(3)}") };
        for (int index = 0; index < releases.Length; index++)
        {
            var release = releases[index];
            string prefix = index == 0 ? "Последняя стабильная · " : "";
            choices.Add(new(release, $"{prefix}v{release.Version.ToString(3)}"));
        }
        var active = MihomoVersions.Active;
        CoreBuilds.ItemsSource = choices;
        CoreBuilds.SelectedItem = active.Bundled ? choices[0] : choices.Skip(1).FirstOrDefault(choice => choice.Version == active.Version);
        CoreVersionLabel.Text = $"Активное v{active.Version.ToString(3)}";
    }

    private void UpdateCoreControls(bool idle, bool connected)
    {
        CoreBuilds.IsEnabled = FindCoreBuildsButton.IsEnabled = idle;
        var active = MihomoVersions.Active;
        InstallCoreBuildButton.IsEnabled = idle && !connected && killSwitch is null &&
            CoreBuilds.SelectedItem is CoreChoice choice &&
            (choice.Version != active.Version || choice.Release is null && !active.Bundled);
        InstallCoreBuildButton.ToolTip = connected || killSwitch is not null
            ? "Отключите VPN и снимите блокировку сети перед сменой ядра."
            : "Выбранная версия будет использована при следующем подключении.";
    }

    private void CoreBuildChanged(object sender, SelectionChangedEventArgs e)
    {
        if (settingsInitialized && !busy)
            UpdateCoreControls(!closing, session is not null);
    }

    private async void FindCoreBuilds(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        CoreUpdateStatus.Text = "Загружаем список версий Mihomo…";
        try
        {
            var releases = await MihomoVersions.ListAsync(lifetime.Token);
            ShowCoreBuilds(releases);
            CoreUpdateStatus.Text = releases.Length == 0 ? "Подходящие стабильные Windows x64-версии не найдены." : "";
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or
            InvalidOperationException or KeyNotFoundException)
        {
            if (!closing)
                CoreUpdateStatus.Text = "Не удалось загрузить список версий Mihomo.";
        }
    });

    private async void InstallCoreBuild(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (session is not null || killSwitch is not null || CoreBuilds.SelectedItem is not CoreChoice choice)
            return;
        CoreUpdateStatus.Text = choice.Release is null ? "Выбираем комплектное ядро…" : "Загружаем и проверяем ядро Mihomo…";
        try
        {
            await Task.Run(() => MihomoVersions.SelectAsync(choice.Release, lifetime.Token), lifetime.Token);
            var active = MihomoVersions.Active;
            CoreVersionLabel.Text = $"Активное v{active.Version.ToString(3)}";
            CoreUpdateStatus.Text = $"Ядро v{active.Version.ToString(3)} выбрано.";
        }
        catch (Exception error) when (error is IOException or HttpRequestException or UnauthorizedAccessException or
            OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            if (!closing)
                CoreUpdateStatus.Text = error is IOException && error.GetType() == typeof(IOException)
                    ? error.Message : "Не удалось установить ядро. Продолжит использоваться прежняя версия.";
        }
    });
}
