// The `collections.deque` type follows CPython 3.14.7 Modules/_collectionsmodule.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Runtime.CompilerServices;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// `collections.deque`: a double-ended queue that can be bounded, so an append at one end
/// drops the element at the other.
/// </summary>
/// <remarks>
/// It compares by content against another deque and orders lexicographically, but is
/// unhashable and has no value identity of its own — like a bytearray, it is one mutable
/// object that `+=` and `*=` mutate in place.
/// </remarks>
internal sealed record PythonDequeValue : PythonValue
{
    internal PythonDequeValue(List<PythonValue> elements, int? maxLength)
    {
        Elements = elements;
        MaxLength = maxLength;
    }

    /// <summary>The live contents, oldest first.</summary>
    internal List<PythonValue> Elements { get; }

    /// <summary>
    /// The bound on how many elements the deque keeps, or null when unbounded. `__init__`
    /// may set a new one.
    /// </summary>
    internal int? MaxLength { get; set; }

    /// <summary>
    /// How many mutations the deque has seen. An iterator carries the count it started with,
    /// because CPython refuses to iterate a deque that changed under it.
    /// </summary>
    internal int Version { get; set; }

    /// <summary>Whether the deque holds as many elements as it may.</summary>
    internal bool IsFull => MaxLength is { } limit && Elements.Count >= limit;

    internal override string ToDisplayString()
    {
        if (!PythonRepresentationGuard.TryEnter(this))
            return "[...]";
        try
        {
            var contents = new PythonListValue(Elements).ToRepresentationString();
            return MaxLength is { } limit
                ? $"deque({contents}, maxlen={limit})"
                : $"deque({contents})";
        }
        finally
        {
            PythonRepresentationGuard.Exit(this);
        }
    }

    // A mutable sequence has no value identity: equality and identity agree.
    public bool Equals(PythonDequeValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}
