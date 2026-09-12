using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Read-only mapping delegation shared by class namespaces and types.MappingProxyType.</summary>
internal static class PythonMappingProxies
{
    internal static readonly PythonBuiltinTypeValue Type = new(
        "mappingproxy",
        Construct,
        PythonKeywordArguments.Adapt(
            "mappingproxy",
            ["mapping"],
            [null],
            Construct,
            typeStyleErrors: true
        )
    );

    private static PythonValue Construct(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        PythonBuiltinFunctions.RequireArgumentCount("mappingproxy", arguments, 1, 1, span);
        var mapping = arguments[0];
        if (
            mapping
                is not (
                    PythonDictionaryValue
                    or PythonMappingProxyValue
                    or PythonTextValue
                    or PythonByteSequenceValue
                    or PythonRangeValue
                )
            && !(
                ManagedObjectProtocols.GetManagedType(mapping) is { } type
                && ManagedObjectProtocols.TryGetTypeAttribute(type, "__getitem__", out _)
            )
        )
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"mappingproxy() argument must be a mapping, not {ManagedObjectProtocols.GetTypeName(mapping)}",
                span,
                "TypeError"
            );
        }
        return new PythonMappingProxyValue(mapping);
    }

    internal static PythonValue Unwrap(PythonValue value)
    {
        while (value is PythonMappingProxyValue proxy)
        {
            value = proxy.Mapping;
        }
        return value;
    }

    internal static PythonValue GetAttribute(
        PythonMappingProxyValue proxy,
        string name,
        TextSpan span
    )
    {
        if (name == "__reversed__")
            return new PythonBoundMethodValue(name, proxy, PythonReverseIterators.Method);
        if (name is not ("get" or "keys" or "values" or "items" or "copy"))
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4023",
                $"'mappingproxy' object has no attribute '{name}'",
                span,
                "AttributeError"
            );
        }
        return new PythonBoundMethodValue(
            name,
            proxy,
            new PythonProtocolFunctionValue(
                name,
                (_, arguments) =>
                {
                    var method = ManagedObjectProtocols.GetAttribute(proxy.Mapping, name, span);
                    return UserObjectProtocols.Dispatcher is { } dispatcher
                        ? dispatcher.Invoke(method, [.. arguments], span)
                        : ManagedObjectProtocols.Call(method, arguments, span);
                }
            )
        );
    }

    internal static PythonValue ViewItem(PythonDictionaryItemValue item, string kind) =>
        kind switch
        {
            "dict_values" => item.Value,
            "dict_items" => new PythonTupleValue([item.Key, item.Value]),
            _ => item.Key,
        };
}
