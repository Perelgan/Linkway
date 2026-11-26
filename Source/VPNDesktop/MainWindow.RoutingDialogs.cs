using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace VPNDesktop;

public partial class MainWindow
{
    private sealed record Choice<T>(T Value, string Label);
    private sealed record Editor(Window Window, StackPanel Body);

    private static string MatchLabel(RouteMatch kind) => kind switch
    {
        RouteMatch.Application => "Приложение",
        RouteMatch.Domain => "Домен",
        RouteMatch.DomainSuffix => "Домен и поддомены",
        RouteMatch.Address => "IP-адрес",
        RouteMatch.Subnet => "Подсеть",
        RouteMatch.Port => "Порт назначения",
        RouteMatch.GeoIp => "GEOIP",
        RouteMatch.GeoSite => "GEOSITE",
        _ => kind.ToString()
    };

    private static string ProtocolLabel(RouteProtocol protocol) => protocol switch
    {
        RouteProtocol.Tcp => "TCP",
        RouteProtocol.Udp => "UDP",
        _ => "TCP и UDP"
    };

    private string TargetLabel(RouteTarget target) => target.Kind switch
    {
        RouteKind.Main => ConnectionServerLabel,
        RouteKind.Direct => "Напрямую",
        RouteKind.Reject => "Блокировка",
        RouteKind.Server => ServerLabel(target.Server!),
        RouteKind.SubscriptionGroup => IsUnavailableSitesGroup(target.Category!.Name) ? "Недоступные сайты" : target.Category.Name,
        _ => "—"
    };

    private string ServerLabel(RouteServer server)
    {
        var source = profiles.FirstOrDefault(profile => RouteServer.SourceKey(profile) == server.Source);
        return source?.Nodes.Any(node => node.Name == server.Name && RoutingConfig.IsRoutable(node)) == true
            ? server.Name : $"{server.Name}  ·  сервер не найден";
    }

    private ComboBox TargetField(Editor editor, RouteTarget selected)
    {
        if (selected.Kind == RouteKind.Main)
            selected = NewRuleTarget();
        var choices = new List<Choice<RouteTarget>>
        {
            new(new(RouteKind.Direct), "Напрямую"),
            new(new(RouteKind.Reject), "Блокировка")
        };
        choices.AddRange(ServerRouteOptions());
        if (RoutingSource is { } source)
            foreach (var item in RoutingSubscriptionGroups.Items.OfType<SubscriptionGroupRow>().Where(item => IsUnavailableSitesGroup(item.Name)))
                choices.Add(new(new(RouteKind.SubscriptionGroup,
                    Category: new(RouteServer.SourceKey(source), item.Name)), "Недоступные сайты"));
        if (!choices.Any(choice => choice.Value == selected))
            choices.Add(new(selected, selected.Kind == RouteKind.SubscriptionGroup ? "Текущий маршрут" : TargetLabel(selected)));
        return SelectField(editor, "Направление трафика", choices, selected, withFlags: true);
    }

    private string ConnectionServerLabel => (activeServer ?? SelectedServer)?.Node.Name ?? "Сервер не выбран";

    private RouteTarget NewRuleTarget() => (activeServer ?? SelectedServer) is { } server
        ? new(RouteKind.Server, RouteServer.From(server)) : new(RouteKind.Reject);

    private IEnumerable<Choice<RouteTarget>> ServerRouteOptions() =>
        profiles.SelectMany(source => source.Nodes.Where(RoutingConfig.IsRoutable)
            .Select(node => new RouteServer(RouteServer.SourceKey(source), node.Name)))
            .Distinct()
            .GroupBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
            .SelectMany(servers => servers.Select((server, index) => new Choice<RouteTarget>(new(RouteKind.Server, server),
                servers.Count() == 1 ? server.Name : $"{server.Name} · {index + 1}")));

    private static bool IsUnavailableSitesGroup(string name) =>
        Regex.IsMatch(name.Trim(), @"\A[^\p{L}\p{N}]*Недоступные\s+сайты\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record FallbackChoice(RouteTarget Target, bool UseSubscription);

    private FallbackChoice? ChooseRoutingFallback(RoutingProfile profile)
    {
        var editor = NewEditor(this, "Маршрут для остального трафика");
        var fromSource = new CheckBox
        {
            Content = "Как в подписке, если маршрут задан",
            Style = (Style)FindResource("SettingToggle"),
            IsChecked = profile.UseSubscriptionRules && profile.UseSubscriptionFallback
        };
        editor.Body.Children.Add(fromSource);
        var direction = TargetField(editor, profile.Default);
        direction.IsEnabled = fromSource.IsChecked != true;
        fromSource.Checked += (_, _) => direction.IsEnabled = false;
        fromSource.Unchecked += (_, _) => direction.IsEnabled = true;
        return Finish(editor, "Сохранить", () => fromSource.IsChecked == true
            ? new FallbackChoice(new(RouteKind.Main), true)
            : new FallbackChoice(Read<RouteTarget>(direction), false));
    }

    private RouteRule? EditRuleDialog(RouteRule rule,
        IReadOnlyList<(RouteMatch Match, string Text)>? fromConnection = null)
    {
        var editor = NewEditor(this, "Правило маршрутизации");
        var type = SelectField(editor, "Условие", Enum.GetValues<RouteMatch>().Select(kind => new Choice<RouteMatch>(kind, MatchLabel(kind))), rule.Match);
        var value = TextField(editor, "Значение", rule.Value);
        var hint = new TextBlock { Foreground = BrushFor("Muted"), TextWrapping = TextWrapping.Wrap };
        editor.Body.Children.Add(hint);
        var appButtons = ActionRow(editor.Body);
        var running = ActionButton(appButtons, "Запущенные…");
        var installed = ActionButton(appButtons, "Установленные…");
        ActionButton(appButtons, "EXE-файл…").Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Приложения (*.exe)|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(editor.Window) == true)
                value.Text = dialog.FileName;
        };
        running.Click += (_, _) => PickApplication(editor.Window, true, value);
        installed.Click += (_, _) => PickApplication(editor.Window, false, value);
        var protocol = SelectField(editor, "Протокол", Enum.GetValues<RouteProtocol>().Select(item =>
            new Choice<RouteProtocol>(item, ProtocolLabel(item))), rule.Protocol);
        var target = TargetField(editor, rule.Target);

        type.SelectionChanged += (_, _) =>
        {
            if (fromConnection?.FirstOrDefault(item => item.Match == Read<RouteMatch>(type)).Text is { } suggestion)
                value.Text = suggestion;
            UpdateHint();
        };
        UpdateHint();
        return Finish(editor, "Сохранить", () =>
        {
            var kind = Read<RouteMatch>(type);
            return new RouteRule(kind, RoutingProfiles.Normalize(kind, value.Text), Read<RouteProtocol>(protocol),
                Read<RouteTarget>(target), rule.Enabled);
        });

        void UpdateHint()
        {
            var kind = Read<RouteMatch>(type);
            appButtons.Visibility = kind == RouteMatch.Application ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = kind switch
            {
                RouteMatch.Application => "Полный путь к EXE-файлу.",
                RouteMatch.Domain => "Например, www.example.org.",
                RouteMatch.DomainSuffix => "Например, example.org для самого домена и его поддоменов.",
                RouteMatch.Address => "Например, 203.0.113.8 или 2001:db8::1.",
                RouteMatch.Subnet => "Например, 192.0.2.0/24 или 2001:db8::/32.",
                RouteMatch.Port => "Один порт или диапазон, например 443 или 8000-8100.",
                RouteMatch.GeoIp => "Двухбуквенный код страны, например ru, или private.",
                RouteMatch.GeoSite => "Категория GeoSite, например category-ads-all.",
                _ => ""
            };
        }
    }

    private string? EditRoutingName(string title, string initial)
    {
        var editor = NewEditor(this, title);
        var name = TextField(editor, "Название профиля", initial);
        return Finish(editor, "Сохранить", () => ReadName(name));
    }

    private static string ReadName(TextBox box)
    {
        string name = box.Text.Trim();
        if (name.Length is 0 or > 100 || name.Any(char.IsControl))
            throw new IOException("Введите название длиной до 100 символов.");
        return name;
    }

    private void PickApplication(Window owner, bool running, TextBox value)
    {
        var editor = NewEditor(owner, running ? "Запущенные приложения" : "Установленные приложения");
        var filter = TextField(editor, "Поиск по имени или пути", "");
        var list = ListField(editor, "Приложения", 260);
        using var iconLoading = new CancellationTokenSource();
        CancellationToken iconToken = iconLoading.Token;
        editor.Window.Closed += (_, _) => iconLoading.Cancel();
        var status = new TextBlock { Foreground = BrushFor("Muted"), TextWrapping = TextWrapping.Wrap, Text = "Загружаем приложения…" };
        editor.Body.Children.Add(status);
        ApplicationPickerEntry[] applications = [];
        editor.Window.Loaded += async (_, _) =>
        {
            try
            {
                var found = await Task.Run(() => running ? ApplicationInventory.Running() : ApplicationInventory.Installed());
                if (!editor.Window.IsVisible)
                    return;
                applications = found.Select(path => new ApplicationPickerEntry(path)).ToArray();
                status.Text = applications.Length == 0 ? "Доступные EXE не найдены. Можно выбрать файл вручную." : "";
                Filter();
            }
            catch (Exception)
            {
                status.Text = "Не удалось прочитать список. Можно выбрать EXE-файл вручную.";
                return;
            }

            try { await ApplicationPickerEntry.LoadIconsAsync(applications, iconToken); }
            catch (OperationCanceledException) when (iconToken.IsCancellationRequested) { }
        };
        filter.TextChanged += (_, _) => Filter();
        if (Finish(editor, "Выбрать", () => list.SelectedItem is ApplicationPickerEntry choice
                ? choice.Path : throw new IOException("Выберите приложение.")) is { } path)
            value.Text = path;

        void Filter()
        {
            var previous = list.SelectedItem;
            string query = filter.Text;
            list.ItemsSource = applications.Where(item => item.Path.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            list.SelectedItem = previous;
        }
    }

    private Editor NewEditor(Window owner, string title)
    {
        var body = new StackPanel { Margin = new Thickness(23, 18, 23, 22) };
        var window = new Window
        {
            Title = title, Owner = owner, Width = 565, MaxHeight = Math.Min(760, SystemParameters.WorkArea.Height),
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = BrushFor("Panel"),
            Foreground = BrushFor("Ink"), FontFamily = FontFamily, FontSize = FontSize, UseLayoutRounding = true,
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body }
        };
        return new(window, body);
    }

    private TextBox TextField(Editor editor, string label, string value)
    {
        AddFieldLabel(editor, label);
        var field = new TextBox { Style = (Style)FindResource(typeof(TextBox)), Text = value };
        AutomationProperties.SetName(field, label);
        editor.Body.Children.Add(field);
        return field;
    }

    private ComboBox SelectField<T>(Editor editor, string label, IEnumerable<Choice<T>> options, T selected, bool withFlags = false)
    {
        AddFieldLabel(editor, label);
        var choices = options.ToArray();
        var field = new ComboBox
        {
            Style = (Style)FindResource("RoutingSelect"), ItemsSource = choices,
            SelectedItem = choices.FirstOrDefault(choice => EqualityComparer<T>.Default.Equals(choice.Value, selected))
        };
        if (withFlags)
            field.ItemTemplate = (DataTemplate)FindResource("FlaggedChoice");
        else
            field.DisplayMemberPath = "Label";
        AutomationProperties.SetName(field, label);
        editor.Body.Children.Add(field);
        return field;
    }

    private ListBox ListField(Editor editor, string label, double height)
    {
        AddFieldLabel(editor, label);
        var field = new ListBox
        {
            Style = (Style)FindResource("DarkList"), ItemContainerStyle = (Style)FindResource("ChoiceItem"),
            ItemTemplate = (DataTemplate)FindResource("ApplicationPickerRow"), MinHeight = 48, MaxHeight = height
        };
        VirtualizingPanel.SetIsVirtualizing(field, true);
        AutomationProperties.SetName(field, label);
        editor.Body.Children.Add(field);
        return field;
    }

    private void AddFieldLabel(Editor editor, string label) => editor.Body.Children.Add(new TextBlock
    {
        Text = label, Foreground = BrushFor("Muted"), Margin = new Thickness(0, 10, 0, 5)
    });

    private static WrapPanel ActionRow(Panel parent)
    {
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        parent.Children.Add(actions);
        return actions;
    }

    private Button ActionButton(Panel row, string label)
    {
        var button = new Button { Content = label, Style = (Style)FindResource("RoutingAction") };
        row.Children.Add(button);
        return button;
    }

    private T? Finish<T>(Editor editor, string caption, Func<T> result) where T : class
    {
        T? value = null;
        var error = new TextBlock { Foreground = BrushFor("StatusWarning"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        editor.Body.Children.Add(error);
        var actions = ActionRow(editor.Body);
        var accept = ActionButton(actions, caption);
        accept.Style = (Style)FindResource("PrimaryButton");
        accept.IsDefault = true;
        accept.Click += (_, _) =>
        {
            try
            {
                value = result();
                editor.Window.DialogResult = true;
            }
            catch (IOException failure) { error.Text = failure.Message; }
        };
        ActionButton(actions, "Отмена").IsCancel = true;
        return editor.Window.ShowDialog() == true ? value : null;
    }

    private static T Read<T>(ComboBox box) => ((Choice<T>)box.SelectedItem).Value;
    private Brush BrushFor(string name) => (Brush)FindResource(name);
}

