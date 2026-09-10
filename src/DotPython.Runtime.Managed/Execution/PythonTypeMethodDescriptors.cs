using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Descriptor behavior for the represented method descriptors declared by type.</summary>
internal static class PythonTypeMethodDescriptors
{
    internal static PythonValue Bind(
        PythonProtocolFunctionValue descriptor,
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
        if (!PythonTypeProtocols.IsType(instance))
            throw Error(
                $"descriptor '{descriptor.Name}' for 'type' objects doesn't apply to a '{ManagedObjectProtocols.GetTypeName(instance)}' object",
                span
            );
        return new PythonBoundMethodValue(descriptor.Name, instance, descriptor);
    }

    internal static PythonValue GetAttribute(
        PythonProtocolFunctionValue descriptor,
        string name,
        TextSpan span
    ) =>
        name switch
        {
            "__name__" => new PythonTextValue(descriptor.Name),
            "__qualname__" => new PythonTextValue("type." + descriptor.Name),
            "__objclass__" => PythonBuiltinTypes.Type,
            "__get__" => new PythonBuiltinFunctionValue(
                "__get__",
                (arguments, callSpan) => Get(descriptor, arguments, callSpan),
                (arguments, names, _, callSpan) =>
                    names.Count == 0
                        ? Get(descriptor, arguments, callSpan)
                        : throw Error("wrapper __get__() takes no keyword arguments", callSpan)
            ),
            _ => throw ManagedObjectProtocols.Fault(
                "DPY4023",
                $"'method_descriptor' object has no attribute '{name}'",
                span,
                "AttributeError"
            ),
        };

    internal static PythonValue GetBoundQualName(PythonBoundMethodValue method)
    {
        var ownerName = method.Target switch
        {
            PythonManagedTypeValue type => type.QualName ?? type.Name,
            PythonBuiltinTypeValue type => type.Name,
            PythonExceptionTypeValue type => type.Name,
            _ => ManagedObjectProtocols.GetTypeName(method.Target),
        };
        return new PythonTextValue(ownerName + "." + method.Name);
    }

    private static PythonValue Get(
        PythonProtocolFunctionValue descriptor,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count == 0)
            throw Error("__get__ expected at least 1 argument, got 0", span);
        if (arguments.Count > 2)
            throw Error($"__get__ expected at most 2 arguments, got {arguments.Count}", span);
        return Bind(descriptor, arguments[0], arguments.Count == 2 ? arguments[1] : null, span);
    }

    private static PythonRuntimeException Error(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, "TypeError");
}
