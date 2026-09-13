using System.Runtime.CompilerServices;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>A writable member descriptor backed by a Unicode error's typed state.</summary>
internal sealed record PythonUnicodeErrorDescriptorValue(string Owner, string Name) : PythonValue
{
    internal PythonValue Get(PythonValue? instance, PythonValue? owner, TextSpan span)
    {
        if (instance is null or PythonNoneValue)
        {
            if (owner is null or PythonNoneValue)
                throw Error("__get__(None, None) is invalid", span);
            return this;
        }
        var exception = RequireInstance(instance, span);
        PythonUnicodeErrors.TryGet(exception, Name, out var value);
        return value;
    }

    internal void Set(PythonValue instance, PythonValue value, TextSpan span) =>
        PythonUnicodeErrors.TrySet(RequireInstance(instance, span), Name, value, span);

    internal void Delete(PythonValue instance, TextSpan span) =>
        PythonUnicodeErrors.TryDelete(RequireInstance(instance, span), Name, span);

    internal PythonValue GetAttribute(string name, TextSpan span) =>
        name switch
        {
            "__name__" => new PythonTextValue(Name),
            "__qualname__" => new PythonTextValue($"{Owner}.{Name}"),
            "__objclass__" => PythonBuiltinTypes.GetExceptionType(Owner),
            "__doc__" => new PythonTextValue($"exception {Name}"),
            "__get__" or "__set__" or "__delete__" => new PythonBuiltinFunctionValue(
                name,
                (arguments, callSpan) => Invoke(name, arguments, callSpan),
                (arguments, names, _, callSpan) =>
                    names.Count == 0
                        ? Invoke(name, arguments, callSpan)
                        : throw Error($"wrapper {name}() takes no keyword arguments", callSpan)
            ),
            _ => throw ManagedObjectProtocols.Fault(
                "DPY4023",
                $"'member_descriptor' object has no attribute '{name}'",
                span,
                "AttributeError"
            ),
        };

    private PythonValue Invoke(string method, IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var minimum = method == "__set__" ? 2 : 1;
        var maximum = method == "__delete__" ? 1 : 2;
        if (arguments.Count < minimum || arguments.Count > maximum)
        {
            var expected =
                minimum == maximum
                    ? minimum.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : arguments.Count < minimum ? $"at least {minimum}"
                : $"at most {maximum}";
            var count = arguments.Count < minimum ? minimum : maximum;
            var prefix = method == "__delete__" ? string.Empty : method + " ";
            throw Error(
                $"{prefix}expected {expected} argument{(count == 1 ? "" : "s")}, got {arguments.Count}",
                span
            );
        }
        if (method == "__get__")
            return Get(arguments[0], arguments.Count == 2 ? arguments[1] : null, span);
        if (method == "__set__")
            Set(arguments[0], arguments[1], span);
        else
            Delete(arguments[0], span);
        return PythonNoneValue.Instance;
    }

    private PythonExceptionValue RequireInstance(PythonValue value, TextSpan span) =>
        value is PythonExceptionValue exception
        && PythonUnicodeErrors.IsApplicable(exception)
        && PythonUnicodeErrors.IsEncode(exception) == (Owner == "UnicodeEncodeError")
            ? exception
            : throw Error(
                $"descriptor '{Name}' for '{Owner}' objects doesn't apply to a '{ManagedObjectProtocols.GetTypeName(value)}' object",
                span
            );

    private static PythonRuntimeException Error(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, "TypeError");

    public bool Equals(PythonUnicodeErrorDescriptorValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    internal override string ToDisplayString() => $"<member '{Name}' of '{Owner}' objects>";
}
