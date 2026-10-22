param([ValidateSet('linux-x64','osx-arm64','osx-x64')][string]$Rid='linux-x64',[switch]$SkipBootstrap,[switch]$SkipArchive)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$native=Join-Path $repo "runtimes/$Rid/native"
$manifest=Join-Path $repo "tools/binaries/$Rid.lock.json"
if(!$SkipBootstrap){& "$PSScriptRoot/bootstrap-unix.ps1" -Rid $Rid}
foreach($name in @('ffmpeg','ffprobe','magick','cjxl','djxl','native-dependencies.json')) {
 if(!(Test-Path -LiteralPath (Join-Path $native $name))){throw "Dépendance native absente : $name"}
}
if(!(Test-Path -LiteralPath (Join-Path $native 'native-lock.sha256')) -or
 (Get-Content -LiteralPath (Join-Path $native 'native-lock.sha256') -Raw) -ne (Get-FileHash -LiteralPath $manifest).Hash){throw 'Verrou natif non préparé ou obsolète'}
$output=[IO.Path]::GetFullPath((Join-Path $repo "artifacts/publish/$Rid"))
$expected=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts/publish'))+[IO.Path]::DirectorySeparatorChar
if(!$output.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase)){throw 'Dossier de publication hors du dépôt'}
# Generated publish output only. A clean directory prevents obsolete binaries/symbols entering a release.
if(Test-Path -LiteralPath $output){Remove-Item -LiteralPath $output -Recurse -Force}
dotnet publish "$repo/PixSeek.Desktop/PixSeek.Desktop.csproj" -c Release -r $Rid --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $output -v minimal
if($LASTEXITCODE -ne 0){throw "Compilation $Rid échouée"}
$version=([xml](Get-Content -LiteralPath "$repo/Directory.Build.props" -Raw)).Project.PropertyGroup | ForEach-Object Version | Where-Object {$_} | Select-Object -First 1
$working=$output
if($Rid -like 'osx-*') {
 $bundle=Join-Path $output 'PixSeek.app'
 $working=Join-Path $bundle 'Contents/MacOS'
 New-Item -ItemType Directory -Path $working -Force | Out-Null
 New-Item -ItemType Directory -Path (Join-Path $bundle 'Contents/Resources') -Force | Out-Null
 foreach($item in Get-ChildItem -LiteralPath $output -Force | Where-Object Name -ne 'PixSeek.app') {
  $source=[IO.Path]::GetFullPath($item.FullName)
  if(!$source.StartsWith($output+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Source hors publication'}
  Move-Item -LiteralPath $source -Destination $working
 }
 Copy-Item -LiteralPath "$repo/PixSeek/Assets/PixSeek.icns" -Destination (Join-Path $bundle 'Contents/Resources/PixSeek.icns')
 $plist=@"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>PixSeek</string>
<key>CFBundleDisplayName</key><string>PixSeek</string>
<key>CFBundleIdentifier</key><string>app.pixseek.desktop</string>
<key>CFBundleExecutable</key><string>PixSeek</string>
<key>CFBundleVersion</key><string>$version</string>
<key>CFBundleShortVersionString</key><string>$version</string>
<key>CFBundleIconFile</key><string>PixSeek.icns</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>LSMinimumSystemVersion</key><string>15.0</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
"@
 [IO.File]::WriteAllText((Join-Path $bundle 'Contents/Info.plist'),$plist)
}
Move-Item -LiteralPath (Join-Path $working 'PixSeek') -Destination (Join-Path $working 'PixSeek.bin')
$launcher=[IO.File]::ReadAllText("$repo/tools/packaging/app-launcher.sh").Replace('__RID__',$Rid).Replace([string][char]13,'')
[IO.File]::WriteAllText((Join-Path $working 'PixSeek'),$launcher)
Copy-Item -LiteralPath "$repo/PixSeek/Assets/PixSeek.png" -Destination (Join-Path $working 'PixSeek.png')
foreach($file in Get-ChildItem -LiteralPath $working -Recurse -File | Where-Object {$_.Extension -in @('.pdb','.dbg','.mdb','.lib','.a')}) {
 $path=[IO.Path]::GetFullPath($file.FullName)
 if(!$path.StartsWith($output+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Symbole hors publication'}
 Remove-Item -LiteralPath $path -Force
}
# On a native Unix builder chmod/sign the local tree as well; the C# tar writer also sets modes on Windows.
if(!$IsWindows) {
 Get-ChildItem -LiteralPath $working -File -Recurse | ForEach-Object {
  $s=[IO.File]::OpenRead($_.FullName);try{$b=[byte[]]::new(4);[void]$s.Read($b)}finally{$s.Dispose()}
  if(($b[0] -eq 0x7f -and $b[1] -eq 69) -or ($b[0] -eq 35 -and $b[1] -eq 33) -or ($b[0] -eq 207 -and $b[1] -eq 250 -and $b[2] -eq 237 -and $b[3] -eq 254)){& chmod +x $_.FullName}
 }
 if($IsMacOS -and $Rid -eq ('osx-'+[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant())) {
  & codesign --force --deep --sign - $bundle
  if($LASTEXITCODE -ne 0){throw 'Signature ad hoc échouée'}
 }
}
& "$PSScriptRoot/verify-unix-release.ps1" -Path $working -Rid $Rid
if(!$SkipArchive) {
 $archives=Join-Path $repo 'artifacts/releases'
 New-Item -ItemType Directory -Path $archives -Force | Out-Null
 $archive=Join-Path $archives "PixSeek-$version-$Rid.tar.gz"
 $packRoot=if($Rid -like 'osx-*'){$bundle}else{$output}
 $archiveRoot=if($Rid -like 'osx-*'){'PixSeek.app'}else{"PixSeek-$version-$Rid"}
 dotnet "$repo/tools/PixSeek.Packaging/bin/Release/net10.0/PixSeek.Packaging.dll" pack $packRoot $archive $archiveRoot
 if($LASTEXITCODE -ne 0){throw 'Création archive échouée'}
 if((Get-Item -LiteralPath $archive).Length -ge 2GB){throw 'Archive supérieure à la limite de 2 Gio par pièce jointe GitHub Release'}
 $validation= & dotnet "$repo/tools/PixSeek.Packaging/bin/Release/net10.0/PixSeek.Packaging.dll" verify-package $archive
 if($LASTEXITCODE -ne 0){throw 'Archive invalide apres relecture du contenu'}
 [IO.File]::WriteAllText((Join-Path $repo "artifacts/$Rid-archive-validation.json"),$validation)
 $hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
 [IO.File]::WriteAllText(($archive+'.sha256'),"$hash  $([IO.Path]::GetFileName($archive))"+[Environment]::NewLine)
 Write-Output "Archive : $archive"
}
Write-Output "Publication Release propre : $output"
