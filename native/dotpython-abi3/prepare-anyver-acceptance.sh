#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
wheel=${1:?pinned Anyver wheel path is required}
output=${2:?output directory is required}
temporary=$(mktemp -d "${TMPDIR:-/tmp}/dotpython-anyver-prepare.XXXXXX")
trap 'rm -rf "$temporary"' EXIT HUP INT TERM

"$root/inspect-anyver-wheel.sh" "$wheel"
unzip -qq "$wheel" 'anyver/_anyver.abi3.so' -d "$temporary"
mkdir -p "$output"
cp "$temporary/anyver/_anyver.abi3.so" "$output/anyver._anyver.abi3.so"
cp "$root/anyver-symbol-manifest.json" "$output/anyver-symbol-manifest.json"
