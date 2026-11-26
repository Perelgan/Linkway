using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace VPNDesktop;

public partial class MainWindow
{
    private ClientSettings clientSettings = new();
    private bool settingsInitialized;
    private GitHubRelease? availableUpdate;
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
        RepositoryBox.Text = clientSettings.Repository is { } repo ? $"https://github.com/{repo}" : "";
        AppVersionLabel.Text = $"Версия {GitHubUpdates.InstalledVersion}";
        UpdateStatus.Text = clientSettings.Repository is null
            ? "Репозиторий пока не указан."
            : "Можно проверить наличие новой версии.";
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
        bool hasAddress = !string.IsNullOrWhiteSpace(RepositoryBox.Text);
        RepositoryBox.IsEnabled = idle;
        SaveRepositoryButton.IsEnabled = idle && (hasAddress || clientSettings.Repository is not null);
        CheckUpdateButton.IsEnabled = idle && hasAddress;
        InstallUpdateButton.Visibility = availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        InstallUpdateButton.IsEnabled = idle && !connected;
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

    private void RepositoryChanged(object sender, TextChangedEventArgs e)
    {
        if (!settingsInitialized)
            return;
        availableUpdate = null;
        UpdateStatus.Text = string.IsNullOrWhiteSpace(RepositoryBox.Text)
            ? "Репозиторий не указан."
            : "Можно проверить наличие новой версии.";
        UpdateControls();
    }

    private void StoreRepository(string? repository)
    {
        if (clientSettings.Repository == repository)
            return;
        var updated = clientSettings with { Repository = repository };
        updated.Save();
        clientSettings = updated;
    }

    private async void SaveRepository(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        string? repository = string.IsNullOrWhiteSpace(RepositoryBox.Text)
            ? null : GitHubUpdates.ParseRepository(RepositoryBox.Text);
        StoreRepository(repository);
        RepositoryBox.Text = repository is null ? "" : $"https://github.com/{repository}";
        UpdateStatus.Text = repository is null ? "Адрес репозитория удалён." : "Адрес репозитория сохранён.";
        return Task.CompletedTask;
    });

    private async void CheckForUpdates(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        availableUpdate = null;
        string repository = GitHubUpdates.ParseRepository(RepositoryBox.Text);
        StoreRepository(repository);
        RepositoryBox.Text = $"https://github.com/{repository}";
        UpdateStatus.Text = "Проверяем релизы на GitHub…";
        try
        {
            availableUpdate = await GitHubUpdates.CheckAsync(repository, lifetime.Token);
            UpdateStatus.Text = availableUpdate is { } release
                ? $"Доступна версия {release.Version}. После загрузки приложение закроется для установки."
                : "Новой версии нет.";
        }
        catch
        {
            UpdateStatus.Text = "Проверка обновлений не завершилась.";
            throw;
        }
    });

    private async void DownloadUpdate(object sender, RoutedEventArgs e)
    {
        string? prepared = null;
        await RunAsync(async () =>
        {
            if (availableUpdate is not { } release || session is not null)
                return;
            UpdateStatus.Text = "Загружаем обновление…";
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
