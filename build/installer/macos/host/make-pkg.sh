#!/usr/bin/env bash
#
# Packages the host-only macOS application into a .pkg installer.
#
# This package installs "Remote Desktop Host.app" — a separate bundle from the client, with its own bundle identifier
# (com.remotedesktop.host), no window and no controller UI. It is the same HostService binary that runs on Linux,
# wrapped in a bundle because macOS grants screen recording and accessibility only to a signed app in a user's
# graphical session, and adds a LaunchAgent that starts it at every graphical login.
#
# It is not the client bundle under another name: the client still contains the controller, this one does not. The two
# can be installed side by side, but they would then be two hosts with two device identities and two listeners; a
# machine should normally have one or the other.
#
# The .pkg is UNSIGNED by default, exactly like the client's: without an Apple "Developer ID Installer" certificate
# Gatekeeper reports "unidentified developer" and the user has to right-click → Open. --distribution signs and
# --notarize submits to Apple, which needs the certificates and credentials described in build/signing/README.md.
#
# Usage:
#   build/installer/macos/host/make-pkg.sh [--configuration Release] [--output <dir>] [--skip-app-build]
#                                          [--distribution] [--notarize]
#                                          [--app-identity "Developer ID Application: …"]
#                                          [--identity     "Developer ID Installer: …"]
#
# Environment:
#   RD_SKIP_APP_BUILD=1   Reuse the existing .app instead of rebuilding it (same as --skip-app-build).
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

CONFIGURATION="Release"
OUTPUT_DIR="$REPO_ROOT/artifacts/installer/macos"
APP_DIR="$REPO_ROOT/artifacts/app/macos/host/Remote Desktop Host.app"
APP_NAME="Remote Desktop Host.app"
BUNDLE_ID="com.remotedesktop.host"
AGENT_LABEL="com.remotedesktop.host"
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

# The same bundle the client package installs, built by the same script: two ways of assembling it would be two things
# that could differ in exactly the part macOS keys permissions to.
if [[ "$SKIP_APP_BUILD" != "1" ]]; then
    echo "==> Building $APP_NAME ($CONFIGURATION)"
    APP_ARGS=(--configuration "$CONFIGURATION" --output "$(dirname "$APP_DIR")")
    if [[ "$DISTRIBUTION" == "1" ]]; then
        APP_ARGS+=(--distribution)
        [[ -n "$APP_IDENTITY" ]] && APP_ARGS+=(--identity "$APP_IDENTITY")
    fi
    "$SCRIPT_DIR/make-app.sh" "${APP_ARGS[@]}"
fi

if [[ ! -d "$APP_DIR" ]]; then
    echo "No .app at $APP_DIR — run $SCRIPT_DIR/make-app.sh first, or drop --skip-app-build." >&2
    exit 1
fi

# --- 2. Version ------------------------------------------------------------------------------------------------

# version.sh owns the read and its rules: a missing or malformed <Version> fails here rather than producing a
# 0.0.0 package.
. "$SCRIPT_DIR/../../version.sh"
VERSION="$(read_product_version "$REPO_ROOT")"

mkdir -p "$OUTPUT_DIR"
WORK_DIR="$(mktemp -d)"
RES_DIR="$WORK_DIR/resources"
AGENT_ROOT="$WORK_DIR/agent-root"
mkdir -p "$RES_DIR" "$AGENT_ROOT/Library/LaunchAgents"
trap 'rm -rf "$WORK_DIR"' EXIT

# --- 3. The two component packages -----------------------------------------------------------------------------

# The app goes in as a *component* package, not as part of a staged root: pkgbuild --component understands a bundle and
# keeps the extended attributes that carry the per-file signatures of the .NET assemblies inside it. A --root copy
# loses them (verified: codesign --deep then reports the app as unsigned), which is exactly the part macOS keys TCC
# permissions and notarization to.
echo "==> Building the application component"
pkgbuild \
    --component "$APP_DIR" \
    --install-location "/Applications" \
    --identifier "$BUNDLE_ID.app" \
    --version "$VERSION" \
    --ownership recommended \
    "$WORK_DIR/app.pkg"

echo "==> Building the login-item component"
cp "$REPO_ROOT/build/service/macos/$AGENT_LABEL.plist" "$AGENT_ROOT/Library/LaunchAgents/"
pkgbuild \
    --root "$AGENT_ROOT" \
    --install-location "/" \
    --identifier "$BUNDLE_ID.agent" \
    --version "$VERSION" \
    --ownership recommended \
    "$WORK_DIR/agent.pkg"

# --- 4. Distribution metadata ----------------------------------------------------------------------------------

if [[ "$DISTRIBUTION" == "1" ]]; then
    SIGNED_NOTE=""
else
    SIGNED_NOTE='<p style="color:#888;">This package is not signed with an Apple Developer ID, so macOS may warn that it is from an unidentified developer. If it refuses to open, right-click the .pkg and choose <b>Open</b>.</p>'
fi

cat > "$RES_DIR/welcome.html" <<HTML
<html><body style="font-family: -apple-system, 'Helvetica Neue', sans-serif; font-size: 13px;">
<h2>Remote Desktop — 被控端（Host）</h2>
<p>安装 <b>Remote Desktop Host.app</b> 到 <b>/Applications</b>，并安装一个登录项，让它在每次图形登录时自动启动。
这个应用<b>没有窗口</b>，只负责被控，不能控制别人。</p>
<p>它与控制端是两个独立的 app（bundle id 不同，TCC 授权也分开）。一台机器通常只装其中一个；两个都装会变成两个
被控端，各有各的设备码和监听。</p>
$SIGNED_NOTE
</body></html>
HTML

cat > "$RES_DIR/conclusion.html" <<HTML
<html><body style="font-family: -apple-system, 'Helvetica Neue', sans-serif; font-size: 13px;">
<h2>还需要两步</h2>
<p><b>1. 授权。</b>打开「系统设置 → 隐私与安全性」，在下面两项里勾选 <b>Remote Desktop Host</b>：</p>
<ul>
  <li><b>屏幕与系统音频录制</b> —— 采集屏幕（和系统声音）。</li>
  <li><b>辅助功能</b> —— 接收远程键鼠。</li>
</ul>
<p>首次启动会弹系统提示；授权后执行下面的命令重启它（屏幕录制权限只在新进程启动时读取）：</p>
<pre>launchctl kickstart -k gui/\$(id -u)/com.remotedesktop.host</pre>

<p><b>2. 设置固定密码。</b>host 没有界面，临时密码只在进程内存里，不在任何窗口或日志里显示；无人值守必须用固定密码。
用当前登录用户在终端执行（至少 8 位，不能包含双引号）：</p>
<pre>"/Applications/Remote Desktop Host.app/Contents/MacOS/RemoteDesktop.HostService" --set-fixed-password '至少8位'</pre>
<p>然后同样 kickstart 重启。Keychain 可能弹一次访问确认。</p>

<p>服务器地址：编辑 <b>~/Library/Application Support/RemoteDesktop/appsettings.json</b> 里的
<b>Transport:RendezvousUri</b>（随包默认是 ws://127.0.0.1:5080/ws），再 kickstart。设备码在
<b>~/Library/Application Support/RemoteDesktop/logs/</b> 的日志里，搜索 “Registered with the rendezvous server as”。</p>
</body></html>
HTML

cp "$REPO_ROOT/LICENSE" "$RES_DIR/License.txt"

cat > "$WORK_DIR/distribution.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="2">
    <title>Remote Desktop Host</title>
    <organization>com.remotedesktop</organization>
    <domains enable_anywhere="false" enable_currentUserHome="false" enable_localSystem="true"/>
    <options customize="never" require-scripts="false" rootVolumeOnly="true" hostArchitectures="$ARCH"/>
    <welcome file="welcome.html" mime-type="text/html"/>
    <conclusion file="conclusion.html" mime-type="text/html"/>
    <license file="License.txt"/>
    <choices-outline>
        <line choice="default">
            <line choice="$BUNDLE_ID.app"/>
            <line choice="$BUNDLE_ID.agent"/>
        </line>
    </choices-outline>
    <choice id="default"/>
    <choice id="$BUNDLE_ID.app" visible="false"><pkg-ref id="$BUNDLE_ID.app"/></choice>
    <choice id="$BUNDLE_ID.agent" visible="false"><pkg-ref id="$BUNDLE_ID.agent"/></choice>
    <pkg-ref id="$BUNDLE_ID.app" version="$VERSION" onConclusion="none">app.pkg</pkg-ref>
    <pkg-ref id="$BUNDLE_ID.agent" version="$VERSION" onConclusion="none">agent.pkg</pkg-ref>
</installer-gui-script>
XML

# --- 5. The product package ------------------------------------------------------------------------------------

PKG_NAME="RemoteDesktop-Host-macOS-$ARCH-$VERSION.pkg"
FINAL_PKG="$OUTPUT_DIR/$PKG_NAME"

BUILD_ARGS=(--distribution "$WORK_DIR/distribution.xml" --resources "$RES_DIR" --package-path "$WORK_DIR")

if [[ "$DISTRIBUTION" == "1" ]]; then
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

# --- 6. Notarization -------------------------------------------------------------------------------------------

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

# --- 7. Verify -------------------------------------------------------------------------------------------------

echo "==> Verifying"
pkgutil --check-signature "$FINAL_PKG" 2>&1 | sed 's/^/    /' || true
if [[ "$DISTRIBUTION" == "1" ]]; then
    spctl -a -vvv -t install "$FINAL_PKG" 2>&1 | sed 's/^/    /' || true
fi

# What has to be right is both parts of the payload: the app (which is what TCC grants permissions to) and the login
# item. Each component is checked as the .pkg pkgbuild produced — pkgutil --payload-files does not read an expanded
# component directory — and the product is then expanded to prove both components made it into it.
echo "    payload:"
pkgutil --payload-files "$WORK_DIR/app.pkg" | grep 'RemoteDesktop.HostService$' | sed 's/^/      /'
pkgutil --payload-files "$WORK_DIR/agent.pkg" | grep 'com.remotedesktop.host.plist$' | sed 's/^/      /'
if ! pkgutil --payload-files "$WORK_DIR/app.pkg" | grep -q 'Remote Desktop Host.app/Contents/Info.plist'; then
    echo "The application bundle is missing from the package." >&2
    exit 1
fi
if ! pkgutil --payload-files "$WORK_DIR/app.pkg" | grep -q 'Remote Desktop Host.app/Contents/MacOS/RemoteDesktop.HostService'; then
    echo "The host executable is missing from the package." >&2
    exit 1
fi
if ! pkgutil --payload-files "$WORK_DIR/agent.pkg" | grep -q 'LaunchAgents/com.remotedesktop.host.plist'; then
    echo "The login item is missing from the package." >&2
    exit 1
fi

VERIFY_DIR="$WORK_DIR/verify"
pkgutil --expand "$FINAL_PKG" "$VERIFY_DIR"
for component in app agent; do
    if [[ ! -d "$VERIFY_DIR/$component.pkg" ]]; then
        echo "The $component component is missing from $PKG_NAME." >&2
        exit 1
    fi
done

echo
echo "Built: $FINAL_PKG"
echo "Install on this machine:  sudo installer -pkg \"$FINAL_PKG\" -target /"
