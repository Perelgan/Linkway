using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VPNDesktop;

public partial class MainWindow
{
    private sealed record RuleRow(int Index, string Title, string Value, string Condition, string Destination)
    {
        public int Priority => Index + 1;
    }

    private RoutingLibrary routing = RoutingLibrary.Create();
    private bool routingReadable;
    private bool pollingConnections;
    private bool routingEmergency;
    private readonly DispatcherTimer routingMonitor = new() { Interval = TimeSpan.FromSeconds(2) };

    private RoutingProfile? RoutingDraft => RoutingProfileBox.SelectedItem as RoutingProfile;
    private int SelectedRoutingRuleIndex => (RoutingRuleList.SelectedItem as RuleRow)?.Index ?? -1;

    private void InitializeRouting()
    {
        try
        {
            routing = RoutingProfiles.Load();
            routingReadable = true;
        }
        catch (Exception)
        {
            RoutingActiveLabel.Text = "Не удалось открыть хранилище маршрутизации.";
        }

        if (routingReadable)
            ShowRoutingProfiles(routing.Active?.Id);
        RoutingRulesTab.IsChecked = true;
        routingMonitor.Tick += (_, _) => PollRoutingConnections();
        routingMonitor.Start();
    }

    private void UpdateRoutingControls(bool idle)
    {
        RoutingModeNote.Text = TunMode.IsChecked == true
            ? "TUN перехватывает TCP, UDP и обычный DNS. Для правил приложений Mihomo определяет процесс по соединению."
            : "PROXY обрабатывает запросы приложений, использующих прокси. Для UDP и приложений без поддержки прокси используйте TUN.";
        RoutingEditor.IsEnabled = idle && routingReadable;
        RoutingApply.IsEnabled = RoutingDraft is not null && (activeServer ?? SelectedServer) is not null;
        RoutingApply.ToolTip = RoutingApply.IsEnabled ? null : "Выберите основной сервер в разделе «Подключение».";
        RoutingDeleteProfile.IsEnabled = routing.Profiles.Length > 1 && RoutingDraft?.Id != routing.Active?.Id;
        var draft = RoutingDraft;
        bool canEdit = idle && routingReadable && draft is not null;
        RoutingSubscriptionToggle.IsEnabled = canEdit;
        RoutingSubscriptionToggle.IsChecked = draft?.UseSubscriptionRules == true;
        RoutingAddRule.IsEnabled = RoutingEditDefault.IsEnabled = canEdit;
        UpdateSubscriptionGroupControls(canEdit);
        int index = SelectedRoutingRuleIndex;
        RoutingRuleActions.Visibility = index >= 0 ? Visibility.Visible : Visibility.Collapsed;
        RoutingEditRule.IsEnabled = RoutingRemoveRule.IsEnabled = RoutingToggleRule.IsEnabled = canEdit && index >= 0;
        RoutingUp.IsEnabled = canEdit && index > 0;
        RoutingDown.IsEnabled = canEdit && index >= 0 && index + 1 < draft!.Rules.Length;
        RoutingToggleRule.Content = index >= 0 && RoutingDraft?.Rules.ElementAtOrDefault(index) is { Enabled: false }
            ? "Включить" : "Отключить";
        RoutingRuleFromConnection.IsEnabled = idle && routingReadable && session is not null
            && RoutingConnectionList.SelectedItem is ActiveConnection;
        if (session is null)
        {
            routingEmergency = false;
            RoutingConnectionList.ItemsSource = null;
            RoutingMonitorStatus.Text = "Подключите VPN, чтобы увидеть соединения Mihomo.";
        }
        if (routingReadable)
            RoutingActiveLabel.Text = routingEmergency ? "Состояние применения требует проверки. Примените профиль ещё раз."
                : routing.Active is { } applied ? $"Применён профиль «{applied.Name}». Изменения сохраняются в черновике."
                : "Применённого профиля пока нет. Изменения сохраняются в черновике.";
    }

    private void OpenRoutingProfileMenu(object sender, RoutedEventArgs e)
    {
        var menu = RoutingProfileMenu.ContextMenu;
        menu.PlacementTarget = RoutingProfileMenu;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void RoutingTabChanged(object sender, RoutedEventArgs e)
    {
        RoutingRulesPane.Visibility = sender == RoutingRulesTab ? Visibility.Visible : Visibility.Collapsed;
        RoutingGroupsPane.Visibility = sender == RoutingGroupsTab ? Visibility.Visible : Visibility.Collapsed;
        RoutingConnectionsPane.Visibility = sender == RoutingConnectionsTab ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.ScrollToTop();
        if (sender == RoutingConnectionsTab)
            PollRoutingConnections();
        if (sender == RoutingGroupsTab)
            RefreshSubscriptionGroups();
    }

    private void ShowRoutingProfiles(Guid? selection, int ruleIndex = -1)
    {
        RoutingProfileBox.ItemsSource = routing.Profiles;
        RoutingProfileBox.SelectedItem = routing.Profiles.FirstOrDefault(profile => profile.Id == selection) ?? routing.Profiles[0];
        RefreshRoutingRows(ruleIndex);
    }

    private void RoutingProfileChanged(object sender, SelectionChangedEventArgs e) => RefreshRoutingRows(-1);

    private void RefreshRoutingRows(int ruleIndex)
    {
        var profile = RoutingDraft;
        RefreshRoutingRules(ruleIndex);
        RefreshSubscriptionGroups();
        RoutingDefaultLabel.Content = RoutingFallbackLabel(profile);
        UpdateRoutingControls(!busy && !closing);
    }

    private void RoutingFilterChanged(object sender, TextChangedEventArgs e) =>
        RefreshRoutingRules(SelectedRoutingRuleIndex);

    private void RefreshRoutingRules(int selected)
    {
        var profile = RoutingDraft;
        string query = RoutingRuleFilter.Text.Trim();
        RuleRow[] rows = profile?.Rules.Select((rule, index) => new RuleRow(
            index, rule.Match == RouteMatch.Application ? Path.GetFileName(rule.Value) : rule.Value, rule.Value,
            $"{MatchLabel(rule.Match)}  ·  {ProtocolLabel(rule.Protocol)}{(rule.Enabled ? "" : "  ·  Отключено")}",
            TargetLabel(rule.Target)))
            .Where(row => query.Length == 0 || Matches(row.Value) || Matches(row.Condition) || Matches(row.Destination))
            .ToArray() ?? [];
        RoutingRuleList.ItemsSource = rows;
        RoutingRuleList.SelectedItem = rows.FirstOrDefault(row => row.Index == selected);
        RoutingRulesEmpty.Text = query.Length == 0 ? "В этом профиле пока нет правил." : "По запросу ничего не найдено.";
        RoutingRulesEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        bool Matches(string text) => text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RoutingRuleChanged(object sender, SelectionChangedEventArgs e) => UpdateRoutingControls(!busy && !closing);
    private void RoutingConnectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRoutingControls(!busy && !closing);

    private void SaveRouting(RoutingLibrary updated, Guid? selected, int ruleIndex = -1)
    {
        RoutingProfiles.Save(updated);
        routing = updated;
        ShowRoutingProfiles(selected, ruleIndex);
    }

    private void SaveDraft(RoutingProfile value, int ruleIndex = -1)
    {
        var draft = RoutingProfiles.Validate(value);
        SaveRouting(routing with
        {
            Profiles = routing.Profiles.Select(profile => profile.Id == draft.Id ? draft : profile).ToArray()
        }, draft.Id, ruleIndex);
    }

    private void AddDraft(RoutingProfile draft)
    {
        if (routing.Profiles.Length >= 100)
            throw new IOException("Достигнут предел в 100 профилей маршрутизации.");
        SaveRouting(routing with { Profiles = [.. routing.Profiles, RoutingProfiles.Validate(draft)] }, draft.Id);
    }

    private async void NewRoutingProfile(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        if (EditRoutingName("Новый профиль", "") is { } name)
            AddDraft(RoutingProfile.Create(name));
        return Task.CompletedTask;
    });

    private async void RenameRoutingProfile(object sender, RoutedEventArgs e) => await EditDraftAsync(profile =>
    {
        if (EditRoutingName("Название профиля", profile.Name) is { } name)
            SaveDraft(profile with { Name = name });
    });

    private async void ImportRoutingProfile(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        var dialog = new OpenFileDialog { Filter = "Профиль маршрутизации (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            AddDraft(RoutingProfiles.Import(dialog.FileName));
            Message.Text = "Профиль импортирован. Проверьте серверы и нажмите «Применить профиль».";
        }
        return Task.CompletedTask;
    });

    private async void ExportRoutingProfile(object sender, RoutedEventArgs e) => await EditDraftAsync(profile =>
    {
        var dialog = new SaveFileDialog { Filter = "Профиль маршрутизации (*.json)|*.json", FileName = "routing.json" };
        if (dialog.ShowDialog(this) == true)
        {
            RoutingProfiles.Export(dialog.FileName, profile);
            Message.Text = "Профиль экспортирован.";
        }
    });

    private async void DeleteRoutingProfile(object sender, RoutedEventArgs e) => await EditDraftAsync(profile =>
    {
        if (routing.Profiles.Length <= 1 || profile.Id == routing.Active?.Id)
            return;
        if (MessageBox.Show(this, $"Удалить профиль «{profile.Name}»?", "Linkway", MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
            SaveRouting(routing with { Profiles = routing.Profiles.Where(draft => draft.Id != profile.Id).ToArray() }, null);
    });

    private async void ApplyRoutingProfile(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (RoutingDraft is not { } draft)
            return;
        draft = RoutingProfiles.Validate(draft);
        var updated = routing with { Active = draft };
        Message.Text = "Применяем профиль маршрутизации…";
        if (session is { } core && activeServer is { } primary)
        {
            try
            {
                await core.ApplyRoutingAsync(primary.Source, primary.Node.Name, profiles.ToArray(), draft,
                    BypassLan.IsChecked == true, () =>
                    {
                        killSwitch?.AllowTunAdapter();
                        RoutingProfiles.Save(updated);
                    }, lifetime.Token, clientSettings.Network);
            }
            catch (RoutingRecoveryException)
            {
                routingEmergency = true;
                throw;
            }
        }
        else if (SelectedServer is { } selected)
        {
            await using (var preview = await Mihomo.StartAsync(selected.Source, selected.Node.Name, profiles.ToArray(), draft,
                BypassLan.IsChecked == true, lifetime.Token))
            {
                lifetime.Token.ThrowIfCancellationRequested();
            }
            RoutingProfiles.Save(updated);
        }
        else
            throw new IOException("Выберите основной сервер в разделе «Подключение».");
        routing = updated;
        routingEmergency = false;
        Message.Text = session is null ? "Профиль будет действовать при подключении." : "Профиль применён. Соединения обновятся по новым правилам.";
    });

    private Task EditDraftAsync(Action<RoutingProfile> edit) => RunAsync(() =>
    {
        if (RoutingDraft is { } draft)
            edit(draft);
        return Task.CompletedTask;
    });

    private async void AddRoutingRule(object sender, RoutedEventArgs e) => await EditDraftAsync(profile =>
    {
        if (EditRuleDialog(new(RouteMatch.Domain, "", RouteProtocol.Any, NewRuleTarget())) is { } rule)
        {
            RoutingRuleFilter.Clear();
            SaveDraft(profile with { Rules = [.. profile.Rules, rule] }, profile.Rules.Length);
            RoutingRuleList.ScrollIntoView(RoutingRuleList.SelectedItem);
        }
    });

    private async void EditRoutingRule(object sender, RoutedEventArgs e) => await EditDraftAsync(profile =>
    {
        int index = SelectedRoutingRuleIndex;
        if (index >= 0 && EditRuleDialog(profile.Rules[index]) is { } rule)
            ChangeRule(profile, index, rule);
    });

    private async void ToggleRoutingRule(object sender, RoutedEventArgs e) => await EditDraftAsync(profile =>
    {
        int index = SelectedRoutingRuleIndex;
        if (index >= 0)
            ChangeRule(profile, index, profile.Rules[index] with { Enabled = !profile.Rules[index].Enabled });
    });

    private void ChangeRule(RoutingProfile profile, int index, RouteRule rule)
    {
        var rules = profile.Rules.ToArray();
        rules[index] = rule;
        SaveDraft(profile with { Rules = rules }, index);
    }

    private async void RemoveRoutingRule(object sender, RoutedEventArgs e) => await EditDraftAsync(profile =>
    {
        int index = SelectedRoutingRuleIndex;
        if (index >= 0)
            SaveDraft(profile with { Rules = profile.Rules.Where((_, i) => i != index).ToArray() },
                Math.Min(index, profile.Rules.Length - 2));
    });

    private async void MoveRoutingRule(object sender, RoutedEventArgs e) => await EditDraftAsync(profile =>
    {
        int source = SelectedRoutingRuleIndex;
        int destination = source + (sender == RoutingUp ? -1 : 1);
        if (source < 0 || destination < 0 || destination >= profile.Rules.Length)
            return;
        var rules = profile.Rules.ToArray();
        (rules[source], rules[destination]) = (rules[destination], rules[source]);
        SaveDraft(profile with { Rules = rules }, destination);
    });

    private async void EditRoutingDefault(object sender, RoutedEventArgs e) => await EditDraftAsync(profile =>
    {
        if (ChooseRoutingFallback(profile) is { } choice)
            SaveDraft(profile with { Default = choice, PreferLocalFallback = true },
                SelectedRoutingRuleIndex);
    });

    private async void PollRoutingConnections()
    {
        if (closing || busy || pollingConnections || RoutingNav.IsChecked != true ||
            RoutingConnectionsTab.IsChecked != true || session is not { } core)
            return;
        pollingConnections = true;
        try
        {
            var connections = await core.GetConnectionsAsync(lifetime.Token);
            if (session != core || closing || busy)
                return;
            string? selection = (RoutingConnectionList.SelectedItem as ActiveConnection)?.Id;
            RoutingConnectionList.ItemsSource = connections;
            RoutingConnectionList.SelectedItem = connections.FirstOrDefault(item => item.Id == selection);
            RoutingMonitorStatus.Text = connections.Length == 0 ? "У Mihomo сейчас нет активных соединений." : $"Соединений: {connections.Length}";
        }
        catch (Exception) when (closing || session != core) { }
        catch (Exception)
        {
            RoutingConnectionList.ItemsSource = null;
            RoutingMonitorStatus.Text = "Не удалось получить список соединений Mihomo.";
        }
        finally { pollingConnections = false; }
    }

    private async void AddRuleFromConnection(object sender, RoutedEventArgs e) => await EditDraftAsync(profile =>
    {
        if (RoutingConnectionList.SelectedItem is not ActiveConnection connection)
            return;
        var candidates = new List<(RouteMatch Match, string Text)>();
        foreach (var candidate in new[] { (RouteMatch.Application, connection.Path), (RouteMatch.Domain, connection.Host),
                     (RouteMatch.Address, connection.Ip), (RouteMatch.Port, connection.Port) })
        {
            try { candidates.Add((candidate.Item1, RoutingProfiles.Normalize(candidate.Item1, candidate.Item2))); }
            catch (IOException) { }
        }
        var protocol = connection.Network.ToLowerInvariant() switch
        {
            "tcp" => RouteProtocol.Tcp,
            "udp" => RouteProtocol.Udp,
            _ => RouteProtocol.Any
        };
        if (candidates.Count == 0)
            throw new IOException("У соединения нет данных для создания правила.");
        var seed = candidates[0];
        if (EditRuleDialog(new(seed.Match, seed.Text, protocol, NewRuleTarget()), candidates) is { } rule)
        {
            RoutingRuleFilter.Clear();
            SaveDraft(profile with { Rules = [rule, .. profile.Rules] }, 0);
            RoutingRulesTab.IsChecked = true;
        }
    });
}

