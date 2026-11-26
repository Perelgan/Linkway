using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Path = System.Windows.Shapes.Path;
using VPNDesktop;

internal static class UiSmoke
{
    public static Task RunAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow(preview: true);
                var layout = (FrameworkElement)window.Content;
                var dark = ((SolidColorBrush)window.FindResource("Surface")).Color;
                Render(layout);
                VerifyServerRow(window);
                VerifyRoutingDialogTheme(window);
                VerifyCloseActionTheme(window);
                window.ApplyTheme(light: true);
                var light = ((SolidColorBrush)window.FindResource("Surface")).Color;
                if (dark == light || dark.R >= light.R)
                    throw new InvalidOperationException("Theme resources did not switch.");
                VerifyRoutingDialogTheme(window);
                VerifyCloseActionTheme(window);
                foreach (var (nav, page) in new[]
                {
                    ("ConnectionNav", "ConnectionPage"), ("ProfilesNav", "ProfilesPage"), ("CabinetNav", "CabinetPage"),
                    ("RoutingNav", "RoutingPage"), ("DiagnosticsNav", "DiagnosticsPage"), ("SettingsNav", "SettingsPage")
                })
                {
                    ((RadioButton)window.FindName(nav)).IsChecked = true;
                    if (((FrameworkElement)window.FindName(page)).Visibility != Visibility.Visible)
                        throw new InvalidOperationException($"Navigation did not show {page}.");
                    Render(layout);
                }
                if (window.FindName("CheckUpdatesButton") is not Button { Content: "Проверить обновления" })
                    throw new InvalidOperationException("The update check button is missing.");
                VerifySubscriptionRulesSwitch(window);
                VerifySubscriptionGroupScrolling(window);
                VerifyTrafficRow(window);
                if (window.Icon is null ||
                    window.FindName("ConnectionButtonScale") is not ScaleTransform ||
                    window.FindName("SidebarSpeed") is not TextBlock ||
                    window.FindName("DashboardPing") is not TextBlock { Visibility: Visibility.Collapsed } ||
                    window.FindName("DeveloperModeToggle") is not CheckBox ||
                    window.FindName("CloseActionBox") is not ComboBox ||
                    window.FindName("AdvancedNetworkSettings") is not Expander { IsExpanded: false, Header: "Для продвинутых" } ||
                    window.FindName("CoreBuilds") is not ComboBox)
                    throw new InvalidOperationException("Connection and kernel settings controls do not match the default layout.");
                var diagnostics = (RadioButton)window.FindName("DiagnosticsNav");
                if (((StackPanel)diagnostics.Content).Children.OfType<Path>().Single().Stroke is null)
                    throw new InvalidOperationException("The diagnostic navigation icon has no stroke.");
                Console.WriteLine("PASS WPF pages and routing dialog controls render in both themes");
                window.Close();
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void VerifySubscriptionGroupScrolling(MainWindow window)
    {
        const int groupCount = 18;
        var names = Enumerable.Range(1, groupCount).Select(index => $"Group-{index}").ToArray();
        var nodes = Enumerable.Range(1, 24).Select(index => new Node($"Server-{index}", "Vless")).ToArray();
        string yaml = "proxy-groups:\n" + string.Join('\n', names.Select(name =>
            $"  - {{name: {name}, type: select, include-all: true}}")) +
            "\nrules:\n" + string.Join('\n', names.Select(name => $"  - DOMAIN,{name.ToLowerInvariant()}.test,{name}"));
        var source = new Profile(Guid.NewGuid(), "Тест прокрутки", null, yaml, nodes, nodes[0].Name);
        var servers = (ListBox)window.FindName("NodesList");
        servers.ItemsSource = new[] { new ServerChoice(source, nodes[0]) };
        servers.SelectedIndex = 0;
        ((RadioButton)window.FindName("RoutingGroupsTab")).IsChecked = true;

        var layout = (FrameworkElement)window.Content;
        var size = new Size(window.MinWidth, window.MinHeight);
        layout.Measure(size);
        layout.Arrange(new Rect(size));
        layout.UpdateLayout();
        var page = (ScrollViewer)window.FindName("PageScroll");
        var groups = (ListBox)window.FindName("RoutingSubscriptionGroups");
        page.ScrollToBottom();
        layout.UpdateLayout();
        if (groups.Items.Count != groupCount ||
            groups.ItemContainerGenerator.ContainerFromIndex(groupCount - 1) is not ListBoxItem lastGroup)
            throw new InvalidOperationException("The page did not reach the last subscription group.");
        VerifyFullyVisible(lastGroup);

        groups.SelectedIndex = groupCount - 1;
        layout.UpdateLayout();
        page.ScrollToBottom();
        layout.UpdateLayout();
        if (FindBorder(lastGroup, "GroupRoutes")?.Child is not ItemsControl routes ||
            routes.Items.Count != nodes.Length + 2 ||
            routes.ItemContainerGenerator.ContainerFromIndex(routes.Items.Count - 1) is not FrameworkElement lastRoute)
            throw new InvalidOperationException("The expanded group did not render the last route option.");
        VerifyFullyVisible(lastRoute);

        void VerifyFullyVisible(FrameworkElement element)
        {
            double top = element.TranslatePoint(new Point(0, 0), page).Y;
            if (element.ActualHeight <= 0 || top < 0 || top + element.ActualHeight > page.ViewportHeight + 1)
                throw new InvalidOperationException("The last item is clipped at the bottom of the routing page.");
        }
    }

    private static void VerifyServerRow(MainWindow window)
    {
        var list = (ListBox)window.FindName("NodesList");
        var row = (Grid)list.ItemTemplate.LoadContent();
        if (window.FindName("UnavailableSitesServerBox") is not null ||
            row.Children.OfType<Button>().SingleOrDefault() is not { Content: "Проверить" })
            throw new InvalidOperationException("Server selection must use the row, with a separate action only for the latency check.");
    }

    private static void VerifySubscriptionRulesSwitch(MainWindow window)
    {
        ((RadioButton)window.FindName("RoutingNav")).IsChecked = true;
        var profiles = (ComboBox)window.FindName("RoutingProfileBox");
        var enabled = RoutingProfile.Create("Включены");
        var disabled = RoutingProfile.Create("Выключены") with { UseSubscriptionRules = false };
        profiles.ItemsSource = new[] { enabled, disabled };
        var toggle = (CheckBox)window.FindName("RoutingSubscriptionToggle");
        profiles.SelectedItem = enabled;
        if (toggle.IsChecked != true || toggle.Visibility != Visibility.Visible ||
            (string)toggle.Content != "Правила подписки")
            throw new InvalidOperationException("Routing does not show the subscription rules switch as enabled.");
        profiles.SelectedItem = disabled;
        if (toggle.IsChecked != false)
            throw new InvalidOperationException("The subscription rules switch does not track the selected profile.");
    }

    private static void VerifyTrafficRow(MainWindow window)
    {
        var list = (ListBox)window.FindName("AppTrafficList");
        ((RadioButton)window.FindName("DiagnosticsNav")).IsChecked = true;
        list.ItemsSource = new[] { new TrafficRow("Тестовое приложение", 2048, 1024) };
        Render((FrameworkElement)window.Content);
        var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        var presenter = FindPresenter(item) ?? throw new InvalidOperationException("Traffic row was not rendered.");
        var icon = (Path)list.ItemTemplate.FindName("FallbackAppIcon", presenter);
        var row = (Grid)VisualTreeHelper.GetChild(presenter, 0);
        var labels = ((StackPanel)row.Children[1]).Children.OfType<TextBlock>().ToArray();
        if (icon.Visibility != Visibility.Visible || labels[0].Text != "Тестовое приложение" ||
            !labels[1].Text.Contains("2 КиБ", StringComparison.Ordinal))
            throw new InvalidOperationException("Application traffic lost its icon, label, or formatted traffic totals.");

        static ContentPresenter? FindPresenter(DependencyObject parent)
        {
            if (parent is ContentPresenter presenter)
                return presenter;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (FindPresenter(VisualTreeHelper.GetChild(parent, index)) is { } child)
                    return child;
            return null;
        }
    }

    private static void VerifyCloseActionTheme(MainWindow main)
    {
        var body = new StackPanel();
        var dialog = main.CreateRoutingDialog(null, "Закрытие Linkway", body);
        try
        {
            var options = new[] { WindowCloseAction.Tray, WindowCloseAction.KeepVpn, WindowCloseAction.Disconnect }
                .Select(main.CreateCloseActionOption).ToArray();
            foreach (var option in options)
                body.Children.Add(option);
            options[1].IsChecked = true;
            Render((FrameworkElement)dialog.Content);
            var ink = ((SolidColorBrush)main.FindResource("Ink")).Color;
            foreach (var option in options)
            {
                var mark = (FrameworkElement)option.Template.FindName("OptionMark", option);
                if (((SolidColorBrush)option.Foreground).Color != ink ||
                    (mark.Visibility == Visibility.Visible) != (option == options[1]))
                    throw new InvalidOperationException("The close dialog lost its themed radio labels or selected marker.");
            }
        }
        finally { dialog.Close(); }
    }

    private static void VerifyRoutingDialogTheme(MainWindow main)
    {
        var body = new StackPanel();
        var dialog = main.CreateRoutingDialog(null, "Правило маршрутизации", body);
        try
        {
            var input = new TextBox { Text = "example.org" };
            var select = new ComboBox { Style = (Style)main.FindResource("RoutingSelect") };
            select.Items.Add("Домен");
            select.SelectedIndex = 0;
            var save = new Button { Style = (Style)main.FindResource("PrimaryButton"), Content = "Сохранить" };
            var cancel = new Button { Style = (Style)main.FindResource("RoutingAction"), Content = "Отмена" };
            foreach (var control in new Control[] { input, select, save, cancel })
                body.Children.Add(control);

            Render((FrameworkElement)dialog.Content);
            var dropdownSurface = FindBorder(select, "Field");
            CheckColor(input.Background, "Field");
            CheckColor(input.BorderBrush, "Line");
            CheckColor((dropdownSurface ?? throw new InvalidOperationException("Dropdown template was not rendered.")).Background, "Field");
            CheckColor(dropdownSurface?.BorderBrush, "Line");
            CheckColor(save.Background, "Accent");
            CheckColor(save.Foreground, "PrimaryInk");
            CheckColor(cancel.Background, "Hover");
        }
        finally { dialog.Close(); }

        void CheckColor(Brush? actual, string key)
        {
            if (actual is not SolidColorBrush brush || brush.Color != ((SolidColorBrush)main.FindResource(key)).Color)
                throw new InvalidOperationException($"Routing dialog did not resolve brush {key} from the active theme.");
        }
    }

    private static Border? FindBorder(DependencyObject parent, string name)
    {
        if (parent is Border border && border.Name == name)
            return border;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindBorder(VisualTreeHelper.GetChild(parent, index), name) is { } child)
                return child;
        return null;
    }

    private static void Render(FrameworkElement layout)
    {
        layout.Measure(new Size(1120, 765));
        layout.Arrange(new Rect(0, 0, 1120, 765));
        layout.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1120, 765, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(layout);
    }
}
