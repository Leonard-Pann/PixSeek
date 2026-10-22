param([ValidateSet('linux-x64','osx-arm64','osx-x64')][string]$Rid = 'linux-x64')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$manifestPath=Join-Path $repo "tools/binaries/$Rid.lock.json"
$manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$work=Join-Path $repo 'artifacts/cross-build'
$downloads=Join-Path $work 'downloads'
$unpacked=Join-Path $work 'unpacked'
$native=Join-Path $repo "runtimes/$Rid/native"
$packager=Join-Path $repo 'tools/PixSeek.Packaging/bin/Release/net10.0/PixSeek.Packaging.dll'
foreach($folder in @($downloads,$unpacked,$native)) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
$expectedNative=[IO.Path]::GetFullPath((Join-Path $repo "runtimes/$Rid/native"))
if([IO.Path]::GetFullPath($native) -ne $expectedNative){throw 'Dossier natif inattendu'}
$currentLock=Join-Path $native 'native-lock.sha256'
if(!(Test-Path -LiteralPath $currentLock) -or (Get-Content -LiteralPath $currentLock -Raw) -ne (Get-FileHash -LiteralPath $manifestPath).Hash) {
 foreach($part in @('media','vlc','licenses/conda','licenses/ffmpeg')) {
  $target=[IO.Path]::GetFullPath((Join-Path $native $part))
  if(!$target.StartsWith($expectedNative+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Nettoyage hors dossier natif'}
  if(Test-Path -LiteralPath $target){Remove-Item -LiteralPath $target -Recurse -Force}
 }
}
if ($IsWindows) { $seven=Join-Path $downloads '7zip/7z.exe' }
else { $seven=@('7zz','7z') | ForEach-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1 -ExpandProperty Source }
if (!$seven -or !(Test-Path -LiteralPath $seven)) { throw '7-Zip requis sur le poste de build. Windows : scripts/prepare-packaging-tools.ps1 ; Unix : installer 7zz.' }
dotnet build "$repo/tools/PixSeek.Packaging/PixSeek.Packaging.csproj" -c Release -v quiet
if($LASTEXITCODE -ne 0){throw 'Compilation outil de packaging échouée'}
function Checked-Download($Asset) {
 $filename=if($Asset.filename){$Asset.filename}else{([uri]$Asset.url).Segments[-1]}
 $file=Join-Path $downloads ($Asset.sha256+'-'+$filename)
 if(!(Test-Path -LiteralPath $file)){Invoke-WebRequest -Uri $Asset.url -OutFile $file -TimeoutSec 300}
 if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $Asset.sha256){throw "SHA-256 incorrect : $filename"}
 return $file
}
$allAssets=@($manifest.mediaPackages)+@($manifest.ffmpeg.assets)+@($manifest.sqliteVec)
if($Rid -eq 'linux-x64'){$allAssets+=@($manifest.vlc.packages)}else{$allAssets+=@($manifest.vlc)}
# Download only; target binaries are never executed on the Windows build host.
$allAssets | ForEach-Object -Parallel {
 $a=$_
 $filename=if($a.filename){$a.filename}else{([uri]$a.url).Segments[-1]}
 $file=Join-Path $using:downloads ($a.sha256+'-'+$filename)
 if(!(Test-Path -LiteralPath $file)){Invoke-WebRequest -Uri $a.url -OutFile $file -TimeoutSec 300}
 if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $a.sha256){throw "SHA-256 incorrect : $filename"}
} -ThrottleLimit 8
function Expand-NativePackage($Asset) {
 $file=Checked-Download $Asset
 $directory=Join-Path $unpacked $Asset.sha256
 $done=Join-Path $directory '.complete'
 if(Test-Path -LiteralPath $done){return $directory}
 New-Item -ItemType Directory -Path $directory -Force | Out-Null
 $raw=Join-Path $directory '.archive'
 New-Item -ItemType Directory -Path $raw -Force | Out-Null
 & $seven x $file "-o$raw" -y *> (Join-Path $raw 'extract.log')
 if($LASTEXITCODE -ne 0){throw "Extraction échouée : $file"}
 foreach($compressed in @(Get-ChildItem -LiteralPath $raw -File | Where-Object {$_.Name -match '\.tar\.(zst|xz|bz2|gz)$'})) {
  & $seven x $compressed.FullName "-o$raw" -y *> (Join-Path $raw 'decompress.log')
  if($LASTEXITCODE -ne 0){throw "Décompression échouée : $($compressed.Name)"}
 }
 foreach($tar in @(Get-ChildItem -LiteralPath $raw -File -Filter '*.tar')) {
  dotnet $packager extract-tar $tar.FullName $directory 2>> (Join-Path $raw 'links.log')
  if($LASTEXITCODE -ne 0){throw "Extraction tar échouée : $($tar.Name)"}
 }
 [IO.File]::WriteAllText($done,$Asset.sha256)
 return $directory
}
function Copy-NativeFile($source,$destination) {
 New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
 Copy-Item -LiteralPath $source -Destination $destination -Force
}
$media=Join-Path $native 'media'
foreach($package in $manifest.mediaPackages) {
 $directory=Expand-NativePackage $package
 foreach($file in Get-ChildItem -LiteralPath $directory -File -Recurse | Where-Object {$_.FullName -notlike '*\.archive\*'}) {
  $relative=[IO.Path]::GetRelativePath($directory,$file.FullName).Replace('\','/')
  $include=($relative -match '^lib/' -and $relative -match '\.(dylib|so)(\.|$)' -and $relative -notmatch '/python') -or
   $relative -match '^etc/(ImageMagick|fonts)' -or $relative -match '^share/(fonts|ImageMagick)' -or $relative -in @('bin/magick','bin/cjxl','bin/djxl','bin/ffmpeg','bin/ffprobe')
  if($include){Copy-NativeFile $file.FullName (Join-Path $media $relative)}
 }
 $licenses=Join-Path $directory 'info/licenses'
 if(Test-Path -LiteralPath $licenses) {
  foreach($license in Get-ChildItem -LiteralPath $licenses -File -Recurse) {
   Copy-NativeFile $license.FullName (Join-Path $native ("licenses/conda/"+$package.name+"/"+[IO.Path]::GetRelativePath($licenses,$license.FullName)))
  }
 }
 $index=Join-Path $directory 'info/index.json'
 if(Test-Path -LiteralPath $index){Copy-NativeFile $index (Join-Path $native ("licenses/conda/"+$package.name+"/package.json"))}
}
foreach($asset in $manifest.ffmpeg.assets) {
 $file=Checked-Download $asset
 if($asset.name -match '^(ffmpeg|ffprobe)-.+\.gz$') {
  $tool=$Matches[1]
  $input=[IO.File]::OpenRead($file);$gzip=[IO.Compression.GZipStream]::new($input,[IO.Compression.CompressionMode]::Decompress)
  try{$output=[IO.File]::Create((Join-Path $native $tool));try{$gzip.CopyTo($output)}finally{$output.Dispose()}}finally{$gzip.Dispose();$input.Dispose()}
 } else {Copy-NativeFile $file (Join-Path $native ('licenses/ffmpeg/'+$asset.name))}
}
$vec=Expand-NativePackage $manifest.sqliteVec
$vecFile=Get-ChildItem -LiteralPath $vec -File -Recurse | Where-Object {$_.Name -in @('vec0.so','vec0.dylib')} | Select-Object -First 1
if(!$vecFile){throw 'sqlite-vec absent'}
Copy-NativeFile $vecFile.FullName (Join-Path $native $vecFile.Name)
if($Rid -eq 'linux-x64') {
 $prefix=Join-Path $work 'vlc-linux-prefix'
 foreach($package in $manifest.vlc.packages) {
  $directory=Expand-NativePackage $package
  foreach($libraryRoot in @('usr/lib','lib')) {
   foreach($file in Get-ChildItem -LiteralPath (Join-Path $directory $libraryRoot) -File -Recurse -ErrorAction SilentlyContinue) {
    Copy-NativeFile $file.FullName (Join-Path $prefix ([IO.Path]::GetRelativePath($directory,$file.FullName)))
   }
  }
  foreach($file in Get-ChildItem -LiteralPath (Join-Path $directory 'usr/share/doc') -File -Recurse -ErrorAction SilentlyContinue | Where-Object {$_.Name -eq 'copyright'}) {
   Copy-NativeFile $file.FullName (Join-Path $native ("licenses/debian/"+$package.name+"/copyright"))
  }
 }
 dotnet $packager stage-vlc $prefix (Join-Path $native 'vlc')
 if($LASTEXITCODE -ne 0){throw 'Dépendances VLC Linux incomplètes'}
} else {
 $file=Checked-Download $manifest.vlc
 $volume=Join-Path $unpacked $manifest.vlc.sha256
 if(!(Test-Path -LiteralPath (Join-Path $volume '.complete'))) {
  New-Item -ItemType Directory -Path $volume -Force | Out-Null
  # Extract real SDK binaries only. Materialize the two library aliases ourselves,
  # so preparing on Windows never requires the symbolic-link privilege.
  & $seven x $file "-o$volume" -y '-ir!*.dylib' '-ir!*COPYING*' '-ir!*LICENSE*' '-xr!libvlc.dylib' '-xr!libvlccore.dylib' *> (Join-Path $volume 'extract.log')
  if($LASTEXITCODE -ne 0){throw 'Extraction VLC.dmg échouée'}
  [IO.File]::WriteAllText((Join-Path $volume '.complete'),$manifest.vlc.sha256)
 }
 $lib=Get-ChildItem -LiteralPath $volume -Directory -Recurse | Where-Object {$_.Name -eq 'lib' -and $_.Parent.Name -eq 'MacOS'} | Select-Object -First 1
 if(!$lib){throw 'Bibliothèques VLC macOS absentes'}
 $vlcMac=$lib.Parent.FullName
 foreach($part in @('lib','plugins')) {
  foreach($file in Get-ChildItem -LiteralPath (Join-Path $vlcMac $part) -File -Recurse | Where-Object {$_.Extension -eq '.dylib' -and $_.Name -notin @('libmacosx_plugin.dylib','libosx_notifications_plugin.dylib')}) {
   Copy-NativeFile $file.FullName (Join-Path $native ('vlc/'+$part+'/'+[IO.Path]::GetRelativePath((Join-Path $vlcMac $part),$file.FullName)))
  }
 }
 foreach($name in @('libvlc','libvlccore')) {
  $real=Get-ChildItem -LiteralPath (Join-Path $native 'vlc/lib') -File | Where-Object {$_.Name -match ('^'+$name+'\.\d+\.dylib$')} | Select-Object -First 1
  if(!$real){throw "Bibliothèque VLC absente : $name"}
  Copy-NativeFile $real.FullName (Join-Path $native "vlc/lib/$name.dylib")
 }
 foreach($file in Get-ChildItem -LiteralPath $volume -File -Recurse | Where-Object {$_.Name -match '(?i)^(copying|license|authors)'}) {
  Copy-NativeFile $file.FullName (Join-Path $native ('licenses/vlc/'+$file.Name))
 }
}
foreach($tool in @('magick','cjxl','djxl','ffmpeg','ffprobe')) {
 $template=[IO.File]::ReadAllText((Join-Path $repo 'tools/packaging/media-launcher.sh'))
 [IO.File]::WriteAllText((Join-Path $native $tool), $template.Replace('__TOOL__',$tool))
 if(!(Test-Path -LiteralPath (Join-Path $media "bin/$tool"))){throw "Outil natif absent : $tool"}
}
if($Rid -like 'osx-*') {
 foreach($name in @('libmacosx_plugin.dylib','libosx_notifications_plugin.dylib')) {
  $plugin=[IO.Path]::GetFullPath((Join-Path $native "vlc/plugins/$name"))
  if(!$plugin.StartsWith([IO.Path]::GetFullPath($native)+[IO.Path]::DirectorySeparatorChar)){throw 'Chemin de plugin inattendu'}
  if(Test-Path -LiteralPath $plugin){Remove-Item -LiteralPath $plugin -Force}
 }
}
$fontDirectory=Join-Path $media 'etc/fonts'
New-Item -ItemType Directory -Path $fontDirectory -Force | Out-Null
$fontConfig=@'
<?xml version="1.0"?>
<!DOCTYPE fontconfig SYSTEM "urn:fontconfig:fonts.dtd">
<fontconfig>
  <dir prefix="relative">../../share/fonts</dir>
  <dir>/usr/share/fonts</dir><dir>/usr/local/share/fonts</dir>
  <dir>/System/Library/Fonts</dir><dir>/Library/Fonts</dir>
  <dir prefix="xdg">fonts</dir><cachedir prefix="xdg">fontconfig</cachedir>
</fontconfig>
'@
[IO.File]::WriteAllText((Join-Path $fontDirectory 'fonts.conf'),$fontConfig)
Copy-NativeFile $manifestPath (Join-Path $native 'native-dependencies.json')
[IO.File]::WriteAllText((Join-Path $native 'native-lock.sha256'),(Get-FileHash -LiteralPath $manifestPath).Hash)
Write-Output "$Rid : dépendances préparées (checksums vérifiés, aucune installation système)."
