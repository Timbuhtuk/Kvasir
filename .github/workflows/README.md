# GitHub Actions Workflows

## Настройка Secrets

Для работы release workflow необходимо настроить следующие secrets в настройках репозитория (Settings → Secrets and variables → Actions):

### FFMPEG_URL (опционально)
URL для скачивания архива FFmpeg для Windows. Если не указан, FFmpeg не будет скачан автоматически.

Примеры:
- `https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip`
- `https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip`

### YTDLP_URL (опционально)
URL для скачивания yt-dlp.exe. Если не указан, будет использована последняя версия с GitHub.

Пример:
- `https://github.com/yt-dlp/yt-dlp/releases/download/2024.01.07/yt-dlp.exe`

## Локальная разработка

Для локальной разработки используйте скрипт `scripts/download-dependencies.ps1` для скачивания необходимых файлов.

## Workflows

### CI
Запускается при push и pull request в основные ветки. Проверяет сборку, миграции SQLite, HTTP-контракты Discord и Spotify, нативные FFmpeg, yt-dlp, Opus и DAVE, темы Tessera и именованные каналы Windows. Discord и Spotify в CI получают тестовые HTTP-ответы. Нативный тест скачивает WAV с локального TCP-сервера.

### External API health

Запускается каждые пять минут из ветки по умолчанию и вручную через **Actions → External API health → Run workflow**. GitHub может задерживать задания и отключает расписание после 60 дней без активности в публичном репозитории. [Правила запуска GitHub Actions](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#schedule)

Ошибка даёт красный статус с именем провайдера. У аккаунта, которому GitHub отправляет уведомления о расписании, можно включить **Actions → Only notify for failed workflows**. [Настройки уведомлений](https://github.com/settings/notifications), [правила доставки](https://docs.github.com/en/actions/concepts/workflows-and-actions/notifications-for-workflow-runs)

Для YouTube достаточно опубликовать workflow в ветке по умолчанию. Проверки используют публичный ролик и плейлист, берут yt-dlp и FFmpeg из кэша с ежедневным обновлением и проверяют оба пути скачивания независимо. Тестовые ссылки можно переопределить переменными Actions `KVASIR_PROBE_YOUTUBE_VIDEO` и `KVASIR_PROBE_YOUTUBE_PLAYLIST`.

| Проверка | Actions variables | Actions secrets |
| --- | --- | --- |
| Spotify | `KVASIR_PROBE_SPOTIFY_ENABLED=true`, `KVASIR_PROBE_SPOTIFY_TRACK`, `KVASIR_PROBE_SPOTIFY_PLAYLIST` | `KVASIR_PROBE_SPOTIFY_CLIENT_ID`, `KVASIR_PROBE_SPOTIFY_CLIENT_SECRET` |
| Discord | `KVASIR_PROBE_DISCORD_ENABLED=true`, `KVASIR_PROBE_DISCORD_GUILD`, `KVASIR_PROBE_DISCORD_TEXT`, `KVASIR_PROBE_DISCORD_VOICE` | `KVASIR_PROBE_DISCORD_TOKEN` |

Для Spotify нужны полные ссылки на доступные приложению трек и плейлист. Для Discord нужны ID тестового сервера и его каналов, а также токен отдельного тестового бота с доступом к текстовому каналу и правами подключения и разговора в голосовом. Бот на короткое время подключается к голосовому каналу и передаёт кадр тишины. Включённая проверка с отсутствующим параметром завершается ошибкой.

Локальные прогоны используют те же команды, что и Actions. Для `--native` положите `ffmpeg.exe` и `yt-dlp.exe` в `src/Kvasir/appdata` до сборки. Для живых проверок Spotify и Discord передайте параметры из таблицы через окружение.

```powershell
dotnet run --project tests/Kvasir.Tests -c Release
dotnet run --project tests/Kvasir.Tests -c Release -- --native
dotnet run --project tests/Kvasir.Desktop.Tests -c Release
dotnet run --project tests/Kvasir.Tests -c Release -- --live-media-probe
dotnet run --project tests/Kvasir.Tests -c Release -- --live-youtube-explode
dotnet run --project tests/Kvasir.Tests -c Release -- --live-spotify
dotnet run --project tests/Kvasir.Tests -c Release -- --live-discord
```

### Release
Запускается при создании тега `v*` или вручную. Собирает приложение и создает архив для релиза.
