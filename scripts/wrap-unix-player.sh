#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
if [[ $# -ne 2 ]]; then echo 'Usage: wrap-unix-player.sh publish-directory RID' >&2; exit 1; fi
output="$1"
rid="$2"
[[ -d "$output/runtimes/$rid/native/vlc" ]] || { echo 'Native LibVLC missing' >&2; exit 1; }
if [[ ! -f "$output/PixSeek.bin" ]]; then mv -- "$output/PixSeek" "$output/PixSeek.bin"; fi
sed "s/__RID__/$rid/g" "$repo/tools/packaging/app-launcher.sh" > "$output/PixSeek"
chmod +x "$output/PixSeek" "$output/PixSeek.bin"
