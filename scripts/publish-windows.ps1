$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
& "$PSScriptRoot/verify-dependencies.ps1"
$output=Join-Path $repo 'artifacts/publish/win-x64'
$expectedOutput=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts/publish/win-x64'))
if ([IO.Path]::GetFullPath($output) -ne $expectedOutput) { throw 'Dossier de publication inattendu' }
# This directory contains generated build output only. Start fresh so removed files never enter a release.
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
dotnet publish "$repo/PixSeek.Desktop/PixSeek.Desktop.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $output
if($LASTEXITCODE -ne 0){throw 'Publication échouée'}
& "$PSScriptRoot/verify-dependencies.ps1" -NativePath "$output/runtimes/win-x64/native"
& "$PSScriptRoot/verify-release.ps1" -Path $output
Write-Output "Publication : $output/PixSeek.exe"
