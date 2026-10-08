# Pinned Unicode names

`generate.py` deterministically builds `src/DotPython.Runtime.Managed/Unicode/UnicodeNames16.bin` and `src/DotPython.Compiler/Unicode/UnicodeNameIndex16.bin` from the checked-in Unicode 16.0.0 source files. It needs Python 3.10 or newer and the standard library. Normal generation and checking do not import `unicodedata`, access the network, or require CPython. The managed runtime only reads the embedded binary.

From the repository root:

```sh
python3 tools/unicode_names/generate.py
python3 tools/unicode_names/generate.py --check
```

`--check` verifies pinned source SHA-256 values, regenerates in memory, compares the binary byte for byte, and validates its pinned artifact size/hash. It does not write the binary. To additionally qualify every codepoint against CPython **3.14.7**, whose Unicode database is **16.0.0**:

```sh
python3.14 tools/unicode_names/generate.py --check --verify-cpython --report /tmp/unicode-names-report.json
```

Qualification independently reads the generated binary and compares both ordinary names and ASCII `namereplace` for all 1,114,112 codepoints, including surrogates and noncharacters. Reports contain canonical CPython name digests. Both surfaces currently match: 2,228,224 comparisons, zero mismatches. The canonical digest appends little-endian `uint32` codepoint, ASCII name, and one NUL byte for each named codepoint in ascending order.

## Binary format

All integers are unsigned little-endian. The format is deterministic and contains no timestamps, compression, or platform-dependent data.

| Region | Layout |
|---|---|
| Header, 16 bytes | Eight magic bytes `44 50 59 55 4e 31 36 00` (`DPYUN16` plus NUL), explicit-record count `uint32`, range-record count `uint32` |
| Explicit records, 12 bytes each | Codepoint `uint32`, absolute name offset `uint32`, ASCII byte length `uint16`, reserved zero `uint16` |
| Range records, 16 bytes each | Inclusive start `uint32`, inclusive end `uint32`, absolute prefix offset `uint32`, ASCII byte length `uint16`, reserved zero `uint16` |
| String data | Printable ASCII names and prefixes, without terminators; duplicate strings share offsets |

Explicit records are sorted by codepoint, and range records by start. All intervals are disjoint. A range name is its stored prefix followed by the codepoint in uppercase hexadecimal, with a minimum of four digits. Source patterns must contain exactly one trailing `*`; the stored prefix omits it. Hangul names are explicit entries from the source.

The current artifact has **46,247 explicit records**, **17 ranges**, and **1,673,535 bytes**. SHA-256: `18103b3507da09341e3d76db9ec7e96c1b006b0ee5df18e9c4f856fbd2dfda79`.

## Compatibility policy

The provider targets CPython 3.14.7 behavior, with two explicit overlays on Unicode's Name property:

1. CPython 3.14 omits the Tangut ideograph names at U+17000–U+187F7 and U+18D00–U+18D08. These two wildcard ranges are excluded. All other Unicode-16 Name-property entries match ordinary `unicodedata.name`.
2. CPython's C name API, used by `namereplace`, exposes its internal alias and named-sequence pseudo-entries in private-use plane 15. The generator assigns `NameAliases.txt` names in original file order from U+F0000 (477 entries through U+F01DC), and `NamedSequences.txt` names from U+F0200 (461 entries through U+F03CC). Ordinary name lookup hides these; the `namereplace` lookup mode includes them. They are CPython implementation compatibility data, not Unicode character names for private-use codepoints.

Consequently, ordinary lookup has 148,853 names and the CAPI-compatible mode 149,791. This resource does not by itself implement the public `unicodedata` module or arbitrary codec registration.

## Reverse index

`--check` also guards a second artifact, `src/DotPython.Compiler/Unicode/UnicodeNameIndex16.bin`, which the compiler reads to decode `\N{NAME}` string escapes. Both files come from one generator run and one set of pinned sources, so provenance stays single.

The reverse index is a third projection, distinct from both lookup modes above. It carries the ordinary names plus the `NameAliases.txt` names **at their real codepoints** — not the plane-15 pseudo-codepoints the `namereplace` mode needs — restores the two Tangut ranges, and leaves named sequences out, because `unicodedata.lookup` resolves those but the escape decoder does not. Matching is exact and case-insensitive with no folding of underscores, hyphens or spaces, which is what CPython 3.14 does.

| Region | Layout |
|---|---|
| Header, 16 bytes | Magic `44 50 59 4e 49 58 31 36` (`DPYNIX16`), record count `uint32`, range-record count `uint32` |
| Name records, 12 bytes each | Codepoint `uint32`, absolute name offset `uint32`, ASCII byte length `uint16`, reserved zero `uint16` |
| Range records, 16 bytes each | Inclusive start `uint32`, inclusive end `uint32`, absolute prefix offset `uint32`, ASCII byte length `uint16`, reserved zero `uint16` |
| String data | Printable ASCII names and prefixes, without terminators; duplicate strings share offsets |

Name records are sorted by name, so lookup is a binary search; range records hold the algorithmic `PREFIX-XXXX` forms, whose suffix is parsed and bounds-checked on a prefix match. The artifact has **45,786 name records** and **19 ranges**, and is 1,656,716 bytes. SHA-256: `27eba97eaffced81f734ed82bbe94da290553d04b62de6e8c3d0756a3e62fb19`.

`--verify-cpython` compiles every indexed name through CPython 3.14.7's own `\N{...}` decoder and requires each to yield the indexed codepoint, and requires every `NamedSequences.txt` name to be rejected: 45,786 names checked, 461 named sequences rejected, zero mismatches. The forward table's 2,228,224 comparisons are unchanged, and its artifact stays byte-identical.

For an update, obtain versioned official sources, preserve their license, update the explicit compatibility policy and provenance, regenerate, and rerun exhaustive qualification against the intended Python version. Do not derive production data from the host's `unicodedata` module or silently replace the pinned sources.

Source URLs, hashes, license, artifact identity, and qualification digests are recorded under [`third_party/unicode/16.0.0`](../../third_party/unicode/16.0.0/README.md).
