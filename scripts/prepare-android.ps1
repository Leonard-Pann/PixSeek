param([string]$Python = 'python', [switch]$SkipModelGeneration)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    $root = (New-Item -ItemType Directory -Path artifacts/android/toolchain -Force).FullName
    $lock = Get-Content tools/binaries/android.lock.json -Raw | ConvertFrom-Json
    function Fetch($asset, $name) {
        $target = Join-Path $root $name
        if (!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $asset.sha256) {
            Invoke-WebRequest $asset.url -OutFile ($target + '.partial')
            if ((Get-FileHash -LiteralPath ($target + '.partial') -Algorithm SHA256).Hash -ne $asset.sha256) { throw "Checksum incorrect: $name" }
            Move-Item -LiteralPath ($target + '.partial') -Destination $target -Force
        }
        return $target
    }
    & "$PSScriptRoot/prepare-packaging-tools.ps1"
    $sevenZip = Join-Path $repo artifacts/cross-build/downloads/7zip/7z.exe
    $ndk = Join-Path $root android-ndk-r30
    if (!(Test-Path -LiteralPath (Join-Path $ndk source.properties))) {
        $archive = Fetch $lock.ndk android-ndk-r30-windows.zip
        & $sevenZip x $archive "-o$root" -y -bso0 -bsp0
        if ($LASTEXITCODE -ne 0) { throw 'NDK extraction failed' }
    }
    $sources = (New-Item -ItemType Directory -Path artifacts/android/native-src -Force).FullName
    $vec = Fetch $lock.sqliteVec sqlite-vec.tar.gz
    tar -xzf $vec -C $sources
    if ($LASTEXITCODE -ne 0) { throw 'sqlite-vec extraction failed' }
    $headers = Fetch $lock.sqliteHeaders sqlite-amalgamation.zip
    Expand-Archive -LiteralPath $headers -DestinationPath $sources -Force
    $llvm = Join-Path $ndk toolchains/llvm/prebuilt/windows-x86_64/bin
    foreach ($entry in @(@('arm64-v8a','aarch64-linux-android28'), @('armeabi-v7a','armv7a-linux-androideabi28'), @('x86_64','x86_64-linux-android28'), @('x86','i686-linux-android28'))) {
        $destination = (New-Item -ItemType Directory -Path "runtimes/android/$($entry[0])" -Force).FullName
        & (Join-Path $llvm ($entry[1] + '-clang.cmd')) @($lock.cflags) -I (Join-Path $sources sqlite-amalgamation-3500400) (Join-Path $sources sqlite-vec.c) -o (Join-Path $destination libvec0.so) -lm
        if ($LASTEXITCODE -ne 0) { throw "sqlite-vec build failed: $($entry[0])" }
    }
    $assets = (New-Item -ItemType Directory -Path artifacts/android/assets/models -Force).FullName
    if (!$SkipModelGeneration) {
        & "$PSScriptRoot/install-model.ps1"
        dotnet run --project build/PixSeek.Build.csproj -c Release -- $repo (Join-Path $root model-assets.txt)
        if ($LASTEXITCODE -ne 0) { throw 'Source models unavailable' }
        Copy-Item tools/models/manifest.json models/manifest.json -Force
        dotnet run --project tools/PixSeek.MobileAssets -c Release -- models/xlmr/textual/tokenizer.json artifacts/android/assets/models/xlmr/textual/sentencepiece.model
        if ($LASTEXITCODE -ne 0) { throw 'Tokenizer parity check failed' }
        $pythonEnv = Join-Path $repo artifacts/android/python-env/Scripts/python.exe
        if (!(Test-Path -LiteralPath $pythonEnv)) { & $Python -m venv artifacts/android/python-env }
        & $pythonEnv -m pip install -r tools/models/android-build-requirements.txt --disable-pip-version-check
        if ($LASTEXITCODE -ne 0) { throw 'Build-time quantization tools unavailable' }
        & $pythonEnv tools/models/quantize-android.py models artifacts/android/assets/models
        if ($LASTEXITCODE -ne 0) { throw 'ONNX quantization failed' }
    }
    $expected = Get-Content tools/models/android-int8.json -Raw | ConvertFrom-Json
    foreach ($asset in $expected.files) {
        $file = Join-Path $assets ('xlmr/' + $asset.path)
        if (!(Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -ne $asset.size -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $asset.sha256) { throw "Mobile model mismatch: $($asset.path)" }
    }
    Copy-Item tools/models/android-int8.json (Join-Path $assets manifest.json) -Force
    Copy-Item tools/models/faces.json (Join-Path $assets faces.json) -Force
    New-Item -ItemType Directory -Path (Join-Path $assets faces) -Force | Out-Null
    Copy-Item models/faces/*.onnx (Join-Path $assets faces) -Force
    $licenses = (New-Item -ItemType Directory -Path artifacts/android/assets/licenses -Force).FullName
    Copy-Item LICENSE,THIRD_PARTY_NOTICES.md,DEPENDENCIES.md $licenses -Force
    Copy-Item licenses/* $licenses -Recurse -Force
    Copy-Item tools/binaries/android.lock.json $licenses -Force
    $assetRoot = Split-Path $assets -Parent
    $index = @(Get-ChildItem -LiteralPath $assetRoot -Recurse -File | Where-Object Name -ne 'asset-index.json' | ForEach-Object {
        [ordered]@{ Path = [IO.Path]::GetRelativePath($assetRoot, $_.FullName).Replace('\','/'); Size = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    [IO.File]::WriteAllText((Join-Path $assetRoot asset-index.json), ($index | ConvertTo-Json -Depth 5))
    Write-Output "Android: four ABIs prepared, $($index.Count) assets verified."
} finally { Pop-Location }
