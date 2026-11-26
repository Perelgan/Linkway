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
                VerifyRoutingDialogTheme(window);
                window.ApplyTheme(light: true);
                var light = ((SolidColorBrush)window.FindResource("Surface")).Color;
                if (dark == light || dark.R >= light.R)
                    throw new InvalidOperationException("Theme resources did not switch.");
                VerifyRoutingDialogTheme(window);
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
