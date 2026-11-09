#!/bin/sh
#
# Builds the server .deb from a checkout.
#
#     build/installer/linux/server/build-deb.sh [output-directory]
#
# One package holding all the servers (signaling, relay, the Ops console and its HTTPS gateway), because they are
# useless apart: the rendezvous server signs the tickets the relay checks, and they share a key that has to match.
# Installing them separately would be two steps that can be done wrong, and a half-installed pair that fails in a way
# that looks like a network problem.
#
# The programs are published self-contained: the target is a server that has to be given a package and nothing
# else, and asking an operator to install a .NET runtime first is one more thing that can be the wrong version.
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
work="$out/remotedesktop-server_${version}_${architecture}"

for tool in dpkg-deb dotnet gzip md5sum; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "Missing required tool: $tool" >&2
        exit 1
    }
done

rm -rf "$work"
mkdir -p "$work/DEBIAN" "$work/usr/lib/remotedesktop-server" "$work/lib/systemd/system" "$work/etc/default"

echo "Publishing the servers for $runtime (version $version)..."
for server in Rendezvous Relay Ops Gateway; do
    dotnet publish "$root/src/Server/RemoteDesktop.Server.$server" \
        -c Release -r "$runtime" --self-contained true \
        -p:RestoreLockedMode=true \
        -p:RemoteDesktopCrossTargeting=true \
        -o "$work/usr/lib/remotedesktop-server/$server" \
        --nologo -v quiet
done

# The settings are not shipped in the package: the post-install step writes them from /etc/default, so that a first
# install and every later upgrade go through the same code. The appsettings.json the publish produced sits next to
# the program and is never read — the services start in /etc, and that is the content root. The console is the same:
# its unit starts in /etc/remotedesktop-server/Ops and reads the appsettings.json there (see remotedesktop-ops.service).
rm -f "$work/usr/lib/remotedesktop-server/Rendezvous/appsettings"*.json
rm -f "$work/usr/lib/remotedesktop-server/Relay/appsettings"*.json
rm -f "$work/usr/lib/remotedesktop-server/Ops/appsettings"*.json
rm -f "$work/usr/lib/remotedesktop-server/Gateway/appsettings"*.json

# Symbols are for the machine that built the package, not the one that installs it. The MSIs already strip them.
find "$work/usr/lib/remotedesktop-server" -type f -name '*.pdb' -delete

# The licence and the third-party notices travel with the binaries they cover.
rd_install_docs "$root" "$work/usr/share/doc/remotedesktop-server"
# The server has no GUI page for its post-install configuration, and the certificate trust step happens on a different
# machine from the one this package is installed on. Keep the instructions beside the package documentation.
install -m 0644 "$root/build/installer/linux/server/how-to-configure.txt" \
    "$work/usr/share/doc/remotedesktop-server/how-to-configure.txt"

install -m 0644 "$root/build/service/linux/remotedesktop-rendezvous.service" "$work/lib/systemd/system/"
install -m 0644 "$root/build/service/linux/remotedesktop-relay.service" "$work/lib/systemd/system/"
install -m 0644 "$root/build/service/linux/remotedesktop-ops.service" "$work/lib/systemd/system/"
install -m 0644 "$root/build/service/linux/remotedesktop-gateway.service" "$work/lib/systemd/system/"
install -m 0644 "$root/build/service/linux/remotedesktop-server.default" "$work/etc/default/remotedesktop-server"

install -m 0755 "$root/build/installer/linux/server/postinst" "$work/DEBIAN/postinst"
install -m 0755 "$root/build/installer/linux/server/postrm" "$work/DEBIAN/postrm"
rd_install_debconf "$root/build/installer/linux/server" "$work"

# The settings file is the package's to ship and the operator's to keep: dpkg preserves their edits across upgrades.
printf '%s\n' /etc/default/remotedesktop-server >"$work/DEBIAN/conffiles"

sed -e "s/@VERSION@/$version/" -e "s/@ARCHITECTURE@/$architecture/" \
    "$root/build/installer/linux/server/control" >"$work/DEBIAN/control"

# The programs are self-contained and start from /usr/lib, so dpkg-deb works out neither their size nor their
# dependencies. The dependencies are stated in control; the size and the file checksums are added here.
rd_finalize_control "$work"

echo "Building the package..."
package="$out/RemoteDesktop-Server-Linux-${architecture}-${version}.deb"
dpkg-deb --build --root-owner-group "$work" "$package"

echo "Built $package"
dpkg-deb --info "$package" | sed -n '1,20p'
