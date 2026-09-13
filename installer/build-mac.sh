#!/bin/bash
#
# Publishes Yinyue for macOS and packages it into a DMG — the counterpart to build.ps1.
#
#     ./installer/build-mac.sh                 # both: -arm64.dmg and -x64.dmg
#     ./installer/build-mac.sh 0.3.0
#     ./installer/build-mac.sh 0.2.0 universal  # one bundle carrying both architectures
#
# Per architecture by default. A universal bundle is one download that runs everywhere, and
# it is the sum of both: 85 MB against 43 each. Since almost nobody needs both slices on one
# machine, shipping them apart halves what anyone actually downloads — it does not reduce
# what the repository or the host carries, which is both files either way.
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
FLAVOUR="${2:-separate}"          # separate | universal
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

# One publish either way: the project declares both RuntimeIdentifiers, so this builds each
# architecture into its own folder and lipos them into a universal bundle beside them. The
# per-architecture bundles are what `separate` ships; the universal one is what `universal`
# ships. Passing -r here would build only one and quietly undo that.
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

BASE="mac/Yinyue.Mac/bin/$CONFIG/net8.0-macos"

case "$FLAVOUR" in
    universal) TARGETS="Yinyue.app:" ;;
    separate)  TARGETS="osx-arm64/Yinyue.app:-arm64 osx-x64/Yinyue.app:-x64" ;;
    *) echo "unknown flavour '$FLAVOUR' — use separate or universal" >&2; exit 1 ;;
esac

for target in $TARGETS; do
    APP="$BASE/${target%%:*}"
    SUFFIX="${target##*:}"
    DMG="$OUT/Yinyue-$VERSION$SUFFIX.dmg"

    if [ ! -d "$APP" ]; then
        echo "publish produced no bundle at $APP" >&2
        exit 1
    fi

    ARCHS=$(lipo -archs "$APP/Contents/MacOS/Yinyue")

    # Worth failing on: a bundle with the wrong slices looks identical from the outside and
    # only shows up as "damaged" on the machines that cannot run it — which are, by
    # definition, not the machine that built it.
    case "$FLAVOUR:$SUFFIX:$ARCHS" in
        universal:*:*arm64*x86_64*|universal:*:*x86_64*arm64*) ;;
        separate:-arm64:arm64) ;;
        separate:-x64:x86_64) ;;
        *) echo "wrong architectures for $SUFFIX: $ARCHS" >&2; exit 1 ;;
    esac

    # --- Version ----------------------------------------------------------------------
    #
    # Stamped into the built bundle rather than passed to the build. Info.plist is
    # hand-written here -- it has to be, because LSUIElement is not something the SDK
    # properties can express -- and a hand-written plist wins over
    # ApplicationDisplayVersion, so the bundle reported 1.0.0 while the DMG said 0.2.0.
    /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" \
        "$APP/Contents/Info.plist"

    # --- Signing ----------------------------------------------------------------------
    #
    # No entitlements: nothing is sandboxed, the Keychain item is the app's own, and the
    # global hotkeys deliberately avoid anything requiring Accessibility.
    if [ -n "${DEVELOPER_ID:-}" ]; then
        codesign --force --deep --options runtime --timestamp --sign "$DEVELOPER_ID" "$APP"
    else
        codesign --force --deep --sign - "$APP"
    fi
    codesign --verify --deep --strict "$APP"

    # --- DMG --------------------------------------------------------------------------
    #
    # With an Applications symlink beside the app, which is the whole of the install gesture.
    STAGE_DMG="$STAGE/dmg$SUFFIX"
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
            xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
            xcrun stapler staple "$DMG"
        fi
    fi

    echo "  $(basename "$DMG")  $ARCHS  $(du -h "$DMG" | cut -f1 | tr -d ' ')  $(shasum -a 256 "$DMG" | cut -d' ' -f1)"
done

if [ -z "${DEVELOPER_ID:-}" ]; then
    echo
    echo "ad-hoc signed: Gatekeeper will need right-click → Open on a downloaded copy."
fi
