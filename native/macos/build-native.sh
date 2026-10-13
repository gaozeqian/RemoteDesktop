#!/bin/sh
#
# Builds the Objective-C helper library the macOS platform layer binds to.
#
# Why this exists: ScreenCaptureKit is Objective-C only, and its shape — an async factory, a delegate protocol for
# frame delivery, block-based completion handlers — is the worst case for P/Invoke. The Objective-C lives in
# native/macos/rd_*.m and crosses into C# as a flat C ABI; see rd_native.h for the contract. This script turns those
# sources into the one dylib the platform layer loads.
#
# The result is cached in artifacts/native/macos (artifacts/ is already ignored) and copied next to every build
# output that needs it, the same way fetch-libmsquic.sh stages libmsquic. Unlike that script this one is *not*
# one-shot: it rebuilds whenever a source or the header is newer than the dylib, because these files are edited far
# more often than a downloaded bottle is.
#
#     native/macos/build-native.sh            # rebuild if needed
#     native/macos/build-native.sh --force    # rebuild regardless
#
# Needs the Xcode Command Line Tools for clang and the macOS SDK. It does *not* need Xcode itself.
set -eu

force=0
case "${1:-}" in
    --force) force=1 ;;
    -h | --help)
        sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
        exit 0
        ;;
    "") ;;
    *)
        echo "Unknown option: $1" >&2
        exit 1
        ;;
esac

root=$(cd "$(dirname "$0")/../.." && pwd)
source_dir="$root/native/macos"
out_dir="$root/artifacts/native/macos"
output="$out_dir/libRemoteDesktopMacNative.dylib"

# The video codec sources sit apart from the capture ones: rd_video_common holds the Annex-B/AVCC conversions that
# both directions share, and rd_video_encoder/rd_video_decoder each hold one VideoToolbox session. Splitting them
# is what lets each be compiled and tested on its own.
sources="$source_dir/rd_screen_capture.m $source_dir/rd_video_common.m $source_dir/rd_video_encoder.m $source_dir/rd_video_decoder.m $source_dir/rd_camera.m $source_dir/rd_audio.m $source_dir/rd_cursor.m $source_dir/rd_clipboard.m $source_dir/rd_system.m"
header="$source_dir/rd_native.h"

for tool in xcrun clang install_name_tool codesign otool; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "Missing required tool: $tool" >&2
        echo "Install the Xcode Command Line Tools with: xcode-select --install" >&2
        exit 1
    }
done

# Rebuild when any input is newer than the output, so editing a source is enough to get a fresh library without
# remembering to pass --force.
needs_build=$force
if [ ! -f "$output" ]; then
    needs_build=1
else
    for input in $sources $header; do
        if [ "$input" -nt "$output" ]; then
            needs_build=1
            break
        fi
    done
fi

if [ "$needs_build" -eq 0 ]; then
    echo "Up to date: $output"
    exit 0
fi

mkdir -p "$out_dir"

# -fobjc-arc because the .m files rely on ARC for the Objective-C object lifetimes they hand across the C ABI.
# -mmacosx-version-min is deliberately lower than the application's floor: nothing here needs anything newer than
# ScreenCaptureKit 12.3, and pinning it to the app's macOS 26 would blame this library for a limit it does not
# impose. -fvisibility=hidden keeps everything that is not explicitly exported out of the symbol table, so the
# header is the whole surface.
echo "Compiling the native helper..."
xcrun clang \
    -dynamiclib \
    -arch arm64 \
    -mmacosx-version-min=14.0 \
    -std=c11 \
    -fobjc-arc \
    -fvisibility=hidden \
    -O2 \
    -Wall \
    -Wextra \
    -Werror \
    -Wno-unused-parameter \
    -I"$source_dir" \
    -o "$output" \
    $sources \
    -framework AppKit \
    -framework AudioToolbox \
    -framework AVFoundation \
    -framework CoreGraphics \
    -framework CoreMedia \
    -framework CoreVideo \
    -framework Foundation \
    -framework ScreenCaptureKit \
    -framework VideoToolbox

# The load command the .NET resolver looks for. @rpath rather than an absolute path, because the library ships
# inside the .app bundle and moves with it.
install_name_tool -id "@rpath/libRemoteDesktopMacNative.dylib" "$output"

# Rewriting the load command invalidates any signature, and macOS refuses to load an arm64 binary whose signature
# is stale. Ad-hoc is enough here: this is a build product, and the .app's own signature covers it at packaging.
codesign --force --sign - "$output"

echo
echo "Built $output"
otool -L "$output" | tail -n +2 | sed 's/^/    /'
echo
echo "Exported symbols:"
nm -gU "$output" | grep ' _rd_' | sed 's/^/    /' || echo "    (none — check -fvisibility and the RD_* declarations)"
