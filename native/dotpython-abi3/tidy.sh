#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

if [ "$#" -ne 0 ]; then
    echo "usage: $0" >&2
    exit 2
fi

resolve_tool() {
    name=$1
    override=$2

    if [ -n "$override" ]; then
        if command -v "$override" >/dev/null 2>&1; then
            command -v "$override"
            return
        fi
        echo "$name override is not executable: $override" >&2
        return 127
    fi

    if command -v "$name" >/dev/null 2>&1; then
        command -v "$name"
        return
    fi

    if command -v brew >/dev/null 2>&1; then
        llvm_prefix=$(brew --prefix llvm 2>/dev/null || true)
        if [ -n "$llvm_prefix" ] && [ -x "$llvm_prefix/bin/$name" ]; then
            printf '%s\n' "$llvm_prefix/bin/$name"
            return
        fi
    fi

    echo "$name was not found; install LLVM or set CLANG_TIDY" >&2
    return 127
}

clang_tidy=$(resolve_tool clang-tidy "${CLANG_TIDY:-}")

find "$root/src" "$root/fixture" "$root/test" -type f -name '*.c' -print \
    | LC_ALL=C sort \
    | while IFS= read -r source; do
        "$clang_tidy" \
            --quiet \
            --config-file="$root/.clang-tidy" \
            "$source" \
            -- \
            -std=c11 \
            -Wall \
            -Wextra \
            -Werror \
            -fPIC \
            -fvisibility=hidden \
            -I"$root/include"
    done
