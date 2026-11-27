using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace VPNDesktop;

internal sealed partial class Mihomo : IAsyncDisposable
{
    private const string Group = RoutingConfig.Main;
    private readonly string directory;
    private readonly HttpClient api;
    private readonly Process process;
    private readonly ProcessJob job;
    private readonly int controller;
    private readonly string secret;
    private RoutingConfig currentConfig;
    private bool tunRequested;
    public int Port { get; }
    public string ExecutablePath { get; }
    public bool Exited => process.HasExited;

    private Mihomo(string directory, HttpClient api, Process process, ProcessJob job, int port,
        int controller, string secret, RoutingConfig config)
    {
        this.directory = directory;
        this.api = api;
        this.process = process;
        this.job = job;
        this.controller = controller;
        this.secret = secret;
        currentConfig = config;
        Port = port;
        ExecutablePath = process.StartInfo.FileName;
    }

    public static Task<Mihomo> StartAsync(string content, CancellationToken cancellationToken) =>
        StartAsync((port, controller, secret) => RoutingConfig.Preview(port, controller, secret, content), cancellationToken);

    public static Task<Mihomo> StartAsync(Profile source, string node, Profile[] sources, RoutingProfile? profile,
        bool bypassLan, CancellationToken cancellationToken, string? sessionRoot = null, NetworkOptions? network = null,
        string? automaticTestUrl = null) =>
        StartAsync((port, controller, secret) => RoutingConfig.Build(port, controller, secret,
            source, node, sources, profile, bypassLan, network, automaticTestUrl), cancellationToken, sessionRoot);

    internal static Task<Mihomo> StartHostedAsync(Profile source, string node, Profile[] sources, RoutingProfile? profile,
        bool bypassLan, NetworkOptions? network, string runtimeDirectory, CancellationToken cancellationToken) =>
        StartAsync((port, controller, secret) => RoutingConfig.Build(port, controller, secret,
            source, node, sources, profile, bypassLan, network), cancellationToken, runtimeDirectory,
            executablePath: Path.Combine(runtimeDirectory, "mihomo.exe"));

    internal async Task<string> ReadVersionAsync(CancellationToken token)
    {
        using var document = await GetAsync("version", token);
        return document.RootElement.GetProperty("version").GetString()?.TrimStart('v') ?? "—";
    }

    private static async Task<Mihomo> StartAsync(Func<int, int, string, RoutingConfig> makeConfig,
        CancellationToken cancellationToken, string? sessionRoot = null, string? preparedDirectory = null,
        string? executablePath = null)
    {
        executablePath ??= MihomoVersions.Active.Executable;
        if (!File.Exists(executablePath))
            throw new IOException("Файл ядра Mihomo недоступен.");

        string directory = Path.Combine(sessionRoot ?? ProfileStore.DirectoryPath, "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ProcessJob? job = null;
        Process? process = null;
        Mihomo? session = null;
        try
        {
            var (port, controller) = GetPorts();
            string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            string config = Path.Combine(directory, "config.yaml");
            var routing = makeConfig(port, controller, secret);
            await WriteProvidersAsync(directory, routing, cancellationToken, preparedDirectory);
            await CheckGeoAsync(directory, routing, executablePath, cancellationToken);
            await File.WriteAllTextAsync(config, routing.Payload(false), cancellationToken);

            var start = new ProcessStartInfo(executablePath)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(directory);
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add(config);
            job = new ProcessJob();
            process = Process.Start(start) ?? throw new IOException("Не удалось запустить Mihomo.");
            job.Attach(process);
            // Core messages can contain credentials supplied by a provider. Drain without persisting.
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var api = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{controller}/"),
                Timeout = TimeSpan.FromSeconds(12)
            };
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            session = new Mihomo(directory, api, process, job, port, controller, secret, routing);
            await session.WaitReadyAsync(cancellationToken);
            await session.VerifyAsync(routing, false, cancellationToken);
            if (preparedDirectory is null)
            {
                _ = session.PrimeAutomaticRouteAsync(routing);
                CacheRemoteRules(directory, routing);
            }
            return session;
        }
        catch
        {
            if (session is not null)
                await session.DisposeAsync();
            else
            {
                job?.Dispose();
                if (process is not null)
                    await StopAsync(process);
                DeleteSession(directory);
            }
            throw;
        }
    }

    internal static async Task CheckExecutableAsync(string executable, Version requested, CancellationToken cancellationToken)
    {
        const string label = "Linkway compatibility check";
        const string yaml = """
            proxies:
              - name: Linkway compatibility check
                type: http
                server: 127.0.0.1
                port: 9
            """;
        var profile = new Profile(Guid.NewGuid(), label, null, yaml, [new Node(label, "Http")], label);
        string? directory = Path.GetDirectoryName(executable);
        await using var core = await StartAsync(
            (port, controller, secret) => RoutingConfig.Build(port, controller, secret, profile, label, [profile], null, false),
            cancellationToken, directory, executablePath: executable);
        using var version = await core.GetAsync("version", cancellationToken);
        if (!version.RootElement.TryGetProperty("version", out var reported) ||
            reported.GetString() != $"v{requested.ToString(3)}")
            throw new IOException("Ядро Mihomo сообщило неожиданную версию.");
        await core.GetTrafficSnapshotAsync(cancellationToken);
    }

    private static (int Port, int Controller) GetPorts()
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            // Windows can allocate a dynamic TCP port in a UDP-only exclusion range.
            int requested = attempt == 0 ? 0 : RandomNumberGenerator.GetInt32(20000, 49152);
            using var proxy = new TcpListener(IPAddress.Loopback, requested);
            using var api = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                proxy.Start();
                int port = ((IPEndPoint)proxy.LocalEndpoint).Port;
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                api.Start();
                return (port, ((IPEndPoint)api.LocalEndpoint).Port);
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.AccessDenied or SocketError.AddressAlreadyInUse) { }
        }
        throw new IOException("Не удалось найти свободный локальный порт TCP и UDP для Mihomo.");
    }

    private async Task WaitReadyAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(18));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (Exited)
                    throw new IOException("Mihomo завершился при запуске. Проверьте версию ядра и формат профиля.");
                try
                {
                    using var config = await GetAsync("configs", deadline.Token);
                    if (config.RootElement.GetProperty("mixed-port").GetInt32() != Port)
                    {
                        await Task.Delay(150, deadline.Token);
                        continue;
                    }
                    using var socket = new TcpClient(AddressFamily.InterNetwork);
                    await socket.ConnectAsync(IPAddress.Loopback, Port, deadline.Token);
                    return;
                }
                catch (Exception error) when (error is HttpRequestException or SocketException)
                {
                    await Task.Delay(150, deadline.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Mihomo не запустился вовремя.");
        }
    }

    public async Task<Node[]> GetNodesAsync(CancellationToken cancellationToken)
    {
        using var provider = await GetAsync("providers/proxies/nodes", cancellationToken);
        using var group = await GetAsync($"proxies/{Group}", cancellationToken);
        var selectable = group.RootElement.GetProperty("all").EnumerateArray()
            .Select(name => name.GetString()).ToHashSet(StringComparer.Ordinal);
        var nodes = provider.RootElement.GetProperty("proxies").EnumerateArray()
            .Select(node => new Node(node.GetProperty("name").GetString()!, node.GetProperty("type").GetString()!))
            .ToArray();
        var denial = nodes.Select(node => SubscriptionDevice.RejectionForNode(node.Name)).FirstOrDefault(reason => reason is not null);
        if (denial is not null)
            throw new SubscriptionDeviceException(denial);
        return nodes
            .Where(node => RoutingConfig.IsRoutable(node) && selectable.Contains(node.Name))
            .DistinctBy(node => node.Name)
            .ToArray();
    }

    public async Task EnableTunAsync(CancellationToken cancellationToken)
    {
        tunRequested = true;
        await SendAsync(HttpMethod.Patch, "configs", new { tun = RoutingConfig.Tun(true) }, cancellationToken);
        await VerifyTunAsync(true, cancellationToken);
    }

    public Task<int?> CheckMainRouteAsync(CancellationToken cancellationToken) =>
        CheckDelayAsync($"proxies/{RoutingConfig.Main}/delay", cancellationToken);

    public async Task<AutomaticRouteStatus?> ReadAutomaticRouteAsync(CancellationToken cancellationToken)
    {
        var config = currentConfig;
        if (!config.UsesAutomaticServerSelection)
            return null;
        using var response = await GetAsync("proxies", cancellationToken);
        var proxies = response.RootElement.GetProperty("proxies");
        if (!ReferenceEquals(config, currentConfig) ||
            !proxies.TryGetProperty(Group, out var group) ||
            group.GetProperty("type").GetString() != "URLTest")
            return null;

        string url = group.GetProperty("testUrl").GetString()!;
        string? current = group.GetProperty("now").GetString();
        var members = group.GetProperty("all");
        string? server = null;
        int? delay = null;
        int available = 0;
        var failed = new List<string>();
        foreach (var member in members.EnumerateArray())
        {
            string alias = member.GetString()!;
            if (!config.ExpectedNodes.TryGetValue(alias, out var name) ||
                !proxies.TryGetProperty(alias, out var proxy) ||
                !proxy.GetProperty("extra").TryGetProperty(url, out var check))
                continue;
            if (!check.GetProperty("alive").GetBoolean())
            {
                failed.Add(name);
                continue;
            }
            var history = check.GetProperty("history");
            int last = history.GetArrayLength() - 1;
            if (last < 0)
                continue;
            int milliseconds = history[last].GetProperty("delay").GetInt32();
            if (milliseconds <= 0)
                continue;
            available++;
            if (alias == current)
            {
                server = name;
                delay = milliseconds;
            }
        }
        return new(server, delay, available, members.GetArrayLength(), failed.ToArray());
    }

    private async Task PrimeAutomaticRouteAsync(RoutingConfig config)
    {
        if (!config.UsesAutomaticServerSelection)
            return;
        // The first scheduled probe can run before the file provider loads its servers.
        await Task.Delay(TimeSpan.FromSeconds(1));
        if (!ReferenceEquals(config, currentConfig))
            return;
        try
        {
            using var response = await api.GetAsync($"providers/proxies/{Group}/healthcheck");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            // The core schedules further checks on its own.
        }
    }

    private async Task<int?> CheckDelayAsync(string path, CancellationToken cancellationToken)
    {
        string target = Uri.EscapeDataString("https://www.gstatic.com/generate_204");
        try
        {
            using var response = await GetAsync(
                $"{path}?url={target}&timeout=6500&expected=204", cancellationToken);
            return response.RootElement.TryGetProperty("delay", out var delay) && delay.TryGetInt32(out int value) ? value : null;
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            return null;
        }
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await api.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }

    private async Task SendAsync(HttpMethod method, string path, object value, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(value) };
        using var response = await api.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        if (tunRequested && !Exited)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await SendAsync(HttpMethod.Patch, "configs", new { tun = new { enable = false } }, deadline.Token);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            {
                // Closing the job also stops the core and closes its adapter handles.
            }
        }
        api.Dispose();
        job.Dispose();
        await StopAsync(process);
        DeleteSession(directory);
    }

    private static async Task StopAsync(Process process)
    {
        using (process)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(deadline.Token);
        }
    }

    public static void DeleteSession(string directory)
    {
        // Remove known session files and delete the directory if it is empty.
        foreach (string file in new[] { "nodes.txt", "config.yaml", "config-check.yaml", "cache.db", "GeoIP.dat", "GeoSite.dat" })
            File.Delete(Path.Combine(directory, file));
        foreach (string file in Directory.EnumerateFiles(directory).Where(IsRoutingProviderFile))
            File.Delete(file);
        if (!Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }
}
