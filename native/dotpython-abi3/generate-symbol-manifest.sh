#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
output=${1:-"$root/symbol-manifest.json"}

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
    printf '{\n  "schemaVersion": 2,\n'
    printf '  "manifestVersion": "dotpython-abi3-fixture-v2",\n'
    printf '  "providerId": "dotpython-managed-abi3",\n'
    printf '  "abiFamily": "abi3",\n'
    printf '  "minimumAbiVersion": "3.11",\n'
    printf '  "bridgeAbiVersion": 4,\n'
    printf '  "capabilityId": "managed-stable-abi-fixture-v2",\n'
    printf '  "libraryLifetime": "module",\n'
    printf '  "moduleName": "dotpython_fixture",\n'
    printf '  "initializationSymbol": "PyInit_dotpython_fixture",\n'
    printf '  "allowedStableAbiSymbols": [\n'
    emit_array "$root/stable-abi-symbols.txt"
    printf '  ],\n  "requiredFixtureExports": [\n'
    emit_array "$root/fixture-exports.txt"
    printf '  ],\n  "requiredBridgeExports": [\n'
    emit_array "$root/bridge-exports.txt"
    printf '  ],\n  "allowedMethods": [\n'
    emit_array "$root/fixture-methods.txt"
    printf '  ]\n}\n'
} > "$output"
