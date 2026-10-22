#!/bin/sh
app_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
vlc_dir="$app_dir/runtimes/__RID__/native/vlc"
export VLC_PLUGIN_PATH="$vlc_dir/plugins"
if [ "$(uname -s)" = Linux ]; then
  # Prefer the host's compatible GUI/driver libraries. Never replace a newer GTK/GL
  # dependency with the Debian build used for the private VLC fallback libraries.
  host_libs=/lib/x86_64-linux-gnu:/usr/lib/x86_64-linux-gnu:/lib64:/usr/lib64:/lib:/usr/lib
  export LD_LIBRARY_PATH="$host_libs:$vlc_dir/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
fi
export DYLD_LIBRARY_PATH="$vlc_dir/lib${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}"
exec "$app_dir/PixSeek.bin" "$@"
