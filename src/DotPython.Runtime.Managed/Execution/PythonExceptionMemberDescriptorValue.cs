using System.Runtime.CompilerServices;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>The slots a member descriptor reads and writes.</summary>
internal enum PythonExceptionMemberFamily
{
    OSError,
    SyntaxError,
}

/// <summary>A writable member descriptor backed by the slots of one exception family.</summary>
internal sealed record PythonExceptionMemberDescriptorValue(
    string Owner,
    string Name,
    PythonExceptionMemberFamily Family
) : PythonValue
{
    /// <summary>Each member's own docstring, as CPython's member tables declare it.</summary>
    private static readonly Dictionary<(PythonExceptionMemberFamily, string), string> DocStrings =
        new()
        {
            [(PythonExceptionMemberFamily.OSError, "errno")] = "POSIX exception code",
            [(PythonExceptionMemberFamily.OSError, "strerror")] = "exception strerror",
            [(PythonExceptionMemberFamily.OSError, "filename")] = "exception filename",
            [(PythonExceptionMemberFamily.OSError, "filename2")] = "second exception filename",
            [(PythonExceptionMemberFamily.SyntaxError, "msg")] = "exception msg",
            [(PythonExceptionMemberFamily.SyntaxError, "filename")] = "exception filename",
            [(PythonExceptionMemberFamily.SyntaxError, "lineno")] = "exception lineno",
            [(PythonExceptionMemberFamily.SyntaxError, "offset")] = "exception offset",
            [(PythonExceptionMemberFamily.SyntaxError, "text")] = "exception text",
            [(PythonExceptionMemberFamily.SyntaxError, "end_lineno")] = "exception end lineno",
            [(PythonExceptionMemberFamily.SyntaxError, "end_offset")] = "exception end offset",
            [(PythonExceptionMemberFamily.SyntaxError, "print_file_and_line")] =
                "exception print_file_and_line",
        };

    private static readonly Dictionary<PythonExceptionMemberFamily, string> FamilyNames = new()
    {
        [PythonExceptionMemberFamily.OSError] = "OSError",
        [PythonExceptionMemberFamily.SyntaxError] = "SyntaxError",
    };

    internal PythonValue Get(PythonValue? instance, PythonValue? owner, TextSpan span)
    {
        if (instance is null or PythonNoneValue)
        {
            if (owner is null or PythonNoneValue)
                throw Error("__get__(None, None) is invalid", span);
            return this;
        }
        return Family switch
        {
            PythonExceptionMemberFamily.OSError => ManagedObjectProtocols.ReadOSErrorSlot(
                RequireInstance(instance, span),
                Name
            ),
            _ => ManagedObjectProtocols.ReadSyntaxErrorSlot(RequireInstance(instance, span), Name),
        };
    }

    internal void Set(PythonValue instance, PythonValue value, TextSpan span)
    {
        if (Family == PythonExceptionMemberFamily.OSError)
        {
            ManagedObjectProtocols.WriteOSErrorSlot(RequireInstance(instance, span), Name, value);
            return;
        }
        ManagedObjectProtocols.WriteSyntaxErrorSlot(RequireInstance(instance, span), Name, value);
    }

    internal void Delete(PythonValue instance, TextSpan span)
    {
        if (Family == PythonExceptionMemberFamily.OSError)
        {
            ManagedObjectProtocols.DeleteOSErrorSlot(RequireInstance(instance, span), Name);
            return;
        }
        ManagedObjectProtocols.DeleteSyntaxErrorSlot(RequireInstance(instance, span), Name);
    }

    internal PythonValue GetAttribute(string name, TextSpan span) =>
        name switch
        {
            "__name__" => new PythonTextValue(Name),
            "__qualname__" => new PythonTextValue($"{Owner}.{Name}"),
            "__objclass__" => PythonBuiltinTypes.GetExceptionType(Owner),
            "__doc__" => new PythonTextValue(DocStrings[(Family, Name)]),
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

    private PythonExceptionValue RequireInstance(PythonValue value, TextSpan span)
    {
        if (value is PythonExceptionValue exception)
        {
            var carries =
                Family == PythonExceptionMemberFamily.OSError
                    ? ManagedObjectProtocols.CarriesOSErrorSlots(exception)
                    : ManagedObjectProtocols.CarriesSyntaxErrorSlots(exception);
            if (carries)
            {
                return exception;
            }
        }

        throw Error(
            $"descriptor '{Name}' for '{Owner}' objects doesn't apply to a '{ManagedObjectProtocols.GetTypeName(value)}' object",
            span
        );
    }

    private static PythonRuntimeException Error(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, "TypeError");

    public bool Equals(PythonExceptionMemberDescriptorValue? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    internal override string ToDisplayString() =>
        $"<member '{Name}' of '{FamilyNames[Family]}' objects>";
}
