#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
output=${1:-"$root/anyver-symbol-manifest.json"}

emit_array() {
    file=$1
    awk '
        BEGIN { first = 1 }
        NF {
            if (!first) printf ",\n"
            printf "    \"%s\"", $0
            first = 0
        }
        END { printf "\n" }
    ' "$file"
}

{
    printf '{\n  "schemaVersion": 1,\n'
    printf '  "manifestVersion": "dotpython-abi3-anyver-1.1.0-v1",\n'
    printf '  "providerId": "dotpython-managed-abi3",\n'
    printf '  "abiFamily": "abi3",\n'
    printf '  "minimumAbiVersion": "3.11",\n'
    printf '  "bridgeAbiVersion": 2,\n'
    printf '  "moduleName": "anyver._anyver",\n'
    printf '  "initializationSymbol": "PyInit__anyver",\n'
    printf '  "artifactFileName": "anyver-1.1.0-cp311-abi3-macosx_11_0_arm64.whl",\n'
    printf '  "artifactSha256": "0f2fa90663b0203d3086c313d6384a6d74177e1f52508abf613cb17439edc4f9",\n'
    printf '  "nativeEntry": "anyver/_anyver.abi3.so",\n'
    printf '  "nativeEntrySha256": "d635b4b37c6db5688d49ecb1b924fc6c3bfe7f51b630d5ca153ab6ab474b2827",\n'
    printf '  "sourceRevision": "3dc892e3eb9d1a4baf7a315a6ce4a41b3893337e",\n'
    printf '  "allowedStableAbiSymbols": [\n'
    emit_array "$root/anyver-stable-abi-symbols.txt"
    printf '  ],\n  "requiredFixtureExports": [\n'
    emit_array "$root/anyver-exports.txt"
    printf '  ],\n  "requiredBridgeExports": [\n'
    emit_array "$root/bridge-exports.txt"
    printf '  ],\n  "allowedMethods": [\n'
    emit_array "$root/anyver-methods.txt"
    printf '  ]\n}\n'
} > "$output"
