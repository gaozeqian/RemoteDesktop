#!/bin/sh
#
# Stages the native MsQuic library so an application can bring its own copy.
#
# Why this exists: System.Net.Quic needs libmsquic at run time. Windows gets msquic.dll from the .NET runtime pack;
# Linux gets nothing, because Microsoft ships it as a separate package in their own apt repository rather than in the
# runtime. A machine that installs that package needs nothing from here.
#
# This script covers the other case: a machine that has no access to Microsoft's repository, or a build that would
# rather pin the version than depend on what a distribution happens to carry. It downloads the .deb, checks it against
# a pinned digest, and unpacks just the shared library into artifacts/lib/linux (artifacts/ is already ignored).
#
# Unlike the macOS script this does no relinking. There the library comes from Homebrew built for the Homebrew prefix
# and linking OpenSSL by absolute path; here it links the distribution's own libssl/libcrypto, which are simply
# present on any system that can run this program.
#
# Run it once per checkout:
#
#     native/linux/fetch-libmsquic.sh
#
# Then install libmsquic on the system, or point LD_LIBRARY_PATH at the staged file's directory (see
# CONTRIBUTING.md).
set -eu

# Pinned on purpose: the .NET runtime loads libmsquic without pinning a version, so an unnoticed upgrade changes
# behaviour under every existing installation. Bump this together with the digest below.
version=2.6.2
digest=42a7f6a4e846c6c7f7b1b727fbc5bf87e8b5c49adf752df6151d9ea47839007f
suite=resolute
base="https://packages.microsoft.com/ubuntu/26.04/prod"
file="pool/main/libm/libmsquic/libmsquic_${version}_amd64.deb"

root=$(cd "$(dirname "$0")/../.." && pwd)
out="$root/artifacts/lib/linux"

# Named with the version, because that is what is actually staged further down: this asked for "libmsquic.so", which
# nothing here ever creates, so the guard never fired and every run downloaded the package again.
if [ -f "$out/libmsquic.so.$version" ]; then
    echo "Already staged in $out"
    exit 0
fi

for tool in curl sha256sum dpkg-deb; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "Missing required tool: $tool" >&2
        exit 1
    }
done

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

echo "Downloading libmsquic $version from packages.microsoft.com..."
curl -fsSL -o "$work/libmsquic.deb" "$base/$file"

# The download is checked before anything is unpacked from it: the archive comes from a third party and its contents
# end up loaded into every process that speaks QUIC.
printf '%s  %s\n' "$digest" "$work/libmsquic.deb" >"$work/libmsquic.sha256"
(cd "$work" && sha256sum -c libmsquic.sha256 >/dev/null) || {
    echo "The downloaded package does not match the pinned digest; refusing to use it." >&2
    exit 1
}

mkdir -p "$work/root" "$out"
dpkg-deb -x "$work/libmsquic.deb" "$work/root"

# The runtime asks the loader for "libmsquic.so.2" — the major-versioned soname, not the unversioned name. That exact
# spelling has to exist here: without it the load fails and QUIC reports itself unsupported with no further detail.
cp "$work/root"/usr/lib/x86_64-linux-gnu/libmsquic.so.*.*.* "$out/"
major=$(printf '%s' "$version" | cut -d. -f1)
ln -sf "libmsquic.so.$version" "$out/libmsquic.so.$major"
chmod 0644 "$out"/libmsquic.so.*.*.*

echo "Staged in $out:"
ls -l "$out"
