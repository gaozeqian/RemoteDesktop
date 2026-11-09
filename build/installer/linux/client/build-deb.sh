#!/bin/sh
#
# Builds the client .deb from a checkout.
#
#     build/installer/linux/client/build-deb.sh [output-directory]
#
# The controlling end: the window somebody drives another machine from. Separate from the host package because they
# are separate things to want — most machines are one or the other, and a package that installed both would make
# every controller a machine that can also be reached.
#
# The signaling address is baked in as the default the installation offers, and the installation can replace it; see
# remotedesktop-client.default. RD_SERVER_CERTIFICATE is the site-build shortcut for a self-signed server
# certificate: it travels inside the package, and an installation can supply a different one.
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
work="$out/remotedesktop-client_${version}_${architecture}"
program="$work/usr/lib/remotedesktop-client"

for tool in dpkg-deb dotnet gzip md5sum; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "Missing required tool: $tool" >&2
        exit 1
    }
done

# Same reasoning as the host's package: the .NET runtime does not ship libmsquic on Linux, and a self-contained
# program looks for it beside itself and nowhere else.
native=$(rd_stage_libmsquic "$root")

rm -rf "$work"
mkdir -p "$work/DEBIAN" "$work/usr/bin" "$work/usr/share/applications" "$work/etc/default" "$program"

echo "Publishing the client for $runtime (version $version)..."
dotnet publish "$root/src/Hosts/RemoteDesktop.Client" \
    -c Release -r "$runtime" --self-contained true \
    -p:RestoreLockedMode=true \
    -p:RemoteDesktopCrossTargeting=true \
    -o "$program" \
    --nologo -v quiet

rd_copy_libmsquic "$native" "$program"

# Symbols are for the machine that built the package, not the one that installs it. The MSIs already strip them.
find "$program" -type f -name '*.pdb' -delete

# A package built for a particular deployment can carry that deployment's address as the default the installation
# offers, so the client works on first launch instead of asking the person to find the setting.
rendezvous=${RD_RENDEZVOUS_URI:-ws://127.0.0.1:5080/ws}
rd_validate_rendezvous_uri "$rendezvous"
rd_apply_rendezvous_uri "$program/appsettings.json" "$rendezvous"

# A self-signed signaling certificate has to be trusted as a root before wss:// works. Installing it through
# update-ca-certificates makes it part of the system bundle the .NET client reads.
server_certificate=${RD_SERVER_CERTIFICATE:-}
if [ -n "$server_certificate" ]; then
    [ -f "$server_certificate" ] || {
        echo "RD_SERVER_CERTIFICATE '$server_certificate' does not exist." >&2
        exit 1
    }
    rd_validate_certificate "$server_certificate"

    certificate_directory="$work/usr/local/share/ca-certificates"
    mkdir -p "$certificate_directory"
    install -m 0644 "$server_certificate" "$certificate_directory/remotedesktop-signaling-client.crt"
fi

# The licence, the third-party notices and the changelog travel with the binaries they cover.
rd_install_docs "$root" "$work/usr/share/doc/remotedesktop-client"

# The icons go where a desktop looks for them, at every size the artwork comes in. A single large one would be scaled
# down for the launcher and look it.
rd_install_icons "$root" "$work"

install -m 0644 "$root/build/installer/linux/client/remotedesktop.desktop" "$work/usr/share/applications/"

# The launcher points at /usr/bin, so there has to be something there. A symlink rather than a copy: the program
# finds its own libraries by looking next to itself, and a second copy elsewhere would be a second place to update.
ln -s "/usr/lib/remotedesktop-client/RemoteDesktop.Client" "$work/usr/bin/remotedesktop"

install -m 0755 "$root/build/installer/linux/client/postinst" "$work/DEBIAN/postinst"
install -m 0755 "$root/build/installer/linux/client/postrm" "$work/DEBIAN/postrm"
rd_install_debconf "$root/build/installer/linux/client" "$work" "$rendezvous"

install -m 0644 "$root/build/installer/linux/client/remotedesktop-client.default" \
    "$work/etc/default/remotedesktop-client"
printf '%s\n' /etc/default/remotedesktop-client >"$work/DEBIAN/conffiles"

sed -e "s/@VERSION@/$version/" -e "s/@ARCHITECTURE@/$architecture/" \
    "$root/build/installer/linux/client/control" >"$work/DEBIAN/control"

rd_finalize_control "$work"

echo "Building the package..."
package="$out/RemoteDesktop-Client-Linux-${architecture}-${version}.deb"
dpkg-deb --build --root-owner-group "$work" "$package"

echo "Built $package"
dpkg-deb --info "$package" | sed -n '1,20p'
