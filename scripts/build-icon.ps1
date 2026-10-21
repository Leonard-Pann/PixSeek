param([string]$Magick)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (!$Magick) { $Magick = Join-Path $repo 'runtimes/win-x64/native/magick.exe' }
if (!(Test-Path -LiteralPath $Magick)) { throw 'ImageMagick embarqué absent. Exécuter bootstrap-windows.ps1.' }
$assets = Join-Path $repo 'PixSeek/Assets'
& $Magick -background none "$assets/PixSeek.svg" -resize 256x256 "$assets/PixSeek.png"
if ($LASTEXITCODE -ne 0) { throw 'Échec du rendu PNG.' }
& $Magick -background none "$assets/PixSeek.svg" -define icon:auto-resize=256,128,64,48,32,24,16 "$assets/PixSeek.ico"
if ($LASTEXITCODE -ne 0) { throw 'Échec du rendu ICO.' }
& $Magick identify "$assets/PixSeek.ico"
if ($LASTEXITCODE -ne 0) { throw 'Icône invalide.' }
