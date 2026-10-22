$ErrorActionPreference='Stop'
if(!$IsWindows){throw 'Sur Unix, installer 7zz (brew install sevenzip / apt install 7zip) sur le poste de build.'}
$repo=Split-Path $PSScriptRoot -Parent
$cache=Join-Path $repo 'artifacts/cross-build/downloads'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$assets=@(
 @{name='7zr.exe';sha256='27cbe3d5804ad09e90bbcaa916da0d5c3b0be9462d0e0fb6cb54be5ed9030875'},
 @{name='7z2501-x64.exe';sha256='78afa2a1c773caf3cf7edf62f857d2a8a5da55fb0fff5da416074c0d28b2b55f'}
)
foreach($asset in $assets){
 $file=Join-Path $cache $asset.name
 if(!(Test-Path -LiteralPath $file)){Invoke-WebRequest -Uri ('https://github.com/ip7z/7zip/releases/download/25.01/'+$asset.name) -OutFile $file -TimeoutSec 180}
 if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $asset.sha256){throw 'Checksum 7-Zip incorrect'}
}
& "$cache/7zr.exe" x "$cache/7z2501-x64.exe" "-o$cache/7zip" -y
if($LASTEXITCODE -ne 0){throw 'Extraction 7-Zip échouée'}
Write-Output '7-Zip 25.01 préparé dans artifacts ; aucun installateur exécuté.'
