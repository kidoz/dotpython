using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Operations on the original mapping returned by a metaclass's __prepare__.</summary>
internal static class PythonNamespaceMapping
{
    // CPython's PyMapping_Check checks the subscription slot, not keys(), mutability,
    // or membership in collections.abc.Mapping. Even builtin sequences pass admission.
    internal static bool IsMapping(PythonValue value) =>
        value
            is PythonDictionaryValue
                or PythonMappingProxyValue
                or PythonListValue
                or PythonTupleValue
                or PythonTextValue
                or PythonByteSequenceValue
                or PythonRangeValue
        || ManagedObjectProtocols.GetManagedType(value) is { } type
            && ManagedObjectProtocols.TryGetTypeAttribute(type, "__getitem__", out _);

    internal static bool TryGet(
        PythonValue mapping,
        string name,
        TextSpan span,
        out PythonValue value
    )
    {
        try
        {
            value = GetItem(mapping, new PythonTextValue(name), span);
            return true;
        }
        catch (Exception error) when (IsPythonException(error, "KeyError"))
        {
            value = null!;
            return false;
        }
    }

    internal static PythonValue GetItem(PythonValue mapping, PythonValue key, TextSpan span)
    {
        if (ManagedObjectProtocols.TryGetSpecialMethod(mapping, "__getitem__", out var getter))
            return UserObjectProtocols.Dispatcher!.Invoke(getter, [key], span);
        return ManagedObjectProtocols.GetItem(mapping, key, span);
    }

    internal static void Set(PythonValue mapping, string name, PythonValue value, TextSpan span)
    {
        var key = new PythonTextValue(name);
        if (ManagedObjectProtocols.TryGetSpecialMethod(mapping, "__setitem__", out var setter))
        {
            UserObjectProtocols.Dispatcher!.Invoke(setter, [key, value], span);
            return;
        }
        ManagedObjectProtocols.SetItem(mapping, key, value, span);
    }

    internal static bool Delete(PythonValue mapping, string name, TextSpan span)
    {
        var key = new PythonTextValue(name);
        try
        {
            if (ManagedObjectProtocols.TryGetSpecialMethod(mapping, "__delitem__", out var deleter))
                UserObjectProtocols.Dispatcher!.Invoke(deleter, [key], span);
            else
                ManagedObjectProtocols.DeleteItem(mapping, key, span);
            return true;
        }
        catch (Exception error) when (IsPythonException(error, "KeyError"))
        {
            return false;
        }
    }

    internal static bool IsPythonException(Exception error, string typeName) =>
        error switch
        {
            PythonRuntimeException fault => (
                fault.PythonExceptionTypeName
                ?? PythonErrorIndicator.GetPythonExceptionTypeName(fault.Code)
            ) == typeName,
            PythonRaisedException { Value.ManagedType: { } type } => type.Mro.Any(entry =>
                entry.Bases.Count == 0 && entry.ExceptionBaseName == typeName
            ),
            PythonRaisedException raised => raised.Value.TypeName == typeName,
            _ => false,
        };
}
