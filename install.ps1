# Downloads the latest Hindsight.exe from GitHub Releases and runs it.
#   irm https://raw.githubusercontent.com/RamenNuudlss/Hindsight/main/install.ps1 | iex
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$release = Invoke-RestMethod 'https://api.github.com/repos/RamenNuudlss/Hindsight/releases/latest' -Headers @{ 'User-Agent' = 'Hindsight-installer' }
$asset = $release.assets | Where-Object { $_.name -eq 'Hindsight.exe' } | Select-Object -First 1
if (-not $asset) { throw "Couldn't find Hindsight.exe in the latest release." }

$target = Join-Path $env:TEMP 'Hindsight.exe'
Write-Host "Downloading Hindsight $($release.tag_name)..."
Invoke-WebRequest $asset.browser_download_url -OutFile $target -UseBasicParsing
Unblock-File $target
Start-Process $target
