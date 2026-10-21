param([string]$Destination)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$Destination) { $Destination = Join-Path $repo 'models/faces' }
$manifest = Get-Content -LiteralPath (Join-Path $repo 'tools/models/faces.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force $Destination | Out-Null
foreach ($asset in $manifest.files) {
    $target = Join-Path $Destination $asset.path
    $valid = (Test-Path -LiteralPath $target) -and (Get-Item -LiteralPath $target).Length -eq $asset.size -and
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $asset.sha256
    if ($valid) { continue }
    $partial = $target + '.partial'
    try {
        Invoke-WebRequest -Uri $asset.url -OutFile $partial
        if ((Get-Item -LiteralPath $partial).Length -ne $asset.size -or (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $asset.sha256) {
            throw "Fichier de modèle facial invalide : $($asset.path)"
        }
        Move-Item -LiteralPath $partial -Destination $target -Force
    } finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    }
}
Write-Output "YuNet/SFace : $Destination"
