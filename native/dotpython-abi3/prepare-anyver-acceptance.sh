#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
wheel=${1:?pinned Anyver wheel path is required}
output=${2:?output directory is required}
temporary=$(mktemp -d "${TMPDIR:-/tmp}/dotpython-anyver-prepare.XXXXXX")
trap 'rm -rf "$temporary"' EXIT HUP INT TERM

"$root/inspect-anyver-wheel.sh" "$wheel"
unzip -qq "$wheel" -d "$temporary/package"
test -f "$temporary/package/anyver/__init__.py"
test -f "$temporary/package/anyver/_anyver.abi3.so"
test -f "$temporary/package/anyver-1.1.0.dist-info/METADATA"
mkdir -p "$output"
rm -rf "$output/anyver-package"
mv "$temporary/package" "$output/anyver-package"
cp "$output/anyver-package/anyver/_anyver.abi3.so" "$output/anyver._anyver.abi3.so"
cp "$root/anyver-symbol-manifest.json" "$output/anyver-symbol-manifest.json"
