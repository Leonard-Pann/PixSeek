param([string]$Rid = 'win-x64')
$ErrorActionPreference = 'Stop'
if ($Rid -ne 'win-x64') { throw 'Le bootstrap validé cible win-x64. Voir docs/CROSS_PLATFORM.md.' }
$repo = Split-Path $PSScriptRoot -Parent
$native = Join-Path $repo "runtimes/$Rid/native"
$cache = Join-Path $repo 'artifacts/downloads'
New-Item -ItemType Directory -Force $native,$cache | Out-Null
$lock = Get-Content (Join-Path $repo "tools/binaries/$Rid.lock.json") -Raw | ConvertFrom-Json
foreach ($tool in $lock) {
    $archive = Join-Path $cache ([IO.Path]::GetFileName($tool.url))
    if (!(Test-Path $archive) -or (Get-FileHash $archive -Algorithm SHA256).Hash -ne $tool.sha256) {
        & curl.exe --fail --location --retry 3 --output $archive $tool.url
        if ($LASTEXITCODE -ne 0) { throw "Téléchargement échoué : $($tool.name)" }
    }
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne $tool.sha256) { throw "SHA-256 incorrect : $archive" }
    $extracted = Join-Path $cache "$($tool.name)-$($tool.version)"
    New-Item -ItemType Directory -Force $extracted | Out-Null
    & tar -xf $archive -C $extracted
    if ($LASTEXITCODE -ne 0) { throw "Extraction échouée : $archive (tar Windows 11 requis)" }
    if ($tool.name -eq 'ffmpeg') {
        Get-ChildItem $extracted -Recurse -File | Where-Object Name -in @('ffmpeg.exe','ffprobe.exe') | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $native -Force }
        Get-ChildItem $extracted -Recurse -File | Where-Object Name -match 'LICENSE|README' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $native "ffmpeg-$($_.Name)") -Force }
    } elseif ($tool.name -eq 'imagemagick') {
        Get-ChildItem $extracted -Recurse -File | Where-Object { $_.Name -eq 'magick.exe' -or $_.Extension -in @('.xml','.icc','.dll','.txt','.md') } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $native -Force }
    } elseif ($tool.name -eq 'libjxl') {
        foreach ($exe in @('cjxl.exe','djxl.exe')) { Get-ChildItem $extracted -Recurse -Filter $exe | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $native -Force } }
        Get-ChildItem $extracted -Recurse -File | Where-Object Name -match 'LICENSE|COPYING' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $native "libjxl-$($_.Name)") -Force }
    } else {
        Get-ChildItem $extracted -Recurse -Filter 'vec0.dll' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $native -Force }
    }
}
& (Join-Path $PSScriptRoot 'stage-windows-crt.ps1')
& (Join-Path $PSScriptRoot 'verify-dependencies.ps1') -NativePath $native
