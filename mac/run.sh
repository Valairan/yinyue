#!/bin/bash
# Build and run the macOS app — the counterpart to the run-yinyue skill on Windows.
#
#   ./mac/run.sh              build, then run in the menu bar only
#   ./mac/run.sh show         build, then run and summon the overlay
#   ./mac/run.sh test         run the seam self-test (Keychain, audio, anchoring, icons, layout)
#   ./mac/run.sh check        report the service graph and exit
#   ./mac/run.sh stop         kill any running copy
#
# Two papercuts this exists to remove.
#
# The SDK is NOT the one on PATH. `dotnet` here is 7.0.309 from /usr/local/share/dotnet,
# which cannot target net8.0 at all, let alone net8.0-macos -- it fails with NETSDK1045 and
# NETSDK1139. The .NET 8 SDK and the macos workload are installed user-locally in ~/.dotnet.
#
# And the app has no single-instance guard yet, unlike the Windows build's mutex. A second
# copy gives you two menu-bar icons and two overlays fighting over the same anchor, so a
# stale process is killed before launching.

set -euo pipefail
cd "$(dirname "$0")/.."

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
APP=mac/Yinyue.Mac/bin/Debug/net8.0-macos/osx-arm64/Yinyue.app
BIN="$APP/Contents/MacOS/Yinyue"

if [ ! -x "$DOTNET" ]; then
    echo "No .NET 8 SDK at $DOTNET." >&2
    echo "Install it with:  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0" >&2
    echo "then:             \$HOME/.dotnet/dotnet workload install macos" >&2
    exit 1
fi

stop() {
    # By bundle path rather than by name, so this cannot match an unrelated process.
    pkill -f "$APP" 2>/dev/null || true
}

case "${1:-run}" in
    stop)
        stop
        echo "stopped"
        ;;
    test|check|show|run)
        "$DOTNET" build mac/Yinyue.Mac -v q --nologo
        stop

        case "${1:-run}" in
            test)  "$BIN" --selftest "${2:-all}" ;;
            check) "$BIN" --check ;;
            show)  echo "Running. Overlay summoned; Ctrl+C or ./mac/run.sh stop to quit."
                   "$BIN" --show ;;
            run)   echo "Running in the menu bar. Look for the 音樂 mark; Ctrl+C to quit."
                   "$BIN" ;;
        esac
        ;;
    *)
        echo "usage: ./mac/run.sh [run|show|test|check|stop]" >&2
        exit 2
        ;;
esac
