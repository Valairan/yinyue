#!/bin/bash
# Generates mac/Assets from the shared masters in this folder.
#
#     ./Common/make-mac-assets.sh
#
# Same rule as the Windows icons: regenerate from Common/ rather than editing the outputs.
# Both apps' marks come from these masters so the two look alike by construction.
#
# Remember what Dark and Light name here: the colour of the MARK, not the theme it belongs
# to. Dark.png is the black 音樂 for light backgrounds, Light.png the white one for dark
# backgrounds. Getting it backwards makes the mark invisible rather than merely wrong.

set -euo pipefail
cd "$(dirname "$0")/.."

OUT=mac/Assets
mkdir -p "$OUT"

# --- Menu-bar mark -----------------------------------------------------------------
#
# ONE asset, not the two Windows needs. A macOS template image is a mask: AppKit reads only
# its alpha and draws it in whatever colour the menu bar requires, inverting automatically
# between light and dark. So there is no tray-light/tray-dark pair here and nothing has to
# watch for a theme change -- App.ApplyTrayIcon's whole job on Windows.
#
# Built from Dark.png because a template's colour is discarded and only its shape matters;
# the black mark is the one with clean alpha.
#
# 18pt is the conventional menu-bar height, with @2x for Retina.
sips -s format png -z 18 18 Common/Dark.png --out "$OUT/menubar.png"          >/dev/null
sips -s format png -z 36 36 Common/Dark.png --out "$OUT/menubar@2x.png"       >/dev/null

# --- Application icon --------------------------------------------------------------
#
# Shown in Finder and in Get Info. An LSUIElement app has no Dock icon, so this is not on
# screen during normal use -- but a bundle without one gets the generic blank page, which
# looks broken in a downloads folder.
ICONSET=$(mktemp -d)/Yinyue.iconset
mkdir -p "$ICONSET"

for size in 16 32 64 128 256 512; do
    sips -s format png -z $size $size Common/yinyue.png --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
done

# Retina variants are the next size up under the previous size's name.
for size in 16 32 128 256 512; do
    double=$((size * 2))
    sips -s format png -z $double $double Common/yinyue.png --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done

rm -f "$ICONSET/icon_64x64.png"   # not part of the iconset spec; only 16/32/128/256/512
iconutil -c icns "$ICONSET" -o "$OUT/Yinyue.icns"
rm -rf "$(dirname "$ICONSET")"

# --- Album-art fallback -------------------------------------------------------------
#
# Copied rather than regenerated: it is a finished asset shared with the Windows app, not
# a derivative of the logo masters.
cp win/Resources/placeholder.png "$OUT/placeholder.png"

echo "wrote $OUT/menubar.png, menubar@2x.png, Yinyue.icns, placeholder.png"
