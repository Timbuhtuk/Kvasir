param(
    [Parameter(Mandatory)]
    [string]$Executable,
    [switch]$CheckWindow
)

$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
$checkRoot = Join-Path $PSScriptRoot "../artifacts/check-standalone-$([guid]::NewGuid().ToString('N'))"
$checkRoot = (New-Item -Path $checkRoot -ItemType Directory).FullName
$isolatedExe = Join-Path $checkRoot (Split-Path -Leaf $exe)
Copy-Item -LiteralPath $exe -Destination $isolatedExe

$previousCache = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
$process = $null
try {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $checkRoot 'extract'
    $launch = @{ FilePath = $isolatedExe; WorkingDirectory = $checkRoot; WindowStyle = 'Hidden'; PassThru = $true }
    if (!$CheckWindow) { $launch.ArgumentList = '--check-package' }
    $process = Start-Process @launch
    if ($CheckWindow) {
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        do {
            Start-Sleep -Milliseconds 250
            $process.Refresh()
            if ($process.HasExited) { throw "Standalone Kvasir exited during startup with code $($process.ExitCode)." }
        } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
        if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Standalone Kvasir did not open its window.' }
        Write-Host 'Standalone Kvasir opened its desktop window.'
    } else {
        if (!$process.WaitForExit(60000)) { throw 'Standalone Kvasir timed out during its package check.' }
        if ($process.ExitCode -ne 0) { throw "Standalone Kvasir failed its package check with code $($process.ExitCode)." }
    }

    $desktop = Get-ChildItem -LiteralPath $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -Recurse -File -Filter Kvasir.Desktop.dll |
        Select-Object -First 1
    if (!$desktop) { throw 'Standalone Kvasir did not extract its payload.' }
    $payload = $desktop.DirectoryName
    foreach ($name in @('Defaults/configuration.json', 'bot/Kvasir.exe', 'bot/coreclr.dll', 'bot/Defaults/configuration.json', 'bot/libdave.dll', 'bot/opus.dll', 'bot/appdata/ffmpeg.exe', 'bot/appdata/yt-dlp.exe')) {
        if (!(Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) {
            throw "Standalone Kvasir is missing $name."
        }
    }

    $ffmpegVersion = & (Join-Path $payload 'bot/appdata/ffmpeg.exe') -version
    if ($LASTEXITCODE -ne 0) { throw "The bundled FFmpeg version check exited with code $LASTEXITCODE." }
    $ffmpegVersion | Select-Object -First 1
    & (Join-Path $payload 'bot/appdata/yt-dlp.exe') --version
    if ($LASTEXITCODE -ne 0) { throw 'Bundled yt-dlp failed to start.' }
    Write-Host 'Standalone Kvasir started from a directory containing only its EXE.'
} finally {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousCache
    if ($process) {
        if (!$process.HasExited) {
            $null = $process.CloseMainWindow()
            if (!$process.WaitForExit(3000)) { $process.Kill(); $process.WaitForExit() }
        }
        $process.Dispose()
    }
}
