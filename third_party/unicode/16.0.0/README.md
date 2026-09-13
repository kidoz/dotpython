# Unicode 16.0.0 data

The files in this directory are unmodified official Unicode data and its associated license notice, retrieved on 2026-09-13:

- [`DerivedName.txt`](https://www.unicode.org/Public/16.0.0/ucd/extracted/DerivedName.txt): Unicode Name-property values and wildcard ranges.
- [`NameAliases.txt`](https://www.unicode.org/Public/16.0.0/ucd/NameAliases.txt): alias names, preserving source order.
- [`NamedSequences.txt`](https://www.unicode.org/Public/16.0.0/ucd/NamedSequences.txt): named sequences, preserving source order.
- [`LICENSE.txt`](https://www.unicode.org/license.txt): Unicode License V3, including the copyright and permission notice accompanying the distributed data and generated resource. The downloaded license notice is the 2026 version; the versioned data retain their original copyright headers.

[`PROVENANCE.json`](PROVENANCE.json) pins each source URL, byte length and SHA-256. It also records the generated resource identity, the explicit CPython compatibility policy, and exhaustive CPython 3.14.7 / Unicode 16.0.0 name digests.

Generation is described in [`tools/unicode_names/README.md`](../../../tools/unicode_names/README.md). Unicode's source files remain intact: CPython-specific Tangut exclusions and private-use pseudo-entry overlays are applied only by the generator.

Primary references for the compatibility policy:

- [CPython 3.14.7 Modules/unicodedata.c](https://github.com/python/cpython/blob/v3.14.7/Modules/unicodedata.c): `_getucname`, Tangut exclusion, and the `with_alias_and_seq` flag.
- [CPython 3.14.7 Tools/unicode/makeunicodedata.py](https://github.com/python/cpython/blob/v3.14.7/Tools/unicode/makeunicodedata.py): alias/named-sequence pseudo-entry bases and original-file ordering.
- [CPython 3.14.7 Python/codecs.c](https://github.com/python/cpython/blob/v3.14.7/Python/codecs.c): `PyCodec_NameReplaceErrors` uses the C name API including those pseudo-entries.
