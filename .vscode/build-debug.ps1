$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot '..\ESP32-AI-Control-WP81.sln'

function Find-MSBuild {
    $cmd = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        "$env:ProgramFiles(x86)\MSBuild\14.0\Bin\MSBuild.exe",
        "$env:ProgramFiles(x86)\Microsoft Visual Studio\2017\BuildTools\MSBuild\15.0\Bin\MSBuild.exe",
        "$env:ProgramFiles(x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe",
        "$env:ProgramFiles(x86)\Microsoft Visual Studio\2017\Professional\MSBuild\15.0\Bin\MSBuild.exe",
        "$env:ProgramFiles(x86)\Microsoft Visual Studio\2017\Enterprise\MSBuild\15.0\Bin\MSBuild.exe"
    )

    foreach ($path in $candidates) {
        if ($path -and (Test-Path $path)) { return $path }
    }

    $vswhere = "$env:ProgramFiles(x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $installPaths = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        foreach ($installPath in $installPaths) {
            $path = Join-Path $installPath 'MSBuild\Current\Bin\MSBuild.exe'
            if (Test-Path $path) { return $path }

            $path = Join-Path $installPath 'MSBuild\15.0\Bin\MSBuild.exe'
            if (Test-Path $path) { return $path }
        }
    }

    return $null
}

$msbuild = Find-MSBuild
if (-not $msbuild) {
    throw 'MSBuild.exe not found. Install Visual Studio 2015/2017 with WP8.1 tools.'
}

Write-Host "Using MSBuild: $msbuild"
& $msbuild $solution /m /t:Build /p:Configuration=Debug /p:Platform=ARM /verbosity:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host 'WP8.1 Debug ARM build completed successfully.'
