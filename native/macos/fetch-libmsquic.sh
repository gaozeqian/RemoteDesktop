#!/bin/sh
#
# Stages the native MsQuic library, and the OpenSSL it links against, as one self-contained set of dylibs that can be
# copied next to an executable.
#
# Why this exists: System.Net.Quic needs libmsquic at run time. Windows gets msquic.dll from the .NET runtime pack;
# macOS gets nothing, so the application has to bring its own. Homebrew's bottle is the only prebuilt arm64 build, but
# it is built for the Homebrew prefix and links OpenSSL by absolute path, so it cannot be shipped as it stands. This
# rewrites the library's own identity and its OpenSSL references to @loader_path — the directory the library was
# loaded from — which makes the three files work wherever they are copied.
#
# The result is cached in artifacts/native/macos (artifacts/ is already ignored), and the transport project copies it
# into every build output that needs it. Run it once per checkout:
#
#     native/macos/fetch-libmsquic.sh
#
# Homebrew's bottles are built for the newest macOS and record that as their minimum, so whatever is produced here
# runs on that macOS or newer. See docs/macos-port-plan.md §3.4 before shipping it to older systems.
set -eu

root=$(cd "$(dirname "$0")/../.." && pwd)
out="$root/artifacts/native/macos"

if [ -f "$out/libmsquic.dylib" ] && [ -f "$out/libssl.4.dylib" ] && [ -f "$out/libcrypto.4.dylib" ]; then
    echo "Already staged in $out"
    exit 0
fi

for tool in brew python3 curl tar shasum install_name_tool codesign; do
    command -v "$tool" >/dev/null 2>&1 || {
        echo "Missing required tool: $tool" >&2
        exit 1
    }
done

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# Downloads the arm64 bottle of a formula and unpacks it, printing the directory holding its libraries.
fetch_bottle() {
    formula=$1

    info=$(brew info --json=v2 "$formula" | python3 -c '
import json, sys

formula = json.load(sys.stdin)["formulae"][0]
files = formula["bottle"]["stable"]["files"]
tags = [tag for tag in files if tag.startswith("arm64_")]
if not tags:
    sys.exit("no arm64 bottle for " + formula["name"])
bottle = files[tags[0]]
print(bottle["url"], bottle["sha256"])
')

    url=${info% *}
    sha=${info#* }

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

libmsquic_lib=$(fetch_bottle libmsquic)
openssl_lib=$(fetch_bottle openssl@4)

mkdir -p "$out"

# -L follows the bottle's symlinks, so each staged file is a real library under a plain name.
cp -L "$libmsquic_lib/libmsquic.dylib" "$out/libmsquic.dylib"
cp -L "$openssl_lib/libssl.dylib" "$out/libssl.4.dylib"
cp -L "$openssl_lib/libcrypto.dylib" "$out/libcrypto.4.dylib"
chmod u+w "$out"/*.dylib

# The identity first: it is what a dependent records as the dependency, and rewriting it also renames the library's
# own entry in its load commands, which is why it must happen before the references are touched.
install_name_tool -id "@loader_path/libmsquic.dylib" "$out/libmsquic.dylib"
install_name_tool -id "@loader_path/libssl.4.dylib" "$out/libssl.4.dylib"
install_name_tool -id "@loader_path/libcrypto.4.dylib" "$out/libcrypto.4.dylib"

rewrite_homebrew_dependencies "$out/libmsquic.dylib"
rewrite_homebrew_dependencies "$out/libssl.4.dylib"
rewrite_homebrew_dependencies "$out/libcrypto.4.dylib"

# Editing load commands invalidates the signature, and macOS refuses to load an arm64 binary whose signature is
# stale, so each one is signed again. Ad-hoc is enough: these are not the app's own code.
codesign --force --sign - "$out/libmsquic.dylib" "$out/libssl.4.dylib" "$out/libcrypto.4.dylib"

echo
echo "Staged in $out:"
for file in "$out"/*.dylib; do
    echo "  $(basename "$file")"
    otool -L "$file" | tail -n +2 | sed 's/^/    /'
done
