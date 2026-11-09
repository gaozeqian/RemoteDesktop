#!/usr/bin/env bash
#
# Builds the macOS Installer pane that collects the install-time answers (database, TLS, signaling address,
# certificate) and writes them where the package's preinstall can pick them up.
#
# The pane is a code-only Installer plugin: no nib, no Xcode project. productbuild includes the resulting bundle with
# --plugins; the package's make-pkg.sh signs it with the same Developer ID Application identity as the app (or ad-hoc
# for a local build) before productbuild.
#
# Usage:
#   native/macos/build-installer-plugin.sh --role <server|client|host> [--output <dir>] [--version <x.y.z>]
#
# Output (default: artifacts/installer/macos/plugins/<role>/):
#   RemoteDesktopSetup.bundle/   the plugin
#   InstallerSections.plist      puts it after the License and Target pages
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

ROLE=""
OUTPUT=""
VERSION=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --role)    ROLE="$2";    shift 2 ;;
        --output)  OUTPUT="$2";  shift 2 ;;
        --version) VERSION="$2"; shift 2 ;;
        -h|--help) sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed '$d'; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

case "$ROLE" in
    server|client|host) ;;
    *) echo "--role must be server, client or host (got '${ROLE:-}')." >&2; exit 2 ;;
esac

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "This script only runs on macOS." >&2
    exit 1
fi

for tool in clang codesign plutil; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "Missing required tool: $tool (part of the Xcode Command Line Tools)" >&2
        exit 1
    }
done

if [[ -z "$VERSION" ]]; then
    . "$REPO_ROOT/build/installer/version.sh"
    VERSION="$(read_product_version "$REPO_ROOT")"
fi

OUTPUT="${OUTPUT:-$REPO_ROOT/artifacts/installer/macos/plugins/$ROLE}"
BUNDLE="$OUTPUT/RemoteDesktopSetup.bundle"
CONTENTS="$BUNDLE/Contents"
SDK="$(xcrun --show-sdk-path)"

case "$ROLE" in
    server) TITLE="服务器配置" ;;
    client) TITLE="客户端配置" ;;
    host)   TITLE="被控端配置" ;;
esac

echo "==> Building the $ROLE installer pane"
rm -rf "$BUNDLE"
mkdir -p "$CONTENTS/MacOS"

# The framework headers live in the SDK; -framework finds the .tbd, the import below finds the headers. A code-only
# pane is supported by the API (InstallerPane.contentView may be built in code) and avoids shipping a nib for three
# roles that differ only in which controls are visible.
clang \
    -fobjc-arc \
    -fmodules \
    -arch arm64 \
    -mmacosx-version-min=12.0 \
    -isysroot "$SDK" \
    -bundle \
    -framework Cocoa \
    -framework InstallerPlugins \
    -Wno-deprecated-declarations \
    -o "$CONTENTS/MacOS/RemoteDesktopSetup" \
    "$SCRIPT_DIR/rd_installer_plugin.m"

cat > "$CONTENTS/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>zh_CN</string>
    <key>CFBundleDisplayName</key>
    <string>Remote Desktop Setup</string>
    <key>CFBundleExecutable</key>
    <string>RemoteDesktopSetup</string>
    <key>CFBundleIdentifier</key>
    <string>com.remotedesktop.installer.$ROLE</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>Remote Desktop Setup</string>
    <key>CFBundlePackageType</key>
    <string>BNDL</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <!-- The server also ships the locked Tahoe MsQuic/OpenSSL bottles, so every role shares the 26.0 floor. -->
    <key>LSMinimumSystemVersion</key>
    <string>26.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>RDInstallerSection</string>
    <key>InstallerSectionTitle</key>
    <string>$TITLE</string>
    <key>RDRole</key>
    <string>$ROLE</string>
</dict>
</plist>
PLIST

plutil -lint "$CONTENTS/Info.plist" >/dev/null

cat > "$OUTPUT/InstallerSections.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>SectionOrder</key>
    <array>
        <string>Introduction</string>
        <string>License</string>
        <string>Target</string>
        <string>RemoteDesktopSetup.bundle</string>
        <string>Install</string>
        <string>FinishUp</string>
    </array>
</dict>
</plist>
PLIST

plutil -lint "$OUTPUT/InstallerSections.plist" >/dev/null

# A placeholder signature so the bundle can be loaded by Installer for a local (unsigned) build. make-pkg.sh replaces
# it with the Developer ID Application identity when it builds a distribution package.
codesign --force --sign - --timestamp=none "$BUNDLE" >/dev/null

echo "    bundle: $BUNDLE"
echo "    $($(command -v stat) -f%z "$CONTENTS/MacOS/RemoteDesktopSetup") bytes, $(lipo -archs "$CONTENTS/MacOS/RemoteDesktopSetup" 2>/dev/null || echo "one architecture")"
echo "    role:   $ROLE ($TITLE)"
