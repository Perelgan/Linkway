using Microsoft.Win32;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace VPNDesktop;

public partial class MainWindow
{
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupName = "Linkway";
    private const int Hotkey = 0x4C57;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private Forms.ContextMenuStrip? trayMenu;
    private Forms.ToolStripMenuItem? trayConnection;
    private HwndSource? hotkeySource;
    private bool hotkeyRegistered, exitRequested, checkingRecovery;
    private DateTimeOffset? retryAt;
    private DateTimeOffset nextHealthCheck;
    private int failedHealthChecks, attempts;
    private ServerChoice? recoveryServer;

    private static string StartupCommand => $"\"{Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "VPNDesktop.exe"))}\"";

    private void LoadDesktopSettings()
    {
        try
        {
            using var startup = Registry.CurrentUser.OpenSubKey(StartupKey);
            StartupToggle.IsChecked = string.Equals(startup?.GetValue(StartupName) as string, StartupCommand, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
        {
            StartupToggle.IsEnabled = false;
        }
        AutoConnectToggle.IsChecked = clientSettings.AutoConnect;
        AutoRecoverToggle.IsChecked = clientSettings.AutoRecover;
        TrayToggle.IsChecked = clientSettings.MinimizeToTray;
        HistoryToggle.IsChecked = clientSettings.SaveHistory;
    }

    private async void SaveDesktopSettings(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        var updated = clientSettings with
        {
            AutoConnect = AutoConnectToggle.IsChecked == true,
            AutoRecover = AutoRecoverToggle.IsChecked == true,
            MinimizeToTray = TrayToggle.IsChecked == true,
            SaveHistory = HistoryToggle.IsChecked == true
        };
        if (StartupToggle.IsEnabled)
        {
            using var startup = Registry.CurrentUser.CreateSubKey(StartupKey, writable: true);
            string? previous = startup.GetValue(StartupName) as string;
            try
            {
                if (StartupToggle.IsChecked == true)
                    startup.SetValue(StartupName, StartupCommand);
                else if (string.Equals(previous, StartupCommand, StringComparison.OrdinalIgnoreCase))
                    startup.DeleteValue(StartupName, false);
                updated.Save();
            }
            catch
            {
                if (previous is null)
                    startup.DeleteValue(StartupName, false);
                else
                    startup.SetValue(StartupName, previous);
                throw;
            }
        }
        else
            updated.Save();
        clientSettings = updated;
        if (!updated.AutoRecover)
            retryAt = null;
        if (!updated.SaveHistory)
            ClearSessionHistory();
        Message.Text = "Настройки запуска сохранены.";
        return Task.CompletedTask;
    });

    private void InitializeDesktopIntegration()
    {
        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Открыть Linkway", null, (_, _) => ShowFromTray());
        trayConnection = (Forms.ToolStripMenuItem)trayMenu.Items.Add("Подключить", null, (_, _) => ToggleConnection(this, new()));
        trayMenu.Items.Add("Диагностика", null, (_, _) => { ShowFromTray(); DiagnosticsNav.IsChecked = true; });
        trayMenu.Items.Add("Выход", null, (_, _) => { exitRequested = true; Close(); });
        using var iconStream = Application.GetResourceStream(new Uri("/VPNDesktop;component/Assets/Linkway.ico",
            UriKind.Relative)).Stream;
        using var resourceIcon = new System.Drawing.Icon(iconStream);
        trayIcon = (System.Drawing.Icon)resourceIcon.Clone();
        tray = new Forms.NotifyIcon
        {
            Icon = trayIcon, Text = "Linkway", ContextMenuStrip = trayMenu, Visible = true
        };
        tray.DoubleClick += (_, _) => ShowFromTray();
        UpdateTray();
        SourceInitialized += (_, _) =>
        {
            hotkeySource = PresentationSource.FromVisual(this) as HwndSource;
            if (hotkeySource is not null)
            {
                hotkeySource.AddHook(OnHotkey);
                hotkeyRegistered = RegisterHotKey(hotkeySource.Handle, Hotkey, 0x4003, 0x4C);
                if (!hotkeyRegistered)
                    RecordEvent("Сочетание Ctrl+Alt+L уже используется");
            }
        };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && clientSettings.MinimizeToTray)
                Hide();
        };
        SystemEvents.PowerModeChanged += OnPowerChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        RecordEvent("Linkway запущен");
        LoadSessionHistory();
    }

    private void DisposeDesktopIntegration()
    {
        SystemEvents.PowerModeChanged -= OnPowerChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        if (hotkeySource is { } source)
        {
            if (hotkeyRegistered)
                UnregisterHotKey(source.Handle, Hotkey);
            source.RemoveHook(OnHotkey);
        }
        tray?.Dispose();
        trayIcon?.Dispose();
        trayMenu?.Dispose();
        tray = null;
    }

    private void UpdateTray()
    {
        if (tray is null || trayConnection is null)
            return;
        tray.Text = session is not null ? "Linkway · Подключено" : killSwitch is not null ? "Linkway · Сеть заблокирована" : "Linkway · Отключено";
        trayConnection.Text = session is not null ? "Отключить" : "Подключить";
        trayConnection.Enabled = ConnectButton.IsEnabled;
    }

    private void NotifyTray(string text)
    {
        if (tray is not null && !IsActive)
            tray.ShowBalloonTip(3000, "Linkway", text, Forms.ToolTipIcon.Info);
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private IntPtr OnHotkey(IntPtr handle, int message, IntPtr key, IntPtr data, ref bool handled)
    {
        if (message == 0x0312 && key == (IntPtr)Hotkey)
        {
            ShowFromTray();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnPowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            QueueHealthCheck();
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => QueueHealthCheck();

    private void QueueHealthCheck() => Dispatcher.BeginInvoke(() =>
    {
        if (!closing && clientSettings.AutoRecover && session is not null)
            nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(4);
    });

    private void BeginRecovery()
    {
        nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(clientSettings.DeveloperMode ? 0 : 20);
        retryAt = null;
        attempts = failedHealthChecks = 0;
        NotifyTray("Подключение установлено.");
    }

    private void ScheduleRecovery()
    {
        if (!clientSettings.AutoRecover || closing || activeServer is null)
            return;
        recoveryServer = activeServer;
        retryAt = DateTimeOffset.UtcNow.AddSeconds(4);
    }

    private async Task CheckRecoveryAsync()
    {
        if (closing || busy || checkingRecovery || (!clientSettings.AutoRecover && !clientSettings.DeveloperMode))
            return;
        if (clientSettings.AutoRecover && session is null && retryAt is { } retry && retry <= DateTimeOffset.UtcNow)
        {
            if (recoveryServer is not { } previous || NodesList.Items.OfType<ServerChoice>().FirstOrDefault(server =>
                RouteServer.From(server) == RouteServer.From(previous)) is not { } target)
            {
                retryAt = null;
                return;
            }
            NodesList.SelectedItem = target;
            retryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, 5 * ++attempts));
            await RunAsync(ConnectSelectedAsync);
            return;
        }

        if (session is not { Exited: false } core || DateTimeOffset.UtcNow < nextHealthCheck)
            return;
        nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(30);
        int? delay;
        checkingRecovery = true;
        try { delay = await core.CheckMainRouteAsync(lifetime.Token); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException)
        {
            delay = null;
        }
        finally { checkingRecovery = false; }

        if (session != core || closing || busy)
            return;
        runtimeLatency = delay;
        UpdateDashboardClock(activeServer);
        if (delay.HasValue)
        {
            failedHealthChecks = 0;
            return;
        }
        if (!clientSettings.AutoRecover || ++failedHealthChecks < 2)
            return;
        await RunAsync(async () =>
        {
            ScheduleRecovery();
            await StopAsync(preserveKillSwitch: true);
            NotifyTray("Соединение потеряно. Linkway попробует переподключиться.");
        });
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
