// The bytearray type follows CPython 3.14.7 Objects/bytearrayobject.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Runtime.CompilerServices;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// `bytearray`: the mutable sibling of `bytes`.
/// </summary>
/// <remarks>
/// It compares by value against both `bytes` and `bytearray`, but is unhashable and has no
/// value identity of its own — two equal bytearrays are separate objects, and `+=` mutates in
/// place and returns the receiver. Storage is a plain array that a resize replaces.
/// </remarks>
internal sealed record PythonByteArrayValue : PythonValue
{
    internal PythonByteArrayValue(byte[] value) => Value = value;

    /// <summary>The live contents; a resize replaces the array rather than growing it.</summary>
    internal byte[] Value { get; set; }

    internal override string ToDisplayString() => $"bytearray({PythonBytesText.Represent(Value)})";

    // A mutable sequence has no value identity: equality and identity agree.
    public bool Equals(PythonByteArrayValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}
