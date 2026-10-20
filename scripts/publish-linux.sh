#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
rid=linux-x64
[[ -x "$repo/runtimes/$rid/native/ffmpeg" && -f "$repo/runtimes/$rid/native/vec0.so" ]] || { echo 'Exécuter scripts/stage-unix.sh linux-x64 sur Linux après préparation des outils de build.' >&2; exit 1; }
dotnet publish "$repo/PixSeek.Desktop/PixSeek.Desktop.csproj" -c Release -r "$rid" --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o "$repo/artifacts/publish/$rid"
chmod +x "$repo/artifacts/publish/$rid/PixSeek" "$repo/artifacts/publish/$rid/runtimes/$rid/native/"{ffmpeg,ffprobe,ffplay,magick,cjxl,djxl}
bash "$repo/scripts/wrap-unix-player.sh" "$repo/artifacts/publish/$rid" "$rid"
"$repo/artifacts/publish/$rid/PixSeek" --diagnostics "$repo/artifacts/linux-diagnostics.json"
