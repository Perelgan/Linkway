using Microsoft.Win32;
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
    private static readonly Uri InternetTest = new("https://www.gstatic.com/generate_204");
    private static readonly Uri Ipv6Test = new("https://api6.ipify.org/");
    private static readonly string SessionHistoryPath = Path.Combine(ProfileStore.DirectoryPath, "session-history.bin");
    private readonly Queue<EventEntry> journal = new();
    private readonly List<SessionEntry> history = [];
    private readonly Dictionary<string, (long Download, long Upload)> observed = [];
    private readonly Dictionary<string, (long Download, long Upload)> appTraffic = [];
    private readonly Dictionary<string, (long Download, long Upload)> serverTraffic = [];
    private DateTimeOffset nextTrafficSample;
    private DateTimeOffset sessionStarted;
    private (long Download, long Upload) currentTraffic;
    private string[]? diagnosticChecks;

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
        sessionStarted = DateTimeOffset.Now;
        currentTraffic = default;
        observed.Clear();
        appTraffic.Clear();
        serverTraffic.Clear();
    }

    private void AccumulateTraffic(long download, long upload, ActiveConnection[] connections)
    {
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
            string app = connection.Process.Length != 0 ? connection.Process : Path.GetFileName(connection.Path);
            Add(appTraffic, app, down, up);
            Add(serverTraffic, connection.Outbound, down, up);
        }
        if (IsVisible && DiagnosticsNav.IsChecked == true)
            RefreshTraffic();

        static void Add(Dictionary<string, (long Download, long Upload)> totals, string key, long down, long up)
        {
            if (key.Length == 0)
                key = "Не определено";
            if (totals.Count >= 256 && !totals.ContainsKey(key))
                key = "Остальные";
            var previous = totals.GetValueOrDefault(key);
            totals[key] = (previous.Download + down, previous.Upload + up);
        }
    }

    private void RefreshTraffic()
    {
        SessionTraffic.Text = $"↓ {currentTraffic.Download:N0} Б   ↑ {currentTraffic.Upload:N0} Б";
        AppTrafficList.ItemsSource = TrafficRows(appTraffic);
        ServerTrafficList.ItemsSource = TrafficRows(serverTraffic);

        static string[] TrafficRows(Dictionary<string, (long Download, long Upload)> values) =>
            values.OrderByDescending(item => item.Value.Download + item.Value.Upload).Take(30)
                .Select(item => $"{item.Key}   ·   ↓ {item.Value.Download:N0} Б   ↑ {item.Value.Upload:N0} Б").ToArray();
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
        history.Add(new(sessionStarted, (int)Math.Min(int.MaxValue, connectedFor.Elapsed.TotalSeconds),
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
        if (killSwitch is not null && session is null)
        {
            diagnosticChecks = ["Kill Switch: блокировка после отключения ядра активна. Сетевые запросы пропущены."];
        }
        else
        {
            DiagnosticResults.Text = "Выполняем проверку…";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            var core = session;
            using var client = new HttpClient(new HttpClientHandler
            {
                UseProxy = core is not null,
                Proxy = core is null ? null : new WebProxy($"http://127.0.0.1:{core.Port}"),
                AllowAutoRedirect = false,
                UseCookies = false
            });
            var internet = Probe(() => CheckHttps(client, InternetTest, deadline.Token));
            var ipv6 = Probe(() => CheckHttps(client, Ipv6Test, deadline.Token));
            var dns = core is null ? Task.FromResult(false) : Probe(() => core.TestDnsAsync(deadline.Token));
            var node = core is null ? Task.FromResult(false)
                : Probe(async () => await core.CheckMainRouteAsync(deadline.Token) is not null);
            bool guardActive = killSwitch is not null && core is not null;
            var bypass = guardActive ? TestPhysicalBypassAsync(deadline.Token) : Task.FromResult<string[]>([]);
            await Task.WhenAll(internet, ipv6, dns, node, bypass);
            lifetime.Token.ThrowIfCancellationRequested();
            bool tun = core is not null && TunMode.IsChecked == true;
            diagnosticChecks =
            [
                $"Интернет через {(core is null ? "Windows" : "текущую конфигурацию Mihomo")}: {Status(await internet)}",
                $"HTTPS к IPv6-сервису: {Status(await ipv6)}",
                core is null ? "Сервер и DNS Mihomo: требуется активное подключение."
                    : $"Сервер: {Status(await node)}. Ответ DNS Mihomo: {Status(await dns)}.",
                tun ? $"Защита IPv4/IPv6: строгая маршрутизация TUN и перехват обычного DNS, Kill Switch {(killSwitch is null ? "выключен" : "включён")}."
                    : "Проверка защиты: для системного перехвата IPv6 и обычного DNS нужен TUN.",
                .. await bypass,
                "Активная проверка обхода выполняется при включённом Kill Switch. Тайм-аут не подтверждает блокировку."
            ];
        }
        DiagnosticResults.Text = string.Join(Environment.NewLine, diagnosticChecks);
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

    private static async Task<bool> CheckHttps(HttpClient client, Uri address, CancellationToken token)
    {
        using var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token);
        return response.IsSuccessStatusCode;
    }

    private static async Task<bool> Probe(Func<Task<bool>> action)
    {
        try { return await action(); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or SocketException)
        {
            return false;
        }
    }

    private static string Status(bool passed) => passed ? "есть ответ" : "нет ответа";

    private async void ExportDiagnostics(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        var dialog = new SaveFileDialog { FileName = "Linkway-diagnostics.json", Filter = "JSON (*.json)|*.json", AddExtension = true };
        if (dialog.ShowDialog(this) == true)
        {
            string report = JsonSerializer.Serialize(new
            {
                Version = GitHubUpdates.InstalledRelease.ToString(),
                Windows = Environment.OSVersion.Version.ToString(),
                Mode = TunMode.IsChecked == true ? "TUN" : "PROXY",
                KillSwitch = killSwitch is not null,
                Connected = session is not null,
                Checks = diagnosticChecks ?? [],
                Events = journal.ToArray()
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(dialog.FileName, report);
            Message.Text = "Диагностический отчёт сохранён.";
        }
        return Task.CompletedTask;
    });
}
