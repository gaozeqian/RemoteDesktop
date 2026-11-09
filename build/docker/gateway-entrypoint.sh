#!/bin/sh
#
# Starts the Ops HTTPS gateway, creating its self-signed certificate on first use.
#
# The address in the certificate is the address an administrator uses in the browser. Inside a container the
# automatically discovered address is the container's bridge address, which is useless outside that bridge, so it is
# explicit: OPS_GATEWAY_IP must be the Docker host's reachable IPv4 address (or a NAT address in front of it).
set -eu

certificate=${OPS_GATEWAY_CERT_PATH:-/data/gateway/tls/ops-gateway.crt}
key=${OPS_GATEWAY_KEY_PATH:-/data/gateway/tls/ops-gateway.key}
state=${OPS_GATEWAY_STATE_PATH:-/data/gateway/tls/ops-gateway.san}
ip=$(printf '%s' "${OPS_GATEWAY_IP:-}" | tr -d '[:space:]')
dns=${OPS_GATEWAY_DNS:-}

# Point Kestrel at the file this script generates unless the deployment supplied its own certificate settings. Keeping
# this here means `docker run` needs only OPS_GATEWAY_IP, while an operator can still override every path.
if [ -z "${Kestrel__Certificates__Default__Path:-}" ]; then
    Kestrel__Certificates__Default__Path=$certificate
    export Kestrel__Certificates__Default__Path
fi
if [ -z "${Kestrel__Certificates__Default__KeyPath:-}" ]; then
    Kestrel__Certificates__Default__KeyPath=$key
    export Kestrel__Certificates__Default__KeyPath
fi

if [ -z "$ip" ]; then
    echo "OPS_GATEWAY_IP is required: set it to the IPv4 address administrators use to open https://<IP>:5443." >&2
    exit 64
fi

case "$ip" in
    *[!0-9.]*|.*|*.)
        echo "OPS_GATEWAY_IP is not an IPv4 address: '$ip'." >&2
        exit 64
        ;;
esac

directory=$(dirname "$certificate")
mkdir -p "$directory"
chmod 0700 "$directory"

# SAN entries are the only names browsers verify. The CN is deliberately descriptive rather than an address.
san="IP:$ip"
old_ifs=$IFS
IFS=','
for name in $dns; do
    IFS=$old_ifs
    name=$(printf '%s' "$name" | tr -d '[:space:]')
    if [ -n "$name" ]; then
        san="$san,DNS:$name"
    fi
    IFS=','
done
IFS=$old_ifs

desired=$(printf '%s\n' "$san" | sha256sum | cut -d' ' -f1)
matching_pair=false
if [ -s "$certificate" ] && [ -s "$key" ]; then
    certificate_public=$(openssl x509 -pubkey -noout -in "$certificate" 2>/dev/null | openssl pkey -pubin -outform pem 2>/dev/null)
    key_public=$(openssl pkey -pubout -in "$key" 2>/dev/null)
    if [ -n "$certificate_public" ] && [ "$certificate_public" = "$key_public" ]; then
        matching_pair=true
    fi
fi

reuse=false
if [ "$matching_pair" = true ] && [ -f "$state" ] \
    && [ "$(cat "$state")" = "$desired" ] \
    && openssl x509 -checkend 2592000 -noout -in "$certificate" >/dev/null 2>&1; then
    reuse=true
fi

if [ "$reuse" = false ]; then
    temporary_key=$(mktemp "$key.XXXXXX")
    temporary_certificate=$(mktemp "$certificate.XXXXXX")
    state_temporary=$(mktemp "$state.XXXXXX")
    trap 'rm -f "$temporary_key" "$temporary_certificate" "$state_temporary"' EXIT

    echo "Generating the Ops gateway certificate for $san."
    if ! openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 1095 \
        -keyout "$temporary_key" \
        -out "$temporary_certificate" \
        -subj "/CN=Remote Desktop Ops Gateway" \
        -addext "subjectAltName=$san" \
        -addext "basicConstraints=critical,CA:FALSE" \
        -addext "keyUsage=critical,digitalSignature,keyEncipherment" \
        -addext "extendedKeyUsage=serverAuth" >/dev/null 2>&1; then
        echo "The Ops gateway certificate could not be generated; check OPS_GATEWAY_IP and OPS_GATEWAY_DNS." >&2
        rm -f "$temporary_key" "$temporary_certificate"
        exit 1
    fi

    chmod 0600 "$temporary_key"
    chmod 0644 "$temporary_certificate"

    # Reuse also checks the public-key pair, so an interruption between these moves is detected and repaired next start.
    mv "$temporary_key" "$key"
    mv "$temporary_certificate" "$certificate"
    printf '%s\n' "$desired" >"$state_temporary"
    mv "$state_temporary" "$state"
    chmod 0600 "$state"
    trap - EXIT
else
    echo "Using the existing Ops gateway certificate for $san."
fi

exec dotnet RemoteDesktop.Server.Gateway.dll
