param([string]$Path = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/publish/win-x64'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
foreach ($entry in @(
    @{ Manifest = 'manifest.json'; Directory = 'xlmr' },
    @{ Manifest = 'faces.json'; Directory = 'faces' }
)) {
    $manifestPath = Join-Path $repo "tools/models/$($entry.Manifest)"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $publishedManifest = Join-Path $Path "models/$($entry.Manifest)"
    if (!(Test-Path -LiteralPath $publishedManifest) -or (Get-FileHash -LiteralPath $publishedManifest).Hash -ne (Get-FileHash -LiteralPath $manifestPath).Hash) {
        throw "Manifeste absent ou modifié : $($entry.Manifest)"
    }
    $expected = @($manifest.files | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $Path "models/$($entry.Directory)/$($_.path)")) })
    foreach ($asset in $manifest.files) {
        $file = Join-Path $Path "models/$($entry.Directory)/$($asset.path)"
        if (!(Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -ne $asset.size -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $asset.sha256) {
            throw "Modèle absent ou invalide : $($entry.Directory)/$($asset.path)"
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $Path "models/$($entry.Directory)") -File -Recurse) {
        if ($file.FullName -notin $expected) { throw "Actif inattendu : $($file.FullName)" }
    }
}
foreach ($file in @('LICENSE','THIRD_PARTY_NOTICES.md','licenses/Apache-2.0.txt','licenses/LGPL-2.1.txt','licenses/YuNet-MIT.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $Path $file))) { throw "Licence ou notice absente : $file" }
}
foreach ($unexpected in @('models/siglip2','models/weights','pixseek.db','settings.json','cache','logs','runtimes/win-x64/native/ffplay.exe')) {
    if (Test-Path -LiteralPath (Join-Path $Path $unexpected)) { throw "Fichier hors release : $unexpected" }
}
Write-Output 'Release vérifiée : modèles SHA-256, licences, aucune donnée utilisateur ni modèle optionnel.'
