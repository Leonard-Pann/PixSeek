#!/usr/bin/env bash
# Run on the target OS with the LibVLC SDK installed as a build dependency.
set -euo pipefail
rid="${1:?RID required}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
target="$repo/runtimes/$rid/native/vlc"
mkdir -p "$target/lib" "$target/plugins"
if [[ "$rid" == linux-* ]]; then
  libdir="$(pkg-config --variable=libdir libvlc)"
  plugins="$libdir/vlc/plugins"
  [[ -f "$libdir/libvlc.so" && -d "$plugins" ]] || { echo 'Install libvlc-dev and vlc-plugin-base on the build runner.' >&2; exit 1; }
  cp -L "$libdir"/libvlc.so* "$libdir"/libvlccore.so* "$target/lib/"
  cp -RL "$plugins/." "$target/plugins/"
  while IFS= read -r -d '' binary; do
    while IFS= read -r lib; do
      case "$(basename "$lib")" in libc.so.*|libm.so.*|libpthread.so.*|libdl.so.*|librt.so.*|ld-linux*) continue;; esac
      cp -L "$lib" "$target/lib/$(basename "$lib")"
    done < <(ldd "$binary" | awk '/=> \// {print $3} /^\// {print $1}')
  done < <(find "$target" -type f -name '*.so*' -print0)
else
  source="${PIXSEEK_VLC_APP:-/Applications/VLC.app}/Contents/MacOS"
  [[ -d "$source/lib" && -d "$source/plugins" ]] || { echo 'Provide a VLC.app matching the target architecture (build dependency).' >&2; exit 1; }
  cp -RL "$source/lib/." "$target/lib/"
  cp -RL "$source/plugins/." "$target/plugins/"
  while IFS= read -r -d '' binary; do
    dylibbundler -b -x "$binary" -d "$target/lib" -p '@loader_path/' -of
  done < <(find "$target" -type f -name '*.dylib' -print0)
fi
echo "LibVLC staged at $target. Native Unix validation is still required."
