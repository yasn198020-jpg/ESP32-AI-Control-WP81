param(
  [Parameter(Mandatory=$true)]
  [string]$Path
)

if (-not (Test-Path $Path)) {
  throw "PRI config not found: $Path"
}

$encoding = New-Object System.Text.UTF8Encoding($false)
$text = [IO.File]::ReadAllText($Path)
$matches = [regex]::Matches($text, 'targetOsVersion\s*=\s*["'']([^"'']+)["'']')

if ($matches.Count -eq 0) {
  throw "No targetOsVersion attribute found in PRI config: $Path"
}

Write-Host "PRI targetOsVersion before patch:"
$matches | ForEach-Object { Write-Host ("  " + $_.Groups[1].Value) }

$text = [regex]::Replace(
  $text,
  '(targetOsVersion\s*=\s*["''])10\.0\.0(["''])',
  '16.3.12'
)

[IO.File]::WriteAllText($Path, $text, $encoding)

$after = [regex]::Matches($text, 'targetOsVersion\s*=\s*["'']([^"'']+)["'']')
Write-Host "PRI targetOsVersion after patch:"
$after | ForEach-Object { Write-Host ("  " + $_.Groups[1].Value) }

$bad = @($after | Where-Object { $_.Groups[1].Value -eq '10.0.0' })
if ($bad.Count -gt 0) {
  throw "PRI config still contains targetOsVersion=10.0.0 after patch."
}

$good = @($after | Where-Object { $_.Groups[1].Value -eq '6.3.1' })
if ($good.Count -eq 0) {
  throw "PRI config does not contain targetOsVersion=6.3.1 after patch."
}
