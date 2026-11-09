#!/bin/bash
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
stub="$work/bin"
mkdir -p "$stub"
export PATH="$stub:$PATH"
export RD_STUB_LOG="$work/commands.log"
: >"$RD_STUB_LOG"

cat >"$work/debconf.sh" <<'EOF'
debconf_value() { printf '%s/%s' "$DEBCONF_STATE" "$1"; }
db_fget() {
    if [ -f "$(debconf_value "$1").seen" ]; then RET=$(cat "$(debconf_value "$1").seen"); else RET=false; fi
    return 0
}
db_set() { mkdir -p "$(dirname "$(debconf_value "$1")")"; printf '%s' "$2" >"$(debconf_value "$1")"; }
db_fset() { mkdir -p "$(dirname "$(debconf_value "$1")")"; printf '%s' "$3" >"$(debconf_value "$1").seen"; }
db_input() { return 0; }
db_go() { return 0; }
db_get() {
    if [ -f "$(debconf_value "$1")" ]; then RET=$(cat "$(debconf_value "$1")"); else RET=; fi
    return 0
}
db_stop() { return 0; }
EOF

cat >"$stub/dpkg-query" <<'EOF'
#!/bin/bash
if [ -n "${RD_STUB_INSTALLED_VERSION:-}" ]; then printf '%s\n' "$RD_STUB_INSTALLED_VERSION"; else exit 1; fi
EOF
cat >"$stub/getent" <<'EOF'
#!/bin/bash
exit 0
EOF
cat >"$stub/chown" <<'EOF'
#!/bin/bash
printf 'chown %s\n' "$*" >>"$RD_STUB_LOG"
exit 0
EOF
cat >"$stub/systemctl" <<'EOF'
#!/bin/bash
printf 'systemctl %s\n' "$*" >>"$RD_STUB_LOG"
if [ "${RD_STUB_SERVICE_FAIL:-}" = gateway ] && [[ "$*" == *"is-active"* ]] && [[ "$*" == *"remotedesktop-gateway.service"* ]]; then exit 1; fi
exit 0
EOF
for command in deb-systemd-helper deb-systemd-invoke update-ca-certificates; do
    cat >"$stub/$command" <<'EOF'
#!/bin/bash
exit 0
EOF
done
cat >"$stub/curl" <<'EOF'
#!/bin/bash
printf 'Healthy\n'
EOF
chmod +x "$stub/curl"
cat >"$stub/runuser" <<'EOF'
#!/bin/bash
while [ "$#" -gt 0 ]; do
    case "$1" in -u) shift 2 ;; --) shift; break ;; *) shift ;; esac
done
"$@"
EOF
chmod +x "$stub"/*

sed \
    -e 's|^\. /usr/share/debconf/confmodule$|. "$DEBCONF_STUB"|' \
    -e "s|^INSTALL_DIRECTORY=.*|INSTALL_DIRECTORY=$work/etc/remotedesktop-server|" \
    -e "s|^DEFAULTS=.*|DEFAULTS=$work/etc/default/remotedesktop-server|" \
    -e "s|^DEFAULT_DATABASE_PATH=.*|DEFAULT_DATABASE_PATH=$work/var/lib/remotedesktop-server/rendezvous.db|" \
    -e "s|^MARKER=.*|MARKER=$work/run/questions-asked|" \
    "$root/build/installer/linux/server/config" >"$work/server-config"
chmod +x "$work/server-config"

export DEBCONF_STUB="$work/debconf.sh"
run_config() {
    DEBCONF_STATE="$work/debconf-state" RD_STUB_INSTALLED_VERSION="${RD_STUB_INSTALLED_VERSION:-}" \
        "$work/server-config" configure "${1:-1.0.0}" >/dev/null
}
reset_answers() {
    rm -rf "$work/debconf-state"
    mkdir -p "$work/debconf-state/remotedesktop-server"
}

mkdir -p "$work/etc/remotedesktop-server/Rendezvous"
cat >"$work/etc/remotedesktop-server/Rendezvous/appsettings.json" <<'EOF'
{"Rendezvous":{"PostgresConnectionString":"Host=db;Database=rd;Username=rd;Password=test"}}
EOF
reset_answers
RD_STUB_INSTALLED_VERSION=1.0.0 run_config 1.0.0
grep -Fx postgres "$work/debconf-state/remotedesktop-server/database-provider" >/dev/null
echo 'PASS: legacy PostgreSQL settings remain PostgreSQL'

reset_answers
rm -f "$work/etc/remotedesktop-server/Rendezvous/appsettings.json"
run_config
grep -Fx sqlite "$work/debconf-state/remotedesktop-server/database-provider" >/dev/null
echo 'PASS: a fresh installation defaults to SQLite'

mkdir -p "$work/etc/default"
cat >"$work/etc/default/remotedesktop-server" <<'EOF'
RD_DATABASE=auto
RD_POSTGRES=Host=db;Database=rd;Username=rd;Password=test
EOF
reset_answers
run_config
grep -Fx postgres "$work/debconf-state/remotedesktop-server/database-provider" >/dev/null
echo 'PASS: an explicit PostgreSQL default selects PostgreSQL'

cat >"$work/etc/default/remotedesktop-server" <<'EOF'
RD_DATABASE=sqlite
RD_DATABASE_PATH=relative.db
EOF
reset_answers
if run_config >/dev/null 2>&1; then
    echo 'FAIL: a relative SQLite path was accepted' >&2
    exit 1
fi
echo 'PASS: a relative SQLite path fails before installation'

sed \
    -e 's|^\. /usr/share/debconf/confmodule$|. "$DEBCONF_STUB"|' \
    -e "s|^INSTALL_DIRECTORY=.*|INSTALL_DIRECTORY=$work/etc/remotedesktop-server|" \
    -e "s|^PROGRAM_DIRECTORY=.*|PROGRAM_DIRECTORY=$work/program|" \
    -e "s|^DEFAULTS=.*|DEFAULTS=$work/etc/default/remotedesktop-server|" \
    -e "s|^DEFAULT_DATABASE_DIRECTORY=.*|DEFAULT_DATABASE_DIRECTORY=$work/var/lib/remotedesktop-server|" \
    -e "s|^DEFAULT_DATABASE_PATH=.*|DEFAULT_DATABASE_PATH=$work/var/lib/remotedesktop-server/rendezvous.db|" \
    -e "s|^questions_marker=.*|questions_marker=$work/run/questions-asked|" \
    -e "s|/etc/systemd/system|$work/systemd|g" \
    -e "s|/var/log/remotedesktop-server|$work/log|g" \
    -e "s|/usr/local/share/ca-certificates/remotedesktop-signaling-server.crt|$work/trust/remotedesktop.crt|" \
    "$root/build/installer/linux/server/postinst" >"$work/server-postinst"
chmod +x "$work/server-postinst"

cat >"$work/program-stub" <<'EOF'
#!/bin/bash
install=
while [ "$#" -gt 0 ]; do
    case "$1" in --install-directory) install=$2; shift 2 ;; *) shift ;; esac
done
if [ -n "$install" ]; then
    for role in Rendezvous Relay Ops Gateway; do
        mkdir -p "$install/$role"
        printf '{}\n' >"$install/$role/appsettings.json"
    done
    printf 'stub setup\n' >"$install/server-setup.log"
fi
exit 0
EOF
chmod +x "$work/program-stub"
for role in Rendezvous Relay Ops Gateway; do
    mkdir -p "$work/program/$role"
    program="RemoteDesktop.Server.$role"
    cp "$work/program-stub" "$work/program/$role/$program"
done
mkdir -p "$work/trust"

custom="$work/shared/remotedesktop-data"
mkdir -p "$custom"
printf 'keep\n' >"$custom/sentinel"
rm -f "$work/etc/default/remotedesktop-server"
reset_answers
printf sqlite >"$work/debconf-state/remotedesktop-server/database-provider"
printf '%s' "$custom/rendezvous.db" >"$work/debconf-state/remotedesktop-server/database-path"
printf none >"$work/debconf-state/remotedesktop-server/tls-mode"
for question in postgres-connection tls-certificate tls-password tls-hostname; do
    : >"$work/debconf-state/remotedesktop-server/$question"
done

run_postinst() {
    DEBCONF_STATE="$work/debconf-state" DEBCONF_RECONFIGURE=1 RD_STUB_SERVICE_FAIL="${RD_STUB_SERVICE_FAIL:-}" \
        RD_URLS=http://127.0.0.1:5080 RD_RELAY_ENDPOINT=127.0.0.1:5082 RD_RELAY_KEY=auto \
        "$work/server-postinst" configure >/dev/null
}
run_postinst
grep -F 'keep' "$custom/sentinel" >/dev/null
grep -F "chown remotedesktop:remotedesktop $custom" "$RD_STUB_LOG" >/dev/null
if grep -F -- "-R remotedesktop:remotedesktop $custom" "$RD_STUB_LOG" >/dev/null; then
    echo 'FAIL: the custom database directory was recursively chowned' >&2
    exit 1
fi
echo 'PASS: a custom database directory preserves unrelated files and is not recursively chowned'

if RD_STUB_SERVICE_FAIL=gateway run_postinst >/dev/null 2>&1; then
    echo 'FAIL: an inactive Gateway service did not fail installation' >&2
    exit 1
fi
echo 'PASS: an inactive service fails the installation'

printf /etc/rendezvous.db >"$work/debconf-state/remotedesktop-server/database-path"
: >"$RD_STUB_LOG"
if run_postinst >/dev/null 2>&1; then
    echo 'FAIL: a system-directory database path was accepted' >&2
    exit 1
fi
if grep -F /etc "$RD_STUB_LOG" >/dev/null; then
    echo 'FAIL: an unsafe path caused an ownership operation' >&2
    exit 1
fi
echo 'PASS: a system-directory database path fails before ownership changes'

echo 'All isolated Linux installer semantics tests passed.'
