#!/usr/bin/env bash
#
# Builds "Remote Desktop.app" — the client as a macOS application bundle.
#
# Why a bundle and not just the executable: macOS grants screen recording, accessibility, camera and keychain access to
# a *bundle with a stable code-signing identity*, not to a path. Since macOS 26 a bare CLI executable does not even
# appear in the Privacy & Security list, so running `dotnet run` cannot be granted anything. See
# docs/ports/README.md ("TCC 授权按签名身份记账" 与 "Keychain 的 OSStatus 100001 是 EPERM").
#
# Usage:
#   build/installer/macos/client/make-app.sh [--configuration Release] [--output <dir>] [--framework-dependent]
#                                            [--distribution]
#
# --distribution signs with the hardened runtime and a secure timestamp (and refuses to build without a real signing
# identity) so the result can be notarized. Without it the bundle is signed for local development, which is faster and
# works offline. make-pkg.sh turns it on for a distributable .pkg.
#
# Self-contained by default, and that is not a size preference: a framework-dependent apphost resolves the shared
# runtime from a registered location (`/usr/local/share/dotnet`, `DOTNET_ROOT`, …), and a machine whose .NET came from
# Homebrew has neither — the bundle then refuses to start with "You must install .NET to run this application". The
# .app is meant to be a deliverable, so it carries its own runtime.
#
# Environment:
#   APPLE_DEVELOPMENT_IDENTITY  Signing identity, e.g. "Apple Development: someone@example.com (ABCDE12345)".
#                               Overrides everything else. Prefer the `development-identity` file below, which does
#                               not have to be retyped per shell.
#   development-identity        A gitignored file next to this script holding the identity to sign with. Worth
#                               creating on any machine with more than one Apple Development certificate, because
#                               TCC grants are keyed to the signing identity and the fallback below cannot know
#                               which one you meant.
#                               With no identity available the bundle is ad-hoc signed and this script says so
#                               loudly: it will run, but TCC grants will keep failing (see docs/ports/README.md, "TCC 授权按签名身份记账").
#   RD_SKIP_PUBLISH=1           Reuse an existing publish output instead of running `dotnet publish`.
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

CONFIGURATION="Release"
OUTPUT_DIR="$REPO_ROOT/artifacts/app/macos"
SELF_CONTAINED=1
# The file name, not CFBundleDisplayName, is what the Dock shows in its hover tooltip: while this bundle was named
# RemoteDesktop.app the tooltip read "RemoteDesktop" even though LaunchServices reported "Remote Desktop". The host
# bundle has always been "Remote Desktop Host.app"; the client matches it now.
APP_NAME="Remote Desktop.app"
BUNDLE_ID="com.remotedesktop.client"
EXECUTABLE_NAME="RemoteDesktop.Client"
PROJECT="$REPO_ROOT/src/Hosts/RemoteDesktop.Client/RemoteDesktop.Client.csproj"
# Apple Silicon only, deliberately: the macOS port is arm64 (see docs/ports/README.md). A universal build would mean
# a second native helper library and a second publish, which is a separate piece of work rather than a flag here.
RUNTIME="osx-arm64"
# Distribution signing (Developer ID) differs from development signing in two ways that matter: the signature needs a
# secure timestamp (`--timestamp`, not `--timestamp=none`) and the hardened runtime (`--options runtime`). Both are
# prerequisites for notarization; neither is needed for a local build. Off by default so the development flow is
# unchanged. See make-pkg.sh, which turns this on.
DISTRIBUTION="${RD_DISTRIBUTION:-0}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --configuration) CONFIGURATION="$2"; shift 2 ;;
        --output)        OUTPUT_DIR="$2";    shift 2 ;;
        --self-contained)    SELF_CONTAINED=1; shift ;;
        --framework-dependent) SELF_CONTAINED=0; shift ;;
        --distribution)  DISTRIBUTION=1; shift ;;
        -h|--help)       sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed '$d'; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "This script only runs on macOS." >&2
    exit 1
fi

# The SDK's own publish folder, not one of our own: MSBuild then owns the contents and removes what a previous publish
# left behind, which a hand-rolled `--output` directory does not get for free. Where that folder *is*, though, is
# MSBuild's business rather than something to spell out here — with UseArtifactsOutput (see Directory.Build.props) it is
# artifacts/publish/<project>/<pivot>/ and not the old bin/<configuration>/<tfm>/<rid>/publish. Writing either one down
# only moves the coupling somewhere else, so it is asked for. The answer comes back absolute.
SELF_CONTAINED_ARG="$([[ "$SELF_CONTAINED" == "1" ]] && echo true || echo false)"
PUBLISH_DIR="$(dotnet msbuild "$PROJECT" -nologo -getProperty:PublishDir \
    -p:Configuration="$CONFIGURATION" -p:RuntimeIdentifier="$RUNTIME" -p:SelfContained="$SELF_CONTAINED_ARG")"
APP_DIR="$OUTPUT_DIR/$APP_NAME"
CONTENTS_DIR="$APP_DIR/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"

echo "==> Publishing $EXECUTABLE_NAME ($CONFIGURATION, osx-arm64)"
if [[ "${RD_SKIP_PUBLISH:-0}" != "1" ]]; then
    dotnet publish "$PROJECT" \
        --configuration "$CONFIGURATION" \
        --runtime "$RUNTIME" \
        "--self-contained=$SELF_CONTAINED_ARG" \
        -p:RestoreLockedMode=true \
        --nologo
fi

if [[ ! -f "$PUBLISH_DIR/$EXECUTABLE_NAME" ]]; then
    echo "Publish output does not contain $EXECUTABLE_NAME: $PUBLISH_DIR" >&2
    exit 1
fi

echo "==> Assembling $APP_NAME"
# Rebuilt from scratch rather than copied over: a file that a previous build left in the bundle but the app no longer
# ships would otherwise stay behind and be sealed into the signature.
[[ -e "$APP_DIR" ]] && rm -rf "$APP_DIR"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"
cp -R "$PUBLISH_DIR/." "$MACOS_DIR/"

# Symbols belong to the machine that built the bundle, not the one that installs it. The MSIs already strip them.
find "$MACOS_DIR" -type f -name '*.pdb' -delete

# The licence and the notices that cover the bundled MIT/BSD/Apache components travel inside the bundle, so they
# remain readable after installation rather than only on the installer's licence page.
cp "$REPO_ROOT/LICENSE" "$RESOURCES_DIR/LICENSE.txt"
cp "$REPO_ROOT/THIRD-PARTY-NOTICES.md" "$RESOURCES_DIR/THIRD-PARTY-NOTICES.md"
chmod +x "$MACOS_DIR/$EXECUTABLE_NAME"

# The version in the bundle should be the version that was built, not a copy that drifts. Substituted the same way
# the host's Info.plist is: one mechanism for both bundles, and the file in the repository states that it is a
# placeholder rather than an old release. version.sh owns the read and its rules, so a missing or malformed
# <Version> fails here instead of producing a 0.0.0 bundle.
. "$SCRIPT_DIR/../../version.sh"
VERSION="$(read_product_version "$REPO_ROOT")"

sed "s/__VERSION__/$VERSION/g" "$SCRIPT_DIR/Info.plist" > "$CONTENTS_DIR/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleIdentifier $BUNDLE_ID" "$CONTENTS_DIR/Info.plist"

# PkgInfo is what pre-CFBundleInfo-era code looks for; AppKit still reads it.
printf 'APPL????' > "$CONTENTS_DIR/PkgInfo"

# The Dock/Finder icon, which is the only icon macOS shows for an application — a macOS window has no title-bar icon,
# so Window.Icon plays no part here. Generated from the same PNGs the app itself uses (see make-icns.sh), and put in
# place before signing so it is sealed into the bundle. A failure is a warning, not an error: an .app with the generic
# icon still runs, and refusing to package over artwork would be the wrong trade.
if "$SCRIPT_DIR/make-icns.sh" --output "$RESOURCES_DIR/AppIcon.icns" >/dev/null; then
    echo "    icon: AppIcon.icns ($(du -h "$RESOURCES_DIR/AppIcon.icns" | cut -f1 | tr -d ' '))"
else
    echo "    WARNING: could not build AppIcon.icns — Finder and the Dock will show the generic application icon." >&2
fi

echo "==> Signing"
IDENTITY="${APPLE_DEVELOPMENT_IDENTITY:-}"

# A machine-local pin, read before any guessing. Which certificate a build is signed with is a decision the operator
# makes once and then has to keep making identically, because TCC keys its grants to the signing identity — so
# "whichever comes first today" is the wrong default on any machine holding more than one. The file is gitignored:
# it names a personal certificate, and it belongs to the machine rather than to the repository.
if [[ -z "$IDENTITY" && -f "$SCRIPT_DIR/development-identity" ]]; then
    IDENTITY="$(sed -e 's/#.*//' -e '/^[[:space:]]*$/d' -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' \
        "$SCRIPT_DIR/development-identity" | sed -n '1p')"
    if [[ -n "$IDENTITY" ]]; then
        echo "    identity pinned by development-identity: $IDENTITY"
    fi
fi

if [[ -z "$IDENTITY" ]]; then
    # A real Apple Development identity is preferred over anything else in the keychain, and the reason is TCC
    # rather than the signature being "better": macOS keys screen-recording and accessibility grants to the signing
    # identity. A self-signed certificate has no Apple Team ID, so the system cannot tell one build from the next
    # and re-prompts. make-dev-cert.sh still has a use — it seals the bundle and proves the packaging works — but
    # it cannot hold a grant.
    #
    # Sorted, so that the pick is at least reproducible: `security find-identity` enumerates the keychain in an order
    # that is not part of any contract. Sorting does not make the choice *correct* when several Apple Development
    # certificates are installed — any of them is a valid answer, and which one is picked decides whose Team ID the
    # bundle carries and therefore which TCC grants it inherits. That is a decision, not a lookup, so when there is
    # more than one candidate the script says so and points at the override rather than choosing quietly.
    APPLE_IDENTITIES="$(security find-identity -v -p codesigning 2>/dev/null \
        | sed -n 's/.*"\(Apple Development: [^"]*\)".*/\1/p' | sort)"
    IDENTITY="$(printf '%s' "$APPLE_IDENTITIES" | sed -n '1p')"
    APPLE_IDENTITY_COUNT="$(printf '%s\n' "$APPLE_IDENTITIES" | sed '/^$/d' | wc -l | tr -d ' ')"
    if [[ "$APPLE_IDENTITY_COUNT" -gt 1 ]]; then
        cat >&2 <<MULTIPLE

    warning: $APPLE_IDENTITY_COUNT Apple Development identities are installed; picked "$IDENTITY".
             Pin the one you want by writing it to build/installer/macos/client/development-identity
             or by setting APPLE_DEVELOPMENT_IDENTITY. Screen-recording and accessibility grants
             are keyed to the signing identity, so building with a different one from last time
             invalidates them and macOS will prompt again.

MULTIPLE
    fi
fi
if [[ -z "$IDENTITY" ]]; then
    # Anything else that can sign, so a machine without a certificate still produces a runnable bundle.
    IDENTITY="$(security find-identity -v -p codesigning 2>/dev/null | sed -n 's/.*"\(.*\)".*/\1/p' | head -1)"
fi

# A distribution build must be signed with a real Developer ID and hardened runtime: an ad-hoc signature cannot be
# notarized, and the whole point of --distribution is to hand the result to notarization. Failing here beats producing
# a bundle that notarytool rejects five minutes later.
#
# The certificate *type* is checked, not just its presence: an "Apple Development" certificate is perfectly valid for
# local development and is exactly what a developer machine has, so without this check `--distribution` would happily
# produce a hardened-runtime bundle signed with a certificate Apple will not notarize.
if [[ "$DISTRIBUTION" == "1" ]]; then
    if [[ -z "$IDENTITY" ]]; then
        echo "Refusing to build a distribution bundle without a signing identity." >&2
        echo "Set APPLE_DEVELOPMENT_IDENTITY to a 'Developer ID Application: …' certificate." >&2
        exit 1
    fi
    if [[ "$IDENTITY" != "Developer ID Application:"* ]]; then
        echo "Refusing to build a distribution bundle with a non-distribution identity:" >&2
        echo "    $IDENTITY" >&2
        echo "It must be a 'Developer ID Application: …' certificate — an Apple Development certificate cannot be notarized." >&2
        exit 1
    fi
fi

if [[ "$DISTRIBUTION" == "1" ]]; then
    SIGN_FLAGS=(--options runtime --timestamp)
else
    SIGN_FLAGS=(--timestamp=none)
fi

SIGN_ARGS=(--force "${SIGN_FLAGS[@]}" --entitlements "$SCRIPT_DIR/entitlements.plist")
if [[ -n "$IDENTITY" ]]; then
    echo "    identity: $IDENTITY"
    SIGN_ARGS+=(--sign "$IDENTITY")
else
    cat >&2 <<'WARNING'

    ###########################################################################################
    # No code-signing identity found: falling back to an ad-hoc signature.                    #
    #                                                                                         #
    # The bundle will start, but macOS ties screen-recording, accessibility and camera grants  #
    # to the signing identity. An ad-hoc signature is different on every build, so the system  #
    # cannot tell one build from the next: TCC either re-prompts each time or silently         #
    # refuses, and the bundle never stays in the Privacy & Security list.                      #
    #                                                                                         #
    # (The keychain is NOT affected — that failure is the caller being sandboxed, not the      #
    # signature. See docs/ports/README.md, "Keychain 的 OSStatus 100001 是 EPERM".)                                             #
    #                                                                                         #
    # Install a free Apple Development certificate in Xcode and re-run.                        #
    ###########################################################################################

WARNING
    SIGN_ARGS+=(--sign -)
fi

# Nested code has to be signed before the bundle that contains it, or the outer signature is invalidated. The native
# libraries are signed without the app's entitlements: a dylib is not an application and has no business carrying
# allow-jit. (--deep below may re-apply them; that is noise, not breakage, and P10 replaces --deep with an explicit
# Contents/Frameworks layout for notarization anyway.)
while IFS= read -r -d '' nested; do
    codesign --force "${SIGN_FLAGS[@]}" --sign "${IDENTITY:--}" "$nested"
done < <(find "$MACOS_DIR" -type f \( -name '*.dylib' -o -name '*.so' \) -print0)

# --deep is not decoration here, it is the only way to seal this bundle. codesign treats *every* file in
# Contents/MacOS as a nested code object — not just Mach-O ones, not just executables. Verified by deletion: with the
# managed assemblies gone it moves on to complaining about RemoteDesktop.Client.deps.json. Since a flat .NET publish
# puts ~100 managed files and JSON config there, a plain `codesign <app>` refuses to seal the bundle at all and falls
# back to signing the executable alone, which produces exactly the signature that looks fine until it matters:
#
#     Identifier=apphost   Info.plist=not bound   Sealed Resources=none   (no Contents/_CodeSignature)
#
# With --deep the bundle seals properly and `codesign --verify --deep --strict` passes. Apple deprecates --deep for
# signing because it can pick the wrong identity for nested code in a structured bundle; a flat one has no such
# structure, and the alternative is moving the payload out of Contents/MacOS, which the .NET apphost cannot follow.
codesign "${SIGN_ARGS[@]}" --deep "$APP_DIR"

echo "==> Verifying"
codesign --verify --deep --strict --verbose=1 "$APP_DIR" 2>&1 | sed 's/^/    /'
/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$CONTENTS_DIR/Info.plist" | sed 's/^/    bundle id: /'
codesign -dv "$APP_DIR" 2>&1 | sed -n 's/^\(Authority\|TeamIdentifier\|Signature\|Identifier\)=/    \1=/p'

# The one thing that cannot be checked by reading the bundle: whether the QUIC native library actually loads. It is
# resolved by the apphost from its own directory, so a missing dylib is invisible until the first connection.
if [[ -f "$MACOS_DIR/libmsquic.dylib" ]]; then
    echo "    libmsquic.dylib present; its dependencies:"
    otool -L "$MACOS_DIR/libmsquic.dylib" | tail -n +2 | sed 's/^/      /'
else
    if [[ "$DISTRIBUTION" == "1" ]]; then
        echo "    ERROR: libmsquic.dylib is missing — QUIC will not start. Run native/macos/fetch-libmsquic.sh." >&2
        exit 1
    fi
    echo "    WARNING: libmsquic.dylib is missing — QUIC will not start. Run native/macos/fetch-libmsquic.sh." >&2
fi

# Same reasoning for the Objective-C helper: without it the platform layer loses screen capture and the cursor, and
# the only symptom is a DllNotFoundException at the first frame. It is produced by the build rather than downloaded,
# so the common cause is a publish that predates native/macos/build-native.sh.
if [[ -f "$MACOS_DIR/libRemoteDesktopMacNative.dylib" ]]; then
    echo "    libRemoteDesktopMacNative.dylib present; exported entry points:"
    nm -gU "$MACOS_DIR/libRemoteDesktopMacNative.dylib" | grep ' _rd_' | sed 's/^/      /' || echo "      (none!)"
else
    if [[ "$DISTRIBUTION" == "1" ]]; then
        echo "    ERROR: libRemoteDesktopMacNative.dylib is missing — screen capture will not work." >&2
        echo "           Run native/macos/build-native.sh, then rebuild and repackage." >&2
        exit 1
    fi
    echo "    WARNING: libRemoteDesktopMacNative.dylib is missing — screen capture will not work." >&2
    echo "             Run native/macos/build-native.sh, then rebuild and repackage." >&2
fi

echo
echo "Built: $APP_DIR"
echo "Run:   open \"$APP_DIR\""
echo "Logs:  open \"\$HOME/Library/Application Support/RemoteDesktop/crash.log\""
