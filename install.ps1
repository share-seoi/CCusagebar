[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\CodexUsageBar'),
    [switch]$AutoStart,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'This installer requires 64-bit Windows 10 or Windows 11.'
}
$framework = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
if ($null -eq $framework -or $framework.Release -lt 528040) {
    throw '.NET Framework 4.8 or newer is required. Install it through Windows Update, then retry.'
}

$target = [IO.Path]::GetFullPath($InstallDirectory)
if ($target.TrimEnd('\') -eq [IO.Path]::GetPathRoot($target).TrimEnd('\')) {
    throw 'Choose an application folder, not a drive root.'
}
$executable = Join-Path $target 'CodexUsageBar.exe'
$buildDirectory = Join-Path ([IO.Path]::GetTempPath()) ('CodexUsageBar-build-' + [Guid]::NewGuid().ToString('N'))
$outputFiles = @('CodexUsageBar.exe', 'CodexUsageBar.exe.config')

try {
    # Build before touching any existing installation. No SDK, package restore, or credentials needed.
    & (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $buildDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Build failed; the existing installation was not changed.' }
    foreach ($name in $outputFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $buildDirectory $name))) { throw "Build output missing: $name" }
    }

    $processes = @(Get-Process -Name CodexUsageBar -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        # Never stop Codex, Claude, or a widget installed at a different path.
        if ($process.Path -and [string]::Equals($process.Path, $executable, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $process.Id -ErrorAction Stop
            $process.WaitForExit(5000) | Out-Null
        }
    }

    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($name in $outputFiles) {
        Copy-Item -LiteralPath (Join-Path $buildDirectory $name) -Destination (Join-Path $target $name) -Force
    }

    if ($AutoStart) {
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        New-Item -Path $runKey -Force | Out-Null
        New-ItemProperty -LiteralPath $runKey -Name 'CodexUsageBar' -Value ('"' + $executable + '"') -PropertyType String -Force | Out-Null
    }

    Write-Output "Installed: $executable"
    if (-not $NoStart) {
        $other = @(Get-Process -Name CodexUsageBar -ErrorAction SilentlyContinue | Where-Object {
            -not $_.Path -or -not [string]::Equals($_.Path, $executable, [StringComparison]::OrdinalIgnoreCase)
        })
        if ($other.Count -gt 0) {
            Write-Warning 'A widget from another folder is running. Quit it from its popup, then run the installed executable.'
        } else {
            $started = Start-Process -FilePath $executable -WorkingDirectory $target -WindowStyle Hidden -PassThru
            Start-Sleep -Milliseconds 700
            $started.Refresh()
            if ($started.HasExited) { throw "Installed, but startup failed (exit $($started.ExitCode))." }
            Write-Output "Started process: $($started.Id)"
        }
    }
    Write-Output 'Open Codex or Claude Desktop to show the taskbar widget. Click it to see both accounts.'
    Write-Output 'Existing login files and usage state were preserved. No credentials were installed.'
} finally {
    # Delete only the two build outputs we own; never recursively remove a computed directory.
    foreach ($name in $outputFiles) {
        $file = Join-Path $buildDirectory $name
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
    if (Test-Path -LiteralPath $buildDirectory) { Remove-Item -LiteralPath $buildDirectory }
}
