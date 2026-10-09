// The bytearray constructor follows CPython 3.14.7 Objects/bytearrayobject.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// `bytearray(...)`, which binds exactly as `bytes` does and then takes a private copy.
/// </summary>
internal static class PythonByteArrayConstruction
{
    internal static PythonByteArrayValue Construct(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    ) => ConstructWithKeywords(arguments, [], [], span);

    internal static PythonByteArrayValue ConstructWithKeywords(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var bytes = PythonBytesConstruction.ConstructNamed(
            "bytearray",
            positional,
            names,
            values,
            span
        );
        // `bytes` hands back interned singletons for short values, so the mutable result must
        // take its own array rather than share one.
        return new PythonByteArrayValue([.. bytes.Value]);
    }
}
