#!/usr/bin/env bash
set -euo pipefail
rid="${1:?RID requis : linux-x64, osx-arm64 ou osx-x64}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
native="$repo/runtimes/$rid/native"
mkdir -p "$native/lib"
tools=(ffmpeg ffprobe magick cjxl djxl)
for name in "${tools[@]}"; do
  original="$(command -v "$name")" || { echo "Outil de build manquant : $name" >&2; exit 1; }
  if [[ "$rid" == linux-* ]]; then
    cp -L "$original" "$native/$name.real"
    # Include the loader's complete dependency closure; retain only glibc and the ELF loader as host requirements.
    while IFS= read -r lib; do
      case "$(basename "$lib")" in libc.so.*|libm.so.*|libpthread.so.*|libdl.so.*|librt.so.*|ld-linux*) continue;; esac
      cp -L "$lib" "$native/lib/$(basename "$lib")"
    done < <(ldd "$original" | awk '/=> \// {print $3} /^\// {print $1}')
    cat > "$native/$name" <<'LAUNCHER'
#!/bin/sh
native_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
export LD_LIBRARY_PATH="$native_dir/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec "$native_dir/$(basename -- "$0").real" "$@"
LAUNCHER
  else
    cp -L "$original" "$native/$name"
    dylibbundler -b -x "$native/$name" -d "$native/lib" -p '@executable_path/lib/' -of
  fi
  chmod +x "$native/$name"
done
if [[ "$rid" == linux-* ]]; then os=linux; arch=x86_64; ext=so; sha=438e0df29f3f8db3525b3aa0dcc0a199869c0bcec9d7abc5b51850469caf867f
elif [[ "$rid" == osx-arm64 ]]; then os=macos; arch=aarch64; ext=dylib; sha=142e195b654092632fecfadbad2825f3140026257a70842778637597f6b8c827
else os=macos; arch=x86_64; ext=dylib; sha=35d014e5f7bcac52645a97f1f1ca34fdb51dcd61d81ac6e6ba1c712393fbf8fd; fi
archive="$repo/artifacts/sqlite-vec-$rid.tar.gz"
curl -fL --retry 3 "https://github.com/asg017/sqlite-vec/releases/download/v0.1.6/sqlite-vec-0.1.6-loadable-$os-$arch.tar.gz" -o "$archive"
actual="$(shasum -a 256 "$archive" | awk '{print $1}')"
[[ "$actual" == "$sha" ]] || { echo 'SHA-256 sqlite-vec incorrect' >&2; exit 1; }
tar -xf "$archive" -C "$native"
[[ -f "$native/vec0.$ext" ]] || { echo 'sqlite-vec absent' >&2; exit 1; }
"$native/ffmpeg" -version > "$native/ffmpeg-build.txt"
"$native/magick" -version > "$native/imagemagick-build.txt"
"$native/ffmpeg" -hide_banner -encoders 2>&1 | grep -E 'libsvtav1|libaom-av1'
"$native/magick" identify -list format | grep -E 'JXL.*rw'
# ImageMagick resources are copied beside the executable. Delegates outside the six staged tools are unsupported.
if command -v brew >/dev/null; then
  cp -R "$(brew --prefix imagemagick)/etc/ImageMagick-7/." "$native/" || true
  brew list --versions > "$native/build-packages.txt"
else
  cp -R "$repo/artifacts/im/etc/ImageMagick-7/." "$native/"
  dpkg-query -W > "$native/build-packages.txt"
fi
bash "$repo/scripts/stage-vlc-unix.sh" "$rid"
