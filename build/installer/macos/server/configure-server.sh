#!/bin/bash
#
# Changes what the installed servers are configured with, after the installation.
#
# The installer asks for these once, when the package is built. This is the way back in: give it the settings you want
# to change and it writes them, then restarts the two launchd jobs so they take effect. Anything you leave out is left
# alone.
#
# It runs the same configuration step the package's postinstall runs
# (Rendezvous/RemoteDesktop.Server.Rendezvous --configure-system), so the two cannot drift apart, and what it did is
# appended to server-setup.log in this folder.
#
# Needs root: it writes the settings next to the programs and restarts the jobs.
#
# Usage:
#   sudo ./configure-server.sh [options]
#
#   --database <auto|none|postgres|sqlite>
#                                    where device codes are kept; auto keeps the PostgreSQL this machine already has
#                                    and otherwise starts a SQLite database of its own
#   --database-path <file>           the SQLite file (default: /usr/local/remotedesktop-server/var/rendezvous.db)
#   --postgres <connection string>   PostgreSQL connection string; '' stops using PostgreSQL
#   --relay-endpoint <ip:port>       where clients are told to reach the relay
#   --urls <http://0.0.0.0:5080>     the address signaling binds to; https:// switches it to TLS
#   --relay-key <base64>             the key relay tickets are signed with (must match a relay on another machine)
#   --tls-certificate <file.pfx>     serve TLS with this certificate
#   --tls-password <password>        its password
#   --ops-listen <url>               the monitoring console's address (default http://127.0.0.1:5090)
#   --ops-database-path <file>       the console's SQLite file (default .../var/ops.db)
#   --no-restart                     write the settings but leave the jobs running on the old ones
#
# Examples:
#   sudo ./configure-server.sh --database sqlite
#   sudo ./configure-server.sh --postgres 'Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=...'
#   sudo ./configure-server.sh --relay-endpoint 203.0.113.10:5082
#   sudo ./configure-server.sh --urls https://0.0.0.0:5080 --tls-certificate /etc/rd/signaling.pfx --tls-password pw
#
set -e

INSTALL_DIR="$(cd "$(dirname "$0")" && pwd)"
RENDEZVOUS="$INSTALL_DIR/Rendezvous/RemoteDesktop.Server.Rendezvous"

if [ "$(id -u)" -ne 0 ]; then
    echo "This needs root: it writes the servers' settings and restarts them." >&2
    echo "Try: sudo $0 $*" >&2
    exit 1
fi

# Consume the arguments into a list for the server. Collected first rather than passed straight through because the
# step reads both settings files, decides, and writes both — so it has to see every answer at once.
#
# An array and not `set --`: appending to the positional parameters while walking them is a loop that never ends.
# (The array is only ever expanded with "${args[@]}", never with set -u, so bash 3.2 — which is what /bin/bash is on
# macOS — is fine with an empty one.)
args=()
restart=1
while [ "$#" -gt 0 ]; do
    case "$1" in
        --database)        shift; args[${#args[@]}]="--database";       args[${#args[@]}]="$1" ;;
        --database-path)   shift; args[${#args[@]}]="--database-path";  args[${#args[@]}]="$1" ;;
        --postgres)        shift; args[${#args[@]}]="--postgres";        args[${#args[@]}]="$1" ;;
        --relay-endpoint)  shift; args[${#args[@]}]="--relay-endpoint";  args[${#args[@]}]="$1" ;;
        --urls)            shift; args[${#args[@]}]="--urls";            args[${#args[@]}]="$1" ;;
        --relay-key)       shift; args[${#args[@]}]="--relay-key";       args[${#args[@]}]="$1" ;;
        --tls-certificate) shift; args[${#args[@]}]="--tls-certificate"; args[${#args[@]}]="$1" ;;
        --tls-password)    shift; args[${#args[@]}]="--tls-password";    args[${#args[@]}]="$1" ;;
        --ops-listen)      shift; args[${#args[@]}]="--ops-listen";      args[${#args[@]}]="$1" ;;
        --ops-database-path) shift; args[${#args[@]}]="--ops-database-path"; args[${#args[@]}]="$1" ;;
        --no-restart)      restart=0 ;;
        -h|--help)         sed -n '2,/^set -e/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown option: $1 (try --help)" >&2; exit 2 ;;
    esac
    shift
done

echo "==> Writing the servers' settings"
if [ "${#args[@]}" -gt 0 ]; then
    "$RENDEZVOUS" --configure-system --install-directory "$INSTALL_DIR" "${args[@]}"
else
    "$RENDEZVOUS" --configure-system --install-directory "$INSTALL_DIR"
fi

if [ "$restart" -eq 0 ]; then
    echo "==> Not restarting (--no-restart): the new settings take effect on the next start"
    exit 0
fi

echo "==> Restarting the servers"
# The console restarts too: the database provider is shared with the signaling server, so a --database or --postgres
# change rewrites the console's settings as well. Leaving it running would keep the old database (and make history
# look as if it had disappeared) until somebody restarted it by hand.
for label in com.remotedesktop.rendezvous com.remotedesktop.relay com.remotedesktop.ops; do
    # kickstart -k restarts a running job and starts a stopped one, which is what "restart the service" means on
    # Windows. `launchctl stop` alone would only stop it, and KeepAlive would bring it back on the old settings.
    launchctl kickstart -k "system/$label" || echo "  warning: could not restart $label" >&2
done

echo "==> Done. Health: curl http://127.0.0.1:5080/health"
