using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace VPNDesktop;

internal sealed partial class Mihomo
{
    private const int MaxRuleFileBytes = 16 * 1024 * 1024;
    private static readonly HttpClient RuleFeedClient = new(new HttpClientHandler
    {
        UseCookies = false, MaxAutomaticRedirections = 5
    }) { Timeout = TimeSpan.FromSeconds(20) };

    public Task ApplyRoutingAsync(Profile source, string node, Profile[] sources, RoutingProfile? profile,
        bool bypassLan, Action save, CancellationToken cancellationToken, NetworkOptions? network = null) =>
        ApplyHostedRoutingAsync(source, node, sources, profile, bypassLan,
            () => { save(); return Task.CompletedTask; }, cancellationToken, network);

    internal async Task ApplyHostedRoutingAsync(Profile source, string node, Profile[] sources, RoutingProfile? profile,
        bool bypassLan, Func<Task> save, CancellationToken cancellationToken, NetworkOptions? network)
    {
        var previous = currentConfig;
        var candidate = RoutingConfig.Build(Port, controller, secret, source, node, sources, profile, bypassLan, network);
        try
        {
            await WriteProvidersAsync(directory, candidate, cancellationToken);
            await CheckGeoAsync(directory, candidate, ExecutablePath, cancellationToken);
            if (candidate.ExpectedRuleProviders.Count > 0)
            {
                // The core can accept a config while logging a rule-provider load failure.
                // Verify the same files in an isolated core before changing live routes.
                await using var checkedConfig = await StartAsync((port, controller, secret) =>
                    RoutingConfig.Build(port, controller, secret, source, node, sources, profile, bypassLan, network),
                    cancellationToken, Path.GetDirectoryName(directory), directory, ExecutablePath);
            }
            try
            {
                await ReloadAsync(candidate, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await save();
                currentConfig = candidate;
                CacheRemoteRules(directory, candidate);
            }
            catch (CoreConfigRejected)
            {
                throw new IOException("Mihomo отклонил профиль. Продолжает действовать прежняя конфигурация.");
            }
            catch (Exception error)
            {
                await RestoreRoutingAsync(previous);
                if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;
                throw new IOException("Не удалось применить профиль или сохранить его на диск. Восстановлена прежняя конфигурация.");
            }
        }
        finally
        {
            PruneProviders();
        }
    }

    private async Task RestoreRoutingAsync(RoutingConfig previous)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await ReloadAsync(previous, deadline.Token);
            return;
        }
        catch (Exception) { }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var blocked = RoutingConfig.Blocked(Port, controller, secret);
            await ReloadAsync(blocked, deadline.Token);
            currentConfig = blocked;
        }
        catch (Exception)
        {
            throw new RoutingRecoveryException("Mihomo не подтвердил откат или блокировку трафика. Проверьте состояние подключения.");
        }
        throw new RoutingRecoveryException("Откат не удался. Mihomo заблокировал трафик. Примените исправленный профиль для восстановления связи.");
    }

    private sealed class CoreConfigRejected : Exception;

    private async Task ReloadAsync(RoutingConfig configuration, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(HttpMethod.Put, "configs", new { payload = configuration.Payload(tunRequested) }, cancellationToken);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.BadRequest)
        {
            // This response comes from the parser before Mihomo calls ApplyConfig.
            throw new CoreConfigRejected();
        }
        await VerifyAsync(configuration, tunRequested, cancellationToken);

        // Existing TCP streams and UDP associations otherwise keep their previous outbound.
        using var response = await api.DeleteAsync("connections", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task VerifyAsync(RoutingConfig configuration, bool tun, CancellationToken cancellationToken)
    {
        await VerifyTunAsync(tun, cancellationToken);

        using var rules = await GetAsync("rules", cancellationToken);
        var liveTargets = rules.RootElement.GetProperty("rules").EnumerateArray()
            .Select(rule => rule.GetProperty("proxy").GetString());
        if (!liveTargets.SequenceEqual(configuration.Rules.Select(rule => SubscriptionRouting.ParseRule(rule).Destination)))
            throw new IOException("Mihomo не подтвердил порядок маршрутов.");

        if (configuration.ExpectedRuleProviders.Count > 0)
        {
            using var providerResponse = await GetAsync("providers/rules", cancellationToken);
            var providers = providerResponse.RootElement.GetProperty("providers");
            foreach (var (alias, name) in configuration.ExpectedRuleProviders)
            {
                if (!providers.TryGetProperty(alias, out var provider) ||
                    !provider.TryGetProperty("ruleCount", out var count) || !count.TryGetInt32(out int loaded) || loaded <= 0)
                    throw new IOException($"Mihomo не загрузил набор правил «{name}».");
            }
        }

        if (configuration.ExpectedNodes.Count == 0)
            return;
        using var document = await GetAsync("proxies", cancellationToken);
        var proxies = document.RootElement.GetProperty("proxies");
        foreach (var (alias, node) in configuration.ExpectedNodes)
        {
            if (!proxies.TryGetProperty(alias, out var group) ||
                group.GetProperty("now").GetString() != node ||
                !group.GetProperty("all").EnumerateArray().Select(item => item.GetString()).SequenceEqual([node]))
                throw new IOException($"Mihomo не загрузил сервер «{node}» для маршрутизации.");
        }

        foreach (var (alias, members) in configuration.ExpectedGroups)
        {
            if (!proxies.TryGetProperty(alias, out var group) ||
                !group.GetProperty("all").EnumerateArray().Select(item => item.GetString()).SequenceEqual(members))
                throw new IOException("Mihomo не подтвердил состав группы серверов.");
        }

    }

    private async Task VerifyTunAsync(bool tun, CancellationToken cancellationToken)
    {
        using var config = await GetAsync("configs", cancellationToken);
        var root = config.RootElement;
        if (root.GetProperty("mixed-port").GetInt32() != Port ||
            !string.Equals(root.GetProperty("mode").GetString(), "rule", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Mihomo не подтвердил порт или режим правил.");

        var actual = root.GetProperty("tun");
        if (actual.GetProperty("enable").GetBoolean() != tun)
            throw new IOException("Mihomo не подтвердил режим TUN.");
        if (!tun)
            return;
        if (!actual.GetProperty("strict-route").GetBoolean() || !actual.GetProperty("auto-route").GetBoolean())
            throw new IOException("Mihomo не подтвердил маршруты адаптера TUN.");
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
                adapter.Name.Equals(RoutingConfig.TunDevice, StringComparison.OrdinalIgnoreCase)
                && adapter.OperationalStatus == OperationalStatus.Up))
                return;
            await Task.Delay(200, cancellationToken);
        }
        throw new IOException("Windows не подтвердила запуск адаптера TUN.");
    }

    private static async Task WriteProvidersAsync(string directory, RoutingConfig configuration,
        CancellationToken cancellationToken, string? preparedDirectory = null)
    {
        foreach (var (file, content) in configuration.Files)
        {
            string path = Path.Combine(directory, file);
            if (File.Exists(path))
                continue;
            await File.WriteAllTextAsync(path + ".new", content, cancellationToken);
            File.Move(path + ".new", path);
        }
        foreach (var (file, address) in configuration.RemoteRuleFiles)
        {
            string path = Path.Combine(directory, file);
            if (File.Exists(path))
                continue;
            if (preparedDirectory is not null)
                File.Copy(Path.Combine(preparedDirectory, file), path);
            else
            {
                try { await DownloadRuleFileAsync(address, path, cancellationToken); }
                catch (IOException) when (!cancellationToken.IsCancellationRequested && File.Exists(RuleCachePath(directory, file)))
                {
                    File.Copy(RuleCachePath(directory, file), path);
                }
            }
        }
    }

    private static string RuleCachePath(string sessionDirectory, string file) =>
        Path.Combine(Path.GetDirectoryName(sessionDirectory)!, "rule-cache", file);

    private static void CacheRemoteRules(string sessionDirectory, RoutingConfig configuration)
    {
        if (configuration.RemoteRuleFiles.Count == 0)
            return;
        try
        {
            string cache = Path.GetDirectoryName(RuleCachePath(sessionDirectory, "unused"))!;
            Directory.CreateDirectory(cache);
            foreach (string file in configuration.RemoteRuleFiles.Keys)
            {
                string cached = Path.Combine(cache, file);
                File.Copy(Path.Combine(sessionDirectory, file), cached + ".new", true);
                File.Move(cached + ".new", cached, true);
                File.SetLastWriteTimeUtc(cached, DateTime.UtcNow);
            }
            foreach (var old in new DirectoryInfo(cache).EnumerateFiles("route-rules-*.bin")
                .Where(file => IsRoutingProviderFile(file.FullName)).OrderByDescending(file => file.LastWriteTimeUtc).Skip(32))
                old.Delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static async Task DownloadRuleFileAsync(Uri address, string path, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await RuleFeedClient.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxRuleFileBytes)
                throw new IOException("Размер набора правил подписки превышает 16 МБ.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            await using (var file = File.Create(path + ".new"))
            {
                byte[] buffer = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    if (file.Length + read > MaxRuleFileBytes)
                        throw new IOException("Размер набора правил подписки превышает 16 МБ.");
                    await file.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
                }
            }
            File.Move(path + ".new", path);
        }
        catch (Exception error) when (error is HttpRequestException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Не удалось скачать набор правил подписки. Проверьте доступ к источнику.");
        }
    }

    private static bool IsRoutingProviderFile(string path)
    {
        string file = Path.GetFileName(path);
        if (file.EndsWith(".new", StringComparison.Ordinal))
            file = file[..^4];
        return file.Length == 80 && file.AsSpan(12, 64).IndexOfAnyExcept("0123456789ABCDEF") < 0 &&
            ((file.StartsWith("route-nodes-", StringComparison.Ordinal) && file.EndsWith(".txt", StringComparison.Ordinal)) ||
             (file.StartsWith("route-rules-", StringComparison.Ordinal) && file.EndsWith(".bin", StringComparison.Ordinal)));
    }

    private void PruneProviders()
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory).Where(IsRoutingProviderFile))
                if (!currentConfig.Files.ContainsKey(Path.GetFileName(path)) &&
                    !currentConfig.RemoteRuleFiles.ContainsKey(Path.GetFileName(path)))
                    File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task CheckGeoAsync(string directory, RoutingConfig configuration, string executablePath,
        CancellationToken cancellationToken)
    {
        if (configuration.GeoFiles.Count == 0)
            return;
        string cache = Path.Combine(Path.GetDirectoryName(directory)!, "geodata");
        foreach (string filename in configuration.GeoFiles)
        {
            string cached = Path.Combine(cache, filename);
            string local = Path.Combine(directory, filename);
            if (!File.Exists(local) && File.Exists(cached))
                File.Copy(cached, local);
        }
        string path = Path.Combine(directory, "config-check.yaml");
        await File.WriteAllTextAsync(path, configuration.Payload(false), cancellationToken);
        var start = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "-t", "-d", directory, "-f", path })
            start.ArgumentList.Add(argument);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(100));
        try
        {
            using var job = new ProcessJob();
            var checker = Process.Start(start) ?? throw new IOException("Не удалось запустить проверку GEOIP и GEOSITE.");
            try
            {
                job.Attach(checker);
                checker.BeginOutputReadLine();
                checker.BeginErrorReadLine();
                await checker.WaitForExitAsync(deadline.Token);
                if (checker.ExitCode != 0)
                    throw new IOException("Mihomo отклонил настройки GEOIP или GEOSITE. Проверьте коды и доступность баз GeoData.");
                CacheGeoFiles();
            }
            finally { await StopAsync(checker); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Mihomo не успел проверить базы GEOIP и GEOSITE.");
        }
        finally { File.Delete(path); }

        void CacheGeoFiles()
        {
            try
            {
                Directory.CreateDirectory(cache);
                foreach (string filename in configuration.GeoFiles)
                {
                    string cached = Path.Combine(cache, filename);
                    File.Copy(Path.Combine(directory, filename), cached + ".new", true);
                    File.Move(cached + ".new", cached, true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<(long Download, long Upload, ActiveConnection[] Connections)> GetTrafficSnapshotAsync(
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        using var snapshot = await GetAsync("connections", deadline.Token);
        var root = snapshot.RootElement;
        var connections = root.TryGetProperty("connections", out var entries) && entries.ValueKind == JsonValueKind.Array
            ? entries.EnumerateArray().Select(item => ActiveConnection.From(item, currentConfig)).ToArray() : [];
        return (root.GetProperty("downloadTotal").GetInt64(), root.GetProperty("uploadTotal").GetInt64(), connections);
    }

    public async Task<ActiveConnection[]> GetConnectionsAsync(CancellationToken cancellationToken)
    {
        var (_, _, connections) = await GetTrafficSnapshotAsync(cancellationToken);
        return connections.OrderByDescending(item => item.Started).ToArray();
    }

    public async Task<string> DescribeDiagnosticConfigurationAsync(CancellationToken cancellationToken)
    {
        using var configuration = await GetAsync("configs", cancellationToken);
        using var rules = await GetAsync("rules", cancellationToken);
        using var feeds = await GetAsync("providers/rules", cancellationToken);
        var liveTun = configuration.RootElement.GetProperty("tun");
        var providers = feeds.RootElement.GetProperty("providers");
        int loaded = currentConfig.ExpectedRuleProviders.Keys.Count(key =>
            providers.TryGetProperty(key, out var provider) &&
            provider.TryGetProperty("ruleCount", out var count) && count.TryGetInt32(out int size) && size > 0);
        int ruleCount = rules.RootElement.GetProperty("rules").GetArrayLength();
        bool tun = liveTun.GetProperty("enable").GetBoolean();
        bool strict = liveTun.TryGetProperty("strict-route", out var strictRoute) && strictRoute.GetBoolean();
        return $"доступны, правил {ruleCount}, наборов загружено {loaded} из {currentConfig.ExpectedRuleProviders.Count}, " +
               (tun ? $"TUN включён, строгий маршрут {(strict ? "включён" : "выключен")}" : "TUN выключен");
    }

    public async Task<(string Host, string Kind)[]> GetDiagnosticRoutesAsync(
        IReadOnlyDictionary<int, byte> proxyPorts, CancellationToken cancellationToken)
    {
        using var snapshot = await GetAsync("connections", cancellationToken);
        if (!snapshot.RootElement.TryGetProperty("connections", out var connections) ||
            connections.ValueKind != JsonValueKind.Array)
            return [];
        var routes = new List<(string Host, string Kind)>();
        foreach (var entry in connections.EnumerateArray())
        {
            if (!entry.TryGetProperty("metadata", out var metadata) ||
                !metadata.TryGetProperty("sourcePort", out var port) || !int.TryParse(port.ToString(), out int socketPort) ||
                !proxyPorts.ContainsKey(socketPort) || !metadata.TryGetProperty("host", out var domain))
                continue;
            string host = domain.GetString() ?? "";
            if (host is not ("www.gstatic.com" or "example.org" or "api6.ipify.org"))
                continue;
            var outbound = entry.TryGetProperty("chains", out var chain) && chain.ValueKind == JsonValueKind.Array &&
                chain.GetArrayLength() > 0 ? chain[0].GetString() : null;
            string? rule = entry.TryGetProperty("rule", out var type) ? type.GetString() : null;
            routes.Add((host, DescribeDiagnosticRoute(outbound, rule)));
        }
        return routes.ToArray();
    }

    internal static string DescribeDiagnosticRoute(string? outbound, string? rule)
    {
        string policy = outbound switch
        {
            "DIRECT" => "напрямую",
            "REJECT" or "REJECT-DROP" => "блокировка",
            null or "" => "выход неизвестен",
            _ => "через прокси"
        };
        string condition = rule switch
        {
            "RuleSet" or "RULE-SET" => "набор правил",
            "Match" or "MATCH" => "маршрут по умолчанию",
            null or "" => "условие неизвестно",
            _ => "правило маршрутизации"
        };
        return $"{policy} ({condition})";
    }

    public async Task<bool> TestDnsAsync(bool ipv6, CancellationToken cancellationToken)
    {
        using var response = await GetAsync($"dns/query?name=www.gstatic.com&type={(ipv6 ? "AAAA" : "A")}", cancellationToken);
        return response.RootElement.TryGetProperty("Answer", out var answers) && answers.ValueKind == JsonValueKind.Array
            && answers.GetArrayLength() > 0;
    }

}

internal sealed class RoutingRecoveryException(string message) : IOException(message);

internal sealed record ActiveConnection(string Id, string Host, string Ip, string Port, string Process, string Path,
    string Network, string Rule, string Route, DateTimeOffset Started, long Upload, long Download, string Outbound)
{
    public string Destination => $"{(Host.Length != 0 ? Host : Ip.Contains(':') ? $"[{Ip}]" : Ip)}:{Port}";
    public string Details => string.Join("  ·  ", new[] { Process, Network.ToUpperInvariant(), Rule, Route, $"↓ {Download:N0} / ↑ {Upload:N0} Б" }
        .Where(part => part.Length != 0));

    public static ActiveConnection From(JsonElement item, RoutingConfig config)
    {
        var metadata = item.GetProperty("metadata");
        string route = item.TryGetProperty("chains", out var chain) && chain.ValueKind == JsonValueKind.Array
            ? string.Join(" → ", chain.EnumerateArray().Reverse().Select(value => value.GetString() ?? "")
                .Where(alias => !config.HiddenGroups.Contains(alias) &&
                    (!config.ExpectedNodes.ContainsKey(alias) || config.GroupLabels.ContainsKey(alias)))
                .Select(alias => config.GroupLabels.GetValueOrDefault(alias, alias))) : "";
        return new(Read(item, "id"), Read(metadata, "host"), Read(metadata, "destinationIP"),
            Read(metadata, "destinationPort"), Read(metadata, "process"), Read(metadata, "processPath"),
            Read(metadata, "network"), (Read(item, "rule") + " " + Read(item, "rulePayload")).Trim(),
            route, DateTimeOffset.TryParse(Read(item, "start"), out var start) ? start : default,
            Count("upload"), Count("download"),
            chain.ValueKind == JsonValueKind.Array && chain.GetArrayLength() > 0 ? chain[0].GetString() ?? "" : "");

        long Count(string property) => item.TryGetProperty(property, out var value) && value.TryGetInt64(out long count) ? count : 0;
        static string Read(JsonElement obj, string property) => obj.TryGetProperty(property, out var value) ? value.ToString() : "";
    }
}

