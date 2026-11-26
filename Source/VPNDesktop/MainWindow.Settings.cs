using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace VPNDesktop;

public partial class MainWindow
{
    private ClientSettings clientSettings = new();
    private bool settingsInitialized;
    private string? pendingUpdate;
    private WindowsKillSwitch? killSwitch;

    private void InitializeClientSettings()
    {
        try
        {
            clientSettings = ClientSettings.Load();
        }
        catch (Exception)
        {
            Message.Text = "Не удалось прочитать сохранённые настройки.";
        }
        (clientSettings.Tun ? TunMode : ProxyMode).IsChecked = true;
        BypassLan.IsChecked = clientSettings.DirectLan;
        KillSwitchToggle.IsChecked = clientSettings.KillSwitch;
        (clientSettings.ExperimentalUpdates ? ExperimentalUpdates : StableUpdates).IsChecked = true;
        AppVersionLabel.Text = $"Версия {GitHubUpdates.InstalledRelease}";
        settingsInitialized = true;
    }

    private void SaveRoutingSettings()
    {
        if (!settingsInitialized)
            return;
        var updated = clientSettings with
        {
            Tun = TunMode.IsChecked == true, DirectLan = BypassLan.IsChecked == true,
            KillSwitch = KillSwitchToggle.IsChecked == true
        };
        if (updated == clientSettings)
            return;
        try
        {
            updated.Save();
            clientSettings = updated;
        }
        catch (Exception)
        {
            Message.Text = "Настройки маршрутизации не удалось сохранить на диск.";
        }
    }

    private void UpdateSettingsControls(bool idle, bool connected)
    {
        KillSwitchToggle.IsEnabled = idle && !connected && killSwitch is null && TunMode.IsChecked == true;
        KillSwitchRelease.IsEnabled = idle && !connected && killSwitch is not null;
        KillSwitchRelease.Visibility = !connected && killSwitch is not null ? Visibility.Visible : Visibility.Collapsed;
        KillSwitchNote.Text = killSwitch is not null
            ? connected ? "Активен. Windows блокирует соединения приложений вне адаптера TUN."
                : "Доступ заблокирован. Подключитесь снова или снимите блокировку."
            : "Для режима TUN. При сбое ядра Windows удерживает блокировку до переподключения, снятия блокировки или выхода из клиента.";
        StableUpdates.IsEnabled = idle;
        ExperimentalUpdates.IsEnabled = idle;
        CheckUpdateButton.IsEnabled = idle;
        UpdateBuilds.IsEnabled = idle && UpdateBuilds.HasItems;
        InstallUpdateButton.IsEnabled = idle && !connected && UpdateBuilds.SelectedItem is GitHubRelease release
            && release.Version != GitHubUpdates.InstalledRelease;
        InstallUpdateButton.ToolTip = connected
            ? "Отключите VPN перед установкой."
            : "Скачивает релиз и закрывает приложение для установки.";
    }

    private async void ReleaseKillSwitch(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        if (session is null && killSwitch is not null)
        {
            DisarmKillSwitch();
            Message.Text = "Kill Switch снял блокировку сети.";
        }
        return Task.CompletedTask;
    });

    private void DisarmKillSwitch()
    {
        killSwitch?.Dispose();
        killSwitch = null;
    }

    private void UpdateChannelChanged(object sender, RoutedEventArgs e)
    {
        if (!settingsInitialized)
            return;
        var updated = clientSettings with { ExperimentalUpdates = ReferenceEquals(sender, ExperimentalUpdates) };
        if (updated == clientSettings)
            return;
        UpdateBuilds.ItemsSource = null;
        UpdateStatus.Text = "";
        try
        {
            updated.Save();
        }
        catch (Exception)
        {
            UpdateStatus.Text = "Не удалось сохранить канал обновлений на диск.";
        }
        clientSettings = updated;
        UpdateControls();
    }

    private void OpenUpdateReleases(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(GitHubUpdates.ReleasesUrl) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Message.Text = "Не удалось открыть GitHub в браузере.";
        }
    }

    private async void CheckForUpdates(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var selection = (UpdateBuilds.SelectedItem as GitHubRelease)?.Version;
        UpdateStatus.Text = "Загружаем список сборок с GitHub…";
        try
        {
            var builds = await GitHubUpdates.ListAsync(clientSettings.ExperimentalUpdates, lifetime.Token);
            UpdateBuilds.ItemsSource = builds;
            UpdateBuilds.SelectedItem = builds.FirstOrDefault(build => build.Version == selection)
                ?? builds.FirstOrDefault(build => build.Version.CompareTo(GitHubUpdates.InstalledRelease) > 0)
                ?? builds.FirstOrDefault(build => build.Version == GitHubUpdates.InstalledRelease);
            ShowBuildStatus();
        }
        catch
        {
            UpdateStatus.Text = "Не удалось обновить список сборок.";
            throw;
        }
    });

    private void UpdateBuildChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!settingsInitialized || busy)
            return;
        ShowBuildStatus();
        UpdateControls();
    }

    private void ShowBuildStatus()
    {
        if (UpdateBuilds.SelectedItem is not GitHubRelease build)
        {
            UpdateStatus.Text = UpdateBuilds.HasItems ? "Выберите сборку для установки." : "В этом канале пока нет доступных сборок.";
            return;
        }

        var installed = GitHubUpdates.InstalledRelease;
        UpdateStatus.Text = build.Version.CompareTo(installed) switch
        {
            < 0 => $"Вы выбрали более раннюю сборку {build.Version}.",
            0 => $"Сборка {installed} уже установлена.",
            _ => $"Готово к установке сборки {build.Version}."
        };
    }

    private async void DownloadUpdate(object sender, RoutedEventArgs e)
    {
        string? prepared = null;
        await RunAsync(async () =>
        {
            if (UpdateBuilds.SelectedItem is not GitHubRelease release || release.Version == GitHubUpdates.InstalledRelease
                || session is not null)
                return;
            UpdateStatus.Text = $"Загружаем сборку {release.Version}…";
            var progress = new Progress<int>(percent =>
            {
                if (busy && !closing)
                    UpdateStatus.Text = percent == 100 ? "Проверяем и распаковываем обновление…" : $"Загрузка · {percent}%";
            });
            try
            {
                prepared = await GitHubUpdates.PrepareAsync(release, progress, lifetime.Token);
            }
            catch
            {
                UpdateStatus.Text = "Загрузка или подготовка обновления не завершилась.";
                throw;
            }
        });
        if (prepared is null)
            return;
        if (closing)
        {
            GitHubUpdates.Discard(prepared);
            return;
        }
        pendingUpdate = prepared;
        Close();
    }

    private void InstallPendingUpdate()
    {
        if (pendingUpdate is not { } stage)
            return;
        try
        {
            GitHubUpdates.StartInstaller(stage);
            pendingUpdate = null;
        }
        catch (Exception)
        {
            CancelPendingUpdate();
            MessageBox.Show("Не удалось запустить установщик. Текущая версия осталась на месте.",
                "VPN Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelPendingUpdate()
    {
        if (pendingUpdate is { } stage)
            GitHubUpdates.Discard(stage);
        pendingUpdate = null;
    }
}
