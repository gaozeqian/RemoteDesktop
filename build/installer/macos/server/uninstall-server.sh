#!/bin/bash
#
# Removes the installed servers.
#
# A .pkg has no uninstaller of its own — macOS has no equivalent of "Apps & features" for a package that is not an
# application bundle — so this is it. It stops the launchd jobs, takes their plists away, and deletes the install
# directory. The Windows package does the same three things from its MSI.
#
# Usage:
#   sudo ./uninstall-server.sh [--keep-settings] [--yes]
#
#   --keep-settings   remove the programs but leave the install directory (settings, logs and the relay key)
#   --yes             do not ask
#
# The relay shared key lives in the settings. If a relay runs on another machine, that machine was given this one's
# key; deleting it here means the other machine has to be reconfigured with whatever the next installation generates.
#
set -e

INSTALL_DIR="/usr/local/remotedesktop-server"
LABELS="com.remotedesktop.rendezvous com.remotedesktop.relay com.remotedesktop.ops"

keep_settings=0
assume_yes=0
while [ "$#" -gt 0 ]; do
    case "$1" in
        --keep-settings) keep_settings=1 ;;
        --yes|-y)        assume_yes=1 ;;
        -h|--help)       sed -n '2,/^set -e/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown option: $1 (try --help)" >&2; exit 2 ;;
    esac
    shift
done

if [ "$(id -u)" -ne 0 ]; then
    echo "This needs root: it stops system jobs and removes files under /usr/local." >&2
    echo "Try: sudo $0 $*" >&2
    exit 1
fi

if [ "$assume_yes" -eq 0 ]; then
    echo "This removes the Remote Desktop servers from this machine:"
    echo "  - the launchd jobs $LABELS"
    echo "  - $INSTALL_DIR$([ "$keep_settings" -eq 1 ] && echo ' (programs only; settings and logs stay)')"
    printf 'Continue? [y/N] '
    read -r answer
    case "$answer" in [Yy]*) ;; *) echo "Nothing was changed."; exit 0 ;; esac
fi

echo "==> Stopping the servers"
for label in $LABELS; do
    plist="/Library/LaunchDaemons/$label.plist"
    # bootout removes the job whether or not it is running. A job that was never loaded is not an error here.
    launchctl bootout system "$plist" 2>/dev/null || true
    rm -f "$plist"
done

if [ "$keep_settings" -eq 0 ]; then
    echo "==> Removing $INSTALL_DIR"
    rm -rf "$INSTALL_DIR"
    # The licence, notices and the exported signaling certificate live outside the install directory; leaving them
    # behind would make a removal look incomplete for a file the package is responsible for.
    rm -rf "/usr/local/share/doc/remotedesktop-server"
else
    echo "==> Leaving $INSTALL_DIR in place (and /usr/local/share/doc/remotedesktop-server)"
fi

# The receipt is what makes pkgutil and the Installer think the product is still installed. There is no uninstaller
# in a .pkg, so this script is also the place that forgets it.
/usr/sbin/pkgutil --forget com.remotedesktop.server >/dev/null 2>&1 || true

echo "==> Done. The servers are gone."
