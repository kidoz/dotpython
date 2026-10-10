// The `operator` module follows CPython 3.14.7 Lib/operator.py and Modules/_operator.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The <c>operator</c> module: the operators as functions, which is what the standard
/// library's own code is written against.
/// </summary>
/// <remarks>
/// Every name is the operation the interpreter performs, reached through the same paths — the
/// comparison, binary, unary and in-place slots, the object protocol for the item and
/// membership names, and the same refusals — so a value that overloads an operator sees the
/// module call it the way the syntax does.
/// </remarks>
internal static class PythonOperatorModule
{
    /// <summary>The module's names, in the order CPython publishes them.</summary>
    private static readonly string[] ExportedNames =
    [
        "abs",
        "add",
        "and_",
        "attrgetter",
        "call",
        "concat",
        "contains",
        "countOf",
        "delitem",
        "eq",
        "floordiv",
        "ge",
        "getitem",
        "gt",
        "iadd",
        "iand",
        "iconcat",
        "ifloordiv",
        "ilshift",
        "imatmul",
        "imod",
        "imul",
        "index",
        "indexOf",
        "inv",
        "invert",
        "ior",
        "ipow",
        "irshift",
        "is_",
        "is_none",
        "is_not",
        "is_not_none",
        "isub",
        "itemgetter",
        "itruediv",
        "ixor",
        "le",
        "length_hint",
        "lshift",
        "lt",
        "matmul",
        "methodcaller",
        "mod",
        "mul",
        "ne",
        "neg",
        "not_",
        "or_",
        "pos",
        "pow",
        "rshift",
        "setitem",
        "sub",
        "truediv",
        "truth",
        "xor",
    ];

    /// <summary>The module's names.</summary>
    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue("__doc__", new PythonTextValue("Operator interface.\n"));
        globals.SetValue(
            "__all__",
            new PythonListValue([
                .. ExportedNames.Select(name => (PythonValue)new PythonTextValue(name)),
            ])
        );
        globals.SetValue("attrgetter", PythonOperatorGetters.AttributeGetterType);
        globals.SetValue("itemgetter", PythonOperatorGetters.ItemGetterType);
        globals.SetValue("methodcaller", PythonOperatorGetters.MethodCallerType);
        foreach (var name in ExportedNames)
        {
            if (Function(name) is { } function)
                globals.SetValue(name, function);
        }
    }

    /// <summary>The function a name stands for, or null for the three classes.</summary>
    private static PythonBuiltinFunctionValue? Function(string name) =>
        name switch
        {
            "attrgetter" or "itemgetter" or "methodcaller" => null,
            "abs" => Function(
                "abs",
                (arguments, span) =>
                    Unary(
                        "abs",
                        arguments,
                        span,
                        value => PythonVirtualMachine.ApplyAbsoluteOperator(value, span)
                    )
            ),
            "not_" => Function(
                "not_",
                (arguments, span) =>
                    Unary(
                        "not_",
                        arguments,
                        span,
                        value => PythonTruthValue.FromBoolean(!ManagedObjectProtocols.IsTrue(value))
                    )
            ),
            "truth" => Function(
                "truth",
                (arguments, span) =>
                    Unary(
                        "truth",
                        arguments,
                        span,
                        value =>
                            ManagedObjectProtocols.IsTrue(value)
                                ? PythonTruthValue.True
                                : PythonTruthValue.False
                    )
            ),
            "neg" => Unary1("neg", PythonOpCode.UnaryNegative),
            "pos" => Unary1("pos", PythonOpCode.UnaryPositive),
            "inv" or "invert" => Unary1(name, PythonOpCode.UnaryInvert),
            "index" => Function("index", Index),
            "is_" => Function(
                "is_",
                (arguments, span) => Identity("is_", arguments, span, equal: true)
            ),
            "is_not" => Function(
                "is_not",
                (arguments, span) => Identity("is_not", arguments, span, equal: false)
            ),
            "is_none" => Function(
                "is_none",
                (arguments, span) =>
                    Unary(
                        "is_none",
                        arguments,
                        span,
                        value =>
                            value is PythonNoneValue
                                ? PythonTruthValue.True
                                : PythonTruthValue.False
                    )
            ),
            "is_not_none" => Function(
                "is_not_none",
                (arguments, span) =>
                    Unary(
                        "is_not_none",
                        arguments,
                        span,
                        value =>
                            value is PythonNoneValue
                                ? PythonTruthValue.False
                                : PythonTruthValue.True
                    )
            ),
            "lt" => Compare("lt", PythonRichComparison.LessThan),
            "le" => Compare("le", PythonRichComparison.LessThanOrEqual),
            "eq" => Compare("eq", PythonRichComparison.Equal),
            "ne" => Compare("ne", PythonRichComparison.NotEqual),
            "ge" => Compare("ge", PythonRichComparison.GreaterThanOrEqual),
            "gt" => Compare("gt", PythonRichComparison.GreaterThan),
            "add" or "concat" => Binary("add" == name ? "add" : "concat", PythonOpCode.BinaryAdd),
            "sub" => Binary("sub", PythonOpCode.BinarySubtract),
            "mul" => Binary("mul", PythonOpCode.BinaryMultiply),
            "matmul" => Binary("matmul", PythonOpCode.BinaryMatrixMultiply),
            "truediv" => Binary("truediv", PythonOpCode.BinaryTrueDivide),
            "floordiv" => Binary("floordiv", PythonOpCode.BinaryFloorDivide),
            "mod" => Binary("mod", PythonOpCode.BinaryModulo),
            "pow" => Binary("pow", PythonOpCode.BinaryPower),
            "lshift" => Binary("lshift", PythonOpCode.BinaryLeftShift),
            "rshift" => Binary("rshift", PythonOpCode.BinaryRightShift),
            "and_" => Binary("and_", PythonOpCode.BinaryAnd),
            "or_" => Binary("or_", PythonOpCode.BinaryOr),
            "xor" => Binary("xor", PythonOpCode.BinaryXor),
            "iadd" or "iconcat" => InPlace(name, PythonOpCode.BinaryAdd, "__iadd__"),
            "isub" => InPlace("isub", PythonOpCode.BinarySubtract, "__isub__"),
            "imul" => InPlace("imul", PythonOpCode.BinaryMultiply, "__imul__"),
            "imatmul" => InPlace("imatmul", PythonOpCode.BinaryMatrixMultiply, "__imatmul__"),
            "itruediv" => InPlace("itruediv", PythonOpCode.BinaryTrueDivide, "__itruediv__"),
            "ifloordiv" => InPlace("ifloordiv", PythonOpCode.BinaryFloorDivide, "__ifloordiv__"),
            "imod" => InPlace("imod", PythonOpCode.BinaryModulo, "__imod__"),
            "ipow" => InPlace("ipow", PythonOpCode.BinaryPower, "__ipow__"),
            "ilshift" => InPlace("ilshift", PythonOpCode.BinaryLeftShift, "__ilshift__"),
            "irshift" => InPlace("irshift", PythonOpCode.BinaryRightShift, "__irshift__"),
            "iand" => InPlace("iand", PythonOpCode.BinaryAnd, "__iand__"),
            "ior" => InPlace("ior", PythonOpCode.BinaryOr, "__ior__"),
            "ixor" => InPlace("ixor", PythonOpCode.BinaryXor, "__ixor__"),
            "contains" => Function(
                "contains",
                (arguments, span) =>
                    Two(
                        "contains",
                        arguments,
                        span,
                        (container, item, callSpan) =>
                            ManagedObjectProtocols.Contains(container, item, callSpan)
                                ? PythonTruthValue.True
                                : PythonTruthValue.False
                    )
            ),
            "countOf" => Function(
                "countOf",
                (arguments, span) =>
                    Two(
                        "countOf",
                        arguments,
                        span,
                        (container, item, callSpan) =>
                            PythonWholeNumberValue.Create(Count(container, item, callSpan))
                    )
            ),
            "indexOf" => Function(
                "indexOf",
                (arguments, span) => Two("indexOf", arguments, span, IndexOf)
            ),
            "getitem" => Function(
                "getitem",
                (arguments, span) =>
                    Two(
                        "getitem",
                        arguments,
                        span,
                        (target, index, callSpan) =>
                            ManagedObjectProtocols.GetItem(target, index, callSpan)
                    )
            ),
            "setitem" => Function(
                "setitem",
                (arguments, span) =>
                {
                    RequireArguments(name, arguments, 3, span);
                    ManagedObjectProtocols.SetItem(arguments[0], arguments[1], arguments[2], span);
                    return PythonNoneValue.Instance;
                }
            ),
            "delitem" => Function(
                "delitem",
                (arguments, span) =>
                {
                    RequireArguments(name, arguments, 2, span);
                    ManagedObjectProtocols.DeleteItem(arguments[0], arguments[1], span);
                    return PythonNoneValue.Instance;
                }
            ),
            "length_hint" => Function("length_hint", LengthHint),
            "call" => new PythonBuiltinFunctionValue(
                "call",
                (arguments, span) => Call("call", arguments, [], [], span),
                (arguments, names, values, span) => Call("call", arguments, names, values, span)
            ),
            _ => null,
        };

    /// <summary>A function of the module, reported as the builtin C function it is.</summary>
    private static PythonBuiltinFunctionValue Function(
        string name,
        Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> body
    ) => new(name, body);

    private static PythonValue Unary(
        string name,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span,
        Func<PythonValue, PythonValue> apply
    )
    {
        RequireArguments(name, arguments, 1, span);
        return apply(arguments[0]);
    }

    private static PythonBuiltinFunctionValue Unary1(string name, PythonOpCode opCode) =>
        Function(
            name,
            (arguments, span) =>
                Unary(
                    name,
                    arguments,
                    span,
                    value => PythonVirtualMachine.ApplyUnaryOperator(opCode, value, span)
                )
        );

    private static PythonBuiltinFunctionValue Binary(string name, PythonOpCode opCode) =>
        Function(
            name,
            (arguments, span) =>
            {
                RequireArguments(name, arguments, 2, span);
                return PythonVirtualMachine.ApplyBinaryOperator(
                    opCode,
                    arguments[0],
                    arguments[1],
                    span
                );
            }
        );

    private static PythonBuiltinFunctionValue InPlace(
        string name,
        PythonOpCode opCode,
        string method
    ) =>
        Function(
            name,
            (arguments, span) =>
            {
                RequireArguments(name, arguments, 2, span);
                return PythonVirtualMachine.ApplyInPlaceOperator(
                    opCode,
                    method,
                    arguments[0],
                    arguments[1],
                    span
                );
            }
        );

    private static PythonBuiltinFunctionValue Compare(
        string name,
        PythonRichComparison comparison
    ) =>
        Function(
            name,
            (arguments, span) =>
            {
                RequireArguments(name, arguments, 2, span);
                return ManagedObjectProtocols.RichCompareValue(
                    arguments[0],
                    arguments[1],
                    comparison,
                    span
                );
            }
        );

    private static PythonTruthValue Identity(
        string name,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span,
        bool equal
    )
    {
        RequireArguments(name, arguments, 2, span);
        var same = ReferenceEquals(arguments[0], arguments[1]);
        return PythonTruthValue.FromBoolean(equal ? same : !same);
    }

    private static PythonValue Two(
        string name,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span,
        Func<PythonValue, PythonValue, TextSpan, PythonValue> apply
    )
    {
        RequireArguments(name, arguments, 2, span);
        return apply(arguments[0], arguments[1], span);
    }

    /// <summary>`operator.index`: the integer an object stands for.</summary>
    private static PythonValue Index(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        RequireArguments("index", arguments, 1, span);
        return PythonWholeNumberValue.Create(
            PythonBuiltinFunctions.RequireIndex(arguments[0], span)
        );
    }

    /// <summary>`operator.length_hint(obj, default=8)`: the hint an iterator reports.</summary>
    private static PythonValue LengthHint(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        RequireArguments("length_hint", arguments, 1, 2, span);
        var fallback = arguments.Count == 2 ? arguments[1] : PythonWholeNumberValue.Create(8);
        if (fallback is not PythonWholeNumberValue)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "length_hint() default must be an integer",
                span,
                "TypeError"
            );
        return PythonWholeNumberValue.Create(
            PythonLengthHints.GetLengthHint(
                arguments[0],
                span,
                (long)((PythonWholeNumberValue)fallback).Value
            )
        );
    }

    /// <summary>`operator.call(obj, /, *args, **kwargs)`: the object called with them.</summary>
    private static PythonValue Call(
        string name,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (arguments.Count == 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{name}() missing required argument 'obj' (pos 1)",
                span,
                "TypeError"
            );
        var rest = new PythonValue[arguments.Count - 1];
        for (var index = 1; index < arguments.Count; index++)
            rest[index - 1] = arguments[index];
        var callable = arguments[0];
        if (keywordNames.Count == 0)
            return ManagedObjectProtocols.Call(callable, rest, span);
        return UserObjectProtocols.Dispatcher!.InvokeWithKeywords(
            callable,
            rest,
            keywordNames,
            keywordValues,
            span
        );
    }

    /// <summary>`operator.countOf(a, b)`: how many items of `a` equal `b`.</summary>
    private static int Count(PythonValue container, PythonValue item, TextSpan span)
    {
        var count = 0;
        foreach (var element in Elements(container, "countOf", span))
        {
            if (ManagedObjectProtocols.AreEqual(element, item))
                count++;
        }
        return count;
    }

    /// <summary>`operator.indexOf(a, b)`: the first index of `b` in `a`.</summary>
    private static PythonValue IndexOf(PythonValue container, PythonValue item, TextSpan span)
    {
        var index = 0;
        foreach (var element in Elements(container, "indexOf", span))
        {
            if (ManagedObjectProtocols.AreEqual(element, item))
                return PythonWholeNumberValue.Create(index);
            index++;
        }
        throw ManagedObjectProtocols.Fault(
            "DPY4003",
            "sequence.index(x): x not in sequence",
            span,
            "ValueError"
        );
    }

    /// <summary>The items of a container, refused in CPython's words when it has none.</summary>
    private static List<PythonValue> Elements(PythonValue container, string name, TextSpan span)
    {
        try
        {
            return ManagedObjectProtocols.MaterializeValues(container, span);
        }
        catch (PythonRuntimeException fault) when (fault.PythonExceptionTypeName == "TypeError")
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"argument of type '{ManagedObjectProtocols.GetTypeName(container)}' is not "
                    + "iterable",
                span,
                "TypeError"
            );
        }
    }

    private static void RequireArguments(
        string name,
        IReadOnlyList<PythonValue> arguments,
        int count,
        TextSpan span
    ) => RequireArguments(name, arguments, count, count, span);

    private static void RequireArguments(
        string name,
        IReadOnlyList<PythonValue> arguments,
        int minimum,
        int maximum,
        TextSpan span
    )
    {
        if (arguments.Count >= minimum && arguments.Count <= maximum)
            return;
        var message =
            minimum == maximum
                ? $"{name} expected {minimum} argument{(minimum == 1 ? "" : "s")}, got "
                    + $"{arguments.Count}"
                : $"{name} expected at most {maximum} arguments, got {arguments.Count}";
        throw ManagedObjectProtocols.Fault("DPY4003", message, span, "TypeError");
    }
}
