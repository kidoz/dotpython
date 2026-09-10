using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal sealed partial class PythonVirtualMachine
{
    private sealed record PendingClassConstruction(
        string Name,
        PythonTupleValue Bases,
        PythonValue Metaclass,
        PythonValue Namespace,
        string[] KeywordNames,
        PythonValue[] KeywordValues,
        TextSpan Span,
        PythonTupleValue? OriginalBases = null
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

    private static PythonValue SelectMetaclass(
        PythonValue candidate,
        PythonTupleValue bases,
        TextSpan span
    )
    {
        if (
            candidate
            is not (PythonManagedTypeValue or PythonBuiltinTypeValue or PythonExceptionTypeValue)
        )
            return candidate;
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
        var resolvedBases = ResolveClassBases(bases, span);
        PythonValue candidate =
            resolvedBases.Elements.Length == 0
                ? PythonBuiltinTypes.Type
                : PythonBuiltinTypes.GetRuntimeType(resolvedBases.Elements[0]);
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
        var metaclass = SelectMetaclass(candidate, resolvedBases, span);
        var prepared = TryGetClassConstructionAttribute(
            metaclass,
            "__prepare__",
            span,
            out var prepare
        )
            ? InvokeCallableNested(
                prepare,
                [new PythonTextValue(name), resolvedBases],
                span,
                [.. names],
                [.. values]
            )
            : new PythonDictionaryValue([]);
        if (!PythonNamespaceMapping.IsMapping(prepared))
            throw Fault(
                "DPY4003",
                $"{TypeDisplayName(metaclass)}.__prepare__() must return a mapping, not {ManagedObjectProtocols.GetTypeName(prepared)}",
                span,
                "TypeError"
            );
        return new(
            name,
            resolvedBases,
            metaclass,
            prepared,
            [.. names],
            [.. values],
            span,
            ReferenceEquals(resolvedBases, bases) ? null : bases
        );
    }

    private PythonTupleValue ResolveClassBases(PythonTupleValue bases, TextSpan span)
    {
        List<PythonValue>? resolved = null;
        for (var index = 0; index < bases.Elements.Length; index++)
        {
            var baseValue = bases.Elements[index];
            if (
                baseValue
                    is PythonManagedTypeValue
                        or PythonBuiltinTypeValue
                        or PythonExceptionTypeValue
                || !TryGetClassConstructionAttribute(
                    baseValue,
                    "__mro_entries__",
                    span,
                    out var resolve
                )
            )
            {
                resolved?.Add(baseValue);
                continue;
            }
            var replacement = InvokeCallableNested(resolve, [bases], span);
            if (replacement is not PythonTupleValue tuple)
                throw Fault("DPY4003", "__mro_entries__ must return a tuple", span, "TypeError");
            if (resolved is null)
            {
                resolved = [];
                for (var previous = 0; previous < index; previous++)
                    resolved.Add(bases.Elements[previous]);
            }
            resolved.AddRange(tuple.Elements);
        }
        return resolved is null ? bases : new PythonTupleValue([.. resolved]);
    }

    private static bool TryGetClassConstructionAttribute(
        PythonValue value,
        string name,
        TextSpan span,
        out PythonValue attribute
    )
    {
        try
        {
            attribute = ManagedObjectProtocols.GetAttribute(value, name, span);
            return true;
        }
        catch (Exception exception)
            when (PythonNamespaceMapping.IsPythonException(exception, "AttributeError"))
        {
            attribute = PythonNoneValue.Instance;
            return false;
        }
    }

    private PythonValue CompleteClassConstruction(
        PendingClassConstruction construction,
        PythonCell? cell
    )
    {
        if (construction.OriginalBases is { } originalBases)
            PythonNamespaceMapping.Set(
                construction.Namespace,
                "__orig_bases__",
                originalBases,
                construction.Span
            );
        var result = InvokeCallableNested(
            construction.Metaclass,
            [new PythonTextValue(construction.Name), construction.Bases, construction.Namespace],
            construction.Span,
            construction.KeywordNames,
            construction.KeywordValues
        );
        if (PythonTypeProtocols.IsType(result) && cell is not null)
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
        // A custom order may omit type while retaining object. The inherited
        // allocation slot still creates a class, independently of the public
        // type.__new__ descriptor's stricter subtype check.
        if (
            metaclass is PythonManagedTypeValue managed
            && !ManagedObjectProtocols.TryGetTypeAttribute(managed, "__new__", out _)
        )
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
        // Custom MROs need not include the new class. Validate super's receiver
        // after __set_name__, just as type.__new__ does.
        if (!PythonBuiltinTypes.GetMro(type).Elements.Any(entry => ReferenceEquals(entry, type)))
            throw Fault(
                "DPY4003",
                $"super(type, obj): obj (type {type.Name}) is not an instance or subtype of type ({type.Name}).",
                span,
                "TypeError"
            );
        var method = ManagedObjectProtocols.GetAttribute(
            new PythonSuperProxyValue(type, type),
            "__init_subclass__",
            span
        );
        InvokeCallableNested(method, [], span, keywordNames, keywordValues);
    }

    private void InitializeMethodResolutionOrder(PythonManagedTypeValue type, TextSpan span)
    {
        List<PythonValue> resolution;
        if (ReferenceEquals(type.Metaclass, PythonBuiltinTypes.Type))
            resolution = PythonTypeMro.Compute(type, span).Elements;
        else
        {
            // Implicit mro() lookup bypasses class attributes and metaclass
            // __getattribute__/__getattr__, but binds user descriptors normally.
            if (!ManagedObjectProtocols.TryGetSpecialMethod(type, "mro", out var method))
                throw Fault("DPY4022", "mro", span, "AttributeError");
            var result = InvokeCallableNested(method, [], span);
            resolution = ManagedObjectProtocols.MaterializeValues(
                result,
                span,
                _userIterationDispatcher
            );
            if (resolution.Count == 0)
                throw Fault("DPY4034", "type MRO must not be empty", span, "TypeError");
            var solid = GetSolidLayoutBase(type);
            foreach (var entry in resolution)
            {
                if (!PythonTypeProtocols.IsType(entry))
                    throw Fault(
                        "DPY4034",
                        $"mro() returned a non-class ('{ManagedObjectProtocols.GetTypeName(entry)}')",
                        span,
                        "TypeError"
                    );
                var entrySolid = GetSolidLayoutBase(entry);
                if (
                    !PythonBuiltinTypes
                        .GetMro(solid)
                        .Elements.Any(value => ReferenceEquals(value, entrySolid))
                )
                    throw Fault(
                        "DPY4034",
                        $"mro() returned base with unsuitable layout ('{TypeDisplayName(entry)}')",
                        span,
                        "TypeError"
                    );
            }
        }
        // Install both the full order and its managed projection before any
        // __set_name__ or __init_subclass__ callback can observe the class.
        type.ResolutionOrder = resolution;
        type.IsMroPending = false;
    }

    private static PythonValue GetSolidLayoutBase(PythonValue type)
    {
        if (type is PythonManagedTypeValue managed)
            return managed.LayoutBase is { } layoutBase
                ? GetSolidLayoutBase(layoutBase)
                : PythonBuiltinFunctions.Object;
        if (type is PythonExceptionTypeValue exception)
        {
            // These builtin exceptions add storage beyond their immediate base.
            // Ordinary managed subclasses add no native storage of their own.
            if (
                exception.Name
                is "BaseException"
                    or "BaseExceptionGroup"
                    or "AttributeError"
                    or "ImportError"
                    or "NameError"
                    or "OSError"
                    or "StopIteration"
                    or "SyntaxError"
                    or "SystemExit"
                    or "UnicodeDecodeError"
                    or "UnicodeEncodeError"
                    or "UnicodeTranslateError"
            )
                return exception;
            return GetBuiltinExceptionBase(exception.Name) is { } baseName
                ? GetSolidLayoutBase(PythonBuiltinTypes.GetExceptionType(baseName))
                : PythonBuiltinFunctions.Object;
        }
        return type;
    }

    private static string TypeDisplayName(PythonValue type) =>
        type switch
        {
            PythonManagedTypeValue managed => managed.Name,
            PythonBuiltinTypeValue builtin => builtin.Name,
            PythonExceptionTypeValue exception => exception.Name,
            _ => ManagedObjectProtocols.GetTypeName(type),
        };

    private static bool HasCustomMetaclassCall(PythonManagedTypeValue type)
    {
        if (type.Metaclass is not PythonManagedTypeValue meta)
            return false;
        foreach (var entry in PythonBuiltinTypes.GetMro(meta).Elements)
        {
            if (ReferenceEquals(entry, PythonBuiltinTypes.Type))
                return false;
            if (
                entry is PythonManagedTypeValue managed
                && managed.Attributes.TryGetValue("__call__", out _)
            )
                return true;
        }
        return false;
    }

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
        EnsureClassAllocationSupported(type, span);
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

    private static void EnsureClassAllocationSupported(PythonManagedTypeValue type, TextSpan span)
    {
        // A custom MRO can remove every default allocation slot while leaving
        // the class's physical layout base intact. Layout alone must not make
        // such a class constructible; an explicit __new__ still takes priority.
        if (ManagedObjectProtocols.TryGetTypeAttribute(type, "__new__", out _))
            return;
        foreach (var entry in PythonBuiltinTypes.GetMro(type).Elements)
        {
            if (
                ReferenceEquals(entry, PythonBuiltinFunctions.Object)
                || entry is PythonExceptionTypeValue
            )
                return;
        }
        throw Fault("DPY4003", $"cannot create '{type.Name}' instances", span, "TypeError");
    }
}
