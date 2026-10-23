param(
    [string]$AndroidSdkDirectory = '',
    [string]$JavaSdkDirectory = '',
    [string]$KeyStore = '',
    [string]$PasswordFile = '',
    [string]$KeyAlias = 'pixseek',
    [switch]$SkipPrepare,
    [switch]$EmulatorOnly
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    if (!$AndroidSdkDirectory) { $AndroidSdkDirectory = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { 'C:/Program Files (x86)/Android/android-sdk' } }
    if (!$JavaSdkDirectory) { $JavaSdkDirectory = if ($env:JAVA_HOME) { $env:JAVA_HOME } else { 'C:/Program Files (x86)/Android/openjdk/jdk-21.0.8' } }
    if (!(Test-Path -LiteralPath (Join-Path $AndroidSdkDirectory build-tools/36.0.0))) { throw 'Android SDK build-tools 36.0.0 required.' }
    if (!(Test-Path -LiteralPath (Join-Path $JavaSdkDirectory bin/keytool.exe))) { throw 'JDK 21 required.' }
    if (!$SkipPrepare) { & "$PSScriptRoot/prepare-android.ps1" }
    $signing = (New-Item -ItemType Directory -Path artifacts/android/signing -Force).FullName
    if (!$KeyStore) { $KeyStore = Join-Path $signing pixseek-release.keystore }
    if (!$PasswordFile) { $PasswordFile = Join-Path $signing password.txt }
    if (!(Test-Path -LiteralPath $KeyStore)) {
        if (!(Test-Path -LiteralPath $PasswordFile)) {
            $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(36))
            [IO.File]::WriteAllText($PasswordFile, $password)
        }
        & (Join-Path $JavaSdkDirectory bin/keytool.exe) -genkeypair -keystore $KeyStore -storetype PKCS12 -alias $KeyAlias -keyalg RSA -keysize 3072 -sigalg SHA256withRSA -validity 10000 -dname 'CN=PixSeek, OU=PixSeek, O=PixSeek' "-storepass:file" $PasswordFile "-keypass:file" $PasswordFile
        if ($LASTEXITCODE -ne 0) { throw 'Release keystore creation failed' }
    }
    $rids = if ($EmulatorOnly) { @('android-x64') } else { @('android-arm64','android-arm','android-x64','android-x86') }
    $previousSigningPassword = $env:PIXSEEK_ANDROID_SIGNING_PASSWORD
    $env:PIXSEEK_ANDROID_SIGNING_PASSWORD = [IO.File]::ReadAllText($PasswordFile).Trim()
    # Shared net10.0 references need their RID build before the Android multi-ABI inner builds.
    foreach ($rid in $rids) {
        foreach ($project in @('PixSeek.Core','PixSeek.Infrastructure','PixSeek.Inference','PixSeek.Mobile')) {
            dotnet build "src/$project/$project.csproj" -c Release -r $rid
            if ($LASTEXITCODE -ne 0) { throw "Shared build failed: $project $rid" }
        }
    }
    $ridList = $rids -join '%3B'
    $publishArguments = @('publish','PixSeek.Android/PixSeek.Android.csproj','-c','Release',"-p:RuntimeIdentifiers=$ridList",
        "-p:AndroidSdkDirectory=$AndroidSdkDirectory", "-p:JavaSdkDirectory=$JavaSdkDirectory",
        '-p:AndroidKeyStore=true', "-p:AndroidSigningKeyStore=$KeyStore", "-p:AndroidSigningKeyAlias=$KeyAlias",
        '-p:AndroidSigningStorePass=env:PIXSEEK_ANDROID_SIGNING_PASSWORD', '-p:AndroidSigningKeyPass=env:PIXSEEK_ANDROID_SIGNING_PASSWORD')
    if ($EmulatorOnly) { $publishArguments += @('-r','android-x64') }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw 'Android Release publish failed' }
    $signed = Get-ChildItem PixSeek.Android/bin/Release/net10.0-android36.0 -Recurse -Filter app.pixseek.mobile-Signed.apk | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (!$signed) { throw 'Signed APK missing' }
    $releases = (New-Item -ItemType Directory -Path artifacts/releases -Force).FullName
    $name = if ($EmulatorOnly) { 'PixSeek-0.3.2-android-x64-test.apk' } else { 'PixSeek-0.3.2-android.apk' }
    $output = Join-Path $releases $name
    Copy-Item -LiteralPath $signed.FullName -Destination $output -Force
    $env:JAVA_HOME = $JavaSdkDirectory
    & (Join-Path $AndroidSdkDirectory build-tools/36.0.0/apksigner.bat) verify --verbose --print-certs $output
    if ($LASTEXITCODE -ne 0) { throw 'APK signature invalid' }
    & (Join-Path $AndroidSdkDirectory build-tools/36.0.0/zipalign.exe) -c -P 16 -v 4 $output | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { throw 'APK alignment invalid' }
    $hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($output + '.sha256'), "$hash  $name" + [Environment]::NewLine)
    Write-Output "Signed Release APK: $output"
} finally { $env:PIXSEEK_ANDROID_SIGNING_PASSWORD = $previousSigningPassword; Pop-Location }
