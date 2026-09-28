$ErrorActionPreference='Stop'
$solution=Join-Path $PSScriptRoot '..\ESP32-AI-Control-WP81.sln'
$msbuild=(Get-Command MSBuild.exe -ErrorAction SilentlyContinue).Source
if(-not $msbuild){throw 'MSBuild.exe not found. Install Visual Studio with WP8.1 tools.'}
& $msbuild $solution /m /t:Build /p:Configuration=Debug /p:Platform=ARM /verbosity:minimal
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
Write-Host 'WP8.1 Debug ARM build completed successfully.'
