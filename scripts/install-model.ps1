param([string]$Destination = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/models'))
$ErrorActionPreference = 'Stop'
$manifest = Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools/models/manifest.json') -Raw | ConvertFrom-Json
foreach ($f in $manifest.files) {
    $target = Join-Path $Destination $f.path
    New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
    if ((Test-Path $target) -and (Get-FileHash $target -Algorithm SHA256).Hash -eq $f.sha256) { continue }
    & curl.exe --fail --location --retry 3 --output "$target.partial" $f.url
    if ($LASTEXITCODE -ne 0) { throw "Échec : $($f.path)" }
    if ((Get-FileHash "$target.partial" -Algorithm SHA256).Hash -ne $f.sha256) { throw "SHA-256 invalide : $($f.path)" }
    Move-Item -LiteralPath "$target.partial" -Destination $target -Force
}
Copy-Item (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools/models/manifest.json') $Destination -Force
