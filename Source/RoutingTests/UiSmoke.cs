using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
                window.ApplyTheme(light: true);
                var light = ((SolidColorBrush)window.FindResource("Surface")).Color;
                if (dark == light || dark.R >= light.R)
                    throw new InvalidOperationException("Theme resources did not switch.");
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
                Console.WriteLine("PASS WPF dark and light themes render, and all six navigation pages open");
                window.Close();
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
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
