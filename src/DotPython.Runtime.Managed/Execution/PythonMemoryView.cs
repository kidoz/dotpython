// The memoryview type follows CPython 3.14.7 Objects/memoryobject.c and the buffer protocol
// it exposes over the represented bytes-like objects:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Numerics;
using System.Runtime.CompilerServices;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// A view over the bytes of another object: `memoryview` in the way this runtime can carry
/// it — a shape, the byte strides that walk it, and a format from the native
/// single-character set.
/// </summary>
/// <remarks>
/// A view over a bytearray is writable and holds an export on it, so the bytearray refuses
/// to resize until every view of it is released. A view whose format is wider than one byte
/// reads and writes machine-order values, which is what `cast` chooses.
/// </remarks>
internal sealed record PythonMemoryViewValue : PythonValue
{
    /// <summary>
    /// The object the bytes came from, the storage itself, the byte offset the first element
    /// starts at, the shape, the byte strides that walk it, the format character and whether
    /// the underlying object may be written.
    /// </summary>
    internal PythonMemoryViewValue(
        PythonValue source,
        byte[] bytes,
        int offset,
        int[] shape,
        int[] strides,
        string format,
        bool writable
    )
    {
        Source = source;
        Bytes = bytes;
        Offset = offset;
        Shape = shape;
        Strides = strides;
        Format = format;
        Writable = writable;
    }

    /// <summary>The object the view reads its bytes from, which `obj` reports.</summary>
    internal PythonValue Source { get; }

    /// <summary>The storage the view reads and writes, shared with the source.</summary>
    internal byte[] Bytes { get; }

    internal int Offset { get; }

    internal int[] Shape { get; }

    /// <summary>How many bytes to step along each dimension, as CPython reports them.</summary>
    internal int[] Strides { get; }

    /// <summary>One native format character, optionally prefixed with `@` by `cast`.</summary>
    internal string Format { get; }

    internal bool Writable { get; }

    /// <summary>A view stops answering once it is released.</summary>
    internal bool Released { get; set; }

    /// <summary>
    /// The object that handed this view out through `__buffer__`, whose
    /// `__release_buffer__` runs when it is released, or null for a view this runtime
    /// made itself.
    /// </summary>
    internal PythonValue? BufferOwner { get; set; }

    /// <summary>The format character the view reads and writes with.</summary>
    internal char FormatChar => Format[^1];

    /// <summary>How many bytes one element takes, by format character.</summary>
    internal int ItemSize => MemoryViewItemSize(FormatChar);

    /// <summary>How many elements the view exposes, over every dimension.</summary>
    internal int Length
    {
        get
        {
            var length = 1;
            foreach (var dimension in Shape)
                length *= dimension;
            return length;
        }
    }

    /// <summary>The first dimension, which is what `len()` and slicing work on.</summary>
    internal int FirstDimension => Shape[0];

    internal int ByteCount => Length * ItemSize;

    internal void RequireLive() => PythonMemoryViewMethods.RequireLive(this);

    internal override string ToDisplayString() =>
        $"<memory at 0x{RuntimeHelpers.GetHashCode(this):x}>";

    internal override string ToRepresentationString() => ToDisplayString();

    /// <summary>
    /// The bytes the view currently exposes, in view order — or column-major, which is what
    /// `tobytes('F')` asks for and what only a multi-dimensional view can tell apart.
    /// </summary>
    internal byte[] Materialize(bool fortran = false)
    {
        var result = new byte[ByteCount];
        if (!fortran || Shape.Length == 1)
        {
            for (var index = 0; index < Length; index++)
                Array.Copy(Bytes, ElementOffset(index), result, index * ItemSize, ItemSize);
            return result;
        }
        // Fortran order walks the first dimension fastest, which the flat index does not.
        var indices = new int[Shape.Length];
        for (var element = 0; element < Length; element++)
        {
            var offset = Offset;
            for (var dimension = 0; dimension < Shape.Length; dimension++)
                offset += indices[dimension] * Strides[dimension];
            Array.Copy(Bytes, offset, result, element * ItemSize, ItemSize);
            for (var dimension = 0; dimension < Shape.Length; dimension++)
            {
                if (++indices[dimension] < Shape[dimension])
                    break;
                indices[dimension] = 0;
            }
        }
        return result;
    }

    /// <summary>Where an element starts, counting the elements in C order.</summary>
    internal int ElementOffset(int index)
    {
        var offset = Offset;
        for (var dimension = Shape.Length - 1; dimension >= 0; dimension--)
        {
            var size = Shape[dimension];
            offset += (index % size) * Strides[dimension];
            index /= size;
        }
        return offset;
    }

    /// <summary>The elements as lists, nested one list per dimension.</summary>
    internal PythonValue ToList()
    {
        var cursor = 0;
        return Build(0);

        PythonValue Build(int dimension)
        {
            var size = Shape[dimension];
            var items = new PythonValue[size];
            for (var index = 0; index < size; index++)
                items[index] =
                    dimension == Shape.Length - 1 ? Read(cursor++) : Build(dimension + 1);
            return new PythonListValue([.. items]);
        }
    }

    /// <summary>Whether the view walks its elements the way C order would.</summary>
    internal bool IsCContiguous => MatchesOrder(fromLastDimension: true);

    /// <summary>Whether the view walks its elements the way Fortran order would.</summary>
    internal bool IsFContiguous => MatchesOrder(fromLastDimension: false);

    internal bool IsContiguous => IsCContiguous || IsFContiguous;

    /// <summary>
    /// Whether the strides are the ones an order would hand out. A dimension of one element
    /// constrains nothing, which is how CPython decides it as well.
    /// </summary>
    private bool MatchesOrder(bool fromLastDimension)
    {
        var stride = ItemSize;
        for (var step = 0; step < Shape.Length; step++)
        {
            var dimension = fromLastDimension ? Shape.Length - 1 - step : step;
            if (Shape[dimension] <= 1)
                continue;
            if (Strides[dimension] != stride)
                return false;
            stride *= Shape[dimension];
        }
        return true;
    }

    /// <summary>The strides that walk a shape in C order, which is what `cast` gives it.</summary>
    internal static int[] ContiguousStrides(int[] shape, int itemSize)
    {
        var strides = new int[shape.Length];
        var stride = itemSize;
        for (var dimension = shape.Length - 1; dimension >= 0; dimension--)
        {
            strides[dimension] = stride;
            stride *= shape[dimension];
        }
        return strides;
    }

    /// <summary>How many bytes one element of a format takes.</summary>
    internal static int MemoryViewItemSize(char format) =>
        format switch
        {
            'b' or 'B' or 'c' => 1,
            'h' or 'H' or 'e' => 2,
            'i' or 'I' or 'f' => 4,
            'l' or 'L' or 'q' or 'Q' or 'd' or 'n' or 'N' or 'P' => 8,
            _ => 1,
        };

    /// <summary>One element as a Python value, in the view's format.</summary>
    internal PythonValue Read(int index)
    {
        var offset = ElementOffset(index);
        return FormatChar switch
        {
            'c' => new PythonByteSequenceValue([Bytes[offset]]),
            'b' => PythonWholeNumberValue.Create((sbyte)Bytes[offset]),
            'B' => PythonWholeNumberValue.Create(Bytes[offset]),
            _ => PythonWholeNumberValue.Create(
                MemoryViewMachineOrder(offset, MemoryViewItemSize(FormatChar))
            ),
        };
    }

    /// <summary>Writes one element, reporting the format's own refusal for a wrong type.</summary>
    internal void Write(int index, PythonValue value)
    {
        if (!Writable)
            throw MemoryViewFault("cannot modify read-only memory", "TypeError");
        var offset = ElementOffset(index);
        switch (FormatChar)
        {
            case 'B':
                Bytes[offset] = (byte)MemoryViewByte(value, Format);
                return;
            case 'b':
                Bytes[offset] = (byte)(sbyte)MemoryViewSigned(value, Format);
                return;
            case 'c':
                if (value is not PythonByteSequenceValue { Value.Length: 1 } single)
                    throw MemoryViewFault(
                        $"memoryview: invalid type for format '{Format}'",
                        "TypeError"
                    );
                Bytes[offset] = single.Value[0];
                return;
            default:
                var number = MemoryViewNumber(value, Format);
                var width = MemoryViewItemSize(FormatChar);
                for (var position = 0; position < width; position++)
                    Bytes[offset + position] = (byte)((number >> (8 * position)) & 0xFF);
                return;
        }
    }

    private BigInteger MemoryViewMachineOrder(int offset, int width) =>
        new(Bytes.AsSpan(offset, width), isUnsigned: true, isBigEndian: false);

    private static byte MemoryViewByte(PythonValue value, string format) =>
        value switch
        {
            PythonWholeNumberValue whole when whole.Value >= 0 && whole.Value <= 255 => (byte)
                whole.Value,
            PythonTruthValue truth => truth.Value ? (byte)1 : (byte)0,
            _ => throw MemoryViewFault(
                $"memoryview: invalid type for format '{format}'",
                "TypeError"
            ),
        };

    private static BigInteger MemoryViewSigned(PythonValue value, string format) =>
        value switch
        {
            PythonWholeNumberValue whole when whole.Value >= -128 && whole.Value <= 127 =>
                whole.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => throw MemoryViewFault(
                $"memoryview: invalid type for format '{format}'",
                "TypeError"
            ),
        };

    private static BigInteger MemoryViewNumber(PythonValue value, string format) =>
        value switch
        {
            PythonWholeNumberValue whole => whole.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            _ => throw MemoryViewFault(
                $"memoryview: invalid type for format '{format}'",
                "TypeError"
            ),
        };

    internal static PythonRuntimeException MemoryViewFault(string message, string type) =>
        ManagedObjectProtocols.Fault("DPY4003", message, default, type);

    // A view is a window rather than a value, so identity is the answer to equality unless
    // the two views expose the same bytes, which the protocol layer decides.
    public bool Equals(PythonMemoryViewValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}
