param(
    [string]$CertificatePath = "",
    [string]$CertificatePassword = ""
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\ESP32AIControl.UWP\ESP32AIControl.UWP.csproj'
$project = [System.IO.Path]::GetFullPath($project)
$projectRoot = Split-Path $project
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe'

if (-not (Test-Path -LiteralPath $msbuild)) {
    throw "VS2017 MSBuild not found: $msbuild"
}
if (-not (Test-Path -LiteralPath $project)) {
    throw "UWP project not found: $project"
}

# Calculate the next package revision. Package.appxmanifest is temporarily
# updated for the build and restored byte-for-byte in the finally block.
# Previous package versions are read from AppPackages and BuildOutput.
$manifestPath = Join-Path $projectRoot 'Package.appxmanifest'
[xml]$manifestXml = Get-Content -LiteralPath $manifestPath
$manifestVersion = [version]$manifestXml.Package.Identity.Version
$knownVersions = @($manifestVersion)

$packagesRoot = Join-Path $projectRoot 'AppPackages'
if (Test-Path -LiteralPath $packagesRoot) {
    Get-ChildItem -LiteralPath $packagesRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Name -match '^ESP32AIControl\.UWP_(\d+\.\d+\.\d+\.\d+)_Debug_Test$') {
            $script:knownVersions += [version]$Matches[1]
        }
    }
}

$dist = Join-Path $projectRoot 'BuildOutput'
if (Test-Path -LiteralPath $dist) {
    Get-ChildItem -LiteralPath $dist -File -Filter 'ESP32AIControl.UWP_*_ARM_x86_Debug.appxbundle' -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Name -match '^ESP32AIControl\.UWP_(\d+\.\d+\.\d+\.\d+)_ARM_x86_Debug\.appxbundle$') {
            $script:knownVersions += [version]$Matches[1]
        }
    }
}

$latestVersion = $knownVersions | Sort-Object -Descending | Select-Object -First 1
if ($latestVersion.Revision -ge 65535) {
    throw "Package revision limit reached for $latestVersion. Update the manifest's major/minor/build version."
}
$nextVersion = '{0}.{1}.{2}.{3}' -f $latestVersion.Major, $latestVersion.Minor, $latestVersion.Build, ($latestVersion.Revision + 1)
Write-Host "Package version: $nextVersion" -ForegroundColor Cyan

Write-Host '=== WP10 ARM Debug build ===' -ForegroundColor Cyan
$originalManifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
$manifestText = [System.IO.File]::ReadAllText($manifestPath)
$identityVersionPattern = '(<Identity\b[^>]*\bVersion=")[^"]+(")'
if (-not [regex]::IsMatch($manifestText, $identityVersionPattern)) {
    throw "Could not locate Identity Version in $manifestPath"
}
$buildManifestText = [regex]::Replace($manifestText, $identityVersionPattern, ('1' + $nextVersion + '2'), 1)
try {
    [System.IO.File]::WriteAllText($manifestPath, $buildManifestText, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Temporarily setting manifest package version to $nextVersion" -ForegroundColor Cyan
    & $msbuild $project /restore /t:Build /p:Configuration=Debug /p:Platform=ARM /p:PlatformTarget=ARM /p:VisualStudioVersion=15.0 /p:TargetPlatformVersion=10.0.14393.0 /p:RuntimeIdentifiers=win10-arm /p:RuntimeIdentifier=win10-arm /p:AppxPackageSigningEnabled=false /p:AppxAutoIncrementPackageRevision=false /v:minimal
    if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE" }
}
finally {
    [System.IO.File]::WriteAllBytes($manifestPath, $originalManifestBytes)
    Write-Host 'Original Package.appxmanifest restored.' -ForegroundColor DarkGray
}

$packageDir = Get-ChildItem -LiteralPath $packagesRoot -Directory -Filter 'ESP32AIControl.UWP_*_Debug_Test' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $packageDir) {
    throw "Debug package directory not found under $packagesRoot"
}

$bundle = Get-ChildItem -LiteralPath $packageDir.FullName -Filter '*ARM_x86_Debug.appxbundle' -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $bundle) {
    throw "ARM/x86 Debug bundle not found in $($packageDir.FullName)"
}

New-Item -ItemType Directory -Path $dist -Force | Out-Null
$destination = Join-Path $dist $bundle.Name
Copy-Item -LiteralPath $bundle.FullName -Destination $destination -Force

if ($CertificatePath) {
    if (-not (Test-Path -LiteralPath $CertificatePath)) { throw "Certificate file not found: $CertificatePath" }
    $sdkRoots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\8.1\bin')
    )
    $signTool = $null
    foreach ($root in $sdkRoots) {
        if (Test-Path $root) {
            $candidate = Get-ChildItem $root -Filter signtool.exe -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\(x86|arm64)\\signtool.exe$' -or $_.FullName -match '\\signtool.exe$' } |
                Select-Object -First 1
            if ($candidate) { $signTool = $candidate.FullName; break }
        }
    }
    if (-not $signTool) { throw 'SignTool.exe not found in Windows SDK folders.' }
    $signArgs = @('sign','/fd','SHA256','/f',$CertificatePath)
    if ($CertificatePassword) { $signArgs += @('/p',$CertificatePassword) }
    $signArgs += $destination
    & $signTool @signArgs
    if ($LASTEXITCODE -ne 0) { throw "SignTool failed with exit code $LASTEXITCODE" }
    Write-Host 'Bundle signed.' -ForegroundColor Green
} else {
    Write-Host 'Bundle copied unsigned. Supply -CertificatePath to sign it.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Build succeeded.' -ForegroundColor Green
Write-Host "Package directory: $($packageDir.FullName)" -ForegroundColor Green
Write-Host "Bundle: $destination" -ForegroundColor Green
