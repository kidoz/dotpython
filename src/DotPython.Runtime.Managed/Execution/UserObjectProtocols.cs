using System.Numerics;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// Runs user-defined special methods on the owning VM for the static protocol layer,
/// which cannot execute interpreter frames itself.
/// </summary>
internal interface IUserObjectDispatcher
{
    PythonValue Invoke(PythonValue callable, PythonValue[] arguments, TextSpan span);

    /// <summary>The executing VM's standard output (`sys.stdout`).</summary>
    TextWriter StandardOutput { get; }

    /// <summary>The executing VM's error stream (`sys.stderr`).</summary>
    TextWriter StandardError { get; }

    /// <summary>The executing VM's standard input (`sys.stdin`), null when absent.</summary>
    TextReader? StandardInput { get; }

    /// <summary>The program arguments (`sys.argv`).</summary>
    IReadOnlyList<string> Arguments { get; }

    /// <summary>The module search roots (`sys.path`).</summary>
    IReadOnlyList<string> SearchRoots { get; }
}

/// <summary>
/// Special-method (`__eq__`, `__add__`, `__len__`, …) dispatch for managed class
/// instances. Lookups follow CPython's implicit special-method rule: the type's MRO is
/// consulted, never the instance dictionary. Every entry point returns false (or null)
/// when the operand is not a managed instance, when its type defines no such method,
/// or when no VM has installed a dispatcher on the current thread, so callers fall
/// through to the builtin behaviour.
/// </summary>
internal static class UserObjectProtocols
{
    [ThreadStatic]
    private static IUserObjectDispatcher? _dispatcher;

    /// <summary>The dispatcher of the VM currently executing on this thread, if any.</summary>
    internal static IUserObjectDispatcher? Dispatcher
    {
        get => _dispatcher;
        set => _dispatcher = value;
    }

    private static readonly Dictionary<
        PythonOpCode,
        (string Symbol, string Forward, string Reflected, string InPlace)
    > BinaryOperators = new()
    {
        [PythonOpCode.BinaryAdd] = ("+", "__add__", "__radd__", "__iadd__"),
        [PythonOpCode.BinarySubtract] = ("-", "__sub__", "__rsub__", "__isub__"),
        [PythonOpCode.BinaryMultiply] = ("*", "__mul__", "__rmul__", "__imul__"),
        [PythonOpCode.BinaryTrueDivide] = ("/", "__truediv__", "__rtruediv__", "__itruediv__"),
        [PythonOpCode.BinaryFloorDivide] = ("//", "__floordiv__", "__rfloordiv__", "__ifloordiv__"),
        [PythonOpCode.BinaryModulo] = ("%", "__mod__", "__rmod__", "__imod__"),
        [PythonOpCode.BinaryPower] = ("** or pow()", "__pow__", "__rpow__", "__ipow__"),
        [PythonOpCode.BinaryMatrixMultiply] = ("@", "__matmul__", "__rmatmul__", "__imatmul__"),
        [PythonOpCode.BinaryAnd] = ("&", "__and__", "__rand__", "__iand__"),
        [PythonOpCode.BinaryOr] = ("|", "__or__", "__ror__", "__ior__"),
        [PythonOpCode.BinaryXor] = ("^", "__xor__", "__rxor__", "__ixor__"),
        [PythonOpCode.BinaryLeftShift] = ("<<", "__lshift__", "__rlshift__", "__ilshift__"),
        [PythonOpCode.BinaryRightShift] = (">>", "__rshift__", "__rrshift__", "__irshift__"),
    };

    private static readonly Dictionary<
        PythonRichComparison,
        (string Symbol, string Forward, PythonRichComparison Reflected)
    > Comparisons = new()
    {
        [PythonRichComparison.Equal] = ("==", "__eq__", PythonRichComparison.Equal),
        [PythonRichComparison.NotEqual] = ("!=", "__ne__", PythonRichComparison.NotEqual),
        [PythonRichComparison.LessThan] = ("<", "__lt__", PythonRichComparison.GreaterThan),
        [PythonRichComparison.LessThanOrEqual] = (
            "<=",
            "__le__",
            PythonRichComparison.GreaterThanOrEqual
        ),
        [PythonRichComparison.GreaterThan] = (">", "__gt__", PythonRichComparison.LessThan),
        [PythonRichComparison.GreaterThanOrEqual] = (
            ">=",
            "__ge__",
            PythonRichComparison.LessThanOrEqual
        ),
    };

    internal static string GetBinaryOperatorSymbol(PythonOpCode opCode) =>
        BinaryOperators.TryGetValue(opCode, out var entry) ? entry.Symbol : "?";

    /// <summary>
    /// Looks up a special method on the instance's type (bound to the instance). Instance
    /// attributes are deliberately skipped, matching CPython's implicit lookup.
    /// </summary>
    internal static bool TryGetSpecialMethod(
        PythonValue value,
        string name,
        out PythonValue method,
        out PythonManagedObjectValue instance
    )
    {
        if (
            value is PythonManagedObjectValue managed
            && _dispatcher is not null
            && ManagedObjectProtocols.TryGetTypeAttribute(managed.Type, name, out var attribute)
        )
        {
            method = attribute switch
            {
                PythonFunctionValue function => new PythonBoundUserMethodValue(
                    name,
                    managed,
                    function
                ),
                PythonProtocolFunctionValue protocolFunction => new PythonBoundMethodValue(
                    name,
                    managed,
                    protocolFunction
                ),
                _ => attribute,
            };
            instance = managed;
            return true;
        }

        method = null!;
        instance = null!;
        return false;
    }

    internal static bool DefinesSpecialMethod(PythonValue value, string name) =>
        value is PythonManagedObjectValue managed
        && ManagedObjectProtocols.TryGetTypeAttribute(managed.Type, name, out _);

    private static bool TryInvoke(
        PythonValue value,
        string name,
        PythonValue[] arguments,
        TextSpan span,
        out PythonValue result
    )
    {
        if (!TryGetSpecialMethod(value, name, out var method, out _))
        {
            result = null!;
            return false;
        }

        if (method is PythonNoneValue)
        {
            // `__hash__ = None` and friends disable the protocol instead of defining it.
            result = null!;
            return false;
        }

        result = _dispatcher!.Invoke(method, arguments, span);
        return true;
    }

    // ----------------------------------------------------------------------------
    // Binary and unary operators
    // ----------------------------------------------------------------------------

    /// <summary>
    /// Applies a binary operator through `__op__` / `__rop__`, honouring the
    /// NotImplemented protocol and the subclass-first reflected rule.
    /// </summary>
    internal static bool TryApplyBinary(
        PythonOpCode opCode,
        PythonValue left,
        PythonValue right,
        TextSpan span,
        out PythonValue result
    )
    {
        result = null!;
        if (
            _dispatcher is null
            || left is not PythonManagedObjectValue && right is not PythonManagedObjectValue
            || !BinaryOperators.TryGetValue(opCode, out var names)
        )
        {
            return false;
        }

        var reflectedFirst =
            right is PythonManagedObjectValue rightInstance
            && left is PythonManagedObjectValue leftInstance
            && !ReferenceEquals(rightInstance.Type, leftInstance.Type)
            && rightInstance.Type.Mro.Contains(leftInstance.Type)
            && DefinesSpecialMethod(right, names.Reflected);

        if (reflectedFirst && TryInvoke(right, names.Reflected, [left], span, out result))
        {
            if (result is not PythonNotImplementedValue)
            {
                return true;
            }
        }

        if (TryInvoke(left, names.Forward, [right], span, out result))
        {
            if (result is not PythonNotImplementedValue)
            {
                return true;
            }
        }

        if (!reflectedFirst && TryInvoke(right, names.Reflected, [left], span, out result))
        {
            if (result is not PythonNotImplementedValue)
            {
                return true;
            }
        }

        throw ManagedObjectProtocols.Fault(
            "DPY4005",
            $"unsupported operand type(s) for {names.Symbol}: "
                + $"'{ManagedObjectProtocols.GetTypeName(left)}' and "
                + $"'{ManagedObjectProtocols.GetTypeName(right)}'",
            span,
            "TypeError"
        );
    }

    /// <summary>`x op= y` through `__iop__`; falls back to the binary protocol.</summary>
    internal static bool TryApplyInPlace(
        PythonOpCode opCode,
        PythonValue left,
        PythonValue right,
        TextSpan span,
        out PythonValue result
    )
    {
        result = null!;
        if (
            _dispatcher is null
            || left is not PythonManagedObjectValue
            || !BinaryOperators.TryGetValue(opCode, out var names)
        )
        {
            return false;
        }

        if (
            TryInvoke(left, names.InPlace, [right], span, out result)
            && result is not PythonNotImplementedValue
        )
        {
            return true;
        }

        return TryApplyBinary(opCode, left, right, span, out result);
    }

    internal static bool TryApplyUnary(
        PythonOpCode opCode,
        PythonValue operand,
        TextSpan span,
        out PythonValue result
    )
    {
        result = null!;
        if (_dispatcher is null || operand is not PythonManagedObjectValue)
        {
            return false;
        }

        var (symbol, name) = opCode switch
        {
            PythonOpCode.UnaryNegative => ("-", "__neg__"),
            PythonOpCode.UnaryPositive => ("+", "__pos__"),
            PythonOpCode.UnaryInvert => ("~", "__invert__"),
            _ => throw new ArgumentOutOfRangeException(nameof(opCode)),
        };
        if (TryInvoke(operand, name, [], span, out result))
        {
            return true;
        }

        throw ManagedObjectProtocols.Fault(
            "DPY4005",
            $"bad operand type for unary {symbol}: '{ManagedObjectProtocols.GetTypeName(operand)}'",
            span,
            "TypeError"
        );
    }

    internal static bool TryAbsolute(PythonValue operand, TextSpan span, out PythonValue result)
    {
        result = null!;
        if (_dispatcher is null || operand is not PythonManagedObjectValue)
        {
            return false;
        }

        if (TryInvoke(operand, "__abs__", [], span, out result))
        {
            return true;
        }

        throw ManagedObjectProtocols.Fault(
            "DPY4003",
            $"bad operand type for abs(): '{ManagedObjectProtocols.GetTypeName(operand)}'",
            span,
            "TypeError"
        );
    }

    // ----------------------------------------------------------------------------
    // Rich comparison
    // ----------------------------------------------------------------------------

    /// <summary>
    /// Rich comparison through `__eq__`/`__lt__`/…: forward, then reflected on the other
    /// operand; `==`/`!=` fall back to identity, ordering raises TypeError.
    /// </summary>
    internal static bool TryRichCompare(
        PythonValue left,
        PythonValue right,
        PythonRichComparison comparison,
        TextSpan span,
        out PythonValue result
    )
    {
        result = null!;
        if (
            _dispatcher is null
            || left is not PythonManagedObjectValue && right is not PythonManagedObjectValue
        )
        {
            return false;
        }

        var names = Comparisons[comparison];
        var reflectedNames = Comparisons[names.Reflected];
        var reflectedFirst =
            right is PythonManagedObjectValue rightInstance
            && left is PythonManagedObjectValue leftInstance
            && !ReferenceEquals(rightInstance.Type, leftInstance.Type)
            && rightInstance.Type.Mro.Contains(leftInstance.Type)
            && DefinesSpecialMethod(right, reflectedNames.Forward);

        if (
            reflectedFirst
            && TryInvoke(right, reflectedNames.Forward, [left], span, out result)
            && result is not PythonNotImplementedValue
        )
        {
            return true;
        }

        if (
            TryInvoke(left, names.Forward, [right], span, out result)
            && result is not PythonNotImplementedValue
        )
        {
            return true;
        }

        if (
            !reflectedFirst
            && TryInvoke(right, reflectedNames.Forward, [left], span, out result)
            && result is not PythonNotImplementedValue
        )
        {
            return true;
        }

        switch (comparison)
        {
            case PythonRichComparison.Equal:
                result = PythonTruthValue.FromBoolean(ReferenceEquals(left, right));
                return true;
            case PythonRichComparison.NotEqual:
                // Default `__ne__` inverts `__eq__` unless that is also NotImplemented.
                if (
                    TryInvoke(left, "__eq__", [right], span, out var equal)
                        && equal is not PythonNotImplementedValue
                    || TryInvoke(right, "__eq__", [left], span, out equal)
                        && equal is not PythonNotImplementedValue
                )
                {
                    result = PythonTruthValue.FromBoolean(!ManagedObjectProtocols.IsTrue(equal));
                    return true;
                }

                result = PythonTruthValue.FromBoolean(!ReferenceEquals(left, right));
                return true;
            default:
                throw ManagedObjectProtocols.Fault(
                    "DPY4005",
                    $"'{names.Symbol}' not supported between instances of "
                        + $"'{ManagedObjectProtocols.GetTypeName(left)}' and "
                        + $"'{ManagedObjectProtocols.GetTypeName(right)}'",
                    span,
                    "TypeError"
                );
        }
    }

    internal static bool TryAreEqual(PythonValue left, PythonValue right, out bool equal)
    {
        if (TryRichCompare(left, right, PythonRichComparison.Equal, default, out var result))
        {
            equal = ManagedObjectProtocols.IsTrue(result);
            return true;
        }

        equal = false;
        return false;
    }

    /// <summary>Three-way ordering for sort/min/max, derived from `__lt__` both ways.</summary>
    internal static bool TryCompareOrdered(
        PythonValue left,
        PythonValue right,
        TextSpan span,
        out int ordering
    )
    {
        ordering = 0;
        if (
            _dispatcher is null
            || left is not PythonManagedObjectValue && right is not PythonManagedObjectValue
        )
        {
            return false;
        }

        if (
            TryRichCompare(left, right, PythonRichComparison.LessThan, span, out var less)
            && ManagedObjectProtocols.IsTrue(less)
        )
        {
            ordering = -1;
            return true;
        }

        if (
            TryRichCompare(right, left, PythonRichComparison.LessThan, span, out var greater)
            && ManagedObjectProtocols.IsTrue(greater)
        )
        {
            ordering = 1;
            return true;
        }

        return true;
    }

    // ----------------------------------------------------------------------------
    // Truthiness, length, items, containment
    // ----------------------------------------------------------------------------

    internal static bool TryIsTrue(PythonValue value, out bool truth)
    {
        truth = true;
        if (_dispatcher is null || value is not PythonManagedObjectValue)
        {
            return false;
        }

        if (TryInvoke(value, "__bool__", [], default, out var result))
        {
            if (result is not PythonTruthValue resultTruth)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{ManagedObjectProtocols.GetTypeName(value)}.__bool__() must return a bool, not {ManagedObjectProtocols.GetTypeName(result)}",
                    default,
                    "TypeError"
                );
            }

            truth = resultTruth.Value;
            return true;
        }

        if (TryGetLength(value, default, out var length))
        {
            truth = length != 0;
            return true;
        }

        return false;
    }

    internal static bool TryGetLength(PythonValue value, TextSpan span, out int length)
    {
        length = 0;
        if (
            _dispatcher is null
            || value is not PythonManagedObjectValue
            || !TryInvoke(value, "__len__", [], span, out var result)
        )
        {
            return false;
        }

        var promoted = result is PythonTruthValue truth
            ? PythonWholeNumberValue.Create(truth.Value ? BigInteger.One : BigInteger.Zero)
            : result;
        if (promoted is not PythonWholeNumberValue whole)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"'{ManagedObjectProtocols.GetTypeName(result)}' object cannot be interpreted as an integer",
                span,
                "TypeError"
            );
        }

        if (whole.Value.Sign < 0)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "__len__() should return >= 0",
                span,
                "ValueError"
            );
        }

        if (whole.Value > int.MaxValue)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4006",
                "cannot fit 'int' into an index-sized integer",
                span,
                "OverflowError"
            );
        }

        length = (int)whole.Value;
        return true;
    }

    internal static bool TryGetItem(
        PythonValue target,
        PythonValue index,
        TextSpan span,
        out PythonValue result
    ) => TryInvokeOnInstance(target, "__getitem__", [index], span, out result);

    internal static bool TrySetItem(
        PythonValue target,
        PythonValue index,
        PythonValue value,
        TextSpan span
    ) => TryInvokeOnInstance(target, "__setitem__", [index, value], span, out _);

    internal static bool TryDeleteItem(PythonValue target, PythonValue index, TextSpan span) =>
        TryInvokeOnInstance(target, "__delitem__", [index], span, out _);

    internal static bool TryContains(
        PythonValue container,
        PythonValue item,
        TextSpan span,
        out bool contains
    )
    {
        contains = false;
        if (!TryInvokeOnInstance(container, "__contains__", [item], span, out var result))
        {
            return false;
        }

        contains = ManagedObjectProtocols.IsTrue(result);
        return true;
    }

    internal static bool IsManagedInstance(PythonValue value) => value is PythonManagedObjectValue;

    private static bool TryInvokeOnInstance(
        PythonValue target,
        string name,
        PythonValue[] arguments,
        TextSpan span,
        out PythonValue result
    )
    {
        result = null!;
        return _dispatcher is not null
            && target is PythonManagedObjectValue
            && TryInvoke(target, name, arguments, span, out result);
    }

    // ----------------------------------------------------------------------------
    // Hashing
    // ----------------------------------------------------------------------------

    /// <summary>
    /// Whether the instance is hashable: a type that defines `__eq__` without
    /// `__hash__`, or sets `__hash__ = None`, is unhashable like CPython.
    /// </summary>
    internal static bool IsHashable(PythonManagedObjectValue instance)
    {
        if (ManagedObjectProtocols.TryGetTypeAttribute(instance.Type, "__hash__", out var hash))
        {
            return hash is not PythonNoneValue;
        }

        return !ManagedObjectProtocols.TryGetTypeAttribute(instance.Type, "__eq__", out _);
    }

    internal static bool TryGetHash(PythonValue value, TextSpan span, out BigInteger hash)
    {
        hash = 0;
        if (_dispatcher is null || value is not PythonManagedObjectValue instance)
        {
            return false;
        }

        if (!IsHashable(instance))
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4014",
                $"unhashable type: '{instance.Type.Name}'",
                span,
                "TypeError"
            );
        }

        if (!TryInvoke(value, "__hash__", [], span, out var result))
        {
            return false;
        }

        var promoted = result is PythonTruthValue truth
            ? PythonWholeNumberValue.Create(truth.Value ? BigInteger.One : BigInteger.Zero)
            : result;
        if (promoted is not PythonWholeNumberValue whole)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "__hash__ method should return an integer",
                span,
                "TypeError"
            );
        }

        // User hashes are reduced like `hash(int)` so equal integers hash equally.
        hash = ManagedObjectProtocols.ComputePythonHash(whole, span);
        return true;
    }

    // ----------------------------------------------------------------------------
    // Representation and conversion
    // ----------------------------------------------------------------------------

    internal static string? TryFormatDisplay(PythonManagedObjectValue instance)
    {
        if (_dispatcher is null)
        {
            return null;
        }

        if (TryInvoke(instance, "__str__", [], default, out var text))
        {
            return RequireText(instance, text, "__str__");
        }

        if (TryInvoke(instance, "__repr__", [], default, out var representation))
        {
            return RequireText(instance, representation, "__repr__");
        }

        return null;
    }

    internal static string? TryFormatRepresentation(PythonManagedObjectValue instance)
    {
        if (_dispatcher is null || !TryInvoke(instance, "__repr__", [], default, out var text))
        {
            return null;
        }

        return RequireText(instance, text, "__repr__");
    }

    /// <summary>`format(x, spec)` / f-string specs through `__format__`.</summary>
    internal static bool TryFormat(
        PythonValue value,
        string specification,
        TextSpan span,
        out string text
    )
    {
        text = null!;
        if (_dispatcher is null || value is not PythonManagedObjectValue instance)
        {
            return false;
        }

        if (
            TryInvoke(
                value,
                "__format__",
                [new PythonTextValue(specification)],
                span,
                out var result
            )
        )
        {
            if (result is not PythonTextValue resultText)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{instance.Type.Name}.__format__() must return a str, not {ManagedObjectProtocols.GetTypeName(result)}",
                    span,
                    "TypeError"
                );
            }

            text = resultText.Value;
            return true;
        }

        if (specification.Length != 0)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"unsupported format string passed to {instance.Type.Name}.__format__",
                span,
                "TypeError"
            );
        }

        text = instance.ToDisplayString();
        return true;
    }

    /// <summary>`int(x)` through `__int__`, then `__index__`.</summary>
    internal static bool TryConvertToInt(PythonValue value, TextSpan span, out BigInteger result)
    {
        result = 0;
        if (_dispatcher is null || value is not PythonManagedObjectValue)
        {
            return false;
        }

        foreach (var name in new[] { "__int__", "__index__" })
        {
            if (TryInvoke(value, name, [], span, out var converted))
            {
                if (converted is PythonTruthValue truth)
                {
                    result = truth.Value ? BigInteger.One : BigInteger.Zero;
                    return true;
                }

                if (converted is not PythonWholeNumberValue whole)
                {
                    throw ManagedObjectProtocols.Fault(
                        "DPY4003",
                        $"{ManagedObjectProtocols.GetTypeName(value)}.{name}() must return an int, not {ManagedObjectProtocols.GetTypeName(converted)}",
                        span,
                        "TypeError"
                    );
                }

                result = whole.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>Sequence indices through `__index__`.</summary>
    internal static bool TryConvertToIndex(PythonValue value, TextSpan span, out BigInteger result)
    {
        result = 0;
        if (
            _dispatcher is null
            || value is not PythonManagedObjectValue
            || !TryInvoke(value, "__index__", [], span, out var converted)
        )
        {
            return false;
        }

        if (converted is PythonTruthValue truth)
        {
            result = truth.Value ? BigInteger.One : BigInteger.Zero;
            return true;
        }

        if (converted is not PythonWholeNumberValue whole)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{ManagedObjectProtocols.GetTypeName(value)}.__index__() must return an int, not {ManagedObjectProtocols.GetTypeName(converted)}",
                span,
                "TypeError"
            );
        }

        result = whole.Value;
        return true;
    }

    internal static bool TryConvertToFloat(PythonValue value, TextSpan span, out double result)
    {
        result = 0;
        if (_dispatcher is null || value is not PythonManagedObjectValue)
        {
            return false;
        }

        if (TryInvoke(value, "__float__", [], span, out var converted))
        {
            if (converted is not PythonFloatingPointValue floating)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{ManagedObjectProtocols.GetTypeName(value)}.__float__() must return a float, not {ManagedObjectProtocols.GetTypeName(converted)}",
                    span,
                    "TypeError"
                );
            }

            result = floating.Value;
            return true;
        }

        if (TryConvertToIndex(value, span, out var index))
        {
            result = (double)index;
            return true;
        }

        return false;
    }

    // ----------------------------------------------------------------------------
    // Attribute hooks
    // ----------------------------------------------------------------------------

    /// <summary>`__getattr__` fallback after the normal lookup misses.</summary>
    internal static bool TryGetAttributeFallback(
        PythonManagedObjectValue instance,
        string name,
        TextSpan span,
        out PythonValue value
    ) => TryInvokeOnInstance(instance, "__getattr__", [new PythonTextValue(name)], span, out value);

    internal static bool TrySetAttribute(
        PythonManagedObjectValue instance,
        string name,
        PythonValue value,
        TextSpan span
    ) =>
        TryInvokeOnInstance(
            instance,
            "__setattr__",
            [new PythonTextValue(name), value],
            span,
            out _
        );

    internal static bool TryDeleteAttribute(
        PythonManagedObjectValue instance,
        string name,
        TextSpan span
    ) => TryInvokeOnInstance(instance, "__delattr__", [new PythonTextValue(name)], span, out _);

    private static string RequireText(
        PythonManagedObjectValue instance,
        PythonValue value,
        string method
    ) =>
        value is PythonTextValue text
            ? text.Value
            : throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{instance.Type.Name}.{method}() must return a str, not {ManagedObjectProtocols.GetTypeName(value)}",
                default,
                "TypeError"
            );
}
