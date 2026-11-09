#!/usr/bin/env bash
#
# Packages "Remote Desktop.app" into a macOS .pkg installer.
#
# Why a .pkg and not a .dmg: the application's login item has to live in /Applications (SMAppService.mainAppService
# is keyed to the installed bundle — see src/Core/RemoteDesktop.Abstractions/Platform/ILoginItem.cs). The unattended
# host-only role is a separate package, build/installer/macos/host/make-pkg.sh. A .pkg installs straight into /Applications with the
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
        --arch)
            ARCH="$2"
            # The app bundle is built for osx-arm64 (make-app.sh). A package labelled with any other architecture
            # would refuse to install on the machine it actually runs on, so say so rather than lie in the metadata.
            if [[ "$ARCH" != "arm64" ]]; then
                echo "This package is arm64 only; --arch $ARCH is not supported." >&2
                exit 2
            fi
            shift 2 ;;
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

# A Developer ID Installer certificate is not a code-signing identity: it has no Code Signing EKU, so
# `find-identity -p codesigning` never lists it. Using the code-signing filter here made --distribution fail on every
# machine that actually had the certificate. Apple DTS: "If you run security find-identity and apply the code signing
# filter, you'll only see the first type."
find_installer_identity() {
    security find-identity -v 2>/dev/null \
        | sed -n "s/.*\"\\($1: [^\"]*\\)\".*/\\1/p" | sort | sed -n '1p'
}

if [[ "$DISTRIBUTION" == "1" ]]; then
    [[ -n "$APP_IDENTITY" ]] || APP_IDENTITY="$(find_identity "Developer ID Application")"
    [[ -n "$PKG_IDENTITY" ]] || PKG_IDENTITY="$(find_installer_identity "Developer ID Installer")"

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

# --- 3. The installer pane -------------------------------------------------------------------------------------

# A .pkg has no MSI property mechanism. The pane collects the signaling address and, for a self-signed server, its
# certificate at install time; the package's preinstall moves the answers to a root-owned file before the payload is
# unpacked, and the postinstall applies them. Same source for all three roles, different RDRole.
PLUGIN_DIR="$WORK_DIR/plugins"
echo "==> Building the installer pane"
"$REPO_ROOT/native/macos/build-installer-plugin.sh" --role client --output "$PLUGIN_DIR" --version "$VERSION"
if [[ "$DISTRIBUTION" == "1" ]]; then
    codesign --force --options runtime --timestamp --sign "$APP_IDENTITY" \
        "$PLUGIN_DIR/RemoteDesktopSetup.bundle"
else
    codesign --force --sign - --timestamp=none "$PLUGIN_DIR/RemoteDesktopSetup.bundle"
fi

# --- 4. The component package ----------------------------------------------------------------------------------

# --ownership recommended sets the standard system ownership (root:wheel) rather than inheriting whatever the build
# directory happened to have. --install-location is where the payload lands: /Applications, because that is where a
# signed bundle has to be for the login item to work.
#
# The bundle must also be marked non-relocatable. By default Installer looks for an existing copy of the same bundle
# identifier anywhere on the machine — LaunchServices knows about the build output this package was just made from —
# and "upgrades" that copy instead of writing to /Applications. Verified on macOS 26: the install succeeded and
# /Applications stayed empty while the staged app under artifacts/ was touched. Relocation is right for a library,
# never for an application whose path is also the path in the documentation and the login item.
#
# A component plist is accepted only with --root, so the bundle is staged with ditto (which keeps the extended
# attributes carrying the per-file signatures; a staging copy that drops them yields an app codesign calls unsigned).
echo "==> Building the component package"
APP_STAGE="$WORK_DIR/app-root"
mkdir -p "$APP_STAGE"
ditto "$APP_DIR" "$APP_STAGE/$(basename "$APP_DIR")"
component_plist="$WORK_DIR/component.plist"
pkgbuild --analyze --root "$APP_STAGE" "$component_plist" >/dev/null
app_relative="$(basename "$APP_DIR")"
found=0
index=0
while relative="$(plutil -extract "$index.RootRelativeBundlePath" raw "$component_plist" 2>/dev/null)"; do
    if [[ "$relative" == "$app_relative" ]]; then
        /usr/libexec/PlistBuddy -c "Set :$index:BundleIsRelocatable false" "$component_plist"
        found=1
    fi
    index=$((index + 1))
done
if [[ "$found" != "1" ]]; then
    echo "Could not analyze $APP_STAGE: pkgbuild did not list $app_relative as a bundle." >&2
    exit 1
fi

pkgbuild \
    --root "$APP_STAGE" \
    --component-plist "$component_plist" \
    --install-location "$INSTALL_LOCATION" \
    --identifier "$BUNDLE_ID" \
    --version "$VERSION" \
    --ownership recommended \
    --scripts "$SCRIPT_DIR/scripts" \
    "$WORK_DIR/component.pkg"

# --- 5. Distribution metadata (welcome + conclusion) -----------------------------------------------------------

# The welcome and conclusion screens are the only place a first-time user is told what the two permission prompts
# they are about to see actually mean. Without them the app looks broken ("why does it want to record my screen?").
if [[ "$DISTRIBUTION" == "1" ]]; then
    SIGNED_NOTE=""
else
    SIGNED_NOTE='<p style="color:#888;">This package is not signed with an Apple Developer ID, so macOS may warn that it is from an unidentified developer. If it refuses to open, right-click the .pkg and choose <b>Open</b>.</p>'
fi

cat > "$RES_DIR/welcome.html" <<HTML
<html><head><meta http-equiv="Content-Type" content="text/html; charset=utf-8"></head><body style="font-family: -apple-system, 'Helvetica Neue', sans-serif; font-size: 13px;">
<h2>Remote Desktop</h2>
<p>This installs <b>Remote Desktop</b> into <b>/Applications</b>.</p>
<p>Remote Desktop can act as a controller (connect out to another machine) and as a controlled host (let someone
else connect in). Both roles live in the same app. <b>Remote Desktop Host</b> may be installed alongside it: the
Host's background service then owns "being controlled", and this app becomes the controller. The two use separate
device identities, so both can stay connected.</p>
<p>The next page asks for the signaling server address. If the server uses <b>wss://</b> with a self-signed
certificate, have the public certificate file ready; Remote Desktop pins exactly that certificate and does not change
the system's trust settings. A server with a publicly trusted certificate, or a plain <b>ws://</b> one, needs no file.</p>
$SIGNED_NOTE
</body></html>
HTML

cat > "$RES_DIR/conclusion.html" <<HTML
<html><head><meta http-equiv="Content-Type" content="text/html; charset=utf-8"></head><body style="font-family: -apple-system, 'Helvetica Neue', sans-serif; font-size: 13px;">
<h2>Almost done</h2>
<p>To be <b>controlled</b> by another machine, Remote Desktop needs two permissions, which macOS only grants to you
manually. Open <b>System Settings → Privacy &amp; Security</b> and enable Remote Desktop under:</p>
<ul>
  <li><b>Screen &amp; System Audio Recording</b> — required for screen capture (and for system-audio loopback).</li>
  <li><b>Accessibility</b> — required for remote keyboard and mouse input.</li>
</ul>
<p>macOS will ask for these the first time they are used. After granting them you may need to relaunch the app.</p>
<p>If the server is on this machine's local network, macOS will also ask for <b>Local Network</b> access the first
time it connects; approve it in <b>System Settings → Privacy &amp; Security → Local Network</b>.</p>
<p>For unattended access, open the app and enable the login item in its settings.</p>
<p>The server address is in <b>/Library/Application Support/RemoteDesktop/appsettings.json</b>; the app's own
settings page writes the same key for the current user and takes precedence.</p>
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
    <options customize="never" require-scripts="true" rootVolumeOnly="true" hostArchitectures="$ARCH"/>
    <volume-check>
        <allowed-os-versions>
            <os-version min="26.0"/>
        </allowed-os-versions>
    </volume-check>
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

# --- 6. The product package ------------------------------------------------------------------------------------

# The role is in the file name, like the Linux packages and the release assets: three .pkg files land in one directory
# and "which one is the controller" should not depend on knowing that the one without a role word is it.
PKG_NAME="RemoteDesktop-Client-macOS-$ARCH-$VERSION.pkg"
FINAL_PKG="$OUTPUT_DIR/$PKG_NAME"

BUILD_ARGS=(--distribution "$WORK_DIR/distribution.xml" --resources "$RES_DIR" --package-path "$WORK_DIR"
    --plugins "$PLUGIN_DIR")

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

# --- 7. Notarization -------------------------------------------------------------------------------------------

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

# --- 8. Verify -------------------------------------------------------------------------------------------------

echo "==> Verifying"
# --check-signature exits non-zero for an unsigned package, which is expected when no certificate is configured, so
# it is not allowed to abort the script — the output is the point, not the status.
if [[ "$DISTRIBUTION" == "1" ]]; then
    pkgutil --check-signature "$FINAL_PKG" 2>&1 | sed 's/^/    /'
    # A signed package that Gatekeeper rejects is a failed signed build, not a warning.
    spctl -a -vvv -t install "$FINAL_PKG" 2>&1 | sed 's/^/    /'
else
    pkgutil --check-signature "$FINAL_PKG" 2>&1 | sed 's/^/    /' || true
fi
if [[ "$NOTARIZE" == "1" ]]; then
    xcrun stapler validate "$FINAL_PKG"
fi
echo "    payload:"
# Read the listing once. Piping it to `head` closed the pipe after eight lines and pkgutil died of SIGPIPE; with
# `set -o pipefail` that turned a successful build into exit 141, after the .pkg had already been written.
payload="$(pkgutil --payload-files "$FINAL_PKG")"
printf '%s\n' "$payload" | sed -n '1,8p' | sed 's/^/      /'
echo "      … ($(printf '%s\n' "$payload" | wc -l | tr -d ' ') entries)"

echo
echo "Built: $FINAL_PKG"
echo "Install on this machine:  sudo installer -pkg \"$FINAL_PKG\" -target /"
