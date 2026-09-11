# Архитектура Kvasir

Бизнес-логика бота не должна знать, какая Discord-библиотека используется.

## Зависимости

```text
Entities <- Application <- Infrastructure
                   ^               ^
                   |               |
       Connectors (media)    Kvasir/Connectors (Discord)
          ^                            ^
          |                            |
 Spotify/YouTube/yt-dlp             NetCord
```

- `Entities` содержит сущности и перечисления.
- `Application` содержит очередь, воспроизведение, плейлисты и построение модели музыкального интерфейса.
- `Application/Interfaces/IBotConnector.cs` описывает сообщения, каналы и подключение к голосу, которые нужны приложению.
- `Application/Models` содержит собственные модели платформы. Здесь запрещены типы NetCord.
- `Kvasir/Connectors/NetCordBotConnector.cs` переводит собственные модели приложения в NetCord и обратно.
- `Connectors/Media` содержит реализации поиска и загрузки аудио через Spotify, YouTube и yt-dlp.
- `Kvasir/Commands` и `Kvasir/Handlers` являются входными адаптерами: они принимают события NetCord и передают в `Application` только собственные модели и простые значения.
- `Infrastructure` реализует хранение данных и не зависит от Discord-библиотеки.

## Как заменить Discord-библиотеку

1. Создать новый коннектор, реализующий `IBotConnector` и `IBotVoiceConnection`.
2. Создать маппинг серверов, каналов и сообщений в модели из `Application/Models`.
3. Переписать только регистрацию клиента, команды и обработчики событий в запускаемом проекте.
4. Зарегистрировать новый коннектор как `IBotConnector`.
5. Выполнить `scripts/check-architecture.ps1` и собрать решение.

Классы `GuildService`, `MusicClientService` и `MusicViewService` при такой замене изменяться не должны.

Для замены Spotify, YouTube или загрузчика реализуется `IVideoFinderService` либо `IAudioDownloaderService` в проекте `Connectors`. Ядро и репозитории зависят только от этих интерфейсов.

## Голос

`Application` пишет PCM 48 kHz, stereo, signed 16-bit в поток, который возвращает `IBotVoiceConnection.CreatePcmStream()`. Кодирование Opus и отправка пакетов являются обязанностью коннектора. Поэтому смена voice API или кодека конкретной Discord-библиотеки не затрагивает музыкальную очередь.

## Секреты

Discord-токен передаётся через переменную окружения `KVASIR_DISCORD_TOKEN`. Не добавляйте настоящий токен в `configuration.json`, сообщения, логи или историю Git. Массив `tokens` поддерживается только для обратной совместимости со старыми локальными конфигурациями.
