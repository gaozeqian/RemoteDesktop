#!/bin/sh
#
# Builds the host .deb from a checkout.
#
#     build/installer/linux/host/build-deb.sh [output-directory]
#
# One package, one program: the end that is controlled. The controller — the window somebody drives it from — is a
# separate package because it is a separate thing to want, and a machine is much more likely to be one than the other.
#
# The program is published self-contained. The target is a desktop somebody already has, and asking them to install a
# .NET runtime of the right version first would be one more thing to get wrong on a machine that is supposed to be
# reachable when nobody is sitting at it.
set -eu

root=$(cd "$(dirname "$0")/../../../.." && pwd)
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -1)
architecture=${DEB_ARCHITECTURE:-amd64}
runtime=${DEB_RUNTIME:-linux-x64}

if [ -z "$version" ]; then
    echo "Could not read the version out of Directory.Build.props." >&2
    exit 1
fi

# Same single artifacts/ root and <kind>/<os>/ shape as the Windows and macOS installers: a .deb is a delivered
# installer, so it belongs under installer/, next to the .msi and the .pkg.
out=${1:-$root/artifacts/installer/linux}
work="$out/remotedesktop-host_${version}_${architecture}"
program="$work/usr/lib/remotedesktop-host"

for tool in dpkg-deb dotnet; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "Missing required tool: $tool" >&2
        exit 1
    }
done

# libmsquic is what System.Net.Quic loads to speak QUIC at all, and the .NET runtime does not ship it on Linux. It is
# put next to the executable, which is a place the loader looks for a self-contained program and nowhere near a place
# it looks for anything else — so this package cannot accidentally give QUIC to some other program, and cannot be
# broken by one either.
native="$root/artifacts/native/linux"
if [ ! -f "$native/libmsquic.so.2" ]; then
    echo "Fetching libmsquic..."
    "$root/native/linux/fetch-libmsquic.sh"
fi

rm -rf "$work"
mkdir -p "$work/DEBIAN" "$work/usr/lib/systemd/user"

echo "Publishing the host for $runtime (version $version)..."
dotnet publish "$root/src/Hosts/RemoteDesktop.HostService" \
    -c Release -r "$runtime" --self-contained true \
    -o "$program" \
    --nologo -v quiet

cp -a "$native/libmsquic.so.2" "$native/libmsquic.so.2.6.2" "$program/" 2>/dev/null || cp -a "$native/libmsquic.so.2" "$program/"

install -m 0644 "$root/build/service/linux/remotedesktop-host.service" "$work/usr/lib/systemd/user/"

install -m 0755 "$root/build/installer/linux/host/postinst" "$work/DEBIAN/postinst"
install -m 0755 "$root/build/installer/linux/host/postrm" "$work/DEBIAN/postrm"

sed -e "s/@VERSION@/$version/" -e "s/@ARCHITECTURE@/$architecture/" \
    "$root/build/installer/linux/host/control" >"$work/DEBIAN/control"

# The program is self-contained and started by a unit file rather than from /usr/bin, so dpkg-deb works out neither
# the size nor the dependencies on its own. The dependencies are stated in control; the size is stated here.
installed_size=$(du -sk "$work" | cut -f1)
# awk and a move rather than `sed -i`: the in-place flag is GNU-only (BSD sed reads -i as a backup suffix),
# and the \n in the replacement is not portable either.
awk -v size="$installed_size" '/^Description:/ { print "Installed-Size: " size } { print }' \
    "$work/DEBIAN/control" >"$work/DEBIAN/control.new"
mv "$work/DEBIAN/control.new" "$work/DEBIAN/control"

echo "Building the package..."
package="$out/RemoteDesktop-Host-Linux-${architecture}-${version}.deb"
dpkg-deb --build --root-owner-group "$work" "$package"

echo "Built $package"
dpkg-deb --info "$package" | sed -n '1,20p'
