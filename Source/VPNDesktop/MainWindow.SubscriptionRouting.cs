using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace VPNDesktop;

public partial class MainWindow
{
    private sealed record SubscriptionGroupRow(string Name, string Details, SubscriptionRouteOption[] Options);
    private sealed record SubscriptionRouteSelection(string? Destination = null, RouteTarget? Target = null);
    private sealed record SubscriptionRouteOption(SubscriptionRouteSelection Selection, string Label, bool Selected);

    private Profile? RoutingSource => (activeServer ?? SelectedServer)?.Source;

    private void RefreshRoutingSourceView()
    {
        if (RoutingNav.IsChecked != true)
            return;
        RefreshSubscriptionGroups();
        RoutingDefaultLabel.Content = RoutingFallbackLabel(RoutingDraft);
    }

    private string RoutingFallbackLabel(RoutingProfile? draft) => draft is null ? "—" : TargetLabel(draft.Default);

    private void RefreshSubscriptionGroups()
    {
        var source = RoutingSource;
        var draft = RoutingDraft;
        string? selected = (RoutingSubscriptionGroups.SelectedItem as SubscriptionGroupRow)?.Name;
        SubscriptionGroupRow[] rows = [];
        string status = "Выберите основной сервер в разделе «Подключение».";
        if (source is not null)
        {
            status = $"В источнике «{source.Name}» нет правил подписки. Для категорий нужна полная конфигурация Mihomo.";
            try
            {
                if (SubscriptionRouting.Read(source) is { } subscription)
                {
                    var counts = subscription.Rules.ToLookup(rule => rule.Destination, StringComparer.Ordinal);
                    string key = RouteServer.SourceKey(source);
                    var choices = (draft?.SubscriptionChoices ?? []).Where(choice => choice.Source == key)
                        .ToDictionary(choice => choice.Group, StringComparer.Ordinal);
                    var personal = ServerRouteOptions().ToArray();
                    rows = subscription.Groups.Values.Where(group => subscription.IsVisibleGroup(group.Name)).Select(group =>
                    {
                        choices.TryGetValue(group.Name, out var saved);
                        string? defaultRoute = group.Default(source);
                        string? route = saved?.Destination ?? defaultRoute;
                        string label = saved?.Target is { } target ? TargetLabel(target)
                            : route is null || (!SubscriptionRouting.IsLocalOption(route) &&
                            !group.Options(source).Contains(route, StringComparer.Ordinal))
                            ? "Маршрут недоступен" : subscription.IsServiceGroup(route)
                                ? "Автоматическая группа" : SubscriptionOptionLabel(route);
                        int count = counts[group.Name].Count();
                        var current = saved?.Destination == RoutingConfig.Main
                            ? new SubscriptionRouteSelection(Target: NewRuleTarget())
                            : new(saved?.Destination, saved?.Target);
                        bool unavailableSites = SubscriptionRouting.IsUnavailableSitesGroup(group.Name);
                        if (unavailableSites)
                            return new SubscriptionGroupRow(group.Name,
                                $"{DashboardUnavailableServerLabel()}  ·  Сервер выбирается на экране подключения", []);
                        string[] members = group.Options(source).Append("DIRECT").Append("REJECT")
                            .Where(value => value != group.Name && value != RoutingConfig.Main && !subscription.IsServiceGroup(value))
                            .Distinct(StringComparer.Ordinal).ToArray();
                        var options = members.Select(value => (Selection: new SubscriptionRouteSelection(value), Label: SubscriptionOptionLabel(value)))
                            .Concat(personal.Where(option => option.Value.Server is not { } server ||
                                server.Source != key || !members.Contains(server.Name, StringComparer.Ordinal) || option.Value == current.Target)
                                .Select(option => (Selection: new SubscriptionRouteSelection(Target: option.Value), Label: option.Label)))
                            .Select(option => new SubscriptionRouteOption(option.Selection, option.Label, option.Selection == current))
                            .ToArray();
                        return new SubscriptionGroupRow(group.Name, count > 0 ? $"{label}  ·  Правил: {count}" : label, options);
                    }).ToArray();
                    status = $"Источник «{source.Name}». Правил подписки: {subscription.Rules.Length}, наборов: {subscription.Feeds.Count}. " +
                        (draft?.UseSubscriptionRules == false ? "Правила подписки отключены в этом профиле."
                            : rows.Length == 0 ? "В подписке нет групп с ручным выбором маршрута."
                            : "Нажмите на группу, чтобы раскрыть маршруты.");
                }
            }
            catch (IOException error) { status = error.Message; }
        }

        RoutingSubscriptionStatus.Text = status;
        RoutingSubscriptionGroups.ItemsSource = rows;
        RoutingSubscriptionGroups.SelectedItem = rows.FirstOrDefault(row => row.Name == selected);
        UpdateSubscriptionGroupControls(!busy && !closing && routingReadable && draft is not null);
    }

    private void UpdateSubscriptionGroupControls(bool canEdit)
    {
        bool selected = RoutingSubscriptionGroups.SelectedItem is SubscriptionGroupRow;
        RoutingSubscriptionGroups.IsEnabled = canEdit;
        RoutingAddSubscriptionRule.Visibility = RoutingSubscriptionGroups.HasItems ? Visibility.Visible : Visibility.Collapsed;
        RoutingAddSubscriptionRule.IsEnabled = canEdit && selected;
        RoutingAssignSubscriptionRule.Visibility = RoutingAddSubscriptionRule.Visibility;
        RoutingAssignSubscriptionRule.IsEnabled = canEdit && selected && RoutingDraft?.Rules.Length > 0;
    }

    private void RoutingSubscriptionGroupChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSubscriptionGroupControls(!busy && !closing && routingReadable && RoutingDraft is not null);

    private async void ToggleSubscriptionRouting(object sender, RoutedEventArgs e)
    {
        // The draft is shown again while RunAsync disables the editor.
        bool enabled = RoutingSubscriptionToggle.IsChecked == true;
        await EditDraftAsync(profile =>
        {
            if (profile.UseSubscriptionRules != enabled)
                SaveDraft(profile with { UseSubscriptionRules = enabled }, SelectedRoutingRuleIndex);
        });
    }

    private void ToggleSubscriptionGroup(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SubscriptionGroupRow row })
            RoutingSubscriptionGroups.SelectedItem = ReferenceEquals(RoutingSubscriptionGroups.SelectedItem, row) ? null : row;
    }

    private async void SelectSubscriptionRoute(object sender, RoutedEventArgs e) => await EditDraftAsync(profile =>
    {
        if (RoutingSource is not { } source || RoutingSubscriptionGroups.SelectedItem is not SubscriptionGroupRow row ||
            sender is not Button { DataContext: SubscriptionRouteOption option } || option.Selected ||
            !row.Options.Contains(option))
            return;
        string key = RouteServer.SourceKey(source);
        var selection = option.Selection;
        var remaining = (profile.SubscriptionChoices ?? []).Where(choice => choice.Source != key || choice.Group != row.Name);
        SaveDraft(profile with
        {
            SubscriptionChoices = [.. remaining, new(key, row.Name, selection.Destination, selection.Target)]
        }, SelectedRoutingRuleIndex);
        Message.Text = $"Маршрут для «{row.Name}» сохранён. Нажмите «Применить».";
    });

    private async void AddSubscriptionGroupRule(object sender, RoutedEventArgs e) => await EditDraftAsync(profile =>
    {
        if (SelectedSubscriptionTarget() is not { } target)
            return;
        if (EditRuleDialog(new(RouteMatch.Domain, "", RouteProtocol.Any, target)) is not { } rule)
            return;
        RoutingRuleFilter.Clear();
        SaveDraft(profile with { Rules = [.. profile.Rules, rule] }, profile.Rules.Length);
        RoutingRulesTab.IsChecked = true;
        RoutingRuleList.ScrollIntoView(RoutingRuleList.SelectedItem);
    });

    private async void AssignSubscriptionGroupRule(object sender, RoutedEventArgs e) => await EditDraftAsync(profile =>
    {
        if (SelectedSubscriptionTarget() is not { } target || profile.Rules.Length == 0)
            return;
        var editor = NewEditor(this, $"Правило для группы «{target.Category!.Name}»");
        var options = profile.Rules.Select((rule, index) => new Choice<int>(index,
            $"{index + 1}. {rule.Value} · {MatchLabel(rule.Match)} · {TargetLabel(rule.Target)}"));
        var field = SelectField(editor, "Существующее правило", options, 0);
        if (Finish(editor, "Назначить", () => (Choice<int>)field.SelectedItem) is not { } choice)
            return;
        RoutingRuleFilter.Clear();
        ChangeRule(profile, choice.Value, profile.Rules[choice.Value] with { Target = target });
        RoutingRulesTab.IsChecked = true;
        RoutingRuleList.ScrollIntoView(RoutingRuleList.SelectedItem);
    });

    private RouteTarget? SelectedSubscriptionTarget() =>
        RoutingSource is { } source && RoutingSubscriptionGroups.SelectedItem is SubscriptionGroupRow row
            ? new(RouteKind.SubscriptionGroup, Category: new(RouteServer.SourceKey(source), row.Name)) : null;

    private string SubscriptionOptionLabel(string option) => option switch
    {
        RoutingConfig.Main => ConnectionServerLabel,
        "DIRECT" => "Напрямую",
        "REJECT" or "REJECT-DROP" => "Блокировка",
        _ => option
    };
}

