$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot '..\ESP32-AI-Control-WP81.sln'

function Find-MSBuild {
    # WP8.1 is a legacy project. Prefer the known working VS2015/MSBuild 14.0.
    $known = @(
        'C:\Program Files (x86)\MSBuild\14.0\Bin\MSBuild.exe',
        'C:\Program Files (x86)\MSBuild\12.0\Bin\MSBuild.exe'
    )

    foreach ($path in $known) {
        if (Test-Path -LiteralPath $path) { return $path }
    }

    # Then try MSBuild available on PATH.
    $cmd = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    # Then check common Visual Studio 2017 installations.
    $programFilesX86 = ${env:ProgramFiles(x86)}
    if ($programFilesX86) {
        $candidates = @(
            (Join-Path $programFilesX86 'Microsoft Visual Studio\2017\BuildTools\MSBuild\15.0\Bin\MSBuild.exe'),
            (Join-Path $programFilesX86 'Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe'),
            (Join-Path $programFilesX86 'Microsoft Visual Studio\2017\Professional\MSBuild\15.0\Bin\MSBuild.exe'),
            (Join-Path $programFilesX86 'Microsoft Visual Studio\2017\Enterprise\MSBuild\15.0\Bin\MSBuild.exe')
        )

        foreach ($path in $candidates) {
            if (Test-Path -LiteralPath $path) { return $path }
        }

        # Finally use vswhere for newer Visual Studio installations.
        $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            $installPaths = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
            foreach ($installPath in $installPaths) {
                if (-not $installPath) { continue }

                $path = Join-Path $installPath 'MSBuild\Current\Bin\MSBuild.exe'
                if (Test-Path -LiteralPath $path) { return $path }

                $path = Join-Path $installPath 'MSBuild\15.0\Bin\MSBuild.exe'
                if (Test-Path -LiteralPath $path) { return $path }
            }
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
