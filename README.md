<div align="center">

# 🎵 Kvasir

**Музыкальный Discord-бот на .NET 10 с изолированными внешними коннекторами**

[![CI](https://github.com/Timbuhtuk/Kvasir/actions/workflows/ci.yml/badge.svg?branch=ToatalRebuild)](https://github.com/Timbuhtuk/Kvasir/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Timbuhtuk/Kvasir?display_name=tag&sort=semver)](https://github.com/Timbuhtuk/Kvasir/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D4)](#требования)

</div>

Kvasir воспроизводит музыку в голосовых каналах Discord, управляет очередью и плейлистами и показывает интерактивную музыкальную панель. Ядро не зависит от конкретной Discord-библиотеки: при очередном изменении API переписывается коннектор, а не логика бота.

## Возможности

- поиск по названию и прямой ссылке;
- очередь, пауза, продолжение, повтор, пропуск и очистка;
- плейлисты, история воспроизведения и популярные треки;
- интерактивная панель с кнопками и выпадающими списками;
- YouTube через `YoutubeExplode` с резервной загрузкой через `yt-dlp`;
- необязательное уточнение метаданных через Spotify;
- локальное хранение в SQLite;
- голосовое соединение Discord с DAVE/E2EE;
- журналирование по серверам и категориям.

## Архитектура

```text
Entities ← Application ← Infrastructure
               ↑               ↑
               │               │
     Connectors/Media     Kvasir/Connectors
   Spotify / YouTube          NetCord
```

`Application` работает только с интерфейсами `IBotConnector`, `IBotVoiceConnection`, `IVideoFinderService` и `IAudioDownloaderService`. NetCord находится на внешней границе проекта. Подробности и порядок замены библиотеки описаны в [ARCHITECTURE.md](ARCHITECTURE.md).

Граница автоматически проверяется командой:

```powershell
.\scripts\check-architecture.ps1
```

## Требования

- Windows x64;
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) для сборки или .NET 10 Runtime для готового релиза;
- Discord-приложение с ботом и разрешениями `Guilds`, `Guild Messages`, `Message Content`, `Guild Voice States`;
- `ffmpeg.exe`, `ffplay.exe` и `ffprobe.exe` в `Kvasir/appdata`;
- `yt-dlp.exe` в `Kvasir/appdata`.

Нативные библиотеки DAVE и Opus уже входят в репозиторий и готовый релиз.

## Быстрый запуск из исходников

```powershell
git clone https://github.com/Timbuhtuk/Kvasir.git
cd Kvasir
Copy-Item Kvasir/appdata/configuration.json.example Kvasir/appdata/configuration.json
.\scripts\download-dependencies.ps1
```

Скрипт автоматически скачивает свежий `yt-dlp`. FFmpeg можно положить в `Kvasir/appdata` вручную либо передать скрипту URL ZIP-архива:

```powershell
.\scripts\download-dependencies.ps1 -FfmpegUrl "https://example.com/ffmpeg.zip"
```

Передайте токен через переменную окружения текущего окна PowerShell:

```powershell
$env:KVASIR_DISCORD_TOKEN = "YOUR_NEW_DISCORD_TOKEN"
cd Kvasir
dotnet run
```

Для постоянного запуска задайте `KVASIR_DISCORD_TOKEN` в настройках среды операционной системы или менеджере секретов. Не записывайте токен в Git.

После подключения выполните `/anchor` в текстовом канале, где должна находиться музыкальная панель. Затем войдите в голосовой канал и вызовите `/play`.

## Команды

| Команда | Назначение |
|---|---|
| `/play <запрос>` | Найти трек, добавить в очередь и начать воспроизведение |
| `/pause` | Приостановить воспроизведение |
| `/resume` | Продолжить воспроизведение |
| `/skip` | Перейти к следующему треку |
| `/clear` | Очистить очередь |
| `/leave` | Отключить бота от голосового канала |
| `/anchor` | Назначить текущий текстовый канал для музыкальной панели |
| `/help` | Показать краткую справку |

Команды `/reboot` и `/execute` доступны только пользователю с идентификатором `owner_id`. Не задавайте `owner_id`, если удалённое выполнение команд не требуется.

## Spotify

Spotify используется только для уточнения названия и исполнителя. Включите интеграцию в локальном `configuration.json`:

```json
{
  "spotify_settings": {
    "SPOTIFY_ENABLED": true,
    "SPOTIFY_CLIENT_ID": "...",
    "SPOTIFY_CLIENT_SECRET": "..."
  }
}
```

Этот файл исключён из Git. Без Spotify бот продолжает искать и загружать музыку через YouTube.

## Сборка и выпуск

```powershell
dotnet restore Kvasir.sln
dotnet build Kvasir.sln --configuration Release --no-restore
dotnet publish Kvasir/Kvasir.csproj --configuration Release --runtime win-x64 --self-contained false
```

Теги вида `v*` запускают GitHub Actions: проект проверяется, публикуется для Windows x64, упаковывается в ZIP и прикрепляется к GitHub Release.

## Безопасность

- рабочий `Kvasir/appdata/configuration.json` не отслеживается Git;
- токен Discord рекомендуется передавать только через `KVASIR_DISCORD_TOKEN`;
- cookies, SQLite-база, логи, скачанная музыка и внешние исполняемые файлы исключены из репозитория;
- `configuration.json.example` содержит только безопасные значения по умолчанию;
- если токен когда-либо появился в сообщении, журнале или коммите, его нужно немедленно сбросить в Discord Developer Portal.

## Лицензии нативных компонентов

Вместе с `libdave.dll` и `opus.dll` распространяются соответствующие файлы `libdave-LICENSE` и `opus-COPYING`.
