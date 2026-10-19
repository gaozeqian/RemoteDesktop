#!/usr/bin/env bash
#
# Builds the host-only macOS application bundle: "Remote Desktop Host.app".
#
# This is the headless side of the macOS product. It is a bundle rather than a bare executable because macOS ties the
# screen-recording and accessibility grants to a signed app in the user's graphical session; a LaunchDaemon cannot
# capture the screen, and a loose binary has no stable identity to remember a grant by. Inside the bundle is the same
# RemoteDesktop.HostService that runs on Linux — no window, no controller UI, no Avalonia.
#
# The bundle identifier is com.remotedesktop.host, deliberately different from the client's. The two apps do different
# jobs and TCC has to be able to grant them separately.
#
# Usage:
#   build/installer/macos/host/make-app.sh [--configuration Release] [--output <dir>] [--skip-build]
#                                          [--distribution] [--identity "Developer ID Application: …"]
#
# Environment:
#   APPLE_DEVELOPMENT_IDENTITY  Signing identity. Without one the bundle is ad-hoc signed, and TCC grants will not
#                               survive a rebuild — see the warning the script prints.
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

CONFIGURATION="Release"
OUTPUT_DIR="$REPO_ROOT/artifacts/app/macos/host"
APP_NAME="Remote Desktop Host.app"
PUBLISH_DIR="$REPO_ROOT/artifacts/publish/RemoteDesktop.HostService/release_osx-arm64"
SKIP_BUILD=0
DISTRIBUTION=0
IDENTITY="${APPLE_DEVELOPMENT_IDENTITY:-}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --configuration) CONFIGURATION="$2"; shift 2 ;;
        --output)        OUTPUT_DIR="$2";    shift 2 ;;
        --skip-build)    SKIP_BUILD=1;       shift ;;
        --distribution)  DISTRIBUTION=1;     shift ;;
        --identity)      IDENTITY="$2"; DISTRIBUTION=1; shift 2 ;;
        -h|--help)       sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed '$d'; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "This script only runs on macOS." >&2
    exit 1
fi

APP_DIR="$OUTPUT_DIR/$APP_NAME"
CONTENTS_DIR="$APP_DIR/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"

# version.sh owns the read and its rules, so a missing or malformed <Version> fails here instead of producing a
# 0.0.0 bundle.
. "$SCRIPT_DIR/../../version.sh"
VERSION="$(read_product_version "$REPO_ROOT")"

if [[ "$SKIP_BUILD" != "1" ]]; then
    echo "==> Publishing RemoteDesktop.HostService ($CONFIGURATION, osx-arm64)"
    rm -rf "$PUBLISH_DIR"
    dotnet publish "$REPO_ROOT/src/Hosts/RemoteDesktop.HostService/RemoteDesktop.HostService.csproj" \
        --configuration "$CONFIGURATION" \
        --runtime osx-arm64 \
        --self-contained true \
        --output "$PUBLISH_DIR" \
        --nologo -v quiet
fi

[[ -x "$PUBLISH_DIR/RemoteDesktop.HostService" ]] || {
    echo "No executable at $PUBLISH_DIR/RemoteDesktop.HostService — publish first, or drop --skip-build." >&2
    exit 1
}

echo "==> Assembling $APP_NAME"
if [[ -e "$APP_DIR" ]]; then
    rm -rf "$APP_DIR"
fi
mkdir -p "$MACOS_DIR" "$CONTENTS_DIR/Resources"
cp -R "$PUBLISH_DIR/." "$MACOS_DIR/"
sed "s/__VERSION__/$VERSION/g" "$SCRIPT_DIR/Info.plist" > "$CONTENTS_DIR/Info.plist"
chmod +x "$MACOS_DIR/RemoteDesktop.HostService"

# The same product icon as the client, generated from the client's artwork rather than kept as a second copy. The
# host runs as an agent (LSUIElement: no Dock icon, no menu bar), but Finder, /Applications and the installer still
# show the bundle, and the generic application icon there is the wrong first impression for a deliverable. The icon
# has to be in place before signing so it is sealed into the bundle.
if "$REPO_ROOT/build/installer/macos/client/make-icns.sh" --output "$CONTENTS_DIR/Resources/AppIcon.icns" >/dev/null; then
    echo "    icon: AppIcon.icns"
else
    echo "    WARNING: could not build AppIcon.icns — Finder will show the generic application icon." >&2
fi

# --- signing --------------------------------------------------------------------------------------------------

if [[ -z "$IDENTITY" ]]; then
    IDENTITY="$(security find-identity -v -p codesigning 2>/dev/null \
        | sed -n 's/.*"\(.*\)".*/\1/p' | sort | sed -n '1p')"
fi

if [[ "$DISTRIBUTION" == "1" ]]; then
    if [[ -z "$IDENTITY" || "$IDENTITY" != "Developer ID Application:"* ]]; then
        echo "Refusing to build a distribution bundle without a 'Developer ID Application:' identity." >&2
        echo "Found: ${IDENTITY:-none}" >&2
        exit 1
    fi
fi

if [[ "$DISTRIBUTION" == "1" ]]; then
    SIGN_FLAGS=(--options runtime --timestamp)
else
    SIGN_FLAGS=(--timestamp=none)
fi

SIGN_ARGS=(--force "${SIGN_FLAGS[@]}" --entitlements "$REPO_ROOT/build/installer/macos/client/entitlements.plist")
if [[ -n "$IDENTITY" ]]; then
    echo "    identity: $IDENTITY"
    SIGN_ARGS+=(--sign "$IDENTITY")
else
    cat >&2 <<'WARNING'

    ###########################################################################################
    # No code-signing identity found: falling back to an ad-hoc signature.                    #
    #                                                                                         #
    # The host will start, but macOS ties screen-recording and accessibility grants to the    #
    # signing identity. An ad-hoc signature changes on every build, so the grants will not    #
    # stick. Install an Apple Development certificate (or a Developer ID for distribution)    #
    # and rebuild before using this on a machine you care about.                              #
    ###########################################################################################

WARNING
    SIGN_ARGS+=(--sign -)
fi

# Nested code first, then the bundle. --deep is the only way to seal a flat .NET publish: codesign treats every file
# in Contents/MacOS as nested code, and a plain `codesign <app>` fails to seal it at all.
while IFS= read -r -d '' nested; do
    codesign --force "${SIGN_FLAGS[@]}" --sign "${IDENTITY:--}" "$nested"
done < <(find "$MACOS_DIR" -type f \( -name '*.dylib' -o -name '*.so' \) -print0)

codesign "${SIGN_ARGS[@]}" --deep "$APP_DIR"

echo "==> Verifying"
codesign --verify --deep --strict --verbose=1 "$APP_DIR" 2>&1 | sed 's/^/    /'
/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$CONTENTS_DIR/Info.plist" | sed 's/^/    bundle id: /'
codesign -dv "$APP_DIR" 2>&1 | sed -n 's/^\(Authority\|TeamIdentifier\|Signature\|Identifier\)=/    \1=/p'

echo
echo "Built: $APP_DIR"
echo "Fixed password: \"$APP_DIR/Contents/MacOS/RemoteDesktop.HostService\" --set-fixed-password '<password>'"
