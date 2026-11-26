using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VPNDesktop;

public partial class MainWindow : Window
{
    private const int ParallelServerChecks = 4;
    private readonly ProfileStore store = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer watchdog = new() { Interval = TimeSpan.FromSeconds(1) };
    private List<Profile> profiles = [];
    private Mihomo? session;
    private ServerChoice? activeServer;
    private CancellationTokenSource? serverCheckCancellation;
    private bool busy, closing, readyToClose, changingList, profilesReadable;
    private bool returnAfterDashboardImport;
    private Profile? Current => ProfilesList.SelectedItem as Profile;
    private ServerChoice? SelectedServer => NodesList.SelectedItem as ServerChoice;

    public MainWindow() : this(preview: false) { }

    // The rendering test exercises the same BAML and layout without opening the user profile.
    internal MainWindow(bool preview)
    {
        InitializeComponent();
        ConnectionNav.IsChecked = true;
        if (preview)
            return;
        try
        {
            Directory.CreateDirectory(ProfileStore.DirectoryPath);
            WindowsProxy.Restore();
            foreach (string directory in Directory.EnumerateDirectories(ProfileStore.DirectoryPath, "session-*"))
            {
                if (Guid.TryParseExact(Path.GetFileName(directory).AsSpan("session-".Length), "N", out _)
                    && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                    Mihomo.DeleteSession(directory);
            }
        }
        catch (Exception)
        {
            Message.Text = "Не удалось завершить восстановление предыдущего сеанса.";
        }
        try
        {
            profiles = store.Load();
            profilesReadable = true;
        }
        catch (Exception)
        {
            Message.Text = "Не удалось открыть хранилище ключей и подписок.";
        }

        InitializeClientSettings();
        InitializeRouting();
        ShowProfiles(profiles.FirstOrDefault()?.Id);
        Loaded += RestoreCabinetOnLoad;
        Closing += CloseWindow;
        Application.Current.SessionEnding += (_, _) =>
        {
            exitRequested = true;
            try { WindowsProxy.Restore(); }
            catch (Exception) { /* The next launch retries the recovery. */ }
        };
        watchdog.Tick += CheckProcess;
        watchdog.Start();
        InitializeDesktopIntegration();
    }

    private void ShowProfiles(Guid? selected, bool preferSelected = false)
    {
        var previous = SelectedServer;
        changingList = true;
        ProfilesList.ItemsSource = profiles;
        ProfilesList.SelectedItem = profiles.FirstOrDefault(profile => profile.Id == selected);
        var previousBySource = NodesList.Items.OfType<ServerChoice>().ToLookup(server => server.Source.Id);
        var servers = profiles.SelectMany(profile => BuildServerRows(profile, previousBySource[profile.Id])).ToArray();
        NodesList.ItemsSource = servers;
        NodesList.SelectedItem = (!preferSelected ? servers.FirstOrDefault(server => previous is not null
                && server.Source.Id == previous.Source.Id && server.Node.Name == previous.Node.Name)
            : null)
            ?? servers.FirstOrDefault(server => server.Source.Id == selected && server.Node.Name == server.Source.Selected)
            ?? servers.FirstOrDefault(server => server.Source.Id == selected)
            ?? servers.FirstOrDefault();
        changingList = false;
        UpdateActiveServerRow();
        ServerListSummary.Text = $"Серверов: {servers.Length}";
        RefreshRoutingSourceView();
        UpdateControls();
    }

    private void UpdateActiveServerRow()
    {
        foreach (var server in NodesList.Items.OfType<ServerChoice>())
        {
            server.IsConnected = activeServer is { } active && server.Node == active.Node
                && server.Source.Id == active.Source.Id && server.Source.Content == active.Source.Content;
        }
    }

    private static IEnumerable<ServerChoice> BuildServerRows(Profile source, IEnumerable<ServerChoice> previous)
    {
        var reusable = previous.FirstOrDefault()?.Source.Content == source.Content
            ? previous.ToDictionary(server => server.Node)
            : new Dictionary<Node, ServerChoice>();
        foreach (var node in source.Nodes.Where(node => node.IsServer))
        {
            if (!reusable.TryGetValue(node, out var server))
                server = new ServerChoice(source, node);
            server.Source = source;
            yield return server;
        }
    }

    private void ProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!changingList)
            UpdateControls();
    }

    private void NavigationChanged(object sender, RoutedEventArgs e)
    {
        if (sender != ProfilesNav)
            returnAfterDashboardImport = false;

        ConnectionPage.Visibility = sender == ConnectionNav ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = sender == ProfilesNav ? Visibility.Visible : Visibility.Collapsed;
        CabinetPage.Visibility = sender == CabinetNav ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = sender == SettingsNav ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = sender == DiagnosticsNav ? Visibility.Visible : Visibility.Collapsed;
        if (sender == DiagnosticsNav)
            RefreshStatistics();
        RoutingPage.Visibility = sender == RoutingNav ? Visibility.Visible : Visibility.Collapsed;
        if (sender == RoutingNav)
        {
            RefreshRoutingRows(SelectedRoutingRuleIndex);
            PollRoutingConnections();
        }
        PageScroll.ScrollToTop();
        AnimatePage(sender);
    }

    private void OpenProfiles(object sender, RoutedEventArgs e)
    {
        returnAfterDashboardImport = true;
        ProfilesNav.IsChecked = true;
        SourceBox.Focus();
    }

    private void NodeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!changingList)
        {
            RefreshRoutingSourceView();
            UpdateControls();
        }
    }

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (IsInitialized)
        {
            SaveRoutingSettings();
            UpdateControls();
        }
    }

    private void UpdateControls()
    {
        bool connected = session is not null;
        bool hasServers = NodesList.HasItems;
        bool useTun = TunMode.IsChecked == true;
        bool idle = !busy && !closing;
        bool editable = idle && !connected;
        UpdateSettingsControls(idle, connected);
        UpdateCabinetControls(idle, connected);
        UpdateRoutingControls(idle);
        bool editableProfiles = editable && profilesReadable;
        var profile = Current;
        ProfilesList.IsEnabled = editableProfiles && profiles.Count > 0;
        NodesList.IsEnabled = idle && profilesReadable && hasServers;
        RefreshButton.IsEnabled = editableProfiles && profile?.Subscription is not null;
        bool hasSubscriptions = cabinetSession is not null || profiles.Any(source => source.Subscription is not null);
        RefreshSubscriptionsButton.IsEnabled = idle && profilesReadable && hasSubscriptions;
        RefreshSubscriptionsButton.ToolTip = hasSubscriptions
            ? "Загрузить свежие списки серверов для всех подписок."
            : "Добавьте ссылку на подписку во вкладке «Источники».";
        RemoveButton.IsEnabled = editableProfiles && profile is { CabinetPlanId: null };
        ImportButton.IsEnabled = SourceBox.IsEnabled = NameBox.IsEnabled = editableProfiles;
        ProxyMode.IsEnabled = TunMode.IsEnabled = editable && killSwitch is null;
        BypassLan.IsEnabled = editable;
        ConnectButton.Visibility = connected || hasServers ? Visibility.Visible : Visibility.Collapsed;
        ServerSelection.Visibility = hasServers ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.IsEnabled = idle && (connected || SelectedServer is not null);
        OpenImportButton.Visibility = hasServers ? Visibility.Collapsed : Visibility.Visible;
        OpenImportButton.IsEnabled = editableProfiles;
        BulkCheckButton.IsEnabled = !closing && (serverCheckCancellation is { IsCancellationRequested: false }
            || (idle && hasServers));
        BulkCheckText.Text = serverCheckCancellation switch
        {
            null => "Проверить все",
            { IsCancellationRequested: true } => "Отменяем…",
            _ => "Отменить проверку"
        };
        AutomationProperties.SetName(BulkCheckButton, BulkCheckText.Text);
        BulkCheckIcon.Data = (Geometry)FindResource(serverCheckCancellation is null ? "LucideActivity" : "LucideStop");
        ProfilesLocked.Visibility = SettingsLocked.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        string connectionAction = connected ? "Отключить" : "Подключить";
        AutomationProperties.SetName(ConnectButton, connectionAction);
        ConnectButton.ToolTip = connectionAction;
        if (connected)
        {
            ConnectButton.Background = (Brush)FindResource("ConnectedFill");
            ConnectButton.BorderBrush = (Brush)FindResource("Success");
            ConnectButton.Foreground = (Brush)FindResource("Success");
        }
        else
        {
            ConnectButton.ClearValue(BackgroundProperty);
            ConnectButton.ClearValue(BorderBrushProperty);
            ConnectButton.ClearValue(ForegroundProperty);
        }
        PowerIcon.Data = (Geometry)FindResource(connected ? "LucideStop" : "LucidePower");
        ConnectionLabel.Text = connected ? "Подключено" : killSwitch is null ? "Отключено" : "Сеть заблокирована";
        ConnectionHeadline.Text = hasServers ? connectionAction : "Добавить сервер";
        StatusDot.Fill = (Brush)FindResource(connected ? "Success" : killSwitch is null ? "Muted" : "StatusWarning");
        SyncDashboardMode(useTun, ProxyMode.IsEnabled);
        UpdateDashboardDetails();
        ConnectionDetail.Content = connected ? activeServer?.Node.Name ?? "VPN подключён."
            : hasServers ? SelectedServer?.Node.Name ?? "Выберите сервер."
            : "Вставьте ключ подключения или ссылку на подписку.";
        UpdateTray();
        ProfileInfo.Text = profile is null ? "Источников пока нет."
            : $"{(profile.Subscription is null ? "Ключ" : "Подписка")} · Серверов: {profile.Nodes.Count(node => node.IsServer)}";
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || closing)
            return;
        busy = true;
        UpdateControls();
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (closing) { }
        catch (OperationCanceledException) { Message.Text = "Операция не завершилась вовремя."; }
        catch (HttpRequestException) { Message.Text = "Запрос завершился ошибкой. Проверьте ядро и сервер."; }
        catch (Win32Exception) { Message.Text = "Windows отклонила операцию. Проверьте права и настройки прокси."; }
        catch (IOException error) when (error.GetType() == typeof(IOException) ||
            error is RoutingRecoveryException or SubscriptionDeviceException) { Message.Text = error.Message; }
        catch (Exception) { Message.Text = "Не удалось завершить операцию. Проверьте файлы и настройки клиента."; }
        finally
        {
            busy = false;
            UpdateControls();
        }
    }

    private async void ImportProfile(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var input = SourceBox.Password.Trim();
        if (input.Length == 0)
        {
            Message.Text = "Вставьте ключ или URL подписки.";
            return;
        }
        Message.Text = "Импортируем серверы…";
        var address = Subscription.TryGetAddress(input, out var uri) ? uri : null;
        var downloaded = address is null ? null : await Subscription.DownloadAsync(address, lifetime.Token);
        string content = downloaded?.Content ?? input;
        Node[] nodes = await ReadNodesAsync(content);
        string name = NameBox.Text.Trim();
        if (name.Length == 0)
            name = downloaded?.Title ?? address?.Host ?? nodes[0].Name;

        var profile = new Profile(Guid.NewGuid(), name, address?.AbsoluteUri, content, nodes, nodes[0].Name);
        Commit([.. profiles, profile], profile.Id);
        SourceBox.Clear();
        NameBox.Clear();
        Message.Text = $"Добавлено серверов: {nodes.Length}.";
        if (returnAfterDashboardImport)
        {
            ConnectionNav.IsChecked = true;
            ConnectionNav.Focus();
        }
    });

    private async void RefreshProfile(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (Current is not { Subscription: { } address } profile)
            return;
        if (profile.CabinetPlanId is not null)
        {
            await SyncCabinetAsync();
            return;
        }

        Message.Text = "Загружаем подписку…";
        var updated = await FetchSubscriptionAsync(profile, address);
        Replace(updated);
        Message.Text = $"Подписка обновлена. Серверов в списке: {updated.Nodes.Length}.";
    });

    private async void RefreshSubscriptions(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var subscriptions = profiles.Where(profile => profile.Subscription is not null && profile.CabinetPlanId is null).ToArray();

        var updates = new Dictionary<Guid, Profile>();
        string? deviceError = null;
        for (int index = 0; index < subscriptions.Length; index++)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            Message.Text = $"Обновляем подписки · {index + 1} из {subscriptions.Length}";
            var profile = subscriptions[index];
            try
            {
                updates.Add(profile.Id, await FetchSubscriptionAsync(profile, profile.Subscription!));
            }
            catch (SubscriptionDeviceException error) { deviceError ??= error.Message; }
            catch (Exception error) when (error is IOException or HttpRequestException or Win32Exception
                                          or UnauthorizedAccessException or UriFormatException)
            {
                // Keep the saved servers for any subscription that could not be loaded.
            }
        }

        lifetime.Token.ThrowIfCancellationRequested();
        if (updates.Count > 0)
            Commit(profiles.Select(profile => updates.GetValueOrDefault(profile.Id, profile)).ToList(), Current?.Id);

        if (subscriptions.Length > 0)
            Message.Text = updates.Count == subscriptions.Length ? $"Подписки обновлены · {updates.Count} из {subscriptions.Length}"
                : $"Обновлено {updates.Count} из {subscriptions.Length}. Для остальных сохранены прежние серверы.";
        if (cabinetSession is not null)
            await SyncCabinetAsync();
        if (deviceError is not null)
            Message.Text = deviceError;
    });

    private async Task<Profile> FetchSubscriptionAsync(Profile profile, string address)
    {
        var uri = new Uri(address);
        bool registerDevice = profile.CabinetPlanId is not null;
        var downloaded = await Subscription.DownloadAsync(uri, lifetime.Token, registerDevice);
        var received = profile with { Content = downloaded.Content };
        if (profile.CabinetPlanId is not null && Subscription.CabinetMihomoAddress(uri) is { } mihomo &&
            !SubscriptionRouting.MayHaveRouting(downloaded.Content))
        {
            try
            {
                var full = await Subscription.DownloadAsync(mihomo, lifetime.Token, registerDevice);
                if (SubscriptionRouting.MayHaveRouting(full.Content))
                    return await WithNodesAsync(received with { Content = full.Content });
            }
            catch (IOException)
            {
                // Keep the response from the original link when the format endpoint isn't available.
            }
        }
        return await WithNodesAsync(received);

        async Task<Profile> WithNodesAsync(Profile source)
        {
            var nodes = await ReadNodesAsync(source.Content);
            var selected = nodes.FirstOrDefault(node => node.Name == profile.Selected) ?? nodes[0];
            return source with { Nodes = nodes, Selected = selected.Name };
        }
    }

    private async Task<Node[]> ReadNodesAsync(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new IOException("Источник не содержит серверов.");
        await using var preview = await Mihomo.StartAsync(content, lifetime.Token);
        var nodes = await preview.GetNodesAsync(lifetime.Token);
        if (nodes.Length == 0)
            throw new IOException("Mihomo не нашёл серверов. Проверьте формат ключа или подписки.");
        return nodes;
    }

    private async void RemoveProfile(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        if (Current is { CabinetPlanId: null } profile)
        {
            var remaining = profiles.Where(item => item.Id != profile.Id).ToList();
            Commit(remaining, remaining.FirstOrDefault()?.Id);
            Message.Text = "Источник удалён.";
        }
        return Task.CompletedTask;
    });

    private void Replace(Profile profile)
    {
        var updated = profiles.Select(item => item.Id == profile.Id ? profile : item).ToList();
        Commit(updated, profile.Id);
    }

    private void Commit(List<Profile> updated, Guid? selected)
    {
        store.Save(updated.Where(profile => profile.CabinetPlanId is null).ToList());
        profiles = updated;
        ShowProfiles(selected);
    }

    private async void ToggleConnection(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (session is not null)
        {
            retryAt = null;
            await StopAsync();
            Message.Text = "Подключение закрыто.";
            return;
        }
        retryAt = null;
        await ConnectSelectedAsync();
    });

    private async Task ConnectSelectedAsync()
    {
        if (session is not null || SelectedServer is not { } server)
            return;
        var profile = server.Source;
        var selected = server.Node;
        if (!routingReadable)
            throw new IOException("Не удалось открыть сохранённый профиль маршрутизации. Подключение не запущено.");

        bool useTun = TunMode.IsChecked == true;
        if (useTun)
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new IOException("Закройте клиент и запустите VPNDesktop.exe от имени администратора для TUN.");
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "wintun.dll")))
                throw new IOException("В папке Libraries отсутствует wintun.dll.");
        }

        WindowsProxy.Restore();
        Message.Text = "Запускаем подключение…";
        var candidate = await Mihomo.StartAsync(profile, selected.Name, profiles.ToArray(), routing.Active,
            BypassLan.IsChecked == true, lifetime.Token, network: clientSettings.Network);
        try
        {
            if (useTun)
            {
                if (KillSwitchToggle.IsChecked == true)
                    killSwitch ??= WindowsKillSwitch.Arm(Path.Combine(AppContext.BaseDirectory, "mihomo.exe"));
                await candidate.EnableTunAsync(lifetime.Token);
                killSwitch?.AllowTunAdapter();
            }
            else
                WindowsProxy.Enable(candidate.Port);
            lifetime.Token.ThrowIfCancellationRequested();
            if (profile.CabinetPlanId is null)
                Replace(profile with { Selected = selected.Name });
            session = candidate;
            StartDashboardSession();
            activeServer = server;
            BeginRecovery();
            RecordEvent("Подключение установлено");
            UpdateActiveServerRow();
            RefreshRoutingSourceView();
            Message.Text = useTun ? "Адаптер TUN запущен." : $"Системный прокси работает на 127.0.0.1:{session.Port}.";
        }
        catch
        {
            try
            {
                if (session == candidate)
                {
                    session = null;
                    activeServer = null;
                    EndDashboardSession();
                    UpdateActiveServerRow();
                }
                killSwitch?.BlockTunAdapter();
                WindowsProxy.Restore();
            }
            finally { await candidate.DisposeAsync(); }
            throw;
        }
    }

    private async void CheckAllServers(object sender, RoutedEventArgs e)
    {
        if (serverCheckCancellation is { } running)
        {
            running.Cancel();
            UpdateControls();
            return;
        }

        await RunAsync(() => CheckServersAsync(NodesList.Items.OfType<ServerChoice>().ToArray()));
    }

    private async void CheckServerRow(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ServerChoice server })
            await RunAsync(() => CheckServersAsync([server]));
    }

    private async Task CheckServersAsync(ServerChoice[] servers)
    {
        if (servers.Length == 0)
            return;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        serverCheckCancellation = cancellation;
        UpdateControls();
        Message.Text = "";
        ServerListSummary.Text = $"Проверяем серверы · 0 из {servers.Length}";
        foreach (var server in servers)
            server.SetCheckState(ServerCheckState.Queued);
        int completed = 0;
        int reachable = 0;
        try
        {
            foreach (var source in servers.GroupBy(server => server.Source.Id))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await CheckSourceAsync(source.ToArray(), ReportProgress, cancellation.Token);
            }
            ServerListSummary.Text = $"Доступно {reachable} из {completed}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ServerListSummary.Text = $"Проверка отменена · {completed} из {servers.Length}";
        }
        finally
        {
            foreach (var server in servers.Where(server => server.CheckState is ServerCheckState.Queued or ServerCheckState.Testing))
                server.SetCheckState(ServerCheckState.None);
            serverCheckCancellation = null;
        }

        void ReportProgress(ServerChoice server)
        {
            completed++;
            if (server.CheckState == ServerCheckState.Available)
                reachable++;
            ServerListSummary.Text = $"Проверено {completed} из {servers.Length}";
            UpdateDashboardDetails();
        }
    }

    private async Task CheckSourceAsync(ServerChoice[] servers, Action<ServerChoice> report, CancellationToken cancellationToken)
    {
        var source = servers[0].Source;
        var active = activeServer?.Source is { } activeSource
            && activeSource.Id == source.Id && activeSource.Content == source.Content ? session : null;
        try
        {
            await using var temporary = active is null ? await Mihomo.StartAsync(source.Content, cancellationToken) : null;
            var core = active ?? temporary!;
            foreach (var batch in servers.Chunk(ParallelServerChecks))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.WhenAll(batch.Select(async server =>
                {
                    await CheckOneAsync(core, server, cancellationToken);
                    report(server);
                }));
            }
        }
        catch (Exception error) when (error is IOException or Win32Exception or HttpRequestException or UnauthorizedAccessException)
        {
            foreach (var server in servers.Where(server => server.CheckState == ServerCheckState.Queued))
            {
                server.SetCheckState(ServerCheckState.Error);
                report(server);
            }
        }
    }

    private static async Task CheckOneAsync(Mihomo core, ServerChoice server, CancellationToken cancellationToken)
    {
        server.SetCheckState(ServerCheckState.Testing);
        try
        {
            int? delay = await core.CheckServerAsync(server.Node.Name, cancellationToken);
            if (delay is int milliseconds)
                server.SetCheckState(ServerCheckState.Available, milliseconds);
            else
                server.SetCheckState(ServerCheckState.Unavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            server.SetCheckState(ServerCheckState.Unavailable);
        }
        catch (HttpRequestException)
        {
            server.SetCheckState(ServerCheckState.Error);
        }
    }

    private async Task StopAsync(bool preserveKillSwitch = false)
    {
        var previous = session;
        if (previous is not null)
        {
            RecordSessionEnd();
            RecordEvent(preserveKillSwitch ? "Сеанс прерван" : "Сеанс завершён");
        }
        session = null;
        EndDashboardSession();
        activeServer = null;
        UpdateActiveServerRow();
        RefreshRoutingSourceView();
        try
        {
            killSwitch?.BlockTunAdapter();
            WindowsProxy.Restore();
        }
        finally
        {
            try
            {
                if (previous is not null)
                    await previous.DisposeAsync();
            }
            finally
            {
                if (!preserveKillSwitch)
                    DisarmKillSwitch();
            }
        }
    }

    private async void CheckProcess(object? sender, EventArgs e)
    {
        if (session is { Exited: false } core)
            await SampleDashboardAsync(core);
        if (session?.Exited == true)
        {
            await RunAsync(async () =>
            {
                ScheduleRecovery();
                await StopAsync(preserveKillSwitch: true);
                NotifyTray("Mihomo завершился.");
                Message.Text = killSwitch is null ? "Mihomo завершился. Подключение закрыто."
                    : "Mihomo завершился. Kill Switch удерживает блокировку сети.";
            });
        }
        await CheckRecoveryAsync();
    }

    private async void CloseWindow(object? sender, CancelEventArgs e)
    {
        if (readyToClose)
            return;
        e.Cancel = true;
        if (closing)
            return;
        if (clientSettings.MinimizeToTray && tray is not null && !exitRequested && pendingUpdate is null)
        {
            Hide();
            return;
        }
        closing = true;
        DisposeDesktopIntegration();
        watchdog.Stop();
        routingMonitor.Stop();
        lifetime.Cancel();
        UpdateControls();
        while (busy)
            await Task.Delay(40);
        try
        {
            await StopAsync();
            InstallPendingUpdate();
        }
        catch (Exception)
        {
            CancelPendingUpdate();
            MessageBox.Show("Не удалось завершить очистку. При следующем запуске клиент повторит восстановление.",
                "Linkway", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        lifetime.Dispose();
        readyToClose = true;
        // Reschedule even when there was no active session and no awaited work.
        await Dispatcher.Yield(DispatcherPriority.Background);
        Close();
    }
}
