param([string]$RedistPath)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$lock=Get-Content "$repo/tools/binaries/msvc-win-x64.lock.json" -Raw | ConvertFrom-Json
if(-not $RedistPath) {
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if(Test-Path $vswhere) {
        $visualStudio=& $vswhere -latest -products '*' -property installationPath
        if($visualStudio){$RedistPath=Join-Path $visualStudio "VC/Redist/MSVC/$($lock.version)"}
    }
}
if(-not $RedistPath -or -not(Test-Path $RedistPath)) { throw "Redistribuables de build MSVC $($lock.version) absents. Fournir le répertoire redistribuable d'une installation Visual Studio licenciée à scripts/stage-windows-crt.ps1 -RedistPath. Voir DEPENDENCIES.md. Aucun runtime n'est installé globalement." }
$native=Join-Path $repo 'runtimes/win-x64/native'
$applocal=Join-Path $repo 'runtimes/win-x64/applocal'
New-Item -ItemType Directory -Force $native,$applocal | Out-Null
foreach($f in $lock.files) {
    $source=Join-Path $RedistPath $f.relativePath
    if(!(Test-Path $source) -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $f.sha256){throw "Redistribuable MSVC absent ou SHA-256 différent : $source"}
    Copy-Item -LiteralPath $source -Destination $applocal -Force
    Copy-Item -LiteralPath $source -Destination $native -Force
}
Write-Output "MSVC $($lock.version) : $($lock.files.Count) DLL redistribuables vérifiées, déploiement local."
