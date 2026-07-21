#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

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

    if [ "$name" = clang-format ] && command -v xcrun >/dev/null 2>&1; then
        if xcrun --find "$name" >/dev/null 2>&1; then
            xcrun --find "$name"
            return
        fi
    fi

    echo "$name was not found; install LLVM or set CLANG_FORMAT" >&2
    return 127
}

clang_format=$(resolve_tool clang-format "${CLANG_FORMAT:-}")

if [ "$#" -gt 1 ]; then
    echo "usage: $0 [--check]" >&2
    exit 2
fi

mode=${1:-format}

case $mode in
    format)
        find "$root/include" "$root/src" "$root/fixture" "$root/test" \
            -type f \( -name '*.c' -o -name '*.h' \) \
            -exec "$clang_format" -i --style=file {} +
        ;;
    --check)
        find "$root/include" "$root/src" "$root/fixture" "$root/test" \
            -type f \( -name '*.c' -o -name '*.h' \) \
            -exec "$clang_format" --dry-run --Werror --style=file {} +
        ;;
    *)
        echo "usage: $0 [--check]" >&2
        exit 2
        ;;
esac
