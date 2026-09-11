$applicationPath = Join-Path $PSScriptRoot "..\Application"
$forbiddenPattern = "NetCord|DSharpPlus|Discord\.Net|OpusEncodeStream|VoiceClient|GatewayClient|RestClient|SpotifyAPI|YoutubeDLSharp|YoutubeExplode"

$violations = Get-ChildItem $applicationPath -Recurse -File |
    Where-Object { $_.Extension -in ".cs", ".csproj" } |
    Select-String -Pattern $forbiddenPattern

if ($violations)
{
    Write-Host "External connector library leaked into Application:" -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
    exit 1
}

Write-Host "Architecture boundary is valid: Application has no connector library references." -ForegroundColor Green
