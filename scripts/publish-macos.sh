#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
rid="${1:-osx-arm64}"
[[ "$rid" == osx-arm64 || "$rid" == osx-x64 ]] || exit 1
[[ -x "$repo/runtimes/$rid/native/ffmpeg" && -f "$repo/runtimes/$rid/native/vec0.dylib" ]] || { echo "Exécuter scripts/stage-unix.sh $rid sur le Mac de même architecture." >&2; exit 1; }
output="$repo/artifacts/publish/$rid"
dotnet publish "$repo/PixSeek.Desktop/PixSeek.Desktop.csproj" -c Release -r "$rid" --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o "$output"
bash "$repo/scripts/wrap-unix-player.sh" "$output" "$rid"
app="$output/PixSeek.app"
mkdir -p "$app/Contents/MacOS"
find "$output" -mindepth 1 -maxdepth 1 ! -name PixSeek.app -exec cp -R {} "$app/Contents/MacOS/" \;
cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict><key>CFBundleName</key><string>PixSeek</string><key>CFBundleIdentifier</key><string>app.pixseek.desktop</string><key>CFBundleExecutable</key><string>PixSeek</string><key>CFBundleVersion</key><string>0.1.0</string><key>CFBundlePackageType</key><string>APPL</string><key>NSHighResolutionCapable</key><true/></dict></plist>
PLIST
find "$app/Contents/MacOS" -type f \( -name '*.dylib' -o -name ffmpeg -o -name ffprobe -o -name ffplay -o -name magick -o -name cjxl -o -name djxl -o -name PixSeek.bin \) -exec codesign --force --sign - {} \;
codesign --force --deep --sign - "$app"
"$app/Contents/MacOS/PixSeek" --diagnostics "$repo/artifacts/macos-$rid-diagnostics.json"
echo "Bundle non notarisé : $app"
