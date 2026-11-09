#!/bin/bash
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
stub="$work/bin"
mkdir -p "$stub" "$work/var/tmp" "$work/LaunchDaemons"
export PATH="$stub:$PATH"
export RD_STUB_LOG="$work/commands.log"
: >"$RD_STUB_LOG"

cat >"$stub/install" <<'EOF'
#!/bin/bash
args=()
while [ "$#" -gt 0 ]; do
    case "$1" in -o|-g) shift 2 ;; *) args+=("$1"); shift ;; esac
done
/usr/bin/install "${args[@]}"
EOF
cat >"$stub/stat" <<'EOF'
#!/bin/bash
if [[ "$*" == *"/dev/console"* ]]; then
    printf '%s\n' "${RD_STUB_CONSOLE_USER:-root}"
else
    printf '%s:%s\n' "${RD_STUB_ANSWER_UID:-0}" "${RD_STUB_ANSWER_LINKS:-1}"
fi
EOF
cat >"$stub/id" <<'EOF'
#!/bin/bash
printf '500\n'
EOF
cat >"$stub/chown" <<'EOF'
#!/bin/bash
printf 'chown %s\n' "$*" >>"$RD_STUB_LOG"
EOF
cat >"$stub/dscl" <<'EOF'
#!/bin/bash
exit 0
EOF
cat >"$stub/sudo" <<'EOF'
#!/bin/bash
exit 0
EOF
cat >"$stub/curl" <<'EOF'
#!/bin/bash
printf 'Healthy\n'
EOF
cat >"$stub/launchctl" <<'EOF'
#!/bin/bash
printf 'launchctl %s\n' "$*" >>"$RD_STUB_LOG"
if [[ "$1" == bootstrap ]] && [[ "$*" == *"${RD_STUB_LAUNCH_FAIL:-nothing}"* ]]; then exit 1; fi
if [[ "$1" == print ]] && [[ "$*" == *"${RD_STUB_PRINT_FAIL:-nothing}"* ]]; then exit 1; fi
if [[ "$1" == print ]]; then printf 'state = running\n'; fi
exit 0
EOF
cat >"$stub/PlistBuddy" <<'EOF'
#!/bin/bash
printf 'PlistBuddy %s\n' "$*" >>"$RD_STUB_LOG"
exit 0
EOF
cat >"$stub/plutil" <<'EOF'
#!/usr/bin/python3
import json
import sys

if len(sys.argv) < 4 or sys.argv[1] != '-extract':
    sys.exit(1)
try:
    with open(sys.argv[-1], encoding='utf-8') as source:
        value = json.load(source)
    for part in sys.argv[2].split('.'):
        value = value[part]
    if isinstance(value, bool):
        print('true' if value else 'false')
    elif value is not None:
        print(value)
except Exception:
    sys.exit(1)
EOF
chmod +x "$stub"/*

cp -R "$root/build/installer/macos/client/scripts" "$work/client-scripts"
cp -R "$root/build/installer/macos/server/scripts" "$work/server-scripts"
sed "s|/var/tmp|$work/var/tmp|g" "$work/client-scripts/apply-answers.sh" >"$work/apply-answers.tmp"
mv "$work/apply-answers.tmp" "$work/client-scripts/apply-answers.sh"
sed -e "s|/var/tmp|$work/var/tmp|g" -e "s|/Library/LaunchDaemons|$work/LaunchDaemons|g" \
    "$work/server-scripts/preinstall" >"$work/server-preinstall.tmp"
mv "$work/server-preinstall.tmp" "$work/server-scripts/preinstall"
chmod +x "$work/server-scripts/preinstall"

run_preinstall() {
    local role=$1
    RD_STUB_ANSWER_UID="${RD_STUB_ANSWER_UID:-0}" RD_STUB_ANSWER_LINKS="${RD_STUB_ANSWER_LINKS:-1}" \
        RD_STUB_CONSOLE_USER="${RD_STUB_CONSOLE_USER:-alice}" "$work/$role-scripts/preinstall" >/dev/null
}

printf 'client-answer\n' >"$work/var/tmp/com.remotedesktop.install.client.plist"
RD_STUB_ANSWER_UID=600 run_preinstall client
test ! -e "$work/var/tmp/com.remotedesktop.install.client.root.plist"
test ! -e "$work/var/tmp/com.remotedesktop.install.client.plist"
echo 'PASS: the client preinstall rejects answers owned by another local user'

printf 'client-answer\n' >"$work/var/tmp/com.remotedesktop.install.client.plist"
RD_STUB_ANSWER_UID=500 run_preinstall client
test "$(cat "$work/var/tmp/com.remotedesktop.install.client.root.plist")" = client-answer
test "$(python3 -c 'import os, stat, sys; print(format(stat.S_IMODE(os.stat(sys.argv[1]).st_mode), "o"))' "$work/var/tmp/com.remotedesktop.install.client.root.plist")" = 600
echo 'PASS: the client preinstall accepts the console user and creates a 0600 root copy'

printf 'server-answer\n' >"$work/var/tmp/com.remotedesktop.install.server.plist"
RD_STUB_ANSWER_UID=600 RD_STUB_ANSWER_LINKS=1 run_preinstall server
test ! -e "$work/var/tmp/com.remotedesktop.install.server.root.plist"
echo 'PASS: the server preinstall rejects another user before launchctl operations'

printf 'server-answer\n' >"$work/var/tmp/com.remotedesktop.install.server.plist"
RD_STUB_ANSWER_UID=500 RD_STUB_ANSWER_LINKS=1 run_preinstall server
test "$(cat "$work/var/tmp/com.remotedesktop.install.server.root.plist")" = server-answer
echo 'PASS: the server preinstall accepts a valid console answer'

printf 'do-not-copy\n' >"$work/answer-target"
ln -s "$work/answer-target" "$work/var/tmp/com.remotedesktop.install.server.plist"
rm -f "$work/var/tmp/com.remotedesktop.install.server.root.plist"
RD_STUB_ANSWER_UID=0 run_preinstall server
test "$(cat "$work/answer-target")" = do-not-copy
test ! -e "$work/var/tmp/com.remotedesktop.install.server.root.plist"
echo 'PASS: a symbolic-link answer is ignored without copying its target'

sed \
    -e "s|^INSTALL_DIR=.*|INSTALL_DIR=\"$work/install\"|" \
    -e "s|^ROOT_ANSWERS=.*|ROOT_ANSWERS=\"$work/var/tmp/server.root.plist\"|" \
    -e "s|^FALLBACK_ANSWERS=.*|FALLBACK_ANSWERS=\"$work/fallback.plist\"|" \
    -e "s|^SUMMARY=.*|SUMMARY=\"$work/install-summary.plist\"|" \
    -e "s|^DOC_DIR=.*|DOC_DIR=\"$work/doc\"|" \
    -e "s|/usr/libexec/PlistBuddy|$stub/PlistBuddy|g" \
    -e "s|/usr/bin/sudo|$stub/sudo|g" \
    -e "s|/usr/bin/curl|$stub/curl|g" \
    -e "s|/Library/LaunchDaemons|$work/LaunchDaemons|g" \
    "$root/build/installer/macos/server/scripts/postinstall" >"$work/server-postinstall"
chmod +x "$work/server-postinstall"

cat >"$work/program-stub" <<'EOF'
#!/bin/bash
install=
while [ "$#" -gt 0 ]; do
    case "$1" in --install-directory) install=$2; shift 2 ;; *) shift ;; esac
done
if [ -n "$install" ]; then
    database_path=${RD_TEST_DATABASE_PATH:-$install/var/rendezvous.db}
    mkdir -p "$install/Rendezvous" "$install/Relay" "$install/Ops" "$install/Gateway"
    cat >"$install/Rendezvous/appsettings.json" <<JSON
{"Urls":"http://127.0.0.1:5080","Rendezvous":{"Database":{"Provider":"sqlite","Path":"$database_path"}}}
JSON
    cat >"$install/Ops/appsettings.json" <<JSON
{"Ops":{"Listen":"http://127.0.0.1:5090","Database":{"Provider":"sqlite","Path":"$database_path"}}}
JSON
    printf '{}\n' >"$install/Relay/appsettings.json"
    printf '{}\n' >"$install/Gateway/appsettings.json"
    printf 'stub setup\n' >"$install/server-setup.log"
fi
exit 0
EOF
chmod +x "$work/program-stub"
for role in Rendezvous Relay Ops Gateway; do
    mkdir -p "$work/install/$role"
    printf '{}\n' >"$work/install/$role/appsettings.default.json"
    cp "$work/program-stub" "$work/install/$role/RemoteDesktop.Server.$role"
done
for label in com.remotedesktop.rendezvous com.remotedesktop.relay com.remotedesktop.ops com.remotedesktop.gateway; do
    printf 'plist\n' >"$work/LaunchDaemons/$label.plist"
done

run_postinstall() {
    RD_STUB_LAUNCH_FAIL="${RD_STUB_LAUNCH_FAIL:-}" RD_STUB_PRINT_FAIL="${RD_STUB_PRINT_FAIL:-}" \
        RD_TEST_DATABASE_PATH="${RD_TEST_DATABASE_PATH:-}" "$work/server-postinstall" >/dev/null
}
custom="$work/shared/remotedesktop-custom"
mkdir -p "$custom"
printf 'keep\n' >"$custom/sentinel"
RD_TEST_DATABASE_PATH="$custom/rendezvous.db" run_postinstall
grep -F keep "$custom/sentinel" >/dev/null
grep -F "chown _remotedesktop:_remotedesktop $custom" "$RD_STUB_LOG" >/dev/null
if grep -F -- "-R _remotedesktop:_remotedesktop $custom" "$RD_STUB_LOG" >/dev/null; then
    echo 'FAIL: the custom macOS database directory was recursively chowned' >&2
    exit 1
fi
echo 'PASS: the macOS postinstall prepares a custom database directory without recursion'

: >"$RD_STUB_LOG"
if RD_TEST_DATABASE_PATH="$custom/rendezvous.db" RD_STUB_LAUNCH_FAIL=gateway run_postinstall >/dev/null 2>&1; then
    echo 'FAIL: a launchd bootstrap failure was reported as success' >&2
    exit 1
fi
echo 'PASS: a launchd bootstrap failure returns nonzero'

: >"$RD_STUB_LOG"
if RD_TEST_DATABASE_PATH="$work/shared/not-special/rendezvous.db" run_postinstall >/dev/null 2>&1; then
    echo 'FAIL: a non-dedicated database directory was accepted' >&2
    exit 1
fi
if grep -F 'bootstrap' "$RD_STUB_LOG" >/dev/null; then
    echo 'FAIL: services were started after database validation failed' >&2
    exit 1
fi
echo 'PASS: an invalid database directory fails before any daemon is bootstrapped'

echo 'All isolated macOS installer semantics tests passed.'
