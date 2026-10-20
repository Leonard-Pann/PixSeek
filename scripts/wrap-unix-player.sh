#!/usr/bin/env bash
set -euo pipefail
output="${1:?publish directory required}"
rid="${2:?RID required}"
[[ -d "$output/runtimes/$rid/native/vlc" ]] || { echo 'Stage LibVLC before publishing.' >&2; exit 1; }
mv "$output/PixSeek" "$output/PixSeek.bin"
cat > "$output/PixSeek" <<'LAUNCHER'
#!/bin/sh
app_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
for vlc_dir in "$app_dir"/runtimes/*/native/vlc; do
  [ -d "$vlc_dir" ] || continue
  export VLC_PLUGIN_PATH="$vlc_dir/plugins"
  export LD_LIBRARY_PATH="$vlc_dir/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
  export DYLD_LIBRARY_PATH="$vlc_dir/lib${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}"
done
exec "$app_dir/PixSeek.bin" "$@"
LAUNCHER
chmod +x "$output/PixSeek" "$output/PixSeek.bin"
