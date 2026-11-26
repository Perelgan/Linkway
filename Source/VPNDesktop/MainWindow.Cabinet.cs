using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace VPNDesktop;

public partial class MainWindow
{
    private CabinetSession? cabinetSession;
    private string? cabinetAccessToken;
    private CancellationTokenSource? telegramLogin;

    private async void RestoreCabinetOnLoad(object sender, RoutedEventArgs e)
    {
        Loaded -= RestoreCabinetOnLoad;
        await RunCabinetAsync(async () =>
        {
            cabinetSession = CabinetSession.Load();
            if (cabinetSession is not null)
                await SyncCabinetAsync();
        });
    }

    private void UpdateCabinetControls(bool idle, bool connected)
    {
        bool signedIn = cabinetSession is not null;
        CabinetLoginForm.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        CabinetAccountActions.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        CabinetLoginForm.IsEnabled = idle && !connected && profilesReadable;
        SyncCabinetButton.IsEnabled = idle && profilesReadable;
        SignOutCabinetButton.IsEnabled = idle;
        CancelTelegramLoginButton.Visibility = telegramLogin is null ? Visibility.Collapsed : Visibility.Visible;
        CancelTelegramLoginButton.IsEnabled = telegramLogin is { IsCancellationRequested: false } && !closing;
        CabinetAccountName.Text = cabinetSession?.Account ?? "";
    }

    private async void CabinetPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await LoginCabinetEmailAsync();
        }
    }

    private async void LoginCabinetEmail(object sender, RoutedEventArgs e) => await LoginCabinetEmailAsync();

    private Task LoginCabinetEmailAsync() => RunCabinetAsync(async () =>
    {
        string email = CabinetEmailBox.Text.Trim();
        string password = CabinetPasswordBox.Password;
        if (email.Length == 0 || password.Length == 0)
            throw new IOException("Введите email и пароль от кабинета.");
        CabinetPasswordBox.Clear();
        SetCabinetStatus("Входим в аккаунт…");
        var tokens = await new CabinetApi().LoginAsync(email, password, lifetime.Token);
        await CompleteCabinetLoginAsync(tokens, email);
    });

    private async void LoginCabinetTelegram(object sender, RoutedEventArgs e) => await RunCabinetAsync(async () =>
    {
        var api = new CabinetApi();
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        telegramLogin = pending;
        UpdateControls();
        try
        {
            SetCabinetStatus("Получаем ссылку на Telegram-бота…");
            var challenge = await api.RequestTelegramAsync(pending.Token);
            if (challenge.BotUsername is not { } bot || !Regex.IsMatch(bot, @"\A[A-Za-z0-9_]{5,32}\z")
                || challenge.Token is not { } token || !Regex.IsMatch(token, @"\A[A-Za-z0-9_-]{16,56}\z")
                || challenge.ExpiresIn <= 0)
                throw new IOException("Кабинет вернул некорректную ссылку на Telegram-бота.");

            var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Min(challenge.ExpiresIn, 300));
            Process.Start(new ProcessStartInfo($"https://t.me/{bot}?start=webauth_{token}") { UseShellExecute = true });
            SetCabinetStatus("Откройте бота в Telegram и подтвердите вход.");
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), pending.Token);
                if (await api.PollTelegramAsync(token, pending.Token) is { } result)
                {
                    pending.Token.ThrowIfCancellationRequested();
                    await CompleteCabinetLoginAsync(result, "Telegram");
                    return;
                }
            }
            throw new IOException("Время подтверждения истекло. Начните вход заново.");
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested && !closing)
        {
            SetCabinetStatus("Вход через Telegram отменён.");
        }
        finally { telegramLogin = null; }
    });

    private void CancelTelegramLogin(object sender, RoutedEventArgs e)
    {
        telegramLogin?.Cancel();
        UpdateControls();
    }

    private async Task CompleteCabinetLoginAsync(CabinetApi.Tokens tokens, string fallback)
    {
        ValidateCabinetTokens(tokens);
        string account = tokens.User?.Email ?? tokens.User?.Username ?? tokens.User?.FirstName ?? fallback;
        var saved = new CabinetSession(CabinetApi.Site.AbsoluteUri, account, tokens.RefreshToken);
        saved.Save();
        cabinetSession = saved;
        cabinetAccessToken = tokens.AccessToken;
        UpdateControls();
        await SyncCabinetAsync(openConnectionPage: true);
    }

    private async void RefreshCabinet(object sender, RoutedEventArgs e) =>
        await RunCabinetAsync(() => SyncCabinetAsync(openConnectionPage: true));

    private async void SignOutCabinet(object sender, RoutedEventArgs e) => await RunCabinetAsync(async () =>
    {
        if (cabinetSession is not { } saved)
            return;
        await ClearCabinetSessionAsync();
        try { await new CabinetApi().LogoutAsync(saved.RefreshToken, lifetime.Token); }
        catch (Exception error) when (error is IOException or CabinetApiException)
        {
            SetCabinetStatus("Вы вышли из аккаунта на этом устройстве. Кабинет не подтвердил отзыв сессии.");
            return;
        }
        SetCabinetStatus("Вы вышли из аккаунта.");
    });

    private async Task ClearCabinetSessionAsync()
    {
        CabinetSession.Delete();
        cabinetSession = null;
        cabinetAccessToken = null;
        try
        {
            if (activeServer?.Source.CabinetPlanId is not null)
                await StopAsync();
        }
        finally
        {
            profiles.RemoveAll(profile => profile.CabinetPlanId is not null);
            ShowProfiles(Current?.Id);
        }
    }

    private async Task SyncCabinetAsync(bool openConnectionPage = false)
    {
        if (cabinetSession is not { } saved)
            return;
        var api = new CabinetApi();
        SetCabinetStatus("Загружаем подписки аккаунта…");
        try
        {
            cabinetAccessToken ??= await RefreshCabinetTokenAsync(api, saved);
            CabinetApi.Plan[] plans;
            try { plans = await api.GetPlansAsync(cabinetAccessToken, lifetime.Token); }
            catch (CabinetApiException error) when (error.Status == HttpStatusCode.Unauthorized)
            {
                cabinetAccessToken = await RefreshCabinetTokenAsync(api, cabinetSession!);
                plans = await api.GetPlansAsync(cabinetAccessToken, lifetime.Token);
            }

            var previous = profiles.Where(profile => profile.CabinetPlanId is not null)
                .ToDictionary(profile => profile.CabinetPlanId!.Value);
            var loaded = new List<Profile>();
            int unavailable = 0;
            string? deviceError = null;
            foreach (var plan in plans.Where(plan => plan.CanConnect).DistinctBy(plan => plan.Id))
            {
                previous.TryGetValue(plan.Id, out var existing);
                string? tariffName = plan.DisplayName;
                if (tariffName is null)
                {
                    try { tariffName = await api.GetTariffNameAsync(plan.Id, cabinetAccessToken, lifetime.Token); }
                    catch (IOException) { /* The title lookup is optional. Keep loading the servers. */ }
                }
                string name = tariffName ?? existing?.Name ?? "Тариф не указан";
                try
                {
                    string? url = plan.SubscriptionUrl;
                    if (string.IsNullOrWhiteSpace(url))
                        url = await api.GetConnectionLinkAsync(plan.Id, cabinetAccessToken, lifetime.Token);
                    if (url is null || !Subscription.TryGetAddress(url, out var address)
                        || address!.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length != 0)
                    {
                        unavailable++;
                        continue;
                    }

                    var profile = existing ?? new Profile(Guid.NewGuid(), name, null, "", [], null, plan.Id);
                    loaded.Add(await FetchSubscriptionAsync(profile with { Name = name, Subscription = address.AbsoluteUri },
                        address.AbsoluteUri));
                }
                catch (Exception error) when (error is IOException or Win32Exception
                    || error is CabinetApiException { Status: not HttpStatusCode.Unauthorized })
                {
                    unavailable++;
                    if (error is SubscriptionDeviceException)
                        deviceError ??= error.Message;
                    if (existing is not null && existing.Nodes.Any(node => node.IsServer))
                        loaded.Add(existing with { Name = name });
                }
            }

            lifetime.Token.ThrowIfCancellationRequested();
            if (activeServer?.Source.CabinetPlanId is { } activePlan
                && loaded.All(profile => profile.CabinetPlanId != activePlan))
                await StopAsync();
            var selected = loaded.FirstOrDefault();
            profiles = [.. profiles.Where(profile => profile.CabinetPlanId is null), .. loaded];
            ShowProfiles(selected?.Id ?? Current?.Id, preferSelected: openConnectionPage && session is null);
            if (openConnectionPage && selected is not null)
                ConnectionNav.IsChecked = true;

            int count = loaded.Sum(profile => profile.Nodes.Length);
            SetCabinetStatus(unavailable > 0 ? $"Серверов в списке: {count}. Не удалось обновить подписок: {unavailable}."
                : count > 0 ? $"Серверов из аккаунта: {count}." : "В аккаунте нет активных подписок.");
            if (deviceError is not null)
                SetCabinetStatus($"{deviceError} Рабочих серверов в списке: {count}.");
        }
        catch (CabinetApiException error) when (error.Status == HttpStatusCode.Unauthorized)
        {
            await ClearCabinetSessionAsync();
            throw new IOException("Сессия кабинета истекла. Войдите в аккаунт снова.");
        }
    }

    private async Task<string> RefreshCabinetTokenAsync(CabinetApi api, CabinetSession saved)
    {
        var tokens = await api.RefreshAsync(saved.RefreshToken, lifetime.Token);
        ValidateCabinetTokens(tokens);
        var refreshed = saved with { RefreshToken = tokens.RefreshToken };
        refreshed.Save();
        cabinetSession = refreshed;
        return tokens.AccessToken;
    }

    private static void ValidateCabinetTokens(CabinetApi.Tokens tokens)
    {
        if (string.IsNullOrWhiteSpace(tokens.AccessToken) || string.IsNullOrWhiteSpace(tokens.RefreshToken))
            throw new IOException("Кабинет не вернул данные сессии.");
    }

    private async Task RunCabinetAsync(Func<Task> action) => await RunAsync(async () =>
    {
        try { await action(); }
        catch (Exception error) when (error is IOException or CabinetApiException)
        {
            SetCabinetStatus(error.Message);
        }
    });

    private void SetCabinetStatus(string text) => CabinetStatus.Text = Message.Text = text;
}
