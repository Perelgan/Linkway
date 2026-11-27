using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VPNDesktop;

public partial class MainWindow
{
    private sealed record DiagnosticSnapshot(DateTimeOffset CheckedAt, string Mode, bool KillSwitch,
        bool Connected, string Mihomo, string[] Checks);

    private static readonly Uri PrimaryProbe = new("https://www.gstatic.com/generate_204");
    private static readonly Uri SecondaryProbe = new("https://example.org/");
    private static readonly Uri Ipv6Probe = new("https://api6.ipify.org/");
    private static readonly IPAddress TunRouteProbe = IPAddress.Parse("1.1.1.1");

    private static async Task<string[]> CollectNetworkChecksAsync(VpnSessionClient? core, bool tun, bool guard, CancellationToken token)
    {
        var checks = new List<string>();
        try { checks.AddRange(ReadWindowsNetwork(core, tun)); }
        catch (Exception error) when (error is NetworkInformationException or System.ComponentModel.Win32Exception)
        {
            checks.Add("Windows: не удалось прочитать состояние адаптеров или прокси.");
        }

        if (core is null && guard)
        {
            checks.Add("Kill Switch удерживает блокировку. Сетевые проверки пропущены.");
            return checks.ToArray();
        }

        var proxyPorts = new ConcurrentDictionary<int, byte>();
        using var proxy = core is null ? null : new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy(IPAddress.Loopback.ToString(), core.Port),
            UseProxy = true,
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (context, cancellation) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, core.Port, cancellation);
                    proxyPorts.TryAdd(((IPEndPoint)socket.LocalEndPoint!).Port, 0);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        });
        using var windows = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false, UseCookies = false, AllowAutoRedirect = false
        });

        var tasks = new List<Task<string>>();
        if (core is not null)
        {
            tasks.Add(MeasureAsync("Локальный порт Mihomo", async ct =>
            {
                using var socket = new TcpClient(AddressFamily.InterNetwork);
                await socket.ConnectAsync(IPAddress.Loopback, core.Port, ct);
                return "доступен";
            }, token));
            tasks.Add(MeasureAsync("Основной VPN-выход", async ct =>
                await core.CheckMainRouteAsync(ct) is int delay ? $"ответил, задержка {delay} мс" : "нет ответа", token));
            tasks.Add(MeasureAsync("API и правила Mihomo", core.DescribeDiagnosticConfigurationAsync, token));
            tasks.Add(MeasureAsync("DNS Mihomo A", async ct => Count(await core.TestDnsAsync(false, ct)), token));
            tasks.Add(MeasureAsync("DNS Mihomo AAAA", async ct => Count(await core.TestDnsAsync(true, ct)), token));
        }
        if (core is null || tun)
        {
            tasks.Add(MeasureAsync("DNS Windows A/AAAA", async ct =>
            {
                var addresses = await Dns.GetHostAddressesAsync(PrimaryProbe.IdnHost, ct);
                return $"IPv4: {addresses.Count(ip => ip.AddressFamily == AddressFamily.InterNetwork)}, IPv6: {addresses.Count(ip => ip.AddressFamily == AddressFamily.InterNetworkV6)}";
            }, token));
            tasks.Add(MeasureHttpAsync(core is null ? "HTTPS через Windows" : "HTTPS через Windows без прокси",
                windows, PrimaryProbe, token));
        }

        if (proxy is not null)
        {
            var requests = new[]
            {
                MeasureHttpAsync("HTTPS через Mihomo · основной сайт", proxy, PrimaryProbe, token),
                MeasureHttpAsync("HTTPS через Mihomo · резервный сайт", proxy, SecondaryProbe, token),
                MeasureHttpAsync("HTTPS через Mihomo · IPv6-сервис", proxy, Ipv6Probe, token)
            };
            tasks.AddRange(requests);
            tasks.Add(ObserveProbeRoutesAsync(core!, proxyPorts, Task.WhenAll(requests), token));
        }
        else
            tasks.Add(MeasureHttpAsync("HTTPS через Windows · IPv6-сервис", windows, Ipv6Probe, token));

        var bypass = guard && core is not null ? TestPhysicalBypassAsync(token) : Task.FromResult<string[]>([]);
        checks.AddRange(await Task.WhenAll(tasks));
        checks.AddRange(await bypass);
        if (guard && core is not null)
            checks.Add("Для проверки обхода TUN тайм-аут означает неопределённый результат.");
        return checks.ToArray();

        static string Count(bool answer) => answer ? "есть ответ" : "записей нет";
    }

    private static async Task<string> ObserveProbeRoutesAsync(VpnSessionClient core, ConcurrentDictionary<int, byte> ports,
        Task complete, CancellationToken token)
    {
        const string label = "Маршруты тестовых HTTPS-запросов";
        var observed = new Dictionary<string, string>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(9));
        try
        {
            do
            {
                if (!ports.IsEmpty)
                {
                    foreach (var route in await core.GetDiagnosticRoutesAsync(ports, deadline.Token))
                        observed.TryAdd(route.Host, route.Kind);
                    if (observed.Count == 3)
                        break;
                }
                if (complete.IsCompleted)
                    break;
                await Task.Delay(200, deadline.Token);
            } while (!deadline.IsCancellationRequested);
        }
        catch (Exception error) when (IsHostCommunicationError(error) || error is HttpRequestException or InvalidOperationException or KeyNotFoundException)
        {
            // Closed or rejected connections may disappear before the API snapshot.
        }
        if (observed.Count == 0)
            return $"{label}: Mihomo не показал соединения во время проверки.";
        return $"{label}: {string.Join(", ", new[] { PrimaryProbe, SecondaryProbe, Ipv6Probe }
            .Select(probe => observed.GetValueOrDefault(probe.IdnHost, "не зафиксирован")))}.";
    }

    private static Task<string> MeasureHttpAsync(string label, HttpClient client, Uri uri, CancellationToken token) =>
        MeasureAsync(label, async ct =>
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            return $"HTTP {(int)response.StatusCode}";
        }, token);

    private static async Task<string> MeasureAsync(string label, Func<CancellationToken, Task<string>> probe, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(9));
        string result;
        try { result = await probe(timeout.Token); }
        catch (HttpRequestException error) { result = DescribeHttpFailure(error.HttpRequestError, error.StatusCode); }
        catch (OperationCanceledException) { result = "тайм-аут"; }
        catch (SocketException error) { result = $"сокет Windows ({error.SocketErrorCode})"; }
        catch (Exception error) when (IsHostCommunicationError(error) || error is InvalidOperationException or KeyNotFoundException)
        {
            result = "не удалось получить результат";
        }
        return $"{label}: {result} · {watch.ElapsedMilliseconds} мс";
    }

    internal static string DescribeHttpFailure(HttpRequestError error, HttpStatusCode? status) =>
        status is { } code ? $"HTTP {(int)code}" : error switch
        {
            HttpRequestError.NameResolutionError => "ошибка DNS",
            HttpRequestError.ConnectionError => "ошибка подключения TCP",
            HttpRequestError.SecureConnectionError => "ошибка TLS",
            HttpRequestError.ProxyTunnelError => "ошибка HTTP CONNECT",
            HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded => "неполный или неверный ответ HTTP",
            _ => "ошибка HTTPS"
        };

    private static IEnumerable<string> ReadWindowsNetwork(VpnSessionClient? core, bool tun)
    {
        if (core is not null && !tun)
            yield return WindowsProxy.UsesLinkway(core.Port) ? "Системный прокси Windows: указывает на Linkway."
                : "Системный прокси Windows: не указывает на Linkway.";

        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up).ToArray();
        var linkway = adapters.FirstOrDefault(adapter => adapter.Name.Equals(RoutingConfig.TunDevice, StringComparison.OrdinalIgnoreCase));
        yield return linkway is null ? "Адаптер Linkway TUN: не активен." : "Адаптер Linkway TUN: активен.";
        int otherTunnels = adapters.Count(a => a != linkway && IsTunnel(a));
        yield return $"Другие активные туннельные адаптеры: {otherTunnels}.";
        if (core is not null && tun)
        {
            yield return "Маршрут Windows к тестовому IPv4: " + RouteInterface(TunRouteProbe, adapters, linkway);
            yield return "Маршрут Windows к тестовому IPv6: " + RouteInterface(IPAddress.Parse("2606:4700:4700::1111"), adapters, linkway);
        }
    }

    private static bool IsTunnel(NetworkInterface adapter) =>
        adapter.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp ||
        adapter.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase) ||
        adapter.Description.Contains("wireguard", StringComparison.OrdinalIgnoreCase) ||
        adapter.Description.Contains("tap-windows", StringComparison.OrdinalIgnoreCase) ||
        adapter.Description.Contains("tun adapter", StringComparison.OrdinalIgnoreCase);

    private static string RouteInterface(IPAddress address, NetworkInterface[] adapters, NetworkInterface? linkway)
    {
        if (BestInterfaceIndex(address) is not uint index)
            return "маршрут не найден.";
        bool ipv4 = address.AddressFamily == AddressFamily.InterNetwork;
        var best = adapters.FirstOrDefault(adapter =>
        {
            var properties = adapter.GetIPProperties();
            return (ipv4 ? properties.GetIPv4Properties()?.Index : properties.GetIPv6Properties()?.Index) == index;
        });
        return best is null ? "адаптер не определён." : best == linkway ? "адаптер Linkway TUN."
            : IsTunnel(best) ? "другой туннельный адаптер." : "обычный сетевой адаптер.";
    }

    private static uint? BestInterfaceIndex(IPAddress address)
    {
        var endpoint = new IPEndPoint(address, 443).Serialize();
        return GetBestInterfaceEx(endpoint.Buffer.Span[..endpoint.Size].ToArray(), out uint index) == 0 ? index : null;
    }

    private static bool HasWindowsTunRoute()
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
            item.OperationalStatus == OperationalStatus.Up && item.Name.Equals(RoutingConfig.TunDevice, StringComparison.OrdinalIgnoreCase));
        return adapter?.GetIPProperties().GetIPv4Properties()?.Index is int index &&
            BestInterfaceIndex(TunRouteProbe) == (uint)index;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestInterfaceEx(byte[] destination, out uint bestInterface);
}
