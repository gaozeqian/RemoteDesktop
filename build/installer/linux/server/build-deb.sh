#!/bin/sh
#
# Builds the server .deb from a checkout.
#
#     build/installer/linux/server/build-deb.sh [output-directory]
#
# One package holding both servers, because they are useless apart: the rendezvous server signs the tickets the relay
# checks, and they share a key that has to match. Installing them separately would be two steps that can be done
# wrong, and a half-installed pair that fails in a way that looks like a network problem.
#
# The two programs are published self-contained: the target is a server that has to be given a package and nothing
# else, and asking an operator to install a .NET runtime first is one more thing that can be the wrong version.
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
work="$out/remotedesktop-server_${version}_${architecture}"

for tool in dpkg-deb dotnet; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "Missing required tool: $tool" >&2
        exit 1
    }
done

rm -rf "$work"
mkdir -p "$work/DEBIAN" "$work/usr/lib/remotedesktop-server" "$work/lib/systemd/system" "$work/etc/default"

echo "Publishing the two servers for $runtime (version $version)..."
for server in Rendezvous Relay; do
    dotnet publish "$root/src/Server/RemoteDesktop.Server.$server" \
        -c Release -r "$runtime" --self-contained true \
        -o "$work/usr/lib/remotedesktop-server/$server" \
        --nologo -v quiet
done

# The settings are not shipped in the package: the post-install step writes them from /etc/default, so that a first
# install and every later upgrade go through the same code. The appsettings.json the publish produced sits next to
# the program and is never read — the services start in /etc, and that is the content root.
rm -f "$work/usr/lib/remotedesktop-server/Rendezvous/appsettings"*.json
rm -f "$work/usr/lib/remotedesktop-server/Relay/appsettings"*.json

install -m 0644 "$root/build/service/linux/remotedesktop-rendezvous.service" "$work/lib/systemd/system/"
install -m 0644 "$root/build/service/linux/remotedesktop-relay.service" "$work/lib/systemd/system/"
install -m 0644 "$root/build/service/linux/default" "$work/etc/default/remotedesktop-server"

install -m 0755 "$root/build/installer/linux/server/postinst" "$work/DEBIAN/postinst"
install -m 0755 "$root/build/installer/linux/server/postrm" "$work/DEBIAN/postrm"

# The settings file is the package's to ship and the operator's to keep: dpkg preserves their edits across upgrades.
printf '%s\n' /etc/default/remotedesktop-server >"$work/DEBIAN/conffiles"

sed -e "s/@VERSION@/$version/" -e "s/@ARCHITECTURE@/$architecture/" \
    "$root/build/installer/linux/server/control" >"$work/DEBIAN/control"

# The programs are self-contained and the package says nothing about libraries, so the sizes have to be right.
# dpkg-deb does this itself for anything under /usr/bin, but not for a program under /usr/lib that is started by a
# unit file. Installed size also has to be stated rather than guessed: du reports in KiB and dpkg wants the number.
installed_size=$(du -sk "$work" | cut -f1)
# awk and a move rather than `sed -i`: the in-place flag is GNU-only (BSD sed reads -i as a backup suffix),
# and the \n in the replacement is not portable either.
awk -v size="$installed_size" '/^Description:/ { print "Installed-Size: " size } { print }' \
    "$work/DEBIAN/control" >"$work/DEBIAN/control.new"
mv "$work/DEBIAN/control.new" "$work/DEBIAN/control"

echo "Building the package..."
package="$out/RemoteDesktop-Server-Linux-${architecture}-${version}.deb"
dpkg-deb --build --root-owner-group "$work" "$package"

echo "Built $package"
dpkg-deb --info "$package" | sed -n '1,20p'
