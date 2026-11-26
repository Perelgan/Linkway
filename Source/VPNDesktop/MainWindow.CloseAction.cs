using System.Windows;
using System.Windows.Controls;

namespace VPNDesktop;

internal enum WindowCloseAction { Ask, Tray, KeepVpn, Disconnect }

public partial class MainWindow
{
    private static readonly string[] CloseActionLabels =
        ["Спрашивать при закрытии", "Свернуть в трей", "Закрыть Linkway, оставить VPN", "Закрыть Linkway и отключить VPN"];

    private bool closePromptOpen, systemEnding;
    private WindowCloseAction SavedCloseAction => Enum.IsDefined(clientSettings.CloseAction)
        ? clientSettings.CloseAction : WindowCloseAction.Ask;

    private WindowCloseAction? ChooseCloseAction()
    {
        var body = new StackPanel { Margin = new Thickness(20) };
        var dialog = CreateRoutingDialog(this, "Закрытие Linkway", body);
        dialog.Width = 440;
        var options = new List<RadioButton>();

        foreach (var action in new[] { WindowCloseAction.Tray, WindowCloseAction.KeepVpn, WindowCloseAction.Disconnect })
        {
            var option = CreateCloseActionOption(action);
            body.Children.Add(option);
            options.Add(option);
        }

        var remember = new CheckBox
        {
            Content = "Запомнить выбранное действие", Style = (Style)FindResource("SettingToggle"),
            Margin = new Thickness(0, 5, 0, 12)
        };
        body.Children.Add(remember);

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        body.Children.Add(actions);
        var confirm = new Button { Content = "Продолжить", IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(confirm);
        actions.Children.Add(new Button
        {
            Content = "Отмена", IsCancel = true, Style = (Style)FindResource(typeof(Button)),
            Margin = new Thickness(8, 0, 0, 0)
        });

        closePromptOpen = true;
        try
        {
            if (dialog.ShowDialog() != true)
                return null;
            var selected = (WindowCloseAction)options.Single(option => option.IsChecked == true).Tag;
            if (remember.IsChecked == true)
            {
                var updated = clientSettings with { CloseAction = selected };
                try
                {
                    updated.Save();
                    clientSettings = updated;
                    CloseActionBox.SelectedIndex = (int)selected;
                }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                {
                    MessageBox.Show(this, "Не удалось сохранить действие по умолчанию.", "Linkway");
                }
            }
            return selected;
        }
        finally { closePromptOpen = false; }
    }

    internal RadioButton CreateCloseActionOption(WindowCloseAction action) => new()
    {
        Content = CloseActionLabels[(int)action], GroupName = "CloseAction", Tag = action,
        Style = (Style)FindResource("CloseActionOption"), Margin = new Thickness(0, 0, 0, 4),
        IsEnabled = action != WindowCloseAction.Disconnect || !backgroundUnavailable,
        IsChecked = action == (exitRequested ? WindowCloseAction.KeepVpn : WindowCloseAction.Tray)
    };
}
