using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace VPNDesktop;

public partial class MainWindow
{
    private ClientSettings clientSettings = new();
    private bool settingsInitialized;
    private GitHubRelease[] cachedBuilds = [];
    private CancellationTokenSource? buildListRequest;
    private string? pendingUpdate;
    private WindowsKillSwitch? killSwitch;

    private sealed record UpdateBuildChoice(GitHubRelease Release, bool Latest = false)
    {
        public string DisplayName
        {
            get
            {
                string version = Release.Version == GitHubUpdates.InstalledRelease
                    ? $"{Release.Version} (установлена)" : Release.Version.ToString();
                return Latest ? $"Последняя версия · {version}" : version;
            }
        }
    }

    private GitHubRelease? SelectedUpdate => (UpdateBuilds.SelectedItem as UpdateBuildChoice)?.Release;

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
        cachedBuilds = GitHubUpdates.LoadCache();
        ShowCachedBuilds();
        if (!UpdateBuilds.HasItems)
            UpdateStatus.Text = "Нажмите «Проверить обновления», чтобы загрузить сборки.";
        LoadNetworkSettings();
        LoadDesktopSettings();
        ShowCoreBuilds([]);
        LightThemeToggle.IsChecked = clientSettings.LightTheme;
        DeveloperModeToggle.IsChecked = clientSettings.DeveloperMode;
        ApplyTheme(clientSettings.LightTheme);
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
        NetworkSettings.IsEnabled = idle && !connected && killSwitch is null;
        KillSwitchToggle.IsEnabled = idle && !connected && killSwitch is null && TunMode.IsChecked == true;
        KillSwitchRelease.IsEnabled = idle && !connected && killSwitch is not null;
        KillSwitchRelease.Visibility = !connected && killSwitch is not null ? Visibility.Visible : Visibility.Collapsed;
        KillSwitchNote.Text = killSwitch is not null
            ? connected ? "Активен. Windows блокирует соединения приложений вне адаптера TUN."
                : "Доступ заблокирован. Подключитесь снова или снимите блокировку."
            : "Для режима TUN. При сбое ядра Windows удерживает блокировку до переподключения, снятия блокировки или выхода из клиента.";
        StableUpdates.IsEnabled = idle;
        ExperimentalUpdates.IsEnabled = idle;
        UpdateBuilds.IsEnabled = idle && UpdateBuilds.HasItems;
        CheckUpdatesButton.IsEnabled = idle && buildListRequest is null;
        InstallUpdateButton.IsEnabled = idle && !connected && buildListRequest is null && SelectedUpdate is { } release
            && release.Version != GitHubUpdates.InstalledRelease;
        UpdateCoreControls(idle, connected);
        InstallUpdateButton.ToolTip = connected
            ? "Отключите VPN перед установкой."
            : SelectedUpdate?.Manifest is not null
                ? "Скачивает изменившиеся файлы. Linkway откроется после установки."
                : "Скачивает архив сборки. Linkway откроется после установки.";
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

    private async void SelectUpdateChannel(object sender, RoutedEventArgs e)
    {
        if (!settingsInitialized || busy || closing)
            return;
        var updated = clientSettings with { ExperimentalUpdates = ReferenceEquals(sender, ExperimentalUpdates) };
        var previous = updated == clientSettings ? UpdateBuilds.SelectedItem as UpdateBuildChoice : null;
        if (updated != clientSettings)
        {
            try
            {
                updated.Save();
            }
            catch (Exception)
            {
                Message.Text = "Не удалось сохранить канал обновлений на диск.";
            }
            clientSettings = updated;
        }

        await RefreshUpdateBuildsAsync(previous);
    }

    private async void CheckUpdates(object sender, RoutedEventArgs e)
    {
        if (!settingsInitialized || busy || closing || buildListRequest is not null)
            return;

        await RefreshUpdateBuildsAsync(UpdateBuilds.SelectedItem as UpdateBuildChoice);
    }

    private async Task RefreshUpdateBuildsAsync(UpdateBuildChoice? previous)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        buildListRequest?.Cancel();
        buildListRequest = request;
        ShowCachedBuilds(previous);
        UpdateControls();
        UpdateStatus.Text = "Проверяем список сборок на GitHub…";
        try
        {
            var builds = await GitHubUpdates.ListAsync(experimental: true, request.Token);
            request.Token.ThrowIfCancellationRequested();
            cachedBuilds = builds;
            GitHubUpdates.SaveCache(builds);
            ShowCachedBuilds(UpdateBuilds.SelectedItem as UpdateBuildChoice);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException
            or JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException or FormatException)
        {
            UpdateStatus.Text = UpdateBuilds.HasItems
                ? "GitHub недоступен. Показан сохранённый список."
                : "Не удалось загрузить сборки с GitHub. Нажмите «Проверить обновления», чтобы повторить.";
        }
        finally
        {
            if (ReferenceEquals(buildListRequest, request))
            {
                buildListRequest = null;
                if (!closing)
                    UpdateControls();
            }
        }
    }

    private void ShowCachedBuilds(UpdateBuildChoice? previous = null)
    {
        var choices = cachedBuilds
            .Where(build => clientSettings.ExperimentalUpdates || build.Version.Prerelease is null)
            .Select(build => new UpdateBuildChoice(build)).ToList();
        if (choices.Count > 0)
            choices.Insert(0, new UpdateBuildChoice(choices[0].Release, Latest: true));

        UpdateBuilds.ItemsSource = choices;
        UpdateBuilds.SelectedItem = previous is { Latest: false }
            ? choices.FirstOrDefault(choice => !choice.Latest && choice.Release.Version == previous.Release.Version)
                ?? choices.FirstOrDefault()
            : choices.FirstOrDefault();
        ShowBuildStatus();
    }

    private void UpdateBuildChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!settingsInitialized || busy)
            return;
        ShowBuildStatus();
        UpdateControls();
    }

    private void ShowBuildStatus()
    {
        if (SelectedUpdate is not { } build)
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
            if (buildListRequest is not null || SelectedUpdate is not { } release || release.Version == GitHubUpdates.InstalledRelease
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
                "Linkway", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelPendingUpdate()
    {
        if (pendingUpdate is { } stage)
            GitHubUpdates.Discard(stage);
        pendingUpdate = null;
    }
}
