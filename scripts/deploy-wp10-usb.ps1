param(
    [switch]$SkipBuild,
    [switch]$SkipDeploy,
    [switch]$Publish,
    [string]$DeviceIp = '127.0.0.1'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$projectRoot = Join-Path $root 'src\ESP32AIControl.UWP'
$buildScript = Join-Path $PSScriptRoot 'build-wp10-debug.ps1'
$bundleDir = Join-Path $projectRoot 'BuildOutput'

$certificateThumbprint = '9A3E64A7F1078FBBCD17BC732AA59397BDFF49B6'
$signTool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x64\signtool.exe'
$deployTool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x86\WinAppDeployCmd.exe'
$coreRuntime = 'C:\Program Files (x86)\Microsoft SDKs\Windows Kits\10\ExtensionSDKs\Microsoft.NET.CoreRuntime\1.1\AppX\arm\Microsoft.NET.CoreRuntime.1.1.appx'
$vcLibs = 'C:\Program Files (x86)\Microsoft SDKs\Windows Kits\10\ExtensionSDKs\Microsoft.VCLibs\14.0\Appx\Debug\ARM\Microsoft.VCLibs.arm.Debug.14.00.appx'

Write-Host "`n=== Windows 10 Mobile: ARM ===" -ForegroundColor Cyan

if (-not $SkipBuild) {
    if (-not (Test-Path -LiteralPath $buildScript)) { throw "Build script not found: $buildScript" }
    Write-Host "`n[1/3] Building application..." -ForegroundColor Cyan
    & $buildScript
    if ($LASTEXITCODE -ne 0) { throw "Build failed: exit code $LASTEXITCODE" }
}

if (-not (Test-Path -LiteralPath $bundleDir)) { throw "Bundle output directory not found: $bundleDir" }
$bundleFile = Get-ChildItem -LiteralPath $bundleDir -Filter 'ESP32AIControl.UWP_*_ARM_x86_Debug.appxbundle' -File |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $bundleFile) { throw "ARM/x86 Debug bundle not found in $bundleDir" }
$bundle = $bundleFile.FullName
Write-Host "Selected bundle: $bundle" -ForegroundColor Cyan

Write-Host "`n[2/3] Signing bundle..." -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $signTool)) { throw "SignTool not found: $signTool" }
$cert = Get-Item "Cert:\CurrentUser\My\$certificateThumbprint" -ErrorAction SilentlyContinue
if (-not $cert) { throw "Certificate not found in CurrentUser\My: $certificateThumbprint" }
if (-not $cert.HasPrivateKey) { throw 'Certificate has no private key.' }
& $signTool sign /fd SHA256 /sha1 $certificateThumbprint /s My $bundle
if ($LASTEXITCODE -ne 0) { throw "Signing failed: exit code $LASTEXITCODE" }
Write-Host 'Bundle signed successfully.' -ForegroundColor Green

if ($Publish) {
    Write-Host "`n=== Publishing OTA artifact ===" -ForegroundColor Cyan
    $git = Get-Command git.exe -ErrorAction SilentlyContinue
    if (-not $git) { throw 'Git executable was not found. Install Git for Windows and ensure git.exe is on PATH.' }
    if ($bundleFile.Name -notmatch '^ESP32AIControl\.UWP_(\d+\.\d+\.\d+\.\d+)_ARM_x86_Debug\.appxbundle$') {
        throw "Cannot extract package version from bundle filename: $($bundleFile.Name)"
    }
    $packageVersion = $Matches[1]
    $sha256 = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant()
    $repoUrl = 'https://github.com/yasn198020-jpg/ESP32-AI-Control-Updates.git'
    $branchName = 'windows-10-mobile'
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ESP32AIControl-OTA-' + [guid]::NewGuid().ToString('N'))
    try {
        & git.exe clone --depth 1 --branch $branchName $repoUrl $tempRoot
        if ($LASTEXITCODE -ne 0) { throw 'Failed to clone OTA repository. Check Git credentials and access.' }
        $targetBundle = Join-Path $tempRoot 'app.appxbundle'
        Copy-Item -LiteralPath $bundle -Destination $targetBundle -Force
        $versionPath = Join-Path $tempRoot 'version.json'
        if (-not (Test-Path $versionPath)) { throw 'OTA version.json not found in cloned repository.' }
        $manifest = Get-Content -LiteralPath $versionPath -Raw | ConvertFrom-Json
        $manifest.version = $packageVersion
        $manifest.package = 'app.appxbundle'
        $manifest.sha256 = $sha256
        # Keep OTA enabled on every publication.
        $manifest.enabled = $true
        $manifest.releaseNotes = "Debug build $packageVersion published; OTA updates are enabled."
        $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $versionPath -Encoding UTF8
        & git.exe -C $tempRoot add app.appxbundle version.json
        if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
        & git.exe -C $tempRoot commit -m "Publish Windows 10 Mobile package $packageVersion"
        if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
        & git.exe -C $tempRoot push origin $branchName
        if ($LASTEXITCODE -ne 0) { throw 'git push failed. Package was committed locally but not published remotely.' }
        Write-Host "Published $packageVersion to OTA repository." -ForegroundColor Green
        Write-Host "SHA-256: $sha256" -ForegroundColor Green
        Write-Host 'OTA enabled: true.' -ForegroundColor Green
    }
    finally {
        if (Test-Path $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if ($SkipDeploy) {
    $msg = if ($Publish) { 'Build, signing and publication completed. Deployment skipped.' } else { 'Build and signing completed. Deployment skipped.' }
    Write-Host "`n$msg" -ForegroundColor Green
    Write-Host "Bundle: $bundle"
    exit 0
}

Write-Host "`n[3/3] Deploying to Lumia 830 via USB..." -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $deployTool)) { throw "WinAppDeployCmd not found: $deployTool" }
if (-not (Test-Path -LiteralPath $coreRuntime)) { throw "Dependency not found: $coreRuntime" }
if (-not (Test-Path -LiteralPath $vcLibs)) { throw "Dependency not found: $vcLibs" }
& $deployTool install -file $bundle -ip $DeviceIp -dependency $coreRuntime $vcLibs
if ($LASTEXITCODE -ne 0) { throw "Deployment failed: exit code $LASTEXITCODE" }
Write-Host "`nSUCCESS: Application installed on Lumia 830." -ForegroundColor Green
