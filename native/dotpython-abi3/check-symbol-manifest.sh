#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
temporary=$(mktemp "${TMPDIR:-/tmp}/dotpython-abi3-manifest.XXXXXX")
trap 'rm -f "$temporary"' EXIT HUP INT TERM
"$root/generate-symbol-manifest.sh" "$temporary"
cmp "$temporary" "$root/symbol-manifest.json"
"$root/generate-anyver-symbol-manifest.sh" "$temporary"
cmp "$temporary" "$root/anyver-symbol-manifest.json"
