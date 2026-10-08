#!/usr/bin/env python3
"""Build DotPython's pinned Unicode-16 case-mapping resource without network access.

Generation uses only checked-in Unicode data. --verify-cpython qualifies every
mapping against a reference CPython 3.14.7 for the whole code space.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / "third_party/unicode/16.0.0"
OUTPUT = ROOT / "src/DotPython.Runtime.Managed/Unicode/UnicodeCase16.bin"
MAGIC = b"DPYCASE16\0"
HEADER = struct.Struct("<10sIIIIIIIII")
RANGE = struct.Struct("<II")
RECORD = struct.Struct("<II")
# One record per (code point, kind) where kind selects the operation.
KIND_UPPER, KIND_LOWER, KIND_TITLE, KIND_FOLD = 0, 1, 2, 3
SEQUENCE = 1 << 31


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def check_sources() -> None:
    provenance = json.loads((DATA / "PROVENANCE.json").read_text(encoding="utf-8"))
    for source in provenance["sources"]:
        path = DATA / source["file"]
        raw = path.read_bytes()
        if len(raw) != source["bytes"] or sha256(raw) != source["sha256"]:
            raise SystemExit(f"{source['file']} does not match PROVENANCE.json")


def simple_mappings() -> dict[int, dict[int, list[int]]]:
    """The UnicodeData.txt simple mappings, which the special casing can override."""
    mappings: dict[int, dict[int, list[int]]] = {
        KIND_UPPER: {},
        KIND_LOWER: {},
        KIND_TITLE: {},
    }
    for line in (DATA / "UnicodeData.txt").read_text(encoding="utf-8").splitlines():
        fields = line.split(";")
        if len(fields) < 15:
            continue
        code = int(fields[0], 16)
        for kind, field in ((KIND_UPPER, 12), (KIND_LOWER, 13), (KIND_TITLE, 14)):
            if fields[field]:
                mappings[kind][code] = [int(fields[field], 16)]
    return mappings


def special_mappings() -> dict[int, dict[int, list[int]]]:
    """SpecialCasing.txt's unconditional full mappings; conditional ones are context."""
    mappings: dict[int, dict[int, list[int]]] = {
        KIND_UPPER: {},
        KIND_LOWER: {},
        KIND_TITLE: {},
    }
    for line in (DATA / "SpecialCasing.txt").read_text(encoding="utf-8").splitlines():
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        fields = [field.strip() for field in line.split(";")]
        if len(fields) < 5 or fields[4]:
            # A non-empty condition means the mapping is context-sensitive; the only
            # one CPython's str methods apply is Final_Sigma, handled in the runtime.
            continue
        code = int(fields[0], 16)
        for kind, field in ((KIND_LOWER, 1), (KIND_TITLE, 2), (KIND_UPPER, 3)):
            if fields[field]:
                lowered = [int(item, 16) for item in fields[field].split()]
                mappings[kind][code] = lowered
    return mappings


def derived_ranges(property_name: str) -> list[tuple[int, int]]:
    """The inclusive ranges of a DerivedCoreProperties binary property."""
    ranges = []
    for line in (DATA / "DerivedCoreProperties.txt").read_text(encoding="utf-8").splitlines():
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        fields = [field.strip() for field in line.split(";")]
        if len(fields) < 2 or fields[1] != property_name:
            continue
        bounds = fields[0].split("..")
        start = int(bounds[0], 16)
        ranges.append((start, int(bounds[-1], 16)))
    return ranges


def fold_mappings() -> dict[int, list[int]]:
    """CaseFolding.txt's common and full foldings; simple and Turkic stay out."""
    mappings: dict[int, list[int]] = {}
    for line in (DATA / "CaseFolding.txt").read_text(encoding="utf-8").splitlines():
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        fields = [field.strip() for field in line.split(";")]
        if len(fields) < 3 or fields[1] not in ("C", "F"):
            continue
        mappings[int(fields[0], 16)] = [int(item, 16) for item in fields[2].split()]
    return mappings


def build() -> tuple[bytes, dict[str, int]]:
    upper = simple_mappings()[KIND_UPPER]
    lower = simple_mappings()[KIND_LOWER]
    title = simple_mappings()[KIND_TITLE]
    special = special_mappings()
    upper.update(special[KIND_UPPER])
    lower.update(special[KIND_LOWER])
    title.update(special[KIND_TITLE])
    fold = fold_mappings()
    # A mapping identical to the character itself is not a mapping.
    tables = {
        KIND_UPPER: {c: v for c, v in upper.items() if v != [c]},
        KIND_LOWER: {c: v for c, v in lower.items() if v != [c]},
        KIND_TITLE: {c: v for c, v in title.items() if v != [c]},
        KIND_FOLD: {c: v for c, v in fold.items() if v != [c]},
    }

    payload: list[int] = []
    records: list[tuple[int, int]] = []
    counts = []
    # Tables are concatenated in kind order, so a record carries only its code point.
    for kind in (KIND_UPPER, KIND_LOWER, KIND_TITLE, KIND_FOLD):
        table = tables[kind]
        counts.append(len(table))
        for code in sorted(table):
            mapped = table[code]
            if len(mapped) == 1:
                records.append((code, mapped[0]))
            else:
                offset = len(payload)
                payload.extend(mapped)
                records.append((code, SEQUENCE | len(mapped) << 24 | offset))

    # The sigma and title rules need the Unicode `Cased` and `Case_Ignorable`
    # properties, which no mapping table can express.
    cased = derived_ranges("Cased")
    ignorable = derived_ranges("Case_Ignorable")
    lowercase = derived_ranges("Lowercase")
    uppercase = derived_ranges("Uppercase")

    body = bytearray()
    body += HEADER.pack(
        MAGIC, *counts, len(payload), len(cased), len(ignorable), len(lowercase),
        len(uppercase),
    )
    for code, value in records:
        body += RECORD.pack(code, value)
    for item in payload:
        body += struct.pack("<I", item)
    for start, end in cased + ignorable + lowercase + uppercase:
        body += RANGE.pack(start, end)
    return bytes(body), {
        "upper": counts[0],
        "lower": counts[1],
        "title": counts[2],
        "fold": counts[3],
        "sequences": len(payload),
        "cased": len(cased),
        "caseIgnorable": len(ignorable),
        "lowercase": len(lowercase),
        "uppercase": len(uppercase),
        "bytes": len(body),
        "sha256": sha256(bytes(body)),
    }


def _apply(mapping: dict[int, list[int]], text: str) -> str:
    return "".join(chr(item) for ch in text for item in mapping.get(ord(ch), [ord(ch)]))


def verify_cpython(reference: str) -> int:
    """Compare every operation over every code point against the reference CPython.

    The prediction is a port of the runtime's own rule (including Final_Sigma and the
    swapcase category rule), so this qualifies the rule and the tables together.
    """
    upper = dict(simple_mappings()[KIND_UPPER])
    lower = dict(simple_mappings()[KIND_LOWER])
    title = dict(simple_mappings()[KIND_TITLE])
    special = special_mappings()
    for target, source in ((upper, special[KIND_UPPER]), (lower, special[KIND_LOWER]),
                           (title, special[KIND_TITLE])):
        for code, mapped in source.items():
            target[code] = mapped
    fold = fold_mappings()
    for table in (upper, lower, title, fold):
        for code in [c for c, m in table.items() if m == [c]]:
            del table[code]

    def in_ranges(ranges: list[tuple[int, int]], code: int) -> bool:
        return any(start <= code <= end for start, end in ranges)

    cased_ranges = derived_ranges("Cased")
    ignorable_ranges = derived_ranges("Case_Ignorable")

    def is_cased(ch: str) -> bool:
        return in_ranges(cased_ranges, ord(ch))

    def is_case_ignorable(ch: str) -> bool:
        return in_ranges(ignorable_ranges, ord(ch))

    lowercase_ranges = derived_ranges("Lowercase")
    uppercase_ranges = derived_ranges("Uppercase")

    def is_lower(ch: str) -> bool:
        return in_ranges(lowercase_ranges, ord(ch))

    def is_upper(ch: str) -> bool:
        return in_ranges(uppercase_ranges, ord(ch))

    def final_sigma(text: str, index: int) -> bool:
        preceded = False
        for previous in range(index - 1, -1, -1):
            if is_case_ignorable(text[previous]):
                continue
            preceded = is_cased(text[previous])
            break
        if not preceded:
            return False
        for following in range(index + 1, len(text)):
            if is_case_ignorable(text[following]):
                continue
            return not is_cased(text[following])
        return True

    def lower_one(text: str, index: int) -> str:
        if text[index] == "Σ" and final_sigma(text, index):
            return "ς"
        return _apply(lower, text[index])

    def predict(kind: str, text: str) -> str:
        # The neighbour contexts re-use the base operation on a widened string.
        kind = {
            "lower_after": "lower", "lower_before": "lower",
            "title_before": "title", "title_after": "title",
            "swap_after": "swapcase", "swap_before": "swapcase",
        }.get(kind, kind)
        if kind == "upper":
            return _apply(upper, text)
        if kind == "fold":
            return _apply(fold, text)
        if kind == "lower":
            return "".join(lower_one(text, i) for i in range(len(text)))
        if kind == "title":
            out, at_word_start = [], True
            for i, ch in enumerate(text):
                out.append(_apply(title, ch) if at_word_start else lower_one(text, i))
                at_word_start = not is_cased(ch)
            return "".join(out)
        if kind == "capitalize":
            if not text:
                return text
            return _apply(title, text[0]) + "".join(
                lower_one(text, i) for i in range(1, len(text))
            )
        if kind == "swapcase":
            out = []
            for i, ch in enumerate(text):
                if is_lower(ch):
                    out.append(_apply(upper, ch))
                elif is_upper(ch):
                    out.append(lower_one(text, i))
                else:
                    out.append(ch)
            return "".join(out)
        raise AssertionError(kind)

    # Fields are hex code-point lists so that control characters cannot break the row.
    script = r"""
def hexes(text):
    return " ".join(f"{ord(ch):X}" for ch in text)
for c in range(0x110000):
    if 0xD800 <= c <= 0xDFFF:
        continue
    s = chr(c)
    print(hexes(s) + "|" + "|".join(hexes(v) for v in [
        s.upper(), s.lower(), s.title(), s.casefold(), s.swapcase(), s.capitalize(),
        ("a" + s).lower()[1:], (s + "a").lower()[:len(s.lower())],
        (s + "A").title()[:len(s.title())], ("A" + s).title()[1:],
        ("a" + s).swapcase()[1:], (s + "A").swapcase()[:len(s.swapcase())],
    ]))
"""
    result = subprocess.run(
        [reference, "-c", script], capture_output=True, text=True, check=True
    )
    mismatches = 0
    kinds = ("upper", "lower", "title", "fold", "swapcase", "capitalize",
             "lower_after", "lower_before", "title_before", "title_after",
             "swap_after", "swap_before")
    for line in result.stdout.splitlines():
        code_text, *fields = line.split("|")
        code = int(code_text.split()[0], 16)
        fields = [
            "".join(chr(int(item, 16)) for item in field.split()) if field else ""
            for field in fields
        ]
        source = chr(code)
        contexts = (
            source, source, source, source, source, source,
            "a" + source, source + "a", source + "A", "A" + source,
            "a" + source, source + "A",
        )
        for kind, expected, text in zip(kinds, fields, contexts):
            actual = predict(kind, text)
            if kind in ("lower_after", "title_after", "swap_after"):
                actual = actual[1:]
            if kind in ("lower_before", "title_before", "swap_before"):
                actual = actual[: len(expected)]
            if actual != expected:
                if mismatches < 20:
                    print(f"{kind} U+{code:X}: reference {expected!r} != rule {actual!r}")
                mismatches += 1

    # The plain operations over a mixed corpus, to exercise composition.
    corpus = ["aBc dEf", "they're x-y", "ΟΔΟΣ ΣΣ", "ßẞﬁﬄ", "ǅǆǳ", "İıſ", "hello WORLD 123"]
    script2 = "for s in " + repr(corpus) + ":\n"
    script2 += "    print('|'.join(' '.join(f'{ord(ch):X}' for ch in v) for v in [s.upper(), s.lower(), s.title(), s.casefold(), s.swapcase(), s.capitalize()]))\n"
    result2 = subprocess.run([reference, "-c", script2], capture_output=True, text=True, check=True)
    for text, line in zip(corpus, result2.stdout.splitlines()):
        decoded = ["".join(chr(int(item, 16)) for item in field.split()) if field else "" for field in line.split("|")]
        for kind, expected in zip(kinds, decoded):
            actual = predict(kind, text)
            if actual != expected:
                print(f"{kind} {text!r}: reference {expected!r} != rule {actual!r}")
                mismatches += 1

    if mismatches:
        print(f"mismatches: {mismatches}")
        return 1
    print("every code point and context matches CPython")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="fail if the resource is stale")
    parser.add_argument("--verify-cpython", metavar="PYTHON", help="qualify against CPython")
    parser.add_argument("--summary", action="store_true", help="print the table summary")
    args = parser.parse_args()

    check_sources()
    data, summary = build()
    if args.summary:
        print(json.dumps(summary, indent=2))
    if args.verify_cpython:
        return verify_cpython(args.verify_cpython)
    if args.check:
        current = OUTPUT.read_bytes() if OUTPUT.exists() else b""
        if current != data:
            print(f"{OUTPUT} is stale; run tools/unicode_case/generate.py", file=sys.stderr)
            return 1
        print(f"{OUTPUT} is up to date ({summary['bytes']} bytes, sha256 {summary['sha256']})")
        return 0
    OUTPUT.write_bytes(data)
    print(f"wrote {OUTPUT} ({summary['bytes']} bytes, sha256 {summary['sha256']})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
