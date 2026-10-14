param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\src\ESP32AIControl.WP81\Assets\Emoji")
)

$ErrorActionPreference = "Stop"
$tag = "v2026-09-24-unicode18_0"
$marker = Join-Path $Destination ".noto-emoji-version"
if ((Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $tag) -and
    (Test-Path (Join-Path $Destination "1f600.png"))) {
    Write-Host "Noto Emoji assets already prepared ($tag)."
    exit 0
}

$parent = Split-Path -Parent $Destination
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("marfa-noto-emoji-" + [Guid]::NewGuid().ToString("N"))
$zip = "$temp.zip"
try {
    New-Item -ItemType Directory -Force -Path $temp | Out-Null
    $url = "https://codeload.github.com/googlefonts/noto-emoji/zip/refs/tags/$tag"
    Write-Host "Downloading Noto Emoji $tag (first build only)..."
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive -LiteralPath $zip -DestinationPath $temp -Force
    $sourceRoot = Get-ChildItem -LiteralPath $temp -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName "2D\png\128") } |
        Select-Object -First 1
    if (-not $sourceRoot) { throw "The Noto Emoji archive did not contain 2D/png/128." }

    if (Test-Path $Destination) {
        Get-ChildItem -LiteralPath $Destination -Force | Remove-Item -Recurse -Force
    } else {
        New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    }
    $pngRoot = Join-Path $sourceRoot.FullName "2D\png\128"
    Copy-Item -Path (Join-Path $pngRoot "*.png") -Destination $Destination -Force
    $license = Join-Path $sourceRoot.FullName "fonts\LICENSE"
    if (Test-Path $license) { Copy-Item $license (Join-Path $Destination "LICENSE.txt") -Force }
    @"
Noto Emoji assets
Source: https://github.com/googlefonts/noto-emoji
Pinned release: $tag
Emoji artwork: SIL Open Font License 1.1 (see LICENSE.txt).
"@ | Set-Content -LiteralPath (Join-Path $Destination "ATTRIBUTION.txt") -Encoding UTF8
    Set-Content -LiteralPath $marker -Value $tag -Encoding ASCII
    $count = @(Get-ChildItem -LiteralPath $Destination -Filter "*.png" -File).Count
    if ($count -lt 1000) { throw "Only $count emoji PNG assets were found; expected a full set." }
    Write-Host "Prepared $count Noto Emoji PNG assets."
}
finally {
    if (Test-Path $zip) { Remove-Item $zip -Force -ErrorAction SilentlyContinue }
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
