using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace VPNDesktop;

public partial class MainWindow
{
    // Winsock uses option 31 for both IP_UNICAST_IF and IPV6_UNICAST_IF.
    private const SocketOptionName UnicastInterface = (SocketOptionName)31;
    private static readonly string SessionHistoryPath = Path.Combine(ProfileStore.DirectoryPath, "session-history.bin");
    private readonly Queue<EventEntry> journal = new();
    private readonly List<SessionEntry> history = [];
    private readonly Dictionary<string, (long Download, long Upload)> observed = [];
    private readonly Dictionary<string, TrafficRow> appTraffic = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrafficRow> serverTraffic = [];
    private readonly Dictionary<string, TrafficApplication> trafficApplications = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<TrafficRow> appTrafficRows = [];
    private readonly ObservableCollection<TrafficRow> serverTrafficRows = [];
    private DateTimeOffset nextTrafficSample;
    private DateTimeOffset sessionStarted;
    private (long Download, long Upload) currentTraffic;
    private DiagnosticSnapshot? diagnostics;

    private sealed record EventEntry(DateTimeOffset Time, string Event);
    private sealed record SessionEntry(DateTimeOffset Start, int Seconds, string Mode, long Download, long Upload);

    private void RecordEvent(string text)
    {
        if (journal.Count == 100)
            journal.Dequeue();
        journal.Enqueue(new(DateTimeOffset.Now, text));
        if (DiagnosticsNav.IsChecked == true)
            RefreshJournal();
    }

    private void RefreshJournal() => DiagnosticJournal.ItemsSource = journal.Reverse()
        .Select(entry => $"{entry.Time:HH:mm:ss}   {entry.Event}").ToArray();

    private void ResetSessionTraffic()
    {
        sessionStarted = session?.Started ?? DateTimeOffset.Now;
        currentTraffic = default;
        observed.Clear();
        appTraffic.Clear();
        serverTraffic.Clear();
    }

    private async Task AccumulateTrafficAsync(VpnSessionClient core, long download, long upload, ActiveConnection[] connections)
    {
        var newApplications = connections.Where(connection => connection.Download > 0 || connection.Upload > 0)
            .DistinctBy(ApplicationLookupKey, StringComparer.OrdinalIgnoreCase)
            .Where(connection => !trafficApplications.ContainsKey(ApplicationLookupKey(connection)))
            .Take(Math.Max(0, 512 - trafficApplications.Count)).ToArray();
        if (newApplications.Length > 0)
        {
            var resolved = await Task.Run(() => newApplications.Select(connection =>
                TrafficApplication.Resolve(connection.Process, connection.Path)).ToArray(), lifetime.Token);
            if (session != core || closing)
                return;
            for (int index = 0; index < newApplications.Length; index++)
                trafficApplications.Add(ApplicationLookupKey(newApplications[index]), resolved[index]);
        }

        currentTraffic = (download, upload);
        var alive = connections.Select(connection => connection.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string closed in observed.Keys.Where(id => !alive.Contains(id)).ToArray())
            observed.Remove(closed);
        foreach (var connection in connections)
        {
            if (connection.Id.Length == 0 || observed.Count >= 2048 && !observed.ContainsKey(connection.Id))
                continue;
            var previous = observed.GetValueOrDefault(connection.Id);
            observed[connection.Id] = (connection.Download, connection.Upload);
            long down = Math.Max(0, connection.Download - previous.Download);
            long up = Math.Max(0, connection.Upload - previous.Upload);
            if (down == 0 && up == 0)
                continue;
            var app = trafficApplications.GetValueOrDefault(ApplicationLookupKey(connection));
            Add(appTraffic, app?.Key ?? "other", app?.Name ?? "Остальные приложения", app?.Icon, down, up);
            string outbound = connection.Outbound;
            string label = outbound switch
            {
                "" => "Не определено",
                "DIRECT" => "Напрямую",
                "REJECT" or "REJECT-DROP" => "Заблокировано",
                _ => outbound
            };
            Add(serverTraffic, outbound, label, null, down, up);
        }
        if (IsVisible && DiagnosticsNav.IsChecked == true)
            RefreshTraffic();

        static void Add(Dictionary<string, TrafficRow> totals, string key, string name,
            System.Windows.Media.Imaging.BitmapSource? icon, long down, long up)
        {
            if (totals.Count >= 256 && !totals.ContainsKey(key))
            {
                key = "Остальные";
                name = "Остальные";
                icon = null;
            }
            if (totals.TryGetValue(key, out var previous))
                totals[key] = previous with { Download = previous.Download + down, Upload = previous.Upload + up, Icon = previous.Icon ?? icon };
            else
                totals[key] = new(name, down, up, icon);
        }
    }

    private static string ApplicationLookupKey(ActiveConnection connection) =>
        connection.Path.Length != 0 ? "path:" + connection.Path : "name:" + connection.Process;

    private void RefreshTraffic()
    {
        var summary = new TrafficRow("Трафик за сеанс", currentTraffic.Download, currentTraffic.Upload);
        SessionTraffic.Text = summary.Amount;
        SessionTraffic.ToolTip = summary.Details;
        AppTrafficList.ItemsSource ??= appTrafficRows;
        ServerTrafficList.ItemsSource ??= serverTrafficRows;
        UpdateRows(appTrafficRows, appTraffic.Values);
        UpdateRows(serverTrafficRows, serverTraffic.Values);
        AppTrafficEmpty.Visibility = appTrafficRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ServerTrafficEmpty.Visibility = serverTrafficRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        static void UpdateRows(ObservableCollection<TrafficRow> rows, IEnumerable<TrafficRow> values)
        {
            int index = 0;
            foreach (var row in values.OrderByDescending(item => item.Download + item.Upload).ThenBy(item => item.Name))
            {
                if (index == rows.Count)
                    rows.Add(row);
                else if (rows[index] != row)
                    rows[index] = row;
                index++;
            }
            while (rows.Count > index)
                rows.RemoveAt(rows.Count - 1);
        }
    }

    private void RefreshStatistics()
    {
        RefreshTraffic();
        RefreshJournal();
        HistoryStatus.Text = clientSettings.SaveHistory ? $"Сеансов сохранено: {history.Count}" : "Хранение истории выключено.";
        HistoryList.ItemsSource = history.AsEnumerable().Reverse().Select(entry =>
            $"{entry.Start:g}   ·   {entry.Mode}   ·   {TimeSpan.FromSeconds(entry.Seconds):g}   ·   ↓ {entry.Download:N0} Б   ↑ {entry.Upload:N0} Б").ToArray();
    }

    private void LoadSessionHistory()
    {
        if (!clientSettings.SaveHistory || !File.Exists(SessionHistoryPath))
            return;
        try
        {
            if (new FileInfo(SessionHistoryPath).Length > 128 * 1024)
                throw new IOException();
            byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(SessionHistoryPath), null, DataProtectionScope.CurrentUser);
            try { history.AddRange((JsonSerializer.Deserialize<SessionEntry[]>(clear) ?? []).TakeLast(100)); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception error) when (error is IOException or CryptographicException or JsonException or UnauthorizedAccessException)
        {
            RecordEvent("Не удалось прочитать историю подключений");
        }
    }

    private void ClearSessionHistory()
    {
        File.Delete(SessionHistoryPath);
        File.Delete(SessionHistoryPath + ".new");
        history.Clear();
    }

    private void RecordSessionEnd()
    {
        if (!clientSettings.SaveHistory)
            return;
        history.Add(new(sessionStarted, (int)Math.Min(int.MaxValue, SessionElapsed.TotalSeconds),
            TunMode.IsChecked == true ? "TUN" : "PROXY", currentTraffic.Download, currentTraffic.Upload));
        if (history.Count > 100)
            history.RemoveAt(0);
        try
        {
            byte[] clear = JsonSerializer.SerializeToUtf8Bytes(history);
            try
            {
                File.WriteAllBytes(SessionHistoryPath + ".new", ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser));
                File.Move(SessionHistoryPath + ".new", SessionHistoryPath, true);
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception error) when (error is IOException or CryptographicException or UnauthorizedAccessException)
        {
            RecordEvent("Не удалось сохранить историю подключений");
        }
        if (DiagnosticsNav.IsChecked == true)
            RefreshStatistics();
    }

    private async void RunDiagnostics(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        DiagnosticResults.Text = "Выполняем проверку…";
        var core = session;
        bool tun = TunMode.IsChecked == true;
        bool guard = killSwitch;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(16));
        var checks = await CollectNetworkChecksAsync(core, tun, guard, deadline.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        diagnostics = new(DateTimeOffset.Now, tun ? "TUN" : "PROXY", guard, core is not null,
            core?.CoreVersion ?? MihomoVersions.Active.Version.ToString(3), checks);
        DiagnosticResults.Text = string.Join(Environment.NewLine, checks);
        RecordEvent("Диагностика завершена");
    });

    private static Task<string[]> TestPhysicalBypassAsync(CancellationToken cancellationToken)
    {
        var checks = new List<Task<string>>();
        int interfaceNumber = 0;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(adapter =>
            adapter.OperationalStatus == OperationalStatus.Up && !adapter.Name.Equals(RoutingConfig.TunDevice, StringComparison.OrdinalIgnoreCase) &&
            adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211).Take(4))
        {
            int number = ++interfaceNumber;
            var properties = adapter.GetIPProperties();
            var ipv4 = properties.UnicastAddresses.Select(address => address.Address).FirstOrDefault(address =>
                address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) &&
                address.GetAddressBytes()[0] != 169);
            var ipv6 = properties.UnicastAddresses.Select(address => address.Address).FirstOrDefault(address =>
                address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal &&
                !IPAddress.IsLoopback(address) && (address.GetAddressBytes()[0] & 0xe0) == 0x20);
            if (ipv4 is not null && properties.GetIPv4Properties() is { } ip4)
            {
                checks.Add(TestBoundSocketAsync(ipv4, ip4.Index, new(IPAddress.Parse("1.1.1.1"), 53),
                    $"DNS UDP, интерфейс {number}", true, cancellationToken));
                checks.Add(TestBoundSocketAsync(ipv4, ip4.Index, new(IPAddress.Parse("1.1.1.1"), 53),
                    $"DNS TCP, интерфейс {number}", false, cancellationToken));
            }
            if (ipv6 is not null && properties.GetIPv6Properties() is { } ip6)
                checks.Add(TestBoundSocketAsync(ipv6, ip6.Index, new(IPAddress.Parse("2606:4700:4700::1111"), 443),
                    $"IPv6, интерфейс {number}", false, cancellationToken));
        }
        return checks.Count == 0
            ? Task.FromResult<string[]>(["Проверка обхода: нет подходящего активного физического интерфейса."])
            : Task.WhenAll(checks);
    }

    private static async Task<string> TestBoundSocketAsync(IPAddress local, int index, IPEndPoint target,
        string label, bool dns, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        bool ipv4 = local.AddressFamily == AddressFamily.InterNetwork;
        try
        {
            using var socket = new Socket(local.AddressFamily, dns ? SocketType.Dgram : SocketType.Stream,
                dns ? ProtocolType.Udp : ProtocolType.Tcp);
            socket.SetSocketOption(ipv4 ? SocketOptionLevel.IP : SocketOptionLevel.IPv6,
                UnicastInterface, ipv4 ? IPAddress.HostToNetworkOrder(index) : index);
            socket.Bind(new IPEndPoint(local, 0));
            await socket.ConnectAsync(target, deadline.Token);
            if (dns)
            {
                // A single ordinary DNS query with no user-supplied names or other data.
                byte[] query = [0x4c, 0x57, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 7, 101, 120, 97, 109, 112, 108, 101, 3, 99, 111, 109, 0, 0, 1, 0, 1];
                await socket.SendAsync(query, SocketFlags.None, deadline.Token);
                byte[] reply = new byte[512];
                int received = await socket.ReceiveAsync(reply, SocketFlags.None, deadline.Token);
                if (received < 12 || reply[0] != query[0] || reply[1] != query[1] || (reply[2] & 0x80) == 0)
                    return $"{label}: ответ не подтверждён.";
            }
            return $"{label}: обнаружен выход вне TUN.";
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AccessDenied)
        {
            return $"{label}: Windows отклонила соединение.";
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        {
            return $"{label}: результат не определён.";
        }
    }

    private async void ExportDiagnostics(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        var dialog = new SaveFileDialog { FileName = "Linkway-diagnostics.json", Filter = "JSON (*.json)|*.json", AddExtension = true };
        if (dialog.ShowDialog(this) == true)
        {
            string report = JsonSerializer.Serialize(new
            {
                Version = GitHubUpdates.InstalledRelease.ToString(),
                Windows = Environment.OSVersion.Version.ToString(),
                Mode = diagnostics?.Mode ?? (TunMode.IsChecked == true ? "TUN" : "PROXY"),
                KillSwitch = diagnostics?.KillSwitch ?? killSwitch,
                Connected = diagnostics?.Connected ?? (session is not null),
                CheckedAt = diagnostics?.CheckedAt,
                Mihomo = diagnostics?.Mihomo ?? session?.CoreVersion ?? MihomoVersions.Active.Version.ToString(3),
                Checks = diagnostics?.Checks ?? [],
                Events = journal.ToArray()
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(dialog.FileName, report);
            Message.Text = "Диагностический отчёт сохранён.";
        }
        return Task.CompletedTask;
    });
}
