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
Запускается при push и pull request в основные ветки. Проверяет сборку, HTTP-контракты Discord и Spotify на тестовых ответах, а также реальные миграции и запросы SQLite.

### External API health

Запускается при push в `main`, по расписанию каждые пять минут и вручную через **Actions → External API health → Run workflow**. GitHub может задерживать задания и отключает расписание после 60 дней без активности в публичном репозитории. [Правила GitHub Actions](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#schedule)

Провал проверки даёт красный статус с именем провайдера. Пользователь, создавший или изменивший расписание, может включить **Actions → Only notify for failed workflows**. [Настройки уведомлений](https://github.com/settings/notifications), [правила доставки](https://docs.github.com/en/actions/concepts/workflows-and-actions/notifications-for-workflow-runs)

YouTube использует публичные фикстуры. YoutubeExplode проверяется через поиск, плейлист и скачивание в Kvasir. YoutubeDLSharp отдельно скачивает аудио через yt-dlp и передаёт его в конвертер. Нативный прогон скачивает WAV с локального сервера и проверяет FFmpeg, Opus и DAVE. По желанию замените фикстуры в Actions variables `KVASIR_PROBE_YOUTUBE_VIDEO` и `KVASIR_PROBE_YOUTUBE_PLAYLIST`.

Если YouTube требует вход с IP GitHub-hosted runner, в Actions variable `KVASIR_PROBE_YOUTUBE_RUNNER` можно указать метку выделенного Windows runner с доступом к публичным фикстурам. Эту же метку использует живой импорт Spotify, поскольку он также обращается к YouTube. Для публичного репозитория важна изоляция выделенного runner. [Рекомендации GitHub по self-hosted runners](https://docs.github.com/en/actions/reference/security/secure-use#hardening-for-self-hosted-runners)

| Проверка | Actions secrets | Actions variables |
| --- | --- | --- |
| Discord | `KVASIR_PROBE_DISCORD_TOKEN` | `KVASIR_PROBE_DISCORD_ENABLED=true`, `KVASIR_PROBE_DISCORD_GUILD`, `KVASIR_PROBE_DISCORD_TEXT`, `KVASIR_PROBE_DISCORD_VOICE` |
| Spotify | `KVASIR_PROBE_SPOTIFY_CLIENT_ID`, `KVASIR_PROBE_SPOTIFY_CLIENT_SECRET` | `KVASIR_PROBE_SPOTIFY_ENABLED=true`, `KVASIR_PROBE_SPOTIFY_TRACK`, `KVASIR_PROBE_SPOTIFY_PLAYLIST` |

Для Discord укажите ID тестового сервера, текстового и голосового каналов. Токен принадлежит отдельному боту с правом видеть каналы, подключаться к голосовому и говорить. Проверка передаёт один кадр тишины. Для Spotify укажите ссылки на доступные приложению трек и небольшой плейлист с треками. Импорт проверяется через найденные Kvasir результаты YouTube. Включённая проверка с отсутствующим параметром завершится ошибкой.

Для запуска контрактов локально выполните `dotnet run --project Kvasir.Integration.Tests -c Release`. Для нативного прогона сначала соберите проект и запустите `scripts/install-test-media-tools.ps1 -Destination Kvasir.Integration.Tests/bin/Release/net10.0/appdata`, затем передайте `-- --native` в команду `dotnet run`.

### Release
Запускается при создании тега `v*` или вручную. Собирает приложение и создает архив для релиза.
