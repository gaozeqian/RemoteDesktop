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
. "$root/build/installer/version.sh"
. "$root/build/installer/linux/common.sh"
version=$(read_product_version "$root")
architecture=${DEB_ARCHITECTURE:-amd64}
runtime=${DEB_RUNTIME:-linux-x64}

# Same single artifacts/ root and <kind>/<os>/ shape as the Windows and macOS installers: a .deb is a delivered
# installer, so it belongs under installer/, next to the .msi and the .pkg.
out=${1:-$root/artifacts/installer/linux}
work="$out/remotedesktop-host_${version}_${architecture}"
program="$work/usr/lib/remotedesktop-host"

for tool in dpkg-deb dotnet gzip md5sum; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "Missing required tool: $tool" >&2
        exit 1
    }
done

# libmsquic is what System.Net.Quic loads to speak QUIC at all, and the .NET runtime does not ship it on Linux. It is
# put next to the executable, which is a place the loader looks for a self-contained program and nowhere near a place
# it looks for anything else — so this package cannot accidentally give QUIC to some other program, and cannot be
# broken by one either.
native=$(rd_stage_libmsquic "$root")

rm -rf "$work"
mkdir -p "$work/DEBIAN" "$work/usr/lib/systemd/user" "$work/usr/share/applications" "$work/etc/default"

echo "Publishing the host for $runtime (version $version)..."
dotnet publish "$root/src/Hosts/RemoteDesktop.HostService" \
    -c Release -r "$runtime" --self-contained true \
    -p:RestoreLockedMode=true \
    -p:RemoteDesktopCrossTargeting=true \
    -o "$program" \
    --nologo -v quiet

rd_copy_libmsquic "$native" "$program"

# Symbols are for the machine that built the package, not the one that installs it. The MSIs already strip them.
find "$program" -type f -name '*.pdb' -delete

# The same address handling as the client: a deployment's server becomes the default the installation offers.
rendezvous=${RD_RENDEZVOUS_URI:-ws://127.0.0.1:5080/ws}
rd_validate_rendezvous_uri "$rendezvous"
rd_apply_rendezvous_uri "$program/appsettings.json" "$rendezvous"

server_certificate=${RD_SERVER_CERTIFICATE:-}
if [ -n "$server_certificate" ]; then
    [ -f "$server_certificate" ] || {
        echo "RD_SERVER_CERTIFICATE '$server_certificate' does not exist." >&2
        exit 1
    }
    rd_validate_certificate "$server_certificate"

    certificate_directory="$work/usr/local/share/ca-certificates"
    mkdir -p "$certificate_directory"
    install -m 0644 "$server_certificate" "$certificate_directory/remotedesktop-signaling-host.crt"
fi

# The licence, the third-party notices and the changelog travel with the binaries they cover.
rd_install_docs "$root" "$work/usr/share/doc/remotedesktop-host"

# The portal shows this application's icon in its permission dialog, and the .desktop file names it. The host
# package needs the same artwork as the client; a missing icon is a generic picture in the one dialog a user sees.
# Its own icon name: the client package owns remotedesktop.png, and two packages may not own the same file.
rd_install_icons "$root" "$work" remotedesktop-host

install -m 0644 "$root/build/service/linux/app-RemoteDesktopHost.service" "$work/usr/lib/systemd/user/"
install -m 0644 "$root/build/installer/linux/host/RemoteDesktopHost.desktop" "$work/usr/share/applications/"

install -m 0755 "$root/build/installer/linux/host/postinst" "$work/DEBIAN/postinst"
install -m 0755 "$root/build/installer/linux/host/postrm" "$work/DEBIAN/postrm"
rd_install_debconf "$root/build/installer/linux/host" "$work" "$rendezvous"

install -m 0644 "$root/build/installer/linux/host/remotedesktop-host.default" \
    "$work/etc/default/remotedesktop-host"
printf '%s\n' /etc/default/remotedesktop-host >"$work/DEBIAN/conffiles"

sed -e "s/@VERSION@/$version/" -e "s/@ARCHITECTURE@/$architecture/" \
    "$root/build/installer/linux/host/control" >"$work/DEBIAN/control"

# The program is self-contained and started by a unit file rather than from /usr/bin, so dpkg-deb works out neither
# the size nor the dependencies on its own. The dependencies are stated in control; the size and checksums here.
rd_finalize_control "$work"

echo "Building the package..."
package="$out/RemoteDesktop-Host-Linux-${architecture}-${version}.deb"
dpkg-deb --build --root-owner-group "$work" "$package"

echo "Built $package"
dpkg-deb --info "$package" | sed -n '1,20p'
