param(
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$destinationPath = (New-Item -Path $Destination -ItemType Directory -Force).FullName
$ytDlp = Join-Path $destinationPath 'yt-dlp.exe'
$ffmpeg = Join-Path $destinationPath 'ffmpeg.exe'

if (!(Test-Path -LiteralPath $ytDlp -PathType Leaf)) {
    Invoke-WebRequest -Uri 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -OutFile $ytDlp -TimeoutSec 90
}

if (!(Test-Path -LiteralPath $ffmpeg -PathType Leaf)) {
    $ffmpegRelease = & curl.exe --fail --silent --show-error --location --max-time 20 'https://www.gyan.dev/ffmpeg/builds/release-version'
    if ($LASTEXITCODE -ne 0 -or $ffmpegRelease -notmatch '^\d+(\.\d+)+$') {
        throw 'Could not resolve the stable FFmpeg release version.'
    }
    $ffmpegUrl = "https://github.com/GyanD/codexffmpeg/releases/download/$ffmpegRelease/ffmpeg-$ffmpegRelease-essentials_build.zip"
    $zip = New-TemporaryFile
    try {
        & curl.exe --fail --silent --show-error --location --max-time 120 --output $zip.FullName $ffmpegUrl
        if ($LASTEXITCODE -ne 0) { throw 'Could not download the FFmpeg archive.' }
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zip.FullName)
        try {
            $entry = $archive.Entries | Where-Object { $_.FullName -like '*/bin/ffmpeg.exe' } | Select-Object -First 1
            if (!$entry) { throw 'The FFmpeg archive does not contain bin/ffmpeg.exe.' }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $ffmpeg, $true)
        } finally {
            $archive.Dispose()
        }
    } finally {
        Remove-Item -LiteralPath $zip.FullName
    }
}

& $ytDlp --version
if ($LASTEXITCODE -ne 0) { throw 'yt-dlp --version failed.' }
$ffmpegVersion = & $ffmpeg -version
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg -version failed.' }
$ffmpegVersion | Select-Object -First 1
