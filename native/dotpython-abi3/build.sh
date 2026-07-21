#!/bin/sh
# Build the Rust DotPython Stable-ABI bridge, fixtures, and harnesses with Cargo, then stage the
# artifacts under the same filenames the managed loader and CI expect. Mirrors the old Meson
# wrapper's contract: build, run the symbol checks and native harness, copy the required artifacts.
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
output=${1:?output directory is required}

case $(uname -s) in
    Darwin)
        bridge=libdotpython_abi3.dylib
        libext=dylib
        ;;
    Linux)
        bridge=libdotpython_abi3.so
        libext=so
        ;;
    *)
        echo "unsupported native bridge build platform" >&2
        exit 2
        ;;
esac

# Strict exports restrict the bridge's export table to exactly the checked-in symbol manifest.
DP_ABI3_STRICT_EXPORTS=1 cargo build --release --manifest-path "$root/Cargo.toml"
target="$root/target/release"

mkdir -p "$output"
cp "$target/$bridge" "$output/$bridge"
cp "$target/libdotpython_fixture.$libext" "$output/dotpython_fixture.abi3.so"
cp "$target/libdotpython_fixture_failure.$libext" "$output/dotpython_fixture_failure.abi3.so"
cp "$target/native_fixture_test" "$output/native_fixture_test"
cp "$target/native_anyver_test" "$output/native_anyver_test"
cp "$root/symbol-manifest.json" "$output/symbol-manifest.json"
cp "$root/anyver-symbol-manifest.json" "$output/anyver-symbol-manifest.json"

# Deterministic symbol-boundary checks (same scripts and expected lists as before).
"$root/check-symbol-manifest.sh"
"$root/check-binary-symbols.sh" "$output/$bridge" bridge-exports
"$root/check-binary-symbols.sh" "$output/dotpython_fixture.abi3.so" imports
"$root/check-binary-symbols.sh" "$output/dotpython_fixture.abi3.so" fixture-exports
"$root/check-binary-symbols.sh" "$output/dotpython_fixture_failure.abi3.so" failure-imports
"$root/check-binary-symbols.sh" "$output/dotpython_fixture_failure.abi3.so" fixture-exports

# Native lifecycle / ownership / failure harness.
"$output/native_fixture_test" \
    "$output/dotpython_fixture.abi3.so" \
    "$output/dotpython_fixture_failure.abi3.so"

# Optional pinned-Anyver harness when the qualified module is provided.
if [ "${ANYVER_MODULE:-}" != "" ]; then
    "$output/native_anyver_test" "$ANYVER_MODULE"
fi
