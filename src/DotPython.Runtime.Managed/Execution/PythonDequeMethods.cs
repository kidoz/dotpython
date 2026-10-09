// The `collections.deque` surface follows CPython 3.14.7 Modules/_collectionsmodule.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The `collections.deque` type: how a bounded double-ended queue is built, the members it
/// carries, and the methods that push and pop at either end.
/// </summary>
internal static class PythonDequeMethods
{
    /// <summary>The `collections.deque` type object.</summary>
    internal static readonly PythonBuiltinTypeValue Type = new(
        "deque",
        Construct,
        ConstructWithKeywords
    )
    {
        ModuleName = "collections",
    };

    /// <summary>The methods a deque answers after its `maxlen` member.</summary>
    private static readonly string[] MethodNames =
    [
        "append",
        "appendleft",
        "clear",
        "copy",
        "count",
        "extend",
        "extendleft",
        "index",
        "insert",
        "pop",
        "popleft",
        "remove",
        "reverse",
        "rotate",
        "__copy__",
        "__reduce__",
    ];

    internal static void AddMemberNames(List<string> names)
    {
        if (!names.Contains("maxlen"))
            names.Add("maxlen");
        foreach (var name in MethodNames)
        {
            if (!names.Contains(name))
                names.Add(name);
        }
    }

    /// <summary>A deque over the elements an iterable produced, keeping the last `maxlen`.</summary>
    internal static PythonDequeValue FromIterable(PythonValue source, int? maxLength, TextSpan span)
    {
        var elements = new List<PythonValue>();
        var iterator = ManagedObjectProtocols.GetIterator(source, span);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var element, span))
        {
            elements.Add(element);
            if (maxLength is { } limit && elements.Count > limit)
                elements.RemoveAt(0);
        }
        return new PythonDequeValue(elements, maxLength);
    }

    /// <summary>The members a deque answers, or null when the name is not one of them.</summary>
    internal static PythonValue? GetAttribute(PythonDequeValue deque, string name, TextSpan span)
    {
        switch (name)
        {
            case "maxlen":
                return deque.MaxLength is { } limit
                    ? PythonWholeNumberValue.Create(limit)
                    : PythonNoneValue.Instance;
            case "append":
                return Single(
                    deque,
                    name,
                    (_, arguments) => Push(deque, arguments[0], front: false)
                );
            case "appendleft":
                return Single(
                    deque,
                    name,
                    (_, arguments) => Push(deque, arguments[0], front: true)
                );
            case "extend":
                return Single(
                    deque,
                    name,
                    (_, arguments) => Extend(deque, arguments[0], front: false, span)
                );
            case "extendleft":
                return Single(
                    deque,
                    name,
                    (_, arguments) => Extend(deque, arguments[0], front: true, span)
                );
            case "insert":
                return Exact(deque, name, (_, arguments) => Insert(deque, arguments, span));
            case "pop":
                return Nullary(deque, name, (_, _) => Pop(deque, front: false, span));
            case "popleft":
                return Nullary(deque, name, (_, _) => Pop(deque, front: true, span));
            case "remove":
                return Single(deque, name, (_, arguments) => Remove(deque, arguments[0], span));
            case "rotate":
                return Window(deque, name, 0, 1, (_, arguments) => Rotate(deque, arguments, span));
            case "clear":
                return Nullary(deque, name, (_, _) => Clear(deque));
            case "reverse":
                return Nullary(deque, name, (_, _) => Reverse(deque));
            case "copy":
            case "__copy__":
                return Nullary(deque, name, (_, _) => CopyOf(deque));
            case "count":
                return Single(deque, name, (_, arguments) => Count(deque, arguments[0]));
            case "index":
                return Window(deque, name, 1, 3, (_, arguments) => Index(deque, arguments, span));
            case "__reduce__":
                return Nullary(deque, name, (_, _) => Reduce(deque, span));
            default:
                return null;
        }
    }

    private static readonly Dictionary<string, PythonMethodDescriptorValue> Descriptors = [];

    /// <summary>The methods a deque publishes through its type object.</summary>
    internal static PythonMethodDescriptorValue? GetTypeDescriptor(string typeName, string name)
    {
        if (typeName != "deque" || Array.IndexOf(MethodNames, name) < 0)
            return null;
        lock (Descriptors)
        {
            if (!Descriptors.TryGetValue(name, out var descriptor))
            {
                descriptor = new PythonMethodDescriptorValue(
                    Type,
                    name,
                    new PythonProtocolFunctionValue(
                        name,
                        (receiver, arguments) =>
                        {
                            var deque = (PythonDequeValue)receiver!;
                            var bound = (PythonBoundMethodValue)GetAttribute(deque, name, default)!;
                            return bound.Function.Invoke(deque, arguments);
                        },
                        (_, _, _, _) =>
                            throw Fault(
                                $"deque.{name}() takes no keyword arguments",
                                "TypeError",
                                default
                            )
                    )
                );
                Descriptors[name] = descriptor;
            }
            return descriptor;
        }
    }

    /// <summary>`d + other`: only another deque concatenates, bounded by the left operand.</summary>
    internal static PythonDequeValue Concatenate(
        PythonDequeValue left,
        PythonValue right,
        TextSpan span
    )
    {
        if (right is not PythonDequeValue other)
            throw Fault(
                $"can only concatenate deque (not \"{ManagedObjectProtocols.GetTypeName(right)}\") "
                    + "to deque",
                "TypeError",
                span
            );
        return Bounded(left, [.. left.Elements, .. other.Elements], span);
    }

    /// <summary>`d.__init__(iterable, maxlen)`, which clears the deque and starts it over.</summary>
    internal static PythonDequeValue Reinitialize(
        PythonDequeValue deque,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        var rebuilt = Build(arguments, [], [], span);
        deque.Elements.Clear();
        deque.Elements.AddRange(rebuilt.Elements);
        deque.MaxLength = rebuilt.MaxLength;
        deque.Version++;
        return deque;
    }

    /// <summary>`d * n` and `n * d`, which repeat the contents and keep the bound.</summary>
    internal static PythonDequeValue Repeat(
        PythonDequeValue deque,
        PythonValue count,
        TextSpan span
    )
    {
        var repeated = new List<PythonValue>();
        var times = PythonSequenceRepetition.GetCount(count, span);
        for (var index = 0; index < times; index++)
            repeated.AddRange(deque.Elements);
        return Bounded(deque, repeated, span);
    }

    /// <summary>`d += iterable`, which extends in place and answers the deque itself.</summary>
    internal static PythonDequeValue ExtendInPlace(
        PythonDequeValue deque,
        PythonValue source,
        TextSpan span
    )
    {
        Extend(deque, source, front: false, span);
        return deque;
    }

    /// <summary>`d *= n`, in place.</summary>
    internal static PythonDequeValue RepeatInPlace(
        PythonDequeValue deque,
        PythonValue count,
        TextSpan span
    )
    {
        var times = Whole(count, span);
        var repeated = Bounded(
            deque,
            [.. Enumerable.Repeat(deque.Elements, (int)times).SelectMany(elements => elements)],
            span
        );
        deque.Elements.Clear();
        deque.Elements.AddRange(repeated.Elements);
        deque.Version++;
        return deque;
    }

    /// <summary>The last elements a bounded deque keeps, in the way CPython builds one.</summary>
    private static PythonDequeValue Bounded(
        PythonDequeValue deque,
        List<PythonValue> elements,
        TextSpan span
    )
    {
        var result = new PythonDequeValue([], deque.MaxLength);
        foreach (var element in elements)
            Push(result, element, front: false);
        return result;
    }

    private static PythonDequeValue Construct(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    ) => Build(arguments, [], [], span);

    private static PythonDequeValue ConstructWithKeywords(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    ) => Build(positional, keywordNames, keywordValues, span);

    private static PythonDequeValue Build(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (positional.Count + keywordNames.Count > 2)
            throw Fault(
                $"deque() takes at most 2 arguments ({positional.Count + keywordNames.Count} given)",
                "TypeError",
                span
            );
        var slots = PythonKeywordArguments.Bind(
            "deque",
            ["iterable", "maxlen"],
            0,
            positional,
            keywordNames,
            keywordValues,
            span,
            typeStyleErrors: true
        );
        // The bound is settled before the iterable is walked, as CPython settles its
        // arguments first.
        var maxLength = slots[1] is { } limit ? MaxLength(limit, span) : (int?)null;
        return slots[0] is { } source
            ? FromIterable(source, maxLength, span)
            : new PythonDequeValue([], maxLength);
    }

    /// <summary>`maxlen`: a whole number of elements, or nothing at all.</summary>
    private static int MaxLength(PythonValue value, TextSpan span)
    {
        BigInteger limit;
        if (value is PythonWholeNumberValue whole)
            limit = whole.Value;
        else if (value is PythonTruthValue truth)
            limit = truth.Value ? BigInteger.One : BigInteger.Zero;
        else
            throw Fault("an integer is required", "TypeError", span);
        if (limit < 0)
            throw Fault("maxlen must be non-negative", "ValueError", span);
        if (limit > int.MaxValue)
            throw Fault("Python int too large to convert to C ssize_t", "OverflowError", span);
        return (int)limit;
    }

    /// <summary>`append` and `appendleft`, which drop an element when the deque is full.</summary>
    private static PythonNoneValue Push(PythonDequeValue deque, PythonValue value, bool front)
    {
        if (deque.IsFull)
        {
            if (deque.MaxLength == 0)
                return PythonNoneValue.Instance;
            deque.Elements.RemoveAt(front ? deque.Elements.Count - 1 : 0);
        }
        if (front)
            deque.Elements.Insert(0, value);
        else
            deque.Elements.Add(value);
        deque.Version++;
        return PythonNoneValue.Instance;
    }

    /// <summary>`extend` and `extendleft`: every element is taken, and the bound keeps the last.</summary>
    private static PythonNoneValue Extend(
        PythonDequeValue deque,
        PythonValue source,
        bool front,
        TextSpan span
    )
    {
        var iterator = ManagedObjectProtocols.GetIterator(source, span);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var element, span))
            Push(deque, element, front);
        return PythonNoneValue.Instance;
    }

    private static PythonNoneValue Insert(
        PythonDequeValue deque,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (deque.IsFull)
            throw Fault("deque already at its maximum size", "IndexError", span);
        var index = Whole(arguments[0], span);
        if (index < 0)
            index += deque.Elements.Count;
        index = BigInteger.Clamp(index, BigInteger.Zero, deque.Elements.Count);
        deque.Elements.Insert((int)index, arguments[1]);
        deque.Version++;
        return PythonNoneValue.Instance;
    }

    /// <summary>`pop` and `popleft`, which refuse an empty deque.</summary>
    private static PythonValue Pop(PythonDequeValue deque, bool front, TextSpan span)
    {
        if (deque.Elements.Count == 0)
            throw Fault("pop from an empty deque", "IndexError", span);
        var index = front ? 0 : deque.Elements.Count - 1;
        var value = deque.Elements[index];
        deque.Elements.RemoveAt(index);
        deque.Version++;
        return value;
    }

    private static PythonNoneValue Remove(PythonDequeValue deque, PythonValue value, TextSpan span)
    {
        for (var index = 0; index < deque.Elements.Count; index++)
        {
            if (!ManagedObjectProtocols.AreEqual(deque.Elements[index], value))
                continue;
            deque.Elements.RemoveAt(index);
            deque.Version++;
            return PythonNoneValue.Instance;
        }
        throw Fault("deque.remove(x): x not in deque", "ValueError", span);
    }

    /// <summary>`rotate(n=1)`, which moves elements from one end to the other.</summary>
    private static PythonNoneValue Rotate(
        PythonDequeValue deque,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        var length = deque.Elements.Count;
        if (length == 0)
            return PythonNoneValue.Instance;
        var count = arguments.Count == 0 ? BigInteger.One : Whole(arguments[0], span);
        var steps = (int)(((count % length) + length) % length);
        for (var step = 0; step < steps; step++)
        {
            var last = deque.Elements[^1];
            deque.Elements.RemoveAt(length - 1);
            deque.Elements.Insert(0, last);
        }
        if (steps != 0)
            deque.Version++;
        return PythonNoneValue.Instance;
    }

    private static PythonNoneValue Clear(PythonDequeValue deque)
    {
        if (deque.Elements.Count > 0)
            deque.Version++;
        deque.Elements.Clear();
        return PythonNoneValue.Instance;
    }

    private static PythonNoneValue Reverse(PythonDequeValue deque)
    {
        deque.Elements.Reverse();
        deque.Version++;
        return PythonNoneValue.Instance;
    }

    private static PythonDequeValue CopyOf(PythonDequeValue deque) =>
        new([.. deque.Elements], deque.MaxLength);

    private static PythonWholeNumberValue Count(PythonDequeValue deque, PythonValue value)
    {
        var count = 0;
        foreach (var element in deque.Elements)
        {
            if (ManagedObjectProtocols.AreEqual(element, value))
                count++;
        }
        return PythonWholeNumberValue.Create(count);
    }

    /// <summary>`index(x, start, stop)`: where the value first appears in the window.</summary>
    private static PythonWholeNumberValue Index(
        PythonDequeValue deque,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        var length = deque.Elements.Count;
        var start = arguments.Count > 1 ? Whole(arguments[1], span) : BigInteger.Zero;
        var stop = arguments.Count > 2 ? Whole(arguments[2], span) : length;
        if (start < 0)
            start += length;
        if (stop < 0)
            stop += length;
        start = BigInteger.Clamp(start, BigInteger.Zero, length);
        stop = BigInteger.Clamp(stop, BigInteger.Zero, length);
        for (var index = (int)start; index < (int)stop; index++)
        {
            if (ManagedObjectProtocols.AreEqual(deque.Elements[index], arguments[0]))
                return PythonWholeNumberValue.Create(index);
        }
        throw Fault("deque.index(x): x not in deque", "ValueError", span);
    }

    /// <summary>`__reduce__`: the type, no arguments, and the iterator over the contents.</summary>
    private static PythonTupleValue Reduce(PythonDequeValue deque, TextSpan span) =>
        new PythonTupleValue([
            Type,
            new PythonTupleValue([]),
            PythonNoneValue.Instance,
            ManagedObjectProtocols.GetIterator(deque, span),
        ]);

    private static BigInteger Whole(PythonValue value, TextSpan span)
    {
        if (value is PythonWholeNumberValue whole)
            return whole.Value;
        if (value is PythonTruthValue truth)
            return truth.Value ? BigInteger.One : BigInteger.Zero;
        throw Fault(
            $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be interpreted "
                + "as an integer",
            "TypeError",
            span
        );
    }

    private static PythonBoundMethodValue Nullary(
        PythonDequeValue deque,
        string name,
        Func<PythonDequeValue, IReadOnlyList<PythonValue>, PythonValue> body
    ) =>
        Bound(
            deque,
            name,
            (_, arguments) =>
                arguments.Count == 0
                    ? body(deque, arguments)
                    : throw Fault(
                        $"deque.{name}() takes no arguments ({arguments.Count} given)",
                        "TypeError",
                        default
                    )
        );

    private static PythonBoundMethodValue Single(
        PythonDequeValue deque,
        string name,
        Func<PythonDequeValue, IReadOnlyList<PythonValue>, PythonValue> body
    ) =>
        Bound(
            deque,
            name,
            (_, arguments) =>
                arguments.Count == 1
                    ? body(deque, arguments)
                    : throw Fault(
                        $"deque.{name}() takes exactly one argument ({arguments.Count} given)",
                        "TypeError",
                        default
                    )
        );

    /// <summary>A fixed count, reported the way CPython's `insert` reports it.</summary>
    private static PythonBoundMethodValue Exact(
        PythonDequeValue deque,
        string name,
        Func<PythonDequeValue, IReadOnlyList<PythonValue>, PythonValue> body
    ) =>
        Bound(
            deque,
            name,
            (_, arguments) =>
                arguments.Count == 2
                    ? body(deque, arguments)
                    : throw Fault(
                        $"{name} expected 2 arguments, got {arguments.Count}",
                        "TypeError",
                        default
                    )
        );

    /// <summary>A count between a minimum and a maximum, as `index` and `rotate` report it.</summary>
    private static PythonBoundMethodValue Window(
        PythonDequeValue deque,
        string name,
        int minimum,
        int maximum,
        Func<PythonDequeValue, IReadOnlyList<PythonValue>, PythonValue> body
    ) =>
        Bound(
            deque,
            name,
            (_, arguments) =>
            {
                if (arguments.Count < minimum)
                    throw Fault(
                        $"{name} expected at least {minimum} argument{(minimum == 1 ? "" : "s")}, "
                            + $"got {arguments.Count}",
                        "TypeError",
                        default
                    );
                if (arguments.Count > maximum)
                    throw Fault(
                        $"{name} expected at most {maximum} argument{(maximum == 1 ? "" : "s")}, "
                            + $"got {arguments.Count}",
                        "TypeError",
                        default
                    );
                return body(deque, arguments);
            }
        );

    private static PythonBoundMethodValue Bound(
        PythonDequeValue deque,
        string name,
        Func<PythonDequeValue, IReadOnlyList<PythonValue>, PythonValue> invoke
    ) =>
        new PythonBoundMethodValue(
            name,
            deque,
            new PythonProtocolFunctionValue(
                name,
                (_, arguments) => invoke(deque, arguments),
                (_, _, _, _) =>
                    throw Fault($"deque.{name}() takes no keyword arguments", "TypeError", default)
            )
        );

    private static PythonRuntimeException Fault(string message, string type, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, type);
}
