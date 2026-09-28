param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler not found.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warn:4', '/warnaserror',
    "/out:$OutputDirectory\CodexUsageBar.exe", "/win32manifest:$PSScriptRoot\app.manifest",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    '/r:System.Web.Extensions.dll', '/r:System.Security.dll')
$resources = Join-Path $PSScriptRoot '..\Resources'
if (Test-Path -LiteralPath (Join-Path $resources 'icon-codex-light.png')) {
    $arguments += "/resource:$resources\icon-codex-light.png,codex-light.png"
    $arguments += "/resource:$resources\icon-codex-dark-color.png,codex-dark.png"
}
& $compiler @arguments @sources
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination (Join-Path $OutputDirectory 'CodexUsageBar.exe.config') -Force
Write-Output (Join-Path $OutputDirectory 'CodexUsageBar.exe')
