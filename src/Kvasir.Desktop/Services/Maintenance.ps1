$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Assert-PlainPath([string] $path) {
    for ($current = [IO.Path]::GetFullPath($path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw ($plan.UnsafePath -f $current) }
    }
}

function Test-KvasirDesktop([string] $path) {
    if (![IO.File]::Exists($path)) { return $false }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    return $version.ProductName -eq 'Kvasir.Desktop' -and $version.OriginalFilename -eq 'Kvasir.Desktop.dll'
}

function Get-KvasirBundleDirectories {
    $currentName = [IO.Path]::GetFileNameWithoutExtension($plan.Executable)
    foreach ($base in $plan.BundleBases) {
        if (![IO.Directory]::Exists($base)) { continue }
        Assert-PlainPath $base
        foreach ($family in [IO.Directory]::EnumerateDirectories($base)) {
            $name = [IO.Path]::GetFileName($family)
            if ($name -ne $currentName -and $name -ne 'Kvasir.Desktop' -and
                $name -notmatch '^Kvasir-v[0-9]+\.[0-9]+\.[0-9]+-win-x64(?: \([0-9]+\))?$') { continue }
            Assert-PlainPath $family
            foreach ($directory in [IO.Directory]::EnumerateDirectories($family)) {
                if ([IO.Path]::GetFileName($directory) -notmatch '^[0-9A-Za-z_-]{12}$') { continue }
                Assert-PlainPath $directory
                $assembly = [IO.Path]::Combine($directory, 'Kvasir.Desktop.dll')
                Assert-PlainPath $assembly
                if ((Test-KvasirDesktop $assembly) -and [IO.File]::Exists([IO.Path]::Combine($directory, 'bot', 'Kvasir.exe'))) {
                    $directory
                }
            }
        }
    }
}

function Remove-OwnedDirectory([string] $path) {
    if (![IO.Directory]::Exists($path)) { return }
    Assert-PlainPath $path

    # Visit ordinary directories and remove junctions without walking their targets.
    foreach ($entry in [IO.Directory]::GetFileSystemEntries($path)) {
        if ($plan.Executable -eq $entry) { continue }
        $attributes = [IO.File]::GetAttributes($entry)
        if ($attributes -band [IO.FileAttributes]::Directory) {
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) { [IO.Directory]::Delete($entry) }
            else { Remove-OwnedDirectory $entry }
        } else {
            [IO.File]::Delete($entry)
        }
    }
    if ($plan.Executable.StartsWith($path.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        $remainingDirectories.Add($path)
    } else {
        [IO.Directory]::Delete($path)
    }
}

function Install-Update {
    $target = $plan.Executable
    $source = $plan.Update
    $directory = [IO.Path]::GetDirectoryName($target)
    $suffix = [Guid]::NewGuid().ToString('N')
    $staged = [IO.Path]::Combine($directory, ".Kvasir-update-$suffix.exe")
    $backup = [IO.Path]::Combine($directory, ".Kvasir-backup-$suffix.exe")
    Assert-PlainPath $source
    try {
        [IO.File]::Copy($source, $staged)
        if (!(Test-KvasirDesktop $staged) -or (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $plan.Sha256) {
            throw $plan.BadUpdate
        }
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($staged)
        if ("$($version.FileMajorPart).$($version.FileMinorPart).$($version.FileBuildPart)" -ne $plan.Version) {
            throw $plan.BadUpdate
        }

        [IO.File]::Replace($staged, $target, $backup)
        try {
            $launch = [Diagnostics.ProcessStartInfo]::new($target)
            $launch.UseShellExecute = $false
            $launch.WorkingDirectory = $directory
            $launch.Arguments = '--check-package'
            $check = [Diagnostics.Process]::Start($launch)
            if (!$check) { throw $plan.RestartFailed }
            try {
                if (!$check.WaitForExit(60000)) {
                    $check.Kill()
                    $check.WaitForExit()
                    throw $plan.RestartFailed
                }
                if ($check.ExitCode -ne 0) { throw $plan.RestartFailed }
            } finally { $check.Dispose() }
            $launch.Arguments = ''
            $signal = "Local\Kvasir.Update.$suffix"
            $ready = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $signal)
            $process = $null
            $opened = $false
            try {
                $launch.EnvironmentVariables['KVASIR_UPDATE_READY_EVENT'] = $signal
                $process = [Diagnostics.Process]::Start($launch)
                if (!$process) { throw $plan.RestartFailed }
                $wait = [Diagnostics.Stopwatch]::StartNew()
                while (!($opened = $ready.WaitOne(250))) {
                    if ($process.HasExited -or $wait.ElapsedMilliseconds -ge 45000) { throw $plan.RestartFailed }
                }
            } finally {
                $ready.Dispose()
                if ($process) {
                    try {
                        if (!$opened -and !$process.HasExited) { $process.Kill(); [void] $process.WaitForExit(10000) }
                    } catch [InvalidOperationException] {
                        if (!$process.HasExited) { throw }
                    } finally { $process.Dispose() }
                }
            }
        } catch {
            [IO.File]::Replace($backup, $target, $staged)
            throw $plan.RestartFailed
        }
        try { [IO.File]::Delete($backup) } catch [IO.IOException] { } catch [UnauthorizedAccessException] { }
    } finally {
        [IO.File]::Delete($staged)
    }
}

$mutex = $null
$acquired = $false
try {
    $plan = ConvertFrom-Json -InputObject $env:KVASIR_MAINTENANCE_PLAN
    [Environment]::SetEnvironmentVariable('KVASIR_MAINTENANCE_PLAN', $null)
    $mutex = [Threading.Mutex]::new($false, 'Local\Kvasir.Maintenance')
    try { $acquired = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (!$acquired) { throw $plan.AlreadyRunning }

    $owner = Get-Process -Id $plan.OwnerId -ErrorAction SilentlyContinue
    if ($owner) {
        try {
            if ($owner.StartTime.ToUniversalTime().Ticks -eq [long] $plan.OwnerStarted -and !$owner.WaitForExit(30000)) {
                throw $plan.CloseTimeout
            }
        } catch [InvalidOperationException] {
            if (!$owner.HasExited) { throw }
        } finally { $owner.Dispose() }
    }

    Assert-PlainPath $plan.Executable
    $exe = [IO.FileInfo]::new($plan.Executable)
    if (!(Test-KvasirDesktop $plan.Executable) -or $exe.Length -ne [long] $plan.Length -or
        $exe.LastWriteTimeUtc.Ticks -ne [long] $plan.LastWrite -or $exe.IsReadOnly) {
        throw ($plan.ChangedExecutable -f $plan.Executable)
    }

    if ($plan.Update) {
        Install-Update
    } else {
        foreach ($instance in [Diagnostics.Process]::GetProcesses()) {
            try {
                $module = $instance.MainModule
                if ($module -and $module.FileVersionInfo.OriginalFilename -in @('Kvasir.Desktop.dll', 'Kvasir.dll')) {
                    throw $plan.OtherInstance
                }
            } catch [ComponentModel.Win32Exception] { } catch [InvalidOperationException] { }
            finally { $instance.Dispose() }
        }

        Assert-PlainPath $plan.DataDirectory
        $bundles = @(Get-KvasirBundleDirectories)
        $neighbors = @([IO.Directory]::EnumerateFiles($exe.DirectoryName, '.Kvasir-*.exe') | Where-Object {
            $_ -ne $plan.Executable -and [IO.Path]::GetFileName($_) -match '^\.Kvasir-(backup|update)-[a-f0-9]{32}\.exe$'
        })
        foreach ($path in $neighbors) { Assert-PlainPath $path }
        $remainingDirectories = [Collections.Generic.List[string]]::new()
        foreach ($path in $bundles) { Remove-OwnedDirectory $path }
        Remove-OwnedDirectory $plan.DataDirectory
        Assert-PlainPath $plan.LocationFile
        [IO.File]::Delete($plan.LocationFile)
        Assert-PlainPath $plan.SessionFile
        [IO.File]::Delete($plan.SessionFile)
        foreach ($path in $neighbors) {
            if (Test-KvasirDesktop $path) { [IO.File]::Delete($path) }
        }
        Assert-PlainPath $plan.Executable
        [IO.File]::Delete($plan.Executable)
        foreach ($path in $remainingDirectories) {
            if ([IO.Directory]::Exists($path) -and [IO.Directory]::GetFileSystemEntries($path).Length -eq 0) {
                [IO.Directory]::Delete($path)
            }
        }
        foreach ($family in ($bundles | ForEach-Object { [IO.Path]::GetDirectoryName($_) } | Select-Object -Unique)) {
            if ([IO.Directory]::Exists($family) -and [IO.Directory]::GetFileSystemEntries($family).Length -eq 0) {
                Assert-PlainPath $family
                [IO.Directory]::Delete($family)
            }
        }
    }
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    if ($plan.ErrorTitle) {
        Add-Type -AssemblyName System.Windows.Forms
        [void] [Windows.Forms.MessageBox]::Show($_.Exception.Message, $plan.ErrorTitle, 'OK', 'Error')
    }
    exit 1
} finally {
    if ($plan.Update) {
        try {
            Assert-PlainPath $plan.Update
            [IO.File]::Delete($plan.Update)
        } catch { }
    }
    if ($acquired) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
