#!/bin/bash
#
# Removes the host-only macOS application.
#
# A .pkg has no uninstaller, and deleting the .app by hand leaves the LaunchAgent in /Library/LaunchAgents pointing at
# a missing executable — launchd then tries to start it at every login. This script removes the job, the plist, the
# bundle and the package receipts, in that order.
#
# Usage:
#   sudo "/Library/Application Support/RemoteDesktop/uninstall-host.sh" [--yes]
#
# The per-user data (~/Library/Application Support/RemoteDesktop: device identity, fixed password, logs) and the
# machine-wide signaling address are left alone: the client uses the same places, and an uninstall that quietly
# removed the other app's identity would be a surprise. Delete that folder yourself if this machine will not run
# either app again.
#
set -e

PLIST="/Library/LaunchAgents/com.remotedesktop.host.plist"
APP="/Applications/Remote Desktop Host.app"

assume_yes=0
while [ "$#" -gt 0 ]; do
    case "$1" in
        --yes|-y) assume_yes=1 ;;
        -h|--help) sed -n '2,/^set -e/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown option: $1 (try --help)" >&2; exit 2 ;;
    esac
    shift
done

if [ "$(id -u)" -ne 0 ]; then
    echo "This needs root: it stops a login item and removes files under /Applications and /Library." >&2
    echo "Try: sudo $0 $*" >&2
    exit 1
fi

if [ "$assume_yes" -eq 0 ]; then
    echo "This removes Remote Desktop Host from this machine:"
    echo "  - the LaunchAgent com.remotedesktop.host and $PLIST"
    echo "  - $APP"
    echo "Per-user data and the machine-wide server address are kept."
    printf 'Continue? [y/N] '
    read -r answer
    case "$answer" in [Yy]*) ;; *) echo "Nothing was changed."; exit 0 ;; esac
fi

console_user="$(stat -f%Su /dev/console 2>/dev/null || true)"
if [ -n "$console_user" ] && [ "$console_user" != "root" ] && [ "$console_user" != "loginwindow" ]; then
    uid="$(id -u "$console_user" 2>/dev/null || true)"
    if [ -n "$uid" ]; then
        launchctl bootout "gui/$uid" "$PLIST" 2>/dev/null || true
    fi
fi

rm -f "$PLIST"
rm -rf "$APP"

/usr/sbin/pkgutil --forget com.remotedesktop.host.app >/dev/null 2>&1 || true
/usr/sbin/pkgutil --forget com.remotedesktop.host.agent >/dev/null 2>&1 || true

echo "==> Done. Remote Desktop Host is gone; per-user data and the trusted server certificate were left in place."
