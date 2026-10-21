param([string]$Destination = '', [ValidateSet('xlmr','siglip2')][string]$Model = 'xlmr')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$manifestName = if ($Model -eq 'siglip2') { 'tools/models/siglip2.json' } else { 'tools/models/manifest.json' }
$manifestPath = Join-Path $repo $manifestName
if (!$Destination) {
    $defaultDirectory = "models/$Model"
    $Destination = Join-Path $repo $defaultDirectory
}
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
foreach ($f in $manifest.files) {
    $target = Join-Path $Destination $f.path
    New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
    if ((Test-Path -LiteralPath $target) -and (Get-Item -LiteralPath $target).Length -eq $f.size -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $f.sha256) { continue }
    $partial = $target + '.partial'
    try {
        Invoke-WebRequest -Uri $f.url -OutFile $partial
        if ((Get-Item -LiteralPath $partial).Length -ne $f.size -or (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $f.sha256) { throw "Modèle invalide : $($f.path)" }
        Move-Item -LiteralPath $partial -Destination $target -Force
    } finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    }
}
