param([string]$NativePath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'runtimes/win-x64/native'))
$ErrorActionPreference = 'Stop'
foreach ($exe in @('ffmpeg.exe','ffprobe.exe','magick.exe','cjxl.exe','djxl.exe','vec0.dll','msvcp140.dll','msvcp140_1.dll','vcruntime140.dll','vcruntime140_1.dll','vcomp140.dll')) { if (!(Test-Path (Join-Path $NativePath $exe))) { throw "Dépendance absente : $exe" } }
& "$NativePath/ffmpeg.exe" -version | Select-Object -First 1
& "$NativePath/ffprobe.exe" -version | Select-Object -First 1
& "$NativePath/magick.exe" -version
$encoders = & "$NativePath/ffmpeg.exe" -hide_banner -encoders 2>&1 | Out-String
if ($encoders -notmatch 'libsvtav1|libaom-av1') { throw 'Aucun encodeur AV1' }
$formats = & "$NativePath/magick.exe" identify -list format | Out-String
if ($formats -notmatch 'JXL\*?\s+rw') { throw 'JPEG XL indisponible dans ImageMagick' }
$formats -split "`n" | Select-String 'JXL|AVIF|HEIC'
