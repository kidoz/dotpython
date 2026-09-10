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

    internal static PythonIteratorValue GetReverseIterator(PythonValue value, TextSpan span)
    {
        value = Unwrap(value);
        var dictionary = value switch
        {
            PythonDictionaryValue mapping => mapping,
            PythonDictionaryViewValue view => view.Dictionary,
            _ => null,
        };
        if (dictionary is null)
        {
            if (ManagedObjectProtocols.TryGetSpecialMethod(value, "__reversed__", out var method))
            {
                return ManagedObjectProtocols.GetIterator(
                    UserObjectProtocols.Dispatcher!.Invoke(method, [], span),
                    span
                );
            }
            throw ManagedObjectProtocols.Fault(
                "DPY4015",
                $"'{ManagedObjectProtocols.GetTypeName(value)}' object is not reversible",
                span,
                "TypeError"
            );
        }
        var version = dictionary.SizeVersion;
        var index = dictionary.Items.Count;
        var exhausted = false;
        var kind = value is PythonDictionaryViewValue dictionaryView
            ? dictionaryView.Kind
            : "dict_keys";
        return new PythonIteratorValue(
            new PythonUserIteratorSourceValue(() =>
            {
                if (exhausted)
                {
                    return (false, PythonNoneValue.Instance);
                }
                if (dictionary.SizeVersion != version)
                {
                    throw ManagedObjectProtocols.Fault(
                        "DPY4016",
                        "dictionary changed size during iteration",
                        span,
                        "RuntimeError"
                    );
                }
                if (index > 0)
                {
                    return (true, ViewItem(dictionary.Items[--index], kind));
                }
                exhausted = true;
                return (false, PythonNoneValue.Instance);
            }),
            -1
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
