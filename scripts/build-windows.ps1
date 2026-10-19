$ErrorActionPreference='Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet restore PixSeek.slnx
    if($LASTEXITCODE -ne 0){throw 'Restore échoué'}
    dotnet build PixSeek.slnx -c Release --no-restore
    if($LASTEXITCODE -ne 0){throw 'Build échoué'}
} finally { Pop-Location }
