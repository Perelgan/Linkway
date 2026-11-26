# VPN Desktop

Портативный VPN-клиент для Windows 10/11 x64 на базе Mihomo. Поддерживает системный прокси, TUN, правила по приложениям и доменам и Kill Switch для TUN.

## Запуск

1. Скачайте `VPNDesktop-win-x64.zip`, распакуйте архив целиком и откройте `VPNDesktop.exe`. Для TUN запустите клиент от имени администратора. [Скачать релиз](https://github.com/Perelgan/VPNDesktop/releases/latest)
2. Добавьте ключ или URL подписки во вкладке **Источники** либо войдите в Bedolaga Cabinet через **Аккаунт**.
3. Выберите сервер во вкладке **Подключение** и нажмите кнопку подключения.

## Обновления

Откройте **Настройки → Обновления** и выберите канал. **Стабильная версия** предлагает обычные релизы. **Экспериментальная версия** также предлагает предварительные релизы. Проверьте наличие обновления и отключите VPN перед установкой. [Все релизы](https://github.com/Perelgan/VPNDesktop/releases)

## Сборка

Требуются Windows, SDK .NET 10 и Go. Команды запускаются из корня репозитория.

```powershell
pwsh -File .\Source\build.ps1 -ReleaseArchive
dotnet run --project .\Source\RoutingTests -c Release
```

Готовый архив появится в `Source\dist\VPNDesktop-win-x64.zip`. Для релиза используйте тег, совпадающий с версией сборки. Например, `v1.2.0` для стабильного выпуска или `v1.3.0-beta.1` для предварительного.

[История изменений](CHANGELOG.md) · [Лицензии зависимостей](Licenses) · [Исходники Mihomo](https://github.com/MetaCubeX/mihomo/tree/v1.19.34) · [Флаги Flagpedia](https://flagpedia.net/download/images)
