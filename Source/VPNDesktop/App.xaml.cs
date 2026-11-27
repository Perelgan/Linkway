using System.Security.Principal;
using System.Windows;

namespace VPNDesktop;

public partial class App : Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool windowsStartup = e.Args is [WindowsVpnStartup.LaunchArgument];
        using var identity = WindowsIdentity.GetCurrent();
        bool first;
        try { instance = new Mutex(true, $@"Local\VPNDesktop-{identity.User!.Value}", out first); }
        catch (UnauthorizedAccessException) { first = false; }
        if (!first)
        {
            if (!windowsStartup)
                MessageBox.Show("Linkway уже запущен. Откройте его из области уведомлений Windows.", "Linkway");
            Shutdown();
            return;
        }

        var window = new MainWindow(preview: false, windowsStartup: windowsStartup);
        MainWindow = window;
        if (windowsStartup)
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Opacity = 0;
            window.Loaded += HideStartupWindow;
        }
        window.Show();

        void HideStartupWindow(object sender, RoutedEventArgs args)
        {
            window.Loaded -= HideStartupWindow;
            window.Hide();
            window.Opacity = 1;
            window.ShowActivated = true;
            window.ShowInTaskbar = true;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}

