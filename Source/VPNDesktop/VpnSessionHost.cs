using System.IO;
using System.IO.Pipes;
using Microsoft.Win32;

namespace VPNDesktop;

internal sealed class VpnSessionHost(string pipeName, string runtimeDirectory, Action<int> enableProxy, Action restoreProxy)
{
    private readonly SemaphoreSlim change = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private Mihomo? core;
    private WindowsKillSwitch? guard;
    private HostedConnection? connection;
    private DateTimeOffset idleSince = DateTimeOffset.UtcNow;
    private bool proxyApplied;

    public static void Run() => new VpnSessionHost(HostProtocol.PipeName, AppContext.BaseDirectory,
        WindowsProxy.Enable, WindowsProxy.Restore).ServeAsync().GetAwaiter().GetResult();

    internal async Task ServeAsync()
    {
        using var presence = new Mutex(false, @"Local\" + pipeName, out bool created);
        if (!created)
            return;
        using var snapshotLock = File.Open(Path.Combine(runtimeDirectory, "session-host.lock"),
            FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        SystemEvents.SessionEnding += EndWindowsSession;
        var monitor = MonitorAsync();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 32,
                    PipeTransmissionMode.Byte, HostProtocol.PipeFlags);
                try { await pipe.WaitForConnectionAsync(lifetime.Token); }
                catch
                {
                    pipe.Dispose();
                    throw;
                }
                _ = HandleAsync(pipe);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            SystemEvents.SessionEnding -= EndWindowsSession;
            await monitor;
            await change.WaitAsync();
            try { await StopAsync(preserveGuard: false); }
            finally { change.Release(); }
        }
    }

    private void EndWindowsSession(object sender, SessionEndingEventArgs e) => lifetime.Cancel();

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                if (!change.Wait(0))
                    continue;
                try
                {
                    if (core?.Exited == true || (core is null && proxyApplied))
                        await StopAsync(preserveGuard: true);
                    if (core is null && guard is null && DateTimeOffset.UtcNow - idleSince > TimeSpan.FromSeconds(12))
                        lifetime.Cancel();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    // In case proxy restoration failed, keep the owner alive to allow another attempt.
                }
                finally { change.Release(); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var token = deadline.Token;
            try
            {
                var request = await HostProtocol.ReadAsync<HostRequest>(pipe, token);
                object? result = request.Command is "start" or "apply" or "stop"
                    ? await ChangeAsync(request, pipe, token)
                    : await InspectAsync(request, token);
                await HostProtocol.WriteAsync(pipe, HostProtocol.Success(result), token);
                if (request.Command == "stop" && !request.Flag)
                    lifetime.Cancel();
            }
            catch (Exception error)
            {
                string message = error switch
                {
                    IOException when error.GetType() == typeof(IOException) || error is RoutingRecoveryException => error.Message,
                    OperationCanceledException => "Фоновая операция Linkway превысила время ожидания.",
                    _ => "Фоновый сеанс Linkway не смог выполнить операцию."
                };
                try
                {
                    var reply = HostProtocol.Success() with { Error = message, Recovery = error is RoutingRecoveryException };
                    await HostProtocol.WriteAsync(pipe, reply, token);
                }
                catch (Exception failure) when (failure is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
    }

    private async Task<object?> ChangeAsync(HostRequest request, Stream pipe, CancellationToken token)
    {
        await change.WaitAsync(token);
        try
        {
            if (request.Command == "stop")
            {
                await StopAsync(request.Flag);
                return Status();
            }

            var config = request.Configuration ?? throw new IOException("Отсутствует конфигурация сеанса.");
            var source = config.Source.Restore();
            var sources = config.Sources.Select(item => item.Restore()).ToArray();
            if (request.Command == "start")
            {
                if (core is { Exited: false })
                    throw new IOException("Фоновый VPN уже подключён.");
                if (core is not null || proxyApplied)
                    await StopAsync(preserveGuard: true);
                if (guard is not null && !config.Tun)
                    throw new IOException("Снимите блокировку Kill Switch перед подключением через прокси.");

                var candidate = await Mihomo.StartHostedAsync(source, config.Node, sources, config.Routing,
                    config.BypassLan, config.Network, runtimeDirectory, token);
                try
                {
                    string version = await candidate.ReadVersionAsync(token);
                    if (config.Tun)
                    {
                        if (config.Guard)
                            guard ??= WindowsKillSwitch.Arm(candidate.ExecutablePath);
                        await candidate.EnableTunAsync(token);
                        guard?.AllowTunAdapter();
                    }
                    else
                    {
                        proxyApplied = true;
                        enableProxy(candidate.Port);
                    }
                    await ConfirmAsync(pipe, token);
                    core = candidate;
                    connection = new(config.Source, config.Node, config.Tun, candidate.Port, DateTimeOffset.Now, version);
                    return new HostedAttachment(Status(), connection);
                }
                catch
                {
                    core = candidate;
                    await StopAsync(preserveGuard: true);
                    throw;
                }
            }

            var current = RequireCore();
            await current.ApplyHostedRoutingAsync(source, config.Node, sources, config.Routing, config.BypassLan, async () =>
            {
                guard?.AllowTunAdapter();
                await ConfirmAsync(pipe, token);
            }, token, config.Network);
            connection = connection! with { Source = config.Source, Node = config.Node };
            return connection;
        }
        finally
        {
            idleSince = DateTimeOffset.UtcNow;
            change.Release();
        }
    }

    private static async Task ConfirmAsync(Stream pipe, CancellationToken token)
    {
        await HostProtocol.WriteAsync(pipe, HostProtocol.Success(prepared: true), token);
        if (!await HostProtocol.ReadAsync<bool>(pipe, token))
            throw new IOException("Не удалось сохранить настройки сеанса.");
    }

    private async Task<object?> InspectAsync(HostRequest request, CancellationToken token) => request.Command switch
    {
        "status" => Status(),
        "attach" => new HostedAttachment(Status(), connection),
        "traffic" => await RequireCore().GetTrafficSnapshotAsync(token),
        "connections" => await RequireCore().GetConnectionsAsync(token),
        "health" => await RequireCore().CheckMainRouteAsync(token),
        "configuration" => await RequireCore().DescribeDiagnosticConfigurationAsync(token),
        "dns" => await RequireCore().TestDnsAsync(request.Flag, token),
        "probe-route" => await RequireCore().ProbeRouteAsync(request.Destination, token),
        "routes" => await RequireCore().GetDiagnosticRoutesAsync(
            (request.Ports ?? []).Distinct().ToDictionary(port => port, _ => (byte)0), token),
        _ => throw new IOException("Неизвестная операция фонового сеанса Linkway.")
    };

    private HostedStatus Status() => new(core is { Exited: false }, guard is not null);
    private Mihomo RequireCore() => core ?? throw new IOException("Фоновый VPN отключён.");

    private async Task StopAsync(bool preserveGuard)
    {
        var previous = core;
        core = null;
        connection = null;
        try
        {
            guard?.BlockTunAdapter();
            if (proxyApplied)
            {
                restoreProxy();
                proxyApplied = false;
            }
        }
        finally
        {
            try
            {
                if (previous is not null)
                    await previous.DisposeAsync();
            }
            finally
            {
                if (!preserveGuard)
                {
                    guard?.Dispose();
                    guard = null;
                }
                idleSince = DateTimeOffset.UtcNow;
            }
        }
    }
}
