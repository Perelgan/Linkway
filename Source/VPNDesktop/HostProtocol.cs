using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace VPNDesktop;

internal sealed record HostedSource(Profile Profile, long? CabinetPlan)
{
    public static HostedSource From(Profile profile) => new(profile, profile.CabinetPlanId);
    public Profile Restore() => Profile with { CabinetPlanId = CabinetPlan };
}

internal sealed record HostedConfiguration(HostedSource Source, string Node, HostedSource[] Sources,
    RoutingProfile? Routing, NetworkOptions? Network, bool BypassLan, bool Tun, bool Guard);

internal sealed record HostedConnection(HostedSource Source, string Node, bool Tun, int Port, DateTimeOffset Started, string CoreVersion);
internal sealed record HostedStatus(bool Connected, bool Guard);
internal sealed record HostedAttachment(HostedStatus Status, HostedConnection? Connection);
internal sealed record HostRequest(string Command, HostedConfiguration? Configuration = null, bool Flag = false, int[]? Ports = null);
internal sealed record HostReply(JsonElement Value, string? Error = null, bool Recovery = false, bool Prepared = false);

internal static class HostProtocol
{
    public const string Argument = "--linkway-session-host";
    public const PipeOptions PipeFlags = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    private const int MaxPacket = 256 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    private static readonly string User = GetUser();
    public static readonly string PipeName = $"Linkway.Session.v1.{User}.{Process.GetCurrentProcess().SessionId}";
    public static readonly string PresenceName = @"Local\" + PipeName;
    public static readonly string RuntimePrefix = PipeName + "-";

    public static bool Elevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    private static string GetUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    public static bool Exists()
    {
        try
        {
            if (!Mutex.TryOpenExisting(PresenceName, out var handle))
                return false;
            handle.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        try
        {
            if (payload.Length is <= 0 or > MaxPacket)
                throw new IOException("Слишком большой запрос к фоновому сеансу Linkway.");
            byte[] header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(payload, token);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxPacket)
            throw new IOException("Некорректный ответ фонового сеанса Linkway.");
        byte[] payload = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(payload, token);
            return JsonSerializer.Deserialize<T>(payload, Json)!;
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    public static HostReply Success(object? value = null, bool prepared = false) =>
        new(JsonSerializer.SerializeToElement(value, Json), Prepared: prepared);

    public static T Result<T>(HostReply reply)
    {
        if (reply.Recovery)
            throw new RoutingRecoveryException(reply.Error!);
        if (reply.Error is { } error)
            throw new IOException(error);
        return reply.Value.Deserialize<T>(Json)!;
    }
}
