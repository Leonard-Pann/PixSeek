param([Parameter(Mandatory)][string]$Path,[ValidateSet('linux-x64','osx-arm64','osx-x64')][string]$Rid)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$Path=[IO.Path]::GetFullPath($Path)
$native=Join-Path $Path "runtimes/$Rid/native"
$packager=Join-Path $repo 'tools/PixSeek.Packaging/bin/Release/net10.0/PixSeek.Packaging.dll'
foreach($file in @('PixSeek','PixSeek.bin','PixSeek.dll','PixSeek.UI.dll','PixSeek.runtimeconfig.json','PixSeek.deps.json','LICENSE','THIRD_PARTY_NOTICES.md','Localization/en.json','Localization/fr.json','Localization/de.json','Localization/it.json','Localization/es.json')) {
 if(!(Test-Path -LiteralPath (Join-Path $Path $file))){throw "Fichier de release absent : $file"}
}
$files=@(Get-ChildItem -LiteralPath $Path -File -Recurse)
$unexpected=@($files | Where-Object {$_.Extension -in @('.pdb','.dbg','.mdb','.exe','.lib','.a') -or $_.FullName -match '[\\/]win-(x64|arm64)[\\/]|[\\/]\.dSYM[\\/]'})
if($unexpected.Count){throw ("Fichiers incompatibles/debug dans la release : "+($unexpected.FullName -join ', '))}
foreach($name in @('ffmpeg','ffprobe','magick','cjxl','djxl')) {
 if(!(Test-Path -LiteralPath (Join-Path $native $name))){throw "Outil absent : $name"}
}
$extension=if($Rid -like 'osx-*'){'dylib'}else{'so'}
foreach($lib in @("vec0.$extension","vlc/lib/libvlc.$extension","vlc/lib/libvlccore.$extension")) {
 if(!(Test-Path -LiteralPath (Join-Path $native $lib))){throw "Bibliothèque native absente : $lib"}
}
foreach($entry in @(@{Manifest='manifest.json';Directory='xlmr'},@{Manifest='faces.json';Directory='faces'})) {
 $source=Join-Path $repo "tools/models/$($entry.Manifest)"
 $manifest=Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
 $published=Join-Path $Path "models/$($entry.Manifest)"
 if(!(Test-Path -LiteralPath $published) -or (Get-FileHash -LiteralPath $published).Hash -ne (Get-FileHash -LiteralPath $source).Hash){throw 'Manifeste modèle différent'}
 foreach($asset in $manifest.files) {
  $file=Join-Path $Path "models/$($entry.Directory)/$($asset.path)"
  if(!(Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -ne $asset.size -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $asset.sha256){throw "Modèle absent/invalide : $file"}
 }
}
$json= & dotnet $packager inspect-tree $Path
if($LASTEXITCODE -ne 0){throw 'Analyse des binaires natifs échouée'}
$binaries=$json|ConvertFrom-Json
$names=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($entry in $binaries){[void]$names.Add([IO.Path]::GetFileName($entry.Path))}
$system=@('libc.so.6','libm.so.6','libdl.so.2','librt.so.1','libpthread.so.0','libresolv.so.2','libutil.so.1','ld-linux-x86-64.so.2','libz.so.1','libstdc++.so.6','libgcc_s.so.1')
$missing=@();$optional=@();$architecture=$Rid.Split('-')[-1]
foreach($entry in $binaries) {
 $binary=$entry.Binary
 if($binary.Architecture -and $binary.Architecture -ne $architecture){throw "Architecture incorrecte : $($entry.Path) : $($binary.Architecture)"}
 if(($Rid -like 'osx-*' -and $binary.Format -eq 'ELF') -or ($Rid -like 'linux-*' -and $binary.Format -like 'MachO*')){throw "Format natif incompatible : $($entry.Path)"}
 foreach($dependency in $binary.Dependencies) {
  if([IO.Path]::GetFileName($entry.Path) -eq 'libcoreclrtraceptprovider.so' -and $dependency -like 'liblttng-ust*'){$optional+='LTTng tracing (optional .NET runtime provider)';continue}
  if($Rid -like 'osx-*' -and ($dependency.StartsWith('/usr/lib/') -or $dependency.StartsWith('/System/Library/'))){continue}
  if($dependency -in $system){continue}
  if(!$names.Contains([IO.Path]::GetFileName($dependency))){$missing+=[pscustomobject]@{File=$entry.Path;Dependency=$dependency}}
 }
}
if($missing.Count){$missing|Format-Table|Out-String|Write-Output;throw "$($missing.Count) dépendances manquantes"}
$report=[ordered]@{Rid=$Rid;ManagedPublish='Success';DebugSymbols=0;WindowsExecutables=0;NativeBinaries=$binaries.Count;MissingNativeDependencies=0;OptionalTracingDependencies=$optional;Models='SHA-256 verified';NativeExecution='Not performed on this host'}
$hostRid=if($IsMacOS){'osx-'+[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()}elseif($IsLinux){'linux-'+[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()}else{'win-x64'}
if($hostRid -eq $Rid) {
 $diagnostics=Join-Path $repo "artifacts/$Rid-diagnostics.json"
 & (Join-Path $Path 'PixSeek') --diagnostics $diagnostics
 if($LASTEXITCODE -ne 0){throw 'Diagnostic natif échoué'}
 $report.NativeExecution='Diagnostics passed; GUI still requires manual testing'
}
$reportPath=Join-Path $repo "artifacts/$Rid-release-validation.json"
[IO.File]::WriteAllText($reportPath,($report|ConvertTo-Json -Depth 5))
Write-Output "$Rid : modèles vérifiés, $($binaries.Count) binaires natifs compatibles, aucun symbole debug/exécutable Windows."
