using System.Globalization;
using System.Numerics;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// VM-independent builtins: numeric text conversions, `format`/`ascii`, the `slice`
/// and `complex` constructors, the descriptor wrappers (`property`, `staticmethod`,
/// `classmethod`), `reversed`, and the `object` base type with its default protocol.
/// </summary>
internal static class PythonBuiltinFunctions
{
    /// <summary>The type of bare `object()` instances.</summary>
    internal static PythonManagedTypeValue ObjectType { get; } = new("object");

    internal static readonly PythonBuiltinTypeValue Object = new("object", ConstructObject);

    internal static IEnumerable<PythonBuiltinFunctionValue> All =>
        [
            new("hex", (arguments, span) => FormatIntegerBase("hex", arguments, 16, "0x", span)),
            new("oct", (arguments, span) => FormatIntegerBase("oct", arguments, 8, "0o", span)),
            new("bin", (arguments, span) => FormatIntegerBase("bin", arguments, 2, "0b", span)),
            new("format", Format),
            new("ascii", Ascii),
            new("slice", Slice),
            new("property", Property),
            new("staticmethod", StaticMethod),
            new("classmethod", ClassMethod),
            new("reversed", Reversed),
        ];

    internal static PythonBuiltinTypeValue Complex { get; } = new("complex", ConstructComplex);

    // ----------------------------------------------------------------------------
    // hex / oct / bin
    // ----------------------------------------------------------------------------

    private static PythonTextValue FormatIntegerBase(
        string name,
        IReadOnlyList<PythonValue> arguments,
        int radix,
        string prefix,
        TextSpan span
    )
    {
        RequireArgumentCount(name, arguments, 1, 1, span);
        var value = RequireIndex(arguments[0], span);
        return new PythonTextValue(
            (value.Sign < 0 ? "-" : string.Empty) + prefix + ToBase(BigInteger.Abs(value), radix)
        );
    }

    internal static string ToBase(BigInteger magnitude, int radix)
    {
        if (magnitude.IsZero)
        {
            return "0";
        }

        const string digits = "0123456789abcdef";
        var builder = new StringBuilder();
        while (!magnitude.IsZero)
        {
            magnitude = BigInteger.DivRem(magnitude, radix, out var remainder);
            builder.Insert(0, digits[(int)remainder]);
        }

        return builder.ToString();
    }

    internal static BigInteger RequireIndex(PythonValue value, TextSpan span)
    {
        if (UserObjectProtocols.TryConvertToIndex(value, span, out var userIndex))
        {
            return userIndex;
        }

        return value switch
        {
            PythonWholeNumberValue whole => whole.Value,
            PythonTruthValue truth => truth.Value ? BigInteger.One : BigInteger.Zero,
            _ => throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object cannot be interpreted as an integer",
                span,
                "TypeError"
            ),
        };
    }

    // ----------------------------------------------------------------------------
    // format / ascii
    // ----------------------------------------------------------------------------

    private static PythonTextValue Format(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        RequireArgumentCount("format", arguments, 1, 2, span);
        var specification = string.Empty;
        if (arguments.Count == 2)
        {
            if (arguments[1] is not PythonTextValue specText)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"format() argument 2 must be str, not {ManagedObjectProtocols.GetTypeName(arguments[1])}",
                    span,
                    "TypeError"
                );
            }

            specification = specText.Value;
        }

        return new PythonTextValue(PythonValueFormatter.Format(arguments[0], specification, span));
    }

    internal static PythonTextValue Ascii(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        RequireArgumentCount("ascii", arguments, 1, 1, span);
        var representation = arguments[0].ToRepresentationString();
        var builder = new StringBuilder(representation.Length);
        foreach (var rune in representation.EnumerateRunes())
        {
            if (rune.Value < 128)
            {
                builder.Append(rune.ToString());
            }
            else if (rune.Value <= byte.MaxValue)
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\x{rune.Value:x2}");
            }
            else if (rune.Value <= char.MaxValue)
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\u{rune.Value:x4}");
            }
            else
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\U{rune.Value:x8}");
            }
        }

        return new PythonTextValue(builder.ToString());
    }

    // ----------------------------------------------------------------------------
    // slice / complex
    // ----------------------------------------------------------------------------

    private static PythonSliceValue Slice(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        RequireArgumentCount("slice", arguments, 1, 3, span);
        return arguments.Count switch
        {
            1 => new PythonSliceValue(
                PythonNoneValue.Instance,
                arguments[0],
                PythonNoneValue.Instance
            ),
            2 => new PythonSliceValue(arguments[0], arguments[1], PythonNoneValue.Instance),
            _ => new PythonSliceValue(arguments[0], arguments[1], arguments[2]),
        };
    }

    private static PythonComplexValue ConstructComplex(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        RequireArgumentCount("complex", arguments, 0, 2, span);
        if (arguments.Count == 0)
        {
            return new PythonComplexValue(System.Numerics.Complex.Zero);
        }

        if (arguments[0] is PythonTextValue text)
        {
            if (arguments.Count == 2)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    "complex() argument 'real' must be a real number, not str",
                    span,
                    "TypeError"
                );
            }

            return new PythonComplexValue(ParseComplex(text.Value, span));
        }

        var real = RequireComplexComponent("complex", arguments[0], span);
        var imaginary =
            arguments.Count == 2
                ? RequireComplexComponent("complex", arguments[1], span)
                : System.Numerics.Complex.Zero;
        // complex(a, b) == a + b*1j, so an imaginary `b` rotates into the real part.
        return new PythonComplexValue(real + imaginary * System.Numerics.Complex.ImaginaryOne);
    }

    private static Complex RequireComplexComponent(string name, PythonValue value, TextSpan span)
    {
        if (UserObjectProtocols.TryConvertToFloat(value, span, out var userFloat))
        {
            return new Complex(userFloat, 0);
        }

        return value switch
        {
            PythonComplexValue complex => complex.Value,
            PythonFloatingPointValue floating => new Complex(floating.Value, 0),
            PythonWholeNumberValue whole => new Complex((double)whole.Value, 0),
            PythonTruthValue truth => new Complex(truth.Value ? 1 : 0, 0),
            _ => throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{name}() argument must be a string or a number, not '{ManagedObjectProtocols.GetTypeName(value)}'",
                span,
                "TypeError"
            ),
        };
    }

    private static Complex ParseComplex(string text, TextSpan span)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('(') && trimmed.EndsWith(')'))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        var malformed = ManagedObjectProtocols.Fault(
            "DPY4003",
            "complex() arg is a malformed string",
            span,
            "ValueError"
        );
        if (trimmed.Length == 0 || trimmed.Contains(' ', StringComparison.Ordinal))
        {
            throw malformed;
        }

        if (!trimmed.EndsWith('j') && !trimmed.EndsWith('J'))
        {
            return TryParseDouble(trimmed, out var realOnly)
                ? new Complex(realOnly, 0)
                : throw malformed;
        }

        var body = trimmed[..^1];
        // Split at the last sign that is not a leading sign or an exponent sign.
        var split = -1;
        for (var index = body.Length - 1; index > 0; index--)
        {
            if (body[index] is '+' or '-' && body[index - 1] is not ('e' or 'E'))
            {
                split = index;
                break;
            }
        }

        if (split < 0)
        {
            var imaginaryText = body.Length == 0 || body is "+" or "-" ? body + "1" : body;
            return TryParseDouble(imaginaryText, out var imaginaryOnly)
                ? new Complex(0, imaginaryOnly)
                : throw malformed;
        }

        var imaginaryPart = body[split..];
        if (imaginaryPart is "+" or "-")
        {
            imaginaryPart += "1";
        }

        return
            TryParseDouble(body[..split], out var real)
            && TryParseDouble(imaginaryPart, out var imaginary)
            ? new Complex(real, imaginary)
            : throw malformed;
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // ----------------------------------------------------------------------------
    // property / staticmethod / classmethod
    // ----------------------------------------------------------------------------

    private static PythonPropertyValue Property(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        RequireArgumentCount("property", arguments, 0, 4, span);
        return new PythonPropertyValue(
            OptionalCallable(arguments, 0),
            OptionalCallable(arguments, 1),
            OptionalCallable(arguments, 2)
        );
    }

    private static PythonValue? OptionalCallable(IReadOnlyList<PythonValue> arguments, int index) =>
        index < arguments.Count && arguments[index] is not PythonNoneValue
            ? arguments[index]
            : null;

    private static PythonStaticMethodValue StaticMethod(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        RequireArgumentCount("staticmethod", arguments, 1, 1, span);
        return new PythonStaticMethodValue(arguments[0]);
    }

    private static PythonClassMethodValue ClassMethod(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        RequireArgumentCount("classmethod", arguments, 1, 1, span);
        return new PythonClassMethodValue(arguments[0]);
    }

    /// <summary>`property.getter/setter/deleter` and `fget/fset/fdel` attribute access.</summary>
    internal static PythonValue GetPropertyAttribute(
        PythonPropertyValue property,
        string name,
        TextSpan span
    ) =>
        name switch
        {
            "fget" => property.Getter ?? PythonNoneValue.Instance,
            "fset" => property.Setter ?? PythonNoneValue.Instance,
            "fdel" => property.Deleter ?? PythonNoneValue.Instance,
            "getter" => PropertyRebinder("getter", callable => property with { Getter = callable }),
            "setter" => PropertyRebinder("setter", callable => property with { Setter = callable }),
            "deleter" => PropertyRebinder(
                "deleter",
                callable => property with { Deleter = callable }
            ),
            _ => throw ManagedObjectProtocols.Fault(
                "DPY4023",
                $"'property' object has no attribute '{name}'",
                span,
                "AttributeError"
            ),
        };

    private static PythonBuiltinFunctionValue PropertyRebinder(
        string name,
        Func<PythonValue, PythonPropertyValue> rebind
    ) =>
        new(
            name,
            (arguments, span) =>
            {
                RequireArgumentCount(name, arguments, 1, 1, span);
                return rebind(arguments[0]);
            }
        );

    // ----------------------------------------------------------------------------
    // reversed
    // ----------------------------------------------------------------------------

    private static PythonValue Reversed(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        RequireArgumentCount("reversed", arguments, 1, 1, span);
        return PythonReverseIterators.Create(arguments[0], span);
    }

    // ----------------------------------------------------------------------------
    // object
    // ----------------------------------------------------------------------------

    private static PythonManagedObjectValue ConstructObject(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count != 0)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "object() takes no arguments",
                span,
                "TypeError"
            );
        }

        return new PythonManagedObjectValue(ObjectType);
    }

    /// <summary>
    /// `object.__init__`, `object.__new__`, `object.__setattr__`, … — the default
    /// protocol that user classes reach through `object.X(self, …)` or `super().X(…)`.
    /// </summary>
    internal static bool TryGetObjectProtocol(string name, out PythonValue value)
    {
        value = name switch
        {
            "__init__" => Descriptor(
                "__init__",
                (self, arguments, span) =>
                {
                    if (arguments.Count > 0)
                    {
                        throw ManagedObjectProtocols.Fault(
                            "DPY4003",
                            "object.__init__() takes exactly one argument (the instance to initialize)",
                            span,
                            "TypeError"
                        );
                    }

                    return PythonNoneValue.Instance;
                }
            ),
            "__new__" => Descriptor(
                "__new__",
                (self, _, span) =>
                    self is PythonManagedTypeValue type
                        ? new PythonManagedObjectValue(type)
                        : throw ManagedObjectProtocols.Fault(
                            "DPY4003",
                            "object.__new__(X): X is not a type object",
                            span,
                            "TypeError"
                        )
            ),
            "__setattr__" => Descriptor(
                "__setattr__",
                (self, arguments, span) =>
                {
                    var (instance, attribute) = RequireInstanceAndName(
                        "__setattr__",
                        self,
                        arguments,
                        2,
                        span
                    );
                    ManagedObjectProtocols.SetInstanceAttribute(
                        instance,
                        attribute,
                        arguments[1],
                        span
                    );
                    return PythonNoneValue.Instance;
                }
            ),
            "__getattribute__" => Descriptor(
                "__getattribute__",
                (self, arguments, span) =>
                {
                    var (instance, attribute) = RequireInstanceAndName(
                        "__getattribute__",
                        self,
                        arguments,
                        1,
                        span
                    );
                    return ManagedObjectProtocols.GetInstanceAttribute(instance, attribute, span);
                }
            ),
            "__delattr__" => Descriptor(
                "__delattr__",
                (self, arguments, span) =>
                {
                    var (instance, attribute) = RequireInstanceAndName(
                        "__delattr__",
                        self,
                        arguments,
                        1,
                        span
                    );
                    ManagedObjectProtocols.DeleteInstanceAttribute(instance, attribute, span);
                    return PythonNoneValue.Instance;
                }
            ),
            "__eq__" => Descriptor(
                "__eq__",
                (self, arguments, span) =>
                {
                    RequireArgumentCount("__eq__", arguments, 1, 1, span);
                    return ReferenceEquals(self, arguments[0])
                        ? PythonTruthValue.True
                        : PythonNotImplementedValue.Instance;
                }
            ),
            "__ne__" => Descriptor(
                "__ne__",
                (self, arguments, span) =>
                {
                    RequireArgumentCount("__ne__", arguments, 1, 1, span);
                    return ReferenceEquals(self, arguments[0])
                        ? PythonTruthValue.False
                        : PythonNotImplementedValue.Instance;
                }
            ),
            "__hash__" => Descriptor(
                "__hash__",
                (self, arguments, span) =>
                {
                    RequireArgumentCount("__hash__", arguments, 0, 0, span);
                    return PythonWholeNumberValue.Create(
                        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(self)
                    );
                }
            ),
            "__repr__" => Descriptor(
                "__repr__",
                (self, arguments, span) =>
                {
                    RequireArgumentCount("__repr__", arguments, 0, 0, span);
                    return new PythonTextValue(
                        self is PythonManagedObjectValue instance
                            ? instance.DefaultRepresentation
                            : self.ToRepresentationString()
                    );
                }
            ),
            "__str__" => Descriptor(
                "__str__",
                (self, arguments, span) =>
                {
                    RequireArgumentCount("__str__", arguments, 0, 0, span);
                    return new PythonTextValue(self.ToRepresentationString());
                }
            ),
            _ => null!,
        };
        return value is not null;
    }

    /// <summary>
    /// A method descriptor: bound through an instance (`self.__setattr__(...)`) the target
    /// is the instance; called through the class (`object.__setattr__(self, ...)`) the
    /// first positional argument is.
    /// </summary>
    private static PythonProtocolFunctionValue Descriptor(
        string name,
        Func<PythonValue, IReadOnlyList<PythonValue>, TextSpan, PythonValue> implementation
    ) =>
        new(
            name,
            (target, arguments) =>
            {
                if (target is not null)
                {
                    return implementation(target, arguments, default);
                }

                if (arguments.Count == 0)
                {
                    throw ManagedObjectProtocols.Fault(
                        "DPY4003",
                        $"descriptor '{name}' of 'object' object needs an argument",
                        default,
                        "TypeError"
                    );
                }

                return implementation(arguments[0], [.. arguments.Skip(1)], default);
            }
        );

    private static (PythonManagedObjectValue Instance, string Name) RequireInstanceAndName(
        string method,
        PythonValue self,
        IReadOnlyList<PythonValue> arguments,
        int expectedCount,
        TextSpan span
    )
    {
        if (arguments.Count != expectedCount)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"expected {expectedCount} argument{(expectedCount == 1 ? "" : "s")}, got {arguments.Count}",
                span,
                "TypeError"
            );
        }

        if (self is not PythonManagedObjectValue instance)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"descriptor '{method}' requires a managed class instance but received '{ManagedObjectProtocols.GetTypeName(self)}'",
                span,
                "TypeError"
            );
        }

        if (arguments[0] is not PythonTextValue name)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"attribute name must be string, not '{ManagedObjectProtocols.GetTypeName(arguments[0])}'",
                span,
                "TypeError"
            );
        }

        return (instance, name.Value);
    }

    internal static void RequireArgumentCount(
        string name,
        IReadOnlyList<PythonValue> arguments,
        int minimum,
        int maximum,
        TextSpan span
    )
    {
        if (arguments.Count >= minimum && arguments.Count <= maximum)
        {
            return;
        }

        var message =
            minimum == maximum
                ? $"{name}() takes exactly {Plural(minimum, "argument")} ({arguments.Count} given)"
            : arguments.Count < minimum
                ? $"{name}() takes at least {Plural(minimum, "argument")} ({arguments.Count} given)"
            : $"{name}() takes at most {Plural(maximum, "argument")} ({arguments.Count} given)";
        throw ManagedObjectProtocols.Fault("DPY4003", message, span, "TypeError");
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"one {noun}" : $"{count} {noun}s";
}
