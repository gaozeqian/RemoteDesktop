#!/usr/bin/env bash
#
# Builds AppIcon.icns for the .app bundle out of the client's PNG icons.
#
# Why generate it rather than commit one: the PNGs in src/Hosts/RemoteDesktop.Client/Assets are already the single
# source of truth (the Windows .exe icon and the window icons come from the same set), and a checked-in .icns would
# be a second copy that goes stale the first time somebody changes the artwork.
#
# Why it matters at all: macOS finds a bundle's icon through Info.plist's CFBundleIconFile, which names an .icns in
# Contents/Resources. Without one, Finder and the Dock show the generic application icon — and that is the only place
# an app icon can come from on macOS, because a macOS window has no title-bar icon for Window.Icon to fill.
#
# Usage:
#   build/installer/client/macos/make-icns.sh [--assets <dir>] [--output <file.icns>]
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"

ASSETS_DIR="$REPO_ROOT/src/Hosts/RemoteDesktop.Client/Assets"
OUTPUT="$REPO_ROOT/artifacts/AppIcon.icns"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --assets) ASSETS_DIR="$2"; shift 2 ;;
        --output) OUTPUT="$2";     shift 2 ;;
        -h|--help) sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed '$d'; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

for tool in iconutil sips; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "Missing required tool: $tool (part of the Xcode Command Line Tools)" >&2
        exit 1
    }
done

# iconutil wants a directory named *.iconset holding these exact file names. The mapping reuses one source PNG for
# several slots where the artwork does not need its own file (16@2x and 32 are both 32px, and so on).
mapping=(
    "icon_16x16.png:icon-16.png"
    "icon_16x16@2x.png:icon-32.png"
    "icon_32x32.png:icon-32.png"
    "icon_32x32@2x.png:icon-64.png"
    "icon_128x128.png:icon-128.png"
    "icon_128x128@2x.png:icon-256.png"
    "icon_256x256.png:icon-256.png"
    "icon_256x256@2x.png:icon-512.png"
    "icon_512x512.png:icon-512.png"
)

work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT
iconset="$work_dir/AppIcon.iconset"
mkdir -p "$iconset"

for pair in "${mapping[@]}"; do
    dst="${pair%%:*}"; src="${pair##*:}"
    if [[ ! -f "$ASSETS_DIR/$src" ]]; then
        echo "Missing source icon: $ASSETS_DIR/$src" >&2
        exit 1
    fi
    cp "$ASSETS_DIR/$src" "$iconset/$dst"
done

# The 512@2x slot wants 1024px and the artwork stops at 512, so this one is an upscale. It is the one soft entry in
# the set; if a 1024 source is ever added to Assets, point this at it instead.
sips -z 1024 1024 "$ASSETS_DIR/icon-512.png" --out "$iconset/icon_512x512@2x.png" >/dev/null

mkdir -p "$(dirname "$OUTPUT")"
iconutil -c icns "$iconset" -o "$OUTPUT"

echo "Built $OUTPUT"
