# DotPython parser grammar provenance

`python314-subset.gram` is the reviewed executable subset used by the managed parser. Its upstream
reference is CPython `v3.14.6`, commit
`c63aec69bd59c55314c06c23f4c22c03de76fe45`, file `Grammar/python.gram`. The upstream file's
SHA-256 digest is
`34f0f9b2e8a22760ca4e6e7e56857cb22f4c73e0853e5452b7e93e48ddb17361`.

The subset retains grammar structure needed for the declared DotPython compatibility profile. It
does not copy CPython's C construction actions; DotPython constructs its own managed AST. CPython
is distributed under the [Python Software Foundation License](https://docs.python.org/3.14/license.html).

Regenerate and verify the checked-in executable grammar with:

```sh
just parser-generate
just parser-check
```

Generation is offline and deterministic. Changing the grammar or its provenance comments changes
the embedded SHA-256 fingerprint and must produce a reviewed generated-file diff.
