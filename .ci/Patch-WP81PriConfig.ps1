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

$packagingNodes = $resources.SelectNodes("./*[local-name()='packaging']")
foreach ($node in $packagingNodes) {
  Write-Host "Removing modern PRI <packaging> node for WP8.1 MakePri."
  [void]$resources.RemoveChild($node)
}

$scaleQualifiers = $xml.SelectNodes("//*[local-name()='qualifier'][@name='Scale']")
foreach ($q in $scaleQualifiers) {
  Write-Host "PRI Scale before patch: $($q.GetAttribute("value"))"
  $q.SetAttribute("value", "240")
  Write-Host "PRI Scale after patch: $($q.GetAttribute("value"))"
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

if ($after -ne "6.3.0") {
  throw "PRI config targetOsVersion is '$after'; expected 6.3.0 for Windows 8.1 MakePri."
}
