using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VPNDesktop;

internal static class WindowsDirectProbe
{
    // Winsock uses option 31 for both IP_UNICAST_IF and IPV6_UNICAST_IF.
    private const SocketOptionName UnicastInterface = (SocketOptionName)31;

    internal sealed record Path(int InterfaceIndex, IPAddress Address);

    public static Path[] GetPaths()
    {
        var paths = new List<Path>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or
                    NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.Wireless80211 or
                    NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2) ||
                adapter.Name.Equals(RoutingConfig.TunDevice, StringComparison.OrdinalIgnoreCase) ||
                adapter.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
                adapter.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
                adapter.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase) ||
                adapter.Description.Contains("wireguard", StringComparison.OrdinalIgnoreCase) ||
                adapter.Description.Contains("tap-windows", StringComparison.OrdinalIgnoreCase))
                continue;
            var properties = adapter.GetIPProperties();
            var addresses = properties.UnicastAddresses.Select(item => item.Address).ToArray();
            foreach (var family in properties.GatewayAddresses.Select(gateway => gateway.Address.AddressFamily).Distinct())
            {
                var address = addresses.FirstOrDefault(ip => ip.AddressFamily == family && !IPAddress.IsLoopback(ip) &&
                    !(family == AddressFamily.InterNetwork && ip.GetAddressBytes() is [169, 254, _, _]) &&
                    !ip.IsIPv6LinkLocal && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any));
                int? index = family == AddressFamily.InterNetwork ? properties.GetIPv4Properties()?.Index
                    : properties.GetIPv6Properties()?.Index;
                if (address is not null && index is > 0)
                    paths.Add(new(index.Value, address));
            }
        }
        return paths.ToArray();
    }

    internal static void Bind(Socket socket, IPAddress local, int index)
    {
        bool ipv4 = local.AddressFamily == AddressFamily.InterNetwork;
        socket.SetSocketOption(ipv4 ? SocketOptionLevel.IP : SocketOptionLevel.IPv6,
            UnicastInterface, ipv4 ? IPAddress.HostToNetworkOrder(index) : index);
        socket.Bind(new IPEndPoint(local, 0));
    }

    public static async Task<int?> MeasureAsync(ServerEndpoint endpoint, Path[] paths, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        var literal = IPAddress.TryParse(endpoint.Host, out var ip) ? ip : null;
        if (literal is not null && IPAddress.IsLoopback(literal))
            return await ConnectAsync(literal, endpoint.Port, null, deadline.Token);
        var attempts = paths.Where(path => literal is null || literal.AddressFamily == path.Address.AddressFamily)
            .Select(CheckPathAsync).ToList();
        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts);
            attempts.Remove(finished);
            if (await finished is int delay)
            {
                await deadline.CancelAsync();
                await Task.WhenAll(attempts);
                return delay;
            }
        }
        token.ThrowIfCancellationRequested();
        return null;

        async Task<int?> CheckPathAsync(Path path)
        {
            try
            {
                var addresses = literal is null ? await ResolveOnInterfaceAsync(endpoint.Host, path, deadline.Token) : [literal];
                foreach (var address in addresses.Take(3))
                    if (await ConnectAsync(address, endpoint.Port, path, deadline.Token) is int delay)
                        return delay;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            return null;
        }
    }

    private static async Task<int?> ConnectAsync(IPAddress address, int port, Path? path, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            if (path is not null)
                Bind(socket, path.Address, path.InterfaceIndex);
            long start = Stopwatch.GetTimestamp();
            await socket.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
            return Math.Max(1, (int)Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds));
        }
        catch (SocketException) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    internal static async Task<IPAddress[]> ResolveOnInterfaceAsync(string host, Path path, CancellationToken token)
    {
        using var dnsDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        dnsDeadline.CancelAfter(TimeSpan.FromMilliseconds(900));
        try
        {
            var addresses = await Task.Run(() => QueryDns(host, path), dnsDeadline.Token).WaitAsync(dnsDeadline.Token);
            if (addresses.Length > 0)
                return addresses;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }

        // Mihomo strict-route can suppress port 53 on the physical adapter. Keep the DoH socket bound as well.
        return await QueryHttpsAsync(host, path, token);
    }

    private static async Task<IPAddress[]> QueryHttpsAsync(string host, Path path, CancellationToken token)
    {
        bool ipv4 = path.Address.AddressFamily == AddressFamily.InterNetwork;
        var resolver = new Uri(ipv4 ? RoutingConfig.BootstrapDns : "https://[2606:4700:4700::1111]/dns-query");
        var resolverIp = IPAddress.Parse(resolver.IdnHost);
        using var client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(resolverIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    Bind(socket, path.Address, path.InterfaceIndex);
                    await socket.ConnectAsync(new IPEndPoint(resolverIp, resolver.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        });
        client.Timeout = TimeSpan.FromSeconds(3);
        client.MaxResponseContentBufferSize = 65536;
        client.DefaultRequestHeaders.Accept.ParseAdd("application/dns-json");
        int type = ipv4 ? 1 : 28;
        try
        {
            using var response = await client.GetAsync($"{resolver}?name={Uri.EscapeDataString(host)}&type={type}", token);
            if (!response.IsSuccessStatusCode)
                return [];
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
            var root = json.RootElement;
            if (!root.TryGetProperty("Status", out var status) || status.GetInt32() != 0 ||
                !root.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
                return [];
            var addresses = new List<IPAddress>();
            foreach (var answer in answers.EnumerateArray())
                if (answer.TryGetProperty("type", out var recordType) && recordType.GetInt32() == type &&
                    answer.TryGetProperty("data", out var data) && IPAddress.TryParse(data.GetString(), out var address) &&
                    address.AddressFamily == path.Address.AddressFamily && !IPAddress.IsLoopback(address))
                    addresses.Add(address);
            return addresses.Distinct().ToArray();
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException) { return []; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return []; }
    }

    private static IPAddress[] QueryDns(string host, Path path)
    {
        const ulong WireOnly = 0x100, TreatAsFqdn = 0x1000;
        bool ipv4 = path.Address.AddressFamily == AddressFamily.InterNetwork;
        var request = new DnsRequest
        {
            Version = 1,
            Name = host,
            Type = ipv4 ? (ushort)1 : (ushort)28,
            Options = WireOnly | TreatAsFqdn,
            InterfaceIndex = (uint)path.InterfaceIndex
        };
        var result = new DnsResult { Version = 1 };
        try
        {
            if (DnsQueryEx(ref request, ref result, IntPtr.Zero) != 0 || result.Status != 0)
                return [];
            var addresses = new List<IPAddress>();
            int dataOffset = Marshal.SizeOf<DnsRecord>();
            for (IntPtr next = result.Records; next != IntPtr.Zero;)
            {
                var record = Marshal.PtrToStructure<DnsRecord>(next);
                int length = ipv4 ? 4 : 16;
                if (record.Type == request.Type && record.Length >= length && (record.Flags & 3) == 1)
                {
                    var data = new byte[length];
                    Marshal.Copy(next + dataOffset, data, 0, length);
                    var address = new IPAddress(data);
                    if (!IPAddress.IsLoopback(address))
                        addresses.Add(address);
                }
                next = record.Next;
            }
            return addresses.Distinct().ToArray();
        }
        finally
        {
            if (result.Records != IntPtr.Zero)
                DnsRecordListFree(result.Records, 1);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DnsRequest
    {
        public uint Version;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public ushort Type;
        public ulong Options;
        public IntPtr ServerList;
        public uint InterfaceIndex;
        public IntPtr Completion, Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsResult
    {
        public uint Version, Status;
        public ulong Options;
        public IntPtr Records, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsRecord
    {
        public IntPtr Next, Name;
        public ushort Type, Length;
        public uint Flags, Ttl, Reserved;
    }

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern int DnsQueryEx(ref DnsRequest request, ref DnsResult result, IntPtr cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern void DnsRecordListFree(IntPtr records, int freeType);
}
