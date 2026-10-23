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
version=$(read_product_version "$root")
architecture=${DEB_ARCHITECTURE:-amd64}
runtime=${DEB_RUNTIME:-linux-x64}

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
native="$root/artifacts/lib/linux"
if [ ! -f "$native/libmsquic.so.2" ]; then
    echo "Fetching libmsquic..."
    "$root/native/linux/fetch-libmsquic.sh"
fi

rm -rf "$work"
mkdir -p "$work/DEBIAN" "$work/usr/lib/systemd/user"

echo "Publishing the host for $runtime (version $version)..."
dotnet publish "$root/src/Hosts/RemoteDesktop.HostService" \
    -c Release -r "$runtime" --self-contained true \
    -p:RemoteDesktopCrossTargeting=true \
    -o "$program" \
    --nologo -v quiet

cp -a "$native/libmsquic.so.2" "$native/libmsquic.so.2.6.2" "$program/" 2>/dev/null || cp -a "$native/libmsquic.so.2" "$program/"

# Symbols are for the machine that built the package, not the one that installs it. The MSIs already strip them.
find "$program" -type f -name '*.pdb' -delete
# A package built for a particular deployment can carry that deployment's address, so the client works on first
# launch instead of asking the person to find the setting. Empty keeps the built-in default.
rendezvous=${RD_RENDEZVOUS_URI:-}
if [ -n "$rendezvous" ]; then
    case "$rendezvous" in
        ws://*|wss://*) ;;
        *) echo "RD_RENDEZVOUS_URI must start with ws:// or wss:// (got '$rendezvous')." >&2; exit 1 ;;
    esac
    case "$rendezvous" in
        *'&'*|*'#'*|*'\\'*|*'"'*)
            echo "RD_RENDEZVOUS_URI contains a character this script cannot safely substitute." >&2; exit 1 ;;
    esac

    settings="$program/appsettings.json"
    sed "s#\"RendezvousUri\"[[:space:]]*:[[:space:]]*\"[^\"]*\"#\"RendezvousUri\": \"$rendezvous\"#" \
        "$settings" > "$settings.new"
    mv "$settings.new" "$settings"
    grep -Fq "\"$rendezvous\"" "$settings" || {
        echo "Could not set Transport:RendezvousUri in $settings." >&2
        exit 1
    }
fi

# A self-signed signaling certificate has to be trusted as a root before wss:// works. Installing it through
# update-ca-certificates makes it part of the system bundle the .NET client reads.
server_certificate=${RD_SERVER_CERTIFICATE:-}
if [ -n "$server_certificate" ]; then
    [ -f "$server_certificate" ] || {
        echo "RD_SERVER_CERTIFICATE '$server_certificate' does not exist." >&2
        exit 1
    }
    certificate_directory="$work/usr/local/share/ca-certificates"
    mkdir -p "$certificate_directory"
    install -m 0644 "$server_certificate" "$certificate_directory/remotedesktop-signaling-host.crt"
fi


# The licence and the third-party notices travel with the binaries they cover. Debian expects the licence at
# /usr/share/doc/<package>/copyright; the notices sit beside it under the name the generator gives them.
doc="$work/usr/share/doc/remotedesktop-host"
mkdir -p "$doc"
install -m 0644 "$root/LICENSE" "$doc/copyright"
install -m 0644 "$root/THIRD-PARTY-NOTICES.md" "$doc/third-party-notices.md"

install -m 0644 "$root/build/service/linux/app-RemoteDesktopHost.service" "$work/usr/lib/systemd/user/"
mkdir -p "$work/usr/share/applications"
install -m 0644 "$root/build/installer/linux/host/RemoteDesktopHost.desktop" "$work/usr/share/applications/"

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
