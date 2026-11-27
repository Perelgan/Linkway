using System.ComponentModel;
using System.IO;
using System.Net.Http;
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
    private VpnSessionClient? session;
    private ServerChoice? activeServer;
    private CancellationTokenSource? serverCheckCancellation;
    private Task? serverCheckTask;
    private bool busy, closing, readyToClose, changingList, profilesReadable;
    private bool returnAfterDashboardImport;
    private readonly bool windowsStartup;
    private Profile? Current => ProfilesList.SelectedItem as Profile;
    private ServerChoice? SelectedServer => NodesList.SelectedItem as ServerChoice;

    public MainWindow() : this(preview: false) { }

    // The rendering test exercises the same BAML and layout without opening the user profile.
    internal MainWindow(bool preview, bool windowsStartup = false)
    {
        this.windowsStartup = windowsStartup;
        InitializeComponent();
        ConnectionNav.IsChecked = true;
        if (preview)
            return;
        try
        {
            Directory.CreateDirectory(ProfileStore.DirectoryPath);
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
        Application.Current.SessionEnding += (_, _) => systemEnding = true;
        watchdog.Tick += CheckProcess;
        watchdog.Start();
        InitializeDesktopIntegration();
    }

    private void ShowProfiles(Guid? selected, bool preferSelected = false)
    {
        var previous = SelectedServer;
        var activeRoute = activeServer is { } active ? RouteServer.From(active) : null;
        changingList = true;
        ProfilesList.ItemsSource = profiles;
        ProfilesList.SelectedItem = profiles.FirstOrDefault(profile => profile.Id == selected);
        var previousBySource = NodesList.Items.OfType<ServerChoice>().ToLookup(server => server.Source.Id);
        var servers = profiles.SelectMany(profile => BuildServerRows(profile, previousBySource[profile.Id])).ToArray();
        NodesList.ItemsSource = servers;
        NodesList.SelectedItem = (!preferSelected ? servers.FirstOrDefault(server => previous is not null
                && server.Source.Id == previous.Source.Id && server.Node.Name == previous.Node.Name)
            : null)
            ?? (activeRoute is not null ? servers.FirstOrDefault(server => RouteServer.From(server) == activeRoute) : null)
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
        var active = activeServer;
        var route = active is not null ? RouteServer.From(active) : null;
        foreach (var server in NodesList.Items.OfType<ServerChoice>())
        {
            server.ShowLatency = clientSettings.DeveloperMode;
            server.IsConnected = route is not null && server.Node.Name == route.Name &&
                RouteServer.SourceKey(server.Source) == route.Source && server.Source.Content == active?.Source.Content;
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

    private async void NodeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingList || busy || closing || e.Source != NodesList)
            return;

        if (session is { } core && activeServer is { } previous && SelectedServer is { } target && target != previous)
        {
            await RunAsync(() => SwitchServerAsync(core, previous, target));
            return;
        }

        RefreshRoutingSourceView();
        UpdateControls();
    }

    private async Task SwitchServerAsync(VpnSessionClient core, ServerChoice previous, ServerChoice target)
    {
        Message.Text = "Переключаем сервер…";
        try
        {
            await ApplyLiveRoutingAsync(core, SessionConfiguration(target, routing.Active), () => RememberSelectedServer(target));
            activeServer = target;
            ResetConnectionHealth();
            UpdateActiveServerRow();
            RecordEvent("Сервер переключён");
            Message.Text = "Сервер переключён.";
        }
        finally
        {
            if (activeServer == previous)
                NodesList.SelectedItem = NodesList.Items.OfType<ServerChoice>().FirstOrDefault(server =>
                    server.Source.Id == previous.Source.Id && server.Node.Name == previous.Node.Name);
            RefreshRoutingSourceView();
        }
    }

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (IsInitialized && !changingList)
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
        ProbeRouteButton.IsEnabled = ProbeRouteInput.IsEnabled = idle;
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
        ProxyMode.IsEnabled = TunMode.IsEnabled = editable && !killSwitch && !backgroundUnavailable;
        BypassLan.IsEnabled = idle && !backgroundUnavailable;
        ConnectButton.Visibility = connected || hasServers ? Visibility.Visible : Visibility.Collapsed;
        ServerSelection.Visibility = hasServers ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.IsEnabled = idle && !backgroundUnavailable && (connected || SelectedServer is not null);
        OpenImportButton.Visibility = hasServers ? Visibility.Collapsed : Visibility.Visible;
        OpenImportButton.IsEnabled = editableProfiles;
        BulkCheckButton.IsEnabled = serverCheckCancellation is { } ping
            ? !closing && !ping.IsCancellationRequested : idle && hasServers;
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
        ConnectionLabel.Text = connected ? "Подключено" : backgroundUnavailable ? "Фоновый VPN" : killSwitch ? "Сеть заблокирована" : "Отключено";
        ConnectionHeadline.Text = hasServers ? connectionAction : "Добавить сервер";
        StatusDot.Fill = (Brush)FindResource(connected ? "Success" : killSwitch || backgroundUnavailable ? "StatusWarning" : "Muted");
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
        var downloaded = address is null ? null : await Subscription.DownloadWithRoutingAsync(address, lifetime.Token);
        string content = downloaded?.Content ?? input;
        Node[] nodes = await ReadNodesAsync(content);
        string name = NameBox.Text.Trim();
        if (name.Length == 0)
            name = downloaded?.Title ?? address?.Host ?? nodes[0].Name;

        var profile = new Profile(Guid.NewGuid(), name, address?.AbsoluteUri, content, nodes, nodes[0].Name);
        Commit([.. profiles, profile], profile.Id);
        SourceBox.Clear();
        NameBox.Clear();
        Message.Text = $"Добавлено серверов: {nodes.Length}." +
            (address is null ? "" : $" {SubscriptionRuleSummary(profile)}");
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
        Message.Text = $"Подписка обновлена. Серверов: {updated.Nodes.Length}. {SubscriptionRuleSummary(updated)}";
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
        var downloaded = await Subscription.DownloadWithRoutingAsync(new Uri(address), lifetime.Token,
            registerDevice: profile.CabinetPlanId is not null, previousContent: profile.Content);
        var nodes = await ReadNodesAsync(downloaded.Content);
        var selected = nodes.FirstOrDefault(node => node.Name == profile.Selected) ?? nodes[0];
        return profile with { Content = downloaded.Content, Nodes = nodes, Selected = selected.Name };
    }

    private static string SubscriptionRuleSummary(Profile source)
    {
        try
        {
            return SubscriptionRouting.Read(source) is { } subscription
                ? $"Правил подписки: {subscription.Rules.Length}, наборов: {subscription.Feeds.Count}."
                : "Сервер вернул список узлов без правил.";
        }
        catch (IOException error)
        {
            return error.Message;
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
        AnimateConnectionButton();
        startupRetryUntil = null;
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
        if (backgroundUnavailable || session is not null || SelectedServer is not { } server)
            return;
        if (!routingReadable)
            throw new IOException("Не удалось открыть сохранённый профиль маршрутизации. Подключение не запущено.");

        bool useTun = TunMode.IsChecked == true;
        if (useTun)
        {
            if (!HostProtocol.Elevated)
                throw new IOException("Закройте клиент и запустите VPNDesktop.exe от имени администратора для TUN.");
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "wintun.dll")))
                throw new IOException("В папке Libraries отсутствует wintun.dll.");
        }

        Message.Text = "Запускаем подключение…";
        try
        {
            var result = await VpnSessionClient.StartAsync(SessionConfiguration(server, routing.Active),
                () => RememberSelectedServer(server), lifetime.Token);
            AcceptBackgroundSession(result);
            RecordEvent("Подключение установлено");
            Message.Text = useTun ? "Адаптер TUN запущен." : $"Системный прокси работает на 127.0.0.1:{result.Connection!.Port}.";
        }
        catch
        {
            if (!closing && HostProtocol.Exists())
                await TryAttachAfterFailureAsync();
            throw;
        }
    }

    private void RememberSelectedServer(ServerChoice server)
    {
        if (server.Source.CabinetPlanId is null)
            Replace(server.Source with { Selected = server.Node.Name });
    }

    private async void CheckAllServers(object sender, RoutedEventArgs e)
    {
        if (serverCheckCancellation is { } running)
        {
            running.Cancel();
            UpdateControls();
            return;
        }

        await StartServerCheckAsync(NodesList.Items.OfType<ServerChoice>().ToArray());
    }

    private async void CheckServerRow(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ServerChoice server })
            await StartServerCheckAsync([server]);
    }

    private async Task StartServerCheckAsync(ServerChoice[] servers)
    {
        if (busy || closing || serverCheckCancellation is not null || servers.Length == 0)
            return;

        serverCheckTask = RunCheckAsync();
        try { await serverCheckTask; }
        finally
        {
            serverCheckTask = null;
            if (!closing)
                UpdateControls();
        }

        async Task RunCheckAsync()
        {
            try { await CheckServersAsync(servers); }
            catch (OperationCanceledException) when (closing) { }
            catch (Exception error) when (error is IOException or Win32Exception or HttpRequestException or
                UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
            {
                if (!closing)
                    ServerListSummary.Text = "Не удалось завершить проверку серверов.";
            }
        }
    }

    private async Task CheckServersAsync(ServerChoice[] servers)
    {
        if (servers.Length == 0)
            return;
        if (killSwitch)
        {
            ServerListSummary.Text = "Kill Switch блокирует прямую проверку серверов.";
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        serverCheckCancellation = cancellation;
        UpdateControls();
        ServerListSummary.Text = $"Проверяем напрямую · 0 из {servers.Length}";
        foreach (var server in servers)
            server.SetCheckState(ServerCheckState.Queued);
        int completed = 0;
        int reachable = 0;
        try
        {
            var paths = WindowsDirectProbe.GetPaths();
            foreach (var source in servers.GroupBy(server => server.Source.Id))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                string content = source.First().Source.Content;
                var endpoints = await Task.Run(() => ServerEndpoint.Read(content), cancellation.Token);
                foreach (var batch in source.Chunk(ParallelServerChecks))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    await Task.WhenAll(batch.Select(async server =>
                    {
                        await CheckOneAsync(server, endpoints.GetValueOrDefault(server.Node.Name), paths, cancellation.Token);
                        ReportProgress(server);
                    }));
                }
            }
            ServerListSummary.Text = $"Ответили напрямую {reachable} из {completed}";
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

    private static async Task CheckOneAsync(ServerChoice server, ServerEndpoint? endpoint, WindowsDirectProbe.Path[] paths,
        CancellationToken cancellationToken)
    {
        if (endpoint is null)
        {
            server.SetCheckState(ServerCheckState.Error);
            return;
        }
        if (!endpoint.Tcp)
        {
            server.SetCheckState(ServerCheckState.Unsupported);
            return;
        }
        server.SetCheckState(ServerCheckState.Testing);
        int? delay = await WindowsDirectProbe.MeasureAsync(endpoint, paths, cancellationToken);
        if (delay is int milliseconds)
            server.SetCheckState(ServerCheckState.Available, milliseconds);
        else
            server.SetCheckState(ServerCheckState.Unavailable);
    }

    private async Task StopAsync(bool preserveKillSwitch = false)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(16));
        if (HostProtocol.Exists() && !backgroundUnavailable)
            killSwitch = (await VpnSessionClient.StopAsync(preserveKillSwitch, deadline.Token)).Guard;
        else if (!backgroundUnavailable)
        {
            WindowsProxy.Restore();
            killSwitch = false;
        }
        ClearBackgroundSession(preserveKillSwitch);
    }

    private async void CheckProcess(object? sender, EventArgs e)
    {
        if (pollingSession || closing || busy)
            return;
        pollingSession = true;
        try
        {
            await PollBackgroundSessionAsync();
            await CheckRecoveryAsync();
        }
        finally { pollingSession = false; }
    }

    private async void CloseWindow(object? sender, CancelEventArgs e)
    {
        if (readyToClose)
            return;
        e.Cancel = true;
        if (closing || closePromptOpen)
            return;
        WindowCloseAction? action = pendingUpdate is not null || systemEnding ? WindowCloseAction.KeepVpn : SavedCloseAction;
        if (action == WindowCloseAction.Ask || (exitRequested && action == WindowCloseAction.Tray) ||
            (backgroundUnavailable && action == WindowCloseAction.Disconnect))
            action = ChooseCloseAction();
        exitRequested = false;
        if (action is null)
            return;
        if (action == WindowCloseAction.Tray)
        {
            Hide();
            return;
        }
        closing = true;
        UpdateControls();
        while (busy)
            await Task.Delay(40);
        if (action == WindowCloseAction.Disconnect)
        {
            try { await StopAsync(); }
            catch (Exception)
            {
                closing = false;
                UpdateControls();
                Message.Text = "Не удалось отключить фоновый VPN. Повторите попытку.";
                return;
            }
        }
        DisposeDesktopIntegration();
        watchdog.Stop();
        routingMonitor.Stop();
        lifetime.Cancel();
        UpdateControls();
        try
        {
            if (serverCheckTask is { } pendingCheck)
                await pendingCheck;
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
