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

internal sealed record PythonNoneValue : PythonValue
{
    internal static PythonNoneValue Instance { get; } = new();

    private PythonNoneValue() { }

    internal override string ToDisplayString() => "None";
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

        var text = Value
            .ToString("R", CultureInfo.InvariantCulture)
            .Replace("E", "e", StringComparison.Ordinal);
        return
            text.Contains('.', StringComparison.Ordinal)
            || text.Contains('e', StringComparison.Ordinal)
            ? text
            : $"{text}.0";
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
        foreach (var rune in Value.EnumerateRunes())
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
                    builder.Append('\\').Append(value);
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

    private static bool IsPythonPrintable(Rune rune)
    {
        if (rune.Value == ' ')
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune)
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
    internal override string ToDisplayString()
    {
        var builder = new StringBuilder("b'");
        foreach (var item in Value)
        {
            switch (item)
            {
                case (byte)'\\':
                    builder.Append("\\\\");
                    break;
                case (byte)'\'':
                    builder.Append("\\'");
                    break;
                case >= 32 and < 127:
                    builder.Append((char)item);
                    break;
                default:
                    builder.Append(CultureInfo.InvariantCulture, $"\\x{item:x2}");
                    break;
            }
        }

        return builder.Append('\'').ToString();
    }

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

internal sealed record PythonBuiltinFunctionValue(
    string Name,
    Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> Invoke
) : PythonValue
{
    internal override string ToDisplayString() => $"<built-in function {Name}>";
}

internal sealed record PythonBuiltinTypeValue(
    string Name,
    Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> Construct
) : PythonValue
{
    internal override string ToDisplayString() => $"<class '{Name}'>";
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

internal sealed record PythonProtocolFunctionValue(
    string Name,
    Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> Invoke
) : PythonValue
{
    internal override string ToDisplayString() => $"<built-in function {Name}>";
}

internal sealed record PythonBoundMethodValue(
    string Name,
    PythonValue Target,
    PythonProtocolFunctionValue Function
) : PythonValue
{
    internal override string ToDisplayString() => $"<bound method {Name}>";
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
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Bases = bases;
        Mro = [this, .. linearizedBases ?? []];
        Construct = construct;
        ExceptionBaseName = exceptionBaseName;
    }

    internal Dictionary<string, PythonValue> Attributes { get; } = new(StringComparer.Ordinal);

    /// <summary>The declared base classes, in source order.</summary>
    internal IReadOnlyList<PythonManagedTypeValue> Bases { get; }

    /// <summary>The C3 method resolution order, starting with this type (no `object` terminus).</summary>
    internal IReadOnlyList<PythonManagedTypeValue> Mro { get; }

    internal PythonManagedTypeValue? BaseType => Bases.Count == 0 ? null : Bases[0];

    /// <summary>The builtin exception type this class derives from, when it is an exception class.</summary>
    internal string? ExceptionBaseName { get; }

    internal Func<IReadOnlyList<PythonValue>, PythonValue>? Construct { get; }

    internal string Name { get; }

    public bool Equals(PythonManagedTypeValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    internal override string ToDisplayString() => $"<class '{Name}'>";
}

internal sealed record PythonManagedObjectValue : PythonValue
{
    internal PythonManagedObjectValue(PythonManagedTypeValue type, object? payload = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
        Payload = payload;
    }

    internal Dictionary<string, PythonValue> Attributes { get; } = new(StringComparer.Ordinal);

    internal object? Payload { get; }

    internal PythonManagedTypeValue Type { get; }

    public bool Equals(PythonManagedObjectValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    internal override string ToDisplayString() => $"<{Type.Name} object>";
}

internal sealed record PythonExceptionTypeValue(string Name) : PythonValue
{
    internal override string ToDisplayString() => $"<class '{Name}'>";
}

internal sealed record PythonExceptionValue(string TypeName, string Message) : PythonValue
{
    /// <summary>Mutable so `BaseException.__init__` can rebind the message.</summary>
    public string Message { get; set; } = Message;

    /// <summary>Instance attributes assigned by user exception-class `__init__` bodies.</summary>
    internal Dictionary<string, PythonValue> Attributes { get; } = new(StringComparer.Ordinal);

    internal PythonExceptionValue? Cause { get; set; }

    internal PythonExceptionValue? Context { get; set; }

    internal bool SuppressContext { get; set; }

    /// <summary>The nested exceptions of an exception group; null for plain exceptions.</summary>
    internal IReadOnlyList<PythonExceptionValue>? GroupExceptions { get; init; }

    /// <summary>
    /// The constructor arguments (`e.args`); null when the value was created from a
    /// bare message, in which case the args derive from <see cref="Message"/>.
    /// </summary>
    internal IReadOnlyList<PythonValue>? Arguments { get; set; }

    internal IReadOnlyList<PythonValue> EffectiveArguments =>
        Arguments
        ?? (Message.Length == 0 ? Array.Empty<PythonValue>() : [new PythonTextValue(Message)]);

    public bool Equals(PythonExceptionValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    internal override string ToDisplayString() =>
        GroupExceptions is { } group
            ? $"{Message} ({group.Count} sub-exception{(group.Count == 1 ? "" : "s")})"
            : Message;

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
    internal override string ToDisplayString() => $"<function {Name}>";
}

internal sealed record PythonInterpolationValue(
    PythonValue Value,
    string Expression,
    char? Conversion,
    string FormatSpecification
) : PythonValue
{
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

    internal override string ToDisplayString() => $"<{TypeName} object {Name}>";

    public bool Equals(PythonGeneratorValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

internal sealed record PythonSuperProxyValue(
    PythonManagedTypeValue DefiningType,
    PythonValue Instance
) : PythonValue
{
    internal override string ToDisplayString() =>
        $"<super: {DefiningType.Name}, {Instance switch { PythonManagedObjectValue managed => managed.Type.Name, PythonExceptionValue exception => exception.TypeName, _ => "object" }}>";
}

internal sealed record PythonBoundUserMethodValue(
    string Name,
    PythonValue Target,
    PythonFunctionValue Function
) : PythonValue
{
    internal override string ToDisplayString() => $"<bound method {Name}>";
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
    internal override string ToDisplayString() => "<zip>";
}

internal sealed record PythonMapSourceValue(
    Func<PythonValue[], PythonValue> Apply,
    PythonIteratorValue[] Inners
) : PythonValue
{
    internal override string ToDisplayString() => "<map>";
}

internal sealed record PythonFilterSourceValue(
    Func<PythonValue, bool> Keep,
    PythonIteratorValue Inner
) : PythonValue
{
    internal override string ToDisplayString() => "<filter>";
}

internal sealed record PythonSetValue(List<PythonValue> Elements) : PythonValue
{
    /// <summary>Whether this value is a `frozenset` (immutable, hashable) rather than a `set`.</summary>
    internal bool IsFrozen { get; init; }

    internal override string ToDisplayString()
    {
        if (Elements.Count == 0)
        {
            return IsFrozen ? "frozenset()" : "set()";
        }

        if (!PythonRepresentationGuard.TryEnter(this))
        {
            return IsFrozen ? "frozenset({...})" : "{...}";
        }

        try
        {
            var elements =
                "{"
                + string.Join(", ", Elements.Select(element => element.ToRepresentationString()))
                + "}";
            return IsFrozen ? $"frozenset({elements})" : elements;
        }
        finally
        {
            PythonRepresentationGuard.Exit(this);
        }
    }
}

/// <summary>
/// A lazy iteration source over a user-defined iterator: `MoveNext` captures the VM
/// and the bound `__next__`, converting a raised StopIteration into exhaustion.
/// </summary>
internal sealed record PythonUserIteratorSourceValue(
    Func<(bool HasValue, PythonValue Value)> MoveNext
) : PythonValue
{
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
    PythonExceptionValue? Injected
) : PythonValue
{
    internal bool Started { get; set; }

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

internal sealed record PythonDictionaryViewValue(string Kind, PythonListValue Snapshot)
    : PythonValue
{
    internal override string ToDisplayString() => $"{Kind}({Snapshot.ToDisplayString()})";
}

internal sealed record PythonListValue(List<PythonValue> Elements) : PythonValue
{
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
    internal PythonDictionaryItemValue(PythonValue key, PythonValue value)
    {
        Key = key;
        Value = value;
    }

    internal PythonValue Key { get; }

    internal PythonValue Value { get; set; }
}

internal sealed record PythonDictionaryValue(List<PythonDictionaryItemValue> Items) : PythonValue
{
    internal int SizeVersion { get; set; }

    internal override string ToDisplayString()
    {
        if (!PythonRepresentationGuard.TryEnter(this))
        {
            return "{...}";
        }

        try
        {
            return $"{{{string.Join(", ", Items.Select(item => $"{item.Key.ToRepresentationString()}: {item.Value.ToRepresentationString()}"))}}}";
        }
        finally
        {
            PythonRepresentationGuard.Exit(this);
        }
    }
}

internal sealed record PythonIteratorValue(PythonValue Iterable, int ExpectedDictionarySizeVersion)
    : PythonValue
{
    internal int Index { get; set; }

    internal override string ToDisplayString() => "<collection_iterator>";
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
