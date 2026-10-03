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
  if ($index.GetAttribute("root") -eq "") {
    $index.SetAttribute("root", "\")
  }
}

# Modern VS XAML/Appx targets emit Windows 10-style default qualifiers.
# The legacy Windows Phone 8.1 MakePri accepts the phone schema but rejects
# the Scale qualifier unless the matching phone resource qualification stack
# is present. This project contains no scale-qualified resource variants, so
# remove only the generated Scale defaults for WP8.1 packaging.
$scaleNodes = $xml.SelectNodes("/*[local-name()='resources']/*[local-name()='index']/*[local-name()='default']/*[local-name()='qualifier'][@name='Scale']")
$scaleCount = @($scaleNodes).Count
foreach ($node in @($scaleNodes)) {
  $node.ParentNode.RemoveChild($node) | Out-Null
}
Write-Host "Removed WP8.1-incompatible Scale default qualifiers: $scaleCount"

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

if ($after -ne "6.3.0") {
  throw "PRI config targetOsVersion is '$after'; expected 6.3.0 for Windows 8.1 MakePri."
}

$remainingScale = $verify.SelectNodes("/*[local-name()='resources']/*[local-name()='index']/*[local-name()='default']/*[local-name()='qualifier'][@name='Scale']")
if (@($remainingScale).Count -ne 0) {
  throw "PRI config still contains Scale qualifiers after WP8.1 patch."
}

Write-Host "=== PATCHED PRI CONFIG ==="
Write-Host $verify.OuterXml
$priDir = Split-Path -Parent $Path
$qualifierFiles = Get-ChildItem $priDir -Filter "qualifiers*.txt" -File -ErrorAction SilentlyContinue
foreach ($f in $qualifierFiles) {
  Write-Host "=== $($f.FullName) ==="
  Get-Content $f.FullName
}
