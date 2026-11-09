#!/bin/bash
#
# Shared by the client and host packages: reads the answers the Installer pane (or the unattended answers file) left,
# and writes the machine-wide signaling address and the trust for a self-signed server certificate.
#
# Sourced, not executed: the caller names the role (client or host) and keeps its own lifecycle steps around this.
#
# The address cannot be written into the signed .app: changing a file inside the bundle invalidates the signature,
# and macOS keys the screen-recording and accessibility grants to that signature. The machine-level file is read by
# the app as the layer between the shipped defaults and the person's own settings.
#

rd_plugin_answers() { printf '/var/tmp/com.remotedesktop.install.%s.plist' "$1"; }
rd_root_answers()   { printf '/var/tmp/com.remotedesktop.install.%s.root.plist' "$1"; }

# Moves the pane's answers out of the user-writable /var/tmp and into a root-owned copy. Called from preinstall.
rd_prepare_answers() {
    role="$1"
    plugin="$(rd_plugin_answers "$role")"
    root="$(rd_root_answers "$role")"
    if [ -L "$plugin" ] || { [ -e "$plugin" ] && [ ! -f "$plugin" ]; }; then
        echo "Remote Desktop: ignoring $plugin, it is not a regular answer file." >&2
        rm -f "$plugin" "$root"
    elif [ -f "$plugin" ]; then
        owner="$(stat -f '%u:%l' "$plugin" 2>/dev/null || true)"
        console_user="$(stat -f%Su /dev/console 2>/dev/null || true)"
        console_uid="$(id -u "$console_user" 2>/dev/null || true)"
        allowed_owner="0:${console_uid:-root-only}"
        if [ "${owner%:*}" != "0" ] && [ "${owner%:*}" != "${allowed_owner#*:}" ]; then
            echo "Remote Desktop: ignoring $plugin, its owner is not root or the current console user." >&2
            rm -f "$plugin" "$root"
            return 0
        fi
        if [ "${owner#*:}" != "1" ]; then
            echo "Remote Desktop: ignoring $plugin, answer files may not have hard links." >&2
            rm -f "$plugin" "$root"
            return 0
        fi

        # mktemp plus an atomic rename prevents a pre-created root-path symlink from becoming an install target.
        rm -f "$root"
        temporary="$(mktemp /var/tmp/com.remotedesktop.install."$role".root.XXXXXX)"
        install -m 600 -o root -g wheel "$plugin" "$temporary"
        mv -f "$temporary" "$root"
        rm -f "$plugin"
    else
        # No pane answers this time (installer -pkg, MDM). A root copy left by an earlier cancelled install must not
        # be applied to this one.
        rm -f "$root"
    fi
}

rd_answer() {
    [ -n "$ANSWERS" ] || return 0
    /usr/libexec/PlistBuddy -c "Print :$1" "$ANSWERS" 2>/dev/null || true
}

# Applies the answers for a role. Exits non-zero on a value that could not be honoured, so the Installer reports the
# failure instead of leaving an app pointed at the wrong server.
apply_rendezvous_answers() {
    role="$1"
    root_answers="$(rd_root_answers "$role")"
    fallback_answers="/Library/Application Support/RemoteDesktop/install-answers.plist"
    machine_dir="/Library/Application Support/RemoteDesktop"
    settings="$machine_dir/appsettings.json"
    certificate_dir="$machine_dir/certificates"
    certificate_copy="$certificate_dir/signaling.crt"

    ANSWERS=""
    cleanup=""
    if [ -f "$root_answers" ]; then
        ANSWERS="$root_answers"
        cleanup="$root_answers"
    elif [ -f "$fallback_answers" ]; then
        ANSWERS="$fallback_answers"
    fi

    address="$(rd_answer RendezvousUri)"
    certificate="$(rd_answer ServerCertificatePath)"
    if [ -z "$ANSWERS" ]; then
        address="${RD_RENDEZVOUS_URI:-}"
        certificate="${RD_SERVER_CERTIFICATE:-}"
    fi

    if [ -n "$address" ]; then
        case "$address" in
            ws://*|wss://*) ;;
            *)
                echo "Remote Desktop: the signaling address '$address' is not a ws:// or wss:// address." >&2
                exit 1
                ;;
        esac
        # The value is interpolated into a plist. The pane validates it with NSURLComponents; this is the same bar
        # for a hand-written answers file: a scheme, a host, an optional path, and no whitespace, control characters,
        # double quotes or backslashes anywhere. The case runs first because grep judges one line at a time and
        # would let a value with a second line through.
        address_ok=true
        case "$address" in
            *[[:space:]]*|*[[:cntrl:]]*|*'"'*|*'\'*) address_ok=false ;;
        esac
        if [ "$address_ok" = true ] &&
            ! printf '%s' "$address" | grep -Eq '^wss?://[^/[:space:]"\\]+(/[^[:space:]"\\]*)?$'; then
            address_ok=false
        fi
        if [ "$address_ok" = false ]; then
            echo "Remote Desktop: the signaling address '$address' is not an absolute ws:// or wss:// address." >&2
            exit 1
        fi
    fi

    if [ -n "$certificate" ]; then
        case "$address" in
            wss://*) ;;
            *)
                echo "Remote Desktop: a server certificate was given but the address is not wss://; it was not imported." >&2
                certificate=""
                ;;
        esac
    fi

    certificate_pin=""
    if [ -n "$certificate" ]; then
        case "$certificate" in
            /*) ;;
            *)
                echo "Remote Desktop: the server certificate path must be absolute (got '$certificate')." >&2
                exit 1
                ;;
        esac
        if [ ! -r "$certificate" ]; then
            echo "Remote Desktop: the server certificate '$certificate' cannot be read." >&2
            exit 1
        fi

        install -d -m 755 -o root -g wheel "$certificate_dir"
        if openssl x509 -in "$certificate" -out "$certificate_copy" 2>/dev/null; then
            :
        elif openssl x509 -inform DER -in "$certificate" -out "$certificate_copy" 2>/dev/null; then
            :
        else
            echo "Remote Desktop: '$certificate' is not a PEM or DER certificate (pass the public half, not the key)." >&2
            exit 1
        fi
        chmod 0644 "$certificate_copy"
        chown root:wheel "$certificate_copy"
        certificate_pin="$certificate_copy"
    fi

    # An upgrade whose address did not change and whose pane left the certificate field empty must keep the pin. The
    # app cannot discover a self-signed server's certificate by itself, and dropping the pin on every reinstall would
    # turn a working wss:// deployment into one that cannot connect.
    if [ -z "$certificate_pin" ] && [ -f "$settings" ]; then
        existing_uri="$(plutil -extract Transport.RendezvousUri raw "$settings" 2>/dev/null || true)"
        existing_pin="$(plutil -extract Transport.RendezvousCertificatePath raw "$settings" 2>/dev/null || true)"
        if [ "$address" = "$existing_uri" ] && [ -n "$existing_pin" ] && [ -f "$existing_pin" ]; then
            certificate_pin="$existing_pin"
            echo "Remote Desktop: keeping the existing server certificate pin ($certificate_pin)"
        fi
    fi

    if [ -n "$address" ]; then
        install -d -m 755 -o root -g wheel "$machine_dir"

        # A fresh plist, converted to JSON, rather than sed on an existing file: the value is arbitrary and the
        # machine file is the installer's (the person's own file is the override).
        temporary="$(mktemp)"
        printf '%s\n' '<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict/></plist>' > "$temporary"
        /usr/libexec/PlistBuddy -c "Add :Transport dict" "$temporary" >/dev/null
        /usr/libexec/PlistBuddy -c "Add :Transport:RendezvousUri string $address" "$temporary" >/dev/null
        if [ -n "$certificate_pin" ]; then
            # The app pins exactly this certificate. It is deliberately not added to the System keychain: a package
            # postinstall has no user session, and macOS refuses the trust change without one.
            /usr/libexec/PlistBuddy -c "Add :Transport:RendezvousCertificatePath string $certificate_pin" "$temporary" >/dev/null
        fi
        plutil -convert json -o "$settings" "$temporary"
        chmod 0644 "$settings"
        chown root:wheel "$settings"
        rm -f "$temporary"
        echo "Remote Desktop: signaling address set to $address in $settings"
        if [ -n "$certificate_pin" ]; then
            echo "Remote Desktop: server certificate pinned at $certificate_pin"
        fi
    fi

    if [ -n "$cleanup" ]; then
        rm -f "$cleanup"
    fi
}
