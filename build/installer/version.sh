#!/bin/sh
#
# The product version, read and validated for the shell packagers.
#
# One number lives in Directory.Build.props; Windows, macOS and Linux all derive from it. The Windows scripts read
# the XML directly and append the fourth field MSI requires. The shell scripts used to repeat the same sed expression
# and then fall back to 0.0.0, which meant an unreadable file produced a valid-looking 0.0.0 package instead of a
# failure. This file is the one copy of that read and its rules.
#
# Usage:
#     . "$repo/build/installer/version.sh"
#     version=$(read_product_version "$repo") || exit 1
#
# It prints the version on stdout and everything else on stderr, so command substitution is safe.

read_product_version() {
    _rd_root=${1:?read_product_version needs the repository root}

    _rd_props="$_rd_root/Directory.Build.props"
    if [ ! -f "$_rd_props" ]; then
        echo "No $_rd_props." >&2
        return 1
    fi

    _rd_version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$_rd_props" | head -1)
    if [ -z "$_rd_version" ]; then
        echo "Could not read <Version> from $_rd_props." >&2
        return 1
    fi

    # Three numeric parts, the shape Windows Installer requires and the one macOS's CFBundleVersion and Debian's
    # Version both accept without interpreting anything. A pre-release suffix is not an option: an MSI cannot carry
    # one, and the other two platforms would read it differently (a Debian revision on Linux, an invalid bundle
    # version on macOS).
    if ! printf '%s\n' "$_rd_version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$'; then
        echo "<Version>$_rd_version</Version> in $_rd_props is not three numeric parts (major.minor.build)." >&2
        return 1
    fi

    # The same limits Windows Installer applies. Enforced here as well so that a version which could never produce
    # an MSI fails while packaging any platform, not only on Windows.
    if ! printf '%s\n' "$_rd_version" | awk -F. '$1 > 255 || $2 > 255 || $3 > 65535 { exit 1 }'; then
        echo "<Version>$_rd_version</Version> in $_rd_props exceeds the MSI limits (major/minor <= 255, build <= 65535)." >&2
        return 1
    fi

    printf '%s\n' "$_rd_version"
}
