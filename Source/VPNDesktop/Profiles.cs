using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPNDesktop;

internal sealed record Node(string Name, string Type)
{
    public string Label => $"{Name}  ·  {Type}";

    [JsonIgnore]
    public bool IsServer => Type.ToUpperInvariant() is not ("DNS" or "DIRECT" or "REJECT" or "REJECTDROP" or "PASS" or "COMPATIBLE") &&
        SubscriptionDevice.RejectionForNode(Name) is null;
}

internal sealed record Profile(Guid Id, string Name, string? Subscription, string Content,
    Node[] Nodes, string? Selected, [property: JsonIgnore] long? CabinetPlanId = null);

internal sealed class ProfileStore
{
    public static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VPNDesktop");
    private readonly string path = Path.Combine(DirectoryPath, "profiles.bin");

    public List<Profile> Load()
    {
        if (!File.Exists(path))
            return [];

        byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<List<Profile>>(clear) ?? [];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public void Save(List<Profile> profiles)
    {
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(profiles);
        try
        {
            File.WriteAllBytes(path + ".new", ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser));
            File.Move(path + ".new", path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }
}

internal static class Subscription
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public sealed record Downloaded(string Content, string? Title);
    private static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public static bool TryGetAddress(string input, out Uri? address)
    {
        return Uri.TryCreate(input, UriKind.Absolute, out address)
            && address.Scheme is "https" or "http"
            && address.Host.Length > 0;
    }

    internal static IEnumerable<Uri> RoutingAddresses(Uri address)
    {
        string path = address.AbsolutePath.TrimEnd('/');
        if (address.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length != 0 || path.Length == 0)
            yield break;

        int separator = path.LastIndexOf('/');
        if (separator > 0 && path[(separator + 1)..].ToLowerInvariant() is
            "mihomo" or "clash" or "stash" or "singbox" or "json" or "v2ray-json")
            path = path[..separator];

        foreach (string format in new[] { "/mihomo", "/clash" })
        {
            var candidate = new UriBuilder(address) { Path = path + format, Fragment = "" }.Uri;
            if (!candidate.Equals(address))
                yield return candidate;
        }
    }

    public static Task<Downloaded> DownloadWithRoutingAsync(Uri address, CancellationToken cancellationToken,
        bool registerDevice = false, string? previousContent = null) =>
        DownloadWithRoutingAsync(address, cancellationToken, registerDevice, previousContent, DownloadAsync);

    internal static async Task<Downloaded> DownloadWithRoutingAsync(Uri address, CancellationToken cancellationToken,
        bool registerDevice, string? previousContent, Func<Uri, CancellationToken, bool, Task<Downloaded>> request)
    {
        Downloaded? original = null;
        IOException? originalError = null;
        try
        {
            original = await request(address, cancellationToken, registerDevice);
            if (SubscriptionRouting.HasRules(original.Content))
                return original;
        }
        catch (IOException error) when (error is not SubscriptionDeviceException)
        {
            originalError = error;
        }

        foreach (var candidate in RoutingAddresses(address))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var downloaded = await request(candidate, cancellationToken, registerDevice);
                if (SubscriptionRouting.HasRules(downloaded.Content))
                    return downloaded with { Title = downloaded.Title ?? original?.Title };
            }
            catch (IOException error) when (error is not SubscriptionDeviceException)
            {
                // Older endpoints may only serve the original subscription format.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (previousContent is not null && SubscriptionRouting.HasRules(previousContent))
            throw new IOException("Сервер не вернул правила. Сохранена последняя полная версия подписки.");
        if (originalError is not null)
            throw originalError;
        return original!;
    }

    internal static string? ReadTitle(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || header.Length > 2048)
            return null;
        string title = header.Trim();
        if (title.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            try { title = StrictUtf8.GetString(Convert.FromBase64String(title["base64:".Length..])).Trim(); }
            catch (Exception error) when (error is FormatException or DecoderFallbackException) { return null; }
        }
        return title.Length is > 0 and <= 160 && !title.Any(char.IsControl) ? title : null;
    }

    public static async Task<Downloaded> DownloadAsync(Uri address, CancellationToken cancellationToken, bool registerDevice = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await GetResponseAsync(address, registerDevice, deadline.Token);
            SubscriptionDevice.CheckHeaders(response.Headers);
            if (!response.IsSuccessStatusCode)
                throw new IOException($"Сервер подписки вернул HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength > MaxBytes)
                throw new IOException("Размер подписки превышает 8 МБ.");

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[16384];
            int count;
            while ((count = await stream.ReadAsync(chunk, deadline.Token)) != 0)
            {
                if (buffer.Length + count > MaxBytes)
                    throw new IOException("Размер подписки превышает 8 МБ.");
                buffer.Write(chunk, 0, count);
            }
            string? title = response.Headers.TryGetValues("profile-title", out var titles) ? ReadTitle(titles.FirstOrDefault()) : null;
            return new(Encoding.UTF8.GetString(buffer.ToArray()).Trim().TrimStart('\uFEFF'), title);
        }
        catch (HttpRequestException)
        {
            throw new IOException("Не удалось загрузить подписку. Проверьте адрес и доступ к сети.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Сервер подписки не ответил вовремя.");
        }
    }

    private static async Task<HttpResponseMessage> GetResponseAsync(Uri address, bool registerDevice, CancellationToken ct)
    {
        for (int redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd($"mihomo/{MihomoVersions.Active.Version.ToString(3)} VPNDesktop/{GitHubUpdates.InstalledVersion}");
            if (registerDevice)
                SubscriptionDevice.AddHeaders(address, request.Headers);
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (redirects == 5 || (int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) ||
                response.Headers.Location is not { } location || !Uri.TryCreate(address, location, out var target) ||
                target.Scheme is not ("http" or "https") || target.UserInfo.Length != 0 ||
                (address.Scheme == Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttps))
                return response;

            response.Dispose();
            address = target;
        }
    }
}
