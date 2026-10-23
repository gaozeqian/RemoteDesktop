#!/usr/bin/env bash
#
# Packages "Remote Desktop.app" into a macOS .pkg installer.
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
# Security). That is a limitation of shipping without a certificate, not a bug in this script.
#
# With certificates, --distribution signs the app (Developer ID Application, hardened runtime, secure timestamp) and
# the .pkg (Developer ID Installer). Add --notarize to also submit the .pkg to Apple and staple the ticket, which is
# what makes it open with no warning at all.
#
# Usage:
#   build/installer/macos/client/make-pkg.sh [--configuration Release] [--output <dir>] [--skip-app-build]
#                                            [--distribution] [--notarize]
#                                            [--app-identity "Developer ID Application: …"]
#                                            [--identity     "Developer ID Installer: …"]
#
# --distribution / --app-identity / --identity: sign for distribution. With --distribution the identities are found
#   in the keychain automatically; pass them explicitly when more than one is installed.
# --notarize: submit to Apple and staple. Needs credentials, from either of:
#     NOTARY_KEY_PATH  + NOTARY_KEY_ID + NOTARY_ISSUER_ID     (App Store Connect API key .p8 — preferred for CI)
#     NOTARY_APPLE_ID  + NOTARY_PASSWORD + NOTARY_TEAM_ID     (Apple ID + app-specific password)
#
# Environment:
#   RD_SKIP_APP_BUILD=1   Reuse the existing .app instead of rebuilding it (same as --skip-app-build).
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

CONFIGURATION="Release"
OUTPUT_DIR="$REPO_ROOT/artifacts/installer/macos"
APP_DIR="$REPO_ROOT/artifacts/app/macos/Remote Desktop.app"
APP_NAME="Remote Desktop.app"
BUNDLE_ID="com.remotedesktop.client"
INSTALL_LOCATION="/Applications"
ARCH="arm64"
SKIP_APP_BUILD="${RD_SKIP_APP_BUILD:-0}"
DISTRIBUTION=0
NOTARIZE=0
APP_IDENTITY=""
PKG_IDENTITY=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --configuration)   CONFIGURATION="$2"; shift 2 ;;
        --output)          OUTPUT_DIR="$2";    shift 2 ;;
        --app)             APP_DIR="$2";       shift 2 ;;
        --arch)            ARCH="$2";          shift 2 ;;
        --app-identity)    APP_IDENTITY="$2"; DISTRIBUTION=1; shift 2 ;;
        --identity)        PKG_IDENTITY="$2"; DISTRIBUTION=1; shift 2 ;;
        --distribution)    DISTRIBUTION=1;     shift ;;
        --notarize)        NOTARIZE=1;         shift ;;
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

# --- 0. Distribution signing identities ------------------------------------------------------------------------

# Sorted so the pick is at least reproducible; `security find-identity` enumerates in no particular order. With more
# than one of a kind installed this is a coin toss, which is why an explicit flag wins over the search.
find_identity() {
    security find-identity -v -p codesigning 2>/dev/null \
        | sed -n "s/.*\"\\($1: [^\"]*\\)\".*/\\1/p" | sort | sed -n '1p'
}

if [[ "$DISTRIBUTION" == "1" ]]; then
    [[ -n "$APP_IDENTITY" ]] || APP_IDENTITY="$(find_identity "Developer ID Application")"
    [[ -n "$PKG_IDENTITY" ]] || PKG_IDENTITY="$(find_identity "Developer ID Installer")"

    if [[ -z "$APP_IDENTITY" ]]; then
        echo "No 'Developer ID Application' certificate found. Install one, or pass --app-identity." >&2
        exit 1
    fi
    if [[ -z "$PKG_IDENTITY" ]]; then
        echo "No 'Developer ID Installer' certificate found. Install one, or pass --identity." >&2
        exit 1
    fi
    echo "==> Distribution signing"
    echo "    app:       $APP_IDENTITY"
    echo "    installer: $PKG_IDENTITY"
fi

# --- 1. The .app -----------------------------------------------------------------------------------------------

if [[ "$SKIP_APP_BUILD" != "1" ]]; then
    echo "==> Building $APP_NAME ($CONFIGURATION)"
    APP_ARGS=(--configuration "$CONFIGURATION" --output "$(dirname "$APP_DIR")")
    if [[ "$DISTRIBUTION" == "1" ]]; then
        # make-app.sh reads the identity from the environment; --distribution switches it to hardened runtime + a
        # secure timestamp, which notarization requires.
        APP_ARGS+=(--distribution)
        export APPLE_DEVELOPMENT_IDENTITY="$APP_IDENTITY"
    fi
    "$SCRIPT_DIR/make-app.sh" "${APP_ARGS[@]}"
fi

if [[ ! -d "$APP_DIR" ]]; then
    echo "No .app at $APP_DIR — run make-app.sh first, or drop --skip-app-build." >&2
    exit 1
fi

# --- 2. Version ------------------------------------------------------------------------------------------------

# The version in the package should be the version that was built, not a copy that drifts. version.sh owns both the
# read and its rules: a missing or malformed <Version> fails here rather than producing a 0.0.0 package.
. "$SCRIPT_DIR/../../version.sh"
VERSION="$(read_product_version "$REPO_ROOT")"

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
if [[ "$DISTRIBUTION" == "1" ]]; then
    SIGNED_NOTE=""
else
    SIGNED_NOTE='<p style="color:#888;">This package is not signed with an Apple Developer ID, so macOS may warn that it is from an unidentified developer. If it refuses to open, right-click the .pkg and choose <b>Open</b>.</p>'
fi

cat > "$RES_DIR/welcome.html" <<HTML
<html><body style="font-family: -apple-system, 'Helvetica Neue', sans-serif; font-size: 13px;">
<h2>Remote Desktop</h2>
<p>This installs <b>Remote Desktop</b> into <b>/Applications</b>.</p>
<p>Remote Desktop can act as a controller (connect out to another machine) and as a controlled host (let someone
else connect in). Both roles live in the same app.</p>
$SIGNED_NOTE
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

# The same licence text the Windows packages show and the other two macOS packages already carry. Without it a
# first-time install never displays the terms it is accepting.
cp "$REPO_ROOT/LICENSE" "$RES_DIR/License.txt"

cat > "$WORK_DIR/distribution.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="2">
    <title>Remote Desktop</title>
    <organization>com.remotedesktop</organization>
    <domains enable_anywhere="false" enable_currentUserHome="false" enable_localSystem="true"/>
    <options customize="never" require-scripts="false" rootVolumeOnly="true" hostArchitectures="$ARCH"/>
    <welcome file="welcome.html" mime-type="text/html"/>
    <conclusion file="conclusion.html" mime-type="text/html"/>
    <license file="License.txt"/>
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

# The role is in the file name, like the Linux packages and the release assets: three .pkg files land in one directory
# and "which one is the controller" should not depend on knowing that the one without a role word is it.
PKG_NAME="RemoteDesktop-Client-macOS-$ARCH-$VERSION.pkg"
FINAL_PKG="$OUTPUT_DIR/$PKG_NAME"

BUILD_ARGS=(--distribution "$WORK_DIR/distribution.xml" --resources "$RES_DIR" --package-path "$WORK_DIR")

if [[ "$DISTRIBUTION" == "1" ]]; then
    BUILD_ARGS+=(--sign "$PKG_IDENTITY")
else
    cat >&2 <<'WARNING'

    ###########################################################################################
    # Building an UNSIGNED .pkg.                                                              #
    #                                                                                         #
    # It will install, but on any other Mac Gatekeeper reports "unidentified developer" and   #
    # the user must right-click → Open (or allow it in System Settings → Privacy & Security).  #
    # To sign, install an Apple "Developer ID Installer" certificate and pass --distribution   #
    # (or --identity "Developer ID Installer: Your Name (TEAMID)").                            #
    #                                                                                         #
    # Signing alone is not enough for a clean first-run on macOS 10.15+: add --notarize, which #
    # submits the .pkg to Apple and staples the ticket. That needs a paid Apple Developer      #
    # account and notary credentials (see the header).                                        #
    ###########################################################################################

WARNING
fi

echo "==> Building $PKG_NAME"
productbuild "${BUILD_ARGS[@]}" "$FINAL_PKG"

# --- 6. Notarization -------------------------------------------------------------------------------------------

if [[ "$NOTARIZE" == "1" ]]; then
    if [[ "$DISTRIBUTION" != "1" ]]; then
        echo "Refusing to notarize an unsigned .pkg: notarization requires a Developer ID signature." >&2
        exit 1
    fi

    # Two credential shapes are accepted. The API key is preferred for CI: no Apple ID, no 2FA, no per-machine
    # app-specific password to rotate.
    NOTARY_CREDS=()
    if [[ -n "${NOTARY_KEY_PATH:-}" && -n "${NOTARY_KEY_ID:-}" && -n "${NOTARY_ISSUER_ID:-}" ]]; then
        NOTARY_CREDS=(--key "$NOTARY_KEY_PATH" --key-id "$NOTARY_KEY_ID" --issuer "$NOTARY_ISSUER_ID")
    elif [[ -n "${NOTARY_APPLE_ID:-}" && -n "${NOTARY_PASSWORD:-}" && -n "${NOTARY_TEAM_ID:-}" ]]; then
        NOTARY_CREDS=(--apple-id "$NOTARY_APPLE_ID" --password "$NOTARY_PASSWORD" --team-id "$NOTARY_TEAM_ID")
    else
        cat >&2 <<'ERR'
--notarize needs credentials. Set either
    NOTARY_KEY_PATH + NOTARY_KEY_ID + NOTARY_ISSUER_ID   (App Store Connect API key)
or  NOTARY_APPLE_ID + NOTARY_PASSWORD + NOTARY_TEAM_ID   (Apple ID + app-specific password)
ERR
        exit 1
    fi

    echo "==> Notarizing (this uploads to Apple and can take a few minutes)"
    xcrun notarytool submit "$FINAL_PKG" "${NOTARY_CREDS[@]}" --wait

    # Stapling attaches the ticket to the .pkg itself, so a machine that installs it later does not have to ask Apple
    # (and works offline).
    echo "==> Stapling the notarization ticket"
    xcrun stapler staple "$FINAL_PKG"
fi

# --- 7. Verify -------------------------------------------------------------------------------------------------

echo "==> Verifying"
# --check-signature exits non-zero for an unsigned package, which is expected when no certificate is configured, so
# it is not allowed to abort the script — the output is the point, not the status.
pkgutil --check-signature "$FINAL_PKG" 2>&1 | sed 's/^/    /' || true
if [[ "$DISTRIBUTION" == "1" ]]; then
    # The question that actually matters for a user: would Gatekeeper let this install? "accepted" is the answer we
    # want; "source=Notarized Developer ID" only appears once --notarize has run.
    spctl -a -vvv -t install "$FINAL_PKG" 2>&1 | sed 's/^/    /' || true
fi
echo "    payload:"
pkgutil --payload-files "$FINAL_PKG" | head -8 | sed 's/^/      /'
echo "      … ($(pkgutil --payload-files "$FINAL_PKG" | wc -l | tr -d ' ') entries)"

echo
echo "Built: $FINAL_PKG"
echo "Install on this machine:  sudo installer -pkg \"$FINAL_PKG\" -target /"
