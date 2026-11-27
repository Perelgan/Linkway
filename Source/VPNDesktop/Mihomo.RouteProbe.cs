using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VPNDesktop;

internal sealed record RouteProbeResult(string? Policy, string? Rule, string? Outbound, string? Chain, int? ProxyStatus);

internal sealed record RouteProbeTarget(string Host, int Port)
{
    public string Authority => IPAddress.TryParse(Host, out var address) &&
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    public static RouteProbeTarget? Parse(string? text)
    {
        string input = text?.Trim() ?? "";
        if (input.Contains(':') && !input.Contains('%') && IPAddress.TryParse(input, out var literal) &&
            literal.AddressFamily == AddressFamily.InterNetworkV6)
            return new(literal.ToString(), 443);
        if (input.Length is 0 or > 2048 || input.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(input.Contains("://", StringComparison.Ordinal) ? input : "https://" + input,
                UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            uri.Port is < 1 or > 65535)
            return null;
        string host = uri.IdnHost.TrimEnd('.');
        return host.Length is > 0 and <= 253 && !host.Contains('%') ? new(host, uri.Port) : null;
    }
}

internal sealed partial class Mihomo
{
    public async Task<RouteProbeResult> ProbeRouteAsync(string? destination, CancellationToken token)
    {
        var target = RouteProbeTarget.Parse(destination) ?? throw new IOException("Введите домен, IP-адрес или URL.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var ct = deadline.Token;
        int? status = null;
        Task<RouteProbeResult?> rejected = Task.FromResult<RouteProbeResult?>(null);
        using var logs = new ClientWebSocket();
        try
        {
            using var socket = new TcpClient(AddressFamily.InterNetwork);
            await socket.ConnectAsync(IPAddress.Loopback, Port, ct);
            int sourcePort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            using (var subscription = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                subscription.CancelAfter(TimeSpan.FromSeconds(1));
                logs.Options.Proxy = null;
                logs.Options.SetRequestHeader("Authorization", $"Bearer {secret}");
                try
                {
                    await logs.ConnectAsync(new Uri($"ws://127.0.0.1:{controller}/logs?level=info"), subscription.Token);
                    rejected = WatchProbeRejectionAsync(logs, sourcePort, target, currentConfig, ct);
                }
                catch (WebSocketException) { }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"CONNECT {target.Authority} HTTP/1.1\r\nHost: {target.Authority}\r\n\r\n"), ct);

            // Keeping an accepted CONNECT tunnel open lets the controller report its actual rule and chain.
            var response = await reader.ReadLineAsync(ct);
            status = ParseConnectStatus(response);
            while (status == 200)
            {
                if (rejected.IsCompleted && await rejected is { } blocked)
                    return blocked with { ProxyStatus = status };
                using var snapshot = await GetAsync("connections", ct);
                var observed = ReadProbeRoute(snapshot.RootElement, sourcePort, target, currentConfig, status);
                if (observed is not null)
                    return observed;
                await Task.Delay(200, ct);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (SocketException) { }
        catch (IOException) { }
        catch (HttpRequestException) { }
        finally
        {
            await deadline.CancelAsync();
            await rejected;
        }
        if (await rejected is { } rejection)
            return rejection with { ProxyStatus = status };
        return new(null, null, null, null, status);
    }

    private static async Task<RouteProbeResult?> WatchProbeRejectionAsync(ClientWebSocket socket, int sourcePort,
        RouteProbeTarget target, RoutingConfig config, CancellationToken token)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (frame.MessageType == WebSocketMessageType.Close)
                    break;
                if (message.Length <= 65536)
                    message.Write(buffer, 0, frame.Count);
                if (!frame.EndOfMessage)
                    continue;
                if (frame.MessageType == WebSocketMessageType.Text && message.Length <= 65536)
                {
                    using var json = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                    if (json.RootElement.TryGetProperty("payload", out var payload) &&
                        ReadProbeRejection(payload.GetString(), sourcePort, target, config) is { } result)
                        return result;
                }
                message.SetLength(0);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException or JsonException) { }
        return null;
    }

    internal static RouteProbeResult? ReadProbeRejection(string? log, int sourcePort, RouteProbeTarget target, RoutingConfig config)
    {
        string prefix = $"[TCP] 127.0.0.1:{sourcePort}";
        if (log is null || !log.StartsWith(prefix, StringComparison.Ordinal) || log.Length <= prefix.Length ||
            log[prefix.Length] is not (' ' or '('))
            return null;
        string marker = $" --> {target.Authority} ";
        int destination = log.IndexOf(marker, prefix.Length, StringComparison.OrdinalIgnoreCase);
        if (destination < 0)
            return null;
        string details = log[(destination + marker.Length)..];
        const string usingOutbound = " using ";
        int split = details.LastIndexOf(usingOutbound, StringComparison.Ordinal);
        string outbound = split >= 0 ? details[(split + usingOutbound.Length)..]
            : details.StartsWith("using ", StringComparison.Ordinal) ? details[6..] : "";
        string? blocked = outbound is "REJECT" or "REJECT-DROP" ? outbound
            : outbound.EndsWith("[REJECT]", StringComparison.Ordinal) ? "REJECT"
            : outbound.EndsWith("[REJECT-DROP]", StringComparison.Ordinal) ? "REJECT-DROP" : null;
        if (blocked is null)
            return null;
        string? rule = split >= 0 && details.StartsWith("match ", StringComparison.Ordinal) ? details[6..split] : null;
        if (rule is not null && rule.IndexOf('(') is > 0 and var open && rule.EndsWith(')'))
            rule = FormatProbeRule(rule[..open], rule[(open + 1)..^1], config);
        string? group = outbound == blocked ? null : outbound[..^(blocked.Length + 2)];
        return new("REJECT", rule, blocked, group is null ? null : config.GroupLabels.GetValueOrDefault(group, group), null);
    }

    private static int? ParseConnectStatus(string? line)
    {
        var parts = line?.Split(' ', 3);
        return parts is { Length: >= 2 } && parts[0].StartsWith("HTTP/", StringComparison.Ordinal) &&
            int.TryParse(parts[1], out int status) ? status : null;
    }

    internal static RouteProbeResult? ReadProbeRoute(JsonElement snapshot, int sourcePort, RouteProbeTarget target,
        RoutingConfig config, int? status)
    {
        if (!snapshot.TryGetProperty("connections", out var connections) || connections.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var entry in connections.EnumerateArray())
        {
            if (!entry.TryGetProperty("metadata", out var metadata) ||
                Read(metadata, "sourceIP") != "127.0.0.1" || Read(metadata, "sourcePort") != sourcePort.ToString() ||
                Read(metadata, "destinationPort") != target.Port.ToString())
                continue;
            string host = Read(metadata, "host").TrimEnd('.');
            string ip = Read(metadata, "destinationIP");
            bool matches = IPAddress.TryParse(target.Host, out var requested)
                ? (IPAddress.TryParse(ip, out var actual) && requested.Equals(actual)) ||
                    (IPAddress.TryParse(host, out actual) && requested.Equals(actual))
                : host.Equals(target.Host, StringComparison.OrdinalIgnoreCase);
            if (!matches)
                continue;
            var connection = ActiveConnection.From(entry, config);
            string payload = Read(entry, "rulePayload");
            string rule = FormatProbeRule(Read(entry, "rule"), payload, config);
            string? policy = connection.Outbound switch
            {
                "DIRECT" => "DIRECT",
                "REJECT" or "REJECT-DROP" => "REJECT",
                "" => null,
                _ => "VPN"
            };
            return new(policy, rule, config.ExpectedNodes.GetValueOrDefault(connection.Outbound, connection.Outbound),
                connection.Route, status);
        }
        return null;

        static string Read(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.ToString() : "";
    }

    private static string FormatProbeRule(string type, string payload, RoutingConfig config) =>
        (type + " " + config.ExpectedRuleProviders.GetValueOrDefault(payload, payload)).Trim();
}
