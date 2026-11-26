using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VPNDesktop;

if (args.Contains("--ui", StringComparer.Ordinal))
{
    await UiSmoke.RunAsync();
    return;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
var token = timeout.Token;
string scratch = Path.Combine(Path.GetTempPath(), "VPNDesktop-routing-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);

try
{
    var oldSettings = JsonSerializer.Deserialize<ClientSettings>("""{"Tun":true,"Repository":"another/project","KillSwitch":true}""");
    string trafficMain = Path.Combine(scratch, "TrafficMain.exe");
    string trafficHelper = Path.Combine(scratch, "TrafficHelper.exe");
    try
    {
        File.Copy(typeof(MainWindow).Assembly.Location, trafficMain);
        File.Copy(typeof(MainWindow).Assembly.Location, trafficHelper);
        var mainApp = TrafficApplication.Resolve("TrafficMain.exe", trafficMain);
        var helperApp = TrafficApplication.Resolve("TrafficHelper.exe", trafficHelper);
        Require(mainApp.Key == helperApp.Key && mainApp.Name == "Linkway" &&
            TrafficApplication.Resolve("svchost.exe", "").Name == "Службы Windows",
            "Traffic groups executables with the same application metadata and names Windows services");
    }
    finally
    {
        File.Delete(trafficMain);
        File.Delete(trafficHelper);
    }
    Require(oldSettings is { Tun: true, KillSwitch: true, ExperimentalUpdates: false, CloseAction: WindowCloseAction.Ask },
        "Existing settings migrate to the stable update channel");
    var closeSettings = oldSettings! with { CloseAction = WindowCloseAction.KeepVpn };
    Require(JsonSerializer.Deserialize<ClientSettings>(JsonSerializer.Serialize(closeSettings))?.CloseAction == WindowCloseAction.KeepVpn,
        "The default action when closing Linkway survives saving and loading");
    var releases = JsonSerializer.SerializeToElement(new[]
    {
        Release("1.3.0-beta.2", prerelease: true), Release("1.2.0"), Release("1.1.0"),
        Release("1.3.0-beta.10", prerelease: true), Release("1.4.0", draft: true),
        Release("1.5.0", withDigest: false), Release("1.6.0-beta.1")
    });
    var stable = GitHubUpdates.ReadReleases(releases, false).ToArray();
    Require(stable.Select(build => build.Version.ToString()).SequenceEqual(["1.2.0", "1.1.0"]),
        "Stable channel includes older stable builds and excludes drafts, prereleases and archives without checksums");
    var experimental = GitHubUpdates.ReadReleases(releases, true).OrderByDescending(build => build.Version).ToArray();
    Require(experimental.Select(build => build.Version.ToString()).SequenceEqual(["1.3.0-beta.10", "1.3.0-beta.2", "1.2.0", "1.1.0"]),
        "Experimental channel includes older builds and sorts numeric prerelease identifiers");
    var earlier = stable.Single(build => build.Version == UpdateVersion.Parse("1.1.0"));
    Require(earlier.Download.AbsolutePath.EndsWith("/v1.1.0/VPNDesktop-win-x64.zip", StringComparison.Ordinal),
        "An older selection retains its exact archive URL");
    Require(UpdateVersion.Parse("1.2.0")!.CompareTo(UpdateVersion.Parse("1.2.0-rc.1")) > 0,
        "A stable release supersedes a prerelease of the same version");

    var kernels = MihomoVersions.ReadReleases(JsonSerializer.SerializeToElement(new[]
    {
        KernelRelease("v1.19.33"), KernelRelease("v1.19.34", variant: "v1"),
        KernelRelease("v1.19.32"), KernelRelease("v1.20.0", prerelease: true),
        KernelRelease("v1.20.1", invalidOrigin: true), KernelRelease("v1.20.2", withDigest: false)
    }));
    Require(kernels.Select(item => item.Version.ToString(3)).SequenceEqual(["1.19.34", "1.19.33"]) &&
        kernels[0].AssetName.Contains("-amd64-v1-", StringComparison.Ordinal),
        "Kernel selection accepts official supported Windows x64 releases with checksums");

    static object KernelRelease(string tag, string variant = "compatible", bool prerelease = false,
        bool invalidOrigin = false, bool withDigest = true)
    {
        string name = $"mihomo-windows-amd64-{variant}-{tag}.zip";
        return new
        {
            tag_name = tag, prerelease, draft = false,
            assets = new[]
            {
                new { name, state = "uploaded", digest = withDigest ? "sha256:" + new string('a', 64) : null,
                    size = 1234, browser_download_url = $"https://github.com/{(invalidOrigin ? "other" : "MetaCubeX")}/mihomo/releases/download/{tag}/{name}" }
            }
        };
    }


    static object Release(string version, bool prerelease = false, bool draft = false, bool withDigest = true)
    {
        var tag = $"v{version}";
        return new
        {
            tag_name = tag,
            prerelease,
            draft,
            assets = new[]
            {
                new
                {
                    name = "VPNDesktop-win-x64.zip", state = "uploaded", size = 1024,
                    digest = withDigest ? "sha256:" + new string('0', 64) : null,
                    browser_download_url = $"{GitHubUpdates.ReleasesUrl}/download/{tag}/VPNDesktop-win-x64.zip"
                }
            }
        };
    }

    await using var primary = new HttpEndpoint(IPAddress.Loopback, "primary");
    await using var secondary = new HttpEndpoint(IPAddress.Loopback, "secondary");
    await using var direct = new HttpEndpoint(IPAddress.Loopback, "direct");
    await using var dead = new HttpEndpoint(IPAddress.Loopback, "dead");
    int deadPort = dead.Port;
    await dead.DisposeAsync();

    Profile first = Source("Same name", primary.Port);
    Profile second = Source("Same name", secondary.Port);
    await HostedSessionChecks.RunAsync(scratch, first, second, token);

    var serverDisplay = new ServerChoice(first, first.Nodes[0]);
    serverDisplay.SetCheckState(ServerCheckState.Available, 47);
    Require(serverDisplay.StatusText == "Доступен", "Response time is hidden by default");
    serverDisplay.ShowLatency = true;
    Require(serverDisplay.StatusText == "47 мс", "Developer mode shows the measured response time");
    Profile unavailable = Source("Unavailable", deadPort);
    var sources = new[] { first, second, unavailable };
    var secondServer = new RouteServer(RouteServer.SourceKey(second), "Same name");
    var deadServer = new RouteServer(RouteServer.SourceKey(unavailable), "Unavailable");
    var group = new RouteGroup(Guid.NewGuid(), "Test group", [secondServer, deadServer]);
    var draft = RoutingProfile.Create("Integration") with
    {
        Default = new(RouteKind.Direct),
        Rules =
        [
            new(RouteMatch.Domain, "primary.test", RouteProtocol.Any, new(RouteKind.Main)),
            new(RouteMatch.Domain, "example.org", RouteProtocol.Any, new(RouteKind.Server, secondServer)),
            new(RouteMatch.DomainSuffix, "secondary.test", RouteProtocol.Tcp, new(RouteKind.Server, secondServer)),
            new(RouteMatch.Domain, "unavailable.test", RouteProtocol.Any, new(RouteKind.Server, deadServer)),
            new(RouteMatch.Domain, "blocked.test", RouteProtocol.Any, new(RouteKind.Reject)),
            new(RouteMatch.Domain, "group.test", RouteProtocol.Any, new(RouteKind.Group, Group: group.Id)),
            new(RouteMatch.Subnet, "2001:db8::/32", RouteProtocol.Tcp, new(RouteKind.Main)),
            new(RouteMatch.Application, @"C:\Program Files (x86)\Test\app,(.exe", RouteProtocol.Udp, new(RouteKind.Reject)),
            new(RouteMatch.Domain, "disabled.test", RouteProtocol.Any, new(RouteKind.Main), Enabled: false)
        ],
        Groups = [group]
    };
    string export = Path.Combine(scratch, "routing.json");
    RoutingProfiles.Export(export, draft);
    var imported = RoutingProfiles.Import(export);
    Require(imported.Id != draft.Id && imported.Groups.Length == 0 &&
        imported.Rules.SequenceEqual(draft.Rules.Select(rule => rule.Target.Kind == RouteKind.Group
            ? rule with { Target = new(RouteKind.Server, secondServer) } : rule)), "Profile JSON and migration to concrete server");
    var bad = File.ReadAllText(export).Replace("\"Tcp\"", "\"UNKNOWN\"");
    File.WriteAllText(export, bad);
    ExpectInvalid(() => RoutingProfiles.Import(export), "Unknown enum in imported JSON");
    ExpectInvalid(() => RoutingProfiles.Normalize(RouteMatch.Port, "0-80"), "Invalid port");
    ExpectInvalid(() => RoutingProfiles.Normalize(RouteMatch.Domain, "example.org,REJECT"), "Rule delimiter injection");
    ExpectInvalid(() => RoutingProfiles.Normalize(RouteMatch.Subnet, "10.0.0.0/33"), "Invalid CIDR");
    Require(RoutingProfiles.Normalize(RouteMatch.DomainSuffix, "*.пример.рф.") == "xn--e1afmkfd.xn--p1ai", "IDN domain");
    Require(ApplicationInventory.Running().Length > 0, "Running application inventory");
    Require(ApplicationInventory.Installed().Length > 0, "Installed application inventory");

    string geo = Path.Combine(scratch, "geodata");
    Directory.CreateDirectory(geo);
    var localInGeoIp = Field(2, [.. Field(1, [127, 0, 0, 0]), 16, 8]);
    var geoHost = Field(2, [8, 3, .. TextField(2, "geo.test")]);
    File.WriteAllBytes(Path.Combine(geo, "GeoIP.dat"),
        [.. Field(1, [.. TextField(1, "CN"), .. localInGeoIp]), .. Field(1, [.. TextField(1, "RU"), .. localInGeoIp])]);
    File.WriteAllBytes(Path.Combine(geo, "GeoSite.dat"),
        [.. Field(1, [.. TextField(1, "CN"), .. geoHost]), .. Field(1, [.. TextField(1, "TEST"), .. geoHost])]);

    await using var core = await Mihomo.StartAsync(first, "Same name", sources, draft, false, token, scratch);
    Require(await Request("primary.test") == "primary", "Main route");
    Require(await Request("a.secondary.test") == "secondary", "Parallel route from another source with an identical node name");
    Require(await Request("group.test") == "secondary", "Group route via its first member");
    Require(await Request("blocked.test") != "direct", "Explicit reject");
    Require(await Request("unavailable.test") != "direct", "Unavailable VPN stays closed");
    Require(await Request("127.0.0.1") == "direct", "IPv4 direct");
    Require(await Request("[2001:db8::8]:443", explicitPort: true) == "primary", "IPv6 subnet dispatch to VPN");

    using (var connection = await OpenRequest("a.secondary.test", "/hold"))
    {
        await secondary.HoldStarted.Task.WaitAsync(token);
        var snapshot = await core.GetConnectionsAsync(token);
        Require(snapshot.Any(item => item.Host == "a.secondary.test" && item.Network == "tcp" &&
            item.Route.Contains("Same name") && item.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
            "Active connections with process, hostname, protocol and actual server");
    }

    using (var connection = await OpenRequest("example.org", "/hold"))
    {
        int port = ((IPEndPoint)connection.Client.LocalEndPoint!).Port;
        var observed = (Host: "", Kind: "");
        for (int attempt = 0; attempt < 20 && observed.Host.Length == 0; attempt++)
        {
            var routes = await core.GetDiagnosticRoutesAsync(new Dictionary<int, byte> { [port] = 0 }, token);
            if (routes.Length > 0)
                observed = routes.Single();
            else
                await Task.Delay(50, token);
        }
        Require(observed == ("example.org", "через прокси (правило маршрутизации)") &&
            (await core.GetDiagnosticRoutesAsync(new Dictionary<int, byte>(), token)).Length == 0,
            "Diagnostic routing recognizes its own test flow and hides the selected server");
    }
    Require((await core.DescribeDiagnosticConfigurationAsync(token)).Contains("наборов загружено 0 из 0"),
        "Diagnostic configuration reports verified rule counts");
    Require(MainWindow.DescribeHttpFailure(HttpRequestError.ProxyTunnelError, null) == "ошибка HTTP CONNECT" &&
        MainWindow.DescribeHttpFailure(HttpRequestError.SecureConnectionError, null) == "ошибка TLS" &&
        MainWindow.DescribeHttpFailure(HttpRequestError.Unknown, HttpStatusCode.BadGateway) == "HTTP 502",
        "Network diagnostics separate proxy, TLS and HTTP status without raw exception text");

    using var echo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    int udpPort = ((IPEndPoint)echo.Client.LocalEndPoint!).Port;
    using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var socks = await TcpClientAsync(core.Port);
    var control = socks.GetStream();
    await control.WriteAsync(new byte[] { 5, 1, 0 }, token);
    var negotiation = new byte[2];
    await control.ReadExactlyAsync(negotiation, token);
    Require(negotiation.SequenceEqual(new byte[] { 5, 0 }), "SOCKS5 on the local proxy port");
    await control.WriteAsync(new byte[] { 5, 3, 0, 1, 127, 0, 0, 1, 0, 0 }, token);
    var association = new byte[10];
    await control.ReadExactlyAsync(association, token);
    Require(association[1] == 0 && association[3] == 1, "SOCKS5 UDP association");
    int relay = association[8] * 256 + association[9];
    var udpGuard = new RouteRule(RouteMatch.Port, udpPort.ToString(), RouteProtocol.Any, new(RouteKind.Main));
    var withUdpGuard = draft with { Rules = [udpGuard, .. draft.Rules] };
    await core.ApplyRoutingAsync(first, "Same name", sources, withUdpGuard, false, () => { }, token);
    await SendDatagram("blocked");
    using (var brief = CancellationTokenSource.CreateLinkedTokenSource(token))
    {
        brief.CancelAfter(700);
        try
        {
            await echo.ReceiveAsync(brief.Token);
            throw new InvalidOperationException("UDP leaked from a TCP-only VPN route to DIRECT.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Console.WriteLine("PASS UDP cannot reach a lower-priority DIRECT route");
        }
    }

    var deadOnPort = new RouteRule(RouteMatch.Port, direct.Port.ToString(), RouteProtocol.Tcp, new(RouteKind.Server, deadServer));
    var next = draft with { Rules = [.. draft.Rules, deadOnPort] };
    bool saved = false;
    await core.ApplyRoutingAsync(first, "Same name", sources, next, false, () => saved = true, token);
    Require(saved && !core.Exited, "Configuration commit in the same running core");
    Require(await Request("127.0.0.1") != "direct", "Unreachable VPN cannot reach a local origin via DIRECT");
    await SendDatagram("allowed");
    var received = await echo.ReceiveAsync(token);
    Require(Encoding.ASCII.GetString(received.Buffer) == "allowed", "Explicit direct UDP after hot apply");

    var blockSecondary = new RouteRule(RouteMatch.DomainSuffix, "secondary.test", RouteProtocol.Any, new(RouteKind.Reject));
    var newDraft = draft with { Rules = [blockSecondary, .. draft.Rules] };
    await core.ApplyRoutingAsync(first, "Same name", sources, newDraft, false, () => { }, token);
    var response = await Request("a.secondary.test");
    Require(response != "secondary", "Hot apply changes priority on the same listener");

    int failures = 0;
    try
    {
        await core.ApplyRoutingAsync(first, "Same name", sources, draft, false,
            () => throw new IOException("Injected persistence failure"), token);
    }
    catch (IOException error) when (error.Message.Contains("Восстановлена"))
    {
        failures++;
    }
    Require(failures == 1 && await Request("a.secondary.test") != "secondary" && await Request("primary.test") == "primary",
        "Failed persistence restores previous rules with the same listener");

    var wrongContent = second with { Content = first.Content.Replace("Same name", "Different node") };
    int missing = 0;
    try
    {
        await core.ApplyRoutingAsync(first, "Same name", [first, wrongContent, unavailable], draft, false,
            () => throw new Exception("An unverified config must never be persisted"), token);
    }
    catch (IOException error) when (error.Message.Contains("Восстановлена"))
    {
        missing++;
    }
    Require(missing == 1 && await Request("a.secondary.test") != "secondary" && await Request("primary.test") == "primary",
        "Missing loaded server rolls back live configuration");

    var paths = Directory.GetFiles(Directory.GetDirectories(scratch, "session-*").Single(), "route-nodes-*.txt");
    Require(paths.Length == 3, "Stale session provider files pruned");
    var geoRules = draft with
    {
        Rules =
        [
            new(RouteMatch.GeoSite, "test", RouteProtocol.Tcp, new(RouteKind.Server, secondServer)),
            new(RouteMatch.GeoIp, "ru", RouteProtocol.Any, new(RouteKind.Main)),
            .. draft.Rules
        ]
    };
    await core.ApplyRoutingAsync(first, "Same name", sources, geoRules, false, () => { }, token);
    Require(await Request("geo.test") == "secondary" && await Request("127.0.0.1") == "primary", "GEOIP and GEOSITE with fixture databases");

    var invalidGeo = geoRules with { Rules = [new(RouteMatch.GeoSite, "not-in-fixture", RouteProtocol.Any, new(RouteKind.Reject))] };
    bool invalidGeoRejected = false;
    try
    {
        await core.ApplyRoutingAsync(first, "Same name", sources, invalidGeo, false,
            () => throw new InvalidOperationException("Invalid GeoSite must not be persisted"), token);
    }
    catch (IOException) { invalidGeoRejected = true; }
    Require(invalidGeoRejected && await Request("geo.test") == "secondary", "Invalid GEO rule leaves the existing rules active");

    string yaml = $$"""
        proxies:
          - {name: Primary, type: http, server: 127.0.0.1, port: {{primary.Port}}}
          - {name: Secondary, type: http, server: 127.0.0.1, port: {{secondary.Port}}}
          - {name: 'Без VPN', type: direct}
        proxy-groups:
          - name: '🚫 Недоступные сайты'
            type: select
            proxies: [Primary, Fastest]
            default-selected: Fastest
          - {name: Fastest, type: url-test, proxies: [Secondary, Primary]}
          - {name: PROXY, type: select, proxies: ['🚫 Недоступные сайты']}
          - {name: Internal, type: select, hidden: true, proxies: [Secondary]}
          - name: YouTube
            type: select
            proxies: ['🚫 Недоступные сайты', Primary, REJECT]
          - name: Steam
            type: select
            proxies: [Primary, Secondary]
          - name: 'Остальные сайты'
            type: select
            proxies: ['Без VPN', Primary]
        rule-providers:
          steam:
            type: inline
            behavior: classical
            payload:
              - DOMAIN,store.steam.test
              - PROCESS-NAME,steam.exe
        rules:
          - DOMAIN-SUFFIX,youtube.test,YouTube
          - AND,((NETWORK,tcp),(RULE-SET,steam)),Steam
          - DOMAIN,proxy.test,PROXY
          - DOMAIN,internal.test,Internal
          - MATCH,Остальные сайты
        """;
    var fullSource = new Profile(Guid.NewGuid(), "Full subscription", null, yaml,
        [new("Primary", "Http"), new("Secondary", "Http")], "Primary");
    var parsed = SubscriptionRouting.Read(fullSource);
    var encoded = fullSource with { Content = Convert.ToBase64String(Encoding.UTF8.GetBytes(yaml)) };
    Require(parsed?.Groups.Count == 7 && parsed.Rules.Length == 5 && SubscriptionRouting.Read(encoded)?.Groups.Count == 7,
        "Full YAML and Base64 subscriptions retain groups and conditions");
    Require(parsed!.Groups.Keys.Where(parsed.IsVisibleGroup).SequenceEqual(["YouTube", "Steam", "Остальные сайты"]) &&
        parsed.IsServiceGroup("PROXY") && parsed.IsServiceGroup("Fastest") && parsed.IsServiceGroup("Internal"),
        "Only categories that expose manual selection appear in the interface");
    var subscriptionUrl = new Uri("https://example.test/sub/id?key=value");
    Require(Subscription.RoutingAddresses(subscriptionUrl).Select(uri => uri.PathAndQuery)
        .SequenceEqual(["/sub/id/mihomo?key=value", "/sub/id/clash?key=value"]) &&
        Subscription.RoutingAddresses(new Uri("https://example.test/sub/id/mihomo")).Single().AbsolutePath == "/sub/id/clash" &&
        Subscription.RoutingAddresses(new Uri("https://example.test/sub/id/json")).First().AbsolutePath == "/sub/id/mihomo",
        "Format endpoints preserve the subscription address and replace explicit format suffixes");
    const string nodesOnly = "proxies: []";
    var formatRequests = new List<string>();
    var fullDownload = await Subscription.DownloadWithRoutingAsync(subscriptionUrl, token, true, null, (uri, ct, registerDevice) =>
    {
        ct.ThrowIfCancellationRequested();
        formatRequests.Add(uri.AbsolutePath);
        if (!registerDevice || uri.Host != subscriptionUrl.Host || uri.Query != subscriptionUrl.Query)
            throw new InvalidOperationException("Subscription request lost its device context or source URL.");
        if (uri.AbsolutePath.EndsWith("/mihomo"))
            throw new IOException("Format not available.");
        return Task.FromResult(new Subscription.Downloaded(uri.AbsolutePath.EndsWith("/clash") ? yaml : nodesOnly,
            uri == subscriptionUrl ? "Subscription title" : null));
    });
    Require(SubscriptionRouting.HasRules(fullDownload.Content) && fullDownload.Title == "Subscription title" &&
        formatRequests.SequenceEqual(["/sub/id", "/sub/id/mihomo", "/sub/id/clash"]),
        "A partial response tries both formats and preserves the original title");
    var encodedRules = await Subscription.DownloadWithRoutingAsync(subscriptionUrl, token, false, null, (uri, _, _) =>
        uri == subscriptionUrl ? Task.FromResult(new Subscription.Downloaded(encoded.Content, null))
            : throw new InvalidOperationException("A complete encoded response needs no fallback."));
    Require(SubscriptionRouting.HasRules(encodedRules.Content) && SubscriptionRouting.HasRules("rules: ['MATCH,DIRECT']"),
        "Encoded subscriptions and YAML rules without groups are recognized");
    var partial = await Subscription.DownloadWithRoutingAsync(subscriptionUrl, token, false, null,
        (_, _, _) => Task.FromResult(new Subscription.Downloaded(nodesOnly, null)));
    Require(partial.Content == nodesOnly, "Initially importing a server-only subscription still works");
    try
    {
        await Subscription.DownloadWithRoutingAsync(subscriptionUrl, token, false, yaml,
            (_, _, _) => Task.FromResult(new Subscription.Downloaded(nodesOnly, null)));
        throw new InvalidOperationException("The saved full subscription would have been discarded.");
    }
    catch (IOException error) when (error.Message.Contains("Сохранена", StringComparison.Ordinal))
    {
        Console.WriteLine("PASS A partial update preserves the last complete subscription");
    }
    const string providerTitle = "Мой VPN ⚡";
    string encodedTitle = "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(providerTitle));
    Require(Subscription.ReadTitle(encodedTitle) == providerTitle && Subscription.ReadTitle("  Demo VPN  ") == "Demo VPN" &&
        Subscription.ReadTitle("base64:Invalid!") is null && Subscription.ReadTitle("base64:/w==") is null,
        "Provider subscription title is decoded and malformed titles are ignored");
    var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    var cabinetPlans = JsonSerializer.Deserialize<CabinetApi.Plan[]>("""
        [{"id":101,"tariff_id":1,"tariff_name":"Стандартный","is_active":true},
         {"id":102,"tariff_id":2,"tariff_name":"Обход БС","is_active":true},
         {"id":103,"tariff_id":3,"is_active":true},
         {"id":104,"is_active":true}]
        """, jsonOptions);
    Require(cabinetPlans is not null && cabinetPlans.All(plan => plan.CanConnect) &&
        cabinetPlans.Select(plan => plan.DisplayName).SequenceEqual(["Стандартный", "Обход БС", null, null]),
        "Cabinet subscriptions use tariff names without displaying internal IDs");
    var fakeInstallation = Guid.NewGuid();
    string deviceId = SubscriptionDevice.MakeIdentifier(fakeInstallation, "sub.example.test");
    Require(deviceId.Length == 64 && deviceId.All(char.IsAsciiHexDigit) &&
        deviceId == SubscriptionDevice.MakeIdentifier(fakeInstallation, "SUB.EXAMPLE.TEST") &&
        deviceId != SubscriptionDevice.MakeIdentifier(fakeInstallation, "other.example.test"),
        "Device identifier is stable and scoped to the provider host");
    using var refusal = new HttpResponseMessage(HttpStatusCode.OK);
    refusal.Headers.Add("x-hwid-max-devices-reached", "true");
    ExpectInvalid(() => SubscriptionDevice.CheckHeaders(refusal.Headers), "Device limit rejected even on HTTP 200");
    refusal.Headers.Clear();
    refusal.Headers.Add("x-hwid-not-supported", "true");
    ExpectInvalid(() => SubscriptionDevice.CheckHeaders(refusal.Headers), "Unsupported client status is detected");
    Require(!new Node("🚫 HWID Error", "Vless").IsServer && !new Node("App not supported", "Vless").IsServer &&
        new Node("Германия 1", "Vless").IsServer, "Provider HWID placeholders never appear as servers");

    var network = new NetworkOptions(ServerStrategy.Fallback, FakeIp: false, RefreshGeoData: true,
        Resolvers: "tls://1.1.1.1", DomainResolvers: "example.test=https://1.0.0.1/dns-query",
        RuleSets: "https://rules.example.test/domain.txt | domain | text | REJECT");
    using (var payload = JsonDocument.Parse(RoutingConfig.Build(1234, 1235, "test", fullSource, "Primary",
        [fullSource], RoutingProfile.Create("Network options"), false, network).Payload(true)))
    {
        var dns = payload.RootElement.GetProperty("dns");
        Require(dns.GetProperty("enhanced-mode").GetString() == "redir-host" &&
            dns.GetProperty("nameserver")[0].GetString() == "tls://1.1.1.1#VPNDesktop-Selected" &&
            dns.GetProperty("nameserver-policy").GetProperty("+.example.test").GetString() ==
                "https://1.0.0.1/dns-query#VPNDesktop-Selected", "DoT, split DoH and DNS through main group");
        Require(payload.RootElement.GetProperty("rule-providers").EnumerateObject().Any() &&
            payload.RootElement.GetProperty("rules").EnumerateArray().Any(rule => rule.GetString() ==
                "RULE-SET,VPNDesktop-UserRules-1,REJECT"), "User HTTPS rule set is managed by Mihomo");
        Require(payload.RootElement.GetProperty("tun").GetProperty("dns-hijack").GetArrayLength() == 2,
            "TUN captures UDP and TCP DNS");
    }
    ExpectInvalid(() => (network with { Resolvers = "tls://1.1.1.1#DIRECT" }).ParseDns("", RoutingConfig.Main),
        "DNS endpoint cannot change the configured outbound");
    ExpectInvalid(() => (network with { RuleSets = "http://example.test | domain | text | VPN" })
        .ParseRuleSets(RoutingConfig.Main).ToArray(), "Rule sets require HTTPS");
    foreach (var strategy in new[] { ServerStrategy.Fastest, ServerStrategy.Fallback, ServerStrategy.Balance })
    {
        var candidate = network with { Strategy = strategy, RuleSets = "" };
        await using var groupCore = await Mihomo.StartAsync(fullSource, "Primary", [fullSource],
            RoutingProfile.Create("Group check") with { UseSubscriptionRules = false }, false, token, scratch, candidate);
        var totals = await groupCore.GetTrafficSnapshotAsync(token);
        Require(totals.Download >= 0 && totals.Upload >= 0, $"Mihomo verifies {strategy} members and exposes traffic counters");
    }

    const string unicodeNode = @"🇪🇪 Сервер \uD83D\uDE80";
    string unicodeYaml = $"""
        proxies:
          - name: '{unicodeNode}'
            type: http
            server: 127.0.0.1
            port: {primary.Port}
        rules:
          - 'DOMAIN,unicode.test,{unicodeNode}'
          - 'MATCH,DIRECT'
        """;
    var unicodeSource = new Profile(Guid.NewGuid(), "Unicode subscription", null, unicodeYaml,
        [new Node(unicodeNode, "Http")], unicodeNode);
    var unicodeConfig = RoutingConfig.Build(1234, 1235, "test", unicodeSource, unicodeNode,
        [unicodeSource], null, false).Payload(false);
    using (var payload = JsonDocument.Parse(unicodeConfig))
        Require(unicodeConfig.Contains("🇪🇪", StringComparison.Ordinal) &&
            payload.RootElement.GetProperty("proxy-groups")[0].GetProperty("filter").GetString() ==
                @"\A" + System.Text.RegularExpressions.Regex.Escape(unicodeNode) + @"\z",
            "Mihomo payload writes Unicode scalars and preserves literal escape text");
    await using (var unicodeCore = await Mihomo.StartAsync(unicodeSource, unicodeNode, [unicodeSource], null, false, token, scratch))
        Require(!unicodeCore.Exited, "Mihomo starts with Unicode subscription rules and server names");

    string automaticYaml = $$"""
        proxies:
          - {name: Unavailable, type: http, server: 127.0.0.1, port: {{deadPort}}}
          - {name: Working, type: http, server: 127.0.0.1, port: {{primary.Port}}}
        proxy-groups:
          - name: Automatic
            type: url-test
            include-all: true
            url: http://health.test/health
          - {name: General, type: select, proxies: [Automatic]}
        rules: ['DOMAIN,web.test,General', 'MATCH,DIRECT']
        """;
    var automaticSource = new Profile(Guid.NewGuid(), "Automatic subscription", null, automaticYaml,
        [new Node("Unavailable", "Http"), new Node("Working", "Http")], "Working");
    await using (var automaticCore = await Mihomo.StartAsync(automaticSource, "Working", [automaticSource], null, false, token, scratch))
    {
        using var handler = new HttpClientHandler { Proxy = new WebProxy(IPAddress.Loopback.ToString(), automaticCore.Port) };
        using var browser = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        string? result = null;
        for (int attempt = 0; attempt < 20 && result is null; attempt++)
        {
            try { result = await browser.GetStringAsync("http://web.test/page", token); }
            catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(100, token);
            }
        }
        Require(result == "primary", "Subscription URL-test selects the working second node through nested default routing");
    }

    var subscriptionProfile = RoutingProfile.Create("From subscription");
    await core.ApplyRoutingAsync(fullSource, "Primary", [fullSource], subscriptionProfile, false, () => { }, token);
    Require(await Request("video.youtube.test") == "primary" && await Request("store.steam.test") == "primary" &&
        await Request("proxy.test") == "primary" && await Request("internal.test") == "secondary" &&
        await Request("127.0.0.1") == "primary", "Nested selector and classical RULE-SET preserve the Linkway default route");

    string sourceKey = RouteServer.SourceKey(fullSource);
    var changedGroup = subscriptionProfile with { SubscriptionChoices = [new(sourceKey, "YouTube", "Primary")] };
    RoutingProfiles.Export(export, changedGroup);
    Require(RoutingProfiles.Import(export).SubscriptionChoices?.Single() == changedGroup.SubscriptionChoices[0],
        "Subscription selector choice survives export and import");
    await core.ApplyRoutingAsync(fullSource, "Primary", [fullSource], changedGroup, false, () => { }, token);
    Require(await Request("video.youtube.test") == "primary", "Subscription group selection applies live");
    var directFallback = changedGroup with { Default = new(RouteKind.Direct) };
    await core.ApplyRoutingAsync(fullSource, "Primary", [fullSource], directFallback, false, () => { }, token);
    Require(await Request("127.0.0.1") == "direct", "Explicit routing profile default overrides subscription MATCH");

    var legacyNetwork = JsonSerializer.Deserialize<NetworkOptions>(
        """{"Strategy":1,"UnavailableSitesServer":{"Source":"old","Name":"Primary"}}""");
    Require(legacyNetwork?.Strategy == ServerStrategy.Fastest,
        "Legacy settings load after removing the independent server assignment");
    var selectedServerProfile = subscriptionProfile with { Default = new(RouteKind.Direct) };
    int originalPort = core.Port;
    bool serverSaved = false;
    await core.ApplyRoutingAsync(fullSource, "Secondary", [fullSource], selectedServerProfile, false,
        () => serverSaved = true, token, legacyNetwork);
    Require(serverSaved && !core.Exited && core.Port == originalPort &&
        await Request("video.youtube.test") == "secondary" && await Request("proxy.test") == "secondary" &&
        await Request("store.steam.test") == "primary" && await Request("127.0.0.1") == "direct",
        "Live server switch updates unavailable sites on the same listener and preserves other configured routes");

    bool serverSwitchReverted = false;
    try
    {
        await core.ApplyRoutingAsync(fullSource, "Primary", [fullSource], null, false,
            () => throw new IOException("Injected server selection save failure"), token, legacyNetwork);
    }
    catch (IOException error) when (error.Message.Contains("Восстановлена")) { serverSwitchReverted = true; }
    Require(serverSwitchReverted && !core.Exited && await Request("video.youtube.test") == "secondary",
        "A failed live server switch restores the previous server with the same running core");

    var manual = changedGroup with
    {
        Rules = [new(RouteMatch.Domain, "video.youtube.test", RouteProtocol.Any, new(RouteKind.Reject))],
        Default = new(RouteKind.Reject)
    };
    await core.ApplyRoutingAsync(fullSource, "Primary", [fullSource], manual, false, () => { }, token);
    Require(await Request("video.youtube.test") != "primary" && await Request("127.0.0.1") != "direct" &&
        await Request("store.steam.test") == "primary", "Manual rules and fallback take precedence over subscription");

    var invalidFeed = fullSource with { Content = yaml.Replace("DOMAIN,store.steam.test", "INVALID,store.steam.test")
        .Replace("PROCESS-NAME,steam.exe", "INVALID,steam.exe") };
    bool invalidFeedRejected = false;
    try
    {
        await core.ApplyRoutingAsync(invalidFeed, "Primary", [invalidFeed], subscriptionProfile, false,
            () => throw new InvalidOperationException("Empty rules from the provider must not be persisted"), token);
    }
    catch (IOException) { invalidFeedRejected = true; }
    Require(invalidFeedRejected && await Request("127.0.0.1") != "direct",
        "Invalid or empty rule provider is rejected before replacing live rules");

    var localOnly = manual with { UseSubscriptionRules = false };
    await core.ApplyRoutingAsync(fullSource, "Primary", [fullSource], localOnly, false, () => { }, token);
    Require(await Request("store.steam.test") != "primary", "Subscription routes can be disabled in a routing profile");

    var customGroup = new RouteGroup(Guid.NewGuid(), "External VPN", [secondServer]);
    var customTarget = new RouteTarget(RouteKind.Group, Group: customGroup.Id);
    var categoryTarget = new RouteTarget(RouteKind.SubscriptionGroup, Category: new(sourceKey, "YouTube"));
    var custom = subscriptionProfile with
    {
        Groups = [customGroup],
        SubscriptionChoices = [new(sourceKey, "YouTube", null, customTarget)],
        Rules = [new(RouteMatch.Domain, "personal.test", RouteProtocol.Any, categoryTarget)]
    };
    RoutingProfiles.Export(export, custom);
    var customImported = RoutingProfiles.Import(export);
    Require(customImported.Groups.Length == 0 &&
        customImported.SubscriptionChoices?.Single().Target == new RouteTarget(RouteKind.Server, secondServer) &&
        customImported.Rules.Single().Target == categoryTarget, "Legacy category group becomes its first server on import");
    await core.ApplyRoutingAsync(fullSource, "Primary", [fullSource, second], customImported, false, () => { }, token);
    Require(await Request("video.youtube.test") == "secondary" && await Request("personal.test") == "secondary",
        "Both subscription and personal rules use the category's selected server across sources");

    var withDirect = custom with
    {
        Default = new(RouteKind.Direct),
        SubscriptionChoices = [new(sourceKey, "YouTube", null, new(RouteKind.Server, secondServer))],
        UseSubscriptionRules = false
    };
    await core.ApplyRoutingAsync(fullSource, "Primary", [fullSource, second], withDirect, false, () => { }, token);
    Require(await Request("personal.test") == "secondary", "Explicit personal category rule works with subscription rules disabled");
    await core.ApplyRoutingAsync(first, "Same name", sources, withDirect, false, () => { }, token);
    Require(await Request("personal.test") != "direct", "Missing category fails closed");
    await VerifyAutomaticFailoverAsync(scratch);
    Console.WriteLine("Routing integration checks passed.");

    async Task<string> Request(string host, bool explicitPort = false)
    {
        try
        {
            using var request = await OpenRequest(host, "/", explicitPort);
            using var reader = new StreamReader(request.GetStream());
            var all = await reader.ReadToEndAsync(token);
            int body = all.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return body >= 0 ? all[(body + 4)..] : all;
        }
        catch (IOException) when (!token.IsCancellationRequested) { return ""; }
    }

    async Task<TcpClient> OpenRequest(string host, string path, bool explicitPort = false)
    {
        var client = await TcpClientAsync(core.Port);
        string target = explicitPort ? host : $"{host}:{direct.Port}";
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
            $"GET http://{target}{path} HTTP/1.1\r\nHost: {target}\r\nConnection: close\r\n\r\n"), token);
        return client;
    }

    async Task<TcpClient> TcpClientAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, token);
        return client;
    }

    async Task SendDatagram(string value)
    {
        byte[] frame = [0, 0, 0, 1, 127, 0, 0, 1, (byte)(udpPort >> 8), (byte)udpPort, .. Encoding.ASCII.GetBytes(value)];
        await udp.SendAsync(frame, new IPEndPoint(IPAddress.Loopback, relay), token);
    }
}
finally
{
    foreach (string directory in Directory.EnumerateDirectories(scratch, "session-*"))
        Mihomo.DeleteSession(directory);
    File.Delete(Path.Combine(scratch, "routing.json"));
    string geo = Path.Combine(scratch, "geodata");
    if (Directory.Exists(geo))
    {
        File.Delete(Path.Combine(geo, "GeoIP.dat"));
        File.Delete(Path.Combine(geo, "GeoSite.dat"));
        Directory.Delete(geo);
    }
    Directory.Delete(scratch);
}

// The fixture protobuf messages fit in single-byte length-delimited fields.
static byte[] Field(int number, byte[] data) => [(byte)(number * 8 + 2), checked((byte)data.Length), .. data];
static byte[] TextField(int number, string data) => Field(number, Encoding.ASCII.GetBytes(data));

static Profile Source(string name, int port)
{
    var yaml = JsonSerializer.Serialize(new
    {
        proxies = new[] { new { name, type = "http", server = "127.0.0.1", port } }
    });
    return new(Guid.NewGuid(), name, null, yaml, [new Node(name, "Http")], name);
}

static async Task VerifyAutomaticFailoverAsync(string scratch)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var token = deadline.Token;
    await using var fast = new HttpEndpoint(IPAddress.Loopback, "fast");
    await using var reserve = new HttpEndpoint(IPAddress.Loopback, "reserve", TimeSpan.FromMilliseconds(170));
    var nodes = new[] { (Name: "Reserve", reserve.Port), (Name: "Fast", fast.Port) };
    var source = new Profile(Guid.NewGuid(), "Automatic route", null, JsonSerializer.Serialize(new
    {
        proxies = nodes.Select(node => new { name = node.Name, type = "http", server = "127.0.0.1", port = node.Port })
    }), nodes.Select(node => new Node(node.Name, "Http")).ToArray(), "Reserve");
    await using var core = await Mihomo.StartAsync(source, "Reserve", [source], null, false, token, scratch,
        new NetworkOptions(ServerStrategy.Fastest), "http://health.test/");
    using var handler = new HttpClientHandler { Proxy = new WebProxy("127.0.0.1", core.Port) };
    using var browser = new HttpClient(handler);

    await WaitForStatusAsync(status => status is { Server: "Fast", Available: 2, Total: 2, Delay: > 0 }, seconds: 25);
    Require(await browser.GetStringAsync("http://automatic.test/", token) == "fast",
        "Automatic mode routes new traffic through the fastest server instead of the first server");
    await fast.DisposeAsync();
    await WaitForStatusAsync(status => status is { Server: "Reserve", Available: 1, Total: 2, Failed: ["Fast"] });
    Require(await browser.GetStringAsync("http://automatic.test/", token) == "reserve",
        "Mihomo switches to the available reserve on the same running proxy port");
    await reserve.DisposeAsync();
    await WaitForStatusAsync(status => status is { Server: null, Available: 0, Total: 2, Failed.Length: 2 });
    Require(true, "Automatic route status reports when all servers are unavailable");

    async Task WaitForStatusAsync(Func<AutomaticRouteStatus?, bool> expected, int seconds = 65)
    {
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(token);
        phase.CancelAfter(TimeSpan.FromSeconds(seconds));
        AutomaticRouteStatus? status = null;
        try
        {
            while (true)
            {
                status = await core.ReadAutomaticRouteAsync(phase.Token);
                if (expected(status))
                    return;
                await Task.Delay(250, phase.Token);
            }
        }
        catch (OperationCanceledException) when (phase.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Automatic route did not reach the expected state. Last status: {JsonSerializer.Serialize(status)}");
        }
    }
}

static void Require(bool condition, string label)
{
    if (!condition)
        throw new InvalidOperationException(label);
    Console.WriteLine($"PASS {label}");
}

static void ExpectInvalid(Action action, string label)
{
    try { action(); }
    catch (IOException)
    {
        Console.WriteLine($"PASS {label}");
        return;
    }
    throw new InvalidOperationException(label);
}

sealed class HttpEndpoint : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly Task accept;
    private readonly string body;
    private readonly TimeSpan responseDelay;
    public int Port { get; }
    public TaskCompletionSource HoldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HttpEndpoint(IPAddress bind, string body, TimeSpan responseDelay = default)
    {
        this.body = body;
        this.responseDelay = responseDelay;
        listener = new TcpListener(bind, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accept = ListenAsync();
    }

    private async Task ListenAsync()
    {
        try
        {
            while (true)
                _ = RespondAsync(await listener.AcceptTcpClientAsync(stop.Token), stop.Token);
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }

    private async Task RespondAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            string request = await ReadHeaders();
            if (request.StartsWith("CONNECT ", StringComparison.Ordinal))
            {
                await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct);
                request = await ReadHeaders();
            }
            if (request.Contains("/hold", StringComparison.Ordinal))
            {
                HoldStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            if (responseDelay > TimeSpan.Zero)
                await Task.Delay(responseDelay, ct);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), ct);

            async Task<string> ReadHeaders()
            {
                string first = await reader.ReadLineAsync(ct) ?? "";
                while (await reader.ReadLineAsync(ct) is { Length: > 0 }) { }
                return first;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (stop.IsCancellationRequested)
            return;
        stop.Cancel();
        listener.Stop();
        await accept;
        stop.Dispose();
    }
}

