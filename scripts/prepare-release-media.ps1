param(
    [string]$FfmpegUrl,
    [string]$YtDlpUrl
)

$ErrorActionPreference = 'Stop'
$appData = (New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot '../src/Kvasir/appdata') -Force).FullName
$downloads = (New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot '../artifacts/release-media') -Force).FullName

function Get-ReleaseFile([string]$Url, [string]$Path, [string]$Sha256) {
    if (!$Sha256 -or !(Test-Path -LiteralPath $Path) -or (Get-FileHash -LiteralPath $Path).Hash -ne $Sha256) {
        & curl.exe --fail --silent --location --retry 2 --max-time 180 --output $Path $Url
        if ($LASTEXITCODE -ne 0) { throw "Download failed for $(Split-Path -Leaf $Path) (curl exit $LASTEXITCODE)." }
    }
    if ($Sha256 -and (Get-FileHash -LiteralPath $Path).Hash -ne $Sha256) {
        throw "SHA-256 mismatch for $(Split-Path -Leaf $Path)."
    }
}

$ffmpegHash = ''
if (!$FfmpegUrl) {
    $FfmpegUrl = 'https://github.com/GyanD/codexffmpeg/releases/download/9.1/ffmpeg-9.1-essentials_build.zip'
    $ffmpegHash = 'B9BBBB3ACECE000CBEEC994F856649354E52C773807841E78908374B24D658FD'
}
$ffmpegArchivePath = Join-Path $downloads 'ffmpeg.zip'
Get-ReleaseFile $FfmpegUrl $ffmpegArchivePath $ffmpegHash
$ffmpegArchive = [IO.Compression.ZipFile]::OpenRead($ffmpegArchivePath)
try {
    foreach ($name in @('ffmpeg.exe', 'ffprobe.exe', 'ffplay.exe')) {
        $entry = $ffmpegArchive.Entries | Where-Object Name -EQ $name | Select-Object -First 1
        if (!$entry -and $name -eq 'ffmpeg.exe') { throw 'The FFmpeg archive must contain ffmpeg.exe.' }
        if ($entry) {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $appData $name), $true)
        }
    }
    foreach ($name in @('LICENSE', 'README.txt')) {
        $entry = $ffmpegArchive.Entries | Where-Object Name -EQ $name | Select-Object -First 1
        if ($entry) {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $downloads "FFmpeg-$name"), $true)
        }
    }
} finally {
    $ffmpegArchive.Dispose()
}

$ytDlpHash = ''
if (!$YtDlpUrl) {
    $YtDlpUrl = 'https://github.com/yt-dlp/yt-dlp/releases/download/2026.11.23/yt-dlp.exe'
    $ytDlpHash = '622ED48023CAC1DAA52A3BF3A6AC38F03514DE214B5F0FA069389FD0F6D17E5C'
    $ytDlpArchivePath = Join-Path $downloads 'yt-dlp.zip'
    Get-ReleaseFile 'https://github.com/yt-dlp/yt-dlp/releases/download/2026.11.23/yt-dlp_win.zip' $ytDlpArchivePath '824860984B96CA01DE09F98F7BFD74ED7BBDCE72A3340B51AF8B9DC5EE037A5F'
    $ytDlpArchive = [IO.Compression.ZipFile]::OpenRead($ytDlpArchivePath)
    try {
        $entry = $ytDlpArchive.GetEntry('_internal/THIRD_PARTY_LICENSES.txt')
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $downloads 'yt-dlp-THIRD_PARTY_LICENSES.txt'), $true)
    } finally {
        $ytDlpArchive.Dispose()
    }
}
Get-ReleaseFile $YtDlpUrl (Join-Path $appData 'yt-dlp.exe') $ytDlpHash

& (Join-Path $appData 'ffmpeg.exe') -version | Select-Object -First 1
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg.exe failed to start.' }
& (Join-Path $appData 'yt-dlp.exe') --version
if ($LASTEXITCODE -ne 0) { throw 'yt-dlp.exe failed to start.' }
