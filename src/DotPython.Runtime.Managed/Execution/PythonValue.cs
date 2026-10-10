using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal abstract record PythonValue
{
    internal abstract string ToDisplayString();

    internal virtual string ToRepresentationString() => ToDisplayString();
}

/// <summary>`dict.fromkeys` and its siblings: a builtin function a type object handed out.</summary>
internal static class PythonBoundDisplay
{
    internal static string Of(string name, PythonValue bound) =>
        $"<built-in method {name} of {QualifiedTypeName(bound)} object at "
        + $"0x{RuntimeHelpers.GetHashCode(bound):x}>";

    /// <summary>The name of a value's type, qualified when the type lives in a module.</summary>
    internal static string QualifiedTypeName(PythonValue value) =>
        PythonBuiltinTypes.GetRuntimeType(value) is PythonBuiltinTypeValue type
            ? type.QualifiedName
            : PythonBuiltinTypes.GetRuntimeTypeName(value);
}

internal sealed record PythonNoneValue : PythonValue
{
    internal static PythonNoneValue Instance { get; } = new();

    private PythonNoneValue() { }

    internal override string ToDisplayString() => "None";
}

internal sealed record PythonEllipsisValue : PythonValue
{
    internal static PythonEllipsisValue Instance { get; } = new();

    private PythonEllipsisValue() { }

    internal override string ToDisplayString() => "Ellipsis";
}

internal sealed record PythonNotImplementedValue : PythonValue
{
    internal static PythonNotImplementedValue Instance { get; } = new();

    private PythonNotImplementedValue() { }

    internal override string ToDisplayString() => "NotImplemented";
}

internal sealed record PythonTruthValue : PythonValue
{
    internal static PythonTruthValue False { get; } = new(false);

    internal static PythonTruthValue True { get; } = new(true);

    private PythonTruthValue(bool value)
    {
        Value = value;
    }

    internal bool Value { get; }

    internal static PythonTruthValue FromBoolean(bool value) => value ? True : False;

    internal override string ToDisplayString() => Value ? "True" : "False";
}

internal sealed record PythonWholeNumberValue(BigInteger Value) : PythonValue
{
    private const int LargestCachedValue = 256;
    private const int SmallestCachedValue = -5;
    private static readonly PythonWholeNumberValue[] CachedValues = CreateCachedValues();

    internal static PythonWholeNumberValue Create(BigInteger value)
    {
        if (value >= SmallestCachedValue && value <= LargestCachedValue)
        {
            return CachedValues[(int)value - SmallestCachedValue];
        }

        return new PythonWholeNumberValue(value);
    }

    internal override string ToDisplayString() => Value.ToString(CultureInfo.InvariantCulture);

    private static PythonWholeNumberValue[] CreateCachedValues()
    {
        var values = new PythonWholeNumberValue[LargestCachedValue - SmallestCachedValue + 1];
        for (var value = SmallestCachedValue; value <= LargestCachedValue; value++)
        {
            values[value - SmallestCachedValue] = new PythonWholeNumberValue(value);
        }

        return values;
    }
}

internal sealed record PythonFloatingPointValue(double Value) : PythonValue
{
    internal override string ToDisplayString()
    {
        if (double.IsNaN(Value))
        {
            return "nan";
        }

        if (double.IsPositiveInfinity(Value))
        {
            return "inf";
        }

        if (double.IsNegativeInfinity(Value))
        {
            return "-inf";
        }

        var text = ShortestRoundTrip(Value);
        return
            text.Contains('.', StringComparison.Ordinal)
            || text.Contains('e', StringComparison.Ordinal)
            ? text
            : $"{text}.0";
    }

    /// <summary>
    /// CPython's `repr` switch to scientific notation is one decade earlier than the
    /// round-trip format's: it goes scientific once the decimal point lands past the
    /// sixteenth digit (`decpt > 16`), so the whole `[1e16, 1e17)` band is respelled here.
    /// </summary>
    private static string ShortestRoundTrip(double value)
    {
        var text = value
            .ToString("R", CultureInfo.InvariantCulture)
            .Replace("E", "e", StringComparison.Ordinal);
        if (text.Contains('e', StringComparison.Ordinal))
            return text;

        var negative = text.StartsWith('-');
        var unsigned = negative ? text[1..] : text;
        var point = unsigned.IndexOf('.', StringComparison.Ordinal);
        var decpt = point < 0 ? unsigned.Length : point;
        if (decpt <= 16)
            return text;

        var digits = unsigned.Replace(".", string.Empty, StringComparison.Ordinal).TrimEnd('0');
        var mantissa = digits.Length == 1 ? digits : $"{digits[..1]}.{digits[1..]}";
        return $"{(negative ? "-" : string.Empty)}{mantissa}e+{decpt - 1:00}";
    }
}

internal sealed record PythonComplexValue(Complex Value) : PythonValue
{
    internal override string ToDisplayString()
    {
        var real = FormatComponent(Value.Real);
        var imaginary = FormatComponent(Math.Abs(Value.Imaginary));
        var sign = Value.Imaginary < 0 ? "-" : "+";

        if (Value.Real == 0)
        {
            return $"{(Value.Imaginary < 0 ? "-" : string.Empty)}{imaginary}j";
        }

        return $"({real}{sign}{imaginary}j)";
    }

    private static string FormatComponent(double value)
    {
        var text = new PythonFloatingPointValue(value).ToDisplayString();
        return text.EndsWith(".0", StringComparison.Ordinal) ? text[..^2] : text;
    }
}

internal sealed record PythonTextValue(string Value) : PythonValue
{
    internal override string ToDisplayString() => Value;

    internal override string ToRepresentationString()
    {
        var delimiter =
            Value.Contains('\'', StringComparison.Ordinal)
            && !Value.Contains('"', StringComparison.Ordinal)
                ? '"'
                : '\'';
        var builder = new StringBuilder().Append(delimiter);
        foreach (var rune in PythonTextTraversal.Enumerate(Value))
        {
            switch (rune.Value)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case var value when value == delimiter:
                    builder.Append('\\').Append((char)value);
                    break;
                case var value when IsPythonPrintable(rune):
                    builder.Append(rune.ToString());
                    break;
                case <= byte.MaxValue:
                    builder.Append(CultureInfo.InvariantCulture, $"\\x{rune.Value:x2}");
                    break;
                case <= char.MaxValue:
                    builder.Append(CultureInfo.InvariantCulture, $"\\u{rune.Value:x4}");
                    break;
                default:
                    builder.Append(CultureInfo.InvariantCulture, $"\\U{rune.Value:x8}");
                    break;
            }
        }

        return builder.Append(delimiter).ToString();
    }

    private static bool IsPythonPrintable(PythonTextTraversal.Character rune)
    {
        if (rune.Value == ' ')
        {
            return true;
        }

        return rune.Category
            is not (
                UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.SpaceSeparator
            );
    }
}

internal sealed record PythonByteSequenceValue(byte[] Value) : PythonValue
{
    internal static readonly PythonByteSequenceValue Empty = new([]);
    private static readonly PythonByteSequenceValue[] Singletons = Enumerable
        .Range(0, 256)
        .Select(value => new PythonByteSequenceValue([(byte)value]))
        .ToArray();

    internal static PythonByteSequenceValue Create(byte[] value) =>
        value.Length switch
        {
            0 => Empty,
            1 => Singletons[value[0]],
            _ => new PythonByteSequenceValue(value),
        };

    internal override string ToDisplayString() => PythonBytesText.Represent(Value);

    public bool Equals(PythonByteSequenceValue? other) =>
        other is not null && Value.AsSpan().SequenceEqual(other.Value);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Value)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

/// <summary>A builtin call with keyword arguments (positional, names, values, span).</summary>
internal delegate PythonValue BuiltinKeywordInvoker(
    IReadOnlyList<PythonValue> positional,
    IReadOnlyList<string> keywordNames,
    IReadOnlyList<PythonValue> keywordValues,
    TextSpan span
);

/// <summary>A bound builtin method call with keyword arguments.</summary>
internal delegate PythonValue ProtocolKeywordInvoker(
    PythonValue? target,
    IReadOnlyList<PythonValue> positional,
    IReadOnlyList<string> keywordNames,
    IReadOnlyList<PythonValue> keywordValues
);

internal sealed record PythonBuiltinFunctionValue(
    string Name,
    Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> Invoke,
    BuiltinKeywordInvoker? InvokeWithKeywords = null
) : PythonValue
{
    /// <summary>
    /// The type object that handed this function out, when one did: `dict.fromkeys` reports
    /// `&lt;built-in method fromkeys of type object at 0x...&gt;`, a function in a module keeps
    /// reporting `&lt;built-in function fromkeys&gt;`. A classmethod's body reads it, so a
    /// subclass the method was reached through can replace it where the function is handed
    /// out — each access builds its own function, and only that one is rewritten.
    /// </summary>
    internal PythonValue? BoundTo { get; set; }

    internal override string ToDisplayString() =>
        BoundTo is null ? $"<built-in function {Name}>" : PythonBoundDisplay.Of(Name, BoundTo);

    /// <summary>Declares the parameter names so keyword calls bind onto the positional form.</summary>
    internal PythonBuiltinFunctionValue WithSignature(
        string[] parameters,
        PythonValue?[] defaults,
        int positionalOnly = 0,
        bool typeStyleErrors = false
    ) =>
        this with
        {
            InvokeWithKeywords = PythonKeywordArguments.Adapt(
                Name,
                parameters,
                defaults,
                Invoke,
                positionalOnly,
                typeStyleErrors
            ),
        };
}

internal sealed record PythonBuiltinTypeValue(
    string Name,
    Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> Construct,
    BuiltinKeywordInvoker? ConstructWithKeywords = null
) : PythonValue
{
    internal string ModuleName { get; init; } = "builtins";

    /// <summary>
    /// The name CPython reports the type under: a type defined in a module is qualified by
    /// it, `collections.deque`, while a builtin keeps its bare name.
    /// </summary>
    internal string QualifiedName => ModuleName == "builtins" ? Name : $"{ModuleName}.{Name}";

    internal PythonTupleValue? MatchArguments { get; init; }

    internal override string ToDisplayString() =>
        $"<class '{(ModuleName == "builtins" ? Name : ModuleName + "." + Name)}'>";
}

/// <summary>
/// A PEP 604 union of type objects (`int | str`): the members in source order with
/// duplicates removed. <see cref="Combine"/> flattens nested unions and collapses a single
/// surviving member back to that member, so `int | int` is `int`. `None` contributes
/// `NoneType`, matching `type.__or__`.
/// </summary>
/// <summary>
/// A parameterized generic such as `list[int]`, produced by subscripting a builtin
/// container type. It is a value in its own right: it renders, compares and hashes by
/// its origin and arguments, calls through to its origin, and is refused by
/// `isinstance` and `issubclass` the way CPython refuses a parameterized generic.
/// </summary>
internal sealed record PythonGenericAliasValue : PythonValue
{
    private PythonGenericAliasValue(PythonValue origin, IReadOnlyList<PythonValue> arguments)
    {
        Origin = origin;
        Arguments = arguments;
        _displayString = Render(origin, arguments);
    }

    /// <summary>The unsubscripted type.</summary>
    internal PythonValue Origin { get; }

    /// <summary>The subscription arguments, in source order.</summary>
    internal IReadOnlyList<PythonValue> Arguments { get; }

    private readonly string _displayString;

    internal override string ToDisplayString() => _displayString;

    internal override string ToRepresentationString() => _displayString;

    /// <summary>
    /// Builds the alias for `origin[index]`. A tuple index supplies one argument per
    /// item, so `list[()]` parameterizes with nothing and `list[int, str]` with two.
    /// </summary>
    internal static PythonGenericAliasValue Create(PythonValue origin, PythonValue index)
    {
        PythonValue[] arguments = index is PythonTupleValue tuple ? [.. tuple.Elements] : [index];
        return new PythonGenericAliasValue(origin, arguments);
    }

    /// <summary>`list[int]`, and `tuple[()]` when the alias carries no arguments.</summary>
    private static string Render(PythonValue origin, IReadOnlyList<PythonValue> arguments)
    {
        var name = origin switch
        {
            PythonBuiltinTypeValue builtin => builtin.Name,
            PythonManagedTypeValue managed => managed.Name,
            _ => origin.ToDisplayString(),
        };
        return arguments.Count == 0
            ? $"{name}[()]"
            : $"{name}[{string.Join(", ", arguments.Select(RenderArgument))}]";
    }

    /// <summary>
    /// One argument as it reads inside the brackets. A type shows its module-qualified
    /// name rather than its `<class ...>` repr, while everything else shows its repr,
    /// so `list[U]` reads `list[__main__.U]` and `list["x"]` reads `list['x']`.
    /// </summary>
    private static string RenderArgument(PythonValue argument) =>
        argument switch
        {
            PythonBuiltinTypeValue type => type.Name,
            PythonManagedTypeValue type => QualifyTypeName(type),
            // `tuple[int, ...]` writes the ellipsis as three dots, not `Ellipsis`.
            PythonEllipsisValue => "...",
            // A nested parameter list reads by the same rules, so a callable alias keeps
            // `typing.Callable[[int], str]` rather than spelling out the parameter's repr.
            PythonListValue list => $"[{string.Join(", ", list.Elements.Select(RenderArgument))}]",
            _ => argument.ToRepresentationString(),
        };

    private static string QualifyTypeName(PythonManagedTypeValue type)
    {
        var qualName =
            type.Attributes.TryGetValue("__qualname__", out var declared)
            && declared is PythonTextValue text
                ? text.Value
                : type.Name;
        return
            type.Attributes.TryGetValue("__module__", out var owner)
            && owner is PythonTextValue module
            && module.Value.Length != 0
            && module.Value != "builtins"
            ? $"{module.Value}.{qualName}"
            : qualName;
    }
}

/// <summary>
/// A PEP 695 `type X = value` alias. It names a value rather than being one: it reads
/// as its own name, is not callable, and exposes the value it was defined with.
/// </summary>
internal sealed record PythonTypeAliasValue(string Name, string Module, PythonValue Value)
    : PythonValue
{
    internal override string ToDisplayString() => Name;

    internal override string ToRepresentationString() => Name;
}

internal sealed record PythonTypeUnionValue : PythonValue
{
    private PythonTypeUnionValue(IReadOnlyList<PythonValue> members)
    {
        Members = members;
        _displayString = Render(members);
    }

    /// <summary>The member types, in source order and without duplicates.</summary>
    internal IReadOnlyList<PythonValue> Members { get; }

    private readonly string _displayString;

    internal override string ToDisplayString() => _displayString;

    /// <summary>
    /// Builds `left | right` from operands that are already known to be union members.
    /// Members of nested unions are flattened in, duplicates are dropped by identity, and
    /// a union left holding one member is that member.
    /// </summary>
    internal static PythonValue Combine(PythonValue left, PythonValue right)
    {
        List<PythonValue> members = [];
        Flatten(left, members);
        Flatten(right, members);
        return members.Count == 1 ? members[0] : new PythonTypeUnionValue(members);
    }

    /// <summary>Set equality: `int | str` and `str | int` are the same union.</summary>
    internal bool SetEquals(PythonTypeUnionValue other)
    {
        if (other.Members.Count != Members.Count)
            return false;
        foreach (var member in Members)
        {
            if (!other.Contains(member))
                return false;
        }
        return true;
    }

    public bool Equals(PythonTypeUnionValue? other) => other is not null && SetEquals(other);

    public override int GetHashCode()
    {
        // Member order is not part of a union's identity, so it is not part of its hash either.
        var hash = 0;
        foreach (var member in Members)
            hash ^= RuntimeHelpers.GetHashCode(member);
        return hash;
    }

    private bool Contains(PythonValue member)
    {
        foreach (var candidate in Members)
        {
            if (ReferenceEquals(candidate, member))
                return true;
        }
        return false;
    }

    private static void Flatten(PythonValue value, List<PythonValue> members)
    {
        if (value is PythonTypeUnionValue union)
        {
            foreach (var member in union.Members)
                Flatten(member, members);
            return;
        }

        foreach (var member in members)
        {
            if (ReferenceEquals(member, value))
                return;
        }
        members.Add(value);
    }

    private static string Render(IReadOnlyList<PythonValue> members)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < members.Count; index++)
        {
            if (index != 0)
                builder.Append(" | ");
            AppendMember(builder, members[index]);
        }
        return builder.ToString();
    }

    /// <summary>
    /// CPython names a member by its `__module__`-qualified type name, and renders the
    /// `NoneType` member of `int | None` as `None`, the way the union was written.
    /// </summary>
    private static void AppendMember(StringBuilder builder, PythonValue member)
    {
        if (ReferenceEquals(member, PythonBuiltinTypes.NoneType))
        {
            builder.Append("None");
            return;
        }

        switch (member)
        {
            case PythonManagedTypeValue managed:
                builder.Append(managed.QualifiedDisplayName);
                break;
            case PythonBuiltinTypeValue builtin:
                builder.Append(
                    builtin.ModuleName == "builtins"
                        ? builtin.Name
                        : $"{builtin.ModuleName}.{builtin.Name}"
                );
                break;
            case PythonExceptionTypeValue exception:
                builder.Append(exception.Name);
                break;
            default:
                builder.Append(member.ToDisplayString());
                break;
        }
    }
}

internal interface PythonExternalObjectProtocol
{
    PythonValue Call(IReadOnlyList<PythonValue> arguments, TextSpan span);

    PythonValue CallWithKeywords(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    );

    PythonValue GetAttribute(string name, TextSpan span);

    PythonValue GetItem(PythonValue index, TextSpan span);

    long GetHash(TextSpan span);

    int GetLength(TextSpan span);

    PythonTruthValue RichCompare(PythonValue other, PythonRichComparison comparison, TextSpan span);

    /// <summary>Whether <paramref name="value"/> is an instance of this external type object.</summary>
    bool IsInstanceOf(PythonValue value, TextSpan span);

    string ToDisplayString();

    string ToRepresentationString();
}

internal sealed record PythonExternalObjectValue(PythonExternalObjectProtocol Protocol)
    : PythonValue
{
    internal override string ToDisplayString() => Protocol.ToDisplayString();

    internal override string ToRepresentationString() => Protocol.ToRepresentationString();

    public bool Equals(PythonExternalObjectValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>Private pickle snapshot: reduction is evaluated at dumps, its factory at loads.</summary>
internal sealed record PythonPickleReductionValue(PythonValue Factory) : PythonValue
{
    internal PythonValue[] Arguments { get; set; } = [];

    internal override string ToDisplayString() => "<pickle reduction snapshot>";
}

internal sealed record PythonProtocolFunctionValue(
    string Name,
    Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> Invoke,
    ProtocolKeywordInvoker? InvokeWithKeywords = null
) : PythonValue
{
    internal bool IsTypeMethodDescriptor { get; init; }

    /// <summary>The type object that handed this function out, when one did.</summary>
    internal PythonValue? BoundTo { get; init; }

    /// <summary>
    /// The type whose type object declares this method, for a method the runtime builds
    /// rather than loads: a static type reports it as `&lt;method 'x' of 'mod.T' objects&gt;`
    /// and a slot it fills as `&lt;slot wrapper 'x' of 'mod.T' objects&gt;`, which is how
    /// CPython reports the C methods of `collections`.
    /// </summary>
    internal string? DeclaringType { get; init; }

    /// <summary>
    /// Whether the declaring type fills a slot rather than defining a method, which
    /// CPython reports as a wrapper at every turn.
    /// </summary>
    internal bool IsSlotWrapper { get; init; }

    /// <summary>
    /// Whether the method belongs to a class written in Python rather than a static type:
    /// `Counter.update` is a function, with the repr a `def` carries.
    /// </summary>
    internal bool IsPythonMethod { get; init; }

    internal override string ToDisplayString() =>
        IsTypeMethodDescriptor ? $"<method '{Name}' of 'type' objects>"
        : IsPythonMethod && DeclaringType is { } pythonType
            ? $"<function {pythonType}.{Name} at 0x{RuntimeHelpers.GetHashCode(this):x}>"
        : DeclaringType is { } declaringType
            ? (IsSlotWrapper ? "<slot wrapper '" : "<method '")
                + $"{Name}' of '{declaringType}' objects>"
        : BoundTo is null ? $"<built-in function {Name}>"
        : PythonBoundDisplay.Of(Name, BoundTo);

    /// <summary>Declares the parameter names so keyword calls bind onto the positional form.</summary>
    internal PythonProtocolFunctionValue WithSignature(
        string[] parameters,
        PythonValue?[] defaults,
        int positionalOnly = 0
    ) =>
        this with
        {
            InvokeWithKeywords = PythonKeywordArguments.AdaptMethod(
                Name,
                parameters,
                defaults,
                Invoke,
                positionalOnly
            ),
        };
}

internal sealed record PythonBoundMethodValue(
    string Name,
    PythonValue Target,
    PythonProtocolFunctionValue Function
) : PythonValue
{
    /// <summary>
    /// A slot wrapper rather than a method: CPython binds the two through different objects
    /// and reports them differently — `&lt;method-wrapper '__len__' of list object at 0x...&gt;`
    /// against `&lt;built-in method append of list object at 0x...&gt;`.
    /// </summary>
    internal bool IsWrapper { get; init; }

    /// <summary>
    /// Whether the bound function works on the storage a subclass instance carries rather
    /// than on the instance itself: `dict.get` does, while `object.__getattribute__` does not.
    /// </summary>
    internal bool TargetsStorage { get; init; }

    internal override string ToDisplayString() =>
        Function is { IsPythonMethod: true, DeclaringType: { } pythonType }
            ? $"<bound method {pythonType}.{Name} of {Target.ToRepresentationString()}>"
        : IsWrapper
            ? $"<method-wrapper '{Name}' of {PythonBoundDisplay.QualifiedTypeName(Target)} object at "
                + $"0x{RuntimeHelpers.GetHashCode(Target):x}>"
        : PythonBoundDisplay.Of(Name, Target);
}

internal sealed record PythonTypeMetadataDescriptorValue(string Name) : PythonValue
{
    internal PythonValue Get(PythonValue? instance, PythonValue? owner, TextSpan span) =>
        PythonTypeMetadata.Get(this, instance, owner, span);

    internal void Set(PythonValue instance, PythonValue value, TextSpan span) =>
        PythonTypeMetadata.Set(this, instance, value, span);

    internal void Delete(PythonValue instance, TextSpan span) =>
        PythonTypeMetadata.Delete(this, instance, span);

    internal PythonValue GetAttribute(string name, TextSpan span) =>
        PythonTypeMetadata.GetAttribute(this, name, span);

    internal override string ToDisplayString() =>
        $"<{(Name == "__base__" ? "member" : "attribute")} '{Name}' of 'type' objects>";
}

internal sealed record PythonDescriptorValue(
    string Name,
    Func<PythonValue, PythonValue> Get,
    Action<PythonValue, PythonValue>? Set = null,
    bool IsDataDescriptor = true
) : PythonValue
{
    internal override string ToDisplayString() => $"<descriptor '{Name}'>";
}

/// <summary>A `property` descriptor: getter, setter, and deleter callables.</summary>
internal sealed record PythonPropertyValue(
    PythonValue? Getter,
    PythonValue? Setter,
    PythonValue? Deleter
) : PythonValue
{
    /// <summary>
    /// The member a native type publishes in place of a property: `default_factory` reports
    /// `<member 'default_factory' of 'collections.defaultdict' objects>`, which is what the
    /// C type's own descriptor prints.
    /// </summary>
    internal string? MemberDisplay { get; init; }

    internal override string ToDisplayString()
    {
        if (MemberDisplay is not { } owner)
            return "<property object>";
        var name = Getter is PythonProtocolFunctionValue { Name: var member } ? member : owner;
        return $"<member '{name}' of '{owner}' objects>";
    }

    public bool Equals(PythonPropertyValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>A `staticmethod` wrapper: attribute access yields the function unbound.</summary>
internal sealed record PythonStaticMethodValue(PythonValue Function) : PythonValue
{
    internal override string ToDisplayString() =>
        $"<staticmethod({Function.ToRepresentationString()})>";
}

/// <summary>A `classmethod` wrapper: attribute access binds the function to the class.</summary>
internal sealed record PythonClassMethodValue(PythonValue Function) : PythonValue
{
    internal override string ToDisplayString() =>
        $"<classmethod({Function.ToRepresentationString()})>";
}

internal sealed record PythonManagedTypeValue : PythonValue
{
    internal PythonManagedTypeValue(
        string name,
        PythonManagedTypeValue? baseType = null,
        Func<IReadOnlyList<PythonValue>, PythonValue>? construct = null,
        string? exceptionBaseName = null
    )
        : this(
            name,
            baseType is null ? [] : [baseType],
            baseType?.Mro,
            construct,
            exceptionBaseName
        ) { }

    internal PythonManagedTypeValue(
        string name,
        IReadOnlyList<PythonManagedTypeValue> bases,
        IReadOnlyList<PythonManagedTypeValue>? linearizedBases,
        Func<IReadOnlyList<PythonValue>, PythonValue>? construct = null,
        string? exceptionBaseName = null
    )
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        QualName = name;
        Bases = bases;
        Mro = [this, .. linearizedBases ?? []];
        Construct = construct;
        ExceptionBaseName = exceptionBaseName;
    }

    internal PythonAttributeDictionary Attributes { get; set; } = new();

    internal PythonTypeHierarchy? OwnerHierarchy { get; set; }

    internal PythonValue Metaclass { get; set; } = PythonBuiltinTypes.Type;

    internal bool IsMetaclass { get; set; }

    internal bool IsBuiltinExceptionGroup { get; init; }

    internal bool HasDeclaredSlots { get; set; }

    /// <summary>
    /// The storage instances of this class accept, or null when the class declares no
    /// `__slots__` and therefore allows any attribute name. Types built directly by the
    /// runtime, rather than by class creation, also leave this null.
    /// </summary>
    internal PythonSlotLayout? Slots { get; set; }

    /// <summary>The declared base classes, in source order.</summary>
    internal IReadOnlyList<PythonManagedTypeValue> Bases { get; private set; }

    internal PythonTupleValue? BasesTuple { get; private set; }

    internal IReadOnlyList<PythonValue>? DeclaredBases
    {
        get => BasesTuple?.Elements;
        set
        {
            if (value is null)
            {
                BasesTuple = null;
                Bases = [];
            }
            else
                SetDeclaredBases(new PythonTupleValue([.. value]));
        }
    }

    internal void SetDeclaredBases(PythonTupleValue tuple)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        BasesTuple = tuple;
        Bases = tuple.Elements.OfType<PythonManagedTypeValue>().ToArray();
    }

    internal PythonTupleValue? MroTuple { get; private set; }

    /// <summary>The installed order, including builtin entries and custom metaclass results.</summary>
    internal IReadOnlyList<PythonValue>? ResolutionOrder
    {
        get => MroTuple?.Elements;
        set => SetResolutionOrder(value is null ? null : new PythonTupleValue([.. value]));
    }

    internal void SetResolutionOrder(PythonTupleValue? tuple)
    {
        MroTuple = tuple;
        Mro = tuple?.Elements.OfType<PythonManagedTypeValue>().ToArray() ?? [];
    }

    internal bool IsMroPending { get; set; }

    internal PythonValue? LayoutBase { get; set; }

    /// <summary>The managed entries of the installed method resolution order.</summary>
    internal IReadOnlyList<PythonManagedTypeValue> Mro { get; private set; }

    internal PythonManagedTypeValue? BaseType => Bases.Count == 0 ? null : Bases[0];

    /// <summary>The builtin exception type this class derives from, when it is an exception class.</summary>
    internal string? ExceptionBaseName { get; }

    internal Func<IReadOnlyList<PythonValue>, PythonValue>? Construct { get; }

    internal string Name { get; set; }

    internal string? QualName { get; set; }

    /// <summary>The `__name__` of the defining module (`__module__`); null for runtime-internal types.</summary>
    internal string? Module { get; set; }

    /// <summary>`__module__.__qualname__`, the form CPython prints in reprs.</summary>
    internal string QualifiedDisplayName =>
        Module is null or "builtins" ? Name : $"{Module}.{QualName ?? Name}";

    /// <summary>
    /// Whether refusals and refusal messages name this type the way a static C type does,
    /// with its module in front — `collections.defaultdict` — rather than by the bare name
    /// a heap type carries. Set by the runtime's own C-shaped types; user classes leave it
    /// false, as CPython's heap types report their `__name__` alone.
    /// </summary>
    internal bool ReportsQualifiedName { get; set; }

    /// <summary>The name this type goes by in a message or a refusal.</summary>
    internal string ReportedName => ReportsQualifiedName ? QualifiedDisplayName : Name;

    public bool Equals(PythonManagedTypeValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    internal override string ToDisplayString() =>
        PythonEnum.TryDescribeType(this, out var enumDescription)
            ? enumDescription
            : $"<class '{QualifiedDisplayName}'>";
}

internal sealed record PythonManagedObjectValue : PythonValue
{
    internal PythonManagedObjectValue(PythonManagedTypeValue type, object? payload = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
        Payload = payload;
        // A dictionary storage points back at the instance it belongs to, which is what a
        // miss on `dict[key]` consults for `__missing__`.
        if (payload is PythonDictionaryValue storage)
            storage.Owner = this;
    }

    internal PythonAttributeDictionary Attributes { get; set; } = new();

    internal object? Payload { get; }

    internal PythonManagedTypeValue Type { get; }

    public bool Equals(PythonManagedObjectValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    internal override string ToDisplayString() =>
        UserObjectProtocols.TryFormatDisplay(this) ?? DefaultRepresentation;

    internal override string ToRepresentationString() =>
        UserObjectProtocols.TryFormatRepresentation(this) ?? DefaultRepresentation;

    /// <summary>
    /// CPython's default repr; the address is a stable per-object token rather than a
    /// heap address.
    /// </summary>
    internal string DefaultRepresentation =>
        PythonCodecInfo.InstanceName(this) is { } codecName
            ? $"<codecs.CodecInfo object for encoding {codecName} at 0x{RuntimeHelpers.GetHashCode(this):x}>"
            : $"<{Type.QualifiedDisplayName} object at 0x{RuntimeHelpers.GetHashCode(this):x}>";
}

internal enum PythonStreamKind
{
    StandardOutput,
    StandardError,
    StandardInput,
}

/// <summary>`sys.stdout` / `sys.stderr` / `sys.stdin`, bound to the executing VM's streams.</summary>
internal sealed record PythonStreamValue(PythonStreamKind Kind) : PythonValue
{
    internal string Name =>
        Kind switch
        {
            PythonStreamKind.StandardOutput => "<stdout>",
            PythonStreamKind.StandardError => "<stderr>",
            _ => "<stdin>",
        };

    internal override string ToDisplayString() =>
        $"<_io.TextIOWrapper name='{Name}' mode='{(Kind == PythonStreamKind.StandardInput ? "r" : "w")}' encoding='UTF-8'>";
}

internal sealed record PythonExceptionTypeValue(string Name) : PythonValue
{
    internal override string ToDisplayString() => $"<class '{Name}'>";
}

internal sealed record PythonExceptionValue(string TypeName, string Message) : PythonValue
{
    private readonly string _originalTypeName = TypeName;

    public string TypeName => ManagedType?.Name ?? _originalTypeName;

    /// <summary>The actual managed exception class, retaining inherited builtin protocols.</summary>
    internal PythonManagedTypeValue? ManagedType { get; init; }

    /// <summary>Mutable so `BaseException.__init__` can rebind the message.</summary>
    public string Message { get; set; } = Message;

    /// <summary>Instance attributes assigned by user exception-class `__init__` bodies.</summary>
    internal PythonAttributeDictionary Attributes { get; set; } = new();

    internal bool HasInstanceDictionary { get; set; }

    // Private traceback identity for except* reraising; public traceback objects
    // remain outside the represented exception API.
    internal object? TracebackIdentity { get; set; }

    internal PythonExceptionValue? Cause { get; set; }

    internal PythonExceptionValue? Context { get; set; }

    internal bool SuppressContext { get; set; }

    internal PythonUnicodeErrors.State? UnicodeErrorState { get; set; }

    private IReadOnlyList<PythonExceptionValue>? _groupExceptions;
    private IReadOnlyList<PythonValue>? _groupArguments;
    private PythonTupleValue? _groupExceptionTuple;

    /// <summary>The nested exceptions of an exception group; null for plain exceptions.</summary>
    internal IReadOnlyList<PythonExceptionValue>? GroupExceptions
    {
        get => _groupExceptions;
        init
        {
            _groupExceptions = value;
            _groupArguments = _arguments;
            _groupExceptionTuple = null;
        }
    }

    internal PythonTupleValue GroupExceptionTuple
    {
        get
        {
            if (_groupExceptionTuple is not null)
                return _groupExceptionTuple;
            var exceptions = GroupExceptions!;
            // An exact tuple supplied to the constructor is retained by CPython.
            // Capture constructor args separately: later args writes do not change children.
            if (
                _groupArguments is { Count: 2 }
                && _groupArguments[1] is PythonTupleValue tuple
                && tuple.Elements.Length == exceptions.Count
                && tuple
                    .Elements.Where((item, index) => !ReferenceEquals(item, exceptions[index]))
                    .Any() == false
            )
                return _groupExceptionTuple = tuple;
            return _groupExceptionTuple = new([.. exceptions.Cast<PythonValue>()]);
        }
    }

    /// <summary>
    /// The constructor arguments (`e.args`); null when the value was created from a
    /// bare message, in which case the args derive from <see cref="Message"/>.
    /// </summary>
    private IReadOnlyList<PythonValue>? _arguments;
    private PythonTupleValue? _argumentTuple;
    private IReadOnlyList<PythonValue>? _constructorArguments;

    internal IReadOnlyList<PythonValue>? Arguments
    {
        get => _arguments;
        set
        {
            _arguments = value;
            _argumentTuple = null;
            if (GroupExceptions is not null)
                _groupArguments ??= value;
        }
    }

    internal PythonTupleValue ArgumentTuple => _argumentTuple ??= new([.. EffectiveArguments]);

    internal IReadOnlyList<PythonValue> ConstructorArguments =>
        _constructorArguments ?? EffectiveArguments;

    internal void InitializeSpecializedArguments(IReadOnlyList<PythonValue> arguments) =>
        _constructorArguments = [.. arguments];

    internal void PreserveSpecializedArguments() =>
        _constructorArguments ??= [.. EffectiveArguments];

    internal void AssignArguments(PythonTupleValue arguments)
    {
        // Builtin exception fields such as StopIteration.value are independent of args.
        _constructorArguments ??= EffectiveArguments;
        _arguments = arguments.Elements;
        _argumentTuple = arguments;
    }

    internal IReadOnlyList<PythonValue> EffectiveArguments =>
        Arguments
        ?? (Message.Length == 0 ? Array.Empty<PythonValue>() : [new PythonTextValue(Message)]);

    public bool Equals(PythonExceptionValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    internal override string ToDisplayString()
    {
        if (UnicodeErrorState is not null)
        {
            var dispatcher = UserObjectProtocols.Dispatcher;
            var span = dispatcher?.CurrentSpan ?? default;
            if (
                dispatcher is not null
                && ManagedType is not null
                && ManagedObjectProtocols.TryGetSpecialMethod(this, "__str__", out var method)
            )
            {
                var result = dispatcher.Invoke(method, [], span);
                return result is PythonTextValue text
                    ? text.Value
                    : throw ManagedObjectProtocols.Fault(
                        "DPY4003",
                        $"__str__ returned non-string (type {ManagedObjectProtocols.GetTypeName(result)})",
                        span,
                        "TypeError"
                    );
            }
            return PythonUnicodeErrors.Format(this, span);
        }
        return GroupExceptions is { } group
            ? $"{Message} ({group.Count} sub-exception{(group.Count == 1 ? "" : "s")})"
            : Message;
    }

    internal override string ToRepresentationString()
    {
        if (GroupExceptions is { } group)
        {
            var nested = string.Join(
                ", ",
                group.Select(exception => exception.ToRepresentationString())
            );
            return $"{TypeName}({new PythonTextValue(Message).ToRepresentationString()}, [{nested}])";
        }

        return $"{TypeName}({string.Join(
            ", ",
            EffectiveArguments.Select(argument => argument.ToRepresentationString())
        )})";
    }
}

/// <summary>
/// Threads `except*` handler state through the clause chain on the evaluation stack:
/// the unmatched remainder plus exceptions raised by clause bodies.
/// </summary>
internal sealed record PythonExceptStarStateValue : PythonValue
{
    internal required PythonRaisedException Original { get; init; }

    internal bool AwaitingClause { get; set; }

    internal PythonExceptionValue? Rest { get; set; }

    internal List<PythonExceptionValue> Raised { get; } = [];

    internal override string ToDisplayString() => "<except* state>";
}

internal sealed record PythonFunctionValue(
    string Name,
    PreparedPythonCode Code,
    PythonGlobalNamespace Globals,
    PythonCell[] Closure,
    PythonValue[] Defaults,
    IReadOnlyDictionary<string, PythonValue>? KeywordDefaults = null
) : PythonValue
{
    internal string? QualName { get; init; }

    internal PythonAttributeDictionary Attributes { get; set; } = new();

    /// <summary>
    /// Metadata names (<c>__name__</c>, <c>__qualname__</c>, <c>__module__</c>,
    /// <c>__doc__</c>, <c>__type_params__</c>) installed by
    /// <c>functools.update_wrapper</c> so that a wrapper reports the wrapped
    /// function's identity. CPython writes these to the function's real attribute
    /// slots, so they shadow the built-in values and stay out of <c>__dict__</c>;
    /// this dictionary is the managed equivalent, and null until a wrapper is built.
    /// </summary>
    internal PythonAttributeDictionary? ShadowAttributes { get; set; }

    /// <summary>
    /// The callable that evaluates this function's annotations, or null when it has
    /// none. `__annotations__` calls it once and caches the mapping it returns.
    /// </summary>
    internal PythonValue? Annotate { get; set; }

    /// <summary>The cached `__annotations__` mapping, discarded when `__annotate__` changes.</summary>
    internal PythonDictionaryValue? Annotations { get; set; }

    /// <summary>
    /// The class namespace an annotation body resolves against, or null when the
    /// definition sits outside a class body. Only annotation bodies carry it: an
    /// ordinary method's body must not see class-scope names.
    /// </summary>
    internal PythonValue? ClassNamespace { get; set; }

    internal override string ToDisplayString() =>
        $"<function {QualName ?? Name} at 0x{RuntimeHelpers.GetHashCode(this):x}>";
}

internal sealed record PythonInterpolationValue(
    PythonValue Value,
    string Expression,
    char? Conversion,
    string FormatSpecification
) : PythonValue
{
    public bool Equals(PythonInterpolationValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    internal override string ToDisplayString() =>
        "Interpolation("
        + Value.ToRepresentationString()
        + ", "
        + new PythonTextValue(Expression).ToRepresentationString()
        + ", "
        + (
            Conversion is { } conversion
                ? new PythonTextValue(conversion.ToString()).ToRepresentationString()
                : "None"
        )
        + ", "
        + new PythonTextValue(FormatSpecification).ToRepresentationString()
        + ")";
}

internal sealed record PythonTemplateValue(
    string[] Strings,
    PythonInterpolationValue[] Interpolations
) : PythonValue
{
    internal override string ToDisplayString()
    {
        var strings = new PythonTupleValue([
            .. Strings.Select(text => (PythonValue)new PythonTextValue(text)),
        ]);
        var interpolations = new PythonTupleValue([.. Interpolations.Cast<PythonValue>()]);
        return "Template(strings="
            + strings.ToRepresentationString()
            + ", interpolations="
            + interpolations.ToRepresentationString()
            + ")";
    }

    public bool Equals(PythonTemplateValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

internal enum PythonGeneratorState
{
    Created,
    Suspended,
    Running,
    Completed,
}

internal sealed record PythonGeneratorValue : PythonValue
{
    internal PythonGeneratorValue(
        string name,
        PreparedPythonCode code,
        PythonGlobalNamespace globals,
        PythonCell[] cells,
        PythonValue[] savedLocals
    )
    {
        Name = name;
        Code = code;
        Globals = globals;
        Cells = cells;
        SavedLocals = savedLocals;
    }

    internal string Name { get; }

    internal PreparedPythonCode Code { get; }

    internal PythonGlobalNamespace Globals { get; }

    internal PythonCell[] Cells { get; }

    internal PythonValue[] SavedLocals { get; }

    /// <summary>Whether this value is a coroutine (from `async def`) rather than a generator.</summary>
    internal bool IsCoroutine { get; init; }

    /// <summary>Whether this value is an async generator (`async def` containing `yield`).</summary>
    internal bool IsAsyncGenerator { get; init; }

    /// <summary>The `generator` / `coroutine` / `async_generator` type name used in messages.</summary>
    internal string TypeName =>
        IsAsyncGenerator ? "async_generator"
        : IsCoroutine ? "coroutine"
        : "generator";

    internal List<PythonValue> SavedEvaluationStack { get; } = [];

    internal int InstructionPointer { get; set; }

    internal PythonGeneratorState State { get; set; } = PythonGeneratorState.Created;

    internal PythonAsyncGeneratorStepValue? ActiveAsyncStep { get; set; }

    internal PythonValue YieldedValue { get; set; } = PythonNoneValue.Instance;

    /// <summary>
    /// VM-owned frame collections (exception blocks, pending finalies, active
    /// exceptions) carried across suspensions; the concrete type is private to the VM.
    /// </summary>
    internal object? OwnedFrameState { get; set; }

    /// <summary>The generator's `return` value, captured at completion (PEP 380).</summary>
    internal PythonValue ReturnValue { get; set; } = PythonNoneValue.Instance;

    /// <summary>
    /// Resumes the generator: pushes <c>sentValue</c> as the yield expression's result
    /// (or None), or raises <c>injected</c> at the suspension point.
    /// </summary>
    internal Func<
        PythonValue?,
        PythonExceptionValue?,
        (bool HasValue, PythonValue Value)
    >? ResumeCore { get; set; }

    internal (bool HasValue, PythonValue Value) Resume() => ResumeCore!(null, null);

    internal override string ToDisplayString() =>
        $"<{TypeName} object {Name} at 0x{RuntimeHelpers.GetHashCode(this):x}>";

    public bool Equals(PythonGeneratorValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

internal sealed record PythonSuperProxyValue(PythonValue DefiningType, PythonValue Instance)
    : PythonValue
{
    internal override string ToDisplayString() =>
        $"<super: {GetClassName(DefiningType)}, {Instance switch { PythonManagedObjectValue managed => managed.Type.Name, PythonExceptionValue exception => exception.TypeName, _ => "object" }}>";

    private static string GetClassName(PythonValue value) =>
        value switch
        {
            PythonManagedTypeValue type => type.Name,
            PythonBuiltinTypeValue type => type.Name,
            PythonExceptionTypeValue type => type.Name,
            _ => ManagedObjectProtocols.GetTypeName(value),
        };
}

internal sealed record PythonBoundUserMethodValue(
    string Name,
    PythonValue Target,
    PythonFunctionValue Function
) : PythonValue
{
    internal override string ToDisplayString() =>
        $"<bound method {ManagedObjectProtocols.GetTypeName(Target)}.{Name} of "
        + $"{Target.ToRepresentationString()}>";
}

internal sealed record PythonModuleValue(string Name, PythonGlobalNamespace Globals) : PythonValue
{
    internal override string ToDisplayString() => $"<module '{Name}'>";
}

internal sealed record PythonRangeValue(BigInteger Start, BigInteger Stop, BigInteger Step)
    : PythonValue
{
    internal BigInteger Count =>
        Step > 0
            ? (Stop > Start ? (Stop - Start + Step - 1) / Step : 0)
            : (Start > Stop ? (Start - Stop - Step - 1) / (-Step) : 0);

    internal override string ToDisplayString() =>
        Step.IsOne ? $"range({Start}, {Stop})" : $"range({Start}, {Stop}, {Step})";
}

internal sealed record PythonEnumerateSourceValue(PythonIteratorValue Inner, BigInteger StartIndex)
    : PythonValue
{
    internal override string ToDisplayString() => "<enumerate>";
}

internal sealed record PythonZipSourceValue(PythonIteratorValue[] Inners) : PythonValue
{
    /// <summary>`zip(strict=True)`: unequal lengths raise ValueError instead of truncating.</summary>
    internal bool Strict { get; init; }

    internal override string ToDisplayString() => "<zip>";
}

internal sealed record PythonMapSourceValue(
    Func<PythonValue[], (PythonValue Value, PythonExceptionValue? Stop)> Apply,
    PythonIteratorValue[] Inners
) : PythonValue
{
    internal bool Strict { get; init; }

    internal override string ToDisplayString() => "<map>";
}

internal sealed record PythonFilterSourceValue(
    Func<PythonValue, bool> Keep,
    PythonIteratorValue Inner
) : PythonValue
{
    internal override string ToDisplayString() => "<filter>";
}

/// <summary>An index-based sequence cursor with no retained execution context.</summary>
internal sealed record PythonSequenceIteratorSourceValue : PythonValue
{
    internal required PythonManagedObjectValue? Sequence { get; set; }

    internal long NextIndex { get; set; }
    internal int TextOffset { get; set; }

    internal override string ToDisplayString() => "<iterator>";
}

/// <summary>
/// A lazy iteration source retaining the original iterator for throw/close delegation.
/// </summary>
internal sealed record PythonUserIteratorSourceValue(
    Func<(bool HasValue, PythonValue Value)> MoveNext
) : PythonValue
{
    internal PythonValue? OriginalIterator { get; init; }

    internal override string ToDisplayString() => "<iterator>";
}

/// <summary>
/// Marks a value produced by an async generator's own `yield`, distinguishing it
/// from an inner-await suspension passing through the same frame.
/// </summary>
internal sealed record PythonAsyncGeneratorWrappedValue(PythonValue Value) : PythonValue
{
    internal override string ToDisplayString() => Value.ToDisplayString();
}

internal enum PythonAsyncGeneratorStepKind
{
    Next,
    Send,
    Throw,
    Close,
}

/// <summary>
/// The awaitable produced by an async generator's `__anext__`/`asend`/`athrow`/
/// `aclose`: driving it through the delegation loop resumes the generator until it
/// yields a wrapped value (the await's result) or completes (StopAsyncIteration).
/// </summary>
internal sealed record PythonAsyncGeneratorStepValue(
    PythonGeneratorValue Generator,
    PythonAsyncGeneratorStepKind Kind,
    PythonValue? Argument,
    PythonValue? ExceptionArgument
) : PythonValue
{
    internal bool Started { get; set; }

    internal bool Completed { get; set; }

    /// <summary>`anext(agen, default)`: completes with this instead of StopAsyncIteration.</summary>
    internal PythonValue? ExhaustedDefault { get; init; }

    internal override string ToDisplayString() => $"<async_generator_{DisplayKind}>";

    private string DisplayKind =>
        Kind switch
        {
            PythonAsyncGeneratorStepKind.Send => "asend",
            PythonAsyncGeneratorStepKind.Throw => "athrow",
            PythonAsyncGeneratorStepKind.Close => "aclose",
            _ => "asend",
        };

    public bool Equals(PythonAsyncGeneratorStepValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

internal sealed record PythonSliceValue(PythonValue Start, PythonValue Stop, PythonValue Step)
    : PythonValue
{
    internal override string ToDisplayString() =>
        $"slice({Start.ToRepresentationString()}, {Stop.ToRepresentationString()}, "
        + $"{Step.ToRepresentationString()})";
}

internal sealed record PythonMappingProxyValue(PythonValue Mapping) : PythonValue
{
    internal override string ToDisplayString() => Mapping.ToDisplayString();

    internal override string ToRepresentationString()
    {
        if (!PythonRepresentationGuard.TryEnter(this))
            return "mappingproxy({...})";
        try
        {
            return $"mappingproxy({Mapping.ToRepresentationString()})";
        }
        finally
        {
            PythonRepresentationGuard.Exit(this);
        }
    }
}

internal sealed record PythonDictionaryViewValue(string Kind, PythonDictionaryValue Dictionary)
    : PythonValue
{
    /// <summary>
    /// The name the view reports: an ordered dictionary's views are `odict_keys`,
    /// `odict_items` and `odict_values`, while the behaviour behind them is the dictionary's.
    /// </summary>
    internal string DisplayKind =>
        Kind.StartsWith("dict_", StringComparison.Ordinal)
        && PythonOrderedDict.IsOrderedStorage(Dictionary)
            ? "odict_" + Kind["dict_".Length..]
            : Kind;

    internal PythonListValue Snapshot =>
        new([.. Dictionary.Items.Select(item => PythonMappingProxies.ViewItem(item, Kind))]);

    internal override string ToDisplayString()
    {
        if (!PythonRepresentationGuard.TryEnter(this))
            return "...";
        try
        {
            return $"{DisplayKind}({Snapshot.ToDisplayString()})";
        }
        finally
        {
            PythonRepresentationGuard.Exit(this);
        }
    }
}

internal sealed record PythonListValue(List<PythonValue> Elements) : PythonValue
{
    internal bool SortActive { get; set; }
    internal bool SortAllocationObserved { get; set; }

    internal override string ToDisplayString()
    {
        if (!PythonRepresentationGuard.TryEnter(this))
        {
            return "[...]";
        }

        try
        {
            return $"[{string.Join(", ", Elements.Select(element => element.ToRepresentationString()))}]";
        }
        finally
        {
            PythonRepresentationGuard.Exit(this);
        }
    }
}

internal sealed record PythonTupleValue(PythonValue[] Elements) : PythonValue
{
    internal long CachedHash { get; set; } = -1;

    internal override string ToDisplayString()
    {
        if (!PythonRepresentationGuard.TryEnter(this))
        {
            return "(...)";
        }

        try
        {
            return FormatTuple();
        }
        finally
        {
            PythonRepresentationGuard.Exit(this);
        }
    }

    private string FormatTuple()
    {
        if (Elements.Length == 0)
        {
            return "()";
        }

        var contents = string.Join(
            ", ",
            Elements.Select(element => element.ToRepresentationString())
        );
        return Elements.Length == 1 ? $"({contents},)" : $"({contents})";
    }
}

internal sealed class PythonDictionaryItemValue
{
    internal PythonDictionaryItemValue(
        PythonValue key,
        PythonValue value,
        BigInteger? keyHash = null
    )
    {
        Key = key;
        Value = value;
        KeyHash = keyHash ?? ManagedObjectProtocols.ComputePythonHash(key);
    }

    internal PythonValue Key { get; }

    internal BigInteger KeyHash { get; }

    internal PythonValue Value { get; set; }
}

internal sealed record PythonReverseIteratorSourceValue : PythonValue
{
    internal required PythonValue? Sequence { get; set; }
    internal required string TypeName { get; init; }
    internal long NextIndex { get; set; }
    internal int TextOffset { get; set; }

    internal override string ToDisplayString() => "<reverse iterator source>";
}

internal sealed record PythonIteratorValue(PythonValue Iterable, int ExpectedCollectionSize)
    : PythonValue
{
    internal PythonExceptionValue? StopIteration { get; set; }

    internal bool IsExhausted { get; set; }

    // A size mismatch observed by next() remains an error even if the size is restored.
    internal bool IsInvalidated { get; set; }

    internal int Index { get; set; }

    /// <summary>
    /// The mutation count a deque had when this iterator was made: a deque refuses to be
    /// iterated once it changed, even when the change kept its length.
    /// </summary>
    internal int ObservedVersion { get; set; }

    internal int DictionaryPosition { get; set; }

    internal int TextOffset { get; set; }

    // Range cursors can advance beyond the managed collection index limit.
    internal BigInteger RangeIndex { get; set; }

    internal override string ToDisplayString() =>
        $"<{PythonBoundDisplay.QualifiedTypeName(this)} object at "
        + $"0x{RuntimeHelpers.GetHashCode(this):x}>";
}

/// <summary>A read-only text file handle over content snapshotted at open time.</summary>
internal sealed record PythonFileValue(string Name, string Mode, string Content) : PythonValue
{
    internal int Position { get; set; }

    internal bool IsClosed { get; set; }

    /// <summary>Reads up to the next newline (inclusive); null at end of file.</summary>
    internal string? ReadLine()
    {
        if (Position >= Content.Length)
        {
            return null;
        }

        var newlineIndex = Content.IndexOf('\n', Position);
        var end = newlineIndex < 0 ? Content.Length : newlineIndex + 1;
        var line = Content[Position..end];
        Position = end;
        return line;
    }

    /// <summary>Reads at most <paramref name="count"/> characters; negative reads the rest.</summary>
    internal string Read(int count)
    {
        var end =
            count < 0 || Position + count > Content.Length ? Content.Length : Position + count;
        var text = Content[Position..end];
        Position = end;
        return text;
    }

    internal override string ToDisplayString() =>
        $"<_io.TextIOWrapper name='{Name}' mode='{Mode}' encoding='UTF-8'>";
}

internal static class PythonRepresentationGuard
{
    [ThreadStatic]
    private static HashSet<PythonValue>? _activeValues;

    internal static bool TryEnter(PythonValue value)
    {
        _activeValues ??= new HashSet<PythonValue>(ReferenceEqualityComparer.Instance);
        return _activeValues.Add(value);
    }

    internal static void Exit(PythonValue value)
    {
        _activeValues?.Remove(value);
        if (_activeValues?.Count == 0)
        {
            _activeValues = null;
        }
    }
}
