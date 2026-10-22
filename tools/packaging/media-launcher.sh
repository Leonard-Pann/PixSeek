#!/bin/sh
native_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
media_dir="$native_dir/media"
export MAGICK_HOME="$media_dir"
export MAGICK_CONFIGURE_PATH="$media_dir/etc/ImageMagick-7"
for module_dir in "$media_dir"/lib/ImageMagick-*/modules-*/coders; do
  [ -d "$module_dir" ] && export MAGICK_CODER_MODULE_PATH="$module_dir"
done
export MAGICK_FONT_PATH="$media_dir/share/fonts"
export FONTCONFIG_PATH="$media_dir/etc/fonts"
export LIBHEIF_PLUGIN_PATH="$media_dir/lib/libheif"
export OPENSSL_MODULES="$media_dir/lib/ossl-modules"
export DYLD_LIBRARY_PATH="$media_dir/lib${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}"
export LD_LIBRARY_PATH="$media_dir/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec "$media_dir/bin/__TOOL__" "$@"
