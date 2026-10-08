// The `itertools` surface follows CPython 3.14.7 Modules/itertoolsmodule.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Collections.Concurrent;
using System.Numerics;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The bounded <c>itertools</c> surface: the combinatoric and lazy-streaming iterators the
/// runtime's iterator machinery can drive.
/// </summary>
/// <remarks>
/// Each name in the module is a type object mirrored on CPython's C types: <c>count</c> is a
/// class, <c>count(1)</c> is one of its instances, and <c>type(count(1)) is count</c>. The
/// instance carries a <see cref="Cursor"/> payload holding the iterator's state in a closure,
/// which is what makes <c>count()</c> infinite, <c>islice</c> non-over-consuming and
/// <c>groupby</c> share its underlying iterator.
/// </remarks>
internal static class PythonItertools
{
    private const string ModuleName = "itertools";

    /// <summary>What an iterator instance carries: its step function and optional repr.</summary>
    private sealed record Cursor(
        Func<(bool HasValue, PythonValue Value)> MoveNext,
        Func<string>? Describe = null
    );

    /// <summary>
    /// One function's argument shape, mirroring the keyword and positional limits CPython's
    /// argument parser enforces for it.
    /// </summary>
    private sealed record Arity(
        string Name,
        string[] Parameters,
        int Required,
        int TotalMax,
        string TotalMessage,
        int PositionalMax = int.MaxValue,
        string? PositionalMessage = null
    );

    private static readonly ConcurrentDictionary<string, PythonManagedTypeValue> IteratorTypes =
        new(StringComparer.Ordinal);

    private static readonly string[] NoParameters = [];

    private static readonly PythonNoneValue None = PythonNoneValue.Instance;

    /// <summary>How many pools `product(repeat=…)` may expand to before refusing.</summary>
    private const long MaxProductPools = 1_000_000;

    /// <summary>How many copies `tee` may hand out before refusing.</summary>
    private const long MaxTeeIterators = 1_000_000;

    private const string IsliceStartMessage =
        "Indices for islice() must be None or an integer: 0 <= x <= sys.maxsize.";

    private const string IsliceStopMessage =
        "Stop argument for islice() must be None or an integer: 0 <= x <= sys.maxsize.";

    private const string IsliceStepMessage =
        "Step for islice() must be a positive integer or None.";

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue("chain", ChainType);
        globals.SetValue("count", CreateCount());
        globals.SetValue("cycle", CreateCycle());
        globals.SetValue("repeat", CreateRepeat());
        globals.SetValue("islice", CreateIslice());
        globals.SetValue("product", CreateProduct());
        globals.SetValue("combinations", CreateCombinations());
        globals.SetValue("permutations", CreatePermutations());
        globals.SetValue(
            "combinations_with_replacement",
            CreateCombinationsWithReplacement()
        );
        globals.SetValue("zip_longest", CreateZipLongest());
        globals.SetValue("groupby", CreateGroupBy());
        globals.SetValue("takewhile", CreateTakeWhile());
        globals.SetValue("dropwhile", CreateDropWhile());
        globals.SetValue("filterfalse", CreateFilterFalse());
        globals.SetValue("compress", CreateCompress());
        globals.SetValue("accumulate", CreateAccumulate());
        globals.SetValue("starmap", CreateStarMap());
        globals.SetValue("pairwise", CreatePairwise());
        globals.SetValue("tee", CreateTee());
        globals.SetValue("batched", CreateBatched());
    }

    // ---- iterator plumbing -------------------------------------------------------------

    /// <summary>
    /// The type object for an iterator kind, created once per name so
    /// <c>type(a) is type(b)</c> holds across every instance of that kind.
    /// </summary>
    private static PythonManagedTypeValue IteratorType(string name, string? doc = null) =>
        IteratorTypes.GetOrAdd(name, static (key, documentation) => Build(key, documentation), doc);

    private static PythonManagedTypeValue Build(string name, string? documentation)
    {
        var type = new PythonManagedTypeValue(name) { Module = ModuleName };
        if (documentation is not null)
        {
            type.Attributes["__doc__"] = new PythonTextValue(documentation);
        }

        type.Attributes["__iter__"] = new PythonProtocolFunctionValue(
            "__iter__",
            static (self, _) => self!
        );
        type.Attributes["__next__"] = new PythonProtocolFunctionValue(
            "__next__",
            static (self, _) => Advance(self!)
        );
        return type;
    }

    /// <summary>
    /// Declares a module name as a type whose construction runs <paramref name="body"/>:
    /// <c>__new__</c> drops the implicitly prepended class and hands the call to the body.
    /// </summary>
    private static PythonManagedTypeValue FunctionType(
        string name,
        string documentation,
        Func<
            IReadOnlyList<PythonValue>,
            IReadOnlyList<string>,
            IReadOnlyList<PythonValue>,
            TextSpan,
            PythonValue
        > body
    )
    {
        var type = IteratorType(name, documentation);
        type.Attributes["__new__"] = new PythonProtocolFunctionValue(
            "__new__",
            (_, arguments) => body(Arguments(arguments, 1), NoParameters, [], default),
            (_, arguments, keywordNames, keywordValues) =>
                body(Arguments(arguments, 1), keywordNames, keywordValues, default)
        );
        return type;
    }

    /// <summary>Declares the CPython-exact `repr` of the iterators that have one.</summary>
    private static void DescribeRepresentation(PythonManagedTypeValue type) =>
        type.Attributes["__repr__"] = new PythonProtocolFunctionValue(
            "__repr__",
            static (self, _) =>
                new PythonTextValue(
                    ((PythonManagedObjectValue)self!).Payload is Cursor { Describe: { } describe }
                        ? describe()
                        : string.Empty
                )
        );

    private static PythonValue Advance(PythonValue self)
    {
        var cursor = (Cursor)((PythonManagedObjectValue)self).Payload!;
        var step = cursor.MoveNext();
        if (!step.HasValue)
        {
            throw new PythonRaisedException(
                ManagedObjectProtocols.CreateStopIteration(PythonNoneValue.Instance)
            );
        }

        return step.Value;
    }

    private static PythonManagedObjectValue Iterator(
        string name,
        Func<(bool HasValue, PythonValue Value)> moveNext,
        Func<string>? describe = null
    ) => Iterator(IteratorType(name), moveNext, describe);

    private static PythonManagedObjectValue Iterator(
        PythonManagedTypeValue type,
        Func<(bool HasValue, PythonValue Value)> moveNext,
        Func<string>? describe = null
    ) => new(type, new Cursor(moveNext, describe));

    private static PythonIteratorValue Source(PythonValue iterable, TextSpan span) =>
        ManagedObjectProtocols.GetIterator(iterable, span);

    private static List<PythonValue> Materialize(PythonValue iterable, TextSpan span) =>
        ManagedObjectProtocols.MaterializeValues(iterable, span);

    private static PythonRuntimeException Error(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, "TypeError");

    private static PythonRuntimeException ValueError(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, "ValueError");

    private static string Plural(int count) => count == 1 ? string.Empty : "s";

    private static PythonValue[] Arguments(IReadOnlyList<PythonValue> arguments, int start)
    {
        var rest = new PythonValue[arguments.Count - start];
        for (var index = start; index < arguments.Count; index++)
        {
            rest[index - start] = arguments[index];
        }

        return rest;
    }

    // ---- argument binding --------------------------------------------------------------

    /// <summary>
    /// Binds positional and keyword arguments the way CPython's <c>_PyArg_UnpackKeywords</c>
    /// does for these iterators: the overall count is checked first, then the required
    /// slots, and only then is an unknown keyword named (with its closest parameter).
    /// </summary>
    private static PythonValue?[] Unpack(
        Arity arity,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var positionalCount = positional.Count;
        var keywordCount = keywordNames.Count;

        // The overall count is checked first, and a surplus of positionals, of keywords
        // and of both is reported differently.
        if (positionalCount + keywordCount > arity.TotalMax)
        {
            if (positionalCount > arity.TotalMax)
            {
                throw Error(
                    $"{arity.TotalMessage} ({positionalCount} given)",
                    span
                );
            }

            if (keywordCount > arity.TotalMax)
            {
                throw Error(
                    $"{arity.Name}() takes at most {arity.TotalMax} keyword argument{Plural(arity.TotalMax)} ({keywordCount} given)",
                    span
                );
            }

            throw Error(
                $"{arity.TotalMessage} ({positionalCount + keywordCount} given)",
                span
            );
        }

        if (positionalCount > arity.PositionalMax && arity.PositionalMessage is { } tooMany)
        {
            throw Error($"{tooMany} ({positionalCount} given)", span);
        }

        var slots = new PythonValue?[arity.Parameters.Length];
        for (var index = 0; index < positionalCount; index++)
        {
            slots[index] = positional[index];
        }

        // Keywords bind before the required slots are inspected, but a keyword that
        // collides with a positional, or names no parameter, is only reported after that
        // check: a missing required argument wins over either.
        string? keywordError = null;
        for (var index = 0; index < keywordCount; index++)
        {
            var keyword = keywordNames[index];
            var slot = Array.IndexOf(arity.Parameters, keyword);
            if (slot < 0)
            {
                keywordError ??=
                    $"{arity.Name}() got an unexpected keyword argument '{keyword}'"
                    + Suggestion(keyword, arity.Parameters);
                continue;
            }

            if (slot < positionalCount)
            {
                keywordError ??=
                    $"argument for {arity.Name}() given by name ('{keyword}') and position ({slot + 1})";
                continue;
            }

            slots[slot] = keywordValues[index];
        }

        for (var index = 0; index < arity.Required; index++)
        {
            if (slots[index] is null)
            {
                throw Error(
                    $"{arity.Name}() missing required argument '{arity.Parameters[index]}' (pos {index + 1})",
                    span
                );
            }
        }

        if (keywordError is not null)
        {
            throw Error(keywordError, span);
        }

        return slots;
    }

    /// <summary>
    /// CPython's "Did you mean" hint: the closest parameter, when twice the edit distance
    /// does not exceed the length of what was typed.
    /// </summary>
    private static string Suggestion(string keyword, string[] parameters)
    {
        var best = string.Empty;
        var bestDistance = int.MaxValue;
        foreach (var parameter in parameters)
        {
            var distance = EditDistance(keyword, parameter);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = parameter;
            }
        }

        return best.Length != 0 && bestDistance * 2 <= keyword.Length
            ? $". Did you mean '{best}'?"
            : string.Empty;
    }

    private static int EditDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
        {
            previous[index] = index;
        }

        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution =
                    previous[rightIndex - 1]
                    + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(
                    Math.Min(previous[rightIndex] + 1, current[rightIndex - 1] + 1),
                    substitution
                );
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static void RejectKeywords(
        string name,
        IReadOnlyList<string> keywordNames,
        TextSpan span
    )
    {
        if (keywordNames.Count != 0)
        {
            throw Error($"{name}() takes no keyword arguments", span);
        }
    }

    /// <summary>CPython's <c>_PyArg_UnpackTuple</c> count check.</summary>
    private static void RequireArity(string name, int count, int expected, TextSpan span)
    {
        if (count != expected)
        {
            throw Error(
                $"{name} expected {expected} argument{Plural(expected)}, got {count}",
                span
            );
        }
    }

    private static void RequireArityRange(
        string name,
        int count,
        int minimum,
        int maximum,
        TextSpan span
    )
    {
        if (count < minimum)
        {
            throw Error($"{name} expected at least {minimum} argument{Plural(minimum)}, got {count}", span);
        }

        if (count > maximum)
        {
            throw Error($"{name} expected at most {maximum} argument{Plural(maximum)}, got {count}", span);
        }
    }

    private static PythonValue RequireNumber(PythonValue value, TextSpan span) =>
        value switch
        {
            PythonTruthValue truth => PythonWholeNumberValue.Create(truth.Value ? 1 : 0),
            PythonWholeNumberValue or PythonFloatingPointValue or PythonComplexValue => value,
            _ => throw Error("a number is required", span),
        };

    /// <summary>CPython's <c>PyNumber_AsSsize_t</c>: an index, or a TypeError naming the type.</summary>
    private static long RequireIndex(PythonValue value, TextSpan span)
    {
        if (value is PythonTruthValue truth)
        {
            return truth.Value ? 1 : 0;
        }

        if (value is PythonWholeNumberValue whole)
        {
            return whole.Value > long.MaxValue ? long.MaxValue
                : whole.Value < long.MinValue ? long.MinValue
                : (long)whole.Value;
        }

        if (UserObjectProtocols.TryConvertToIndex(value, span, out var index))
        {
            return index > long.MaxValue ? long.MaxValue
                : index < long.MinValue ? long.MinValue
                : (long)index;
        }

        throw Error(
            $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be interpreted as an integer",
            span
        );
    }

    /// <summary>`r` and `n` are counts: an index that cannot exceed the machine's range.</summary>
    private static int RequireR(PythonValue value, TextSpan span)
    {
        var index = RequireIndex(value, span);
        return index > int.MaxValue ? int.MaxValue
            : index < int.MinValue ? int.MinValue
            : (int)index;
    }

    /// <summary>`times` clamps a negative count to zero rather than failing.</summary>
    private static long RequireTimes(PythonValue value, TextSpan span)
    {
        var count = RequireIndex(value, span);
        return count < 0 ? 0 : count;
    }

    private static string Describe(PythonValue value) => value.ToRepresentationString();

    private static bool IsOne(PythonValue value) =>
        value is PythonWholeNumberValue { Value: { IsOne: true } };

    // ---- count -------------------------------------------------------------------------

    private static PythonManagedTypeValue CreateCount()
    {
        var type = FunctionType("count", CountDoc, CoreCount);
        DescribeRepresentation(type);
        return type;
    }

    private static PythonManagedObjectValue CoreCount(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var slots = Unpack(CountArity, positional, keywordNames, keywordValues, span);
        var value = RequireNumber(slots[0] ?? PythonWholeNumberValue.Create(0), span);
        var step = RequireNumber(slots[1] ?? PythonWholeNumberValue.Create(1), span);
        return Iterator(
            "count",
            () =>
            {
                var result = value;
                value = PythonVirtualMachine.ApplyBinaryOperator(
                    PythonOpCode.BinaryAdd,
                    value,
                    step,
                    span
                );
                return (true, result);
            },
            () =>
                IsOne(step)
                    ? $"count({Describe(value)})"
                    : $"count({Describe(value)}, {Describe(step)})"
        );
    }

    // ---- repeat ------------------------------------------------------------------------

    private static PythonManagedTypeValue CreateRepeat()
    {
        var type = FunctionType("repeat", RepeatDoc, CoreRepeat);
        DescribeRepresentation(type);
        return type;
    }

    private static PythonManagedObjectValue CoreRepeat(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var slots = Unpack(RepeatArity, positional, keywordNames, keywordValues, span);
        var subject = slots[0]!;
        var remaining = slots[1] is null or PythonNoneValue ? -1L : RequireTimes(slots[1]!, span);
        return Iterator(
            "repeat",
            () =>
            {
                if (remaining == 0)
                {
                    return (false, None);
                }

                if (remaining > 0)
                {
                    remaining--;
                }

                return (true, subject);
            },
            () =>
                remaining < 0
                    ? $"repeat({Describe(subject)})"
                    : $"repeat({Describe(subject)}, {Describe(PythonWholeNumberValue.Create(remaining))})"
        );
    }

    // ---- cycle -------------------------------------------------------------------------

    private static PythonManagedTypeValue CreateCycle() => FunctionType("cycle", CycleDoc, CoreCycle);

    private static PythonManagedObjectValue CoreCycle(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        _ = keywordValues;
        RejectKeywords("cycle", keywordNames, span);
        RequireArity("cycle", positional.Count, 1, span);
        var source = Source(positional[0], span);
        var saved = new List<PythonValue>();
        var draining = true;
        var index = 0;
        return Iterator(
            "cycle",
            () =>
            {
                if (draining)
                {
                    if (TryNext(source, out var value, span))
                    {
                        saved.Add(value);
                        return (true, value);
                    }

                    draining = false;
                }

                if (saved.Count == 0)
                {
                    return (false, None);
                }

                var result = saved[index];
                index++;
                if (index == saved.Count)
                {
                    index = 0;
                }

                return (true, result);
            }
        );
    }

    // ---- islice ------------------------------------------------------------------------

    private static PythonManagedTypeValue CreateIslice() =>
        FunctionType("islice", IsliceDoc, CoreIslice);

    private static PythonManagedObjectValue CoreIslice(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        _ = keywordValues;
        RejectKeywords("islice", keywordNames, span);
        RequireArityRange("islice", positional.Count, 2, 4, span);

        // islice(iterable, stop) or islice(iterable, start, stop[, step]).
        var startArgument = positional.Count == 2 ? null : positional[1];
        var stopArgument = positional.Count == 2 ? positional[1] : positional[2];
        var stepArgument = positional.Count == 4 ? positional[3] : null;

        var start = IsliceBound(startArgument, IsliceStartMessage, 0, span);
        var step = IsliceBound(stepArgument, IsliceStepMessage, 1, span);
        if (step <= 0)
        {
            throw ValueError(IsliceStepMessage, span);
        }

        // A missing or `None` stop is unbounded and only ends when the source does.
        var hasStop = stopArgument is not null and not PythonNoneValue;
        var stop = IsliceBound(stopArgument, IsliceStopMessage, long.MaxValue, span);

        var source = Source(positional[0], span);
        var next = start;
        var consumed = 0L;
        return Iterator(
            "islice",
            () =>
            {
                while (consumed < next)
                {
                    if (!TryNext(source, out _, span))
                    {
                        return (false, None);
                    }

                    consumed++;
                }

                if (hasStop && next >= stop)
                {
                    return (false, None);
                }

                if (!TryNext(source, out var value, span))
                {
                    return (false, None);
                }

                consumed++;
                next = next > long.MaxValue - step ? long.MaxValue : next + step;
                return (true, value);
            }
        );
    }

    /// <summary>
    /// One `islice` bound: `None` (or an argument that was not passed) takes the fallback,
    /// anything else must be a non-negative index that fits a machine word. CPython's
    /// `islice_convert_int` reports every failure as the bound's own ValueError, hiding
    /// the conversion error rather than naming the offending type.
    /// </summary>
    private static long IsliceBound(
        PythonValue? argument,
        string message,
        long fallback,
        TextSpan span
    )
    {
        if (argument is null or PythonNoneValue)
        {
            return fallback;
        }

        if (argument is PythonTruthValue truth)
        {
            return truth.Value ? 1 : 0;
        }

        if (argument is PythonWholeNumberValue whole)
        {
            if (whole.Value < 0 || whole.Value > long.MaxValue)
            {
                throw ValueError(message, span);
            }

            return (long)whole.Value;
        }

        if (UserObjectProtocols.TryConvertToIndex(argument, span, out var index))
        {
            if (index < 0 || index > long.MaxValue)
            {
                throw ValueError(message, span);
            }

            return (long)index;
        }

        throw ValueError(message, span);
    }

    // ---- product -----------------------------------------------------------------------

    private static PythonManagedTypeValue CreateProduct() =>
        FunctionType("product", ProductDoc, CoreProduct);

    private static PythonManagedObjectValue CoreProduct(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        // product(*iterables, repeat=1): positionals are unlimited, keywords are not.
        if (keywordNames.Count > 1)
        {
            throw Error(
                $"product() takes at most 1 keyword argument ({keywordNames.Count} given)",
                span
            );
        }

        var repeat = 1L;
        if (keywordNames.Count == 1)
        {
            if (keywordNames[0] != "repeat")
            {
                throw Error(
                    $"product() got an unexpected keyword argument '{keywordNames[0]}'"
                        + Suggestion(keywordNames[0], ["repeat"]),
                    span
                );
            }

            repeat = RequireIndex(keywordValues[0], span);
            if (repeat < 0)
            {
                throw ValueError("repeat argument cannot be negative", span);
            }
        }

        var pools = new List<PythonValue[]>();
        if (repeat > 0)
        {
            foreach (var iterable in positional)
            {
                pools.Add(Materialize(iterable, span).ToArray());
            }

            var once = pools.Count;
            if (once != 0 && repeat > MaxProductPools / once)
            {
                throw OverflowError("product() repeats too many times", span);
            }

            for (var copy = 1; copy < repeat; copy++)
            {
                for (var index = 0; index < once; index++)
                {
                    pools.Add(pools[index]);
                }
            }
        }

        var indices = new int[pools.Count];
        var started = false;
        return Iterator(
            "product",
            () =>
            {
                if (!started)
                {
                    started = true;
                    foreach (var pool in pools)
                    {
                        if (pool.Length == 0)
                        {
                            return (false, None);
                        }
                    }

                    return (true, ProductTuple(pools, indices));
                }

                var position = pools.Count - 1;
                while (position >= 0)
                {
                    indices[position]++;
                    if (indices[position] < pools[position].Length)
                    {
                        break;
                    }

                    indices[position] = 0;
                    position--;
                }

                if (position < 0)
                {
                    return (false, None);
                }

                return (true, ProductTuple(pools, indices));
            }
        );
    }

    private static PythonTupleValue ProductTuple(List<PythonValue[]> pools, int[] indices)
    {
        var values = new PythonValue[pools.Count];
        for (var index = 0; index < pools.Count; index++)
        {
            values[index] = pools[index][indices[index]];
        }

        return new PythonTupleValue(values);
    }

    // ---- combinations ------------------------------------------------------------------

    private static PythonManagedTypeValue CreateCombinations() =>
        FunctionType("combinations", CombinationsDoc, CoreCombinations);

    private static PythonManagedObjectValue CoreCombinations(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var slots = Unpack(CombinationsArity, positional, keywordNames, keywordValues, span);
        var pool = Materialize(slots[0]!, span);
        var r = RequireR(slots[1]!, span);
        if (r < 0)
        {
            throw ValueError("r must be non-negative", span);
        }

        var n = pool.Count;
        var indices = new int[r];
        for (var index = 0; index < r; index++)
        {
            indices[index] = index;
        }

        var first = true;
        return Iterator(
            "combinations",
            () =>
            {
                if (r > n)
                {
                    return (false, None);
                }

                if (first)
                {
                    first = false;
                    return (true, IndexedTuple(pool, indices, r));
                }

                var position = r - 1;
                while (position >= 0 && indices[position] == position + n - r)
                {
                    position--;
                }

                if (position < 0)
                {
                    return (false, None);
                }

                indices[position]++;
                for (var index = position + 1; index < r; index++)
                {
                    indices[index] = indices[index - 1] + 1;
                }

                return (true, IndexedTuple(pool, indices, r));
            }
        );
    }

    private static PythonTupleValue IndexedTuple(List<PythonValue> pool, int[] indices, int count)
    {
        var values = new PythonValue[count];
        for (var index = 0; index < count; index++)
        {
            values[index] = pool[indices[index]];
        }

        return new PythonTupleValue(values);
    }

    // ---- permutations ------------------------------------------------------------------

    private static PythonManagedTypeValue CreatePermutations() =>
        FunctionType("permutations", PermutationsDoc, CorePermutations);

    private static PythonManagedObjectValue CorePermutations(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var slots = Unpack(PermutationsArity, positional, keywordNames, keywordValues, span);
        var pool = Materialize(slots[0]!, span);
        var n = pool.Count;
        // `r=None` is documented as "the whole iterable", unlike combinations.
        var r = slots[1] is null or PythonNoneValue ? n : RequireR(slots[1]!, span);
        if (r < 0)
        {
            throw ValueError("r must be non-negative", span);
        }

        var indices = new int[n];
        for (var index = 0; index < n; index++)
        {
            indices[index] = index;
        }

        var cycles = new int[r];
        for (var index = 0; index < r; index++)
        {
            cycles[index] = n - index;
        }

        var first = true;
        return Iterator(
            "permutations",
            () =>
            {
                if (r > n)
                {
                    return (false, None);
                }

                if (first)
                {
                    first = false;
                    return (true, IndexedTuple(pool, indices, r));
                }

                if (r == 0)
                {
                    return (false, None);
                }

                for (var position = r - 1; position >= 0; position--)
                {
                    cycles[position]--;
                    if (cycles[position] != 0)
                    {
                        var swap = n - cycles[position];
                        (indices[position], indices[swap]) = (indices[swap], indices[position]);
                        return (true, IndexedTuple(pool, indices, r));
                    }

                    var head = indices[position];
                    for (var index = position; index < n - 1; index++)
                    {
                        indices[index] = indices[index + 1];
                    }

                    indices[n - 1] = head;
                    cycles[position] = n - position;
                }

                return (false, None);
            }
        );
    }

    // ---- combinations_with_replacement --------------------------------------------------

    private static PythonManagedTypeValue CreateCombinationsWithReplacement() =>
        FunctionType(
            "combinations_with_replacement",
            CombinationsWithReplacementDoc,
            CoreCombinationsWithReplacement
        );

    private static PythonManagedObjectValue CoreCombinationsWithReplacement(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var slots = Unpack(
            CombinationsWithReplacementArity,
            positional,
            keywordNames,
            keywordValues,
            span
        );
        var pool = Materialize(slots[0]!, span);
        var r = RequireR(slots[1]!, span);
        if (r < 0)
        {
            throw ValueError("r must be non-negative", span);
        }

        var n = pool.Count;
        var indices = new int[r];
        var first = true;
        return Iterator(
            "combinations_with_replacement",
            () =>
            {
                if (first)
                {
                    first = false;
                    if (n == 0 && r > 0)
                    {
                        return (false, None);
                    }

                    return (true, IndexedTuple(pool, indices, r));
                }

                var position = r - 1;
                while (position >= 0 && indices[position] == n - 1)
                {
                    position--;
                }

                if (position < 0)
                {
                    return (false, None);
                }

                var next = indices[position] + 1;
                for (var index = position; index < r; index++)
                {
                    indices[index] = next;
                }

                return (true, IndexedTuple(pool, indices, r));
            }
        );
    }

    // ---- zip_longest -------------------------------------------------------------------

    private static PythonManagedTypeValue CreateZipLongest() =>
        FunctionType("zip_longest", ZipLongestDoc, CoreZipLongest);

    private static PythonManagedObjectValue CoreZipLongest(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        // zip_longest(*iterables, fillvalue=None): CPython's parser rejects any keyword
        // list that is not exactly one `fillvalue` without naming what it got.
        var fill = (PythonValue)None;
        if (keywordNames.Count != 0)
        {
            if (keywordNames.Count != 1 || keywordNames[0] != "fillvalue")
            {
                throw Error("zip_longest() got an unexpected keyword argument", span);
            }

            fill = keywordValues[0];
        }

        var sources = new PythonIteratorValue[positional.Count];
        for (var index = 0; index < positional.Count; index++)
        {
            sources[index] = Source(positional[index], span);
        }

        return Iterator(
            "zip_longest",
            () =>
            {
                var values = new PythonValue[sources.Length];
                var live = false;
                for (var index = 0; index < sources.Length; index++)
                {
                    if (TryNext(sources[index], out var value, span))
                    {
                        values[index] = value;
                        live = true;
                    }
                    else
                    {
                        values[index] = fill;
                    }
                }

                return live ? (true, new PythonTupleValue(values)) : (false, None);
            }
        );
    }

    // ---- groupby -----------------------------------------------------------------------

    /// <summary>
    /// The state `groupby` shares with its groupers. A group is valid only while
    /// <see cref="GrouperId"/> still matches <see cref="NextGrouperId"/>: creating the next
    /// group is what invalidates the previous grouper, exactly as in CPython.
    /// </summary>
    private sealed class GroupByState
    {
        internal GroupByState(PythonIteratorValue source, PythonValue? keyFunction, TextSpan span)
        {
            Source = source;
            KeyFunction = keyFunction;
            Span = span;
        }

        internal PythonIteratorValue Source { get; }

        /// <summary>Null when the key is the identity, as `key=None` and an omitted argument both are.</summary>
        internal PythonValue? KeyFunction { get; }

        internal TextSpan Span { get; }

        /// <summary>The value read ahead of the group being handed out, if any.</summary>
        internal PythonValue? Pending { get; set; }

        internal PythonValue CurrentKey { get; set; } = None;

        internal bool HasCurrentKey { get; set; }

        internal long GrouperId { get; set; }

        internal long NextGrouperId { get; set; }

        internal PythonValue KeyOf(PythonValue value) =>
            KeyFunction is null ? value : Call(KeyFunction, [value], Span);
    }

    private static PythonManagedTypeValue CreateGroupBy() =>
        FunctionType("groupby", GroupByDoc, CoreGroupBy);

    private static PythonManagedObjectValue CoreGroupBy(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var slots = Unpack(GroupByArity, positional, keywordNames, keywordValues, span);
        var keyFunction = slots[1] is null or PythonNoneValue ? null : slots[1];
        var state = new GroupByState(Source(slots[0]!, span), keyFunction, span);
        return Iterator(
            "groupby",
            () =>
            {
                while (true)
                {
                    PythonValue value;
                    if (state.Pending is { } pending)
                    {
                        state.Pending = null;
                        value = pending;
                    }
                    else if (!TryNext(state.Source, out value, span))
                    {
                        return (false, None);
                    }

                    var key = state.KeyOf(value);
                    if (state.HasCurrentKey && ManagedObjectProtocols.AreEqual(key, state.CurrentKey))
                    {
                        // A leftover value of the group that was just handed out: it is
                        // dropped, which is what makes advancing groupby skip a group.
                        continue;
                    }

                    state.CurrentKey = key;
                    state.HasCurrentKey = true;
                    state.Pending = value;
                    state.GrouperId = ++state.NextGrouperId;
                    return (true, new PythonTupleValue([key, Grouper(state)]));
                }
            }
        );
    }

    private static PythonManagedObjectValue Grouper(GroupByState state)
    {
        var id = state.GrouperId;
        var finished = false;
        return Iterator(
            "_grouper",
            () =>
            {
                // A group that groupby has already moved past, and a grouper whose read
                // ahead now belongs to the next group, are both simply exhausted.
                if (id != state.NextGrouperId || finished)
                {
                    return (false, None);
                }

                if (state.Pending is { } first)
                {
                    state.Pending = null;
                    return (true, first);
                }

                while (TryNext(state.Source, out var value, state.Span))
                {
                    if (
                        ManagedObjectProtocols.AreEqual(
                            state.KeyOf(value),
                            state.CurrentKey
                        )
                    )
                    {
                        return (true, value);
                    }

                    // The value starts the next group: hand it back to groupby.
                    state.Pending = value;
                    finished = true;
                    return (false, None);
                }

                return (false, None);
            }
        );
    }

    // ---- takewhile, dropwhile, filterfalse, compress -------------------------------------

    private static PythonManagedTypeValue CreateTakeWhile() =>
        FunctionType("takewhile", TakeWhileDoc, CoreTakeWhile);

    private static PythonManagedObjectValue CoreTakeWhile(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        _ = keywordValues;
        RejectKeywords("takewhile", keywordNames, span);
        RequireArity("takewhile", positional.Count, 2, span);
        var predicate = positional[0];
        var source = Source(positional[1], span);
        var live = true;
        return Iterator(
            "takewhile",
            () =>
            {
                if (!live || !TryNext(source, out var value, span))
                {
                    return (false, None);
                }

                if (!ManagedObjectProtocols.IsTrue(Call(predicate, [value], span)))
                {
                    live = false;
                    return (false, None);
                }

                return (true, value);
            }
        );
    }

    private static PythonManagedTypeValue CreateDropWhile() =>
        FunctionType("dropwhile", DropWhileDoc, CoreDropWhile);

    private static PythonManagedObjectValue CoreDropWhile(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        _ = keywordValues;
        RejectKeywords("dropwhile", keywordNames, span);
        RequireArity("dropwhile", positional.Count, 2, span);
        var predicate = positional[0];
        var source = Source(positional[1], span);
        var dropping = true;
        return Iterator(
            "dropwhile",
            () =>
            {
                if (!dropping)
                {
                    return TryNext(source, out var value, span)
                        ? (true, value)
                        : (false, None);
                }

                while (TryNext(source, out var candidate, span))
                {
                    if (!ManagedObjectProtocols.IsTrue(Call(predicate, [candidate], span)))
                    {
                        dropping = false;
                        return (true, candidate);
                    }
                }

                return (false, None);
            }
        );
    }

    private static PythonManagedTypeValue CreateFilterFalse() =>
        FunctionType("filterfalse", FilterFalseDoc, CoreFilterFalse);

    private static PythonManagedObjectValue CoreFilterFalse(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        _ = keywordValues;
        RejectKeywords("filterfalse", keywordNames, span);
        RequireArity("filterfalse", positional.Count, 2, span);
        var predicate = positional[0];
        var source = Source(positional[1], span);
        var identity = predicate is PythonNoneValue;
        return Iterator(
            "filterfalse",
            () =>
            {
                while (TryNext(source, out var value, span))
                {
                    var keep = identity
                        ? !ManagedObjectProtocols.IsTrue(value)
                        : !ManagedObjectProtocols.IsTrue(Call(predicate, [value], span));
                    if (keep)
                    {
                        return (true, value);
                    }
                }

                return (false, None);
            }
        );
    }

    private static PythonManagedTypeValue CreateCompress() =>
        FunctionType("compress", CompressDoc, CoreCompress);

    private static PythonManagedObjectValue CoreCompress(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var slots = Unpack(CompressArity, positional, keywordNames, keywordValues, span);
        var source = Source(slots[0]!, span);
        var selectors = Source(slots[1]!, span);
        return Iterator(
            "compress",
            () =>
            {
                while (TryNext(source, out var value, span))
                {
                    if (!TryNext(selectors, out var selector, span))
                    {
                        return (false, None);
                    }

                    if (ManagedObjectProtocols.IsTrue(selector))
                    {
                        return (true, value);
                    }
                }

                return (false, None);
            }
        );
    }

    // ---- accumulate --------------------------------------------------------------------

    private static PythonManagedTypeValue CreateAccumulate() =>
        FunctionType("accumulate", AccumulateDoc, CoreAccumulate);

    private static PythonManagedObjectValue CoreAccumulate(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var slots = Unpack(AccumulateArity, positional, keywordNames, keywordValues, span);
        var source = Source(slots[0]!, span);
        var function = slots[1] is null or PythonNoneValue ? null : slots[1];
        // An explicit `initial=None` is the same as no initial at all.
        var initial = slots[2] is PythonNoneValue ? null : slots[2];
        var first = true;
        var started = false;
        PythonValue total = None;
        return Iterator(
            "accumulate",
            () =>
            {
                if (first)
                {
                    first = false;
                    if (initial is not null)
                    {
                        total = initial;
                        started = true;
                        return (true, initial);
                    }
                }

                if (!TryNext(source, out var value, span))
                {
                    return (false, None);
                }

                if (!started)
                {
                    started = true;
                    total = value;
                }
                else
                {
                    total =
                        function is null
                            ? PythonVirtualMachine.ApplyBinaryOperator(
                                PythonOpCode.BinaryAdd,
                                total,
                                value,
                                span
                            )
                            : Call(function, [total, value], span);
                }

                return (true, total);
            }
        );
    }

    // ---- starmap -----------------------------------------------------------------------

    private static PythonManagedTypeValue CreateStarMap() =>
        FunctionType("starmap", StarMapDoc, CoreStarMap);

    private static PythonManagedObjectValue CoreStarMap(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        _ = keywordValues;
        RejectKeywords("starmap", keywordNames, span);
        RequireArity("starmap", positional.Count, 2, span);
        var function = positional[0];
        var source = Source(positional[1], span);
        return Iterator(
            "starmap",
            () =>
            {
                while (TryNext(source, out var item, span))
                {
                    var arguments = Materialize(item, span);
                    return (true, Call(function, arguments.ToArray(), span));
                }

                return (false, None);
            }
        );
    }

    // ---- pairwise ----------------------------------------------------------------------

    private static PythonManagedTypeValue CreatePairwise() =>
        FunctionType("pairwise", PairwiseDoc, CorePairwise);

    private static PythonManagedObjectValue CorePairwise(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        _ = keywordValues;
        RejectKeywords("pairwise", keywordNames, span);
        RequireArity("pairwise", positional.Count, 1, span);
        var source = Source(positional[0], span);
        PythonValue? previous = null;
        return Iterator(
            "pairwise",
            () =>
            {
                if (previous is null)
                {
                    if (!TryNext(source, out var first, span))
                    {
                        return (false, None);
                    }

                    previous = first;
                }

                if (!TryNext(source, out var value, span))
                {
                    return (false, None);
                }

                var pair = new PythonTupleValue([previous, value]);
                previous = value;
                return (true, pair);
            }
        );
    }

    // ---- tee ---------------------------------------------------------------------------

    /// <summary>The value log `tee` copies share: one source, one log, one cursor each.</summary>
    private sealed class TeeState
    {
        internal TeeState(PythonIteratorValue source, int count, TextSpan span)
        {
            Source = source;
            Cursors = new long[count];
            Span = span;
        }

        internal PythonIteratorValue Source { get; }

        /// <summary>Absolute position of each tee in the log.</summary>
        internal long[] Cursors { get; }

        internal TextSpan Span { get; }

        /// <summary>The values read so far that at least one tee has not consumed.</summary>
        internal List<PythonValue> Log { get; } = [];

        /// <summary>Absolute position of <c>Log[0]</c>.</summary>
        internal long Base { get; set; }

        internal bool Done { get; set; }
    }

    private static PythonBuiltinFunctionValue CreateTee() =>
        new PythonBuiltinFunctionValue(
            "tee",
            static (arguments, span) => CoreTee(arguments, span),
            static (positional, keywordNames, _, span) =>
                keywordNames.Count == 0
                    ? CoreTee(positional, span)
                    : throw Error("itertools.tee() takes no keyword arguments", span)
        );

    private static PythonTupleValue CoreTee(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        RequireArityRange("tee", arguments.Count, 1, 2, span);
        var n = arguments.Count == 2 ? RequireIndex(arguments[1], span) : 2L;
        if (n < 0)
        {
            throw ValueError("n must be >= 0", span);
        }

        if (n > MaxTeeIterators)
        {
            // CPython asks for the memory; this runtime refuses first.
            throw OverflowError("too many tee iterators", span);
        }

        var source = Source(arguments[0], span);
        var result = new PythonValue[n];
        if (n == 0)
        {
            return new PythonTupleValue(result);
        }

        var state = new TeeState(source, (int)n, span);
        for (var index = 0; index < n; index++)
        {
            result[index] = Tee(state, index);
        }

        return new PythonTupleValue(result);
    }

    private static PythonManagedObjectValue Tee(TeeState state, int index) =>
        Iterator(
            "_tee",
            () =>
            {
                var cursor = state.Cursors[index];
                if (cursor < state.Base + state.Log.Count)
                {
                    state.Cursors[index] = cursor + 1;
                    return (true, state.Log[(int)(cursor - state.Base)]);
                }

                if (state.Done || !TryNext(state.Source, out var value, state.Span))
                {
                    state.Done = true;
                    return (false, None);
                }

                state.Log.Add(value);
                state.Cursors[index] = cursor + 1;
                Trim(state);
                return (true, value);
            }
        );

    /// <summary>Drops the log prefix every tee has already consumed, so it stays bounded.</summary>
    private static void Trim(TeeState state)
    {
        var lowest = long.MaxValue;
        foreach (var cursor in state.Cursors)
        {
            lowest = Math.Min(lowest, cursor);
        }

        var drop = (int)Math.Min(lowest - state.Base, state.Log.Count);
        if (drop > 64 || drop == state.Log.Count)
        {
            state.Log.RemoveRange(0, drop);
            state.Base += drop;
        }
    }

    // ---- batched -----------------------------------------------------------------------

    private static PythonManagedTypeValue CreateBatched() =>
        FunctionType("batched", BatchedDoc, CoreBatched);

    private static PythonManagedObjectValue CoreBatched(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var slots = Unpack(BatchedArity, positional, keywordNames, keywordValues, span);
        var n = RequireIndex(slots[1]!, span);
        if (n < 1)
        {
            throw ValueError("n must be at least one", span);
        }

        var strict = slots[2] is not null && ManagedObjectProtocols.IsTrue(slots[2]!);
        var source = Source(slots[0]!, span);
        return Iterator(
            "batched",
            () =>
            {
                var batch = new List<PythonValue>();
                while (batch.Count < n)
                {
                    if (!TryNext(source, out var value, span))
                    {
                        if (batch.Count == 0)
                        {
                            return (false, None);
                        }

                        if (strict)
                        {
                            throw ValueError("batched(): incomplete batch", span);
                        }

                        break;
                    }

                    batch.Add(value);
                }

                return (true, new PythonTupleValue(batch.ToArray()));
            }
        );
    }

    // ---- chain -------------------------------------------------------------------------

    private static readonly PythonManagedTypeValue ChainType = BuildChain();

    private static PythonManagedTypeValue BuildChain()
    {
        var type = IteratorType("chain", ChainDoc);
        type.Attributes["__new__"] = new PythonProtocolFunctionValue(
            "__new__",
            static (_, arguments) => Chained(Arguments(arguments, 1), default),
            static (_, arguments, keywordNames, _) =>
            {
                if (keywordNames.Count != 0)
                {
                    throw Error("chain() takes no keyword arguments", default);
                }

                return Chained(Arguments(arguments, 1), default);
            }
        );
        type.Attributes["from_iterable"] = new PythonBuiltinFunctionValue(
            "chain.from_iterable",
            static (arguments, span) => FromIterableArguments(arguments, span),
            static (positional, keywordNames, _, span) =>
                keywordNames.Count == 0
                    ? FromIterableArguments(positional, span)
                    : throw Error("chain.from_iterable() takes no keyword arguments", span)
        );
        return type;
    }

    /// <summary>
    /// `chain` converts each of its iterables only when it reaches it, which is why
    /// `chain([1], 2)` builds and `list()` on it is what raises.
    /// </summary>
    private static PythonManagedObjectValue Chained(PythonValue[] iterables, TextSpan span)
    {
        var position = 0;
        PythonIteratorValue? current = null;
        return Iterator(
            "chain",
            () =>
            {
                while (true)
                {
                    if (current is not null && TryNext(current, out var value, span))
                    {
                        return (true, value);
                    }

                    if (position >= iterables.Length)
                    {
                        return (false, None);
                    }

                    current = Source(iterables[position], span);
                    position++;
                }
            }
        );
    }

    private static PythonManagedObjectValue FromIterableArguments(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        // `from_iterable` is a METH_O method, so its arity error is the "exactly one"
        // spelling rather than `_PyArg_UnpackTuple`'s "expected".
        if (arguments.Count != 1)
        {
            throw Error(
                $"chain.from_iterable() takes exactly one argument ({arguments.Count} given)",
                span
            );
        }

        return FromIterable(arguments[0], span);
    }

    /// <summary>The outer iterable is converted now, the inner ones as they are reached.</summary>
    private static PythonManagedObjectValue FromIterable(PythonValue iterable, TextSpan span)
    {
        var outer = Source(iterable, span);
        PythonIteratorValue? inner = null;
        return Iterator(
            "chain",
            () =>
            {
                while (true)
                {
                    if (inner is not null && TryNext(inner, out var value, span))
                    {
                        return (true, value);
                    }

                    if (!TryNext(outer, out var next, span))
                    {
                        return (false, None);
                    }

                    inner = Source(next, span);
                }
            }
        );
    }

    // ---- argument shapes ---------------------------------------------------------------

    private static readonly Arity CountArity = new(
        "count",
        ["start", "step"],
        0,
        2,
        "count() takes at most 2 arguments"
    );

    private static readonly Arity RepeatArity = new(
        "repeat",
        ["object", "times"],
        1,
        2,
        "repeat() takes at most 2 arguments"
    );

    private static readonly Arity CombinationsArity = new(
        "combinations",
        ["iterable", "r"],
        2,
        2,
        "combinations() takes at most 2 arguments"
    );

    private static readonly Arity PermutationsArity = new(
        "permutations",
        ["iterable", "r"],
        1,
        2,
        "permutations() takes at most 2 arguments"
    );

    private static readonly Arity CombinationsWithReplacementArity = new(
        "combinations_with_replacement",
        ["iterable", "r"],
        2,
        2,
        "combinations_with_replacement() takes at most 2 arguments"
    );

    private static readonly Arity GroupByArity = new(
        "groupby",
        ["iterable", "key"],
        1,
        2,
        "groupby() takes at most 2 arguments"
    );

    private static readonly Arity CompressArity = new(
        "compress",
        ["data", "selectors"],
        2,
        2,
        "compress() takes at most 2 arguments"
    );

    private static readonly Arity AccumulateArity = new(
        "accumulate",
        ["iterable", "func", "initial"],
        1,
        3,
        "accumulate() takes at most 3 arguments",
        2,
        "accumulate() takes at most 2 positional arguments"
    );

    private static readonly Arity BatchedArity = new(
        "batched",
        ["iterable", "n", "strict"],
        2,
        3,
        "batched() takes at most 3 arguments",
        2,
        "batched() takes exactly 2 positional arguments"
    );

    // ---- helpers -----------------------------------------------------------------------

    private static bool TryNext(
        PythonIteratorValue iterator,
        out PythonValue value,
        TextSpan span = default
    ) => ManagedObjectProtocols.TryGetNext(iterator, out value, span);

    /// <summary>Calls a callable through the interpreter's own call path.</summary>
    private static PythonValue Call(
        PythonValue callable,
        PythonValue[] arguments,
        TextSpan span
    ) => UserObjectProtocols.Dispatcher!.Invoke(callable, arguments, span);

    private static PythonRuntimeException OverflowError(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, "OverflowError");

    // ---- docstrings --------------------------------------------------------------------

    private const string ChainDoc =
        "Return a chain object whose .__next__() method returns elements from the\n"
        + "first iterable until it is exhausted, then elements from the next\n"
        + "iterable, until all of the iterables are exhausted.";

    private const string CountDoc =
        "Return a count object whose .__next__() method returns consecutive values.\n"
        + "\n"
        + "Equivalent to:\n"
        + "    def count(firstval=0, step=1):\n"
        + "        x = firstval\n"
        + "        while 1:\n"
        + "            yield x\n"
        + "            x += step";

    private const string CycleDoc =
        "Return elements from the iterable until it is exhausted. Then repeat the sequence indefinitely.";

    private const string RepeatDoc =
        "repeat(object [,times]) -> create an iterator which returns the object\n"
        + "for the specified number of times.  If not specified, returns the object\n"
        + "endlessly.";

    private const string IsliceDoc =
        "islice(iterable, stop) --> islice object\n"
        + "islice(iterable, start, stop[, step]) --> islice object\n"
        + "\n"
        + "Return an iterator whose next() method returns selected values from an\n"
        + "iterable.  If start is specified, will skip all preceding elements;\n"
        + "otherwise, start defaults to zero.  Step defaults to one.  If\n"
        + "specified as another value, step determines how many values are\n"
        + "skipped between successive calls.  Works like a slice() on a list\n"
        + "but returns an iterator.";

    private const string ProductDoc =
        "Cartesian product of input iterables.  Equivalent to nested for-loops.\n"
        + "\n"
        + "For example, product(A, B) returns the same as:  ((x,y) for x in A for y in B).\n"
        + "The leftmost iterators are in the outermost for-loop, so the output tuples\n"
        + "cycle in a manner similar to an odometer (with the rightmost element changing\n"
        + "on every iteration).\n"
        + "\n"
        + "To compute the product of an iterable with itself, specify the number\n"
        + "of repetitions with the optional repeat keyword argument. For example,\n"
        + "product(A, repeat=4) means the same as product(A, A, A, A).\n"
        + "\n"
        + "product('ab', range(3)) --> ('a',0) ('a',1) ('a',2) ('b',0) ('b',1) ('b',2)\n"
        + "product((0,1), (0,1), (0,1)) --> (0,0,0) (0,0,1) (0,1,0) (0,1,1) (1,0,0) ...";

    private const string CombinationsDoc =
        "Return successive r-length combinations of elements in the iterable.\n"
        + "\n"
        + "combinations(range(4), 3) --> (0,1,2), (0,1,3), (0,2,3), (1,2,3)";

    private const string PermutationsDoc =
        "Return successive r-length permutations of elements in the iterable.\n"
        + "\n"
        + "permutations(range(3), 2) --> (0,1), (0,2), (1,0), (1,2), (2,0), (2,1)";

    private const string CombinationsWithReplacementDoc =
        "Return successive r-length combinations of elements in the iterable allowing individual elements to have successive repeats.\n"
        + "\n"
        + "combinations_with_replacement('ABC', 2) --> ('A','A'), ('A','B'), ('A','C'), ('B','B'), ('B','C'), ('C','C')";

    private const string ZipLongestDoc =
        "Return a zip_longest object whose .__next__() method returns a tuple where\n"
        + "the i-th element comes from the i-th iterable argument.  The .__next__()\n"
        + "method continues until the longest iterable in the argument sequence\n"
        + "is exhausted and then it raises StopIteration.  When the shorter iterables\n"
        + "are exhausted, the fillvalue is substituted in their place.  The fillvalue\n"
        + "defaults to None or can be specified by a keyword argument.\n";

    private const string GroupByDoc =
        "make an iterator that returns consecutive keys and groups from the iterable\n"
        + "\n"
        + "  iterable\n"
        + "    Elements to divide into groups according to the key function.\n"
        + "  key\n"
        + "    A function for computing the group category for each element.\n"
        + "    If the key function is not specified or is None, the element itself\n"
        + "    is used for grouping.";

    private const string TakeWhileDoc =
        "Return successive entries from an iterable as long as the predicate evaluates to true for each entry.";

    private const string DropWhileDoc =
        "Drop items from the iterable while predicate(item) is true.\n"
        + "\n"
        + "Afterwards, return every element until the iterable is exhausted.";

    private const string FilterFalseDoc =
        "Return those items of iterable for which function(item) is false.\n"
        + "\n"
        + "If function is None, return the items that are false.";

    private const string CompressDoc =
        "Return data elements corresponding to true selector elements.\n"
        + "\n"
        + "Forms a shorter iterator from selected data elements using the selectors\n"
        + "to choose the data elements.";

    private const string AccumulateDoc =
        "Return series of accumulated sums (or other binary function results).";

    private const string StarMapDoc =
        "Return an iterator whose values are returned from the function evaluated with an argument tuple taken from the given sequence.";

    private const string PairwiseDoc =
        "Return an iterator of overlapping pairs taken from the input iterator.\n"
        + "\n"
        + "    s -> (s0,s1), (s1,s2), (s2, s3), ...";

    private const string BatchedDoc =
        "Batch data into tuples of length n. The last batch may be shorter than n.\n"
        + "\n"
        + "Loops over the input iterable and accumulates data into tuples\n"
        + "up to size n.  The input is consumed lazily, just enough to\n"
        + "fill a batch.  The result is yielded as soon as a batch is full\n"
        + "or when the input iterable is exhausted.\n"
        + "\n"
        + "    >>> for batch in batched('ABCDEFG', 3):\n"
        + "    ...     print(batch)\n"
        + "    ...\n"
        + "    ('A', 'B', 'C')\n"
        + "    ('D', 'E', 'F')\n"
        + "    ('G',)\n"
        + "\n"
        + "If \"strict\" is True, raises a ValueError if the final batch is shorter\n"
        + "than n.";
}
