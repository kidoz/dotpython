#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
binary=${1:?binary path is required}
kind=${2:?binary kind is required}
observed=$(mktemp "${TMPDIR:-/tmp}/dotpython-abi3-symbols.XXXXXX")
trap 'rm -f "$observed"' EXIT HUP INT TERM

case $kind in
    imports|failure-imports)
        if [ "$kind" = imports ]; then
            expected="$root/stable-abi-symbols.txt"
        else
            expected="$root/failure-stable-abi-symbols.txt"
        fi
        case $(uname -s) in
            Darwin)
                nm -u "$binary" | sed -n 's/^_\(Py[A-Za-z0-9_]*\)$/\1/p' | LC_ALL=C sort > "$observed"
                ;;
            Linux)
                nm -D --undefined-only "$binary" | awk '{ print $NF }' \
                    | sed -n 's/^\(Py[A-Za-z0-9_]*\)$/\1/p' | LC_ALL=C sort > "$observed"
                ;;
            *) exit 2 ;;
        esac
        ;;
    fixture-exports|secondary-fixture-exports)
        if [ "$kind" = fixture-exports ]; then
            expected="$root/fixture-exports.txt"
        else
            expected="$root/secondary-fixture-exports.txt"
        fi
        case $(uname -s) in
            Darwin)
                nm -gU "$binary" | awk '{ print $NF }' | sed 's/^_//' \
                    | LC_ALL=C sort > "$observed"
                ;;
            Linux)
                nm -D --defined-only "$binary" | awk '{ print $NF }' | LC_ALL=C sort > "$observed"
                ;;
            *) exit 2 ;;
        esac
        ;;
    bridge-exports)
        expected="$root/bridge-exports.txt"
        case $(uname -s) in
            Darwin)
                nm -gU "$binary" | awk '{ print $NF }' | sed 's/^_//' \
                    | LC_ALL=C sort > "$observed"
                ;;
            Linux)
                nm -D --defined-only "$binary" | awk '{ print $NF }' | LC_ALL=C sort > "$observed"
                ;;
            *) exit 2 ;;
        esac
        ;;
    *)
        echo "unknown symbol check kind: $kind" >&2
        exit 2
        ;;
esac

LC_ALL=C sort -c "$expected"
cmp "$expected" "$observed"
