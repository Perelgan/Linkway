using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace VPNDesktop;

internal sealed record CabinetSession(string Site, string Account, string RefreshToken)
{
    private static readonly string FilePath = Path.Combine(ProfileStore.DirectoryPath, "cabinet.bin");

    public static CabinetSession? Load()
    {
        if (!File.Exists(FilePath))
            return null;
        byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
        try
        {
            var saved = JsonSerializer.Deserialize<CabinetSession>(clear);
            return Uri.TryCreate(saved?.Site, UriKind.Absolute, out var site)
                && site.AbsoluteUri == CabinetApi.Site.AbsoluteUri ? saved : null;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public void Save()
    {
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(this);
        try
        {
            File.WriteAllBytes(FilePath + ".new", ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser));
            File.Move(FilePath + ".new", FilePath, true);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public static void Delete() => File.Delete(FilePath);
}

internal sealed class CabinetApi
{
    public static Uri Site { get; } = new("https://cabinet.iodtechnologies.store/");
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = 2 * 1024 * 1024
    };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public sealed record User(string? Email, string? Username, string? FirstName);
    public sealed record Tokens(string AccessToken, string RefreshToken, User? User);
    public sealed record TelegramChallenge(string Token, string BotUsername, int ExpiresIn);
    public sealed record Plan(long Id, string? Status, string? TariffName, string? SubscriptionUrl,
        DateTimeOffset? EndDate, bool? IsActive, bool IsExpired, bool IsLimited, bool IsDailyPaused)
    {
        public string? DisplayName => string.IsNullOrWhiteSpace(TariffName) ? null : TariffName.Trim();

        public bool CanConnect => Id > 0 && !IsExpired && !IsLimited && !IsDailyPaused && (IsActive
            ?? (Status is "active" or "trial" && (EndDate is null || EndDate > DateTimeOffset.UtcNow)));
    }
    private sealed record PlanList(Plan[] Subscriptions);
    private sealed record SinglePlan(Plan? Subscription);
    private sealed record ConnectionLink(string? SubscriptionUrl);

    public Task<Tokens> LoginAsync(string email, string password, CancellationToken ct) =>
        ReadAsync<Tokens>(HttpMethod.Post, "auth/email/login", new { email, password }, null, ct);

    public Task<TelegramChallenge> RequestTelegramAsync(CancellationToken ct) =>
        ReadAsync<TelegramChallenge>(HttpMethod.Post, "auth/deeplink/request", null, null, ct);

    public async Task<Tokens?> PollTelegramAsync(string token, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "auth/deeplink/poll", new { token }, null, ct);
        if (response.StatusCode == HttpStatusCode.Accepted)
            return null;
        return Read<Tokens>(response);
    }

    public async Task<string?> GetTariffNameAsync(long id, string accessToken, CancellationToken ct)
    {
        string multiRoute = FormattableString.Invariant($"subscriptions/{id}");
        using (var response = await SendAsync(HttpMethod.Get, multiRoute, null, accessToken, ct))
        {
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var details = Read<Plan>(response);
                    if (details.Id == id && details.DisplayName is { } name)
                        return name;
                }
                catch (IOException) { /* Some older deployments return a different body for this path. */ }
            }
        }

        string legacyRoute = FormattableString.Invariant($"subscription?subscription_id={id}");
        using var legacy = await SendAsync(HttpMethod.Get, legacyRoute, null, accessToken, ct);
        // Older cabinets may ignore subscription_id and return the default subscription.
        return legacy.IsSuccessStatusCode && Read<SinglePlan>(legacy).Subscription is { } plan && plan.Id == id
            ? plan.DisplayName : null;
    }

    public Task<Tokens> RefreshAsync(string refreshToken, CancellationToken ct) =>
        ReadAsync<Tokens>(HttpMethod.Post, "auth/refresh", new { refresh_token = refreshToken }, null, ct);

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "auth/logout", new { refresh_token = refreshToken }, null, ct);
        EnsureSuccess(response);
    }

    public async Task<Plan[]> GetPlansAsync(string accessToken, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "subscriptions", null, accessToken, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            var legacy = await ReadAsync<SinglePlan>(HttpMethod.Get, "subscription", null, accessToken, ct);
            return legacy.Subscription is { } plan ? [plan] : [];
        }
        return Read<PlanList>(response).Subscriptions
            ?? throw new IOException("Кабинет не вернул список подписок.");
    }

    public async Task<string?> GetConnectionLinkAsync(long id, string accessToken, CancellationToken ct)
    {
        string route = FormattableString.Invariant($"subscription/connection-link?subscription_id={id}");
        var link = await ReadAsync<ConnectionLink>(HttpMethod.Get, route, null, accessToken, ct);
        return link.SubscriptionUrl;
    }

    private async Task<T> ReadAsync<T>(HttpMethod method, string route, object? body, string? bearer, CancellationToken ct)
    {
        using var response = await SendAsync(method, route, body, bearer, ct);
        return Read<T>(response);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, object? body, string? bearer,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(Site, "api/cabinet/" + route));
        request.Headers.Accept.ParseAdd("application/json");
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (method == HttpMethod.Post)
        {
            string csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            request.Headers.Add("Cookie", "csrf_token=" + csrf);
            request.Headers.Add("X-CSRF-Token", csrf);
        }
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        try
        {
            return await Http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new IOException("Не удалось связаться с кабинетом. Проверьте доступ к сети.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Кабинет не ответил вовремя.");
        }
    }

    private static T Read<T>(HttpResponseMessage response)
    {
        EnsureSuccess(response);
        try
        {
            using var stream = response.Content.ReadAsStream();
            return JsonSerializer.Deserialize<T>(stream, Json)
                ?? throw new IOException("Кабинет вернул пустой ответ.");
        }
        catch (JsonException)
        {
            throw new IOException("Ответ кабинета не распознан. Проверьте версию Bedolaga Cabinet.");
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new CabinetApiException(response.StatusCode);
    }
}

internal sealed class CabinetApiException(HttpStatusCode status) : Exception(status switch
{
    HttpStatusCode.Unauthorized => "Кабинет отклонил вход. Проверьте данные или войдите заново.",
    HttpStatusCode.Forbidden => "Кабинет запретил доступ. Проверьте настройки входа и подтверждение email.",
    HttpStatusCode.NotFound => "Запрошенный API кабинета недоступен. Проверьте версию Bedolaga.",
    HttpStatusCode.Gone => "Время подтверждения истекло. Начните вход заново.",
    HttpStatusCode.TooManyRequests => "Слишком много запросов. Повторите попытку через минуту.",
    _ when (int)status is >= 300 and < 400 => "Кабинет перенаправил запрос. Проверьте работу сервиса.",
    _ => $"Кабинет вернул ошибку HTTP {(int)status}."
})
{
    public HttpStatusCode Status { get; } = status;
}
