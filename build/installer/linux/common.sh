#!/bin/sh
#
# Helpers shared by the three Linux package builds.
#
# Sourced, not executed. Everything here is build-time only: the maintainer scripts in the packages have to stand on
# their own, because a config script runs before the package it belongs to has been unpacked.
#
# Usage:
#
#     . "$root/build/installer/linux/common.sh"

# The two packages that carry their own libmsquic. The runtime does not ship it on Linux, and a self-contained
# program looks for it beside itself and nowhere else.
rd_stage_libmsquic() {
    _rd_root=${1:?rd_stage_libmsquic needs the repository root}
    _rd_native="$_rd_root/artifacts/lib/linux"

    if [ ! -f "$_rd_native/libmsquic.so.2" ]; then
        # The path is this function's output; everything the fetch says goes to the person watching the build.
        echo "Fetching libmsquic..." >&2
        "$_rd_root/native/linux/fetch-libmsquic.sh" >&2
    fi

    printf '%s\n' "$_rd_native"
}

# Copies the versioned library and recreates the soname link from whatever was actually staged. The build scripts
# used to spell "2.6.2" themselves, which meant a version bump left a dangling symlink in the package — and the
# package still built.
rd_copy_libmsquic() {
    _rd_native=${1:?rd_copy_libmsquic needs the staged directory}
    _rd_program=${2:?rd_copy_libmsquic needs the program directory}

    _rd_versioned=$(ls "$_rd_native"/libmsquic.so.*.*.* 2>/dev/null | head -n 1)
    if [ -z "$_rd_versioned" ]; then
        echo "No versioned libmsquic in $_rd_native." >&2
        return 1
    fi

    cp -a "$_rd_versioned" "$_rd_program/"
    _rd_major=$(basename "$_rd_versioned" | cut -d. -f3)
    ln -sf "$(basename "$_rd_versioned")" "$_rd_program/libmsquic.so.$_rd_major"
}

# The client and host packages carry a signaling address baked in at build time. It is written into the shipped
# appsettings.json, and an installation can replace it with the address the operator enters.
rd_validate_rendezvous_uri() {
    case "$1" in
        ws://*|wss://*) ;;
        *)
            echo "The signaling address must start with ws:// or wss:// (got '$1')." >&2
            return 1
            ;;
    esac

    case "$1" in
        *'&'*|*'#'*|*'\'*|*'"'*|*'|'*|*[[:space:]]*)
            echo "The signaling address contains a character this script cannot safely substitute." >&2
            return 1
            ;;
    esac
}

rd_apply_rendezvous_uri() {
    _rd_settings=${1:?rd_apply_rendezvous_uri needs a settings file}
    _rd_uri=${2:?rd_apply_rendezvous_uri needs an address}

    rd_validate_rendezvous_uri "$_rd_uri" || return 1

    sed "s#\"RendezvousUri\"[[:space:]]*:[[:space:]]*\"[^\"]*\"#\"RendezvousUri\": \"$_rd_uri\"#" \
        "$_rd_settings" > "$_rd_settings.new"
    mv "$_rd_settings.new" "$_rd_settings"

    grep -Fq "\"$_rd_uri\"" "$_rd_settings" || {
        echo "Could not set Transport:RendezvousUri in $_rd_settings." >&2
        return 1
    }
}

# A certificate, not a key. Handing this script the private key by mistake used to put it into every client package,
# world-readable, under the name of a trusted certificate.
rd_validate_certificate() {
    _rd_certificate=${1:?rd_validate_certificate needs a file}

    if command -v openssl >/dev/null 2>&1; then
        openssl x509 -in "$_rd_certificate" -noout 2>/dev/null || {
            echo "$_rd_certificate is not a PEM certificate. Pass the public half (signaling.crt), not the key." >&2
            return 1
        }
    else
        grep -q -- '-----BEGIN CERTIFICATE-----' "$_rd_certificate" || {
            echo "$_rd_certificate is not a PEM certificate. Pass the public half (signaling.crt), not the key." >&2
            return 1
        }
    fi
}

# The licence and third-party notices travel with the binaries they cover; the changelog is what Debian's tools
# look for next to them.
rd_install_docs() {
    _rd_root=${1:?rd_install_docs needs the repository root}
    _rd_doc=${2:?rd_install_docs needs the package doc directory}

    mkdir -p "$_rd_doc"
    install -m 0644 "$_rd_root/LICENSE" "$_rd_doc/copyright"
    install -m 0644 "$_rd_root/THIRD-PARTY-NOTICES.md" "$_rd_doc/third-party-notices.md"
    gzip -9 -n -c "$_rd_root/CHANGELOG.md" > "$_rd_doc/changelog.gz"
    chmod 0644 "$_rd_doc/changelog.gz"
}

# The icon sizes a desktop launcher asks for. The host needs these too: the portal shows the application's icon in
# the permission dialog, and a .desktop file whose icon resolves to nothing gets a generic picture.
rd_install_icons() {
    _rd_root=${1:?rd_install_icons needs the repository root}
    _rd_work=${2:?rd_install_icons needs the package work directory}
    _rd_name=${3:-remotedesktop}

    for _rd_icon in "$_rd_root"/src/Hosts/RemoteDesktop.Client/Assets/icon-*.png; do
        _rd_size=$(basename "$_rd_icon" .png | sed 's/^icon-//')
        case "$_rd_size" in
            16|24|32|48|64|128|256|512)
                mkdir -p "$_rd_work/usr/share/icons/hicolor/${_rd_size}x${_rd_size}/apps"
                install -m 0644 "$_rd_icon" "$_rd_work/usr/share/icons/hicolor/${_rd_size}x${_rd_size}/apps/${_rd_name}.png"
                ;;
        esac
    done
}

# Installed-Size and md5sums. dpkg-deb --build does neither for a tree that was assembled without debhelper, and
# both are part of what a Debian package is supposed to carry.
rd_finalize_control() {
    _rd_work=${1:?rd_finalize_control needs the package work directory}

    _rd_installed_size=$(du -sk "$_rd_work" | cut -f1)
    # awk and a move rather than `sed -i`: the in-place flag is GNU-only (BSD sed reads -i as a backup suffix),
    # and the \n in the replacement is not portable either.
    awk -v size="$_rd_installed_size" '/^Description:/ { print "Installed-Size: " size } { print }' \
        "$_rd_work/DEBIAN/control" >"$_rd_work/DEBIAN/control.new"
    mv "$_rd_work/DEBIAN/control.new" "$_rd_work/DEBIAN/control"

    (
        cd "$_rd_work" || exit 1
        # Paths in a Debian md5sums file are relative to the root, without the ./ that find prints.
        find . -type f ! -path './DEBIAN/*' -print0 | sort -z | xargs -0 md5sum | sed 's|  \./|  |'
    ) >"$_rd_work/DEBIAN/md5sums"
    chmod 0644 "$_rd_work/DEBIAN/md5sums"
}

# Copies the debconf question set into the package, substituting the address the package was built with. On a first
# install the operator sees that value as the default; on an upgrade debconf keeps the answer already given.
rd_install_debconf() {
    _rd_source=${1:?rd_install_debconf needs the package installer directory}
    _rd_work=${2:?rd_install_debconf needs the package work directory}
    _rd_default_uri=${3:-}

    if [ -n "$_rd_default_uri" ]; then
        # The placeholder is in both files: the template is the question's default, and the config script uses the
        # same build-time value to seed the question before the package's own appsettings is unpacked.
        sed "s|@DEFAULT_RENDEZVOUS_URI@|$_rd_default_uri|" \
            "$_rd_source/config" >"$_rd_work/DEBIAN/config"
        sed "s|@DEFAULT_RENDEZVOUS_URI@|$_rd_default_uri|" \
            "$_rd_source/templates" >"$_rd_work/DEBIAN/templates"
    else
        install -m 0755 "$_rd_source/config" "$_rd_work/DEBIAN/config"
        install -m 0644 "$_rd_source/templates" "$_rd_work/DEBIAN/templates"
    fi

    chmod 0755 "$_rd_work/DEBIAN/config"

    chmod 0644 "$_rd_work/DEBIAN/templates"
}
