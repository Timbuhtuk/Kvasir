# Скрипт для скачивания зависимостей для локальной разработки

param(
    [string]$FfmpegUrl = "",
    [string]$YtDlpUrl = ""
)

$appdataPath = Join-Path $PSScriptRoot "..\Kvasir\appdata"
New-Item -ItemType Directory -Force -Path $appdataPath | Out-Null

Write-Host "Downloading dependencies to $appdataPath" -ForegroundColor Green

# Скачиваем yt-dlp
if ($YtDlpUrl) {
    Write-Host "Downloading yt-dlp from custom URL..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri $YtDlpUrl -OutFile (Join-Path $appdataPath "yt-dlp.exe")
} else {
    Write-Host "Downloading latest yt-dlp from GitHub..." -ForegroundColor Yellow
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest"
        $ytdlpAsset = $release.assets | Where-Object { $_.name -like "*yt-dlp.exe" } | Select-Object -First 1
        if ($ytdlpAsset) {
            Invoke-WebRequest -Uri $ytdlpAsset.browser_download_url -OutFile (Join-Path $appdataPath "yt-dlp.exe")
            Write-Host "✓ yt-dlp downloaded successfully" -ForegroundColor Green
        } else {
            Write-Warning "Could not find yt-dlp.exe in latest release"
        }
    } catch {
        Write-Error "Failed to download yt-dlp: $_"
    }
}

# Скачиваем FFmpeg
if ($FfmpegUrl) {
    Write-Host "Downloading FFmpeg from custom URL..." -ForegroundColor Yellow
    $ffmpegZip = Join-Path $env:TEMP "ffmpeg.zip"
    try {
        Invoke-WebRequest -Uri $FfmpegUrl -OutFile $ffmpegZip
        $extractPath = Join-Path $env:TEMP "ffmpeg_temp"
        Expand-Archive -Path $ffmpegZip -DestinationPath $extractPath -Force

        # Ищем exe файлы
        $ffmpegExe = Get-ChildItem -Path $extractPath -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
        $ffplayExe = Get-ChildItem -Path $extractPath -Recurse -Filter "ffplay.exe" | Select-Object -First 1
        $ffprobeExe = Get-ChildItem -Path $extractPath -Recurse -Filter "ffprobe.exe" | Select-Object -First 1

        if ($ffmpegExe) {
            Copy-Item $ffmpegExe.FullName -Destination (Join-Path $appdataPath "ffmpeg.exe")
            Write-Host "✓ ffmpeg.exe downloaded" -ForegroundColor Green
        }
        if ($ffplayExe) {
            Copy-Item $ffplayExe.FullName -Destination (Join-Path $appdataPath "ffplay.exe")
            Write-Host "✓ ffplay.exe downloaded" -ForegroundColor Green
        }
        if ($ffprobeExe) {
            Copy-Item $ffprobeExe.FullName -Destination (Join-Path $appdataPath "ffprobe.exe")
            Write-Host "✓ ffprobe.exe downloaded" -ForegroundColor Green
        }

        Remove-Item -Recurse -Force $extractPath
        Remove-Item $ffmpegZip
    } catch {
        Write-Error "Failed to download FFmpeg: $_"
    }
} else {
    Write-Warning "FFmpeg URL not provided. Use -FfmpegUrl parameter to download FFmpeg."
    Write-Host "Example: .\download-dependencies.ps1 -FfmpegUrl 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'" -ForegroundColor Cyan
}

# Создаем configuration.json из примера если его нет
$configPath = Join-Path $appdataPath "configuration.json"
$configExamplePath = Join-Path $appdataPath "configuration.json.example"
if (-not (Test-Path $configPath)) {
    if (Test-Path $configExamplePath) {
        Copy-Item $configExamplePath -Destination $configPath
        Write-Host "✓ Created configuration.json from example" -ForegroundColor Green
        Write-Warning "Please edit configuration.json with your settings!"
    } else {
        Write-Warning "configuration.json.example not found"
    }
}

Write-Host "`nDone!" -ForegroundColor Green
