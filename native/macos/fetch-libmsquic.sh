#!/bin/sh
#
# Stages the native MsQuic library, and the OpenSSL it links against, as one self-contained set of dylibs that can be
# copied next to an executable.
#
# Why this exists: System.Net.Quic needs libmsquic at run time. Windows gets msquic.dll from the .NET runtime pack;
# macOS gets nothing, so the application has to bring its own. Homebrew's bottle is the only prebuilt arm64 build, but
# it is built for the Homebrew prefix and links OpenSSL by absolute path, so it cannot be shipped as it stands. This
# rewrites the library's own identity and its references to @loader_path — the directory the library was loaded from —
# which makes the staged set work wherever it is copied.
#
# Which files that set holds is read off the binaries rather than written down, and the set is dlopen'd before this
# script finishes. Both exist because of the same shipped bug: the bottle in the CI runner linked libcrypto.3 while
# the script staged openssl@4's libcrypto.4, and every check up to a real load — files present, plausible otool
# output, signatures applied — passed for a set that could not load at all. The runtime reported it as "QUIC is not
# supported", with nothing to point at the cause.
#
# The result is cached in artifacts/lib/macos (artifacts/ is already ignored), and the transport project copies it
# into every build output that needs it. Run it once per checkout:
#
#     native/macos/fetch-libmsquic.sh
#
# Homebrew's bottles are built for the newest macOS and record that as their minimum, so whatever is produced here
# runs on that macOS or newer. See docs/ports/README.md, "最低系统版本是 macOS 26", before shipping it to older systems.
set -eu

root=$(cd "$(dirname "$0")/../.." && pwd)
out="$root/artifacts/lib/macos"
. "$root/native/macos/native-dependencies.lock"
versions="$MSQUIC_VERSION/$OPENSSL_VERSION/$MSQUIC_TAG/$OPENSSL_TAG"

if [ -f "$out/libmsquic.dylib" ] && [ -f "$out/.native-versions" ] && [ "$(cat "$out/.native-versions")" = "$versions" ]; then
    missing=$(for reference in $(otool -L "$out/libmsquic.dylib" | tail -n +2 | awk '{ print $1 }' | grep -E '@@HOMEBREW_|^/opt/homebrew/|^/usr/local/|^@loader_path/' || true); do
        [ -f "$out/$(basename "$reference")" ] || echo "$(basename "$reference")"
    done)
    if [ -z "$missing" ]; then
        echo "Already staged in $out"
        exit 0
    fi

    echo "The staged set in $out is missing $missing; staging it again" >&2
fi

for tool in python3 curl tar shasum install_name_tool codesign clang; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "Missing required tool: $tool" >&2
        exit 1
    }
done

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# Downloads one explicitly locked bottle and unpacks it, printing the directory holding its libraries.
fetch_bottle() {
    formula=$1
    url=$2
    sha=$3

    # Homebrew serves bottles from a registry, so the blob needs a pull token scoped to the formula.
    repository=$(printf '%s' "$url" | sed -n 's|https://ghcr.io/v2/\(.*\)/blobs/.*|\1|p')
    token=$(curl -fsSL "https://ghcr.io/token?scope=repository:$repository:pull&service=ghcr.io" |
        python3 -c 'import json, sys; print(json.load(sys.stdin)["token"])')

    tarball="$work/$formula.tar.gz"
    printf '%s  %s\n' "$sha" "$tarball" >"$work/$formula.sha256"
    curl -fsSL -H "Authorization: Bearer $token" -o "$tarball" "$url"
    (cd "$work" && shasum -a 256 -c "$formula.sha256" >/dev/null)

    mkdir -p "$work/$formula"
    tar -xzf "$tarball" -C "$work/$formula"
    find "$work/$formula" -type d -name lib | head -1
}

# Points every Homebrew dependency at the copy that will sit next to this library. Bottles refer to each other by
# placeholder (@@HOMEBREW_PREFIX@@, @@HOMEBREW_CELLAR@@) as well as by the paths those placeholders expand to.
rewrite_homebrew_dependencies() {
    file=$1
    dependencies=$(otool -L "$file" | tail -n +2 | awk '{print $1}')
    for dependency in $dependencies; do
        case "$dependency" in
            /opt/homebrew/* | /usr/local/* | @@HOMEBREW_*)
                install_name_tool -change "$dependency" "@loader_path/$(basename "$dependency")" "$file"
                ;;
        esac
    done
}

libmsquic_lib=$(fetch_bottle libmsquic "$MSQUIC_URL" "$MSQUIC_SHA256")
openssl_lib=$(fetch_bottle "$OPENSSL_FORMULA" "$OPENSSL_URL" "$OPENSSL_SHA256")

mkdir -p "$out"
rm -f "$out"/*.dylib

# -L follows the bottle's symlinks, so each staged file is a real library under a plain name.
cp -L "$libmsquic_lib/libmsquic.dylib" "$out/libmsquic.dylib"

# Everything the libraries reference has to sit next to them, under the name the reference uses. The references
# are read back off the staged files, so the pass repeats until it adds nothing — a dependency of a dependency is
# still a dependency. (A file's own identity shows up in otool's output too and matches itself, which is why an
# already-staged name is skipped rather than fetched again.)
#
# This runs before the rewrite below, so a reference is still in the bottle's own form — a Homebrew placeholder or
# an explicit /opt/homebrew path — and not yet @loader_path. Both shapes are matched: reading only the rewritten
# one found nothing at all, which is how a set with a missing library sailed through every check up to the dlopen
# at the end.
for round in 1 2 3; do
    for file in "$out"/*.dylib; do
        for reference in $(otool -L "$file" | tail -n +2 | awk '{ print $1 }' | grep -E '@@HOMEBREW_|^/opt/homebrew/|^/usr/local/|^@loader_path/' || true); do
            name=$(basename "$reference")

            # The first reference of the output is the library's own identity, which has one of those shapes too;
            # it is staged already, so the check below passes it over.
            [ -f "$out/$name" ] && continue
            source=$(find "$libmsquic_lib" "$openssl_lib" -maxdepth 1 -name "$name" -print -quit)
            if [ -z "$source" ]; then
                echo "$(basename "$file") references $name, which none of the fetched bottles hold" >&2
                exit 1
            fi
            cp -L "$source" "$out/$name"
            echo "  staged $name (referenced by $(basename "$file"))"
        done
    done
done

chmod u+w "$out"/*.dylib

# The identity first: it is what a dependent records as the dependency, and rewriting it also renames the library's
# own entry in its load commands, which is why it must happen before the references are touched.
for file in "$out"/*.dylib; do
    install_name_tool -id "@loader_path/$(basename "$file")" "$file"
done

for file in "$out"/*.dylib; do
    rewrite_homebrew_dependencies "$file"
done

# Editing load commands invalidates the signature, and macOS refuses to load an arm64 binary whose signature is
# stale, so each one is signed again. Ad-hoc is enough: these are not the app's own code.
codesign --force --sign - "$out"/*.dylib

# The staged files are only worth anything if they load. The check is a tiny program compiled here rather than
# `python3`, because the interpreters macOS ships are platform binaries, and a platform binary enforces library
# validation — it would refuse a locally signed library whatever the library's state, which is a false alarm rather
# than a check.
cat >"$work/check-load.c" <<'EOF'
#include <dlfcn.h>
#include <stdio.h>

int main(int argc, char **argv)
{
    int failed = 0;
    for (int i = 1; i < argc; i++)
    {
        if (dlopen(argv[i], RTLD_NOW) != NULL)
        {
            printf("  %s: loads\n", argv[i]);
        }
        else
        {
            fprintf(stderr, "  %s: %s\n", argv[i], dlerror());
            failed++;
        }
    }

    return failed;
}
EOF
clang -o "$work/check-load" "$work/check-load.c"
(cd "$out" && "$work/check-load" ./*.dylib)
printf '%s\n' "$versions" >"$out/.native-versions"

echo
echo "Staged in $out:"
for file in "$out"/*.dylib; do
    echo "  $(basename "$file")"
    otool -L "$file" | tail -n +2 | sed 's/^/    /'
done
