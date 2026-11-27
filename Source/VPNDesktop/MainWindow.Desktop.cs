using Microsoft.Win32;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;
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
    private DateTimeOffset? startupRetryUntil;
    private DateTimeOffset nextStartupAttempt;
    private DateTimeOffset nextHealthCheck;
    private int failedHealthChecks, attempts;
    private ServerChoice? recoveryServer;
    private bool vpnStartupEnabled;

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
        try { StartupVpnToggle.IsChecked = vpnStartupEnabled = WindowsVpnStartup.IsEnabled(); }
        catch (Exception error) when (error is COMException or IOException or SecurityException or UnauthorizedAccessException)
        {
            StartupVpnToggle.IsEnabled = false;
            StartupVpnStatus.Text = "Не удалось прочитать настройки Планировщика заданий.";
        }
        AutoConnectToggle.IsChecked = clientSettings.AutoConnect;
        AutoRecoverToggle.IsChecked = clientSettings.AutoRecover;
        TrayToggle.IsChecked = clientSettings.MinimizeToTray;
        CloseActionBox.ItemsSource = CloseActionLabels;
        CloseActionBox.SelectedIndex = (int)SavedCloseAction;
        HistoryToggle.IsChecked = clientSettings.SaveHistory;
    }

    private async void SaveDesktopSettings(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var updated = clientSettings with
        {
            AutoConnect = AutoConnectToggle.IsChecked == true,
            AutoRecover = AutoRecoverToggle.IsChecked == true,
            MinimizeToTray = TrayToggle.IsChecked == true,
            SaveHistory = HistoryToggle.IsChecked == true,
            CloseAction = (WindowCloseAction)Math.Max(0, CloseActionBox.SelectedIndex)
        };
        using var startup = StartupToggle.IsEnabled ? Registry.CurrentUser.CreateSubKey(StartupKey, writable: true) : null;
        string? previous = startup?.GetValue(StartupName) as string;
        bool enableVpnStartup = StartupVpnToggle.IsEnabled ? StartupVpnToggle.IsChecked == true : vpnStartupEnabled;
        try
        {
            if (startup is not null)
            {
                if (StartupToggle.IsChecked == true && !enableVpnStartup)
                    startup.SetValue(StartupName, StartupCommand);
                else if (string.Equals(previous, StartupCommand, StringComparison.OrdinalIgnoreCase))
                    startup.DeleteValue(StartupName, false);
            }
            updated.Save();
            if (enableVpnStartup != vpnStartupEnabled)
            {
                try { await WindowsVpnStartup.SetAsync(enableVpnStartup, lifetime.Token); }
                catch (Exception error) when (error is COMException or IOException or System.ComponentModel.Win32Exception)
                {
                    clientSettings.Save();
                    StartupVpnToggle.IsChecked = vpnStartupEnabled;
                    StartupVpnStatus.Text = error is IOException ? error.Message : "Windows отклонила настройку автозапуска VPN.";
                    throw;
                }
            }
        }
        catch
        {
            if (previous is null)
                startup?.DeleteValue(StartupName, false);
            else
                startup?.SetValue(StartupName, previous);
            throw;
        }
        vpnStartupEnabled = enableVpnStartup;
        if (enableVpnStartup)
            StartupToggle.IsChecked = false;
        else
            startupRetryUntil = null;
        if (StartupVpnToggle.IsEnabled)
            StartupVpnStatus.Text = enableVpnStartup ? "VPN будет подключаться при входе в Windows."
                : "Подключение VPN при входе в Windows выключено.";
        clientSettings = updated;
        if (!updated.AutoRecover)
            retryAt = null;
        if (!updated.SaveHistory)
            ClearSessionHistory();
        Message.Text = "Настройки запуска сохранены.";
    });

    private void InitializeDesktopIntegration()
    {
        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Открыть Linkway", null, (_, _) => ShowFromTray());
        trayConnection = (Forms.ToolStripMenuItem)trayMenu.Items.Add("Подключить", null, (_, _) => ToggleConnection(this, new()));
        trayMenu.Items.Add("Диагностика", null, (_, _) => { ShowFromTray(); DiagnosticsNav.IsChecked = true; });
        trayMenu.Items.Add("Выход", null, (_, _) => { ShowFromTray(); exitRequested = true; Close(); });
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
        tray.Text = session is not null ? "Linkway · Подключено" : backgroundUnavailable ? "Linkway · Фоновый VPN"
            : killSwitch ? "Linkway · Сеть заблокирована" : "Linkway · Отключено";
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
        {
            var due = DateTimeOffset.UtcNow.AddSeconds(4);
            if (nextHealthCheck > due)
                nextHealthCheck = due;
        }
    });

    private void BeginRecovery()
    {
        ResetConnectionHealth();
        NotifyTray("Подключение установлено.");
    }

    private void ResetConnectionHealth()
    {
        nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(clientSettings.DeveloperMode ? 0 : 20);
        retryAt = null;
        startupRetryUntil = null;
        attempts = failedHealthChecks = 0;
        runtimeLatency = null;
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
        if (closing || busy || checkingRecovery)
            return;
        if (startupRetryUntil is { } until && session is null && DateTimeOffset.UtcNow >= nextStartupAttempt)
        {
            if (DateTimeOffset.UtcNow >= until || backgroundUnavailable)
            {
                startupRetryUntil = null;
                if (!backgroundUnavailable)
                    NotifyTray("Автоподключение VPN не удалось. Откройте Linkway для проверки.");
                return;
            }
            nextStartupAttempt = DateTimeOffset.UtcNow.AddSeconds(15);
            if (SelectedServer is null && cabinetSession is not null)
                await RunCabinetAsync(() => SyncCabinetAsync());
            if (SelectedServer is not null)
                await RunAsync(ConnectSelectedAsync);
            return;
        }
        if (!clientSettings.AutoRecover && !clientSettings.DeveloperMode)
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

        if (session is not { } core || DateTimeOffset.UtcNow < nextHealthCheck)
            return;
        var server = activeServer;
        nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(30);
        int? delay;
        checkingRecovery = true;
        try { delay = await core.CheckMainRouteAsync(lifetime.Token); }
        catch (Exception error) when (IsHostCommunicationError(error) || error is HttpRequestException or ObjectDisposedException)
        {
            delay = null;
        }
        finally { checkingRecovery = false; }

        if (session != core || activeServer != server || closing || busy)
            return;
        runtimeLatency = delay;
        UpdateDashboardClock(activeServer);
        if (delay.HasValue)
        {
            failedHealthChecks = 0;
            return;
        }
        if (!clientSettings.AutoRecover)
            return;
        if (++failedHealthChecks < 2)
        {
            var due = DateTimeOffset.UtcNow.AddSeconds(5);
            if (nextHealthCheck > due)
                nextHealthCheck = due;
            return;
        }
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
