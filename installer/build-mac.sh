#!/bin/bash
#
# Publishes Yinyue for macOS and packages it into a DMG — the counterpart to build.ps1.
#
#     ./installer/build-mac.sh                 # builds website/downloads/Yinyue-0.2.0.dmg
#     ./installer/build-mac.sh 0.3.0
#
# A DMG rather than a .pkg, and that is the macOS convention rather than a shortcut: a
# menu-bar app is one bundle with no system-wide state, so there is nothing for an installer
# to do that dragging it to Applications does not. The Windows MSI exists because Windows has
# no equivalent gesture and because the wizard seeds a first-run configuration; macOS asks
# for none of that.
#
# Needs a full Xcode (the .NET macos workload builds against its SDK) and the .NET 8 SDK.
#
# SIGNING. This ad-hoc signs, which is enough to run on the machine that built it and not
# enough for anyone else: Gatekeeper will refuse a downloaded ad-hoc bundle until the user
# right-clicks and chooses Open. Proper distribution needs an Apple Developer Program
# membership — a Developer ID Application certificate to sign with, and notarisation so the
# quarantine check passes silently. Set DEVELOPER_ID and the script will use it; without it
# the DMG is still usable, just with that extra step.

set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${1:-0.2.0}"
CONFIG="${CONFIGURATION:-Release}"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"

PROJECT=mac/Yinyue.Mac/Yinyue.Mac.csproj
STAGE=installer/obj/mac
OUT=website/downloads
DMG="$OUT/Yinyue-$VERSION.dmg"

if [ ! -x "$DOTNET" ]; then
    echo "No .NET 8 SDK at $DOTNET — see mac/run.sh for how to install it." >&2
    exit 1
fi

if ! xcodebuild -version >/dev/null 2>&1; then
    echo "Needs a full Xcode, not just the Command Line Tools." >&2
    exit 1
fi

rm -rf "$STAGE"
mkdir -p "$STAGE" "$OUT"

# No -r: the project declares both RuntimeIdentifiers, so publish builds each architecture
# and lipos them into one universal bundle beside them. Passing -r here would pin it to a
# single architecture and quietly undo that.
#
# CreatePackage=false because the macOS SDK otherwise wraps the bundle in a .pkg and that is
# what lands in the output directory — a .pkg installer for an app whose entire installation
# is dragging it to Applications. The DMG is the convention for a menu-bar app; the Windows
# MSI exists because Windows has no drag gesture and because its wizard seeds a first-run
# configuration.
echo "publishing ${CONFIG} (arm64 + x64)…"
"$DOTNET" publish "$PROJECT" -c "$CONFIG" --nologo -v q \
    -p:CreatePackage=false \
    -p:ApplicationDisplayVersion="$VERSION" >/dev/null

# The universal bundle sits at the framework root; the per-architecture ones are beside it in
# osx-arm64/ and osx-x64/ and are inputs, not outputs.
APP="mac/Yinyue.Mac/bin/$CONFIG/net8.0-macos/Yinyue.app"
if [ ! -d "$APP" ]; then
    echo "publish produced no universal Yinyue.app at $APP" >&2
    exit 1
fi

ARCHS=$(lipo -archs "$APP/Contents/MacOS/Yinyue")
case "$ARCHS" in
    *arm64*x86_64*|*x86_64*arm64*) ;;
    *)
        # Worth failing on: a single-architecture bundle looks identical from the outside and
        # only shows up as "damaged" on the machines that cannot run it.
        echo "not universal — the bundle carries only: $ARCHS" >&2
        exit 1
        ;;
esac
echo "universal: $ARCHS"

# --- Version ------------------------------------------------------------------------
#
# Stamped into the built bundle rather than passed to the build. Info.plist is hand-written
# here -- it has to be, because LSUIElement is not something the SDK properties can express --
# and a hand-written plist wins over ApplicationDisplayVersion, so the bundle reported 1.0.0
# while the DMG beside it said 0.2.0.
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" \
    "$APP/Contents/Info.plist"

# --- Signing ------------------------------------------------------------------------
#
# Deep, and the entitlements-free default: the app sandboxes nothing and needs no special
# entitlements — the Keychain item is its own, and the global hotkeys deliberately avoid
# anything requiring Accessibility.
if [ -n "${DEVELOPER_ID:-}" ]; then
    echo "signing with ${DEVELOPER_ID}…"
    codesign --force --deep --options runtime --timestamp \
        --sign "$DEVELOPER_ID" "$APP"
else
    echo "signing ad-hoc (no DEVELOPER_ID set — Gatekeeper will need right-click → Open)"
    codesign --force --deep --sign - "$APP"
fi

codesign --verify --deep --strict "$APP"

# --- DMG ----------------------------------------------------------------------------
#
# With an Applications symlink beside the app, which is the whole of the install gesture.
STAGE_DMG="$STAGE/dmg"
rm -rf "$STAGE_DMG"
mkdir -p "$STAGE_DMG"

cp -R "$APP" "$STAGE_DMG/"
ln -s /Applications "$STAGE_DMG/Applications"

rm -f "$DMG"
hdiutil create -quiet -volname "Yinyue $VERSION" -srcfolder "$STAGE_DMG" \
    -ov -format UDZO "$DMG"

if [ -n "${DEVELOPER_ID:-}" ]; then
    codesign --force --sign "$DEVELOPER_ID" "$DMG"

    # Notarisation needs credentials stored once with:
    #   xcrun notarytool store-credentials yinyue --apple-id … --team-id … --password …
    if [ -n "${NOTARY_PROFILE:-}" ]; then
        echo "notarising…"
        xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
        xcrun stapler staple "$DMG"
    else
        echo "not notarised: set NOTARY_PROFILE to submit it."
    fi
fi

SIZE=$(du -h "$DMG" | cut -f1 | tr -d ' ')
echo
echo "wrote $DMG ($SIZE)"
echo "SHA-256: $(shasum -a 256 "$DMG" | cut -d' ' -f1)"
