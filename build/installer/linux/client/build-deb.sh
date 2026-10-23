#!/bin/sh
#
# Builds the client .deb from a checkout.
#
#     build/installer/linux/client/build-deb.sh [output-directory]
#
# The controlling end: the window somebody drives another machine from. Separate from the host package because they
# are separate things to want — most machines are one or the other, and a package that installed both would make
# every controller a machine that can also be reached.
set -eu

root=$(cd "$(dirname "$0")/../../../.." && pwd)
. "$root/build/installer/version.sh"
version=$(read_product_version "$root")
architecture=${DEB_ARCHITECTURE:-amd64}
runtime=${DEB_RUNTIME:-linux-x64}

# Same single artifacts/ root and <kind>/<os>/ shape as the Windows and macOS installers: a .deb is a delivered
# installer, so it belongs under installer/, next to the .msi and the .pkg.
out=${1:-$root/artifacts/installer/linux}
work="$out/remotedesktop-client_${version}_${architecture}"
program="$work/usr/lib/remotedesktop-client"
icons="$work/usr/share/icons/hicolor"

for tool in dpkg-deb dotnet; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "Missing required tool: $tool" >&2
        exit 1
    }
done

# Same reasoning as the host's package: the .NET runtime does not ship libmsquic on Linux, and a self-contained
# program looks for it beside itself and nowhere else.
native="$root/artifacts/lib/linux"
if [ ! -f "$native/libmsquic.so.2" ]; then
    echo "Fetching libmsquic..."
    "$root/native/linux/fetch-libmsquic.sh"
fi

rm -rf "$work"
mkdir -p "$work/DEBIAN" "$work/usr/bin" "$work/usr/share/applications" "$program"

echo "Publishing the client for $runtime (version $version)..."
dotnet publish "$root/src/Hosts/RemoteDesktop.Client" \
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
    install -m 0644 "$server_certificate" "$certificate_directory/remotedesktop-signaling-client.crt"
fi


# The licence and the third-party notices travel with the binaries they cover. Debian expects the licence at
# /usr/share/doc/<package>/copyright; the notices sit beside it under the name the generator gives them.
doc="$work/usr/share/doc/remotedesktop-client"
mkdir -p "$doc"
install -m 0644 "$root/LICENSE" "$doc/copyright"
install -m 0644 "$root/THIRD-PARTY-NOTICES.md" "$doc/third-party-notices.md"

# The icons go where a desktop looks for them, at every size the artwork comes in. A single large one would be scaled
# down for the launcher and look it.
for icon in "$root"/src/Hosts/RemoteDesktop.Client/Assets/icon-*.png; do
    size=$(basename "$icon" .png | sed 's/^icon-//')
    case "$size" in
        16|24|32|48|64|128|256|512)
            mkdir -p "$icons/${size}x${size}/apps"
            install -m 0644 "$icon" "$icons/${size}x${size}/apps/remotedesktop.png"
            ;;
    esac
done

install -m 0644 "$root/build/installer/linux/client/remotedesktop.desktop" "$work/usr/share/applications/"

# The launcher points at /usr/bin, so there has to be something there. A symlink rather than a copy: the program
# finds its own libraries by looking next to itself, and a second copy elsewhere would be a second place to update.
ln -s "/usr/lib/remotedesktop-client/RemoteDesktop.Client" "$work/usr/bin/remotedesktop"

install -m 0755 "$root/build/installer/linux/client/postinst" "$work/DEBIAN/postinst"
install -m 0755 "$root/build/installer/linux/client/postrm" "$work/DEBIAN/postrm"

sed -e "s/@VERSION@/$version/" -e "s/@ARCHITECTURE@/$architecture/" \
    "$root/build/installer/linux/client/control" >"$work/DEBIAN/control"

installed_size=$(du -sk "$work" | cut -f1)
# awk and a move rather than `sed -i`: the in-place flag is GNU-only (BSD sed reads -i as a backup suffix),
# and the \n in the replacement is not portable either.
awk -v size="$installed_size" '/^Description:/ { print "Installed-Size: " size } { print }' \
    "$work/DEBIAN/control" >"$work/DEBIAN/control.new"
mv "$work/DEBIAN/control.new" "$work/DEBIAN/control"

echo "Building the package..."
package="$out/RemoteDesktop-Client-Linux-${architecture}-${version}.deb"
dpkg-deb --build --root-owner-group "$work" "$package"

echo "Built $package"
dpkg-deb --info "$package" | sed -n '1,16p'
