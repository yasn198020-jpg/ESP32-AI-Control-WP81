param(
  [Parameter(Mandatory=$true)]
  [string]$Path
)

if (-not (Test-Path $Path)) {
  throw "PRI config not found: $Path"
}

$encoding = New-Object System.Text.UTF8Encoding($false)
$text = [IO.File]::ReadAllText($Path)

$match = [regex]::Match($text, '<resources(?:\s+targetOsVersion\s*=\s*["'']([^"'']+)["''])?')
if (-not $match.Success) {
  throw "PRI config root <resources> element was not found: $Path"
}

$before = $match.Groups[1].Value
Write-Host "PRI targetOsVersion before patch: " + ($(if ($before) { $before } else { "<missing>" }))

if ($before -eq '10.0.0') {
  $text = [regex]::Replace(
    $text,
    '(<resources\s+targetOsVersion\s*=\s*["''])10\.0\.0(["''])',
    '16.3.02',
    1
  )
} elseif (-not $before) {
  $text = [regex]::Replace(
    $text,
    '<resources(\s|>)',
    '<resources targetOsVersion="6.3.0"1',
    1
  )
}

[IO.File]::WriteAllText($Path, $text, $encoding)

$afterMatch = [regex]::Match($text, '<resources\s+targetOsVersion\s*=\s*["'']([^"'']+)["'']')
if (-not $afterMatch.Success) {
  throw "PRI config still has no targetOsVersion after patch."
}

$after = $afterMatch.Groups[1].Value
Write-Host "PRI targetOsVersion after patch: $after"

if ($after -ne '6.3.0') {
  throw "PRI config targetOsVersion is '$after'; expected 6.3.0 for Windows 8.1 MakePri."
}
