#!/usr/bin/env bash
#
# Packages the Remote Desktop servers (signaling, relay and the monitoring console) into a macOS .pkg installer.
#
# The counterpart of build/installer/windows/server/Build-ServerInstaller.ps1, and deliberately the same shape: the
# three programs go into one install directory, each with its own appsettings.json, and a setup step writes the
# signaling/relay settings at install time. That step is the server's own `--configure-system`, which is
# platform-neutral .NET, so both platforms produce identical settings and generate the relay key the same way. The
# console needs no setup step of its own: its database path and loopback address come from its launchd job.
#
# What differs is only what has to: there is no MSI property mechanism here, so the answers are baked into the package
# when it is built (as setup-options) and handed to the setup step by the postinstall; the two servers are launchd
# LaunchDaemons instead of Windows services; and there is no firewall step, because macOS has no per-port rule to add
# — its application firewall is off by default and, when on, is per-application. docs/ops/README.md says what to open.
#
# The .pkg is UNSIGNED by default; see the note in make-pkg.sh of the client for what that costs and what --identity
# and --notarize add.
#
# Usage:
#   build/installer/macos/server/make-pkg.sh [--configuration Release] [--output <dir>] [--skip-publish]
#                                            [--distribution] [--notarize]
#                                            [--rendezvous-url <http://0.0.0.0:5080>]
#                                            [--relay-endpoint <ip:port>]
#                                            [--relay-key <base64>]
#                                            [--database <auto|none|postgres|sqlite>] [--database-path <file>]
#                                            [--postgres <connection string>]
#                                            [--tls-certificate <file.pfx>] [--tls-password <password>]
#
# Every --rendezvous-url / --relay-endpoint / … is optional and means the same as leaving the corresponding setting
# out of the Windows installer: the setup step decides (the relay address falls back to this machine's first
# non-loopback address, the key is generated once and kept, signaling stays on plain http).
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

CONFIGURATION="Release"
OUTPUT_DIR="$REPO_ROOT/artifacts/installer/macos"
PUBLISH_DIR="$REPO_ROOT/artifacts/app/macos/server"
ARCH="arm64"
RID="osx-arm64"
INSTALL_LOCATION="/usr/local/remotedesktop-server"
BUNDLE_ID="com.remotedesktop.server"
SKIP_PUBLISH=0
DISTRIBUTION=0
NOTARIZE=0
PKG_IDENTITY=""

# The settings the package will be built with; empty means "let the setup step decide".
OPT_URLS=""
OPT_RELAY_ENDPOINT=""
OPT_RELAY_KEY=""
OPT_DATABASE=""
OPT_DATABASE_PATH=""
OPT_POSTGRES=""
OPT_TLS_CERTIFICATE=""
OPT_TLS_PASSWORD=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --configuration)   CONFIGURATION="$2"; shift 2 ;;
        --output)          OUTPUT_DIR="$2";    shift 2 ;;
        --publish)         PUBLISH_DIR="$2";   shift 2 ;;
        --arch)            ARCH="$2"; RID="osx-$2"; shift 2 ;;
        --identity)        PKG_IDENTITY="$2"; DISTRIBUTION=1; shift 2 ;;
        --distribution)    DISTRIBUTION=1;     shift ;;
        --notarize)        NOTARIZE=1;         shift ;;
        --skip-publish)    SKIP_PUBLISH=1;     shift ;;
        --rendezvous-url)  OPT_URLS="$2";            shift 2 ;;
        --relay-endpoint)  OPT_RELAY_ENDPOINT="$2";  shift 2 ;;
        --relay-key)       OPT_RELAY_KEY="$2";       shift 2 ;;
        --database)        OPT_DATABASE="$2";        shift 2 ;;
        --database-path)   OPT_DATABASE_PATH="$2";   shift 2 ;;
        --postgres)        OPT_POSTGRES="$2";        shift 2 ;;
        --tls-certificate) OPT_TLS_CERTIFICATE="$2"; shift 2 ;;
        --tls-password)    OPT_TLS_PASSWORD="$2";    shift 2 ;;
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

# --- 1. The two servers ----------------------------------------------------------------------------------------

if [[ "$SKIP_PUBLISH" != "1" ]]; then
    echo "==> Publishing the servers ($CONFIGURATION, $RID)"
    for project in RemoteDesktop.Server.Rendezvous RemoteDesktop.Server.Relay RemoteDesktop.Server.Ops; do
        dotnet publish "$REPO_ROOT/src/Server/$project/$project.csproj" \
            --configuration "$CONFIGURATION" \
            --runtime "$RID" \
            --self-contained true \
            --output "$PUBLISH_DIR/$project" \
            --nologo
    done
fi

for project in RemoteDesktop.Server.Rendezvous RemoteDesktop.Server.Relay RemoteDesktop.Server.Ops; do
    [[ -f "$PUBLISH_DIR/$project/$project" ]] || {
        echo "No executable at $PUBLISH_DIR/$project/$project — publish first, or drop --skip-publish." >&2
        exit 1
    }
done

# --- 2. Version ------------------------------------------------------------------------------------------------

# version.sh owns the read and its rules: a missing or malformed <Version> fails here rather than producing a
# 0.0.0 package.
. "$SCRIPT_DIR/../../version.sh"
VERSION="$(read_product_version "$REPO_ROOT")"

mkdir -p "$OUTPUT_DIR"
WORK_DIR="$(mktemp -d)"
RES_DIR="$WORK_DIR/resources"
STAGE_DIR="$WORK_DIR/root"
mkdir -p "$RES_DIR"
trap 'rm -rf "$WORK_DIR"' EXIT

# --- 3. The payload --------------------------------------------------------------------------------------------

# Laid out exactly as it will land: pkgbuild --root with --install-location / copies this tree to the filesystem as
# it is, which is what lets one package install both the programs under /usr/local and the launchd jobs under
# /Library/LaunchDaemons. The directory names inside the install location match what the launchd plists point at, so
# the two cannot disagree.
SERVER_DIR="$STAGE_DIR$INSTALL_LOCATION"
mkdir -p "$SERVER_DIR/Rendezvous" "$SERVER_DIR/Relay" "$SERVER_DIR/Ops" "$SERVER_DIR/logs" "$STAGE_DIR/Library/LaunchDaemons"

echo "==> Assembling the payload"
cp -R "$PUBLISH_DIR/RemoteDesktop.Server.Rendezvous/." "$SERVER_DIR/Rendezvous/"
cp -R "$PUBLISH_DIR/RemoteDesktop.Server.Relay/." "$SERVER_DIR/Relay/"
cp -R "$PUBLISH_DIR/RemoteDesktop.Server.Ops/." "$SERVER_DIR/Ops/"

# Symbols belong to the machine that built the package, not the one that installs it. The MSIs already strip them.
find "$SERVER_DIR" -type f -name '*.pdb' -delete

# The licence and the notices travel with the binaries they cover; the installer's licence page alone is not a copy
# that survives the installer.
DOC_DIR="$STAGE_DIR/usr/local/share/doc/remotedesktop-server"
mkdir -p "$DOC_DIR"
cp "$REPO_ROOT/LICENSE" "$DOC_DIR/copyright"
cp "$REPO_ROOT/THIRD-PARTY-NOTICES.md" "$DOC_DIR/third-party-notices.md"

# The shipped settings are a template, not the live file. A .pkg has no conffile concept: a payload file with the same
# path is overwritten on upgrade, which would put the build's defaults back over what the operator configured — and,
# for the signaling server, throw away the relay key every client and relay has been given. The live appsettings.json
# is created by the postinstall only if it is missing; this template is what it copies on a first install and what an
# upgrade refreshes for the next fresh install.
for server in Rendezvous Relay Ops; do
    if [ -f "$SERVER_DIR/$server/appsettings.json" ]; then
        mv "$SERVER_DIR/$server/appsettings.json" "$SERVER_DIR/$server/appsettings.default.json"
    fi
done

cp "$REPO_ROOT/build/service/macos/com.remotedesktop.rendezvous.plist" "$REPO_ROOT/build/service/macos/com.remotedesktop.relay.plist" "$REPO_ROOT/build/service/macos/com.remotedesktop.ops.plist" "$STAGE_DIR/Library/LaunchDaemons/"
cp "$SCRIPT_DIR/configure-server.sh" "$SCRIPT_DIR/uninstall-server.sh" "$SCRIPT_DIR/how-to-configure.txt" "$SERVER_DIR/"
chmod +x "$SERVER_DIR/configure-server.sh" "$SERVER_DIR/uninstall-server.sh"

# The answers this package was built with, read by the postinstall and handed to the server's --configure-system step.
# "name=value" per line, split on the first =, so a Postgres connection string is fine; the postinstall deletes it
# afterwards because it may hold a certificate password.
options_file="$SERVER_DIR/setup-options"
{
    echo "# Written by make-pkg.sh. Read once by the postinstall and then removed; edit nothing here."
    echo "# Each line is one option of: RemoteDesktop.Server.Rendezvous --configure-system"
    [[ -n "$OPT_URLS" ]]            && printf -- '--urls=%s\n'            "$OPT_URLS"
    [[ -n "$OPT_RELAY_ENDPOINT" ]]  && printf -- '--relay-endpoint=%s\n'  "$OPT_RELAY_ENDPOINT"
    [[ -n "$OPT_RELAY_KEY" ]]       && printf -- '--relay-key=%s\n'       "$OPT_RELAY_KEY"

    # Always passed, never left out: a package with no database answer gets "auto", which keeps the PostgreSQL this
    # machine already has and otherwise starts a SQLite database. Leaving it out would mean "change nothing", and on a
    # fresh installation there is nothing to keep — device codes would live in memory.
    printf -- '--database=%s\n' "${OPT_DATABASE:-auto}"
    [[ -n "$OPT_DATABASE_PATH" ]]   && printf -- '--database-path=%s\n'   "$OPT_DATABASE_PATH"
    [[ -n "$OPT_POSTGRES" ]]        && printf -- '--postgres=%s\n'        "$OPT_POSTGRES"
    [[ -n "$OPT_TLS_CERTIFICATE" ]] && printf -- '--tls-certificate=%s\n' "$OPT_TLS_CERTIFICATE"
    [[ -n "$OPT_TLS_PASSWORD" ]]    && printf -- '--tls-password=%s\n'    "$OPT_TLS_PASSWORD"
    true
} > "$options_file"

# --- 4. Distribution metadata (welcome, license, conclusion) ---------------------------------------------------

cat > "$RES_DIR/welcome.html" <<'HTML'
<html><body style="font-family: -apple-system, 'Helvetica Neue', sans-serif; font-size: 13px;">
<h2>Remote Desktop Server</h2>
<p>This installs the three server programs this product needs into <b>/usr/local/remotedesktop-server</b> and registers
them with <b>launchd</b> so they start at boot and restart if they crash:</p>
<ul>
  <li><b>Rendezvous</b> — signaling: device codes, presence, and introductions.</li>
  <li><b>Relay</b> — carries the traffic when two machines cannot reach each other directly.</li>
  <li><b>Ops</b> — the monitoring console: nodes, sessions, alerts and audit, on <b>127.0.0.1:5090</b>.</li>
</ul>
<p>The two network servers listen on <b>TCP 5080</b> (signaling), <b>UDP 5081</b> (address reflection) and
<b>UDP 5082</b> (relay). macOS does not let a package open ports, so open them yourself if other machines have to reach
this one. The console stays on loopback; exposing it is a reverse-proxy decision.</p>
</body></html>
HTML

cat > "$RES_DIR/conclusion.html" <<'HTML'
<html><body style="font-family: -apple-system, 'Helvetica Neue', sans-serif; font-size: 13px;">
<h2>Installed</h2>
<p>The signaling server and the relay are running. Check them with:</p>
<p style="font-family: ui-monospace, Menlo, monospace;">curl http://127.0.0.1:5080/health</p>
<p>The monitoring console is running on <b>http://127.0.0.1:5090</b>, but it has no account yet and no default
password. Create the first administrator in a terminal (it asks for a password and prints a TOTP secret once):</p>
<p style="font-family: ui-monospace, Menlo, monospace;">sudo /usr/local/remotedesktop-server/Ops/RemoteDesktop.Server.Ops --create-admin admin<br>
sudo launchctl kickstart -k system/com.remotedesktop.ops</p>
<p>Two defaults are worth knowing, because their consequences are the worst ones:</p>
<ul>
  <li>Device codes are kept in a <b>SQLite database</b> under <b>var/</b>, so they survive a restart and an unattended
      machine keeps its code. To use a PostgreSQL server instead, build the package with <b>--postgres</b>; to keep
      nothing at all, <b>--database none</b> — and then every restart gives every device a new code.</li>
  <li>Without a certificate, signaling is <b>plain http</b>: device codes, who is connecting to whom, and the relay
      tickets are all readable on the way.</li>
</ul>
<p>Both are set with <b>configure-server.sh</b> in the install directory; <b>how-to-configure.txt</b> next to it explains
them, and logs are in <b>logs/</b>.</p>
</body></html>
HTML

cat > "$WORK_DIR/distribution.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="2">
    <title>Remote Desktop Server</title>
    <organization>com.remotedesktop</organization>
    <domains enable_anywhere="false" enable_currentUserHome="false" enable_localSystem="true"/>
    <options customize="never" rootVolumeOnly="true" hostArchitectures="$ARCH"/>
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

# The product's own licence text — the repository's LICENSE, the same document the Windows packages show. It is copied
# into the package resources under the name the installer-gui-script above refers to.
cp "$REPO_ROOT/LICENSE" "$RES_DIR/License.txt"

# --- 5. The component package ----------------------------------------------------------------------------------

# --root with --install-location / because the payload is already laid out from the filesystem root (it installs
# under /usr/local and under /Library). --ownership recommended sets root:wheel, which launchd insists on for a
# LaunchDaemon plist.
echo "==> Building the component package"
pkgbuild \
    --root "$STAGE_DIR" \
    --install-location / \
    --identifier "$BUNDLE_ID" \
    --version "$VERSION" \
    --ownership recommended \
    --scripts "$SCRIPT_DIR/scripts" \
    "$WORK_DIR/component.pkg"

# --- 6. The product package ------------------------------------------------------------------------------------

PKG_NAME="RemoteDesktop-Server-macOS-$ARCH-$VERSION.pkg"
FINAL_PKG="$OUTPUT_DIR/$PKG_NAME"

BUILD_ARGS=(--distribution "$WORK_DIR/distribution.xml" --resources "$RES_DIR" --package-path "$WORK_DIR")

if [[ "$DISTRIBUTION" == "1" ]]; then
    [[ -n "$PKG_IDENTITY" ]] || PKG_IDENTITY="$(security find-identity -v -p codesigning 2>/dev/null \
        | sed -n 's/.*"\(Developer ID Installer: [^"]*\)".*/\1/p' | sort | sed -n '1p')"
    if [[ -z "$PKG_IDENTITY" ]]; then
        echo "No 'Developer ID Installer' certificate found. Install one, or pass --identity." >&2
        exit 1
    fi
    echo "==> Signing with: $PKG_IDENTITY"
    BUILD_ARGS+=(--sign "$PKG_IDENTITY")
else
    cat >&2 <<'WARNING'

    ###########################################################################################
    # Building an UNSIGNED .pkg.                                                              #
    #                                                                                         #
    # It installs, but on any other Mac Gatekeeper reports "unidentified developer" and the   #
    # user must right-click → Open. Pass --distribution (or --identity "Developer ID          #
    # Installer: …") to sign, and --notarize to have Apple notarize it as well.               #
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
    echo "==> Stapling the notarization ticket"
    xcrun stapler staple "$FINAL_PKG"
fi

# --- 8. Verify -------------------------------------------------------------------------------------------------

echo "==> Verifying"
pkgutil --check-signature "$FINAL_PKG" 2>&1 | sed 's/^/    /' || true
if [[ "$DISTRIBUTION" == "1" ]]; then
    spctl -a -vvv -t install "$FINAL_PKG" 2>&1 | sed 's/^/    /' || true
fi

# The payload is what actually has to be right here: two executables where the launchd plists say they are, and the
# plists themselves. Printing both makes a wrong layout visible now rather than as a job that will not start.
echo "    payload:"
pkgutil --payload-files "$FINAL_PKG" | grep -E 'Rendezvous/RemoteDesktop|Relay/RemoteDesktop|Ops/RemoteDesktop|LaunchDaemons/|configure-server|uninstall-server|how-to-configure' | sed 's/^/      /'
echo "      … ($(pkgutil --payload-files "$FINAL_PKG" | wc -l | tr -d ' ') entries in total)"

echo
echo "Built: $FINAL_PKG"
echo "Install on this machine:  sudo installer -pkg \"$FINAL_PKG\" -target /"
