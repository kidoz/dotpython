#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
output=${1:?output directory is required}
cc=${CC:-cc}
mkdir -p "$output"

common="-std=c11 -Wall -Wextra -Werror -fPIC -fvisibility=hidden -I$root/include"
sanitize=""
if [ "${SANITIZE:-0}" = "1" ]; then
    sanitize="-fsanitize=address,undefined -fno-omit-frame-pointer"
fi

case $(uname -s) in
    Darwin)
        bridge="$output/libdotpython_abi3.dylib"
        fixture="$output/dotpython_fixture.abi3.so"
        failure="$output/dotpython_fixture_failure.abi3.so"
        $cc $common $sanitize -dynamiclib "$root/src/dotpython_abi3.c" \
            -install_name @rpath/libdotpython_abi3.dylib -o "$bridge"
        $cc $common $sanitize -dynamiclib "$root/fixture/dotpython_fixture.c" \
            -L"$output" -ldotpython_abi3 -Wl,-rpath,@loader_path -o "$fixture"
        $cc $common $sanitize -dynamiclib "$root/fixture/dotpython_fixture_init_failure.c" \
            -L"$output" -ldotpython_abi3 -Wl,-rpath,@loader_path -o "$failure"
        $cc $common $sanitize "$root/test/native_fixture_test.c" -L"$output" \
            -ldotpython_abi3 -Wl,-rpath,@loader_path -o "$output/native_fixture_test"
        $cc $common $sanitize "$root/test/native_anyver_test.c" -L"$output" \
            -ldotpython_abi3 -Wl,-rpath,@loader_path -o "$output/native_anyver_test"
        ;;
    Linux)
        bridge="$output/libdotpython_abi3.so"
        fixture="$output/dotpython_fixture.abi3.so"
        failure="$output/dotpython_fixture_failure.abi3.so"
        $cc $common $sanitize -shared "$root/src/dotpython_abi3.c" \
            -Wl,-soname,libdotpython_abi3.so -o "$bridge"
        $cc $common $sanitize -shared "$root/fixture/dotpython_fixture.c" \
            -L"$output" -ldotpython_abi3 -Wl,-rpath,'$ORIGIN' -o "$fixture"
        $cc $common $sanitize -shared "$root/fixture/dotpython_fixture_init_failure.c" \
            -L"$output" -ldotpython_abi3 -Wl,-rpath,'$ORIGIN' -o "$failure"
        $cc $common $sanitize "$root/test/native_fixture_test.c" -L"$output" \
            -ldotpython_abi3 -ldl -Wl,-rpath,'$ORIGIN' -o "$output/native_fixture_test"
        $cc $common $sanitize "$root/test/native_anyver_test.c" -L"$output" \
            -ldotpython_abi3 -ldl -Wl,-rpath,'$ORIGIN' -o "$output/native_anyver_test"
        ;;
    *)
        echo "unsupported native fixture build platform" >&2
        exit 2
        ;;
esac

cp "$root/symbol-manifest.json" "$output/symbol-manifest.json"
cp "$root/anyver-symbol-manifest.json" "$output/anyver-symbol-manifest.json"
"$root/check-symbol-manifest.sh"
"$root/check-binary-symbols.sh" "$fixture" imports
"$root/check-binary-symbols.sh" "$fixture" fixture-exports
"$root/check-binary-symbols.sh" "$bridge" bridge-exports
"$output/native_fixture_test" "$fixture"
