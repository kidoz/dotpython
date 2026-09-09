# DotPython parser grammar provenance

`python315-subset.gram` is the reviewed executable subset used by the managed parser. Its upstream
reference is CPython `v3.15.0rc2`, commit
`435c9e5a798c99653e3ab64ce29baed0e4f3dfee`, file `Grammar/python.gram`. The upstream file's
SHA-256 digest is
`cc813f7e8f56a5c8c8f4dacf1b760f4c6c478ab54f1f3a21edeeb493d1534bc0`. The previous pin was
CPython `v3.14.6` (commit `c63aec69bd59c55314c06c23f4c22c03de76fe45`, SHA-256
`34f0f9b2e8a22760ca4e6e7e56857cb22f4c73e0853e5452b7e93e48ddb17361`); the 3.15 upstream grammar
adds PEP 810 `lazy` imports and PEP 798 comprehension unpacking, neither of which is in the
executable subset yet.

The subset retains grammar structure needed for the declared DotPython compatibility profile. It
does not copy CPython's C construction actions; DotPython constructs its own managed AST. CPython
is distributed under the [Python Software Foundation License](https://docs.python.org/3.15/license.html).

Regenerate and verify the checked-in executable grammar with:

```sh
just parser-generate
just parser-check
```

Generation is offline and deterministic. Changing the grammar or its provenance comments changes
the embedded SHA-256 fingerprint and must produce a reviewed generated-file diff.
