using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// Runtime-native standard-library modules seeded into every module catalog. These are
/// the layer-1 stdlib substrate: pure managed implementations with CPython 3.14 error
/// surfaces, no I/O, and no VM dependency (external objects reconstruct through their
/// Stable-ABI protocol, never through interpreter frames).
/// </summary>
internal static class PythonStandardModules
{
    private const int MaximumFactorialInput = 100_000;

    internal static void AddTo(Dictionary<string, PythonModuleDefinition> modules)
    {
        modules["math"] = PythonModuleDefinition.Native(
            "<dotpython math>",
            isPackage: false,
            InitializeMath
        );
        modules["copy"] = PythonModuleDefinition.Native(
            "<dotpython copy>",
            isPackage: false,
            InitializeCopy
        );
    }

    private static void InitializeMath(PythonGlobalNamespace globals)
    {
        globals.SetValue("pi", new PythonFloatingPointValue(Math.PI));
        globals.SetValue("e", new PythonFloatingPointValue(Math.E));
        globals.SetValue("tau", new PythonFloatingPointValue(Math.Tau));
        globals.SetValue("inf", new PythonFloatingPointValue(double.PositiveInfinity));
        globals.SetValue("nan", new PythonFloatingPointValue(double.NaN));

        MathFunction(globals, "sqrt", 1, 1, (arguments, span) => Sqrt(arguments[0], span));
        MathFunction(
            globals,
            "floor",
            1,
            1,
            (arguments, span) => RealToInteger(arguments[0], Math.Floor, span)
        );
        MathFunction(
            globals,
            "ceil",
            1,
            1,
            (arguments, span) => RealToInteger(arguments[0], Math.Ceiling, span)
        );
        MathFunction(
            globals,
            "trunc",
            1,
            1,
            (arguments, span) => RealToInteger(arguments[0], Math.Truncate, span)
        );
        MathFunction(
            globals,
            "fabs",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(Math.Abs(RequireReal(arguments[0], span)))
        );
        MathFunction(
            globals,
            "copysign",
            2,
            2,
            (arguments, span) =>
                new PythonFloatingPointValue(
                    Math.CopySign(RequireReal(arguments[0], span), RequireReal(arguments[1], span))
                )
        );
        MathFunction(globals, "gcd", 0, int.MaxValue, GreatestCommonDivisor);
        MathFunction(globals, "lcm", 0, int.MaxValue, LeastCommonMultiple);
        MathFunction(globals, "factorial", 1, 1, Factorial);
        MathFunction(globals, "comb", 2, 2, Combinations);
        MathFunction(globals, "perm", 2, 2, Permutations);
        MathFunction(globals, "log", 1, 2, Logarithm);
        MathFunction(
            globals,
            "log2",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(Math.Log2(RequirePositive(arguments[0], span)))
        );
        MathFunction(
            globals,
            "log10",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(Math.Log10(RequirePositive(arguments[0], span)))
        );
        MathFunction(
            globals,
            "exp",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(Math.Exp(RequireReal(arguments[0], span)))
        );
        MathFunction(
            globals,
            "pow",
            2,
            2,
            (arguments, span) =>
                new PythonFloatingPointValue(
                    Math.Pow(RequireReal(arguments[0], span), RequireReal(arguments[1], span))
                )
        );
        MathFunction(globals, "hypot", 0, int.MaxValue, Hypotenuse);
        MathFunction(
            globals,
            "degrees",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(RequireReal(arguments[0], span) * (180.0 / Math.PI))
        );
        MathFunction(
            globals,
            "radians",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(RequireReal(arguments[0], span) * (Math.PI / 180.0))
        );
        MathFunction(
            globals,
            "sin",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(Math.Sin(RequireReal(arguments[0], span)))
        );
        MathFunction(
            globals,
            "cos",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(Math.Cos(RequireReal(arguments[0], span)))
        );
        MathFunction(
            globals,
            "tan",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(Math.Tan(RequireReal(arguments[0], span)))
        );
        MathFunction(
            globals,
            "fmod",
            2,
            2,
            (arguments, span) =>
                new PythonFloatingPointValue(
                    RequireReal(arguments[0], span) % RequireReal(arguments[1], span)
                )
        );
        MathFunction(
            globals,
            "isnan",
            1,
            1,
            (arguments, span) =>
                PythonTruthValue.FromBoolean(double.IsNaN(RequireReal(arguments[0], span)))
        );
        MathFunction(
            globals,
            "isinf",
            1,
            1,
            (arguments, span) =>
                PythonTruthValue.FromBoolean(double.IsInfinity(RequireReal(arguments[0], span)))
        );
        MathFunction(
            globals,
            "isfinite",
            1,
            1,
            (arguments, span) =>
                PythonTruthValue.FromBoolean(double.IsFinite(RequireReal(arguments[0], span)))
        );
        MathFunction(globals, "isclose", 2, 2, IsClose);
    }

    private static void MathFunction(
        PythonGlobalNamespace globals,
        string name,
        int minimumArguments,
        int maximumArguments,
        Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> implementation
    ) =>
        globals.SetValue(
            name,
            new PythonBuiltinFunctionValue(
                name,
                (arguments, span) =>
                {
                    if (arguments.Count < minimumArguments || arguments.Count > maximumArguments)
                    {
                        throw new PythonRuntimeException(
                            "DPY4028",
                            $"math.{name}() takes {minimumArguments} to {maximumArguments} arguments ({arguments.Count} given).",
                            span,
                            "TypeError"
                        );
                    }

                    return implementation(arguments, span);
                }
            )
        );

    private static PythonFloatingPointValue Sqrt(PythonValue value, TextSpan span)
    {
        var real = RequireReal(value, span);
        if (real < 0)
        {
            throw new PythonRuntimeException(
                "DPY4028",
                $"expected a nonnegative input, got {new PythonFloatingPointValue(real).ToDisplayString()}",
                span,
                "ValueError"
            );
        }

        return new PythonFloatingPointValue(Math.Sqrt(real));
    }

    private static PythonWholeNumberValue RealToInteger(
        PythonValue value,
        Func<double, double> round,
        TextSpan span
    )
    {
        var promoted = PromoteTruthValue(value);
        if (promoted is PythonWholeNumberValue whole)
        {
            return whole;
        }

        var real = RequireReal(promoted, span);
        if (double.IsInfinity(real))
        {
            throw new PythonRuntimeException(
                "DPY4028",
                "cannot convert float infinity to integer",
                span,
                "OverflowError"
            );
        }

        if (double.IsNaN(real))
        {
            throw new PythonRuntimeException(
                "DPY4028",
                "cannot convert float NaN to integer",
                span,
                "ValueError"
            );
        }

        return PythonWholeNumberValue.Create(new BigInteger(round(real)));
    }

    private static PythonValue GreatestCommonDivisor(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        var result = BigInteger.Zero;
        foreach (var argument in arguments)
        {
            result = BigInteger.GreatestCommonDivisor(result, RequireInteger(argument, span));
        }

        return PythonWholeNumberValue.Create(BigInteger.Abs(result));
    }

    private static PythonValue LeastCommonMultiple(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        var result = BigInteger.One;
        foreach (var argument in arguments)
        {
            var value = BigInteger.Abs(RequireInteger(argument, span));
            if (value.IsZero)
            {
                return PythonWholeNumberValue.Create(BigInteger.Zero);
            }

            result = result / BigInteger.GreatestCommonDivisor(result, value) * value;
        }

        return PythonWholeNumberValue.Create(result);
    }

    private static PythonValue Factorial(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var value = RequireInteger(arguments[0], span);
        if (value.Sign < 0)
        {
            throw new PythonRuntimeException(
                "DPY4028",
                "factorial() not defined for negative values",
                span,
                "ValueError"
            );
        }

        if (value > MaximumFactorialInput)
        {
            throw new PythonRuntimeException(
                "DPY4028",
                $"factorial() argument should not exceed {MaximumFactorialInput} in this runtime slice.",
                span,
                "OverflowError"
            );
        }

        var result = BigInteger.One;
        for (var factor = new BigInteger(2); factor <= value; factor++)
        {
            result *= factor;
        }

        return PythonWholeNumberValue.Create(result);
    }

    private static PythonValue Combinations(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var total = RequireNonNegativeInteger("comb", arguments[0], span);
        var chosen = RequireNonNegativeInteger("comb", arguments[1], span);
        if (chosen > total)
        {
            return PythonWholeNumberValue.Create(BigInteger.Zero);
        }

        if (chosen > total - chosen)
        {
            chosen = total - chosen;
        }

        GuardIterationCount("comb", chosen, span);
        var result = BigInteger.One;
        for (var index = BigInteger.One; index <= chosen; index++)
        {
            result = result * (total - chosen + index) / index;
        }

        return PythonWholeNumberValue.Create(result);
    }

    private static PythonValue Permutations(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var total = RequireNonNegativeInteger("perm", arguments[0], span);
        var chosen = RequireNonNegativeInteger("perm", arguments[1], span);
        if (chosen > total)
        {
            return PythonWholeNumberValue.Create(BigInteger.Zero);
        }

        GuardIterationCount("perm", chosen, span);
        var result = BigInteger.One;
        for (var index = BigInteger.Zero; index < chosen; index++)
        {
            result *= total - index;
        }

        return PythonWholeNumberValue.Create(result);
    }

    private static void GuardIterationCount(string name, BigInteger count, TextSpan span)
    {
        if (count > MaximumFactorialInput)
        {
            throw new PythonRuntimeException(
                "DPY4028",
                $"math.{name}() computation is too large for this runtime slice.",
                span,
                "OverflowError"
            );
        }
    }

    private static PythonValue Logarithm(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var value = RequirePositive(arguments[0], span);
        if (arguments.Count == 1)
        {
            return new PythonFloatingPointValue(Math.Log(value));
        }

        var logBase = RequirePositive(arguments[1], span);
        return new PythonFloatingPointValue(Math.Log(value) / Math.Log(logBase));
    }

    private static PythonValue Hypotenuse(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var sum = 0.0;
        foreach (var argument in arguments)
        {
            var real = RequireReal(argument, span);
            sum += real * real;
        }

        return new PythonFloatingPointValue(Math.Sqrt(sum));
    }

    private static PythonValue IsClose(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var left = RequireReal(arguments[0], span);
        var right = RequireReal(arguments[1], span);
        if (left == right)
        {
            return PythonTruthValue.True;
        }

        if (double.IsInfinity(left) || double.IsInfinity(right))
        {
            return PythonTruthValue.False;
        }

        const double relativeTolerance = 1e-09;
        var difference = Math.Abs(left - right);
        return PythonTruthValue.FromBoolean(
            difference <= relativeTolerance * Math.Max(Math.Abs(left), Math.Abs(right))
        );
    }

    private static double RequireReal(PythonValue value, TextSpan span) =>
        PromoteTruthValue(value) switch
        {
            PythonWholeNumberValue whole => (double)whole.Value,
            PythonFloatingPointValue floatingPoint => floatingPoint.Value,
            var other => throw new PythonRuntimeException(
                "DPY4028",
                $"must be real number, not {ManagedObjectProtocols.GetTypeName(other)}",
                span,
                "TypeError"
            ),
        };

    private static double RequirePositive(PythonValue value, TextSpan span)
    {
        var real = RequireReal(value, span);
        if (real <= 0)
        {
            throw new PythonRuntimeException(
                "DPY4028",
                "expected a positive input",
                span,
                "ValueError"
            );
        }

        return real;
    }

    private static BigInteger RequireInteger(PythonValue value, TextSpan span) =>
        PromoteTruthValue(value) switch
        {
            PythonWholeNumberValue whole => whole.Value,
            var other => throw new PythonRuntimeException(
                "DPY4028",
                $"'{ManagedObjectProtocols.GetTypeName(other)}' object cannot be interpreted as an integer",
                span,
                "TypeError"
            ),
        };

    private static BigInteger RequireNonNegativeInteger(
        string name,
        PythonValue value,
        TextSpan span
    )
    {
        var integer = RequireInteger(value, span);
        if (integer.Sign < 0)
        {
            throw new PythonRuntimeException(
                "DPY4028",
                $"{name}() requires non-negative arguments.",
                span,
                "ValueError"
            );
        }

        return integer;
    }

    private static PythonValue PromoteTruthValue(PythonValue value) =>
        value is PythonTruthValue truth
            ? PythonWholeNumberValue.Create(truth.Value ? BigInteger.One : BigInteger.Zero)
            : value;

    private static void InitializeCopy(PythonGlobalNamespace globals)
    {
        globals.SetValue(
            "copy",
            new PythonBuiltinFunctionValue(
                "copy",
                (arguments, span) =>
                {
                    RequireCopyArguments("copy", arguments, span);
                    return ShallowCopy(arguments[0], span);
                }
            )
        );
        globals.SetValue(
            "deepcopy",
            new PythonBuiltinFunctionValue(
                "deepcopy",
                (arguments, span) =>
                {
                    RequireCopyArguments("deepcopy", arguments, span);
                    return DeepCopy(
                        arguments[0],
                        new Dictionary<PythonValue, PythonValue>(
                            ReferenceEqualityComparer.Instance
                        ),
                        span
                    );
                }
            )
        );
    }

    private static void RequireCopyArguments(
        string name,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count != 1)
        {
            throw new PythonRuntimeException(
                "DPY4028",
                $"copy.{name}() takes exactly one argument ({arguments.Count} given).",
                span,
                "TypeError"
            );
        }
    }

    private static PythonValue ShallowCopy(PythonValue value, TextSpan span) =>
        value switch
        {
            PythonListValue list => new PythonListValue([.. list.Elements]),
            PythonDictionaryValue dictionary => new PythonDictionaryValue([
                .. dictionary.Items.Select(item => new PythonDictionaryItemValue(
                    item.Key,
                    item.Value
                )),
            ]),
            PythonSetValue { IsFrozen: false } set => new PythonSetValue([.. set.Elements]),
            PythonManagedObjectValue instance => CopyInstance(instance, deep: false, null, span),
            PythonExternalObjectValue external => ReconstructExternal(external, null, span),
            _ => value,
        };

    private static PythonValue DeepCopy(
        PythonValue value,
        Dictionary<PythonValue, PythonValue> memo,
        TextSpan span
    )
    {
        if (memo.TryGetValue(value, out var existing))
        {
            return existing;
        }

        switch (value)
        {
            case PythonListValue list:
            {
                var copy = new PythonListValue([]);
                memo[value] = copy;
                foreach (var element in list.Elements)
                {
                    copy.Elements.Add(DeepCopy(element, memo, span));
                }

                return copy;
            }
            case PythonDictionaryValue dictionary:
            {
                var copy = new PythonDictionaryValue([]);
                memo[value] = copy;
                foreach (var item in dictionary.Items)
                {
                    copy.Items.Add(
                        new PythonDictionaryItemValue(
                            DeepCopy(item.Key, memo, span),
                            DeepCopy(item.Value, memo, span)
                        )
                    );
                }

                return copy;
            }
            case PythonSetValue set:
            {
                var copy = new PythonSetValue([]) { IsFrozen = set.IsFrozen };
                memo[value] = copy;
                foreach (var element in set.Elements)
                {
                    copy.Elements.Add(DeepCopy(element, memo, span));
                }

                return copy;
            }
            case PythonTupleValue tuple:
            {
                var elements = new PythonValue[tuple.Elements.Length];
                var unchanged = true;
                for (var index = 0; index < tuple.Elements.Length; index++)
                {
                    elements[index] = DeepCopy(tuple.Elements[index], memo, span);
                    unchanged &= ReferenceEquals(elements[index], tuple.Elements[index]);
                }

                if (unchanged)
                {
                    return tuple;
                }

                var copy = new PythonTupleValue(elements);
                memo[value] = copy;
                return copy;
            }
            case PythonManagedObjectValue instance:
                return CopyInstance(instance, deep: true, memo, span);
            case PythonExternalObjectValue external:
            {
                var copy = ReconstructExternal(external, memo, span);
                memo[value] = copy;
                return copy;
            }
            default:
                return value;
        }
    }

    private static PythonManagedObjectValue CopyInstance(
        PythonManagedObjectValue instance,
        bool deep,
        Dictionary<PythonValue, PythonValue>? memo,
        TextSpan span
    )
    {
        var copy = new PythonManagedObjectValue(instance.Type);
        if (deep)
        {
            memo![instance] = copy;
        }

        foreach (var (name, attribute) in instance.Attributes)
        {
            copy.Attributes[name] = deep ? DeepCopy(attribute, memo!, span) : attribute;
        }

        return copy;
    }

    private static PythonValue ReconstructExternal(
        PythonExternalObjectValue external,
        Dictionary<PythonValue, PythonValue>? memo,
        TextSpan span
    )
    {
        // Native Stable-ABI objects copy through the reduce protocol:
        // `__reduce__()` returns `(callable, args)`, both crossing the boundary as
        // managed-visible values, and calling the callable rebuilds the object.
        var reduce = external.Protocol.GetAttribute("__reduce__", span);
        var reduced = ManagedObjectProtocols.Call(reduce, [], span);
        if (
            reduced
            is not PythonTupleValue { Elements: [var factory, PythonTupleValue factoryArguments] }
        )
        {
            throw new PythonRuntimeException(
                "DPY4028",
                $"cannot copy '{ManagedObjectProtocols.GetTypeName(external)}' object: __reduce__ did not return (callable, args).",
                span,
                "TypeError"
            );
        }

        var arguments = new PythonValue[factoryArguments.Elements.Length];
        for (var index = 0; index < arguments.Length; index++)
        {
            arguments[index] = memo is null
                ? factoryArguments.Elements[index]
                : DeepCopy(factoryArguments.Elements[index], memo, span);
        }

        return ManagedObjectProtocols.Call(factory, arguments, span);
    }
}
