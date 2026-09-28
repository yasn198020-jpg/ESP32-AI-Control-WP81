$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

git config core.hooksPath .githooks

Write-Host ""
Write-Host "Git hook configured successfully." -ForegroundColor Green
Write-Host "After git pull/merge, WP8.1 Debug ARM will be built automatically."
Write-Host "If the build fails, the hook returns an error code."
Write-Host ""