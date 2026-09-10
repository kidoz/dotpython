using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal sealed partial class PythonVirtualMachine
{
    private sealed record PendingClassConstruction(
        string Name,
        PythonTupleValue Bases,
        PythonValue Metaclass,
        PythonDictionaryValue Namespace,
        string[] KeywordNames,
        PythonValue[] KeywordValues,
        TextSpan Span
    );

    private sealed record PythonClassCellValue(PythonCell Cell) : PythonValue
    {
        internal override string ToDisplayString() => "<cell>";
    }

    PythonValue IUserObjectDispatcher.CreateType(
        PythonValue metaclass,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    ) => CreateTypeCore(metaclass, arguments, keywordNames, keywordValues, span);

    PythonValue IUserObjectDispatcher.CallType(
        PythonValue type,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (type is PythonManagedTypeValue managed)
            return InvokeDefaultClassCall(
                managed,
                [.. arguments],
                [.. keywordNames],
                [.. keywordValues],
                span
            );
        if (!ReferenceEquals(type, PythonBuiltinTypes.Type))
            throw Fault("DPY4003", "type.__call__ requires a type object", span, "TypeError");
        if (arguments.Count == 1)
        {
            if (keywordNames.Count != 0)
                throw Fault("DPY4003", "type() takes no keyword arguments", span, "TypeError");
            return TypeOf(arguments, span);
        }
        if (arguments.Count != 3)
            throw Fault("DPY4003", "type() takes 1 or 3 arguments", span, "TypeError");
        return InvokeMetaclassConstructor(
            type,
            [.. arguments],
            [.. keywordNames],
            [.. keywordValues],
            span
        );
    }

    private PythonValue SelectMetaclass(
        PythonValue candidate,
        PythonTupleValue bases,
        TextSpan span
    )
    {
        if (
            !ReferenceEquals(candidate, PythonBuiltinTypes.Type)
            && candidate is not PythonManagedTypeValue { IsMetaclass: true }
        )
            throw Fault(
                "DPY4003",
                "Only type-based metaclasses are supported in this runtime slice.",
                span,
                "TypeError"
            );
        var winner = candidate;
        foreach (var baseType in bases.Elements)
        {
            var metaclass = PythonBuiltinTypes.GetRuntimeType(baseType);
            if (IsSubclassOf(winner, metaclass, span))
                continue;
            if (IsSubclassOf(metaclass, winner, span))
            {
                winner = metaclass;
                continue;
            }
            throw Fault(
                "DPY4003",
                "metaclass conflict: the metaclass of a derived class must be a (non-strict) subclass of the metaclasses of all its bases",
                span,
                "TypeError"
            );
        }
        return winner;
    }

    private PendingClassConstruction PrepareClassConstruction(
        string name,
        PythonTupleValue bases,
        PythonDictionaryValue keywords,
        TextSpan span
    )
    {
        PythonValue candidate = PythonBuiltinTypes.Type;
        var names = new List<string>();
        var values = new List<PythonValue>();
        foreach (var item in keywords.Items)
        {
            if (item.Key is not PythonTextValue text)
                throw Fault("DPY4003", "keywords must be strings", span, "TypeError");
            if (text.Value == "metaclass")
                candidate = item.Value;
            else
            {
                names.Add(text.Value);
                values.Add(item.Value);
            }
        }
        var metaclass = SelectMetaclass(candidate, bases, span);
        var prepare = ManagedObjectProtocols.GetAttribute(metaclass, "__prepare__", span);
        var prepared = InvokeCallableNested(
            prepare,
            [new PythonTextValue(name), bases],
            span,
            [.. names],
            [.. values]
        );
        if (prepared is not PythonDictionaryValue dictionary)
            throw Fault(
                "DPY4003",
                $"{TypeDisplayName(metaclass)}.__prepare__() must return a mapping, not {ManagedObjectProtocols.GetTypeName(prepared)}",
                span,
                "TypeError"
            );
        return new(name, bases, metaclass, dictionary, [.. names], [.. values], span);
    }

    private PythonValue CompleteClassConstruction(
        PendingClassConstruction construction,
        PythonCell? cell
    )
    {
        var result = InvokeMetaclassConstructor(
            construction.Metaclass,
            [new PythonTextValue(construction.Name), construction.Bases, construction.Namespace],
            construction.KeywordNames,
            construction.KeywordValues,
            construction.Span
        );
        if (result is PythonManagedTypeValue && cell is not null)
        {
            if (cell.Value is null)
                throw Fault(
                    "DPY4003",
                    $"__class__ not set defining '{construction.Name}' as {result.ToRepresentationString()}. Was __classcell__ propagated to type.__new__?",
                    construction.Span,
                    "RuntimeError"
                );
            if (!ReferenceEquals(cell.Value, result))
                throw Fault(
                    "DPY4003",
                    $"__class__ set to {cell.Value.ToRepresentationString()} defining '{construction.Name}' as {result.ToRepresentationString()}",
                    construction.Span,
                    "TypeError"
                );
        }
        return result;
    }

    private PythonValue InvokeMetaclassNew(
        PythonValue metaclass,
        PythonValue[] arguments,
        string[] keywordNames,
        PythonValue[] keywordValues,
        TextSpan span
    )
    {
        if (ReferenceEquals(metaclass, PythonBuiltinTypes.Type))
            return CreateTypeCore(metaclass, arguments, keywordNames, keywordValues, span);
        var newMethod = ManagedObjectProtocols.GetAttribute(metaclass, "__new__", span);
        return InvokeCallableNested(
            newMethod,
            PrependArgument(metaclass, arguments),
            span,
            keywordNames,
            keywordValues
        );
    }

    private PythonValue InvokeMetaclassConstructor(
        PythonValue metaclass,
        PythonValue[] arguments,
        string[] keywordNames,
        PythonValue[] keywordValues,
        TextSpan span
    )
    {
        var result = InvokeMetaclassNew(metaclass, arguments, keywordNames, keywordValues, span);
        if (
            result is PythonManagedTypeValue created
            && IsSubclassOf(created.Metaclass, metaclass, span)
        )
        {
            var initializer = ManagedObjectProtocols.GetAttribute(
                created.Metaclass,
                "__init__",
                span
            );
            // type.__init__ and metaclass functions are unbound on the metaclass itself.
            var initialized = InvokeCallableNested(
                initializer,
                PrependArgument(created, arguments),
                span,
                keywordNames,
                keywordValues
            );
            if (initialized is not PythonNoneValue)
                throw Fault(
                    "DPY4003",
                    $"__init__() should return None, not '{ManagedObjectProtocols.GetTypeName(initialized)}'",
                    span,
                    "TypeError"
                );
        }
        return result;
    }

    private void InitializeSubclass(
        PythonManagedTypeValue type,
        string[] keywordNames,
        PythonValue[] keywordValues,
        TextSpan span
    )
    {
        // Lookup begins after the new class, then follows its C3 MRO.
        var method = ManagedObjectProtocols.GetAttribute(
            new PythonSuperProxyValue(type, type),
            "__init_subclass__",
            span
        );
        InvokeCallableNested(method, [], span, keywordNames, keywordValues);
    }

    private static string TypeDisplayName(PythonValue type) =>
        type switch
        {
            PythonManagedTypeValue managed => managed.Name,
            PythonBuiltinTypeValue builtin => builtin.Name,
            _ => ManagedObjectProtocols.GetTypeName(type),
        };

    private static bool HasCustomMetaclassCall(PythonManagedTypeValue type) =>
        type.Metaclass is PythonManagedTypeValue meta
        && meta.Mro.Any(entry => entry.Attributes.TryGetValue("__call__", out _));

    private PythonValue InvokeClassCall(
        PythonManagedTypeValue type,
        PythonValue[] arguments,
        string[] keywordNames,
        PythonValue[] keywordValues,
        TextSpan span
    )
    {
        if (HasCustomMetaclassCall(type))
        {
            var meta = (PythonManagedTypeValue)type.Metaclass;
            ManagedObjectProtocols.TryGetTypeAttribute(meta, "__call__", out var method);
            var bound = ManagedObjectProtocols.BindDescriptor(method, type, meta, span, "__call__");
            return InvokeCallableNested(bound, arguments, span, keywordNames, keywordValues);
        }
        return InvokeDefaultClassCall(type, arguments, keywordNames, keywordValues, span);
    }

    private PythonValue InvokeDefaultClassCall(
        PythonManagedTypeValue type,
        PythonValue[] arguments,
        string[] keywordNames,
        PythonValue[] keywordValues,
        TextSpan span
    )
    {
        if (type.IsMetaclass)
            return InvokeMetaclassConstructor(type, arguments, keywordNames, keywordValues, span);
        if (type.ExceptionBaseName is not null)
            return ConstructExceptionInstance(type, arguments, keywordNames, keywordValues, span);
        if (type.Construct is not null)
            return type.Construct(arguments);
        if (
            TryConstructThroughNew(
                type,
                arguments,
                keywordNames,
                keywordValues,
                span,
                out var created
            )
        )
            return created;
        var instance = new PythonManagedObjectValue(type);
        if (!ManagedObjectProtocols.TryGetTypeAttribute(type, "__init__", out var initializer))
        {
            if (arguments.Length != 0 || keywordNames.Length != 0)
                throw Fault("DPY4003", $"{type.Name}() takes no arguments", span, "TypeError");
            return instance;
        }
        var bound = ManagedObjectProtocols.BindDescriptor(
            initializer,
            instance,
            type,
            span,
            "__init__"
        );
        var result = InvokeCallableNested(bound, arguments, span, keywordNames, keywordValues);
        if (result is not PythonNoneValue)
            throw Fault(
                "DPY4003",
                $"__init__() should return None, not '{ManagedObjectProtocols.GetTypeName(result)}'",
                span,
                "TypeError"
            );
        return instance;
    }
}
