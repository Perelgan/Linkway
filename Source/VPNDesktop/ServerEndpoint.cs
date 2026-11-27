using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;

namespace VPNDesktop;

internal sealed record ServerEndpoint(string Host, int Port, bool Tcp)
{
    public static ServerEndpoint? Create(string host, string port, string protocol, string network = "", string transport = "")
    {
        host = host.Trim('[', ']').TrimEnd('.');
        if (!int.TryParse(port, out int number) || number is < 1 or > 65535 ||
            Uri.CheckHostName(host) == UriHostNameType.Unknown)
            return null;
        if (!IPAddress.TryParse(host, out _))
        {
            try { host = new System.Globalization.IdnMapping().GetAscii(host); }
            catch (ArgumentException) { return null; }
        }
        bool tcp = protocol.ToLowerInvariant() is not ("hysteria" or "hysteria2" or "hy2" or "tuic" or "wireguard" or "juicity") &&
            !network.Equals("quic", StringComparison.OrdinalIgnoreCase) &&
            !transport.Equals("udp", StringComparison.OrdinalIgnoreCase);
        return new(host, number, tcp);
    }

    public static Dictionary<string, ServerEndpoint> Read(string content)
    {
        content = content.Trim().TrimStart('\uFEFF');
        if (!content.Contains("://", StringComparison.Ordinal) && DecodeBase64(content) is { } decoded)
            content = decoded.Trim().TrimStart('\uFEFF');
        var endpoints = SubscriptionRouting.ReadProxyEndpoints(content);
        if (endpoints.Count > 0)
            return endpoints;

        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ReadLink(line) is not { } entry)
                continue;
            var (name, endpoint) = entry;
            int count = occurrences.GetValueOrDefault(name);
            occurrences[name] = count + 1;
            string expected = count == 0 ? name : $"{name}-{count:D2}";
            endpoints.TryAdd(expected, endpoint);
        }
        return endpoints;
    }

    private static (string Name, ServerEndpoint Endpoint)? ReadLink(string line)
    {
        int separator = line.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
            return null;
        string scheme = line[..separator].ToLowerInvariant();
        if (scheme == "vmess" && DecodeBase64(line[(separator + 3)..]) is { } vmess)
        {
            try
            {
                using var json = JsonDocument.Parse(vmess);
                var config = json.RootElement;
                if (config.ValueKind == JsonValueKind.Object &&
                    Create(Read("add"), Read("port"), scheme, Read("net")) is { } vmessEndpoint)
                    return (Read("ps"), vmessEndpoint);

                string Read(string key) => config.TryGetProperty(key, out var value) ? value.ToString() : "";
            }
            catch (JsonException) { }
            return null;
        }

        if (!Uri.TryCreate(line, UriKind.Absolute, out var link))
            return null;
        string name = Uri.UnescapeDataString(link.Fragment.TrimStart('#'));
        if (scheme == "ss" && link.Port < 1 && DecodeBase64(link.Host) is { } legacy &&
            Uri.TryCreate("ss://" + legacy, UriKind.Absolute, out var decodedShadowsocks))
            link = decodedShadowsocks;
        string host = link.IdnHost;
        if (name.Length == 0 && scheme is "http" or "https" or "socks" or "socks5" or "socks5h" or "anytls" or "vless" or "vmess")
            name = $"{host}:{link.Port}";
        var network = HttpUtility.ParseQueryString(link.Query)["type"] ?? "";
        return Create(host, link.Port.ToString(), scheme, network) is { } endpoint ? (name, endpoint) : null;
    }

    private static string? DecodeBase64(string value)
    {
        string normalized = string.Concat(value.Where(c => !char.IsWhiteSpace(c))).Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(normalized)); }
        catch (FormatException) { return null; }
    }
}
