#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
output=${1:?output directory is required}
build=${2:-"$output-build"}

if [ "${SANITIZE:-0}" = "1" ]; then
    setup_options="-Db_sanitize=address,undefined -Db_lundef=false"
else
    setup_options="-Db_sanitize= -Db_lundef=true"
fi

if [ -f "$build/meson-private/coredata.dat" ]; then
    meson setup --reconfigure "$build" "$root" \
        $setup_options
else
    meson setup "$build" "$root" \
        $setup_options
fi

meson compile -C "$build"
meson test -C "$build" --print-errorlogs
mkdir -p "$output"

case $(uname -s) in
    Darwin)
        bridge=libdotpython_abi3.dylib
        ;;
    Linux)
        bridge=libdotpython_abi3.so
        ;;
    *)
        echo "unsupported native fixture build platform" >&2
        exit 2
        ;;
esac

for artifact in \
    "$bridge" \
    dotpython_fixture.abi3.so \
    dotpython_fixture_failure.abi3.so \
    native_fixture_test \
    native_anyver_test \
    symbol-manifest.json \
    anyver-symbol-manifest.json; do
    cp "$build/$artifact" "$output/$artifact"
done
