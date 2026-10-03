param(
  [Parameter(Mandatory=$true)]
  [string]$Path
)

if (-not (Test-Path $Path)) {
  throw "PRI config not found: $Path"
}

$xml = New-Object System.Xml.XmlDocument
$xml.PreserveWhitespace = $true
$xml.Load($Path)

$resources = $xml.SelectSingleNode("/*[local-name()='resources']")
if (-not $resources) {
  throw "PRI config root <resources> element was not found: $Path"
}

$before = $resources.GetAttribute("targetOsVersion")
Write-Host "PRI targetOsVersion before patch: " + ($(if ($before) { $before } else { "<missing>" }))

$resources.SetAttribute("targetOsVersion", "6.3.0")

$indexes = $xml.SelectNodes("//*[local-name()='index']")
foreach ($index in $indexes) {
  $index.SetAttribute("root", "")

}

$settings = New-Object System.Xml.XmlWriterSettings
$settings.Encoding = New-Object System.Text.UTF8Encoding($false)
$settings.Indent = $false
$settings.OmitXmlDeclaration = $false

$writer = [System.Xml.XmlWriter]::Create($Path, $settings)
try {
  $xml.Save($writer)
}
finally {
  $writer.Close()
}

$verify = New-Object System.Xml.XmlDocument
$verify.Load($Path)
$root = $verify.SelectSingleNode("/*[local-name()='resources']")
$after = $root.GetAttribute("targetOsVersion")
Write-Host "PRI targetOsVersion after patch: $after"

$badRoots = @($verify.SelectNodes("//*[local-name()='index']") | Where-Object { $_.GetAttribute("root") -ne "" })
if ($badRoots.Count -ne 0) {
  throw "PRI config still contains an index with root different from \": $Path"
}
Write-Host "PRI index roots verified: \" 

if ($after -ne "6.3.0") {
  throw "PRI config targetOsVersion is '$after'; expected 6.3.0 for Windows 8.1 MakePri."
}


Write-Host "=== PATCHED PRI CONFIG ==="
Write-Host $verify.OuterXml
$priDir = Split-Path -Parent $Path
$qualifierFiles = Get-ChildItem $priDir -Filter "qualifiers*.txt" -File -ErrorAction SilentlyContinue
foreach ($f in $qualifierFiles) {
  Write-Host "=== $($f.FullName) ==="
  Get-Content $f.FullName
}
