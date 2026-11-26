using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
using VPNDesktop;

internal static class HostedSessionChecks
{
    public static async Task RunAsync(string scratch, Profile first, Profile second, CancellationToken token)
    {
        string pipeName = "Linkway.Host.Test." + Guid.NewGuid().ToString("N");
        string runtime = Path.Combine(scratch, "background-runtime");
        Directory.CreateDirectory(runtime);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "mihomo.exe"), Path.Combine(runtime, "mihomo.exe"));

        int systemProxy = 0;
        var host = new VpnSessionHost(pipeName, runtime, port => systemProxy = port, () => systemProxy = 0).ServeAsync();
        var source = HostedSource.From(first with { CabinetPlanId = 4321 });
        var other = HostedSource.From(second);
        var config = new HostedConfiguration(source, first.Nodes[0].Name, [source, other], null, null, false, false, false);
        try
        {
            using (var refused = await OpenAsync(new("start", config)))
            {
                try
                {
                    await CommitAsync<HostedAttachment>(refused, confirm: false);
                    throw new InvalidOperationException("The background session accepted a cancelled start.");
                }
                catch (IOException) { }
            }
            if (systemProxy != 0 || (await SendAsync<HostedStatus>(new("status"))).Connected)
                throw new InvalidOperationException("A cancelled start must restore the previous network state.");

            HostedAttachment started;
            using (var ui = await OpenAsync(new("start", config)))
                started = await CommitAsync<HostedAttachment>(ui, confirm: true);

            var connection = started.Connection ?? throw new InvalidOperationException("Background connection is missing.");
            if (!connection.AutomaticStatusSupported ||
                await SendAsync<AutomaticRouteStatus?>(new("automatic-route")) is not null)
                throw new InvalidOperationException("The host did not expose automatic status only for automatic routing.");
            using var handler = new HttpClientHandler { Proxy = new WebProxy("127.0.0.1", connection.Port) };
            using var browser = new HttpClient(handler);
            if (systemProxy != connection.Port || await browser.GetStringAsync("http://background.test/", token) != "primary")
                throw new InvalidOperationException("Traffic did not survive closing the first interface connection.");

            var restored = await SendAsync<HostedAttachment>(new("attach"));
            if (!restored.Status.Connected || restored.Connection?.Started != connection.Started ||
                restored.Connection.Source.CabinetPlan != 4321 || restored.Connection.Port != connection.Port)
                throw new InvalidOperationException("A new interface failed to attach to the same protected session.");

            var alternate = config with { Source = other, Node = second.Nodes[0].Name };
            using (var ui = await OpenAsync(new("apply", alternate)))
                _ = await CommitAsync<HostedConnection>(ui, confirm: true);
            if (await browser.GetStringAsync("http://background.test/", token) != "secondary")
                throw new InvalidOperationException("The reattached interface could not switch the server.");

            var changed = alternate with
            {
                BypassLan = true,
                Network = new NetworkOptions(FakeIp: false, Resolvers: "tls://1.1.1.1",
                    DomainResolvers: "background.test=https://1.0.0.1/dns-query"),
                Routing = RoutingProfile.Create("Live edits") with
                {
                    Rules = [new(RouteMatch.Domain, "background.test", RouteProtocol.Tcp,
                        new(RouteKind.Server, new RouteServer(RouteServer.SourceKey(source.Restore()), first.Nodes[0].Name)))]
                }
            };
            using (var ui = await OpenAsync(new("apply", changed)))
                _ = await CommitAsync<HostedConnection>(ui, confirm: true);
            if (await browser.GetStringAsync("http://background.test/", token) != "primary" ||
                await browser.GetStringAsync("http://unmatched.test/", token) != "secondary" ||
                (await SendAsync<HostedAttachment>(new("attach"))).Connection?.Started != connection.Started)
                throw new InvalidOperationException("Live routing and network edits did not preserve the background session.");
            Console.WriteLine("PASS Routing rules and DNS settings are accepted together by the running background core");

            var traffic = await SendAsync<(long Download, long Upload, ActiveConnection[] Connections)>(new("traffic"));
            if (traffic.Connections is null)
                throw new InvalidOperationException("The host did not expose traffic statistics correctly.");
            Console.WriteLine("PASS Background VPN survives UI detach, reattaches and changes routes on the original port");
        }
        finally
        {
            _ = await SendAsync<HostedStatus>(new("stop"));
            await host.WaitAsync(token);
        }
        if (systemProxy != 0 || Directory.EnumerateDirectories(runtime, "session-*").Any())
            throw new InvalidOperationException("Explicit disconnect did not clean up the background VPN.");
        File.Delete(Path.Combine(runtime, "mihomo.exe"));
        File.Delete(Path.Combine(runtime, "session-host.lock"));
        Directory.Delete(runtime);
        Console.WriteLine("PASS Disconnect releases the background session and its simulated system proxy");

        async Task<T> SendAsync<T>(HostRequest request)
        {
            using var pipe = await OpenAsync(request);
            return HostProtocol.Result<T>(await HostProtocol.ReadAsync<HostReply>(pipe, token));
        }

        async Task<T> CommitAsync<T>(NamedPipeClientStream pipe, bool confirm)
        {
            var response = await HostProtocol.ReadAsync<HostReply>(pipe, token);
            if (!response.Prepared)
            {
                _ = HostProtocol.Result<T>(response);
                throw new InvalidOperationException("The background host skipped the confirmation handshake.");
            }
            await HostProtocol.WriteAsync(pipe, confirm, token);
            return HostProtocol.Result<T>(await HostProtocol.ReadAsync<HostReply>(pipe, token));
        }

        async Task<NamedPipeClientStream> OpenAsync(HostRequest request)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                HostProtocol.PipeFlags, TokenImpersonationLevel.Identification);
            try
            {
                await pipe.ConnectAsync(3000, token);
                await HostProtocol.WriteAsync(pipe, request, token);
                return pipe;
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }
    }
}
