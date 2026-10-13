#!/usr/bin/env bash
#
# Packages RemoteDesktop.app into a macOS .pkg installer.
#
# Why a .pkg and not a .dmg: this application is meant to run as an unattended controlled-side host, which on macOS
# means it has to live in /Applications (SMAppService.mainAppService is keyed to the installed bundle — see
# src/Core/RemoteDesktop.Abstractions/Platform/ILoginItem.cs). A .pkg installs straight into /Applications with the
# right ownership; a .dmg makes the user drag it there themselves. It also gives us a place to explain the two TCC
# prompts (Screen Recording, Accessibility) that a first-time user will otherwise hit with no context.
#
# The .pkg is UNSIGNED by default. Without an Apple "Developer ID Installer" certificate there is nothing to sign it
# with, and the consequence is concrete: on someone else's Mac, Gatekeeper shows "cannot be opened because it is from
# an unidentified developer" and the user has to right-click → Open (or allow it in System Settings → Privacy &
# Security). That is a limitation of shipping without a certificate, not a bug in this script. When a certificate is
# available, pass it with --identity and the .pkg is signed — see the note at the bottom for what notarization adds.
#
# Usage:
#   build/installer/client/macos/make-pkg.sh [--configuration Release] [--output <dir>] [--skip-app-build]
#                                           [--identity "Developer ID Installer: ..."]
#
# Environment:
#   RD_SKIP_APP_BUILD=1   Reuse the existing .app instead of rebuilding it (same as --skip-app-build).
#   INSTALLER_IDENTITY    Signing identity; overrides nothing, only fills in when --identity is absent.
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

CONFIGURATION="Release"
OUTPUT_DIR="$REPO_ROOT/artifacts/pkg/macos"
APP_DIR="$REPO_ROOT/artifacts/app/macos/RemoteDesktop.app"
APP_NAME="RemoteDesktop.app"
BUNDLE_ID="com.remotedesktop.client"
INSTALL_LOCATION="/Applications"
ARCH="arm64"
SKIP_APP_BUILD="${RD_SKIP_APP_BUILD:-0}"
IDENTITY="${INSTALLER_IDENTITY:-}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --configuration)   CONFIGURATION="$2"; shift 2 ;;
        --output)          OUTPUT_DIR="$2";    shift 2 ;;
        --app)             APP_DIR="$2";       shift 2 ;;
        --arch)            ARCH="$2";          shift 2 ;;
        --identity)        IDENTITY="$2";      shift 2 ;;
        --skip-app-build)  SKIP_APP_BUILD=1;   shift ;;
        -h|--help)         sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed '$d'; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "This script only runs on macOS." >&2
    exit 1
fi

for tool in pkgbuild productbuild pkgutil; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "Missing required tool: $tool (part of the Xcode Command Line Tools)" >&2
        exit 1
    }
done

# --- 1. The .app -----------------------------------------------------------------------------------------------

if [[ "$SKIP_APP_BUILD" != "1" ]]; then
    echo "==> Building $APP_NAME ($CONFIGURATION)"
    "$SCRIPT_DIR/make-app.sh" --configuration "$CONFIGURATION" --output "$(dirname "$APP_DIR")"
fi

if [[ ! -d "$APP_DIR" ]]; then
    echo "No .app at $APP_DIR — run make-app.sh first, or drop --skip-app-build." >&2
    exit 1
fi

# --- 2. Version ------------------------------------------------------------------------------------------------

# The version in the package should be the version that was built, not a copy that drifts.
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$REPO_ROOT/Directory.Build.props" | head -1)"
VERSION="${VERSION:-0.0.0}"

mkdir -p "$OUTPUT_DIR"
WORK_DIR="$(mktemp -d)"
RES_DIR="$WORK_DIR/resources"
mkdir -p "$RES_DIR"
trap 'rm -rf "$WORK_DIR"' EXIT

# --- 3. The component package ----------------------------------------------------------------------------------

# --ownership recommended sets the standard system ownership (root:wheel) rather than inheriting whatever the build
# directory happened to have. --install-location is where the payload lands: /Applications, because that is where a
# signed bundle has to be for the login item to work.
echo "==> Building the component package"
pkgbuild \
    --component "$APP_DIR" \
    --install-location "$INSTALL_LOCATION" \
    --identifier "$BUNDLE_ID" \
    --version "$VERSION" \
    --ownership recommended \
    "$WORK_DIR/component.pkg"

# --- 4. Distribution metadata (welcome + conclusion) -----------------------------------------------------------

# The welcome and conclusion screens are the only place a first-time user is told what the two permission prompts
# they are about to see actually mean. Without them the app looks broken ("why does it want to record my screen?").
cat > "$RES_DIR/welcome.html" <<HTML
<html><body style="font-family: -apple-system, 'Helvetica Neue', sans-serif; font-size: 13px;">
<h2>Remote Desktop</h2>
<p>This installs <b>Remote Desktop</b> into <b>/Applications</b>.</p>
<p>Remote Desktop can act as a controller (connect out to another machine) and as a controlled host (let someone
else connect in). Both roles live in the same app.</p>
<p style="color:#888;">This package is not signed with an Apple Developer ID, so macOS may warn that it is from an
unidentified developer. If it refuses to open, right-click the .pkg and choose <b>Open</b>.</p>
</body></html>
HTML

cat > "$RES_DIR/conclusion.html" <<HTML
<html><body style="font-family: -apple-system, 'Helvetica Neue', sans-serif; font-size: 13px;">
<h2>Almost done</h2>
<p>To be <b>controlled</b> by another machine, Remote Desktop needs two permissions, which macOS only grants to you
manually. Open <b>System Settings → Privacy &amp; Security</b> and enable Remote Desktop under:</p>
<ul>
  <li><b>Screen &amp; System Audio Recording</b> — required for screen capture (and for system-audio loopback).</li>
  <li><b>Accessibility</b> — required for remote keyboard and mouse input.</li>
</ul>
<p>macOS will ask for these the first time they are used. After granting them you may need to relaunch the app.</p>
<p>For unattended access, open the app and enable the login item in its settings.</p>
</body></html>
HTML

cat > "$WORK_DIR/distribution.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="2">
    <title>Remote Desktop</title>
    <organization>com.remotedesktop</organization>
    <domains enable_anywhere="false" enable_currentUserHome="false" enable_localSystem="true"/>
    <options customize="never" require-scripts="false" rootVolumeOnly="true" hostArchitectures="$ARCH"/>
    <welcome file="welcome.html" mime-type="text/html"/>
    <conclusion file="conclusion.html" mime-type="text/html"/>
    <choices-outline>
        <line choice="default">
            <line choice="$BUNDLE_ID"/>
        </line>
    </choices-outline>
    <choice id="default"/>
    <choice id="$BUNDLE_ID" visible="false">
        <pkg-ref id="$BUNDLE_ID"/>
    </choice>
    <pkg-ref id="$BUNDLE_ID" version="$VERSION" onConclusion="none">component.pkg</pkg-ref>
</installer-gui-script>
XML

# --- 5. The product package ------------------------------------------------------------------------------------

PKG_NAME="RemoteDesktop-macOS-$ARCH-$VERSION.pkg"
FINAL_PKG="$OUTPUT_DIR/$PKG_NAME"

BUILD_ARGS=(--distribution "$WORK_DIR/distribution.xml" --resources "$RES_DIR" --package-path "$WORK_DIR")

if [[ -n "$IDENTITY" ]]; then
    echo "==> Signing with: $IDENTITY"
    BUILD_ARGS+=(--sign "$IDENTITY")
else
    cat >&2 <<'WARNING'

    ###########################################################################################
    # No --identity given: building an UNSIGNED .pkg.                                         #
    #                                                                                         #
    # It will install, but on any other Mac Gatekeeper reports "unidentified developer" and   #
    # the user must right-click → Open (or allow it in System Settings → Privacy & Security).  #
    # To sign, install an Apple "Developer ID Installer" certificate and pass                 #
    #     --identity "Developer ID Installer: Your Name (TEAMID)"                             #
    #                                                                                         #
    # Signing alone is not enough for a clean first-run on macOS 10.15+: the .pkg should also  #
    # be notarized (xcrun notarytool submit … && xcrun stapler staple …). That needs a paid    #
    # Apple Developer account and is deliberately out of scope here.                          #
    ###########################################################################################

WARNING
fi

echo "==> Building $PKG_NAME"
productbuild "${BUILD_ARGS[@]}" "$FINAL_PKG"

# --- 6. Verify -------------------------------------------------------------------------------------------------

echo "==> Verifying"
# --check-signature exits non-zero for an unsigned package, which is expected here, so it is not allowed to abort
# the script — the output is the point, not the status.
pkgutil --check-signature "$FINAL_PKG" 2>&1 | sed 's/^/    /' || true
echo "    payload:"
pkgutil --payload-files "$FINAL_PKG" | head -8 | sed 's/^/      /'
echo "      … ($(pkgutil --payload-files "$FINAL_PKG" | wc -l | tr -d ' ') entries)"

echo
echo "Built: $FINAL_PKG"
echo "Install on this machine:  sudo installer -pkg \"$FINAL_PKG\" -target /"
