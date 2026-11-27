using System.IO;
using System.IO.Pipes;
using System.Security.Principal;

namespace VPNDesktop;

internal sealed class VpnSessionClient(HostedConnection connection)
{
    public int Port => connection.Port;
    public DateTimeOffset Started => connection.Started;
    public string CoreVersion => connection.CoreVersion;
    public bool AutomaticStatusSupported => connection.AutomaticStatusSupported;

    public static async Task WaitForHostAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        token = deadline.Token;
        using var pipe = await OpenAsync(token, timeoutMs: 8000);
        await HostProtocol.WriteAsync(pipe, new HostRequest("status"), token);
        _ = HostProtocol.Result<HostedStatus>(await HostProtocol.ReadAsync<HostReply>(pipe, token));
    }

    public static Task<HostedAttachment> AttachAsync(CancellationToken token) => SendAsync<HostedAttachment>(new("attach"), token);
    public static Task<HostedStatus> StatusAsync(CancellationToken token) => SendAsync<HostedStatus>(new("status"), token);
    public static async Task<HostedStatus> StopAsync(bool preserveGuard, CancellationToken token)
    {
        var status = await SendAsync<HostedStatus>(new("stop", Flag: preserveGuard), token);
        if (!preserveGuard)
        {
            while (HostProtocol.Exists())
                await Task.Delay(40, token);
            await Task.Run(HostRuntime.Reap, token);
        }
        return status;
    }

    public static async Task<HostedAttachment> StartAsync(HostedConfiguration configuration, Action save, CancellationToken token)
    {
        await HostRuntime.EnsureAsync(token);
        return await SendAsync<HostedAttachment>(new("start", configuration), token, save);
    }

    public async Task ApplyRoutingAsync(HostedConfiguration configuration, Action save, CancellationToken token) =>
        connection = await SendAsync<HostedConnection>(new("apply", configuration), token, save);

    public Task<(long Download, long Upload, ActiveConnection[] Connections)> GetTrafficSnapshotAsync(CancellationToken token) =>
        SendAsync<(long, long, ActiveConnection[])>(new("traffic"), token);
    public Task<ActiveConnection[]> GetConnectionsAsync(CancellationToken token) =>
        SendAsync<ActiveConnection[]>(new("connections"), token);
    public Task<int?> CheckMainRouteAsync(CancellationToken token) => SendAsync<int?>(new("health"), token);
    public Task<AutomaticRouteStatus?> ReadAutomaticRouteAsync(CancellationToken token) =>
        SendAsync<AutomaticRouteStatus?>(new("automatic-route"), token);
    public Task<string> DescribeDiagnosticConfigurationAsync(CancellationToken token) =>
        SendAsync<string>(new("configuration"), token);
    public Task<bool> TestDnsAsync(bool ipv6, CancellationToken token) => SendAsync<bool>(new("dns", Flag: ipv6), token);
    public Task<RouteProbeResult> ProbeRouteAsync(string destination, CancellationToken token) =>
        SendAsync<RouteProbeResult>(new("probe-route", Destination: destination), token);
    public Task<(string Host, string Kind)[]> GetDiagnosticRoutesAsync(IReadOnlyDictionary<int, byte> ports, CancellationToken token) =>
        SendAsync<(string, string)[]>(new("routes", Ports: ports.Keys.ToArray()), token);

    private static async Task<NamedPipeClientStream> OpenAsync(CancellationToken token, int timeoutMs = 1500)
    {
        var pipe = new NamedPipeClientStream(".", HostProtocol.PipeName, PipeDirection.InOut,
            HostProtocol.PipeFlags, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(timeoutMs, token);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static async Task<T> SendAsync<T>(HostRequest request, CancellationToken token, Action? save = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(save is null ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(2));
        token = deadline.Token;
        using var pipe = await OpenAsync(token);
        await HostProtocol.WriteAsync(pipe, request, token);
        var reply = await HostProtocol.ReadAsync<HostReply>(pipe, token);
        if (reply.Prepared)
        {
            try { (save ?? throw new IOException("Неожиданный запрос фонового сеанса."))(); }
            catch
            {
                await HostProtocol.WriteAsync(pipe, false, token);
                _ = HostProtocol.Result<T>(await HostProtocol.ReadAsync<HostReply>(pipe, token));
                throw;
            }
            await HostProtocol.WriteAsync(pipe, true, token);
            reply = await HostProtocol.ReadAsync<HostReply>(pipe, token);
        }
        return HostProtocol.Result<T>(reply);
    }
}
