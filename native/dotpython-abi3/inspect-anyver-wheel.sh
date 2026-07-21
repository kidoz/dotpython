#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
wheel=${1:?pinned Anyver wheel path is required}
expected_wheel=0f2fa90663b0203d3086c313d6384a6d74177e1f52508abf613cb17439edc4f9
expected_entry=d635b4b37c6db5688d49ecb1b924fc6c3bfe7f51b630d5ca153ab6ab474b2827
temporary=$(mktemp -d "${TMPDIR:-/tmp}/dotpython-anyver-inspect.XXXXXX")
trap 'rm -rf "$temporary"' EXIT HUP INT TERM

actual_wheel=$(shasum -a 256 "$wheel" | awk '{ print $1 }')
test "$actual_wheel" = "$expected_wheel"
unzip -qq "$wheel" 'anyver/_anyver.abi3.so' -d "$temporary"
binary="$temporary/anyver/_anyver.abi3.so"
actual_entry=$(shasum -a 256 "$binary" | awk '{ print $1 }')
test "$actual_entry" = "$expected_entry"

nm -u "$binary" | awk '{ print $1 }' | sed 's/^_//' \
    | sed -n '/^Py/p; /^_Py/p' | LC_ALL=C sort -u > "$temporary/imports.txt"
cmp "$root/anyver-stable-abi-symbols.txt" "$temporary/imports.txt"

nm -gU "$binary" | awk '{ print $NF }' | sed 's/^_//' \
    | sed -n '/^PyInit_/p' | LC_ALL=C sort > "$temporary/exports.txt"
cmp "$root/anyver-exports.txt" "$temporary/exports.txt"
