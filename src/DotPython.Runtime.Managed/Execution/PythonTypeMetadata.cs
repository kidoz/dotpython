using System.Runtime.CompilerServices;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Raw storage operations for the implemented metadata descriptors on type.</summary>
internal static class PythonTypeMetadata
{
    internal static IReadOnlyList<string> Names { get; } =
    [
        "__name__",
        "__qualname__",
        "__module__",
        "__doc__",
        "__dict__",
        "__bases__",
        "__base__",
        "__mro__",
    ];

    private static readonly ConditionalWeakTable<
        PythonValue,
        PythonDictionaryValue
    > BuiltinDictionaries = new();

    internal static PythonDictionaryValue GetDictionary(PythonValue type) =>
        type switch
        {
            PythonManagedTypeValue managed => managed.Attributes.Dictionary,
            _ when ReferenceEquals(type, PythonBuiltinTypes.Type) =>
                PythonTypeProtocols.GetDictionary(),
            _ => BuiltinDictionaries.GetValue(type, static _ => new PythonDictionaryValue([])),
        };

    internal static PythonValue Get(
        PythonTypeMetadataDescriptorValue descriptor,
        PythonValue? instance,
        PythonValue? owner,
        TextSpan span
    )
    {
        if (instance is null or PythonNoneValue)
        {
            if (owner is null or PythonNoneValue)
                throw Error("__get__(None, None) is invalid", span);
            return descriptor;
        }
        RequireType(instance, descriptor.Name, span);
        var managed = instance as PythonManagedTypeValue;
        return descriptor.Name switch
        {
            "__name__" => new PythonTextValue(ClassName(instance)),
            "__qualname__" => new PythonTextValue(managed?.QualName ?? ClassName(instance)),
            "__module__" => managed is not null
            && managed.Attributes.TryGetValue("__module__", out var module)
                ? module
                : new PythonTextValue(
                    managed?.Module
                        ?? (
                            instance is PythonBuiltinTypeValue builtin
                                ? builtin.ModuleName
                                : "builtins"
                        )
                ),
            "__doc__" => managed is not null
            && managed.Attributes.TryGetValue("__doc__", out var doc)
                ? ManagedObjectProtocols.BindDescriptor(doc, null, instance, span, "__doc__")
                : PythonNoneValue.Instance,
            "__dict__" => new PythonMappingProxyValue(GetDictionary(instance)),
            "__bases__" => PythonBuiltinTypes.GetBases(instance),
            "__base__" => managed?.LayoutBase ?? GetBase(instance),
            "__mro__" => managed is { IsMroPending: true }
                ? PythonNoneValue.Instance
                : PythonBuiltinTypes.GetMro(instance),
            _ => throw new ArgumentException(
                "Unknown type metadata descriptor.",
                nameof(descriptor)
            ),
        };
    }

    internal static void Set(
        PythonTypeMetadataDescriptorValue descriptor,
        PythonValue instance,
        PythonValue value,
        TextSpan span
    )
    {
        var name = descriptor.Name;
        RequireType(instance, name, span);
        RequireWritable(name, span);
        var type = RequireMutable(instance, name, span);
        switch (name)
        {
            case "__name__":
            case "__qualname__":
                if (value is not PythonTextValue text)
                    throw Error(
                        $"can only assign string to {type.Name}.{name}, not '{ManagedObjectProtocols.GetTypeName(value)}'",
                        span
                    );
                if (name == "__name__")
                {
                    if (text.Value.Contains('\0', StringComparison.Ordinal))
                        throw ManagedObjectProtocols.Fault(
                            "DPY4003",
                            "type name must not contain null characters",
                            span,
                            "ValueError"
                        );
                    type.Name = text.Value;
                }
                else
                    type.QualName = text.Value;
                return;
            case "__module__":
                type.Attributes.Remove("__firstlineno__");
                type.Attributes[name] = value;
                type.Module = value is PythonTextValue module ? module.Value : null;
                return;
            case "__doc__":
                type.Attributes[name] = value;
                return;
            case "__bases__":
                ValidateBases(type, value, span);
                UserObjectProtocols.Dispatcher!.SetTypeBases(type, (PythonTupleValue)value, span);
                return;
            default:
                throw new ArgumentException(
                    "Unknown writable type metadata descriptor.",
                    nameof(descriptor)
                );
        }
    }

    internal static void Delete(
        PythonTypeMetadataDescriptorValue descriptor,
        PythonValue instance,
        TextSpan span
    )
    {
        RequireType(instance, descriptor.Name, span);
        RequireWritable(descriptor.Name, span);
        var type = RequireMutable(instance, descriptor.Name, span);
        throw Error(
            $"cannot delete '{descriptor.Name}' attribute of immutable type '{type.Name}'",
            span
        );
    }

    internal static PythonValue GetAttribute(
        PythonTypeMetadataDescriptorValue descriptor,
        string name,
        TextSpan span
    ) =>
        name switch
        {
            "__name__" => new PythonTextValue(descriptor.Name),
            "__objclass__" => PythonBuiltinTypes.Type,
            "__get__" => new PythonBuiltinFunctionValue(
                name,
                (arguments, callSpan) =>
                {
                    RequireArity(name, arguments.Count, 1, 2, callSpan);
                    return descriptor.Get(
                        arguments[0],
                        arguments.Count == 2 ? arguments[1] : null,
                        callSpan
                    );
                }
            ),
            "__set__" => new PythonBuiltinFunctionValue(
                name,
                (arguments, callSpan) =>
                {
                    RequireArity(name, arguments.Count, 2, 2, callSpan);
                    descriptor.Set(arguments[0], arguments[1], callSpan);
                    return PythonNoneValue.Instance;
                }
            ),
            "__delete__" => new PythonBuiltinFunctionValue(
                name,
                (arguments, callSpan) =>
                {
                    RequireArity(name, arguments.Count, 1, 1, callSpan);
                    descriptor.Delete(arguments[0], callSpan);
                    return PythonNoneValue.Instance;
                }
            ),
            _ => throw ManagedObjectProtocols.Fault(
                "DPY4023",
                $"'{(descriptor.Name == "__base__" ? "member_descriptor" : "getset_descriptor")}' object has no attribute '{name}'",
                span,
                "AttributeError"
            ),
        };

    private static PythonValue GetBase(PythonValue type)
    {
        var bases = PythonBuiltinTypes.GetBases(type).Elements;
        return bases.Length == 0 ? PythonNoneValue.Instance : bases[0];
    }

    private static string ClassName(PythonValue value) =>
        value switch
        {
            PythonManagedTypeValue type => type.Name,
            PythonBuiltinTypeValue type => type.Name,
            PythonExceptionTypeValue type => type.Name,
            _ => ManagedObjectProtocols.GetTypeName(value),
        };

    private static void RequireType(PythonValue value, string name, TextSpan span)
    {
        if (!PythonTypeProtocols.IsType(value))
            throw Error(
                $"descriptor '{name}' for 'type' objects doesn't apply to a '{ManagedObjectProtocols.GetTypeName(value)}' object",
                span
            );
    }

    private static PythonManagedTypeValue RequireMutable(
        PythonValue value,
        string name,
        TextSpan span
    ) =>
        value is PythonManagedTypeValue type
            ? type
            : throw Error(
                $"cannot set '{name}' attribute of immutable type '{ClassName(value)}'",
                span
            );

    private static void RequireWritable(string name, TextSpan span)
    {
        if (name is "__dict__" or "__mro__" or "__base__")
            throw ManagedObjectProtocols.Fault(
                "DPY4023",
                name == "__base__"
                    ? "readonly attribute"
                    : $"attribute '{name}' of 'type' objects is not writable",
                span,
                "AttributeError"
            );
    }

    private static void ValidateBases(PythonManagedTypeValue type, PythonValue value, TextSpan span)
    {
        if (value is not PythonTupleValue tuple)
            throw Error(
                $"can only assign tuple to {type.Name}.__bases__, not {ManagedObjectProtocols.GetTypeName(value)}",
                span
            );
        if (tuple.Elements.Length == 0)
            throw Error($"can only assign non-empty tuple to {type.Name}.__bases__, not ()", span);
        foreach (var item in tuple.Elements)
        {
            if (!PythonTypeProtocols.IsType(item))
                throw Error(
                    $"{type.Name}.__bases__ must be tuple of classes, not '{ManagedObjectProtocols.GetTypeName(item)}'",
                    span
                );
            if (PythonBuiltinTypes.GetMro(item).Elements.Any(entry => ReferenceEquals(entry, type)))
                throw Error("a __bases__ item causes an inheritance cycle", span);
            // An in-progress update has new structural bases but its old MRO.
            // Check the physical base chain as well before allowing reentrance.
            for (
                var current = item as PythonManagedTypeValue;
                current is not null;
                current = current.LayoutBase as PythonManagedTypeValue
            )
                if (ReferenceEquals(current, type))
                    throw Error("a __bases__ item causes an inheritance cycle", span);
        }
    }

    private static void RequireArity(
        string name,
        int count,
        int minimum,
        int maximum,
        TextSpan span
    )
    {
        if (count < minimum || count > maximum)
        {
            var expected =
                minimum == maximum
                    ? minimum.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : count < minimum ? $"at least {minimum}"
                : $"at most {maximum}";
            var expectedCount = count < minimum ? minimum : maximum;
            var prefix = name == "__delete__" ? "" : name + " ";
            throw Error(
                $"{prefix}expected {expected} argument{(expectedCount == 1 ? "" : "s")}, got {count}",
                span
            );
        }
    }

    private static PythonRuntimeException Error(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, "TypeError");
}
