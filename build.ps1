# Builds release packages for GitHub:
#   dist\console.zip  — CLI  (ailimit.exe,      self-contained single file, trimmed)
#   dist\desktop.zip  — Tray (ailimit-tray.exe, self-contained single file; WinForms does not support trimming)
# Users just unzip and run — no .NET Runtime install required.
#
# Usage:  .\build.ps1            (runs tests first)
#         .\build.ps1 -SkipTests

param(
    [switch]$SkipTests,
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

if (-not $SkipTests) {
    Write-Host "==> Running tests" -ForegroundColor Cyan
    dotnet test $root --nologo -c Release
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
}

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

Write-Host "==> Publishing console (trimmed, self-contained, single file)" -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src/AiLimitMonitor.Cli') -c Release -r $Runtime --self-contained `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=true `
    -p:DebugType=none `
    -o (Join-Path $dist 'console')
if ($LASTEXITCODE -ne 0) { throw "console publish failed" }

Write-Host "==> Publishing desktop (self-contained, single file)" -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src/AiLimitMonitor.Tray') -c Release -r $Runtime --self-contained `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o (Join-Path $dist 'desktop')
if ($LASTEXITCODE -ne 0) { throw "desktop publish failed" }

Write-Host "==> Zipping" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $dist 'console/*') -DestinationPath (Join-Path $dist 'console.zip') -Force
Compress-Archive -Path (Join-Path $dist 'desktop/*') -DestinationPath (Join-Path $dist 'desktop.zip') -Force

Write-Host "==> Done" -ForegroundColor Green
Get-ChildItem $dist -Filter '*.zip' | ForEach-Object {
    "{0,-14} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)
}
