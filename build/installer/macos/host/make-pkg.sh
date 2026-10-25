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
        --arch)
            ARCH="$2"
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

find_identity() {
    security find-identity -v -p codesigning 2>/dev/null \
        | sed -n "s/.*\"\\($1: [^\"]*\\)\".*/\\1/p" | sort | sed -n '1p'
}

# Developer ID Installer certificates have no Code Signing EKU and are not returned by the codesigning policy.
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

# The host-only bundle, assembled by its own make-app.sh. It is not the client's bundle under another name: same
# composition root in the same .app shape, but no controller UI and a different bundle identifier, so TCC can grant
# the two separately.
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
mkdir -p "$RES_DIR" "$AGENT_ROOT/Library/LaunchAgents" \
    "$AGENT_ROOT/Library/Application Support/RemoteDesktop"
trap 'rm -rf "$WORK_DIR"' EXIT

# --- 3. The installer pane and its scripts ---------------------------------------------------------------------

PLUGIN_DIR="$WORK_DIR/plugins"
echo "==> Building the installer pane"
"$REPO_ROOT/native/macos/build-installer-plugin.sh" --role host --output "$PLUGIN_DIR" --version "$VERSION"
if [[ "$DISTRIBUTION" == "1" ]]; then
    codesign --force --options runtime --timestamp --sign "$APP_IDENTITY" \
        "$PLUGIN_DIR/RemoteDesktopSetup.bundle"
else
    codesign --force --sign - --timestamp=none "$PLUGIN_DIR/RemoteDesktopSetup.bundle"
fi

# The answers/certificate code is shared with the client package; the host's preinstall and postinstall add the
# launchd lifecycle around it. pkgbuild copies this directory into the product, so the shared file travels with it.
HOST_SCRIPTS="$WORK_DIR/scripts"
mkdir -p "$HOST_SCRIPTS"
cp -p "$SCRIPT_DIR/scripts/preinstall" "$SCRIPT_DIR/scripts/postinstall" "$HOST_SCRIPTS/"
cp -p "$REPO_ROOT/build/installer/macos/client/scripts/apply-answers.sh" "$HOST_SCRIPTS/"
chmod +x "$HOST_SCRIPTS/preinstall" "$HOST_SCRIPTS/postinstall"

# --- 4. The two component packages -----------------------------------------------------------------------------

# The app is staged with ditto and packaged with --root. ditto keeps the extended attributes that carry the per-file
# signatures of the .NET assemblies; staging without them produces an app that codesign reports as unsigned, which is
# exactly what TCC and notarization key on. --root is also the only form that accepts a component plist, which is how
# the bundle is marked non-relocatable.
#
# That flag matters: by default Installer looks for a copy of the same bundle identifier anywhere on the machine —
# LaunchServices knows about the build output this package was just made from — and upgrades that copy instead of
# writing to /Applications. Verified on macOS 26: the install reported success, /Applications stayed empty and the
# build tree was rewritten root-owned, so the login agent's absolute path had nothing to start.
echo "==> Building the application component"
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
    --install-location "/Applications" \
    --identifier "$BUNDLE_ID.app" \
    --version "$VERSION" \
    --ownership recommended \
    "$WORK_DIR/app.pkg"

echo "==> Building the login-item component"
cp "$REPO_ROOT/build/service/macos/$AGENT_LABEL.plist" "$AGENT_ROOT/Library/LaunchAgents/"
# A .pkg has no uninstaller; the login item is the part that would keep firing after a manual delete, so the removal
# script ships next to it rather than only in the documentation.
cp "$SCRIPT_DIR/uninstall-host.sh" "$AGENT_ROOT/Library/Application Support/RemoteDesktop/uninstall-host.sh"
chmod +x "$AGENT_ROOT/Library/Application Support/RemoteDesktop/uninstall-host.sh"

# The scripts live on the *agent* component, not the app component. The components are installed in the order the
# choices-outline lists: app, then agent. A postinstall on the app would run before the LaunchAgent plist exists, so
# `launchctl bootstrap` would have nothing to load. On the agent component the preinstall still sees the old job (and
# stops it) and the postinstall runs with both the new app and the new plist in place.
pkgbuild \
    --root "$AGENT_ROOT" \
    --install-location "/" \
    --identifier "$BUNDLE_ID.agent" \
    --version "$VERSION" \
    --ownership recommended \
    --scripts "$HOST_SCRIPTS" \
    "$WORK_DIR/agent.pkg"

# --- 5. Distribution metadata ----------------------------------------------------------------------------------

if [[ "$DISTRIBUTION" == "1" ]]; then
    SIGNED_NOTE=""
else
    SIGNED_NOTE='<p style="color:#888;">This package is not signed with an Apple Developer ID, so macOS may warn that it is from an unidentified developer. If it refuses to open, right-click the .pkg and choose <b>Open</b>.</p>'
fi

cat > "$RES_DIR/welcome.html" <<HTML
<html><head><meta http-equiv="Content-Type" content="text/html; charset=utf-8"></head><body style="font-family: -apple-system, 'Helvetica Neue', sans-serif; font-size: 13px;">
<h2>Remote Desktop — 被控端（Host）</h2>
<p>安装 <b>Remote Desktop Host.app</b> 到 <b>/Applications</b>，并安装一个登录项，让它在每次图形登录时自动启动。
这个应用<b>没有窗口</b>，只负责被控，不能控制别人。</p>
<p>它与控制端是两个独立的 app（bundle id 不同，TCC 授权也分开），<b>可以同时安装</b>：客户端检测到 Host 的
控制服务后只做控制端，Host 常驻负责被控；两者使用不同的设备身份，客户端显示自己的控制端代码，Host 的设备代码
在它的日志里。如果 Host 服务比客户端晚就绪，客户端会提示重启以交给 Host 托管。</p>
<p>下一页会要求填写信令服务器地址；如果服务器用 <b>wss://</b> 和自签名证书，请准备好它的公钥证书文件。
安装器会把它保存到 <b>/Library/Application Support/RemoteDesktop/certificates/</b>，host 只信任这一张证书
（证书固定），不会修改系统的信任设置。公开可信证书或 <b>ws://</b> 不需要证书。</p>
<p>同一页还可以设置<b>无人值守密码</b>（至少 8 位，保存在当前登录用户的钥匙串里）。留空表示不设置；升级时留空
会保留现有密码。</p>
$SIGNED_NOTE
</body></html>
HTML

cat > "$RES_DIR/conclusion.html" <<HTML
<html><head><meta http-equiv="Content-Type" content="text/html; charset=utf-8"></head><body style="font-family: -apple-system, 'Helvetica Neue', sans-serif; font-size: 13px;">
<h2>还需要两步</h2>
<p><b>1. 授权。</b>打开「系统设置 → 隐私与安全性」，在下面两项里勾选 <b>Remote Desktop Host</b>：</p>
<ul>
  <li><b>屏幕与系统音频录制</b> —— 采集屏幕（和系统声音）。</li>
  <li><b>辅助功能</b> —— 接收远程键鼠。</li>
</ul>
<p>安装器已经在当前图形会话里启动了 host，首次启动会弹系统提示。授权后执行下面的命令重启它（屏幕录制权限只在
新进程启动时读取）：</p>
<pre>launchctl kickstart -k gui/\$(id -u)/com.remotedesktop.host</pre>
<p>如果当时没有用户登录、安装器没有启动它，先执行：</p>
<pre>launchctl bootstrap gui/\$(id -u) /Library/LaunchAgents/com.remotedesktop.host.plist</pre>
<p>再 kickstart。局域网部署还需要在「系统设置 → 隐私与安全性 → 本地网络」里允许 Remote Desktop Host。</p>

<p><b>2. 无人值守密码。</b>host 没有界面，临时密码只在进程内存里，不在任何窗口或日志里显示；无人值守必须用
固定密码。安装器已经把你填写的密码保存到当前登录用户的钥匙串并重启了 host；如果安装时留空，或当时没有用户登录，
请用当前登录用户在终端执行（至少 8 位，不能包含双引号）：</p>
<pre>"/Applications/Remote Desktop Host.app/Contents/MacOS/RemoteDesktop.HostService" --set-fixed-password '至少8位'</pre>
<p>然后同样 kickstart 重启。Keychain 可能弹一次访问确认。</p>

<p>服务器地址：安装器已把它写入 <b>/Library/Application Support/RemoteDesktop/appsettings.json</b> 的
<b>Transport:RendezvousUri</b>。要只改当前用户的地址，编辑
<b>~/Library/Application Support/RemoteDesktop/appsettings.json</b>（不存在就创建，格式
<code>{"Transport":{"RendezvousUri":"wss://主机:5080/ws"}}</code>），它的优先级更高；改完 kickstart。
设备码在 <b>~/Library/Application Support/RemoteDesktop/logs/</b> 的日志里，搜索
“Registered with the rendezvous server as”。</p>

<p>卸载：<code>sudo "/Library/Application Support/RemoteDesktop/uninstall-host.sh"</code>。</p>
</body></html>
HTML

cp "$REPO_ROOT/LICENSE" "$RES_DIR/License.txt"

cat > "$WORK_DIR/distribution.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="2">
    <title>Remote Desktop Host</title>
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

# --- 6. The product package ------------------------------------------------------------------------------------

PKG_NAME="RemoteDesktop-Host-macOS-$ARCH-$VERSION.pkg"
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

# What has to be right is both parts of the payload: the app (which is what TCC grants permissions to) and the login
# item. Each component is checked as the .pkg pkgbuild produced — pkgutil --payload-files does not read an expanded
# component directory — and the product is then expanded to prove both components made it into it.
echo "    payload:"
pkgutil --payload-files "$WORK_DIR/app.pkg" | grep 'RemoteDesktop.HostService$' | sed 's/^/      /'
pkgutil --payload-files "$WORK_DIR/agent.pkg" | grep 'com.remotedesktop.host.plist$' | sed 's/^/      /'

VERIFY_DIR="$WORK_DIR/verify"
pkgutil --expand "$FINAL_PKG" "$VERIFY_DIR"
for component in app agent; do
    if [[ ! -d "$VERIFY_DIR/$component.pkg" ]]; then
        echo "The $component component is missing from $PKG_NAME." >&2
        exit 1
    fi
done

# The BOM, not pkgutil --payload-files: on a component package built with --scripts, pkgutil does not list the bundle's
# Info.plist even though it is in the BOM and the Installer installs it. Reading the BOM asks the question that
# matters — will this file land on disk — without depending on that listing quirk.
check_bom() {
    component="$1"
    wanted="$2"
    # grep without -q: it reads the whole BOM. With pipefail, -q closing the pipe early makes lsbom die of SIGPIPE and
    # turns a match into a failure, which is how this check reported a file that was plainly there.
    if ! lsbom "$VERIFY_DIR/$component.pkg/Bom" | grep -F -- "$wanted" >/dev/null; then
        echo "The $component component does not contain $wanted." >&2
        exit 1
    fi
}

check_bom app 'Remote Desktop Host.app/Contents/Info.plist'
check_bom app 'Remote Desktop Host.app/Contents/MacOS/RemoteDesktop.HostService'
check_bom app 'Remote Desktop Host.app/Contents/MacOS/libmsquic.dylib'
check_bom app 'Remote Desktop Host.app/Contents/MacOS/libRemoteDesktopMacNative.dylib'
check_bom agent 'LaunchAgents/com.remotedesktop.host.plist'
check_bom agent 'uninstall-host.sh'

echo
echo "Built: $FINAL_PKG"
echo "Install on this machine:  sudo installer -pkg \"$FINAL_PKG\" -target /"
