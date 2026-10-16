#!/usr/bin/env bash
#
# Creates a self-signed code-signing identity for local development.
#
# Why this exists: signing a .app needs a *stable* identity. Ad-hoc signatures (`codesign -s -`) change on every
# build, so macOS treats each build as a different program -- TCC grants are lost on every rebuild, and keychain
# items written by a previous build become "another app's" items. The right answer is a real Apple Development
# certificate (see docs/ports/macos-port-plan.md 3.9), but obtaining one needs Xcode plus a signed-in Apple ID. This
# script is the stand-in for machines that have neither.
#
# It is NOT a substitute for an Apple Development certificate. A self-signed certificate carries no Apple-issued
# Team ID, so TCC (screen recording, accessibility, camera) will still re-prompt. It does make the signature
# stable, which is what the packaging script and the keychain round trip actually need.
#
# Usage:
#   ./make-dev-cert.sh                     Create the identity, or report the healthy one that already exists.
#   ./make-dev-cert.sh --name "My Cert"    Use a different common name.
#   ./make-dev-cert.sh --print-identity    Print only the identity name, for use in a script.
#   ./make-dev-cert.sh --remove            Delete every certificate with this common name, plus its trust setting.
#   ./make-dev-cert.sh -h | --help         This text.
#
# Environment:
#   KEYCHAIN   Keychain to install into. Defaults to the login keychain.
#
# Three things here are not obvious and were each learned the hard way:
#
#   1. The PKCS#12 must use legacy PBE algorithms. OpenSSL 4's defaults produce a MAC that macOS's Security
#      framework rejects, and `security import` then reports "MAC verification failed during PKCS12 import
#      (wrong password?)" even though the password is right.
#   2. The certificate must be added to the trust settings explicitly. Without it the certificate is present but
#      reports CSSMERR_TP_NOT_TRUSTED, and `security find-identity -v` still says "0 valid identities found".
#   3. There must never be two certificates with the same common name. Once there are, `codesign --sign <name>`
#      and `security delete-certificate -c <name>` both fail with "ambiguous, matches more than one certificate".
#      That is why everything below works off SHA-1 hashes and why `create` cleans up before it generates.

set -euo pipefail

NAME="Remote Desktop Development"
KEYCHAIN="${KEYCHAIN:-$HOME/Library/Keychains/login.keychain-db}"
VALID_DAYS=3650
MODE="create"

usage() {
    sed -n '2,/^set -euo pipefail/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --name)
            [[ $# -ge 2 ]] || { echo "error: --name needs a value" >&2; exit 2; }
            NAME="$2"
            shift 2
            ;;
        --print-identity) MODE="print"; shift ;;
        --remove)         MODE="remove"; shift ;;
        -h|--help)        usage; exit 0 ;;
        *) echo "error: unknown argument '$1' (try --help)" >&2; exit 2 ;;
    esac
done

# Every certificate whose common name matches, as SHA-1 hashes -- trusted or not, with or without a private key.
# `find-identity` is the narrower net: it only lists certificates that still have their key. Deleting has to reach
# the orphaned ones too, otherwise the common name stays ambiguous.
certificate_hashes() {
    security find-certificate -a -c "$NAME" -Z "$KEYCHAIN" 2>/dev/null |
        sed -n 's/^ *SHA-1 hash: *\([0-9A-F]\{40\}\)$/\1/p'
}

# The name, but only when the identity is actually usable for signing.
valid_identity() {
    security find-identity -v -p codesigning "$KEYCHAIN" 2>/dev/null |
        sed -n "s/.*\"\\($NAME\\)\".*/\\1/p" | head -1
}

count_certificates() {
    certificate_hashes | grep -c . || true
}

# Removes every certificate with this common name. -Z (hash) is the only unambiguous form; -t drops the trust
# setting as well, which is what makes the name usable for signing again.
drain() {
    local hash
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        hash="$(certificate_hashes | head -1)"
        [[ -n "$hash" ]] || break
        security delete-identity -Z "$hash" "$KEYCHAIN" >/dev/null 2>&1 || true
        security delete-certificate -Z "$hash" -t "$KEYCHAIN" >/dev/null 2>&1 || true
    done
}

if [[ "$MODE" == "print" ]]; then
    valid_identity
    exit 0
fi

if [[ "$MODE" == "remove" ]]; then
    if [[ "$(count_certificates)" == "0" ]]; then
        echo "No '$NAME' certificate in $KEYCHAIN; nothing to remove."
        exit 0
    fi

    drain

    if [[ "$(count_certificates)" == "0" ]]; then
        echo "Removed '$NAME' from $KEYCHAIN."
    else
        echo "Could not fully remove '$NAME'; check Keychain Access." >&2
        exit 1
    fi
    exit 0
fi

# ---------------------------------------------------------------- create

# Only an untouched, working identity counts as "already present". Anything else -- untrusted, duplicated, orphaned
# -- is cleaned out first, because a duplicated common name breaks `codesign --sign <name>` outright.
if [[ "$(count_certificates)" == "1" && -n "$(valid_identity)" ]]; then
    echo "Already present in $KEYCHAIN: $NAME"
    echo "$NAME"
    exit 0
fi

if [[ "$(count_certificates)" != "0" ]]; then
    echo "Cleaning up $(count_certificates) unusable certificate(s) named '$NAME' first."
    drain
fi

if ! command -v openssl >/dev/null 2>&1; then
    echo "error: openssl is required to generate the certificate." >&2
    exit 1
fi

if [[ ! -f "$KEYCHAIN" ]]; then
    echo "error: keychain not found: $KEYCHAIN" >&2
    exit 1
fi

WORK_DIR="$(mktemp -d)"
# The private key lands here first, so keep the directory to the owner.
umask 077
trap 'rm -rf "$WORK_DIR"' EXIT

PASSWORD="$(openssl rand -hex 24)"

echo "Generating a self-signed code-signing certificate:"
echo "    common name : $NAME"
echo "    validity    : $VALID_DAYS days"
echo "    keychain    : $KEYCHAIN"

openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days "$VALID_DAYS" \
    -keyout "$WORK_DIR/key.pem" -out "$WORK_DIR/cert.pem" \
    -subj "/CN=$NAME/O=Remote Desktop Development/C=CN" \
    -addext "basicConstraints=critical,CA:FALSE" \
    -addext "keyUsage=critical,digitalSignature" \
    -addext "extendedKeyUsage=critical,codeSigning" \
    -addext "subjectKeyIdentifier=hash" 2>/dev/null

# Legacy PBE algorithms are mandatory; see note 1 at the top of this file.
openssl pkcs12 -export -out "$WORK_DIR/identity.p12" \
    -inkey "$WORK_DIR/key.pem" -in "$WORK_DIR/cert.pem" -name "$NAME" \
    -passout "pass:$PASSWORD" \
    -certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1

# -A lets any process use the key without a prompt. That is acceptable for a throwaway development certificate with
# no Apple-issued identity behind it, and it is what keeps a headless build from hanging on a dialog.
security import "$WORK_DIR/identity.p12" -k "$KEYCHAIN" -P "$PASSWORD" \
    -T /usr/bin/codesign -T /usr/bin/security -A >/dev/null

# See note 2: without an explicit trust setting the certificate is imported but unusable.
security add-trusted-cert -r trustRoot -p codeSign -k "$KEYCHAIN" "$WORK_DIR/cert.pem"

if [[ -z "$(valid_identity)" ]]; then
    echo "error: the certificate was imported but is still not a valid signing identity." >&2
    echo "       Check Keychain Access > login > Certificates > '$NAME' > Trust." >&2
    exit 1
fi

# Prove the name resolves to exactly one identity, which is the failure mode duplicates produce. Counting is used
# rather than trying `codesign --sign` on a scratch file, because that would depend on codesign's rules for
# non-Mach-O input rather than on the thing actually being checked. `sort -u` is required: without -v,
# `find-identity` prints both a "Matching identities" and a "Valid identities only" section, so every identity
# would otherwise be counted twice.
matching="$(security find-identity -p codesigning "$KEYCHAIN" 2>/dev/null |
    sed -n "s/^ *[0-9]*) \([0-9A-F]\{40\}\) \"$NAME\".*/\1/p" | sort -u | grep -c . || true)"
if [[ "$matching" != "1" ]]; then
    echo "warning: $matching identities resolve to '$NAME'; codesign will refuse with \"ambiguous\"." >&2
    echo "         Pass a SHA-1 instead: APPLE_DEVELOPMENT_IDENTITY=$(certificate_hashes | head -1)" >&2
fi

echo
echo "Done. '$NAME' is now a valid code-signing identity."
echo
echo "Use it for packaging:"
echo "    APPLE_DEVELOPMENT_IDENTITY=\"$NAME\" ./build/installer/macos/client/make-app.sh"
