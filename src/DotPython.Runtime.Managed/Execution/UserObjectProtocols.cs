using System.Numerics;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Where a running frame stands: its file, its line, and the module it runs in.</summary>
internal readonly record struct PythonFrameLocation(
    string FileName,
    int Line,
    string Module,
    PythonGlobalNamespace? Globals
);

/// <summary>
/// Runs user-defined special methods on the owning VM for the static protocol layer,
/// which cannot execute interpreter frames itself.
/// </summary>
internal interface IUserObjectDispatcher
{
    TextSpan CurrentSpan { get; }

    PythonCodecErrorRegistry CodecErrors { get; }

    PythonManagedTypeValue ExceptionGroupType { get; }

    PythonValue InvokeExceptionGroupMethod(
        string name,
        PythonExceptionValue group,
        PythonValue argument,
        TextSpan span
    );

    PythonExceptionValue AllocateException(
        string allocator,
        PythonValue type,
        PythonValue[] arguments,
        TextSpan span
    );

    (bool HasValue, PythonValue Value) ThrowGenerator(
        PythonGeneratorValue generator,
        PythonValue exception,
        TextSpan span
    );

    (bool HasValue, PythonValue Value) ResumeGenerator(
        PythonGeneratorValue generator,
        PythonValue? sent,
        PythonExceptionValue? injected,
        TextSpan span
    );

    PythonValue Invoke(PythonValue callable, PythonValue[] arguments, TextSpan span);

    /// <summary>
    /// Calls a user-defined callable with keyword arguments, for native code that must
    /// honour the same calling convention Python source uses.
    /// </summary>
    PythonValue InvokeWithKeywords(
        PythonValue callable,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    );

    (bool HasValue, PythonValue Value) StepUserIterator(PythonValue nextMethod, TextSpan span);

    PythonIteratorValue GetUserIterator(PythonManagedObjectValue instance, TextSpan span);

    void CheckIterationWork(TextSpan span);

    /// <summary>The `__name__` of the module the running frame belongs to.</summary>
    string? CurrentModuleName();

    /// <summary>
    /// Where a call came from: the file and line of the frame `level` steps up from the
    /// running one — level 1 is the caller of a builtin — with the module name and globals
    /// of that frame, which is what `warnings.warn` reports a warning at.
    /// </summary>
    PythonFrameLocation? CallerLocation(int level);

    (bool HasValue, PythonValue Value) StepSequenceIterator(
        PythonSequenceIteratorSourceValue source,
        TextSpan span
    );

    PythonValue ConstructType(IReadOnlyList<PythonValue> arguments, TextSpan span);

    PythonValue GetSubclasses(PythonValue type, TextSpan span);

    void SetTypeBases(PythonManagedTypeValue type, PythonTupleValue bases, TextSpan span);

    PythonValue CreateType(
        PythonValue metaclass,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    );

    PythonValue CallType(
        PythonValue type,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    );

    PythonBuiltinFunctionValue GetBuiltinConstructor(string name);

    /// <summary>The stable identity token for a value, as `id()` returns it.</summary>
    PythonValue GetIdentity(PythonValue value);

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
            method = ManagedObjectProtocols.BindDescriptor(
                attribute,
                managed,
                managed.Type,
                attributeName: name
            );
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

    /// <summary>
    /// Whether the type itself supplies the slot, rather than inheriting one. CPython tries
    /// the reflected slot of a subclass operand first only when that subclass *overrides*
    /// it, which is the difference between `Sub(dict) | other` and `Dict | Sub(dict)`.
    /// </summary>
    internal static bool OwnsSpecialMethod(PythonValue value, string name) =>
        value is PythonManagedObjectValue managed
        && ManagedObjectProtocols.TryGetOwnTypeAttribute(managed.Type, name, out _);

    private static bool TryInvoke(
        PythonValue value,
        string name,
        PythonValue[] arguments,
        TextSpan span,
        out PythonValue result,
        bool invokeNone = false
    )
    {
        if (!TryGetSpecialMethod(value, name, out var method, out _))
        {
            result = null!;
            return false;
        }

        if (method is PythonNoneValue && !invokeNone)
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
        // pathlib paths are external objects with a native `__truediv__`/`__rtruediv__`.
        if (
            opCode == PythonOpCode.BinaryTrueDivide
            && PythonPathlib.TryApplyTrueDivide(left, right, span, out result)
        )
        {
            return true;
        }

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
            && OwnsSpecialMethod(right, names.Reflected);

        if (
            reflectedFirst
            && TryInvoke(right, names.Reflected, [left], span, out result, invokeNone: true)
        )
        {
            if (result is not PythonNotImplementedValue)
            {
                return true;
            }
        }

        if (TryInvoke(left, names.Forward, [right], span, out result, invokeNone: true))
        {
            if (result is not PythonNotImplementedValue)
            {
                return true;
            }
        }

        if (
            !reflectedFirst
            && TryInvoke(right, names.Reflected, [left], span, out result, invokeNone: true)
        )
        {
            if (result is not PythonNotImplementedValue)
            {
                return true;
            }
        }

        // Sequence repetition follows failed numeric slots, including reflected ones.
        if (
            opCode == PythonOpCode.BinaryMultiply
            && (
                left
                    is PythonListValue
                        or PythonTupleValue
                        or PythonTextValue
                        or PythonByteSequenceValue
                || right
                    is PythonListValue
                        or PythonTupleValue
                        or PythonTextValue
                        or PythonByteSequenceValue
            )
        )
            return false;

        // A builtin left operand handles its own operators in the interpreter — `bytes`
        // takes a buffer on its right — so the user-object dispatch steps aside here and
        // lets the interpreter try that, refusing in its own words if nothing applies.
        if (left is not PythonManagedObjectValue)
            return false;

        // A dictionary subclass unioned with another dictionary is `dict.__or__`'s to
        // answer — `Counter(...) | {...}` answers NotImplemented and the plain dict's own
        // union follows — so the pair steps aside for the interpreter as well.
        if (
            opCode == PythonOpCode.BinaryOr
            && PythonSubclassStorage.StorageKindOf(left) == "dict"
            && PythonSubclassStorage.Resolve(right) is PythonDictionaryValue
        )
            return false;

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
            TryInvoke(left, names.InPlace, [right], span, out result, invokeNone: true)
            && result is not PythonNotImplementedValue
        )
        {
            // An in-place slot returns the object it was given, and the builtin slots of a
            // storage subclass answer for the storage — `d |= {...}` is still the subclass
            // instance, which is what CPython's `dict.__ior__` returning `self` gives.
            if (ReferenceEquals(result, PythonSubclassStorage.Of(left)))
                result = left;
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

    internal static PythonValue InvokeSortRichCompare(
        PythonManagedObjectValue left,
        PythonValue right,
        TextSpan span
    ) =>
        TryInvoke(left, "__lt__", [right], span, out var result, invokeNone: true)
            ? result
            : PythonNotImplementedValue.Instance;

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
            && OwnsSpecialMethod(right, reflectedNames.Forward);

        if (
            reflectedFirst
            && TryInvoke(right, reflectedNames.Forward, [left], span, out result, invokeNone: true)
            && result is not PythonNotImplementedValue
        )
        {
            return true;
        }

        if (
            TryInvoke(left, names.Forward, [right], span, out result, invokeNone: true)
            && result is not PythonNotImplementedValue
        )
        {
            return true;
        }

        if (
            !reflectedFirst
            && TryInvoke(right, reflectedNames.Forward, [left], span, out result, invokeNone: true)
            && result is not PythonNotImplementedValue
        )
        {
            return true;
        }

        // A dictionary subclass whose `__eq__` declined — `Counter.__eq__` answers
        // NotImplemented for anything that is not a counter — is compared as the dictionary
        // it carries, which is `dict.__eq__`'s answer and is applied by the equality switch.
        if (
            comparison is PythonRichComparison.Equal or PythonRichComparison.NotEqual
            && (
                PythonSubclassStorage.StorageKindOf(left) == "dict"
                    && right is PythonDictionaryValue
                || PythonSubclassStorage.StorageKindOf(right) == "dict"
                    && left is PythonDictionaryValue
            )
        )
        {
            return false;
        }

        switch (comparison)
        {
            case PythonRichComparison.Equal:
                result = PythonTruthValue.FromBoolean(ReferenceEquals(left, right));
                return true;
            case PythonRichComparison.NotEqual:
                // Default `__ne__` inverts `__eq__` unless that is also NotImplemented.
                if (
                    TryInvoke(left, "__eq__", [right], span, out var equal, invokeNone: true)
                        && equal is not PythonNotImplementedValue
                    || TryInvoke(right, "__eq__", [left], span, out equal, invokeNone: true)
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
                    $"__bool__ should return bool, returned {ManagedObjectProtocols.GetTypeName(result)}",
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

    /// <summary>
    /// Calls the instance's type's `__missing__`, which a miss on a dictionary storage
    /// consults: `defaultdict` and `Counter` answer a missing key through it, and a user
    /// subclass of `dict` that defines one sees its own.
    /// </summary>
    internal static bool TryInvokeMissing(
        PythonValue instance,
        PythonValue key,
        TextSpan span,
        out PythonValue result
    ) => TryInvokeOnInstance(instance, "__missing__", [key], span, out result);

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
        if (!TryGetSpecialMethod(container, "__contains__", out var method, out _))
            return false;
        if (method is PythonNoneValue)
            throw ManagedObjectProtocols.Fault(
                "DPY4015",
                $"'{ManagedObjectProtocols.GetTypeName(container)}' object is not a container",
                span,
                "TypeError"
            );
        contains = ManagedObjectProtocols.IsTrue(_dispatcher!.Invoke(method, [item], span));
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
                $"unhashable type: '{instance.Type.ReportedName}'",
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

        hash = PythonNumericHash.UserResult(whole.Value);
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
            return RequireText(text, "__str__");
        }

        if (TryInvoke(instance, "__repr__", [], default, out var representation))
        {
            return RequireText(representation, "__repr__");
        }

        return null;
    }

    internal static string? TryFormatRepresentation(PythonManagedObjectValue instance)
    {
        if (_dispatcher is null || !TryInvoke(instance, "__repr__", [], default, out var text))
        {
            return null;
        }

        return RequireText(text, "__repr__");
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
                    $"__format__ must return a str, not {ManagedObjectProtocols.GetTypeName(result)}",
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
                        $"{name} returned non-int (type {ManagedObjectProtocols.GetTypeName(converted)})",
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
            || !TryGetSpecialMethod(value, "__index__", out var method, out _)
        )
        {
            return false;
        }

        var converted = _dispatcher.Invoke(method, [], span);

        if (converted is PythonTruthValue truth)
        {
            result = truth.Value ? BigInteger.One : BigInteger.Zero;
            return true;
        }

        if (converted is not PythonWholeNumberValue whole)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__index__ returned non-int (type {ManagedObjectProtocols.GetTypeName(converted)})",
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
                    $"__float__ returned non-float (type {ManagedObjectProtocols.GetTypeName(converted)})",
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

    /// <summary>
    /// `complex(value)` for an object that implements the protocol itself. The conversion
    /// is asked for before the real-number fallbacks, which is the order CPython's own
    /// `complex()` uses.
    /// </summary>
    internal static bool TryConvertToComplex(
        PythonValue value,
        TextSpan span,
        out System.Numerics.Complex result
    )
    {
        result = default;
        if (
            _dispatcher is null
            || value is not PythonManagedObjectValue
            || !TryInvoke(value, "__complex__", [], span, out var converted)
        )
        {
            return false;
        }

        if (converted is not PythonComplexValue complex)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__complex__ returned non-complex (type {ManagedObjectProtocols.GetTypeName(converted)})",
                span,
                "TypeError"
            );
        }

        result = complex.Value;
        return true;
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

    private static string RequireText(PythonValue value, string method) =>
        value is PythonTextValue text
            ? text.Value
            : throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{method} returned non-string (type {ManagedObjectProtocols.GetTypeName(value)})",
                default,
                "TypeError"
            );
}
