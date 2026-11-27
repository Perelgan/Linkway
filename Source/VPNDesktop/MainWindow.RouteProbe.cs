using System.IO;
using System.Windows;
using System.Windows.Input;

namespace VPNDesktop;

public partial class MainWindow
{
    private void ProbeRouteKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ProbeRouteButton.IsEnabled)
        {
            e.Handled = true;
            RunRouteProbe(sender, e);
        }
    }

    private async void RunRouteProbe(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (RouteProbeTarget.Parse(ProbeRouteInput.Text) is not { } target)
        {
            ProbeRouteOutput.Text = "Введите корректный домен, IP-адрес или URL.";
            return;
        }
        if (session is not { } core)
        {
            ProbeRouteOutput.Text = "Подключите VPN, чтобы проверить маршрут.";
            return;
        }

        ProbeRouteOutput.Text = $"Проверяем {target.Authority}…";
        try
        {
            var result = await core.ProbeRouteAsync(target.Authority, lifetime.Token);
            string? outcome = result.ProxyStatus switch
            {
                200 => null,
                int status => $"Прокси вернул HTTP {status}.",
                _ => null
            };
            ProbeRouteOutput.Text = result.Policy is { } policy
                ? string.Join("\n", new[] { target.Authority, $"Решение · {policy}",
                    Row("Правило", result.Rule), Row("Выход", result.Outbound), Row("Цепочка", result.Chain), outcome }
                    .OfType<string>())
                : $"{target.Authority}\n{(outcome is null ? "" : outcome + " ")}Mihomo не зафиксировал маршрут.";
        }
        catch (IOException error) when (error.Message == "Неизвестная операция фонового сеанса Linkway.")
        {
            ProbeRouteOutput.Text = "Фоновый сеанс запущен старой версией. Переподключите VPN и повторите проверку.";
        }
        catch (Exception error) when (IsHostCommunicationError(error))
        {
            ProbeRouteOutput.Text = "Не удалось получить результат от фонового Mihomo. Повторите проверку.";
        }

        static string? Row(string label, string? value) =>
            string.IsNullOrEmpty(value) ? null : $"{label} · {value}";
    });
}
