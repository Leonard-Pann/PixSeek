param([string]$Destination = '', [ValidateSet('xlmr','siglip2')][string]$Model = 'xlmr')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$manifestName = if ($Model -eq 'siglip2') { 'tools/models/siglip2.json' } else { 'tools/models/manifest.json' }
$manifestPath = Join-Path $repo $manifestName
if (!$Destination) {
    $defaultDirectory = if ($Model -eq 'siglip2') { 'artifacts/models/siglip2' } else { 'artifacts/models' }
    $Destination = Join-Path $repo $defaultDirectory
}
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
foreach ($f in $manifest.files) {
    $target = Join-Path $Destination $f.path
    New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
    if ((Test-Path $target) -and (Get-FileHash $target -Algorithm SHA256).Hash -eq $f.sha256) { continue }
    & curl.exe --fail --location --retry 3 --output "$target.partial" $f.url
    if ($LASTEXITCODE -ne 0) { throw "Échec : $($f.path)" }
    if ((Get-FileHash "$target.partial" -Algorithm SHA256).Hash -ne $f.sha256) { throw "SHA-256 invalide : $($f.path)" }
    Move-Item -LiteralPath "$target.partial" -Destination $target -Force
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $Destination 'manifest.json') -Force
