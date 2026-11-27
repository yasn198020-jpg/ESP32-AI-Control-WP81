param(
    [string]$CertificatePath = "",
    [string]$CertificatePassword = ""
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\ESP32AIControl.UWP\ESP32AIControl.UWP.csproj'
$project = [System.IO.Path]::GetFullPath($project)
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe'

if (-not (Test-Path -LiteralPath $msbuild)) {
    throw "VS2017 MSBuild not found: $msbuild"
}
if (-not (Test-Path -LiteralPath $project)) {
    throw "UWP project not found: $project"
}

Write-Host '=== WP10 ARM Debug build ===' -ForegroundColor Cyan
& $msbuild $project /restore /t:Build /p:Configuration=Debug /p:Platform=ARM /p:PlatformTarget=ARM /p:VisualStudioVersion=15.0 /p:TargetPlatformVersion=10.0.14393.0 /p:RuntimeIdentifiers=win10-arm /p:RuntimeIdentifier=win10-arm /p:AppxPackageSigningEnabled=false /v:minimal
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE" }

$packageDir = Join-Path (Split-Path $project) 'AppPackages\ESP32AIControl.UWP_1.0.0.0_Debug_Test'
$bundle = Get-ChildItem $packageDir -Filter '*ARM_x86_Debug.appxbundle' -File -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $bundle) {
    throw "ARM/x86 Debug bundle not found in $packageDir"
}

$dist = Join-Path (Split-Path $project) 'BuildOutput'
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
Write-Host "Bundle: $destination" -ForegroundColor Green
