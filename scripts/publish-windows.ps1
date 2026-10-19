param([switch]$IncludeModel)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
& "$PSScriptRoot/verify-dependencies.ps1"
$output=Join-Path $repo 'artifacts/publish/win-x64'
dotnet publish "$repo/PixSeek.Desktop/PixSeek.Desktop.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $output
if($LASTEXITCODE -ne 0){throw 'Publication échouée'}
# Retirer uniquement les anciens artefacts générés lors du renommage du produit.
Get-ChildItem -LiteralPath $output -File | Where-Object {
    $_.Name -match '^Lumina(?:\.(?:AI|Core|Infrastructure|Media))?\.(?:exe|dll|pdb|deps\.json|runtimeconfig\.json)$'
} | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
if($IncludeModel) {
    & "$PSScriptRoot/install-model.ps1"
    $modelTarget=Join-Path $output 'models/weights'
    New-Item -ItemType Directory -Force $modelTarget | Out-Null
    Copy-Item -Path "$repo/artifacts/models/*" -Destination $modelTarget -Recurse -Force
}
& "$PSScriptRoot/verify-dependencies.ps1" -NativePath "$output/runtimes/win-x64/native"
Write-Output "Publication : $output/PixSeek.exe"
