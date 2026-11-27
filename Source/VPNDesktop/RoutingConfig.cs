using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VPNDesktop;

internal sealed partial class RoutingConfig
{
    public const string Main = "VPNDesktop-Selected";
    public const string MainProvider = "nodes";
    public const string TunDevice = "VPNDesktop";
    private const string DnsGroup = "VPNDesktop-DNS";
    internal const string BootstrapDns = "https://1.1.1.1/dns-query";
    private const string HealthUrl = "https://www.gstatic.com/generate_204";
    private const string ExcludedTypes = "direct|reject|rejectdrop|pass|passrule|compatible|dns|rematch|selector|fallback|urltest|loadbalance|relay";

    private readonly Dictionary<string, object> configuration;
    public Dictionary<string, string> Files { get; } = [];
    public Dictionary<string, Uri> RemoteRuleFiles { get; } = [];
    public Dictionary<string, string> ExpectedRuleProviders { get; } = [];
    public Dictionary<string, string> ExpectedNodes { get; } = [];
    public Dictionary<string, string[]> ExpectedGroups { get; } = [];
    public HashSet<string> HiddenGroups { get; } = [];
    public Dictionary<string, string> GroupLabels { get; } = new()
    {
        [DnsGroup] = "DNS через VPN"
    };
    public List<string> Rules { get; } = [];
    public HashSet<string> GeoFiles { get; } = [];
    public bool UsesAutomaticServerSelection { get; private init; }

    private RoutingConfig(int port, int controller, string secret)
    {
        configuration = new()
        {
            ["mixed-port"] = port,
            ["allow-lan"] = false,
            ["bind-address"] = "127.0.0.1",
            ["external-controller"] = $"127.0.0.1:{controller}",
            ["secret"] = secret,
            ["mode"] = "rule",
            ["ipv6"] = true,
            ["log-level"] = "warning",
            ["find-process-mode"] = "always",
            ["profile"] = new Dictionary<string, bool> { ["store-selected"] = false },
            ["rules"] = Rules
        };
    }

    public static Dictionary<string, object> Tun(bool enabled) => new()
    {
        ["enable"] = enabled,
        ["device"] = TunDevice,
        ["stack"] = "mixed",
        ["auto-route"] = true,
        ["auto-detect-interface"] = true,
        ["strict-route"] = true,
        ["dns-hijack"] = new[] { "any:53", "tcp://any:53" },
        ["inet6-address"] = new[] { "fdfe:dcba:9876::1/126" }
    };

    public string Payload(bool tun)
    {
        configuration["tun"] = Tun(tun);
        // Mihomo reads the payload as YAML, which rejects JSON's escaped surrogate halves.
        return EscapedSurrogatePair().Replace(JsonSerializer.Serialize(configuration), static escape =>
            string.Concat(escape.Groups[1].Value,
                (char)Convert.ToInt32(escape.Groups[2].Value, 16),
                (char)Convert.ToInt32(escape.Groups[3].Value, 16)));
    }

    // Preserve escaped backslashes, including literal Unicode escape text in regular expressions.
    [GeneratedRegex(@"(?<!\\)((?:\\\\)*)\\u([dD][89aAbB][0-9A-Fa-f]{2})\\u([dD][c-fC-F][0-9A-Fa-f]{2})")]
    private static partial Regex EscapedSurrogatePair();

    public static RoutingConfig Preview(int port, int controller, string secret, string content)
    {
        var config = new RoutingConfig(port, controller, secret);
        config.Files.Add("nodes.txt", content);
        config.configuration["find-process-mode"] = "off";
        config.configuration["proxy-providers"] = new Dictionary<string, object>
        {
            [MainProvider] = new { type = "file", path = "./nodes.txt" }
        };
        config.configuration["proxy-groups"] = new[]
        {
            new { name = Main, type = "select", proxies = new[] { "REJECT" }, use = new[] { MainProvider } }
        };
        config.Rules.Add("MATCH,REJECT");
        config.SetDns(Main);
        return config;
    }

    public static RoutingConfig Blocked(int port, int controller, string secret)
    {
        var config = new RoutingConfig(port, controller, secret);
        config.Rules.Add("MATCH,REJECT");
        config.SetDns("REJECT");
        return config;
    }

    public static bool IsRoutable(Node node) =>
        node.IsServer && !ExcludedTypes.Split('|').Contains(node.Type, StringComparer.OrdinalIgnoreCase) &&
        node.Name is not ("DIRECT" or "REJECT" or "REJECT-DROP" or "PASS" or "COMPATIBLE" or "GLOBAL" or Main);

    private static Dictionary<string, Profile> Catalog(IEnumerable<Profile> sources) =>
        sources.Where(source => source.Nodes.Length > 0).DistinctBy(RouteServer.SourceKey)
            .ToDictionary(RouteServer.SourceKey, StringComparer.Ordinal);

    private static Profile Resolve(Dictionary<string, Profile> catalog, RouteServer server)
    {
        if (catalog.TryGetValue(server.Source, out var source) &&
            source.Nodes.Any(candidate => candidate.Name == server.Name && IsRoutable(candidate)))
            return source;
        throw new IOException($"Сервер «{server.Name}» отсутствует в источниках. Выберите сервер заново в профиле маршрутизации.");
    }

    public static RoutingConfig Build(int port, int controller, string secret, Profile primary, string node,
        IEnumerable<Profile> sources, RoutingProfile? profile, bool bypassLan, NetworkOptions? network = null,
        string? automaticTestUrl = null)
    {
        profile = profile is null ? RoutingProfile.Create("Основной") : RoutingProfiles.Upgrade(profile);
        var config = new RoutingConfig(port, controller, secret)
        {
            UsesAutomaticServerSelection = network?.Strategy == ServerStrategy.Fastest
        };
        var catalog = Catalog(sources.Prepend(primary));
        var providers = new Dictionary<string, object>();
        var groups = new List<object>();
        var ruleProviders = new Dictionary<string, object>();
        var serverGroups = new Dictionary<RouteServer, string>();
        var primaryServer = new RouteServer(RouteServer.SourceKey(primary), node);
        string source = primaryServer.Source;
        var subscription = profile.UseSubscriptionRules ||
            profile.Default.Kind == RouteKind.SubscriptionGroup ||
            profile.Rules.Any(rule => rule.Enabled && rule.Target.Kind == RouteKind.SubscriptionGroup)
                ? SubscriptionRouting.Read(primary) : null;
        var choices = (profile.SubscriptionChoices ?? []).Where(choice => choice.Source == source)
            .ToDictionary(choice => choice.Group, StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        if (network?.Strategy is ServerStrategy.Fastest or ServerStrategy.Fallback or ServerStrategy.Balance)
        {
            var members = primary.Nodes.Where(IsRoutable).Select(candidate => candidate.Name)
                .Prepend(node).Distinct(StringComparer.Ordinal).Take(64)
                .Select(name => AddServer(new RouteServer(source, name))).ToArray();
            bool automatic = config.UsesAutomaticServerSelection;
            AddHealthGroup(Main, members, network.Strategy switch
            {
                ServerStrategy.Fastest => "url-test",
                ServerStrategy.Balance => "load-balance",
                _ => "fallback"
            }, automatic ? automaticTestUrl : null, interval: automatic ? 30 : 120, tolerance: automatic ? 50 : null);
        }
        else
            AddServer(primaryServer, Main);

        foreach (var rule in profile.Rules.Where(rule => rule.Enabled))
        {
            if (rule.Match == RouteMatch.GeoIp)
                config.GeoFiles.Add("GeoIP.dat");
            if (rule.Match == RouteMatch.GeoSite)
                config.GeoFiles.Add("GeoSite.dat");
            string match = Matcher(rule);
            if (rule.Protocol != RouteProtocol.Any)
                match = $"AND,((NETWORK,{rule.Protocol.ToString().ToLowerInvariant()}),({match}))";
            AddRule(match, rule.Target);
        }

        if (bypassLan)
        {
            config.Rules.AddRange([
                "DOMAIN-SUFFIX,local,DIRECT", "IP-CIDR,10.0.0.0/8,DIRECT,no-resolve",
                "IP-CIDR,172.16.0.0/12,DIRECT,no-resolve", "IP-CIDR,192.168.0.0/16,DIRECT,no-resolve",
                "IP-CIDR,169.254.0.0/16,DIRECT,no-resolve", "IP-CIDR6,fc00::/7,DIRECT,no-resolve",
                "IP-CIDR6,fe80::/10,DIRECT,no-resolve"
            ]);
        }
        if (network is not null)
        {
            int index = 0;
            foreach (var (address, behavior, format, outbound) in network.ParseRuleSets(Main))
            {
                string alias = $"VPNDesktop-UserRules-{++index}";
                string path = $"route-rules-{Hash(address.AbsoluteUri + "\\n" + behavior + "\\n" + format)}.bin";
                config.RemoteRuleFiles.TryAdd(path, address);
                config.ExpectedRuleProviders.Add(alias, $"Пользовательский набор {index}");
                ruleProviders.Add(alias, new { type = "file", path = "./" + path, behavior, format });
                WriteRule($"RULE-SET,{alias}", outbound);
            }
        }
        if (profile.UseSubscriptionRules && subscription is not null)
            AddSubscriptionRules(subscription);
        if (!config.Rules.Any(rule => rule.StartsWith("MATCH,", StringComparison.Ordinal)))
            AddRule("MATCH", profile.Default);

        bool useDnsGroup = serverGroups.Count > 1 && !config.ExpectedGroups.ContainsKey(Main);
        if (useDnsGroup)
            AddHealthGroup(DnsGroup, serverGroups.Values.ToArray());
        config.SetDns(useDnsGroup ? DnsGroup : Main, network);
        if (ruleProviders.Count > 0)
            config.configuration["rule-providers"] = ruleProviders;
        config.configuration["proxy-providers"] = providers;
        config.configuration["proxy-groups"] = groups;
        config.configuration["geodata-mode"] = true;
        config.configuration["geo-auto-update"] = network?.RefreshGeoData == true && config.GeoFiles.Count > 0;
        return config;

        void AddSubscriptionRules(SubscriptionRouting subscription)
        {
            var feedAliases = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var rule in subscription.Rules)
            {
                if (rule.Match == "MATCH")
                {
                    AddRule("MATCH", profile.Default);
                    break;
                }
                string match = SubscriptionRouting.RewriteFeedReferences(rule.Match, reference =>
                    $"{reference.Groups["prefix"].Value}RULE-SET,{AddFeed(reference.Groups["name"].Value.Trim())}");
                if (match.Contains("GEOIP,", StringComparison.Ordinal))
                    config.GeoFiles.Add("GeoIP.dat");
                if (match.Contains("GEOSITE,", StringComparison.Ordinal))
                    config.GeoFiles.Add("GeoSite.dat");
                string outbound = ResolvePolicy(rule.Destination);
                WriteRule(match, outbound, rule.Modifier);
            }

            string AddFeed(string name)
            {
                if (feedAliases.TryGetValue(name, out string? known))
                    return known;
                if (!subscription.Feeds.TryGetValue(name, out var feed) ||
                    feed.Behavior is not ("classical" or "domain" or "ipcidr"))
                    throw new IOException($"Набор правил «{name}» в подписке отсутствует или имеет неизвестный тип.");
                string alias = $"VPNDesktop-Rules-{feedAliases.Count}";
                object provider;
                if (feed.Payload is { Length: > 0 } payload)
                {
                    provider = new { type = "inline", behavior = feed.Behavior, payload };
                }
                else if (feed.Address is { Scheme: "https", UserInfo.Length: 0, Host.Length: > 0 } address &&
                    !address.IsLoopback && feed.Format is "yaml" or "text" or "mrs")
                {
                    string path = $"route-rules-{Hash(address.AbsoluteUri + "\n" + feed.Behavior + "\n" + feed.Format)}.bin";
                    config.RemoteRuleFiles.TryAdd(path, address);
                    provider = new { type = "file", path = "./" + path, behavior = feed.Behavior, format = feed.Format };
                }
                else
                    throw new IOException($"Набор правил «{name}» должен быть встроенным или загружаться по HTTPS.");

                feedAliases.Add(name, alias);
                config.ExpectedRuleProviders.Add(alias, name);
                ruleProviders.Add(alias, provider);
                return alias;
            }
        }

        string ResolvePolicy(string? name)
        {
            if (name is null)
                return "REJECT";
            if (name is "DIRECT" or "REJECT" or "REJECT-DROP" or Main)
                return name;
            if (aliases.TryGetValue(name, out string? existing))
                return existing;
            if (subscription?.Groups.TryGetValue(name, out var group) == true)
            {
                if (!pending.Add(name))
                    throw new IOException($"Группа «{name}» образует цикл. Выберите другой маршрут.");
                choices.TryGetValue(name, out var saved);
                string? choice = saved is null ? group.Default(primary) : saved.Destination;
                if (choice is not null && !SubscriptionRouting.IsLocalOption(choice) &&
                    !group.Options(primary).Contains(choice, StringComparer.Ordinal))
                    choice = null;
                bool unavailableSites = SubscriptionRouting.IsUnavailableSitesGroup(name);
                bool automatic = !unavailableSites && saved is null && group.IsAutomatic;
                var destinations = unavailableSites ? [AddServer(primaryServer)] : automatic
                    ? group.Options(primary).Select(ResolvePolicy).Distinct(StringComparer.Ordinal).ToArray()
                    : [saved?.Target is { } target ? ResolveTarget(target) : ResolvePolicy(choice)];
                pending.Remove(name);
                string alias = $"VPNDesktop-Subscription-{aliases.Count}";
                aliases.Add(name, alias);
                if (subscription.IsServiceGroup(name))
                    config.HiddenGroups.Add(alias);
                else
                    config.GroupLabels.Add(alias, name);
                if (automatic && destinations.Length > 0)
                    AddHealthGroup(alias, destinations, group.Type, group.TestUrl);
                else
                {
                    if (destinations.Length == 0)
                        destinations = ["REJECT"];
                    config.ExpectedGroups.Add(alias, destinations);
                    groups.Add(new Dictionary<string, object>
                    {
                        ["name"] = alias, ["type"] = "select", ["proxies"] = destinations,
                        ["empty-fallback"] = "REJECT"
                    });
                }
                return alias;
            }
            if (subscription?.Policies.TryGetValue(name, out string? policy) == true)
                return policy;
            return primary.Nodes.Any(candidate => candidate.Name == name && IsRoutable(candidate))
                ? AddServer(new(source, name)) : "REJECT";
        }

        string AddServer(RouteServer server, string? alias = null)
        {
            if (serverGroups.TryGetValue(server, out string? existing))
                return existing;
            var source = Resolve(catalog, server);

            string provider = server.Source == primaryServer.Source ? MainProvider : $"route-source-{server.Source}";
            if (!providers.ContainsKey(provider))
            {
                string fingerprint = Hash(source.Content);
                string filename = $"route-nodes-{fingerprint}.txt";
                config.Files.TryAdd(filename, source.Content);
                providers.Add(provider, new { type = "file", path = "./" + filename });
            }

            alias ??= $"VPNDesktop-Server-{serverGroups.Count}";
            serverGroups.Add(server, alias);
            config.ExpectedNodes.Add(alias, server.Name);
            groups.Add(new Dictionary<string, object>
            {
                ["name"] = alias,
                ["type"] = "select",
                ["use"] = new[] { provider },
                ["filter"] = @"\A" + Regex.Escape(server.Name).Replace(((char)96).ToString(), @"\x60", StringComparison.Ordinal) + @"\z",
                ["exclude-type"] = ExcludedTypes,
                ["empty-fallback"] = "REJECT"
            });
            return alias;
        }

        void AddHealthGroup(string alias, string[] members, string strategy = "fallback", string? testUrl = null,
            int interval = 120, int? tolerance = null)
        {
            var address = Uri.TryCreate(testUrl, UriKind.Absolute, out var url) &&
                url.Scheme is "http" or "https" && url.Host.Length > 0 && url.UserInfo.Length == 0 ? url.AbsoluteUri : HealthUrl;
            config.ExpectedGroups.Add(alias, members);
            var group = new Dictionary<string, object>
            {
                ["name"] = alias, ["type"] = strategy, ["proxies"] = members,
                ["url"] = address, ["expected-status"] = address == HealthUrl ? "204" : "*", ["interval"] = interval,
                ["timeout"] = 5000, ["lazy"] = false, ["empty-fallback"] = "REJECT"
            };
            if (tolerance is int milliseconds)
                group["tolerance"] = milliseconds;
            groups.Add(group);
        }

        string ResolveTarget(RouteTarget target) => target.Kind switch
        {
            RouteKind.Main => Main,
            RouteKind.Server => AddServer(target.Server!),
            RouteKind.Direct => "DIRECT",
            RouteKind.Reject => "REJECT",
            RouteKind.SubscriptionGroup => target.Category is { } category && category.Source == source &&
                subscription?.Groups.ContainsKey(category.Name) == true ? ResolvePolicy(category.Name) : "REJECT",
            _ => throw new IOException("Неизвестное направление маршрута.")
        };

        void AddRule(string match, RouteTarget target) => WriteRule(match, ResolveTarget(target));

        void WriteRule(string match, string outbound, string modifier = "")
        {
            config.Rules.Add($"{match},{outbound}{modifier}");
            // Mihomo skips a matched adapter without UDP support and continues to the next rule.
            if (outbound is not ("DIRECT" or "REJECT" or "REJECT-DROP"))
                config.Rules.Add($"{match},REJECT{modifier}");
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private void SetDns(string outbound, NetworkOptions? options = null)
    {
        options ??= new();
        var (resolvers, policies) = options.ParseDns(BootstrapDns, outbound);
        configuration["dns"] = new Dictionary<string, object>
        {
            ["enable"] = true,
            ["ipv6"] = true,
            ["enhanced-mode"] = options.FakeIp ? "fake-ip" : "redir-host",
            ["fake-ip-range6"] = "fd00:198:18::1/64",
            ["fake-ip-filter"] = new[] { "*.local", "localhost" },
            ["default-nameserver"] = new[] { BootstrapDns },
            ["proxy-server-nameserver"] = new[] { BootstrapDns },
            ["nameserver"] = resolvers,
            ["nameserver-policy"] = policies
        };
    }

    private static string Matcher(RouteRule rule)
    {
        string value = rule.Value;
        return rule.Match switch
        {
            RouteMatch.Application when value.Contains(',') || (rule.Protocol != RouteProtocol.Any && value.IndexOfAny(['(', ')']) >= 0) =>
                @"PROCESS-PATH-REGEX,\A" + Regex.Escape(value).Replace(@"\(", @"\x28").Replace(")", @"\x29").Replace(",", @"\x2c") + @"\z",
            RouteMatch.Application => $"PROCESS-PATH,{value}",
            RouteMatch.Domain => $"DOMAIN,{value}",
            RouteMatch.DomainSuffix => $"DOMAIN-SUFFIX,{value}",
            RouteMatch.Address when value.Contains(':') => $"IP-CIDR6,{value}/128",
            RouteMatch.Address => $"IP-CIDR,{value}/32",
            RouteMatch.Subnet when value.Contains(':') => $"IP-CIDR6,{value}",
            RouteMatch.Subnet => $"IP-CIDR,{value}",
            RouteMatch.Port => $"DST-PORT,{value}",
            RouteMatch.GeoIp => $"GEOIP,{value}",
            RouteMatch.GeoSite => $"GEOSITE,{value}",
            _ => throw new IOException("Неизвестное условие правила.")
        };
    }
}

