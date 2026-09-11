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
Запускается при push и pull request в основные ветки. Проверяет, что проект собирается без ошибок.

### Release
Запускается при создании тега `v*` или вручную. Собирает приложение и создает архив для релиза.
