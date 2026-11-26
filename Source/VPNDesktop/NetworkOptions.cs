using System.IO;
using System.Net;

namespace VPNDesktop;

internal enum ServerStrategy { Manual, Fastest, Fallback, Balance }

internal sealed record NetworkOptions(ServerStrategy Strategy = ServerStrategy.Manual, bool FakeIp = true,
    bool RefreshGeoData = false, string Resolvers = "", string DomainResolvers = "", string RuleSets = "", RouteServer? UnavailableSitesServer = null)
{
    public (string[] Resolvers, Dictionary<string, string> Policies) ParseDns(string fallback, string outbound)
    {
        var resolvers = Lines(Resolvers, 8).Select(Resolver).ToArray();
        if (resolvers.Length == 0)
            resolvers = [fallback];
        var policies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in Lines(DomainResolvers, 64))
        {
            string[] pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
                throw new IOException("Используйте формат домен=DNS, по одной записи на строку.");
            string domain = "+." + RoutingProfiles.Normalize(RouteMatch.DomainSuffix, pair[0]);
            if (!policies.TryAdd(domain, $"{Resolver(pair[1])}#{outbound}"))
                throw new IOException("Домен DNS указан несколько раз.");
        }
        return (resolvers.Select(resolver => $"{resolver}#{outbound}").ToArray(), policies);
    }

    public IEnumerable<(Uri Address, string Behavior, string Format, string Outbound)> ParseRuleSets(string main)
    {
        foreach (string line in Lines(RuleSets, 16))
        {
            string[] fields = line.Split('|', StringSplitOptions.TrimEntries);
            if (fields.Length != 4 || !Uri.TryCreate(fields[0], UriKind.Absolute, out var address) ||
                address.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length != 0 || address.IsLoopback ||
                address.Fragment.Length != 0 || fields[1] is not ("classical" or "domain" or "ipcidr") ||
                fields[2] is not ("yaml" or "text" or "mrs") || fields[2] == "mrs" && fields[1] == "classical")
                throw new IOException("Проверьте формат набора правил и его HTTPS-адрес.");
            string outbound = fields[3] switch
            {
                "VPN" => main,
                "DIRECT" => "DIRECT",
                "REJECT" => "REJECT",
                _ => throw new IOException("Направление набора правил должно быть VPN, DIRECT или REJECT.")
            };
            yield return (address, fields[1], fields[2], outbound);
        }
    }

    private static string Resolver(string input)
    {
        if (!input.Contains('%') && IPAddress.TryParse(input, out var ip))
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"udp://[{ip}]:53" : ip.ToString();
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "tls" &&
            uri.Host.Length != 0 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0)
        {
            if (uri.Scheme == "https")
                return uri.AbsoluteUri;
            if (uri.AbsolutePath == "/" && uri.Query.Length == 0)
                return uri.GetLeftPart(UriPartial.Authority);
        }
        throw new IOException("Укажите IP-адрес DNS, https:// для DoH или tls:// для DoT.");
    }

    private static string[] Lines(string input, int limit)
    {
        var lines = (input ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length <= limit ? lines : throw new IOException($"Допустимо не более {limit} строк.");
    }
}
