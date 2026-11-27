<div align="center">

# 🎵 Kvasir

**Музыкальный Discord-бот на .NET 10 с изолированными внешними коннекторами**

[![CI](https://github.com/Timbuhtuk/Kvasir/actions/workflows/ci.yml/badge.svg?branch=ToatalRebuild)](https://github.com/Timbuhtuk/Kvasir/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Timbuhtuk/Kvasir?display_name=tag&sort=semver)](https://github.com/Timbuhtuk/Kvasir/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D4)](#требования)

</div>

Kvasir воспроизводит музыку в голосовых каналах Discord, управляет очередью и плейлистами и показывает интерактивную музыкальную панель. Ядро не зависит от конкретной Discord-библиотеки: при очередном изменении API переписывается коннектор, а не логика бота.

В комплект входит WPF-пульт на Tessera.WpfStyles: запуск бота, управление музыкой, очередь, библиотека, правила сервера, диагностика и журнал без работы с консолью.

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
- отдельный WPF-пульт с чтением конфигурации из файла и общим с Discord состоянием музыки.

## Структура репозитория

Основное решение [Kvasir.sln](Kvasir.sln) включает проекты из `src/` и проверки из `tests/`. Поиск и обработку аудио можно открыть отдельно в [Kvasir.Media.sln](src/Kvasir.Media/Kvasir.Media.sln).

Документация находится в `docs/`, учебные примеры рефакторинга — в `examples/Refactoring/`, скрипты сборки и подготовки зависимостей — в `scripts/`. Локальные результаты публикации и проверки хранятся в `artifacts/`.

## Архитектура

```text
Entities ← Application ← Infrastructure
               ↑               ↑
               │               │
     src/Connectors/Media     src/Kvasir/Connectors
   Spotify / YouTube          NetCord
```

`Application` работает только с интерфейсами `IBotConnector`, `IBotVoiceConnection`, `IVideoFinderService` и `IAudioDownloaderService`. NetCord находится на внешней границе проекта. Подробности и порядок замены библиотеки описаны в [ARCHITECTURE.md](docs/ARCHITECTURE.md).

Граница автоматически проверяется командой:

```powershell
.\scripts\check-architecture.ps1
```

## Требования

- Windows x64;
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) для сборки. Готовые EXE и ZIP содержат .NET;
- Discord-приложение с ботом и разрешениями `Guilds`, `Guild Messages`, `Message Content`, `Guild Voice States`;
- FFmpeg и `yt-dlp.exe` в `src/Kvasir/appdata` при сборке из исходников. Оба компонента входят в готовые EXE и ZIP.

Нативные библиотеки DAVE и Opus уже входят в репозиторий и готовый релиз.

## Пульт WPF

Выберите `Kvasir.Desktop` запускаемым проектом в Visual Studio или выполните:

```powershell
dotnet build Kvasir.sln -c Release -m:1 -p:UseSharedCompilation=false
dotnet run --project src/Kvasir.Desktop/Kvasir.Desktop.csproj -c Release --no-build
```

После запуска бота выберите сервер и голосовой канал. В пульте доступны:

- запуск и остановка процесса бота;
- текущий трек, пауза, продолжение, пропуск, повтор, перемотка и громкость;
- добавление запросов, удаление и перестановка заказов в общей с Discord очереди;
- личные/общие плейлисты, порядок треков, импорт с прогрессом, история;
- роль DJ, голосование, справедливая очередь и лимиты участников;
- диагностика зависимостей, очистка и восстановление кэша;
- состояние процесса и живой журнал `stdout` / `stderr`;
- темы Tessera Dark, Light и Contrast.

Токен читается из массива `tokens` в `%LOCALAPPDATA%\Kvasir\configuration.json` при каждом запуске. Папка определяется стандартным API Windows и одинакова для пульта, бота и любой копии приложения. Шестерёнка слева от кнопки сворачивания открывает отдельное окно настроек: тема, конфигурация и папка данных. Достаточно один раз заполнить файл. Тема сохраняется между запусками. При закрытии окна пульт запрашивает штатную остановку и ждёт завершения аудио/импорта. Если бот не успел завершиться за 40 секунд, окно остаётся открытым с сообщением.

Пульт управляет запущенным им ботом через именованный канал Windows, доступный только текущему пользователю. Локальный оператор имеет права администратора бота. Его заказы и серверные плейлисты записываются от Discord ID бота. Локальные плейлисты доступны на всех серверах в пульте. [Документация пульта](docs/DESKTOP.md)

Настоящий пакет `Tessera.WpfStyles 0.3.0` находится в `vendor/nuget`; проект подключает этот источник относительным путём. Внешний checkout Tessera для сборки не нужен. MIT и лицензии шрифтов находятся в `vendor/Tessera.WpfStyles/licenses` и копируются в релиз.

## Быстрый запуск из исходников

```powershell
git clone https://github.com/Timbuhtuk/Kvasir.git
cd Kvasir
.\scripts\download-dependencies.ps1
```

Скрипт автоматически скачивает свежий `yt-dlp`. FFmpeg можно положить в `src/Kvasir/appdata` вручную либо передать скрипту URL ZIP-архива:

```powershell
.\scripts\download-dependencies.ps1 -FfmpegUrl "https://example.com/ffmpeg.zip"
```

При первом запуске приложение создаёт `%LOCALAPPDATA%\Kvasir\configuration.json` из встроенного шаблона. Введите токен в окне пульта. При ручной настройке укажите его в массиве `tokens`.

```json
"tokens": ["YOUR_DISCORD_BOT_TOKEN"]
```

Затем запускайте бот через пульт или консоль:

```powershell
dotnet run --project src/Kvasir/Kvasir.csproj
```

Бот повторно читает сохранённый файл при каждом запуске. Для консольного запуска дополнительно поддерживается `KVASIR_DISCORD_TOKEN`, если в конфигурации токена нет. При переходе со старой версии перенесите свою конфигурацию в папку Kvasir в AppData. Существующий файл никогда не заменяется шаблоном.

После подключения выполните `/anchor` в текстовом канале, где должна находиться музыкальная панель. Затем войдите в голосовой канал и вызовите `/play`.

## Команды

Команда `/help` показывает названия и описания slash-команд, зарегистрированных подключёнными модулями.

Музыкальная панель использует **Discord Components V2**: общую карточку плеера и персональные меню очереди, библиотеки, воспроизведения, режима компании и диагностики. Поддерживаются перестановка заказов, голосование за пропуск, роль DJ, личные/общие плейлисты без ограничения в 23 трека, импорт с отчётом, перемотка, громкость и управляемый кэш. Подробности: [DISCORD_PANEL.md](docs/DISCORD_PANEL.md).

| Команда | Назначение |
|---|---|
| `/play <запрос>` | Найти трек, добавить в очередь и начать воспроизведение |
| `/pause` | Приостановить воспроизведение |
| `/resume` | Продолжить воспроизведение |
| `/skip` | Перейти к следующему треку |
| `/clear` | Очистить очередь |
| `/leave` | Отключить бота от голосового канала |
| `/anchor` | Назначить текущий текстовый канал для музыкальной панели |
| `/panel` | Открыть персональную музыкальную библиотеку |
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
dotnet build Kvasir.sln --configuration Release --no-restore -m:1 -p:UseSharedCompilation=false
dotnet publish src/Kvasir/Kvasir.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64/bot -m:1 -p:UseSharedCompilation=false
dotnet publish src/Kvasir.Desktop/Kvasir.Desktop.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64 -m:1 -p:UseSharedCompilation=false
```

Теги вида `v*` запускают GitHub Actions. Workflow проверяет и публикует для Windows x64 один EXE и ZIP.

Скачайте `Kvasir-v<версия>-win-x64.exe` для запуска одним файлом. При первом запуске .NET извлечёт встроенные компоненты в пользовательский кэш. Для запуска из ZIP распакуйте архив и откройте `Kvasir.Desktop.exe`. По умолчанию пользовательская конфигурация, база и настройки хранятся в `%LOCALAPPDATA%\Kvasir`. Путь можно изменить в настройках пульта.

## Безопасность

- рабочая конфигурация хранится в `%LOCALAPPDATA%\Kvasir` и не входит в репозиторий;
- токен Discord хранится в локальной конфигурации; пульт маскирует его в своём журнале;
- cookies, SQLite-база, логи, скачанная музыка и внешние исполняемые файлы исключены из репозитория;
- `configuration.json.example` содержит только безопасные значения по умолчанию;
- если токен когда-либо появился в сообщении, журнале или коммите, его нужно немедленно сбросить в Discord Developer Portal.

## Лицензии нативных компонентов

Вместе с `libdave.dll` и `opus.dll` распространяются соответствующие файлы `libdave-LICENSE` и `opus-COPYING`.
