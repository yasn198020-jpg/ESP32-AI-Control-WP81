$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$hooks = Join-Path $root '.githooks'
if (-not (Test-Path $hooks)) { New-Item -ItemType Directory -Path $hooks | Out-Null }
git -C $root config core.hooksPath .githooks
Write-Host "Git hook enabled: .githooks/post-merge"
Write-Host "Now git pull/merge will run the WP8.1 Debug ARM build automatically."