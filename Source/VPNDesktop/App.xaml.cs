using System.Security.Principal;
using System.Windows;

namespace VPNDesktop;

public partial class App : Application
{
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        using var identity = WindowsIdentity.GetCurrent();
        instance = new Mutex(true, $@"Local\VPNDesktop-{identity.User!.Value}", out bool first);
        if (!first)
        {
            MessageBox.Show("Linkway уже запущен.", "Linkway");
            Shutdown();
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}

