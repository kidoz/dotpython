#!/usr/bin/env python3
"""Build DotPython's Unicode-16 character database for the `unicodedata` module.

The pinned `third_party/unicode/16.0.0/UnicodeData.txt`, `NameAliases.txt` and
`NamedSequences.txt` supply categories, combining classes, bidirectional classes,
decompositions, numeric values, mirrored flags and names. East Asian Width and the
composition pairs are read from the generating interpreter's `unicodedata` (the
same UCD 16 as CPython 3.14.7); the script refuses any other UCD version.
"""
from __future__ import annotations

from pathlib import Path
import struct
import sys
import unicodedata

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / "third_party/unicode/16.0.0"
OUTPUT = ROOT / "src/DotPython.Runtime.Managed/Unicode/UnicodeData16.bin"
MAGIC = b"DPYUD16\0"
MAX = 0x110000


def parse_unicode_data():
    cat = ["Cn"] * MAX
    ccc = [0] * MAX
    bidi = [""] * MAX
    mirrored = [False] * MAX
    decomp = {}
    numeric = {}
    names = []
    pending = None
    for line in (DATA / "UnicodeData.txt").read_text(encoding="utf-8").splitlines():
        fields = line.split(";")
        cp = int(fields[0], 16)
        name, category, combining, direction = fields[1], fields[2], int(fields[3]), fields[4]
        if name.endswith(", First>"):
            pending = (cp, category, combining, direction, fields[9])
            continue
        if name.endswith(", Last>"):
            start, pcat, pccc, pbidi, pmi = pending
            for value in range(start, cp + 1):
                cat[value] = pcat
                ccc[value] = pccc
                bidi[value] = pbidi
                mirrored[value] = pmi == "Y"
            pending = None
            continue
        cat[cp] = category
        ccc[cp] = combining
        bidi[cp] = direction
        mirrored[cp] = fields[9] == "Y"
        if not name.startswith("<"):
            names.append((name, chr(cp)))
        raw = fields[5]
        if raw:
            parts = raw.split()
            tag = ""
            if parts[0].startswith("<"):
                tag = parts[0][1:-1]
                parts = parts[1:]
            decomp[cp] = (tag, [int(part, 16) for part in parts])
        decimal, digit, number = fields[6], fields[7], fields[8]
        if decimal:
            numeric[(cp, 1)] = (int(decimal), 1)
        if digit:
            numeric[(cp, 2)] = (int(digit), 1)
        if number:
            if "/" in number:
                numerator, denominator = number.split("/")
                numeric[(cp, 3)] = (int(numerator), int(denominator))
            else:
                numeric[(cp, 3)] = (int(number), 1)
    aliases = []
    for line in (DATA / "NameAliases.txt").read_text(encoding="utf-8").splitlines():
        if not line or line.startswith("#"):
            continue
        fields = line.split(";")
        cp = int(fields[0], 16)
        aliases.append((fields[1].upper(), chr(cp)))
    sequences = []
    for line in (DATA / "NamedSequences.txt").read_text(encoding="utf-8").splitlines():
        if not line or line.startswith("#"):
            continue
        name, sequence = line.split(";")
        sequences.append((name.upper(), "".join(chr(int(p, 16)) for p in sequence.split())))
    return cat, ccc, bidi, mirrored, decomp, numeric, names, aliases, sequences


def ranges(values, default):
    result = []
    start = 0
    current = values[0]
    for index in range(1, len(values)):
        value = values[index]
        if value != current:
            if current != default:
                result.append((start, index - 1, current))
            start = index
            current = value
    if current != default:
        result.append((start, len(values) - 1, current))
    return result


def main() -> int:
    version = unicodedata.unidata_version
    if version != "16.0.0":
        print(f"unicodedata reports UCD {version}; 16.0.0 is required", file=sys.stderr)
        return 1
    cat, ccc, bidi, mirrored, decomp, numeric, names, aliases, sequences = parse_unicode_data()
    eaw = [unicodedata.east_asian_width(chr(cp)) for cp in range(MAX)]

    category_names = sorted({value for value in cat})
    category_index = {value: index for index, value in enumerate(category_names)}
    bidi_names = sorted({value for value in bidi})
    bidi_index = {value: index for index, value in enumerate(bidi_names)}
    eaw_names = sorted({value for value in eaw})
    eaw_index = {value: index for index, value in enumerate(eaw_names)}

    cat_ranges = ranges(cat, "Cn")
    ccc_ranges = ranges(ccc, 0)
    bidi_ranges = ranges(bidi, "")
    eaw_ranges = ranges(eaw, "N")
    mirror_ranges = ranges(mirrored, False)

    tags = [""]
    for tag, _ in decomp.values():
        if tag not in tags:
            tags.append(tag)
    tag_index = {tag: index for index, tag in enumerate(tags)}

    compositions = []
    for cp, (tag, values) in decomp.items():
        if tag or len(values) != 2:
            continue
        if unicodedata.normalize("NFC", chr(values[0]) + chr(values[1])) == chr(cp):
            compositions.append((values[0], values[1], cp))
    compositions.sort()

    entries = [(name.encode("utf-8"), value) for name, value in names]
    entries += [(name.encode("utf-8"), value) for name, value in aliases]
    entries += [(name.encode("utf-8"), value) for name, value in sequences]
    entries.sort(key=lambda entry: entry[0])
    blob = bytearray()
    records = []
    for name, value in entries:
        value_bytes = value.encode("utf-8")
        records.append((len(blob), len(name), len(value_bytes)))
        blob += name
        blob += value_bytes

    out = bytearray()
    out += MAGIC
    version_bytes = version.encode("ascii")
    out += struct.pack("<H", len(version_bytes)) + version_bytes
    out += struct.pack("<I", len(category_names))
    for name in category_names:
        raw = name.encode("ascii")
        out += struct.pack("<B", len(raw)) + raw
    out += struct.pack("<I", len(bidi_names))
    for name in bidi_names:
        raw = name.encode("ascii")
        out += struct.pack("<B", len(raw)) + raw
    out += struct.pack("<I", len(eaw_names))
    for name in eaw_names:
        raw = name.encode("ascii")
        out += struct.pack("<B", len(raw)) + raw
    out += struct.pack("<I", len(tags))
    for tag in tags:
        raw = tag.encode("ascii")
        out += struct.pack("<B", len(raw)) + raw

    def section(items, writer):
        out.extend(struct.pack("<I", len(items)))
        for item in items:
            writer(item)

    section(cat_ranges, lambda r: out.extend(struct.pack("<IIB", r[0], r[1], category_index[r[2]])))
    section(ccc_ranges, lambda r: out.extend(struct.pack("<IIB", r[0], r[1], r[2])))
    section(bidi_ranges, lambda r: out.extend(struct.pack("<IIB", r[0], r[1], bidi_index[r[2]])))
    section(eaw_ranges, lambda r: out.extend(struct.pack("<IIB", r[0], r[1], eaw_index[r[2]])))
    section(mirror_ranges, lambda r: out.extend(struct.pack("<II", r[0], r[1])))
    ordered_decomp = sorted(decomp.items())
    out.extend(struct.pack("<I", len(ordered_decomp)))
    for cp, (tag, values) in ordered_decomp:
        out += struct.pack("<IHH", cp, tag_index[tag], len(values))
        out += b"".join(struct.pack("<I", value) for value in values)
    section(compositions, lambda r: out.extend(struct.pack("<III", *r)))
    ordered_numeric = sorted(numeric.items())
    section(
        ordered_numeric,
        lambda item: out.extend(
            struct.pack("<IBqq", item[0][0], item[0][1], item[1][0], item[1][1])
        ),
    )
    out += struct.pack("<I", len(records))
    for offset, name_length, value_length in records:
        out += struct.pack("<III", offset, name_length, value_length)
    out += struct.pack("<I", len(blob)) + blob

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_bytes(out)
    print(f"wrote {OUTPUT} ({len(out)} bytes, UCD {version})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
