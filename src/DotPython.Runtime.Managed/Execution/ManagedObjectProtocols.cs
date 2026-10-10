using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal enum PythonRichComparison
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
}

/// <summary>
/// Resolves a user-defined instance into an iterator by running its `__iter__` on the
/// owning VM; the static protocol layer cannot execute frames itself.
/// </summary>
internal delegate PythonIteratorValue UserIterationDispatcher(
    PythonManagedObjectValue instance,
    TextSpan span
);

/// <summary>
/// Central protocol dispatch for managed values exposed through a native-compatibility boundary.
/// </summary>
internal static class ManagedObjectProtocols
{
    internal static bool IsCallable(PythonValue value) =>
        value switch
        {
            PythonFunctionValue
            or PythonBuiltinFunctionValue
            or PythonBuiltinTypeValue
            or PythonExceptionTypeValue
            or PythonManagedTypeValue
            or PythonProtocolFunctionValue
            or PythonBoundMethodValue
            or PythonBoundUserMethodValue
            or PythonMethodDescriptorValue
            or PythonStaticMethodValue => true,
            PythonManagedObjectValue instance => ManagedObjectProtocols.TryGetTypeAttribute(
                instance.Type,
                "__call__",
                out _
            ),
            _ => false,
        };

    [ThreadStatic]
    private static int _sequenceComparisonDepth;

    internal static PythonValue Call(
        PythonValue callable,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(callable);
        ArgumentNullException.ThrowIfNull(arguments);

        return callable switch
        {
            PythonBuiltinFunctionValue builtin => builtin.Invoke(arguments, span),
            // A parameterized generic constructs its origin, so `list[int]([1, 2])` is
            // `list([1, 2])`.
            PythonGenericAliasValue alias => Call(alias.Origin, arguments, span),
            PythonBuiltinTypeValue builtinType => builtinType.Construct(arguments, span),
            PythonProtocolFunctionValue function => function.Invoke(null, arguments),
            PythonMethodDescriptorValue descriptor => descriptor.Invoke(arguments, span),
            PythonBoundMethodValue
            {
                Target: PythonIteratorValue iterator,
                Name: "__iter__" or "__next__"
            } method => PythonIteratorProtocols.Invoke(iterator, method.Name, arguments, span),
            PythonBoundMethodValue method => method.Function.Invoke(
                method.TargetsStorage
                    ? PythonSubclassStorage.Of(method.Target) ?? method.Target
                    : method.Target,
                arguments
            ),
            PythonExternalObjectValue external => external.Protocol.Call(arguments, span),
            PythonManagedTypeValue type when type.Construct is not null => type.Construct(
                arguments
            ),
            // An abstract class refuses to be instantiated while any abstract method it
            // declares is still the stub the class itself carries.
            PythonManagedTypeValue type
                when arguments.Count == 0
                    && PythonCollectionsAbc.RefuseInstantiation(type, span)
                        is { } abstractRefusal => throw abstractRefusal,
            PythonManagedTypeValue type when arguments.Count == 0 => new PythonManagedObjectValue(
                type
            ),
            PythonStaticMethodValue staticMethod => Call(staticMethod.Function, arguments, span),
            PythonClassMethodValue classMethod => Call(classMethod.Function, arguments, span),
            PythonManagedObjectValue instance
                when UserObjectProtocols.TryGetSpecialMethod(
                    instance,
                    "__call__",
                    out var call,
                    out _
                ) => UserObjectProtocols.Dispatcher!.Invoke(call, [.. arguments], span),
            PythonManagedObjectValue instance => throw Fault(
                "DPY4003",
                $"'{instance.Type.ReportedName}' object is not callable",
                span,
                "TypeError"
            ),
            _ => throw Fault(
                "DPY4009",
                $"'{GetTypeName(callable)}' object is not callable",
                span,
                "TypeError"
            ),
        };
    }

    internal static PythonValue GetAttribute(
        PythonValue target,
        string name,
        TextSpan span = default
    )
    {
        if (GetManagedType(target) is null)
        {
            return GetAttributeCore(target, name, span);
        }
        try
        {
            return TryGetSpecialMethod(target, "__getattribute__", out var getAttribute)
                ? UserObjectProtocols.Dispatcher!.Invoke(
                    getAttribute,
                    [new PythonTextValue(name)],
                    span
                )
                : GetAttributeCore(target, name, span);
        }
        catch (Exception error) when (IsAttributeError(error))
        {
            if (TryGetSpecialMethod(target, "__getattr__", out var fallback))
            {
                return UserObjectProtocols.Dispatcher!.Invoke(
                    fallback,
                    [new PythonTextValue(name)],
                    span
                );
            }
            throw;
        }
    }

    private static bool TryGetMetaclassAttribute(
        PythonValue target,
        string name,
        out PythonValue metaclass,
        out PythonValue attribute
    )
    {
        if (!PythonTypeProtocols.IsType(target))
        {
            metaclass = null!;
            attribute = null!;
            return false;
        }
        metaclass = PythonBuiltinTypes.GetRuntimeType(target);
        return metaclass is PythonManagedTypeValue managed
            ? TryGetTypeAttribute(managed, name, out attribute)
            : PythonTypeProtocols.TryGetAttribute(name, out attribute);
    }

    /// <summary>
    /// The numeric members are getset descriptors with no setter, so writing or deleting one
    /// reports the defining type rather than the target: a bool says `int`.
    /// </summary>
    private static void RejectReadOnlyNumberMember(PythonValue target, string name, TextSpan span)
    {
        var owner = target switch
        {
            PythonWholeNumberValue
            or PythonTruthValue when PythonIntMethods.IsReadOnlyMember(name) => "int",
            PythonFloatingPointValue when PythonFloatMethods.IsReadOnlyMember(name) => "float",
            PythonDequeValue when name == "maxlen" => "collections.deque",
            _ => null,
        };
        if (owner is not null)
            throw Fault(
                "DPY4023",
                $"attribute '{name}' of '{owner}' objects is not writable",
                span,
                "AttributeError"
            );
    }

    private static void RejectBuiltinTypeMutation(
        PythonValue target,
        string name,
        string operation,
        TextSpan span
    )
    {
        var typeName = target switch
        {
            PythonBuiltinTypeValue builtin => builtin.Name,
            PythonExceptionTypeValue exception => exception.Name,
            _ => null,
        };
        if (typeName is not null)
            throw Fault(
                "DPY4023",
                $"cannot {operation} '{name}' attribute of immutable type '{typeName}'",
                span,
                "TypeError"
            );
    }

    internal static PythonValue GetAttributeCore(
        PythonValue target,
        string name,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (
            TryGetMetaclassAttribute(
                target,
                name,
                out var descriptorMetaclass,
                out var metaDescriptor
            )
            && IsDataDescriptor(metaDescriptor)
            && HasDescriptorGetter(metaDescriptor)
        )
            return BindDescriptor(metaDescriptor, target, descriptorMetaclass, span, name);

        if (name == "__class__" && target is not PythonManagedObjectValue)
        {
            return UserObjectProtocols.Dispatcher is null
                ? PythonBuiltinTypes.GetRuntimeType(target)
                : PythonBuiltinTypes.Type.Construct([target], span);
        }
        switch (target)
        {
            case PythonModuleValue module when name == "__dict__":
                return module.Globals.Dictionary;
            case PythonModuleValue module when name == "__annotate__":
                // A module with no annotations reports None rather than failing, and
                // the callable itself is an ordinary global once the body has run.
                return module.Globals.TryGetValue(name, out var moduleAnnotate)
                    ? moduleAnnotate
                    : PythonNoneValue.Instance;
            case PythonModuleValue module when name == "__annotations__":
                return GetModuleAnnotations(module, span);
            // A module object always carries a `__doc__`, answering None when its body
            // opens with anything that is not a string.
            case PythonModuleValue module when name == "__doc__":
                return module.Globals.TryGetValue(name, out var moduleDoc)
                    ? moduleDoc
                    : PythonNoneValue.Instance;
            case PythonModuleValue module
                when module.Globals.TryGetValue(name, out var moduleValue):
                return moduleValue;
            case PythonModuleValue module:
                throw Fault(
                    "DPY4022",
                    $"module '{module.Name}' has no attribute '{name}'",
                    span,
                    "AttributeError"
                );
            case PythonManagedObjectValue instance:
                return GetInstanceAttribute(instance, name, span);
            case PythonTupleGetterValue tupleGetter
                when GetTupleGetterAttribute(tupleGetter, name) is { } getterAttribute:
                return getterAttribute;
            case PythonSuperProxyValue proxy:
                if (TryResolveSuperAttribute(proxy, name, out var inherited))
                {
                    var owner = GetSuperResolutionType(proxy);
                    var classAccess =
                        PythonTypeProtocols.IsType(proxy.Instance)
                        && ReferenceEquals(owner, proxy.Instance);
                    return BindDescriptor(
                        inherited,
                        classAccess ? null : proxy.Instance,
                        owner,
                        span,
                        name
                    );
                }
                if (name == "__init_subclass__")
                    return BindDescriptor(
                        PythonTypeProtocols.InitSubclass,
                        null,
                        PythonBuiltinTypes.CreateOpaque("super"),
                        span,
                        name
                    );
                throw Fault(
                    "DPY4022",
                    $"'super' object has no attribute '{name}'",
                    span,
                    "AttributeError"
                );
            case PythonMappingProxyValue proxy:
                return PythonMappingProxies.GetAttribute(proxy, name, span);
            case PythonTypeAliasValue alias when name == "__name__":
                return new PythonTextValue(alias.Name);
            case PythonTypeAliasValue alias when name == "__module__":
                return new PythonTextValue(alias.Module);
            case PythonTypeAliasValue alias when name == "__value__":
                return alias.Value;
            case PythonTypeAliasValue when name == "__type_params__":
                // Type parameters are not implemented in this slice, so every alias
                // reports an empty parameter list.
                return new PythonTupleValue([]);
            case PythonTypeAliasValue alias:
                throw MissingAttribute(alias.Name, name, span);
            case PythonGenericAliasValue alias when name == "__origin__":
                return alias.Origin;
            case PythonGenericAliasValue alias when name == "__args__":
                return new PythonTupleValue([.. alias.Arguments]);
            case PythonGenericAliasValue when name == "__parameters__":
                return new PythonTupleValue([]);
            case PythonTypeUnionValue when name == "__origin__":
                return PythonBuiltinTypes.Union;
            case PythonTypeUnionValue union when name == "__args__":
                return new PythonTupleValue([.. union.Members]);
            case PythonTypeUnionValue when name == "__parameters__":
                return new PythonTupleValue([]);
            case PythonTypeUnionValue when name == "__name__":
                return new PythonTextValue(PythonBuiltinTypes.Union.Name);
            case PythonManagedTypeValue type when name == "__annotate__":
                // Deliberately the class's own dictionary, never the MRO: an
                // unannotated subclass reports None and an empty mapping rather than
                // inheriting its base's annotations.
                return type.Attributes.TryGetValue(AnnotateClassDictName, out var typeAnnotate)
                    ? typeAnnotate
                    : PythonNoneValue.Instance;
            case PythonManagedTypeValue type when name == "__annotations__":
                return GetTypeAnnotations(type, span);
            case PythonManagedTypeValue type when TryGetTypeAttribute(type, name, out var value):
                return BindDescriptor(value, null, type, span, name);
            case PythonManagedTypeValue { IsMetaclass: false } objectClass
                when PythonBuiltinTypes
                    .GetMro(objectClass)
                    .Elements.Any(entry => ReferenceEquals(entry, PythonBuiltinFunctions.Object))
                    && PythonBuiltinFunctions.TryGetObjectProtocol(
                        name,
                        out var inheritedObjectMember
                    ):
                return inheritedObjectMember;
            case PythonManagedTypeValue { Metaclass: PythonManagedTypeValue metaclass } type
                when TryGetTypeAttribute(metaclass, name, out var metaclassAttribute):
                return BindDescriptor(metaclassAttribute, type, metaclass, span, name);
            case PythonManagedTypeValue type
                when PythonTypeProtocols.TryGetAttribute(name, out var defaultTypeAttribute):
                return BindDescriptor(defaultTypeAttribute, type, type.Metaclass, span, name);
            case PythonManagedTypeValue type:
                throw MissingTypeAttribute(type.Name, name, span);
            case PythonExternalObjectValue external:
                return external.Protocol.GetAttribute(name, span);
            case PythonTypeMetadataDescriptorValue descriptor:
                return descriptor.GetAttribute(name, span);
            case PythonUnicodeErrorDescriptorValue descriptor:
                return descriptor.GetAttribute(name, span);
            case PythonProtocolFunctionValue { IsTypeMethodDescriptor: true } descriptor:
                return PythonTypeMethodDescriptors.GetAttribute(descriptor, name, span);
            case PythonPropertyValue property when name == "__isabstractmethod__":
                return PythonAbc.IsAbstract(property)
                    ? PythonTruthValue.True
                    : PythonTruthValue.False;
            // A bound method of a class written in Python answers the function's own
            // attributes, the marker among them, as CPython's method object delegates.
            case PythonBoundUserMethodValue boundMethod when name == "__isabstractmethod__":
                return GetAttributeCore(boundMethod.Function, name, span);
            case PythonPropertyValue property:
                return PythonBuiltinFunctions.GetPropertyAttribute(property, name, span);
            case PythonStaticMethodValue staticMethod when name == "__func__":
                return staticMethod.Function;
            case PythonClassMethodValue classMethod when name == "__func__":
                return classMethod.Function;
            case PythonBuiltinTypeValue builtin
                when ReferenceEquals(builtin, PythonBuiltinTypes.Type)
                    && PythonTypeProtocols.TryGetAttribute(name, out var typeAttribute):
                return BindDescriptor(typeAttribute, null, builtin, span, name);
            case PythonBuiltinTypeValue
            or PythonExceptionTypeValue when name is "mro" or "__subclasses__":
                PythonTypeProtocols.TryGetAttribute(name, out var mroMethod);
                return BindDescriptor(mroMethod, target, PythonBuiltinTypes.Type, span, name);
            case PythonExceptionTypeValue exceptionType
                when PythonExceptionProtocols.TryGetAttribute(
                    exceptionType,
                    name,
                    out var exceptionSlot
                ):
                return exceptionSlot;
            case PythonBuiltinTypeValue builtin when name == "__init_subclass__":
                return BindDescriptor(PythonTypeProtocols.InitSubclass, null, builtin, span, name);
            case PythonBuiltinTypeValue { Name: "object" }
                when PythonBuiltinFunctions.TryGetObjectProtocol(name, out var objectMember):
                return objectMember;
            case PythonClassMethodValue classMethod
                when name == "__isabstractmethod__" && PythonAbc.IsAbstract(classMethod.Function):
                return PythonTruthValue.True;
            case PythonStaticMethodValue staticMethod
                when name == "__isabstractmethod__" && PythonAbc.IsAbstract(staticMethod.Function):
                return PythonTruthValue.True;
            case PythonFunctionValue function
                when function.ShadowAttributes is { } shadowed
                    && shadowed.TryGetValue(name, out var shadowedValue):
                return shadowedValue;
            case PythonFunctionValue function when name == "__name__":
                return new PythonTextValue(function.Name);
            // The docstring the function's body opens with, which the compiler keeps on
            // the code object the function was built from.
            case PythonFunctionValue function when name == "__doc__":
                return function.Code.Definition.DocString is { } doc
                    ? new PythonTextValue(doc)
                    : PythonNoneValue.Instance;
            case PythonFunctionValue when name == "__type_params__":
                return new PythonTupleValue([]);
            case PythonFunctionValue function when name == "__qualname__":
                return new PythonTextValue(function.QualName ?? function.Name);
            case PythonFunctionValue function when name == "__module__":
                return function.Globals.TryGetValue("__name__", out var functionModule)
                    ? functionModule
                    : PythonNoneValue.Instance;
            case PythonProtocolFunctionValue { IsPythonMethod: true } namedFunction
                when name == "__name__":
                return new PythonTextValue(namedFunction.Name);
            case PythonProtocolFunctionValue
            {
                IsPythonMethod: true,
                DeclaringType: { } functionOwner
            } namedFunction when name == "__qualname__":
                return new PythonTextValue($"{functionOwner}.{namedFunction.Name}");
            case PythonProtocolFunctionValue { IsPythonMethod: true } namedFunction
                when name == "__qualname__":
                return new PythonTextValue(namedFunction.Name);
            case PythonProtocolFunctionValue { IsPythonMethod: true, Module: { } ownerModule }
                when name == "__module__":
                return new PythonTextValue(ownerModule);
            case PythonProtocolFunctionValue { IsPythonMethod: true } documentedFunction
                when name == "__doc__":
                return documentedFunction.Doc is { } functionDoc
                    ? new PythonTextValue(functionDoc)
                    : PythonNoneValue.Instance;
            case PythonProtocolFunctionValue { IsPythonMethod: true } defaultedFunction
                when name == "__defaults__":
                return defaultedFunction.Defaults ?? PythonNoneValue.Instance;
            // The attributes a function the runtime builds carries, `__wrapped__` among
            // them, which is what `functools.wraps` writes and what introspection reads.
            case PythonProtocolFunctionValue function
                when !IsFunctionMetadataName(name)
                    && function.Attributes.TryGetValue(name, out var protocolAttribute):
                return protocolAttribute;
            case PythonFunctionValue function when name == "__dict__":
                return function.Attributes.Dictionary;
            case PythonFunctionValue function when name == "__annotate__":
                return function.Annotate ?? PythonNoneValue.Instance;
            case PythonFunctionValue function when name == "__annotations__":
                return GetFunctionAnnotations(function, span);
            case PythonFunctionValue function
                when !IsFunctionMetadataName(name)
                    && function.Attributes.TryGetValue(name, out var functionAttribute):
                return functionAttribute;
            case PythonFunctionValue:
                throw MissingAttribute("function", name, span);
            case PythonStreamValue stream:
                return GetStreamAttribute(stream, name, span);
            case PythonBoundUserMethodValue boundUserMethod when name == "__name__":
                return new PythonTextValue(boundUserMethod.Function.Name);
            case PythonBoundUserMethodValue boundUserMethod when name == "__self__":
                return boundUserMethod.Target;
            case PythonBoundUserMethodValue boundUserMethod when name == "__doc__":
                return GetAttributeCore(boundUserMethod.Function, name, span);
            case PythonBoundMethodValue boundMethod when name == "__name__":
                return new PythonTextValue(boundMethod.Name);
            case PythonBoundMethodValue { Function.IsTypeMethodDescriptor: true } boundMethod
                when name == "__qualname__":
                return PythonTypeMethodDescriptors.GetBoundQualName(boundMethod);
            case PythonBoundMethodValue { Function.IsTypeMethodDescriptor: true } boundMethod
                when name == "__self__":
                return boundMethod.Target;
            case PythonBuiltinFunctionValue builtinFunction when name == "__name__":
                return new PythonTextValue(builtinFunction.Name);
            // The runtime's own functions carry no docstrings, so the attribute exists and
            // is None, where CPython documents every builtin.
            case PythonBuiltinFunctionValue when name == "__doc__":
                return PythonNoneValue.Instance;
            case PythonBuiltinTypeValue builtinTypeValue when name == "__name__":
                return new PythonTextValue(builtinTypeValue.Name);
            case PythonBuiltinTypeValue builtinTypeValue when name == "__qualname__":
                return new PythonTextValue(builtinTypeValue.Name);
            case PythonBuiltinTypeValue builtinTypeValue when name == "__module__":
                return new PythonTextValue(builtinTypeValue.ModuleName);
            // The runtime's own types carry no docstrings either, so the attribute exists
            // and is None, where CPython documents every builtin.
            case PythonBuiltinTypeValue or PythonExceptionTypeValue when name == "__doc__":
                return PythonNoneValue.Instance;
            case PythonBuiltinTypeValue { MatchArguments: { Elements.Length: > 0 } matchArguments }
                when name == "__match_args__":
                return matchArguments;
            case PythonDictionaryValue when name == "fromkeys":
            case PythonBuiltinTypeValue { Name: "dict" } when name == "fromkeys":
            {
                // The class the method was reached through rides in `BoundTo`, so a subclass
                // is the class the result belongs to.
                PythonBuiltinFunctionValue fromKeys = null!;
                fromKeys = new PythonBuiltinFunctionValue(
                    "fromkeys",
                    (arguments, callSpan) =>
                    {
                        PythonBuiltinFunctions.RequireArgumentCount(
                            "fromkeys",
                            arguments,
                            1,
                            2,
                            callSpan
                        );
                        var fill = arguments.Count == 2 ? arguments[1] : PythonNoneValue.Instance;
                        return DictionaryFromKeys(arguments[0], fill, callSpan, fromKeys.BoundTo);
                    }
                );
                fromKeys.BoundTo = PythonBuiltinTypes.Dict;
                return fromKeys;
            }
            case PythonBuiltinTypeValue { Name: "str" } builtin when name == "maketrans":
                return PythonTextMethods.CreateMakeTrans() with { BoundTo = builtin };
            // `bytearray` inherits both classmethods from `bytes`.
            case PythonBuiltinTypeValue { Name: "bytes" or "bytearray" } builtin
                when name == "fromhex":
                return PythonBytesMethods.CreateFromHex() with { BoundTo = builtin };
            case PythonBuiltinTypeValue { Name: "bytes" or "bytearray" } builtin
                when name == "maketrans":
                return PythonBytesMethods.CreateMakeTrans() with { BoundTo = builtin };
            // `from_bytes` is a classmethod, so it constructs the class it was reached
            // through: `bool.from_bytes(b'\x01')` is `True`, not `1`.
            case PythonBuiltinTypeValue { Name: "int" } builtin when name == "from_bytes":
                return PythonIntMethods.CreateFromBytes(boolean: false) with { BoundTo = builtin };
            case PythonBuiltinTypeValue { Name: "bool" } builtin when name == "from_bytes":
                return PythonIntMethods.CreateFromBytes(boolean: true) with { BoundTo = builtin };
            case PythonBuiltinTypeValue { Name: "float" } builtin when name == "fromhex":
                return PythonFloatMethods.CreateFromHex() with { BoundTo = builtin };
            case PythonExceptionTypeValue exceptionTypeValue when name == "__name__":
                return new PythonTextValue(exceptionTypeValue.Name);
            case PythonExceptionValue { ManagedType: { } exceptionClass } exception
                when TryGetTypeAttribute(exceptionClass, name, out var classAttribute):
                if (
                    !(IsDataDescriptor(classAttribute) && HasDescriptorGetter(classAttribute))
                    && exception.Attributes.TryGetValue(name, out var storedAttribute)
                )
                {
                    return storedAttribute;
                }
                return BindDescriptor(classAttribute, exception, exceptionClass, span, name);
            case PythonExceptionValue { ManagedType: null } unicodeException
                when PythonUnicodeErrors.TryGet(unicodeException, name, out var unicodeMember):
                return unicodeMember;
            case PythonExceptionValue exceptionDictionary when name == "__dict__":
                exceptionDictionary.HasInstanceDictionary = true;
                return exceptionDictionary.Attributes.Dictionary;
            case PythonExceptionValue exceptionValue when name == "args":
                return exceptionValue.ArgumentTuple;
            case PythonExceptionValue systemExit
                when name == "code"
                    && PythonBuiltinTypes
                        .GetMro(PythonBuiltinTypes.GetRuntimeType(systemExit))
                        .Elements.Any(entry =>
                            entry is PythonExceptionTypeValue { Name: "SystemExit" }
                        ):
                return systemExit.ConstructorArguments.Count switch
                {
                    0 => PythonNoneValue.Instance,
                    1 => systemExit.ConstructorArguments[0],
                    _ => new PythonTupleValue([.. systemExit.ConstructorArguments]),
                };
            case PythonExceptionValue stopIteration
                when name == "value"
                    && (
                        (
                            stopIteration.ManagedType is null
                            && stopIteration.TypeName == "StopIteration"
                        )
                        || stopIteration.ManagedType is { } iterationType
                            && PythonBuiltinTypes
                                .GetMro(iterationType)
                                .Elements.Any(entry =>
                                    entry is PythonExceptionTypeValue { Name: "StopIteration" }
                                )
                    ):
                return stopIteration.ConstructorArguments.Count == 0
                    ? PythonNoneValue.Instance
                    : stopIteration.ConstructorArguments[0];
            case PythonExceptionValue { GroupExceptions: not null } group when name == "message":
                return new PythonTextValue(group.Message);
            case PythonExceptionValue { GroupExceptions: not null } group when name == "exceptions":
                return group.GroupExceptionTuple;
            case PythonExceptionValue exceptionInstance
                when name
                    is "__new__"
                        or "__str__"
                        or "__init__"
                        or "derive"
                        or "split"
                        or "subgroup"
                        or "add_note"
                    && PythonExceptionProtocols.TryGetAttribute(
                        PythonBuiltinTypes.GetRuntimeType(exceptionInstance),
                        name,
                        out var slot
                    ):
                return BindDescriptor(
                    slot,
                    exceptionInstance,
                    PythonBuiltinTypes.GetRuntimeType(exceptionInstance),
                    span,
                    name
                );
            case PythonExceptionValue exceptionInstance
                when name is not ("__cause__" or "__context__" or "__suppress_context__")
                    && exceptionInstance.Attributes.TryGetValue(name, out var exceptionAttribute):
                return exceptionAttribute;
            case PythonExceptionValue contextSource when name == "__context__":
                return (PythonValue?)contextSource.Context ?? PythonNoneValue.Instance;
            case PythonExceptionValue causeSource when name == "__cause__":
                return (PythonValue?)causeSource.Cause ?? PythonNoneValue.Instance;
            case PythonExceptionValue suppressSource when name == "__suppress_context__":
                return PythonTruthValue.FromBoolean(suppressSource.SuppressContext);
            case PythonDictionaryViewValue view
                when name == "isdisjoint" && PythonDictionaryViews.IsSetLike(view):
                return new PythonBoundMethodValue(
                    name,
                    view,
                    new PythonProtocolFunctionValue(
                        name,
                        (receiver, arguments) =>
                            PythonDictionaryViews.IsDisjoint(receiver!, arguments, default)
                    )
                );
            case PythonListValue
            or PythonRangeValue
            or PythonDictionaryValue
            or PythonDictionaryViewValue when name == "__reversed__":
                return new PythonBoundMethodValue(name, target, PythonReverseIterators.Method);
            case PythonIteratorValue iterator when name is "__iter__" or "__next__":
                // Both are slots a builtin iterator answers, so both report that way.
                return new PythonBoundMethodValue(
                    name,
                    iterator,
                    name == "__iter__"
                        ? PythonIteratorProtocols.IterMethod
                        : PythonIteratorProtocols.NextMethod
                )
                {
                    IsWrapper = true,
                };
            case PythonIteratorValue iterator
                when name == "__length_hint__" && PythonLengthHints.SupportsIterator(iterator):
                return new PythonBoundMethodValue(name, iterator, PythonLengthHints.IteratorMethod);
            case PythonExceptionValue tracebackSource when name == "with_traceback":
                // The managed model does not carry traceback objects; the method
                // accepts and ignores its argument, returning the exception itself.
                return new PythonBoundMethodValue(
                    "with_traceback",
                    tracebackSource,
                    new PythonProtocolFunctionValue(
                        "with_traceback",
                        (_, tracebackArguments) =>
                            tracebackArguments.Count == 1
                                ? tracebackSource
                                : throw Fault(
                                    "DPY4003",
                                    $"with_traceback() takes exactly one argument ({tracebackArguments.Count} given).",
                                    default,
                                    "TypeError"
                                )
                    )
                );
            case PythonExceptionValue missingAttributeException:
                throw MissingAttribute(missingAttributeException.TypeName, name, span);
            case PythonFileValue enterFile when name == "__enter__":
                return new PythonBoundMethodValue(
                    "__enter__",
                    enterFile,
                    new PythonProtocolFunctionValue(
                        "__enter__",
                        (_, _) =>
                        {
                            EnsureFileOpen(enterFile, span);
                            return enterFile;
                        }
                    )
                );
            case PythonFileValue exitFile when name == "__exit__":
                return new PythonBoundMethodValue(
                    "__exit__",
                    exitFile,
                    new PythonProtocolFunctionValue(
                        "__exit__",
                        (_, _) =>
                        {
                            exitFile.IsClosed = true;
                            return PythonNoneValue.Instance;
                        }
                    )
                );
            case PythonFileValue closeFile when name == "close":
                return new PythonBoundMethodValue(
                    "close",
                    closeFile,
                    new PythonProtocolFunctionValue(
                        "close",
                        (_, _) =>
                        {
                            closeFile.IsClosed = true;
                            return PythonNoneValue.Instance;
                        }
                    )
                );
            case PythonFileValue nameFile when name == "name":
                return new PythonTextValue(nameFile.Name);
            case PythonFileValue modeFile when name == "mode":
                return new PythonTextValue(modeFile.Mode);
            case PythonFileValue closedFile when name == "closed":
                return PythonTruthValue.FromBoolean(closedFile.IsClosed);
            case PythonFileValue readFile when name == "read":
                return new PythonBoundMethodValue(
                    "read",
                    readFile,
                    new PythonProtocolFunctionValue(
                        "read",
                        (_, readArguments) =>
                        {
                            EnsureFileOpen(readFile, span);
                            if (readArguments.Count > 1)
                            {
                                throw Fault(
                                    "DPY4037",
                                    $"read expected at most 1 argument, got {readArguments.Count}",
                                    span,
                                    "TypeError"
                                );
                            }

                            var count = -1;
                            if (readArguments.Count == 1)
                            {
                                count = readArguments[0] switch
                                {
                                    PythonNoneValue => -1,
                                    PythonWholeNumberValue size => size.Value.Sign < 0
                                        ? -1
                                        : (int)BigInteger.Min(size.Value, int.MaxValue),
                                    var other => throw Fault(
                                        "DPY4037",
                                        $"argument should be integer or None, not '{GetTypeName(other)}'",
                                        span,
                                        "TypeError"
                                    ),
                                };
                            }

                            return new PythonTextValue(readFile.Read(count));
                        }
                    )
                );
            case PythonFileValue readLineFile when name == "readline":
                return new PythonBoundMethodValue(
                    "readline",
                    readLineFile,
                    new PythonProtocolFunctionValue(
                        "readline",
                        (_, _) =>
                        {
                            EnsureFileOpen(readLineFile, span);
                            return new PythonTextValue(readLineFile.ReadLine() ?? string.Empty);
                        }
                    )
                );
            case PythonFileValue readLinesFile when name == "readlines":
                return new PythonBoundMethodValue(
                    "readlines",
                    readLinesFile,
                    new PythonProtocolFunctionValue(
                        "readlines",
                        (_, _) =>
                        {
                            EnsureFileOpen(readLinesFile, span);
                            var lines = new List<PythonValue>();
                            while (readLinesFile.ReadLine() is { } line)
                            {
                                lines.Add(new PythonTextValue(line));
                            }

                            return new PythonListValue(lines);
                        }
                    )
                );
            case PythonFileValue:
                throw Fault(
                    "DPY4022",
                    $"'_io.TextIOWrapper' object has no attribute '{name}'",
                    span,
                    "AttributeError"
                );
            case PythonTemplateValue template when name == "strings":
                return new PythonTupleValue([
                    .. template.Strings.Select(text => (PythonValue)new PythonTextValue(text)),
                ]);
            case PythonTemplateValue template when name == "interpolations":
                return new PythonTupleValue([.. template.Interpolations.Cast<PythonValue>()]);
            case PythonTemplateValue template when name == "values":
                return new PythonTupleValue([
                    .. template.Interpolations.Select(interpolation => interpolation.Value),
                ]);
            case PythonTemplateValue:
                throw Fault(
                    "DPY4023",
                    $"'Template' object has no attribute '{name}'",
                    span,
                    "AttributeError"
                );
            case PythonGeneratorValue { IsAsyncGenerator: true } asyncGenerator:
                return name switch
                {
                    "asend" => AsyncGeneratorMethod(
                        asyncGenerator,
                        "asend",
                        (arguments, methodSpan) =>
                            new PythonAsyncGeneratorStepValue(
                                asyncGenerator,
                                PythonAsyncGeneratorStepKind.Send,
                                RequireSingleArgument("asend", arguments, methodSpan),
                                null
                            )
                    ),
                    "athrow" => AsyncGeneratorMethod(
                        asyncGenerator,
                        "athrow",
                        (arguments, methodSpan) =>
                            new PythonAsyncGeneratorStepValue(
                                asyncGenerator,
                                PythonAsyncGeneratorStepKind.Throw,
                                null,
                                RequireSingleArgument("athrow", arguments, methodSpan)
                            )
                    ),
                    "aclose" => AsyncGeneratorMethod(
                        asyncGenerator,
                        "aclose",
                        (_, _) =>
                            new PythonAsyncGeneratorStepValue(
                                asyncGenerator,
                                PythonAsyncGeneratorStepKind.Close,
                                null,
                                null
                            )
                    ),
                    "__anext__" => AsyncGeneratorMethod(
                        asyncGenerator,
                        "__anext__",
                        (_, _) =>
                            new PythonAsyncGeneratorStepValue(
                                asyncGenerator,
                                PythonAsyncGeneratorStepKind.Next,
                                null,
                                null
                            )
                    ),
                    "__aiter__" => new PythonBoundMethodValue(
                        "__aiter__",
                        asyncGenerator,
                        new PythonProtocolFunctionValue("__aiter__", (_, _) => asyncGenerator)
                    ),
                    _ => throw Fault(
                        "DPY4023",
                        $"'{asyncGenerator.TypeName}' object has no attribute '{name}'",
                        span,
                        "AttributeError"
                    ),
                };
            case PythonGeneratorValue generator:
                return name switch
                {
                    "send" => new PythonBoundMethodValue(
                        "send",
                        generator,
                        new PythonProtocolFunctionValue(
                            "send",
                            (_, arguments) => SendToGenerator(generator, arguments, span)
                        )
                    ),
                    "throw" => new PythonBoundMethodValue(
                        "throw",
                        generator,
                        new PythonProtocolFunctionValue(
                            "throw",
                            (_, arguments) => ThrowIntoGenerator(generator, arguments, span)
                        )
                    ),
                    "close" => new PythonBoundMethodValue(
                        "close",
                        generator,
                        new PythonProtocolFunctionValue(
                            "close",
                            (_, _) => CloseGenerator(generator, span)
                        )
                    ),
                    // `__next__` and `__iter__` are the iterator slots a generator answers.
                    "__next__" or "__iter__" => new PythonBoundMethodValue(
                        name,
                        generator,
                        new PythonProtocolFunctionValue(
                            name,
                            (_, arguments) =>
                                name == "__iter__"
                                    ? generator
                                    : SendToGenerator(generator, arguments, span)
                        )
                    )
                    {
                        IsWrapper = true,
                    },
                    _ => throw Fault(
                        "DPY4023",
                        $"'{generator.TypeName}' object has no attribute '{name}'",
                        span,
                        "AttributeError"
                    ),
                };
            case PythonInterpolationValue interpolation:
                return name switch
                {
                    "value" => interpolation.Value,
                    "expression" => new PythonTextValue(interpolation.Expression),
                    "conversion" => interpolation.Conversion is { } conversion
                        ? new PythonTextValue(conversion.ToString())
                        : PythonNoneValue.Instance,
                    "format_spec" => new PythonTextValue(interpolation.FormatSpecification),
                    _ => throw Fault(
                        "DPY4023",
                        $"'Interpolation' object has no attribute '{name}'",
                        span,
                        "AttributeError"
                    ),
                };
            case var subclassSource
                when name == "__subclasshook__" && subclassSource is not PythonBuiltinTypeValue:
                // Every value answers the hook, bound to its type rather than to itself. A
                // builtin type object answers it through its own chain, under its own name.
                return PythonPickleProtocols.SubclassHook(
                    PythonBuiltinTypes.GetRuntimeType(subclassSource),
                    PythonBuiltinTypes.GetRuntimeTypeName(subclassSource)
                );
            case var builtin when PythonBuiltinMethods.SupportsMethods(builtin):
                if (PythonBuiltinMethods.TryGet(builtin, name, out var method))
                {
                    return new PythonBoundMethodValue(name, builtin, method);
                }

                var runtimeName = PythonBuiltinTypes.GetRuntimeTypeName(builtin);
                if (name == "__hash__" && PythonSlotMethods.HasNoneHash(runtimeName))
                {
                    return PythonNoneValue.Instance;
                }

                if (
                    PythonSlotMethods.TryGetForValue(
                        runtimeName,
                        name,
                        out var slotFunction,
                        out var slotIsWrapper
                    )
                )
                {
                    return new PythonBoundMethodValue(name, builtin, slotFunction)
                    {
                        IsWrapper = slotIsWrapper,
                    };
                }

                if (
                    PythonObjectMembers.TryGetValueMember(
                        builtin,
                        runtimeName,
                        name,
                        out var objectValueMember,
                        out var objectValueIsWrapper
                    )
                )
                {
                    return new PythonBoundMethodValue(name, builtin, objectValueMember)
                    {
                        IsWrapper = objectValueIsWrapper,
                    };
                }

                // The numeric members are answered from the value itself, so they never
                // reach a method table: `(5).real` is `5` and `(1.5).imag` is `0.0`.
                if (
                    PythonIntMethods.TryGetMember(builtin, name, out var member)
                    || PythonFloatMethods.TryGetMember(builtin, name, out member)
                )
                {
                    return member;
                }

                // This case serves both a value and a class, so only a class reached here
                // reports in CPython's `type object 'X'` form.
                throw builtin is PythonBuiltinTypeValue builtinTarget
                    ? MissingTypeAttribute(builtinTarget.Name, name, span)
                    : Fault(
                        "DPY4023",
                        $"'{GetTypeName(builtin)}' object has no attribute '{name}'",
                        span,
                        "AttributeError"
                    );
            // An unhashable type answers `__hash__` with the value None.
            case PythonBuiltinTypeValue builtinType
                when name == "__hash__" && PythonSlotMethods.HasNoneHash(builtinType.Name):
                return PythonNoneValue.Instance;
            case PythonBuiltinTypeValue builtinType
                when PythonSlotMethods.GetDescriptor(builtinType.Name, name) is { } slot:
                return slot;
            case PythonBuiltinTypeValue builtinType
                when PythonMemberDescriptors.Get(builtinType.Name, name) is { } numericMember:
                return numericMember;
            case PythonBuiltinTypeValue builtinType
                when PythonMemoryViewMethods.GetTypeDescriptor(builtinType.Name, name)
                    is { } viewMethod:
                return viewMethod;
            case PythonBuiltinTypeValue builtinType
                when PythonDequeMethods.GetTypeDescriptor(builtinType.Name, name)
                    is { } dequeMethod:
                return dequeMethod;
            // What the type inherits from `object`, and the two entry points it carries.
            case PythonBuiltinTypeValue builtinType
                when PythonObjectMembers.GetTypeDescriptor(builtinType.Name, name)
                    is { } objectMember:
                return objectMember;
            case PythonBuiltinTypeValue builtinType
                when PythonObjectMembers.GetTypeFunction(builtinType.Name, name)
                    is { } typeFunction:
                return typeFunction with { BoundTo = builtinType };
            case PythonBuiltinTypeValue builtinType
                when PythonBuiltinMethods.GetTypeMemberDescriptor(builtinType.Name, name)
                    is { } descriptor:
                return descriptor;
            case PythonBuiltinTypeValue builtinType:
                throw MissingTypeAttribute(builtinType.Name, name, span);
            case PythonMemoryViewValue view:
                if (PythonMemoryViewMethods.GetAttribute(view, name, span) is { } viewMember)
                    return viewMember;
                if (
                    PythonSlotMethods.TryGetForValue(
                        "memoryview",
                        name,
                        out var viewSlot,
                        out var viewSlotIsWrapper
                    )
                )
                    return new PythonBoundMethodValue(name, view, viewSlot)
                    {
                        IsWrapper = viewSlotIsWrapper,
                    };
                if (
                    PythonObjectMembers.TryGetValueMember(
                        view,
                        "memoryview",
                        name,
                        out var objectViewMember,
                        out var objectViewIsWrapper
                    )
                )
                    return new PythonBoundMethodValue(name, view, objectViewMember)
                    {
                        IsWrapper = objectViewIsWrapper,
                    };
                throw Fault(
                    "DPY4023",
                    $"'memoryview' object has no attribute '{name}'",
                    span,
                    "AttributeError"
                );
            case PythonDequeValue deque:
                // An unhashable type answers `__hash__` with the value None.
                if (name == "__hash__")
                    return PythonNoneValue.Instance;
                if (PythonDequeMethods.GetAttribute(deque, name, span) is { } dequeMember)
                    return dequeMember;
                if (
                    PythonSlotMethods.TryGetForValue(
                        "deque",
                        name,
                        out var dequeSlot,
                        out var dequeSlotIsWrapper
                    )
                )
                    return new PythonBoundMethodValue(name, deque, dequeSlot)
                    {
                        IsWrapper = dequeSlotIsWrapper,
                    };
                if (
                    PythonObjectMembers.TryGetValueMember(
                        deque,
                        "deque",
                        name,
                        out var dequeObjectMember,
                        out var dequeObjectIsWrapper
                    )
                )
                    return new PythonBoundMethodValue(name, deque, dequeObjectMember)
                    {
                        IsWrapper = dequeObjectIsWrapper,
                    };
                throw Fault(
                    "DPY4023",
                    $"'deque' object has no attribute '{name}'",
                    span,
                    "AttributeError"
                );
            case PythonRangeValue range:
                // A range answers its bounds as members, its protocol through the slots its
                // type publishes, and `object`'s members after those.
                if (PythonMemberDescriptors.Get("range", name) is { } rangeMember)
                    return rangeMember.Get(range);
                if (
                    PythonSlotMethods.TryGetForValue(
                        "range",
                        name,
                        out var rangeSlot,
                        out var rangeSlotIsWrapper
                    )
                )
                    return new PythonBoundMethodValue(name, range, rangeSlot)
                    {
                        IsWrapper = rangeSlotIsWrapper,
                    };
                if (
                    PythonObjectMembers.TryGetValueMember(
                        range,
                        "range",
                        name,
                        out var rangeObjectMember,
                        out var rangeObjectIsWrapper
                    )
                )
                    return new PythonBoundMethodValue(name, range, rangeObjectMember)
                    {
                        IsWrapper = rangeObjectIsWrapper,
                    };
                throw Fault(
                    "DPY4023",
                    $"'range' object has no attribute '{name}'",
                    span,
                    "AttributeError"
                );
            case PythonMethodDescriptorValue methodDescriptor:
                return methodDescriptor.GetAttribute(name, span);
            case PythonMemberDescriptorValue memberDescriptor:
                return memberDescriptor.GetAttribute(name, span);
            default:
                // `object`'s members answer for any value, including the ones without a
                // method table of their own.
                if (
                    PythonObjectMembers.TryGetValueMember(
                        target,
                        PythonBuiltinTypes.GetRuntimeTypeName(target),
                        name,
                        out var objectMemberForValue,
                        out var objectValueForValueIsWrapper
                    )
                )
                {
                    return new PythonBoundMethodValue(name, target, objectMemberForValue)
                    {
                        IsWrapper = objectValueForValueIsWrapper,
                    };
                }

                throw Fault(
                    "DPY4023",
                    $"'{GetTypeName(target)}' object has no attribute '{name}'",
                    span,
                    "AttributeError"
                );
        }
    }

    /// <summary>`sys.stdout`/`sys.stderr`/`sys.stdin` methods bound to the executing VM's streams.</summary>
    private static PythonValue GetStreamAttribute(
        PythonStreamValue stream,
        string name,
        TextSpan span
    )
    {
        PythonRuntimeException NoDispatcher() =>
            Fault(
                "DPY4016",
                "The standard streams are only available while a program is running.",
                span,
                "RuntimeError"
            );

        switch (name)
        {
            case "name":
                return new PythonTextValue(stream.Name);
            case "encoding":
                return new PythonTextValue("utf-8");
            case "closed":
                return PythonTruthValue.False;
            case "write" when stream.Kind != PythonStreamKind.StandardInput:
                return StreamMethod(
                    stream,
                    "write",
                    arguments =>
                    {
                        if (arguments.Count != 1 || arguments[0] is not PythonTextValue text)
                        {
                            throw Fault(
                                "DPY4003",
                                $"write() argument must be str, not {(arguments.Count == 1 ? GetTypeName(arguments[0]) : "missing")}",
                                span,
                                "TypeError"
                            );
                        }

                        var dispatcher = UserObjectProtocols.Dispatcher ?? throw NoDispatcher();
                        (
                            stream.Kind == PythonStreamKind.StandardError
                                ? dispatcher.StandardError
                                : dispatcher.StandardOutput
                        ).Write(text.Value);
                        return PythonWholeNumberValue.Create(
                            PythonTextTraversal.Count(text.Value, span)
                        );
                    }
                );
            case "flush":
                return StreamMethod(
                    stream,
                    "flush",
                    _ =>
                    {
                        var dispatcher = UserObjectProtocols.Dispatcher ?? throw NoDispatcher();
                        if (stream.Kind == PythonStreamKind.StandardOutput)
                        {
                            dispatcher.StandardOutput.Flush();
                        }
                        else if (stream.Kind == PythonStreamKind.StandardError)
                        {
                            dispatcher.StandardError.Flush();
                        }

                        return PythonNoneValue.Instance;
                    }
                );
            case "readline" when stream.Kind == PythonStreamKind.StandardInput:
                return StreamMethod(
                    stream,
                    "readline",
                    _ =>
                    {
                        var dispatcher = UserObjectProtocols.Dispatcher ?? throw NoDispatcher();
                        var line = dispatcher.StandardInput?.ReadLine();
                        return new PythonTextValue(line is null ? string.Empty : line + "\n");
                    }
                );
            case "read" when stream.Kind == PythonStreamKind.StandardInput:
                return StreamMethod(
                    stream,
                    "read",
                    _ =>
                    {
                        var dispatcher = UserObjectProtocols.Dispatcher ?? throw NoDispatcher();
                        return new PythonTextValue(
                            dispatcher.StandardInput?.ReadToEnd() ?? string.Empty
                        );
                    }
                );
            case "readlines" when stream.Kind == PythonStreamKind.StandardInput:
                return StreamMethod(
                    stream,
                    "readlines",
                    _ =>
                    {
                        var dispatcher = UserObjectProtocols.Dispatcher ?? throw NoDispatcher();
                        var lines = new List<PythonValue>();
                        while (dispatcher.StandardInput?.ReadLine() is { } line)
                        {
                            lines.Add(new PythonTextValue(line + "\n"));
                        }

                        return new PythonListValue(lines);
                    }
                );
            default:
                throw Fault(
                    "DPY4023",
                    $"'_io.TextIOWrapper' object has no attribute '{name}'",
                    span,
                    "AttributeError"
                );
        }
    }

    private static PythonBoundMethodValue StreamMethod(
        PythonStreamValue stream,
        string name,
        Func<IReadOnlyList<PythonValue>, PythonValue> implementation
    ) =>
        new(
            name,
            stream,
            new PythonProtocolFunctionValue(name, (_, arguments) => implementation(arguments))
        );

    /// <summary>`_tuplegetter.__set__`: a field of a named tuple cannot be written.</summary>
    internal static PythonRuntimeException TupleFieldNotWritable(TextSpan span) =>
        Fault("DPY4003", "can't set attribute", span, "AttributeError");

    internal static void SetAttribute(
        PythonValue target,
        string name,
        PythonValue value,
        TextSpan span = default
    )
    {
        if (
            target is PythonManagedTypeValue
            && TryGetSpecialMethod(target, "__setattr__", out var setter)
        )
        {
            UserObjectProtocols.Dispatcher!.Invoke(
                setter,
                [new PythonTextValue(name), value],
                span
            );
            return;
        }
        SetAttributeCore(target, name, value, span);
    }

    internal static void SetAttributeCore(
        PythonValue target,
        string name,
        PythonValue value,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        RejectBuiltinTypeMutation(target, name, "set", span);
        RejectReadOnlyNumberMember(target, name, span);
        if (
            TryGetMetaclassAttribute(target, name, out _, out var metaclassDescriptor)
            && TrySetDescriptor(metaclassDescriptor, target, name, value, span)
        )
            return;

        switch (target)
        {
            case PythonFunctionValue function when name == "__dict__":
                function.Attributes = new PythonAttributeDictionary(
                    RequireNamespaceDictionary(value, span)
                );
                return;
            // `functools.update_wrapper` copies the wrapped callable's identity onto
            // the wrapper; CPython assigns the real slots, so the copy shadows the
            // wrapper's own metadata and never lands in its `__dict__`.
            case PythonFunctionValue function when IsFunctionShadowableName(name):
                function.ShadowAttributes ??= new PythonAttributeDictionary();
                function.ShadowAttributes[name] = value;
                return;
            case PythonFunctionValue function when !IsFunctionMetadataName(name):
                function.Attributes[name] = value;
                return;
            case PythonMappingProxyValue:
                throw MissingAttribute("mappingproxy", name, span);

            case PythonModuleValue when name == "__dict__":
                throw Fault("DPY4022", "readonly attribute", span, "AttributeError");
            case PythonModuleValue module:
                module.Globals.SetValue(name, value);
                return;
            case PythonExceptionValue { ManagedType: { } exceptionClass } exception
                when TryGetTypeAttribute(exceptionClass, name, out var descriptor)
                    && TrySetDescriptor(descriptor, exception, name, value, span):
                return;
            case PythonExceptionValue { ManagedType: null } unicodeException
                when PythonUnicodeErrors.TrySet(unicodeException, name, value, span):
                return;
            case PythonExceptionValue exceptionInstance when name == "__dict__":
                exceptionInstance.Attributes = new PythonAttributeDictionary(
                    RequireNamespaceDictionary(value, span)
                );
                exceptionInstance.HasInstanceDictionary = true;
                return;
            case PythonExceptionValue exceptionInstance when name == "args":
                var arguments =
                    value as PythonTupleValue
                    ?? new PythonTupleValue([.. MaterializeValues(value, span)]);
                exceptionInstance.AssignArguments(arguments);
                if (
                    exceptionInstance.GroupExceptions is null
                    && exceptionInstance.UnicodeErrorState is null
                )
                {
                    exceptionInstance.Message = arguments.Elements.Length switch
                    {
                        0 => string.Empty,
                        1
                            when PythonBuiltinTypes
                                .GetMro(PythonBuiltinTypes.GetRuntimeType(exceptionInstance))
                                .Elements.Any(entry =>
                                    entry is PythonExceptionTypeValue { Name: "KeyError" }
                                ) => arguments.Elements[0].ToRepresentationString(),
                        1 => arguments.Elements[0].ToDisplayString(),
                        _ => arguments.ToDisplayString(),
                    };
                }
                return;
            case PythonExceptionValue exceptionInstance when name is "__cause__" or "__context__":
                if (value is not (PythonExceptionValue or PythonNoneValue))
                    throw Fault(
                        "DPY4003",
                        name == "__cause__"
                            ? "exception cause must be None or derive from BaseException"
                            : "exception context must be None or derive from BaseException",
                        span,
                        "TypeError"
                    );
                if (name == "__cause__")
                {
                    exceptionInstance.Cause = value as PythonExceptionValue;
                    exceptionInstance.SuppressContext = true;
                }
                else
                    exceptionInstance.Context = value as PythonExceptionValue;
                return;
            case PythonExceptionValue exceptionInstance when name == "__suppress_context__":
                if (value is not PythonTruthValue truth)
                    throw Fault("DPY4003", "attribute value type must be bool", span, "TypeError");
                exceptionInstance.SuppressContext = truth.Value;
                return;
            case PythonExceptionValue exceptionInstance:
                exceptionInstance.HasInstanceDictionary = true;
                exceptionInstance.Attributes[name] = value;
                return;
            case PythonManagedObjectValue instance:
                if (UserObjectProtocols.TrySetAttribute(instance, name, value, span))
                {
                    return;
                }

                SetInstanceAttribute(instance, name, value, span);
                return;
            case PythonManagedTypeValue type:
                type.Attributes[name] = value;
                if (name == "__module__")
                {
                    type.Module = value is PythonTextValue moduleText ? moduleText.Value : null;
                }
                return;
            default:
                throw Fault(
                    "DPY4023",
                    "This value does not expose writable managed attributes.",
                    span,
                    "AttributeError"
                );
        }
    }

    private static PythonDictionaryValue RequireNamespaceDictionary(
        PythonValue value,
        TextSpan span
    ) =>
        value as PythonDictionaryValue
        ?? throw Fault(
            "DPY4003",
            $"__dict__ must be set to a dictionary, not a '{GetTypeName(value)}'",
            span,
            "TypeError"
        );

    // The metadata names `functools.update_wrapper` assigns by default. CPython
    // writes each one to a writable function slot, so the assignment shadows the
    // value the function would otherwise report.
    private static bool IsFunctionShadowableName(string name) =>
        name
            is "__name__"
                or "__qualname__"
                or "__module__"
                or "__doc__"
                or "__type_params__"
                or "__annotate__";

    // Function runtime metadata keeps its existing dedicated attribute behavior;
    // arbitrary user state belongs to the function dictionary instead.
    private static bool IsFunctionMetadataName(string name) =>
        name
            is "__class__"
                or "__name__"
                or "__qualname__"
                or "__module__"
                or "__doc__"
                or "__annotations__"
                or "__annotate__"
                or "__defaults__"
                or "__kwdefaults__"
                or "__code__"
                or "__globals__"
                or "__closure__"
                or "__builtins__"
                or "__type_params__";

    /// <summary>
    /// The default attribute lookup for a managed instance (data descriptors, the
    /// instance dictionary, then bound type attributes) without `__getattr__` hooks.
    /// </summary>
    /// <summary>The class dictionary entry a class body stores its annotate callable in.</summary>
    private const string AnnotateClassDictName = "__annotate_func__";

    /// <summary>The class dictionary entry holding the evaluated annotations.</summary>
    private const string AnnotationsCacheName = "__annotations_cache__";

    /// <summary>The value formats the compiled annotation bodies accept.</summary>
    private const int ValueAnnotationFormat = 1;

    /// <summary>
    /// A class's `__annotations__`, evaluated from the class's own annotate callable
    /// on first access and cached. Bases are never consulted, so a subclass that
    /// declares no annotations reports an empty mapping.
    /// </summary>
    internal static PythonDictionaryValue GetTypeAnnotations(
        PythonManagedTypeValue type,
        TextSpan span
    )
    {
        if (
            type.Attributes.TryGetValue(AnnotationsCacheName, out var cached)
            && cached is PythonDictionaryValue cache
        )
        {
            return cache;
        }

        var evaluated = null as PythonValue;
        if (type.Attributes.TryGetValue(AnnotateClassDictName, out var annotate))
        {
            // The class body's own annotations resolve against the class dictionary,
            // which is the namespace object the body built.
            if (annotate is PythonFunctionValue annotateFunction)
            {
                annotateFunction.ClassNamespace ??= type.Attributes.Dictionary;
            }

            evaluated = UserObjectProtocols.Dispatcher?.Invoke(
                annotate,
                [PythonWholeNumberValue.Create(ValueAnnotationFormat)],
                span
            );
        }
        var annotations = evaluated as PythonDictionaryValue ?? new PythonDictionaryValue([]);
        type.Attributes[AnnotationsCacheName] = annotations;
        return annotations;
    }

    /// <summary>
    /// A module's `__annotations__`, evaluated from its `__annotate__` global on first
    /// access. The module dictionary is the global namespace, so the result becomes an
    /// ordinary module attribute once computed.
    /// </summary>
    internal static PythonDictionaryValue GetModuleAnnotations(
        PythonModuleValue module,
        TextSpan span
    )
    {
        if (module.Globals.TryGetValue("__annotations__", out var existing))
        {
            return existing as PythonDictionaryValue ?? new PythonDictionaryValue([]);
        }

        var evaluated =
            module.Globals.TryGetValue("__annotate__", out var annotate)
            && annotate is not PythonNoneValue
                ? UserObjectProtocols.Dispatcher?.Invoke(
                    annotate,
                    [PythonWholeNumberValue.Create(ValueAnnotationFormat)],
                    span
                )
                : null;
        var annotations = evaluated as PythonDictionaryValue ?? new PythonDictionaryValue([]);
        module.Globals.SetValue("__annotations__", annotations);
        return annotations;
    }

    /// <summary>
    /// The function's `__annotations__`: evaluated on first access from the annotate
    /// callable and then cached, so later reads hand back the same mapping. A
    /// definition with no annotations reports an empty mapping, never a missing
    /// attribute. A failure during evaluation propagates and is not cached.
    /// </summary>
    internal static PythonDictionaryValue GetFunctionAnnotations(
        PythonFunctionValue function,
        TextSpan span
    )
    {
        if (function.Annotations is { } cached)
        {
            return cached;
        }

        var evaluated = function.Annotate is { } annotate
            ? UserObjectProtocols.Dispatcher?.Invoke(
                annotate,
                [PythonWholeNumberValue.Create(ValueAnnotationFormat)],
                span
            )
            : null;
        function.Annotations = evaluated as PythonDictionaryValue ?? new PythonDictionaryValue([]);
        return function.Annotations;
    }

    /// <summary>
    /// Whether an instance exposes a `__dict__`. False only for a class whose
    /// `__slots__` declaration omits it and whose bases provide none.
    /// </summary>
    private static bool AllowsInstanceDictionary(PythonManagedObjectValue instance) =>
        instance.Type.Slots is not { } layout || layout.AllowsInstanceDictionary;

    /// <summary>
    /// Whether a class along the instance's resolution order declares `__dict__` itself,
    /// which is what `class C: __dict__ = 42` does. A builtin's generic entry — the one
    /// `dict` and `object` contribute — leaves the instance dictionary in charge.
    /// </summary>
    private static bool DeclaresDictionary(PythonManagedTypeValue type)
    {
        foreach (var entry in type.ResolutionOrder ?? [.. type.Mro])
        {
            if (TryGetOwnTypeAttribute(entry, "__dict__", out _))
                return entry is PythonManagedTypeValue;
        }
        return false;
    }

    internal static bool TryGetInstanceAttribute(
        PythonManagedObjectValue instance,
        string name,
        TextSpan span,
        out PythonValue value
    )
    {
        var hasTypeValue = TryGetTypeAttribute(instance.Type, name, out var typeValue);

        if (hasTypeValue && IsDataDescriptor(typeValue) && HasDescriptorGetter(typeValue))
        {
            value = BindDescriptor(typeValue, instance, instance.Type, span, name);
            return true;
        }

        // A value that carries an instance dictionary answers `__dict__` with it. A class
        // that declares its own `__dict__` — `class C: __dict__ = 42` — answers instead,
        // while the generic entry a builtin contributes does not.
        if (
            name == "__dict__"
            && AllowsInstanceDictionary(instance)
            && !DeclaresDictionary(instance.Type)
        )
        {
            value = instance.Attributes.Dictionary;
            return true;
        }

        if (instance.Attributes.TryGetValue(name, out value!))
        {
            return true;
        }

        // The generic `__dict__` a builtin contributes is not an instance's mapping, so a
        // value that carries no dictionary and declares none answers AttributeError.
        if (
            name == "__dict__"
            && !DeclaresDictionary(instance.Type)
            && !AllowsInstanceDictionary(instance)
        )
        {
            value = null!;
            return false;
        }

        if (hasTypeValue)
        {
            // `object.__class__` reports the instance's own class; the entry a builtin base
            // contributes is not the class of a subclass instance, so it is answered here
            // rather than bound — a `__class__` a class declares for itself still wins below,
            // and one the instance carries in its dictionary has already won above.
            value =
                name == "__class__" && !DeclaresClassEntry(instance.Type)
                    ? instance.Type
                    : BindDescriptor(typeValue, instance, instance.Type, span, name);
            return true;
        }

        if (name == "__class__")
        {
            value = instance.Type;
            return true;
        }

        return false;
    }

    /// <summary>Whether a class or one of its bases declares `__class__` for itself.</summary>
    private static bool DeclaresClassEntry(PythonManagedTypeValue type) =>
        type.Mro.Any(entry => entry.Attributes.TryGetValue("__class__", out _));

    internal static PythonValue GetInstanceAttribute(
        PythonManagedObjectValue instance,
        string name,
        TextSpan span
    ) =>
        TryGetInstanceAttribute(instance, name, span, out var value)
            ? value
            : throw MissingAttribute(instance.Type.ReportedName, name, span);

    /// <summary>`object.__setattr__`: data descriptors and properties, then the instance dictionary.</summary>
    internal static void SetInstanceAttribute(
        PythonManagedObjectValue instance,
        string name,
        PythonValue value,
        TextSpan span
    )
    {
        if (
            TryGetTypeAttribute(instance.Type, name, out var typeValue)
            && TrySetDescriptor(typeValue, instance, name, value, span)
        )
        {
            return;
        }

        // A class that declares `__slots__` without `'__dict__'` accepts only its
        // declared members, which also rules out assigning `__dict__` itself.
        if (instance.Type.Slots is { } layout && !layout.Accepts(name))
        {
            throw Fault(
                "DPY4022",
                $"'{instance.Type.ReportedName}' object has no attribute '{name}' and no __dict__ for setting new attributes",
                span,
                "AttributeError"
            );
        }

        if (name == "__dict__" && !DeclaresDictionary(instance.Type))
        {
            instance.Attributes = new PythonAttributeDictionary(
                RequireNamespaceDictionary(value, span)
            );
            return;
        }

        instance.Attributes[name] = value;
    }

    /// <summary>`object.__delattr__`: property deleters, then the instance dictionary.</summary>
    internal static void DeleteInstanceAttribute(
        PythonManagedObjectValue instance,
        string name,
        TextSpan span
    )
    {
        if (
            TryGetTypeAttribute(instance.Type, name, out var typeValue)
            && TryDeleteDescriptor(typeValue, instance, name, span)
        )
        {
            return;
        }

        if (
            name == "__dict__"
            && !TryGetTypeAttribute(instance.Type, name, out _)
            && AllowsInstanceDictionary(instance)
        )
        {
            instance.Attributes = new PythonAttributeDictionary();
            return;
        }

        if (!instance.Attributes.Remove(name))
        {
            if (instance.Type.Slots is { AllowsInstanceDictionary: false } layout)
            {
                // A declared member that was never assigned reports the bare name,
                // while anything else reports the missing dictionary.
                throw layout.MemberNames.Contains(name)
                    ? Fault("DPY4022", name, span, "AttributeError")
                    : Fault(
                        "DPY4022",
                        $"'{instance.Type.ReportedName}' object has no attribute '{name}' and no __dict__ for setting new attributes",
                        span,
                        "AttributeError"
                    );
            }

            throw MissingAttribute(instance.Type.ReportedName, name, span);
        }
    }

    private static bool TrySetDescriptor(
        PythonValue descriptorValue,
        PythonValue instance,
        string name,
        PythonValue value,
        TextSpan span
    )
    {
        if (TryInvokeUserDescriptorMutation(descriptorValue, "__set__", [instance, value], span))
        {
            return true;
        }

        switch (descriptorValue)
        {
            case PythonTypeMetadataDescriptorValue descriptor:
                descriptor.Set(instance, value, span);
                return true;
            case PythonUnicodeErrorDescriptorValue descriptor:
                descriptor.Set(instance, value, span);
                return true;
            case PythonDescriptorValue { IsDataDescriptor: true, Set: null }:
                throw Fault("DPY4023", $"Attribute '{name}' is read-only.", span, "AttributeError");
            case PythonDescriptorValue { IsDataDescriptor: true } descriptor:
                descriptor.Set!(instance, value);
                return true;
            case PythonTupleGetterValue:
                throw TupleFieldNotWritable(span);
            case PythonPropertyValue { Setter: null }:
                throw Fault(
                    "DPY4023",
                    $"property '{name}' of '{GetTypeName(instance)}' object has no setter",
                    span,
                    "AttributeError"
                );
            case PythonPropertyValue property:
                UserObjectProtocols.Dispatcher!.Invoke(property.Setter, [instance, value], span);
                return true;
        }

        return false;
    }

    private static bool TryDeleteDescriptor(
        PythonValue descriptorValue,
        PythonValue instance,
        string name,
        TextSpan span
    )
    {
        if (TryInvokeUserDescriptorMutation(descriptorValue, "__delete__", [instance], span))
        {
            return true;
        }

        switch (descriptorValue)
        {
            case PythonTypeMetadataDescriptorValue descriptor:
                descriptor.Delete(instance, span);
                return true;
            case PythonUnicodeErrorDescriptorValue descriptor:
                descriptor.Delete(instance, span);
                return true;
            case PythonTupleGetterValue:
                throw Fault("DPY4003", "can't delete attribute", span, "AttributeError");
            case PythonDescriptorValue { IsDataDescriptor: true }:
                throw Fault(
                    "DPY4023",
                    $"Attribute '{name}' cannot be deleted.",
                    span,
                    "AttributeError"
                );
            case PythonPropertyValue { Deleter: null }:
                throw Fault(
                    "DPY4023",
                    $"property '{name}' of '{GetTypeName(instance)}' object has no deleter",
                    span,
                    "AttributeError"
                );
            case PythonPropertyValue property:
                UserObjectProtocols.Dispatcher!.Invoke(property.Deleter, [instance], span);
                return true;
        }

        return false;
    }

    private static PythonValue GetPropertyValue(
        PythonPropertyValue property,
        PythonValue instance,
        string name,
        TextSpan span
    )
    {
        if (property.Getter is null)
        {
            throw Fault(
                "DPY4023",
                $"property '{name}' of '{GetTypeName(instance)}' object has no getter",
                span,
                "AttributeError"
            );
        }

        return UserObjectProtocols.Dispatcher!.Invoke(property.Getter, [instance], span);
    }

    internal static void DeleteAttribute(PythonValue target, string name, TextSpan span = default)
    {
        if (
            target is PythonManagedTypeValue
            && TryGetSpecialMethod(target, "__delattr__", out var deleter)
        )
        {
            UserObjectProtocols.Dispatcher!.Invoke(deleter, [new PythonTextValue(name)], span);
            return;
        }
        DeleteAttributeCore(target, name, span);
    }

    internal static void DeleteAttributeCore(
        PythonValue target,
        string name,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        RejectBuiltinTypeMutation(target, name, "set", span);
        RejectReadOnlyNumberMember(target, name, span);
        if (
            TryGetMetaclassAttribute(target, name, out _, out var metaclassDescriptor)
            && TryDeleteDescriptor(metaclassDescriptor, target, name, span)
        )
            return;

        switch (target)
        {
            case PythonFunctionValue when name == "__dict__":
                throw Fault("DPY4023", "cannot delete __dict__", span, "TypeError");
            case PythonFunctionValue function
                when !IsFunctionMetadataName(name) && function.Attributes.Remove(name):
                return;
            case PythonFunctionValue:
                throw MissingAttribute("function", name, span);
            case PythonMappingProxyValue:
                throw MissingAttribute("mappingproxy", name, span);

            case PythonModuleValue when name == "__dict__":
                throw Fault("DPY4022", "readonly attribute", span, "AttributeError");
            case PythonExceptionValue { ManagedType: { } exceptionClass } exception
                when TryGetTypeAttribute(exceptionClass, name, out var descriptor)
                    && TryDeleteDescriptor(descriptor, exception, name, span):
                return;
            case PythonExceptionValue { ManagedType: null } unicodeException
                when PythonUnicodeErrors.TryDelete(unicodeException, name, span):
                return;
            case PythonExceptionValue when name == "__dict__":
                throw Fault("DPY4023", "cannot delete __dict__", span, "TypeError");
            case PythonExceptionValue when name == "args":
                throw Fault("DPY4023", "args may not be deleted", span, "TypeError");
            case PythonExceptionValue when name is "__cause__" or "__context__":
                throw Fault("DPY4003", name + " may not be deleted", span, "TypeError");
            case PythonExceptionValue when name == "__suppress_context__":
                throw Fault("DPY4003", "can't delete numeric/char attribute", span, "TypeError");
            case PythonModuleValue module when module.Globals.Remove(name):
                return;
            case PythonModuleValue module:
                throw Fault(
                    "DPY4022",
                    $"'module' object has no attribute '{name}'",
                    span,
                    "AttributeError"
                );
            case PythonExceptionValue exception when exception.Attributes.Remove(name):
                return;
            case PythonExceptionValue exception:
                throw MissingAttribute(exception.TypeName, name, span);
            case PythonManagedObjectValue instance:
                if (UserObjectProtocols.TryDeleteAttribute(instance, name, span))
                {
                    return;
                }

                DeleteInstanceAttribute(instance, name, span);
                return;
            case PythonManagedTypeValue type when name == "__annotations__":
                // Deleting clears the mapping rather than restoring the computed one:
                // a later read reports an empty mapping.
                type.Attributes[AnnotationsCacheName] = new PythonDictionaryValue([]);
                return;
            case PythonManagedTypeValue type when type.Attributes.Remove(name):
                return;
            case PythonManagedTypeValue type:
                throw MissingTypeAttribute(type.Name, name, span);
            default:
                throw Fault(
                    "DPY4023",
                    "This value does not expose deletable managed attributes.",
                    span,
                    "AttributeError"
                );
        }
    }

    /// <summary>A builtin function a type object handed out, which reports as its method.</summary>
    private static PythonBuiltinFunctionValue BindToType(
        PythonBuiltinFunctionValue function,
        PythonValue type
    ) => function with { BoundTo = type };

    /// <summary>
    /// A deque is indexed by a whole number: reading a slice is refused with the sequence
    /// wording, while assigning one reports the integer conversion CPython reports.
    /// </summary>
    private static PythonValue GetDequeItem(
        PythonDequeValue deque,
        PythonValue index,
        TextSpan span
    )
    {
        if (index is not PythonWholeNumberValue number)
            throw Fault(
                "DPY4003",
                $"sequence index must be integer, not '{GetTypeName(index)}'",
                span,
                "TypeError"
            );
        return deque.Elements[ResolveDequeIndex(deque, number.Value, span)];
    }

    private static int ResolveDequeIndex(PythonDequeValue deque, BigInteger index, TextSpan span)
    {
        var resolved = index;
        if (resolved < 0)
            resolved += deque.Elements.Count;
        if (resolved < 0 || resolved >= deque.Elements.Count)
            throw Fault("DPY4003", "deque index out of range", span, "IndexError");
        return (int)resolved;
    }

    private static int ResolveDequeAssignment(
        PythonDequeValue deque,
        PythonValue index,
        TextSpan span
    )
    {
        BigInteger value;
        if (index is PythonWholeNumberValue whole)
            value = whole.Value;
        else if (index is PythonTruthValue truth)
            value = truth.Value ? BigInteger.One : BigInteger.Zero;
        else
            throw Fault(
                "DPY4003",
                $"'{GetTypeName(index)}' object cannot be interpreted as an integer",
                span,
                "TypeError"
            );
        return ResolveDequeIndex(deque, value, span);
    }

    /// <summary>
    /// Binds a builtin's descriptor to whatever it works on: the storage a subclass instance
    /// carries for the builtin's own kind — `dict.get` reads the dict — and the instance
    /// itself for `object`'s members, which every object answers.
    /// </summary>
    private static PythonValue BindBuiltinDescriptor(
        PythonMethodDescriptorValue descriptor,
        PythonValue instance,
        PythonValue owner,
        TextSpan span
    )
    {
        var kind = PythonSubclassStorage.StorageKindOf(instance);
        // A slot the builtin answers through `object`, such as `__str__`, still works on the
        // storage; only `object`'s own instance protocol — `__getattribute__`, `__format__` —
        // works on the instance.
        var targetsStorage =
            kind is not null
            && (
                kind == descriptor.OwnerName
                // `object.__str__` is `repr(self)`, asked of the instance so that a class
                // which declares its own `__repr__` is the one that answers.
                || descriptor.Name != "__str__"
                    && PythonSlotMethods.IsObjectSlot(kind, descriptor.Name)
            );
        // The receiver stays the instance — a bound method reports the object it was read
        // from — while the call itself resolves the storage the method works on.
        var bound = descriptor.BindDescriptor(instance, owner, span);
        return bound is PythonBoundMethodValue method
            ? method with
            {
                TargetsStorage = targetsStorage,
            }
            : bound;
    }

    /// <summary>The length of a view is its current dimension.</summary>
    internal static int GetViewLength(PythonMemoryViewValue view)
    {
        PythonMemoryViewMethods.RequireLive(view);
        return view.Shape is { Length: > 0 } shape ? shape[0] : view.Length;
    }

    internal static int GetLength(PythonValue value, TextSpan span = default) =>
        value switch
        {
            PythonTextValue text => PythonTextTraversal.Count(text.Value, span),
            PythonByteSequenceValue bytes => bytes.Value.Length,
            PythonByteArrayValue mutable => mutable.Value.Length,
            PythonMemoryViewValue view => GetViewLength(view),
            PythonDequeValue deque => deque.Elements.Count,
            PythonListValue list => list.Elements.Count,
            PythonTupleValue tuple => tuple.Elements.Length,
            PythonMappingProxyValue proxy => GetLength(proxy.Mapping, span),
            PythonDictionaryValue dictionary => dictionary.Items.Count,
            PythonDictionaryViewValue view => view.Dictionary.Items.Count,
            PythonSetValue set => set.Elements.Count,
            PythonRangeValue range => range.Count <= int.MaxValue
                ? (int)range.Count
                : throw Fault(
                    "DPY4011",
                    "The range length exceeds the supported size.",
                    span,
                    "OverflowError"
                ),
            PythonExternalObjectValue external => external.Protocol.GetLength(span),
            PythonManagedTypeValue enumType => PythonEnum.TryGetTypeLength(
                enumType,
                out var enumLength,
                span
            )
                ? enumLength
                : throw Fault("DPY4011", "object of type 'type' has no len()", span, "TypeError"),
            PythonManagedObjectValue instance => UserObjectProtocols.TryGetLength(
                instance,
                span,
                out var userLength
            )
                ? userLength
                : throw Fault(
                    "DPY4011",
                    $"object of type '{instance.Type.ReportedName}' has no len()",
                    span,
                    "TypeError"
                ),
            _ => throw Fault("DPY4011", "This value has no managed length.", span, "TypeError"),
        };

    /// <summary>A StopIteration carrying the generator's return value (PEP 380).</summary>
    internal static PythonExceptionValue CreateStopIteration(PythonValue returnValue) =>
        returnValue is PythonNoneValue
            ? new PythonExceptionValue("StopIteration", string.Empty)
            : new PythonExceptionValue("StopIteration", returnValue.ToDisplayString())
            {
                Arguments = [returnValue],
            };

    /// <summary>`BaseException.__init__(self, *args)`: rebinds args and the message.</summary>
    internal static void ApplyBaseExceptionInit(
        PythonExceptionValue exception,
        IReadOnlyList<PythonValue> arguments
    )
    {
        exception.PreserveSpecializedArguments();
        exception.Arguments = [.. arguments];
        if (exception.GroupExceptions is not null || exception.UnicodeErrorState is not null)
            return;
        exception.Message = arguments.Count switch
        {
            0 => string.Empty,
            1
                when PythonBuiltinTypes
                    .GetMro(PythonBuiltinTypes.GetRuntimeType(exception))
                    .Elements.Any(entry =>
                        entry is PythonExceptionTypeValue { Name: "KeyError" }
                    ) => arguments[0].ToRepresentationString(),
            1 => arguments[0].ToDisplayString(),
            _ => new PythonTupleValue([.. arguments]).ToDisplayString(),
        };
    }

    /// <summary>A CPython-shaped KeyError carrying the missing key as its argument.</summary>
    internal static PythonRaisedException MissingKey(PythonValue key) =>
        new(
            new PythonExceptionValue("KeyError", key.ToRepresentationString()) { Arguments = [key] }
        );

    private static PythonRuntimeException ReusedCoroutineFault(TextSpan span) =>
        Fault("DPY4036", "cannot reuse already awaited coroutine", span, "RuntimeError");

    private static PythonBoundMethodValue AsyncGeneratorMethod(
        PythonGeneratorValue generator,
        string name,
        Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> createStep
    ) =>
        new(
            name,
            generator,
            new PythonProtocolFunctionValue(name, (_, arguments) => createStep(arguments, default))
        );

    private static PythonValue RequireSingleArgument(
        string name,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count != 1)
        {
            throw Fault(
                "DPY4003",
                $"{name}() takes exactly one argument ({arguments.Count} given).",
                span,
                "TypeError"
            );
        }

        return arguments[0];
    }

    private static PythonValue SendToGenerator(
        PythonGeneratorValue generator,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count != 1)
        {
            throw Fault(
                "DPY4003",
                $"send() takes exactly one argument ({arguments.Count} given).",
                span,
                "TypeError"
            );
        }

        if (generator.State == PythonGeneratorState.Created && arguments[0] is not PythonNoneValue)
        {
            throw Fault(
                "DPY4003",
                $"can't send non-None value to a just-started {generator.TypeName}",
                span,
                "TypeError"
            );
        }

        if (generator is { IsCoroutine: true, State: PythonGeneratorState.Completed })
        {
            throw ReusedCoroutineFault(span);
        }

        var advanced = generator.ResumeCore!(
            arguments[0] is PythonNoneValue ? null : arguments[0],
            null
        );
        if (advanced.HasValue)
        {
            return advanced.Value;
        }

        throw new PythonRaisedException(CreateStopIteration(advanced.Value));
    }

    private static PythonValue ThrowIntoGenerator(
        PythonGeneratorValue generator,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count != 1)
        {
            throw Fault(
                "DPY4003",
                $"throw() takes exactly one argument ({arguments.Count} given).",
                span,
                "TypeError"
            );
        }

        var advanced = UserObjectProtocols.Dispatcher!.ThrowGenerator(
            generator,
            arguments[0],
            span
        );
        if (advanced.HasValue)
        {
            return advanced.Value;
        }

        throw new PythonRaisedException(CreateStopIteration(advanced.Value));
    }

    private static PythonValue CloseGenerator(PythonGeneratorValue generator, TextSpan span)
    {
        if (generator.State is PythonGeneratorState.Created or PythonGeneratorState.Completed)
        {
            generator.State = PythonGeneratorState.Completed;
            return PythonNoneValue.Instance;
        }

        try
        {
            var advanced = UserObjectProtocols.Dispatcher!.ThrowGenerator(
                generator,
                new PythonExceptionValue("GeneratorExit", string.Empty),
                span
            );
            if (advanced.HasValue)
            {
                throw Fault(
                    "DPY4016",
                    generator.IsCoroutine
                        ? "coroutine ignored GeneratorExit"
                        : "Generator ignored GeneratorExit.",
                    span,
                    "RuntimeError"
                );
            }

            return generator.IsCoroutine ? PythonNoneValue.Instance : advanced.Value;
        }
        catch (PythonRaisedException raised)
            when (string.Equals(raised.Value.TypeName, "GeneratorExit", StringComparison.Ordinal))
        {
            return PythonNoneValue.Instance;
        }
    }

    internal static PythonIteratorValue GetIterator(
        PythonValue value,
        TextSpan span = default,
        UserIterationDispatcher? userIteration = null
    )
    {
        if (value is PythonIteratorValue iterator)
        {
            return iterator;
        }

        if (value is PythonManagedObjectValue instance)
        {
            if (userIteration is not null)
                return userIteration(instance, span);
            if (UserObjectProtocols.Dispatcher is { } dispatcher)
                return dispatcher.GetUserIterator(instance, span);
        }

        if (value is PythonGeneratorValue generatorValue)
        {
            if (generatorValue.IsCoroutine || generatorValue.IsAsyncGenerator)
            {
                throw Fault(
                    "DPY4003",
                    $"'{generatorValue.TypeName}' object is not iterable",
                    span,
                    "TypeError"
                );
            }

            // A generator is its own iterator.
            return new PythonIteratorValue(value, -1);
        }

        if (
            value is PythonManagedTypeValue enumType
            && PythonEnum.TryGetTypeIterator(enumType) is { } enumIterator
        )
        {
            return enumIterator;
        }

        if (value is PythonTemplateValue template)
        {
            var items = new List<PythonValue>();
            for (var index = 0; index < template.Strings.Length; index++)
            {
                if (template.Strings[index].Length != 0)
                {
                    items.Add(new PythonTextValue(template.Strings[index]));
                }

                if (index < template.Interpolations.Length)
                {
                    items.Add(template.Interpolations[index]);
                }
            }

            return new PythonIteratorValue(new PythonTupleValue([.. items]), -1);
        }

        if (value is PythonMappingProxyValue proxy)
        {
            return GetIterator(proxy.Mapping, span, userIteration);
        }
        if (value is PythonDictionaryViewValue view)
        {
            return new PythonIteratorValue(view, view.Dictionary.Items.Count);
        }

        if (value is PythonFileValue file)
        {
            // A file is its own line cursor; the wrapper defers to it on each step.
            EnsureFileOpen(file, span);
            return new PythonIteratorValue(file, -1);
        }

        if (value is PythonExternalObjectValue { Protocol: IPythonExternalIterable iterable })
        {
            return new PythonIteratorValue(new PythonListValue([.. iterable.IterationItems]), -1);
        }

        if (
            value
            is not (
                PythonListValue
                or PythonTupleValue
                or PythonDictionaryValue
                or PythonTextValue
                or PythonByteSequenceValue
                or PythonByteArrayValue
                or PythonMemoryViewValue
                or PythonDequeValue
                or PythonRangeValue
                or PythonSetValue
            )
        )
        {
            throw Fault(
                "DPY4015",
                $"'{GetTypeName(value)}' object is not iterable",
                span,
                "TypeError"
            );
        }

        var created = new PythonIteratorValue(
            value,
            value switch
            {
                PythonDictionaryValue dictionary => dictionary.Items.Count,
                PythonSetValue set => set.Elements.Count,
                _ => -1,
            }
        );
        if (value is PythonDequeValue deque)
            created.ObservedVersion = deque.Version;
        return created;
    }

    internal static void EnsureFileOpen(PythonFileValue file, TextSpan span)
    {
        if (file.IsClosed)
        {
            throw Fault("DPY4037", "I/O operation on closed file.", span, "ValueError");
        }
    }

    internal static bool TryGetNext(
        PythonIteratorValue iterator,
        out PythonValue value,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(iterator);
        iterator.StopIteration = null;
        if (iterator.IsExhausted)
        {
            value = PythonNoneValue.Instance;
            return false;
        }
        if (
            iterator.Iterable is not PythonSequenceIteratorSourceValue
            && PythonLengthHints.SupportsIterator(iterator)
        )
            UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
        switch (iterator.Iterable)
        {
            case PythonReverseIteratorSourceValue reverse:
                return PythonReverseIterators.TryGetNext(iterator, reverse, out value, span);
            case PythonFileValue file:
            {
                EnsureFileOpen(file, span);
                if (file.ReadLine() is { } line)
                {
                    value = new PythonTextValue(line);
                    return true;
                }

                break;
            }
            case PythonDequeValue deque:
                // CPython refuses to iterate a deque that changed under the iterator, even
                // when its length did not.
                if (iterator.ObservedVersion != deque.Version)
                {
                    iterator.IsExhausted = true;
                    throw Fault("DPY4003", "deque mutated during iteration", span, "RuntimeError");
                }
                if (iterator.Index < deque.Elements.Count)
                {
                    value = deque.Elements[iterator.Index++];
                    return true;
                }
                iterator.IsExhausted = true;
                break;
            case PythonListValue list when iterator.Index < list.Elements.Count:
                value = list.Elements[iterator.Index++];
                return true;
            case PythonTupleValue tuple when iterator.Index < tuple.Elements.Length:
                value = tuple.Elements[iterator.Index++];
                return true;
            case PythonListValue or PythonTupleValue:
                iterator.IsExhausted = true;
                break;
            case PythonDictionaryViewValue view:
                return TryGetDictionaryNext(iterator, view.Dictionary, view.Kind, out value, span);
            case PythonDictionaryValue dictionary:
                return TryGetDictionaryNext(iterator, dictionary, "dict_keys", out value, span);
            case PythonTextValue text:
            {
                if (iterator.TextOffset < text.Value.Length)
                {
                    var width = PythonTextTraversal.Width(text.Value, iterator.TextOffset);
                    value = new PythonTextValue(text.Value.Substring(iterator.TextOffset, width));
                    iterator.TextOffset += width;
                    iterator.Index++;
                    return true;
                }
                iterator.IsExhausted = true;
                break;
            }
            case PythonByteSequenceValue bytes when iterator.Index < bytes.Value.Length:
                value = PythonWholeNumberValue.Create(bytes.Value[iterator.Index++]);
                return true;
            case PythonByteSequenceValue:
                iterator.IsExhausted = true;
                break;
            // A view iterates the elements it exposes, whatever its format reads them as;
            // an array of more than one dimension has no flat elements to hand out.
            case PythonMemoryViewValue { Shape.Length: > 1 } view:
                PythonMemoryViewMethods.RequireLive(view);
                throw Fault(
                    "DPY4003",
                    "multi-dimensional sub-views are not implemented",
                    span,
                    "NotImplementedError"
                );
            case PythonMemoryViewValue view when iterator.Index < GetViewLength(view):
                value = view.Read(iterator.Index++);
                return true;
            case PythonMemoryViewValue:
                iterator.IsExhausted = true;
                break;
            // A bytearray iterates its live contents, so shrinking it during iteration stops
            // the iterator rather than reading past the end.
            case PythonByteArrayValue mutable when iterator.Index < mutable.Value.Length:
                value = PythonWholeNumberValue.Create(mutable.Value[iterator.Index++]);
                return true;
            case PythonByteArrayValue:
                iterator.IsExhausted = true;
                break;
            case PythonSetValue set:
                ValidateIteratorSize(iterator, set.Elements.Count, "Set", span);
                if (iterator.Index < set.Elements.Count)
                {
                    value = set.Elements[iterator.Index++];
                    return true;
                }
                iterator.IsExhausted = true;
                break;
            case PythonRangeValue range:
            {
                var current = range.Start + range.Step * iterator.RangeIndex;
                if (range.Step > 0 ? current < range.Stop : current > range.Stop)
                {
                    iterator.RangeIndex++;
                    value = PythonWholeNumberValue.Create(current);
                    return true;
                }
                iterator.IsExhausted = true;
                break;
            }
            // A view of a mapping the mixin iterates: the key comes from the mapping's own
            // walk and the value is read as the element is produced.
            case PythonMappingViewSourceValue viewSource:
            {
                if (!TryGetNext(viewSource.Inner, out var key, span))
                {
                    iterator.StopIteration = viewSource.Inner.StopIteration;
                    iterator.IsExhausted = true;
                    break;
                }
                var item = GetItem(viewSource.Mapping, key, span);
                value = viewSource.Items ? new PythonTupleValue([key, item]) : item;
                return true;
            }
            case PythonEnumerateSourceValue enumerateSource:
                if (TryGetNext(enumerateSource.Inner, out var element, span))
                {
                    value = new PythonTupleValue([
                        PythonWholeNumberValue.Create(enumerateSource.StartIndex + iterator.Index),
                        element,
                    ]);
                    iterator.Index++;
                    return true;
                }

                iterator.StopIteration = enumerateSource.Inner.StopIteration;
                break;
            case PythonZipSourceValue zipSource when zipSource.Inners.Length != 0:
            {
                var row = new PythonValue[zipSource.Inners.Length];
                var exhaustedAt = -1;
                for (var index = 0; index < zipSource.Inners.Length; index++)
                {
                    if (!TryGetNext(zipSource.Inners[index], out row[index], span))
                    {
                        exhaustedAt = index;
                        break;
                    }
                }

                if (exhaustedAt < 0)
                {
                    value = new PythonTupleValue(row);
                    return true;
                }

                if (!zipSource.Strict)
                {
                    iterator.StopIteration = zipSource.Inners[exhaustedAt].StopIteration;
                }
                else
                {
                    if (exhaustedAt > 0)
                    {
                        throw Fault(
                            "DPY4003",
                            $"zip() argument {exhaustedAt + 1} is shorter than argument{(exhaustedAt == 1 ? "" : "s")} 1{(exhaustedAt == 1 ? "" : $"-{exhaustedAt}")}",
                            span,
                            "ValueError"
                        );
                    }

                    for (var index = 1; index < zipSource.Inners.Length; index++)
                    {
                        if (TryGetNext(zipSource.Inners[index], out _, span))
                        {
                            throw Fault(
                                "DPY4003",
                                $"zip() argument {index + 1} is longer than argument{(index == 1 ? "" : "s")} 1{(index == 1 ? "" : $"-{index}")}",
                                span,
                                "ValueError"
                            );
                        }
                    }
                }

                break;
            }
            case PythonMapSourceValue mapSource:
            {
                var row = new PythonValue[mapSource.Inners.Length];
                var exhaustedAt = -1;
                for (var index = 0; index < mapSource.Inners.Length; index++)
                {
                    if (!TryGetNext(mapSource.Inners[index], out row[index], span))
                    {
                        exhaustedAt = index;
                        break;
                    }
                }

                if (exhaustedAt < 0)
                {
                    var applied = mapSource.Apply(row);
                    value = applied.Value;
                    iterator.StopIteration = applied.Stop;
                    return applied.Stop is null;
                }

                if (!mapSource.Strict)
                {
                    iterator.StopIteration = mapSource.Inners[exhaustedAt].StopIteration;
                }
                else
                {
                    if (exhaustedAt > 0)
                    {
                        throw StrictMapLengthFault(exhaustedAt, "shorter", span);
                    }

                    for (var index = 1; index < mapSource.Inners.Length; index++)
                    {
                        if (TryGetNext(mapSource.Inners[index], out _, span))
                        {
                            throw StrictMapLengthFault(index, "longer", span);
                        }
                    }
                }

                break;
            }
            case PythonGeneratorValue generator:
            {
                var advanced = generator.Resume();
                if (advanced.HasValue)
                {
                    value = advanced.Value;
                    return true;
                }

                break;
            }
            case PythonFilterSourceValue filterSource:
                while (TryGetNext(filterSource.Inner, out var candidate, span))
                {
                    if (filterSource.Keep(candidate))
                    {
                        value = candidate;
                        return true;
                    }
                }

                iterator.StopIteration = filterSource.Inner.StopIteration;
                break;
            case PythonSequenceIteratorSourceValue sequenceSource:
            {
                var step = UserObjectProtocols.Dispatcher!.StepSequenceIterator(
                    sequenceSource,
                    span
                );
                if (step.HasValue)
                {
                    value = step.Value;
                    return true;
                }
                iterator.IsExhausted = true;
                break;
            }
            case PythonUserIteratorSourceValue userSource:
            {
                var step = userSource.MoveNext();
                if (step.HasValue)
                {
                    value = step.Value;
                    return true;
                }

                break;
            }
        }

        value = PythonNoneValue.Instance;
        return false;
    }

    private static bool TryGetDictionaryNext(
        PythonIteratorValue iterator,
        PythonDictionaryValue dictionary,
        string kind,
        out PythonValue value,
        TextSpan span
    )
    {
        ValidateIteratorSize(iterator, dictionary.Items.Count, "dictionary", span);
        while (iterator.DictionaryPosition < dictionary.EntryCount)
        {
            var item = dictionary.GetEntry(iterator.DictionaryPosition);
            if (item is null)
            {
                UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
                iterator.DictionaryPosition++;
                continue;
            }
            if (iterator.Index == iterator.ExpectedCollectionSize)
            {
                iterator.IsExhausted = true;
                throw Fault(
                    "DPY4016",
                    "dictionary keys changed during iteration",
                    span,
                    "RuntimeError"
                );
            }
            iterator.DictionaryPosition++;
            iterator.Index++;
            value = PythonMappingProxies.ViewItem(item, kind);
            return true;
        }
        iterator.IsExhausted = true;
        value = PythonNoneValue.Instance;
        return false;
    }

    internal static void ValidateIteratorSize(
        PythonIteratorValue iterator,
        int count,
        string kind,
        TextSpan span
    )
    {
        if (iterator.IsInvalidated || count != iterator.ExpectedCollectionSize)
        {
            iterator.IsInvalidated = true;
            throw Fault("DPY4016", $"{kind} changed size during iteration", span, "RuntimeError");
        }
    }

    /// <summary>
    /// Writes through a view: one element, or a slice whose rvalue has the same structure.
    /// </summary>
    internal static void SetViewItem(
        PythonMemoryViewValue view,
        PythonValue index,
        PythonValue value,
        TextSpan span
    )
    {
        PythonMemoryViewMethods.RequireLive(view);
        if (!view.Writable)
            throw Fault("DPY4003", "cannot modify read-only memory", span, "TypeError");
        if (index is not PythonSliceValue slice)
        {
            // Only a one-dimensional view has elements an index can name.
            if (view.Shape.Length != 1)
                throw Fault(
                    "DPY4003",
                    "sub-views are not implemented",
                    span,
                    "NotImplementedError"
                );
            view.Write(ResolveIndex(view, index, span), value);
            return;
        }
        if (view.Shape.Length != 1)
            throw Fault(
                "DPY4003",
                "memoryview slice assignments are currently restricted to ndim = 1",
                span,
                "NotImplementedError"
            );
        if (
            !PythonBufferProtocol.TryGetContent(
                value,
                PythonBufferProtocol.FullReadOnly,
                span,
                out var content
            )
        )
            throw Fault(
                "DPY4003",
                $"memoryview: invalid type for format '{view.Format}'",
                span,
                "TypeError"
            );
        var (start, stop, step) = GetSliceIndices(slice, view.FirstDimension, span);
        var length = SliceLength(start, stop, step);
        if (content.Length != length * view.ItemSize)
            throw Fault(
                "DPY4003",
                "memoryview assignment: lvalue and rvalue have different structures",
                span,
                "ValueError"
            );
        for (var position = 0; position < length; position++)
        {
            var target = view.ElementOffset(start + position * step);
            Array.Copy(content, position * view.ItemSize, view.Bytes, target, view.ItemSize);
        }
    }

    /// <summary>The element a view reads at an index, or a sub-view for a slice.</summary>
    internal static PythonValue GetViewItem(
        PythonMemoryViewValue view,
        PythonValue index,
        TextSpan span
    )
    {
        PythonMemoryViewMethods.RequireLive(view);
        if (index is PythonSliceValue slice)
            return SliceView(view, slice, span);
        // Only a one-dimensional view has elements an index can name.
        if (view.Shape.Length != 1)
            throw Fault(
                "DPY4003",
                "multi-dimensional sub-views are not implemented",
                span,
                "NotImplementedError"
            );
        return view.Read(ResolveIndex(view, index, span));
    }

    /// <summary>A slice of a view is another view: dimension zero is cut, the rest kept.</summary>
    private static PythonMemoryViewValue SliceView(
        PythonMemoryViewValue view,
        PythonSliceValue slice,
        TextSpan span
    )
    {
        var (start, stop, step) = GetSliceIndices(slice, view.FirstDimension, span);
        var shape = (int[])view.Shape.Clone();
        var strides = (int[])view.Strides.Clone();
        shape[0] = SliceLength(start, stop, step);
        strides[0] *= step;
        return PythonMemoryViewMethods.CreateView(
            view.Source,
            view.Bytes,
            view.Offset + start * view.Strides[0],
            shape,
            strides,
            view.Format,
            view.Writable
        );
    }

    /// <summary>The index a view resolves, with CPython's own message for a bad one.</summary>
    internal static int ResolveIndex(PythonMemoryViewValue view, PythonValue index, TextSpan span)
    {
        if (index is not PythonWholeNumberValue number)
            throw Fault(
                "DPY4003",
                $"memoryview: invalid type for format '{view.Format}'",
                span,
                "TypeError"
            );
        var value = number.Value;
        if (value < 0)
            value += view.Length;
        if (value < 0 || value >= view.Length)
            throw Fault("DPY4003", "index out of bounds on memoryview", span, "IndexError");
        return (int)value;
    }

    internal static PythonValue GetItem(
        PythonValue target,
        PythonValue index,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(index);

        if (target is PythonManagedObjectValue subscriptable)
        {
            if (UserObjectProtocols.TryGetItem(subscriptable, index, span, out var userItem))
            {
                return userItem;
            }

            throw Fault(
                "DPY4011",
                $"'{subscriptable.Type.ReportedName}' object is not subscriptable",
                span,
                "TypeError"
            );
        }

        switch (target)
        {
            case PythonListValue list when index is PythonSliceValue slice:
            {
                var unpacked = UnpackSlice(slice, span);
                var (start, stop, step) = AdjustSliceIndices(unpacked, list.Elements.Count);
                var result = new List<PythonValue>();
                foreach (var elementIndex in EnumerateSliceIndices(start, stop, step))
                {
                    result.Add(list.Elements[elementIndex]);
                }

                return new PythonListValue(result);
            }
            case PythonTupleValue tuple when index is PythonSliceValue slice:
            {
                var (start, stop, step) = GetSliceIndices(slice, tuple.Elements.Length, span);
                if (
                    tuple.Elements.Length == 0
                    || start == 0 && stop == tuple.Elements.Length && step == 1
                )
                    return tuple;

                var result = new List<PythonValue>();
                foreach (var elementIndex in EnumerateSliceIndices(start, stop, step))
                {
                    result.Add(tuple.Elements[elementIndex]);
                }

                return new PythonTupleValue([.. result]);
            }
            case PythonListValue list:
                return list.Elements[GetListIndex(list, index, span)];
            case PythonTupleValue tuple:
                return tuple.Elements[GetSequenceIndex(index, tuple.Elements.Length, span)];
            case PythonTextValue text:
                return PythonTextTraversal.GetItem(text, index, span);
            case PythonByteSequenceValue bytes:
                return PythonBytesOperations.GetItem(bytes, index, span);
            case PythonByteArrayValue mutable:
                return PythonByteArrayOperations.GetItem(mutable, index, span);
            case PythonMemoryViewValue view:
                return GetViewItem(view, index, span);
            case PythonDequeValue deque:
                return GetDequeItem(deque, index, span);
            case PythonRangeValue range when index is PythonSliceValue slice:
            {
                if (range.Count > int.MaxValue)
                {
                    throw Fault(
                        "DPY4011",
                        "The range length exceeds the supported size.",
                        span,
                        "OverflowError"
                    );
                }

                var (start, stop, step) = GetSliceIndices(slice, (int)range.Count, span);
                return new PythonRangeValue(
                    range.Start + range.Step * start,
                    range.Start + range.Step * stop,
                    range.Step * step
                );
            }
            case PythonRangeValue range:
            {
                BigInteger position;
                if (UserObjectProtocols.TryConvertToIndex(index, span, out var userIndex))
                {
                    position = userIndex;
                }
                else if (PromoteTruthValue(index) is PythonWholeNumberValue wholeNumber)
                {
                    position = wholeNumber.Value;
                }
                else
                {
                    throw Fault("DPY4011", "Sequence indices must be integers.", span, "TypeError");
                }

                if (position < 0)
                {
                    position += range.Count;
                }

                if (position < 0 || position >= range.Count)
                {
                    throw Fault("DPY4012", "The range index is out of range.", span, "IndexError");
                }

                return PythonWholeNumberValue.Create(range.Start + range.Step * position);
            }
            case PythonMappingProxyValue proxy:
                return GetItem(proxy.Mapping, index, span);
            case PythonDictionaryValue dictionary
                when TryFindDictionaryItem(dictionary, index, out var item):
                return item.Value;
            case PythonDictionaryValue dictionary:
                // A subclass storage asks its own type before giving up, exactly as
                // `dict_subscript` does for a dictionary that is not exactly a dict.
                if (
                    dictionary.Owner is { } owner
                    && UserObjectProtocols.TryInvokeMissing(owner, index, span, out var missing)
                )
                    return missing;
                throw MissingKey(index);
            case PythonExternalObjectValue external:
                return external.Protocol.GetItem(index, span);
            case PythonBuiltinTypeValue unionType
                when ReferenceEquals(unionType, PythonBuiltinTypes.Union):
                return PythonTypeProtocols.BuildTypeUnion(index, span);
            case PythonBuiltinTypeValue type when IsGenericSubscribable(type.Name):
                return PythonGenericAliasValue.Create(type, index);
            case PythonBuiltinTypeValue type:
                throw Fault(
                    "DPY4011",
                    $"type '{type.Name}' is not subscriptable",
                    span,
                    "TypeError"
                );
            case PythonManagedTypeValue type:
                return PythonEnum.TryGetTypeItem(type, index, span)
                    ?? throw Fault(
                        "DPY4011",
                        $"type '{type.Name}' is not subscriptable",
                        span,
                        "TypeError"
                    );
            default:
                throw Fault("DPY4011", "This value is not subscriptable.", span, "TypeError");
        }
    }

    /// <summary>
    /// The builtin container types that accept a subscription. CPython gives these a
    /// `__class_getitem__`; every other builtin type, and any class without one, is
    /// not subscriptable.
    /// </summary>
    private static bool IsGenericSubscribable(string name) =>
        name is "list" or "dict" or "set" or "frozenset" or "tuple" or "type";

    internal static void SetItem(
        PythonValue target,
        PythonValue index,
        PythonValue value,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(value);

        if (target is PythonManagedObjectValue assignable)
        {
            if (UserObjectProtocols.TrySetItem(assignable, index, value, span))
            {
                return;
            }

            throw Fault(
                "DPY4011",
                $"'{assignable.Type.ReportedName}' object does not support item assignment",
                span,
                "TypeError"
            );
        }

        switch (target)
        {
            case PythonMappingProxyValue:
                throw Fault(
                    "DPY4011",
                    "'mappingproxy' object does not support item assignment",
                    span,
                    "TypeError"
                );

            case PythonByteArrayValue mutable:
                PythonByteArrayMutation.SetItem(mutable, index, value, span);
                return;
            case PythonMemoryViewValue view:
                SetViewItem(view, index, value, span);
                return;
            case PythonDequeValue deque:
                deque.Elements[ResolveDequeAssignment(deque, index, span)] = value;
                deque.Version++;
                return;
            case PythonListValue list when index is PythonSliceValue slice:
                AssignListSlice(list, slice, value, span);
                return;
            case PythonListValue list:
                list.Elements[GetListIndex(list, index, span, assignment: true)] = value;
                return;
            case PythonDictionaryValue dictionary:
                SetDictionaryItem(dictionary, index, value, span);
                return;
            default:
                throw Fault(
                    "DPY4011",
                    "This value does not support item assignment.",
                    span,
                    "TypeError"
                );
        }
    }

    internal static void SetListItem(
        PythonListValue list,
        int index,
        PythonValue value,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(value);
        if ((uint)index >= (uint)list.Elements.Count)
        {
            throw Fault("DPY4012", "The list index is out of range.", span, "IndexError");
        }

        list.Elements[index] = value;
    }

    internal static void SetTupleItem(
        PythonTupleValue tuple,
        int index,
        PythonValue value,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(tuple);
        ArgumentNullException.ThrowIfNull(value);
        if ((uint)index >= (uint)tuple.Elements.Length)
        {
            throw Fault("DPY4012", "The tuple index is out of range.", span, "IndexError");
        }

        tuple.Elements[index] = value;
        tuple.CachedHash = -1;
    }

    internal static bool Contains(
        PythonValue container,
        PythonValue item,
        TextSpan span = default,
        UserIterationDispatcher? userIteration = null
    )
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(item);
        if (UserObjectProtocols.TryContains(container, item, span, out var userContains))
        {
            return userContains;
        }

        if (
            container is PythonManagedTypeValue enumContainer
            && PythonEnum.TryContains(enumContainer, item, span) is { } enumContains
        )
        {
            return enumContains;
        }

        if (
            container is PythonManagedObjectValue instance
            && !UserObjectProtocols.DefinesSpecialMethod(instance, "__iter__")
            && !UserObjectProtocols.DefinesSpecialMethod(instance, "__getitem__")
        )
        {
            throw Fault(
                "DPY4015",
                $"argument of type '{instance.Type.ReportedName}' is not a container or iterable",
                span,
                "TypeError"
            );
        }

        if (container is PythonExternalObjectValue { Protocol: not IPythonExternalIterable })
        {
            throw Fault(
                "DPY4015",
                $"argument of type '{GetTypeName(container)}' is not a container or iterable",
                span,
                "TypeError"
            );
        }

        if (container is PythonTextValue text)
        {
            if (item is not PythonTextValue substring)
            {
                throw Fault(
                    "DPY4011",
                    $"'in <string>' requires string as left operand, not {GetTypeName(item)}",
                    span,
                    "TypeError"
                );
            }

            return text.Value.Contains(substring.Value, StringComparison.Ordinal);
        }

        if (container is PythonByteSequenceValue bytes)
        {
            return BytesContains(bytes.Value, item, span);
        }

        if (container is PythonByteArrayValue mutable)
        {
            return BytesContains(mutable.Value, item, span);
        }

        if (container is PythonMappingProxyValue proxy)
        {
            return Contains(proxy.Mapping, item, span, userIteration);
        }
        if (container is PythonDictionaryValue dictionary)
        {
            return TryFindDictionaryItem(dictionary, item, out _);
        }
        if (container is PythonSetValue set)
            return FindSetEntry(set, item, span) >= 0;
        // A keys view answers membership the way the dictionary does — the item is hashed as
        // a key, so an unhashable one is refused — and an items view looks the pair up.
        if (container is PythonDictionaryViewValue view)
        {
            switch (view.Kind)
            {
                case "dict_keys":
                    return TryFindDictionaryItem(view.Dictionary, item, out _);
                case "dict_items":
                    return item is PythonTupleValue { Elements.Length: 2 } pair
                        && TryFindDictionaryItem(view.Dictionary, pair.Elements[0], out var found)
                        && AreEqual(found.Value, pair.Elements[1]);
            }
        }

        var iterator = GetIterator(container, span, userIteration);
        while (TryGetNext(iterator, out var candidate, span))
        {
            if (AreEqual(candidate, item))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// `x in b"..."`: an integer is a byte value and must be in range, a bytes-like object is
    /// a subsequence, and anything else is refused. Iterating the bytes would answer the
    /// integer case only, so the whole test is spelled out here.
    /// </summary>
    private static bool BytesContains(byte[] bytes, PythonValue item, TextSpan span)
    {
        if (item is PythonByteSequenceValue subsequence)
        {
            return bytes.AsSpan().IndexOf(subsequence.Value) >= 0;
        }

        if (item is PythonByteArrayValue subsequenceBytes)
        {
            return bytes.AsSpan().IndexOf(subsequenceBytes.Value) >= 0;
        }

        var byteValue = item switch
        {
            PythonWholeNumberValue whole => whole.Value,
            PythonTruthValue truth => truth.Value ? BigInteger.One : BigInteger.Zero,
            _ => (BigInteger?)null,
        };
        if (byteValue is { } value)
        {
            if (value < 0 || value > 255)
                throw Fault("DPY4003", "byte must be in range(0, 256)", span, "ValueError");
            return bytes.AsSpan().IndexOf((byte)value) >= 0;
        }

        throw Fault(
            "DPY4003",
            $"a bytes-like object is required, not '{GetTypeName(item)}'",
            span,
            "TypeError"
        );
    }

    internal static IEnumerable<int> EnumerateSliceIndices(
        PythonSliceValue slice,
        int length,
        TextSpan span
    )
    {
        var (start, stop, step) = GetSliceIndices(slice, length, span);
        foreach (var index in EnumerateSliceIndices(start, stop, step))
            yield return index;
    }

    private static IEnumerable<int> EnumerateSliceIndices(int start, int stop, int step)
    {
        // A clamped step can cross Int32's boundary after the last valid element.
        // Keep the cursor wide so it terminates instead of wrapping into the sequence.
        for (long index = start; step > 0 ? index < stop : index > stop; index += step)
            yield return (int)index;
    }

    internal static (int Start, int Stop, int Step) GetSliceIndices(
        PythonSliceValue slice,
        int length,
        TextSpan span
    )
    {
        return AdjustSliceIndices(UnpackSlice(slice, span), length);
    }

    internal static (int Start, int Stop, int Step) UnpackSlice(
        PythonSliceValue slice,
        TextSpan span
    )
    {
        var step = slice.Step is PythonNoneValue ? 1 : GetSliceBound(slice.Step, span);
        if (step == 0)
        {
            throw Fault("DPY4012", "slice step cannot be zero", span, "ValueError");
        }

        // Convert in Python's step/start/stop order, before observing a mutable
        // sequence's length: any __index__ callback may change that sequence.
        var start =
            slice.Start is PythonNoneValue
                ? (step > 0 ? 0 : int.MaxValue)
                : GetSliceBound(slice.Start, span);
        var stop =
            slice.Stop is PythonNoneValue
                ? (step > 0 ? int.MaxValue : int.MinValue)
                : GetSliceBound(slice.Stop, span);
        return (start, stop, step);
    }

    internal static (int Start, int Stop, int Step) AdjustSliceIndices(
        (int Start, int Stop, int Step) slice,
        int length
    )
    {
        return (
            AdjustSliceIndex(slice.Start, length, slice.Step),
            AdjustSliceIndex(slice.Stop, length, slice.Step),
            slice.Step
        );
    }

    /// <summary>How many elements a slice covers once its bounds are adjusted.</summary>
    internal static int SliceLength(int start, int stop, int step) =>
        step > 0
            ? start < stop
                ? (int)(1 + ((long)stop - start - 1) / step)
                : 0
            : start > stop
                ? (int)(1 + ((long)start - stop - 1) / -(long)step)
                : 0;

    private static int AdjustSliceIndex(int index, int length, int step)
    {
        if (index < 0)
        {
            index += length;
            if (index < 0)
            {
                return step < 0 ? -1 : 0;
            }

            return index;
        }

        if (index >= length)
        {
            return step < 0 ? length - 1 : length;
        }

        return index;
    }

    private static int GetSliceBound(PythonValue value, TextSpan span)
    {
        BigInteger bound;
        if (PromoteTruthValue(value) is PythonWholeNumberValue wholeNumber)
        {
            bound = wholeNumber.Value;
        }
        else if (!UserObjectProtocols.TryConvertToIndex(value, span, out bound))
        {
            throw Fault(
                "DPY4011",
                "slice indices must be integers or None or have an __index__ method",
                span,
                "TypeError"
            );
        }

        if (bound > int.MaxValue)
        {
            return int.MaxValue;
        }

        if (bound < int.MinValue)
        {
            return int.MinValue;
        }

        return (int)bound;
    }

    internal static void AssignListSlice(
        PythonListValue list,
        PythonSliceValue slice,
        PythonValue value,
        TextSpan span
    )
    {
        var unpacked = UnpackSlice(slice, span);
        var values = MaterializeSliceValues(value, span);
        var (start, stop, step) = AdjustSliceIndices(unpacked, list.Elements.Count);
        if (step == 1)
        {
            if (stop < start)
            {
                stop = start;
            }

            list.Elements.RemoveRange(start, stop - start);
            list.Elements.InsertRange(start, values);
            return;
        }

        var indices = EnumerateSliceIndices(start, stop, step).ToList();
        if (indices.Count != values.Count)
        {
            throw Fault(
                "DPY4012",
                $"attempt to assign sequence of size {values.Count} "
                    + $"to extended slice of size {indices.Count}",
                span,
                "ValueError"
            );
        }

        for (var position = 0; position < indices.Count; position++)
        {
            list.Elements[indices[position]] = values[position];
        }
    }

    private static List<PythonValue> MaterializeSliceValues(PythonValue value, TextSpan span)
    {
        if (value is PythonListValue list)
            return [.. list.Elements];
        if (value is PythonTupleValue tuple)
            return [.. tuple.Elements];

        PythonIteratorValue iterator;
        try
        {
            iterator = GetIterator(value, span);
        }
        catch (Exception error) when (PythonNamespaceMapping.IsPythonException(error, "TypeError"))
        {
            throw Fault("DPY4011", "must assign iterable to extended slice", span, "TypeError");
        }

        // PySequence_Fast materializes list(iter(value)). Keep the actual Python
        // iterator so its second __iter__ call and its length hint remain visible.
        var source = iterator.Iterable
            is PythonUserIteratorSourceValue { OriginalIterator: { } original }
            ? original
            : (PythonValue)iterator;
        return MaterializeValues(source, span, useLengthHint: true);
    }

    internal static void DeleteItem(PythonValue target, PythonValue index, TextSpan span = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(index);

        if (target is PythonManagedObjectValue deletable)
        {
            if (UserObjectProtocols.TryDeleteItem(deletable, index, span))
            {
                return;
            }

            throw Fault(
                "DPY4011",
                $"'{deletable.Type.ReportedName}' object doesn't support item deletion",
                span,
                "TypeError"
            );
        }

        switch (target)
        {
            case PythonMappingProxyValue:
                throw Fault(
                    "DPY4011",
                    "'mappingproxy' object does not support item deletion",
                    span,
                    "TypeError"
                );

            case PythonByteArrayValue mutable:
                PythonByteArrayMutation.DeleteItem(mutable, index, span);
                return;
            case PythonMemoryViewValue view:
                PythonMemoryViewMethods.RequireLive(view);
                throw Fault(
                    "DPY4003",
                    view.Writable ? "cannot delete memory" : "cannot modify read-only memory",
                    span,
                    "TypeError"
                );
            case PythonDequeValue deque:
                deque.Elements.RemoveAt(ResolveDequeAssignment(deque, index, span));
                deque.Version++;
                return;
            case PythonListValue list when index is PythonSliceValue slice:
            {
                var unpacked = UnpackSlice(slice, span);
                var (start, stop, step) = AdjustSliceIndices(unpacked, list.Elements.Count);
                var indices = EnumerateSliceIndices(start, stop, step).ToList();
                indices.Sort();
                for (var position = indices.Count - 1; position >= 0; position--)
                {
                    list.Elements.RemoveAt(indices[position]);
                }

                return;
            }
            case PythonListValue list:
                list.Elements.RemoveAt(GetListIndex(list, index, span, assignment: true));
                return;
            case PythonDictionaryValue dictionary
                when TryFindDictionaryItem(dictionary, index, out var item):
                dictionary.RemoveItem(item);
                return;
            case PythonDictionaryValue:
                throw MissingKey(index);
            default:
                throw Fault(
                    "DPY4011",
                    "This value does not support item deletion.",
                    span,
                    "TypeError"
                );
        }
    }

    internal static void ExtendList(
        PythonListValue list,
        PythonValue iterable,
        TextSpan span,
        UserIterationDispatcher? userIteration = null
    )
    {
        if (iterable is PythonListValue source)
        {
            list.Elements.AddRange(source.Elements);
            return;
        }
        var iterator = GetIterator(iterable, span, userIteration);
        // Validate hints and run their callbacks; grow only as values actually arrive.
        var hint = PythonLengthHints.GetLengthHint(iterable, span);
        // CPython allocates for a positive hint even if the iterator yields nothing.
        if (list.SortActive && hint > 0)
            list.SortAllocationObserved = true;
        while (TryGetNext(iterator, out var value, span))
            list.Elements.Add(value);
    }

    internal static void RepeatListInPlace(PythonListValue list, PythonValue count, TextSpan span)
    {
        var repetitions = PythonSequenceRepetition.GetCount(count, span);
        if (repetitions <= 0)
        {
            list.Elements.Clear();
            return;
        }
        if (list.Elements.Count == 0 || repetitions == BigInteger.One)
            return;

        var bounded = PythonSequenceRepetition.GetBoundedCount(
            list.Elements.Count,
            repetitions,
            10_000_000,
            span
        );
        var repeated = new List<PythonValue>(list.Elements.Count * bounded);
        for (var repetition = 0; repetition < bounded; repetition++)
        {
            UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
            repeated.AddRange(list.Elements);
        }
        list.Elements.Clear();
        list.Elements.AddRange(repeated);
    }

    internal static void AddToSet(PythonSetValue set, PythonValue value, TextSpan span)
    {
        if (!IsHashable(value))
            throw Fault(
                "DPY4014",
                $"cannot use '{GetTypeName(value)}' as a set element (unhashable type: '{GetTypeName(value)}')",
                span,
                "TypeError"
            );
        AddToSetKnownHash(set, value, GetKeyHash(value, "set element", span), span);
    }

    internal static void AddToSetKnownHash(
        PythonSetValue set,
        PythonValue value,
        BigInteger hash,
        TextSpan span
    )
    {
        if (FindSetEntry(set, value, hash, span, forInsertion: true) < 0)
            set.AddEntry(value, hash);
    }

    internal static int FindSetEntry(PythonSetValue set, PythonValue value, TextSpan span = default)
    {
        // Python membership/removal accepts a mutable set as a frozenset lookup key.
        if (value is PythonSetValue { IsFrozen: false } mutable)
            value = mutable.Copy(frozen: true, span: span);
        return FindSetEntry(set, value, GetKeyHash(value, "set element", span), span);
    }

    internal static int FindSetEntry(
        PythonSetValue set,
        PythonValue value,
        BigInteger hash,
        TextSpan span,
        bool forInsertion = false
    )
    {
        for (var index = 0; index < set.Entries.Count; index++)
        {
            // A work unit covers at most 64 dense positions; restart checks also
            // bound reentrant equality that repeatedly mutates the set.
            if ((index & 63) == 0)
                UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
            var entry = set.Entries[index];
            if (entry.Hash != hash)
                continue;
            var version = set.MutationVersion;
            var matches = ReferenceEquals(entry.Value, value) || AreEqual(entry.Value, value);
            // CPython add accepts a successful comparison before its restart test.
            if (matches && forInsertion)
                return index;
            if (set.MutationVersion != version)
            {
                index = -1;
                continue;
            }
            if (matches)
                return index;
        }
        return -1;
    }

    internal static PythonSetValue CreateSet(IReadOnlyList<PythonValue> values, TextSpan span)
    {
        var set = new PythonSetValue([]);
        foreach (var value in values)
        {
            AddToSet(set, value, span);
        }

        return set;
    }

    internal static List<PythonValue> MaterializeValues(
        PythonValue iterable,
        TextSpan span,
        UserIterationDispatcher? userIteration = null,
        bool useLengthHint = false
    )
    {
        var iterator = GetIterator(iterable, span, userIteration);
        var capacity = useLengthHint
            ? (int)Math.Min(PythonLengthHints.GetLengthHint(iterable, span), 1024)
            : 0;
        var values = new List<PythonValue>(capacity);
        while (TryGetNext(iterator, out var value, span))
            values.Add(value);
        return values;
    }

    internal static bool IsTrue(PythonValue value) =>
        value switch
        {
            PythonNotImplementedValue => throw Fault(
                "DPY4003",
                "NotImplemented should not be used in a boolean context",
                default,
                "TypeError"
            ),
            PythonNoneValue => false,
            PythonTruthValue truth => truth.Value,
            PythonWholeNumberValue whole => !whole.Value.IsZero,
            PythonFloatingPointValue floatingPoint => floatingPoint.Value != 0,
            PythonComplexValue complex => complex.Value != Complex.Zero,
            PythonTextValue text => text.Value.Length != 0,
            PythonByteSequenceValue bytes => bytes.Value.Length != 0,
            PythonByteArrayValue mutable => mutable.Value.Length != 0,
            PythonMemoryViewValue view => GetViewLength(view) != 0,
            PythonDequeValue deque => deque.Elements.Count != 0,
            PythonListValue list => list.Elements.Count != 0,
            PythonTupleValue tuple => tuple.Elements.Length != 0,
            PythonMappingProxyValue proxy => GetLength(proxy.Mapping) != 0,
            PythonDictionaryValue dictionary => dictionary.Items.Count != 0,
            PythonRangeValue range => !range.Count.IsZero,
            PythonDictionaryViewValue view => view.Dictionary.Items.Count != 0,
            PythonSetValue set => set.Elements.Count != 0,
            PythonExternalObjectValue external => IsExternalTruthy(external),
            PythonManagedObjectValue instance => !UserObjectProtocols.TryIsTrue(
                instance,
                out var userTruth
            ) || userTruth,
            _ => true,
        };

    private static PythonRuntimeException StrictMapLengthFault(
        int index,
        string relation,
        TextSpan span
    ) =>
        Fault(
            "DPY4003",
            $"map() argument {index + 1} is {relation} than argument{(index == 1 ? "" : "s")} 1{(index == 1 ? "" : $"-{index}")}",
            span,
            "ValueError"
        );

    private static bool IsExternalTruthy(PythonExternalObjectValue external)
    {
        // Native sequence-shaped objects define truthiness through their length;
        // objects without a length protocol stay truthy like other opaque values.
        try
        {
            return external.Protocol.GetLength(default) != 0;
        }
        catch (PythonRuntimeException)
        {
            return true;
        }
    }

    internal static PythonTruthValue RichCompare(
        PythonValue left,
        PythonValue right,
        PythonRichComparison comparison,
        TextSpan span = default
    ) => PythonTruthValue.FromBoolean(IsTrue(RichCompareValue(left, right, comparison, span)));

    internal static PythonValue RichCompareValue(
        PythonValue left,
        PythonValue right,
        PythonRichComparison comparison,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left is PythonMappingProxyValue leftProxy)
        {
            return RichCompareValue(leftProxy.Mapping, right, comparison, span);
        }
        if (UserObjectProtocols.TryRichCompare(left, right, comparison, span, out var userResult))
        {
            return userResult;
        }

        if (left is PythonExternalObjectValue leftExternal)
        {
            return leftExternal.Protocol.RichCompare(right, comparison, span);
        }

        if (right is PythonExternalObjectValue rightExternal)
        {
            return rightExternal.Protocol.RichCompare(left, Reverse(comparison), span);
        }

        if (right is PythonMappingProxyValue rightProxy)
        {
            return RichCompareValue(rightProxy.Mapping, left, Reverse(comparison), span);
        }

        if (left is PythonListValue leftList && right is PythonListValue rightList)
            return CompareSequenceValues(
                leftList.Elements,
                rightList.Elements,
                comparison,
                span,
                isList: true
            );
        if (left is PythonTupleValue leftTuple && right is PythonTupleValue rightTuple)
            return CompareSequenceValues(
                leftTuple.Elements,
                rightTuple.Elements,
                comparison,
                span,
                isList: false
            );

        if (comparison is PythonRichComparison.Equal or PythonRichComparison.NotEqual)
        {
            var equals = AreEqual(left, right);
            return PythonTruthValue.FromBoolean(
                comparison == PythonRichComparison.Equal ? equals : !equals
            );
        }

        if (left is PythonSetValue leftSet && right is PythonSetValue rightSet)
            return PythonSetOperations.CompareOrdered(leftSet, rightSet, comparison, span);

        if (
            left is not PythonComplexValue
            && right is not PythonComplexValue
            && IsNumeric(PromoteTruthValue(left))
            && IsNumeric(PromoteTruthValue(right))
            && HasUnorderedFloatingPointOperand(left, right)
        )
        {
            return PythonTruthValue.False;
        }

        var ordered = CompareOrdered(left, right, span, ComparisonSymbol(comparison));
        return PythonTruthValue.FromBoolean(
            comparison switch
            {
                PythonRichComparison.LessThan => ordered < 0,
                PythonRichComparison.LessThanOrEqual => ordered <= 0,
                PythonRichComparison.GreaterThan => ordered > 0,
                PythonRichComparison.GreaterThanOrEqual => ordered >= 0,
                _ => throw new ArgumentOutOfRangeException(nameof(comparison)),
            }
        );
    }

    private static PythonRichComparison Reverse(PythonRichComparison comparison) =>
        comparison switch
        {
            PythonRichComparison.LessThan => PythonRichComparison.GreaterThan,
            PythonRichComparison.LessThanOrEqual => PythonRichComparison.GreaterThanOrEqual,
            PythonRichComparison.Equal => PythonRichComparison.Equal,
            PythonRichComparison.NotEqual => PythonRichComparison.NotEqual,
            PythonRichComparison.GreaterThan => PythonRichComparison.LessThan,
            PythonRichComparison.GreaterThanOrEqual => PythonRichComparison.LessThanOrEqual,
            _ => throw new ArgumentOutOfRangeException(nameof(comparison)),
        };

    /// <summary>
    /// Computes the value returned by the Python <c>hash()</c> builtin. Numeric values use
    /// CPython's 64-bit algorithms; native values delegate to tp_hash through the Stable-ABI
    /// bridge. Remaining hashable values use their qualified managed hash implementations.
    /// </summary>
    internal static BigInteger ComputePythonHash(PythonValue value, TextSpan span = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        BigInteger hash = value switch
        {
            PythonMappingProxyValue proxy => ComputePythonHash(proxy.Mapping, span),
            PythonSetValue { IsFrozen: true } frozen => GetFrozenSetHash(frozen, span),
            PythonTupleValue tuple => GetTupleHash(tuple, span),
            PythonTruthValue truth => truth.Value ? 1 : 0,
            PythonWholeNumberValue whole => PythonNumericHash.Integer(whole.Value),
            PythonFloatingPointValue floating => PythonNumericHash.Float(floating, floating.Value),
            PythonMemoryViewValue view => PythonMemoryViewHash(view),
            PythonRangeValue range => PythonRangeHash(range),
            PythonComplexValue complex => PythonNumericHash.Complex(complex),
            PythonExternalObjectValue external => external.Protocol.GetHash(span),
            PythonManagedObjectValue instance
                when UserObjectProtocols.TryGetHash(instance, span, out var userHash) => userHash,
            _ => GetPythonHash(value, span),
        };
        return hash == -1 ? -2 : hash;
    }

    /// <summary>
    /// CPython hashes a view only when it is read-only and one byte wide; a writable or
    /// wider view is unhashable, because its contents could change under the hash.
    /// </summary>
    private static BigInteger PythonMemoryViewHash(PythonMemoryViewValue view)
    {
        PythonMemoryViewMethods.RequireLive(view);
        if (view.Writable)
            throw Fault("DPY4003", "cannot hash writable memoryview object", default, "ValueError");
        if (view.FormatChar is not ('B' or 'b' or 'c'))
            throw Fault(
                "DPY4003",
                "memoryview: hashing is restricted to formats 'B', 'b' or 'c'",
                default,
                "ValueError"
            );
        // CPython asks the object the bytes came from for its own hash before it hashes the
        // view's, so a read-only view of a bytearray is unhashable because a bytearray is.
        ComputePythonHash(view.Source);
        return ComputePythonHash(PythonByteSequenceValue.Create(view.Materialize()));
    }

    /// <summary>
    /// CPython hashes a range as the triple it is — its length, its start and its step — so
    /// two ranges that walk the same sequence hash alike. Every empty range shares one value,
    /// since the sequence cannot tell them apart.
    /// </summary>
    private static BigInteger PythonRangeHash(PythonRangeValue range) =>
        ComputePythonHash(
            new PythonTupleValue([
                PythonWholeNumberValue.Create(range.Count),
                PythonWholeNumberValue.Create(range.Count.IsZero ? BigInteger.Zero : range.Start),
                PythonWholeNumberValue.Create(range.Count.IsZero ? BigInteger.One : range.Step),
            ])
        );

    internal static int GetPythonHash(PythonValue value, TextSpan span = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            PythonNoneValue => 0x1462_0a3,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            PythonWholeNumberValue or PythonFloatingPointValue or PythonComplexValue =>
                ComputePythonHash(value, span).GetHashCode(),
            PythonTextValue text => StringComparer.Ordinal.GetHashCode(text.Value),
            PythonByteSequenceValue bytes => GetByteHash(bytes.Value),
            PythonTupleValue tuple => new BigInteger(GetTupleHash(tuple, span)).GetHashCode(),
            PythonManagedObjectValue instance => UserObjectProtocols.TryGetHash(
                instance,
                span,
                out var userHash
            )
                ? userHash.GetHashCode()
                : RuntimeHelpers.GetHashCode(instance),
            PythonExternalObjectValue external => external.Protocol.GetHash(span).GetHashCode(),
            PythonManagedTypeValue type => RuntimeHelpers.GetHashCode(type),
            PythonBuiltinFunctionValue function => RuntimeHelpers.GetHashCode(function),
            PythonProtocolFunctionValue function => RuntimeHelpers.GetHashCode(function),
            PythonBoundMethodValue method => HashCode.Combine(
                RuntimeHelpers.GetHashCode(method.Target),
                RuntimeHelpers.GetHashCode(method.Function)
            ),
            PythonModuleValue module => RuntimeHelpers.GetHashCode(module),
            PythonSetValue { IsFrozen: true } frozen => GetFrozenSetHash(frozen, span)
                .GetHashCode(),
            PythonMappingProxyValue proxy => GetPythonHash(proxy.Mapping, span),
            PythonTypeUnionValue union => GetTypeUnionHash(union),
            PythonGenericAliasValue alias => GetGenericAliasHash(alias, span),
            PythonListValue
            or PythonDictionaryValue
            or PythonSetValue
            or PythonByteArrayValue
            or PythonDequeValue
            or PythonDictionaryViewValue => throw Fault(
                "DPY4014",
                $"unhashable type: '{PythonBoundDisplay.QualifiedTypeName(value)}'",
                span,
                "TypeError"
            ),
            _ => RuntimeHelpers.GetHashCode(value),
        };
    }

    /// <summary>`list[int]` hashes by its origin and arguments.</summary>
    private static int GetGenericAliasHash(PythonGenericAliasValue alias, TextSpan span)
    {
        var hash = RuntimeHelpers.GetHashCode(alias.Origin);
        foreach (var argument in alias.Arguments)
        {
            hash = HashCode.Combine(hash, GetPythonHash(argument, span));
        }

        return hash;
    }

    /// <summary>Member order is not part of a union's identity, so it is not part of its hash.</summary>
    private static int GetTypeUnionHash(PythonTypeUnionValue union)
    {
        var hash = 0;
        foreach (var member in union.Members)
            hash ^= GetPythonHash(member);
        return hash;
    }

    private static BigInteger GetFrozenSetHash(PythonSetValue frozen, TextSpan span)
    {
        if (frozen.CachedFrozenHash is { } cached)
            return cached;
        // CPython's order-independent 64-bit mixing, using stored insertion hashes.
        // The represented entry hashes still follow each value's qualified hash policy.
        ulong hash = 0;
        unchecked
        {
            foreach (var entry in frozen.Entries)
            {
                UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
                var value = (ulong)(entry.Hash & ulong.MaxValue);
                hash ^= ((value ^ 89869747UL) ^ (value << 16)) * 3644798167UL;
            }
            hash ^= ((ulong)frozen.Elements.Count + 1) * 1927868237UL;
            hash ^= (hash >> 11) ^ (hash >> 25);
            hash = hash * 69069UL + 907133923UL;
            if (hash == ulong.MaxValue)
                hash = 590923713UL;
            var result = new BigInteger((long)hash);
            frozen.CachedFrozenHash = result;
            return result;
        }
    }

    /// <summary>The contents of a bytes-like value, without copying.</summary>
    /// <summary>
    /// A view is bytes-like: its bytes in view order, which for a strided view is a copy.
    /// </summary>
    internal static bool TryGetMemoryViewContent(PythonMemoryViewValue view, out byte[] content)
    {
        PythonMemoryViewMethods.RequireLive(view);
        content = view.Materialize();
        return true;
    }

    internal static bool TryGetByteContent(PythonValue value, out byte[] content)
    {
        switch (value)
        {
            case PythonByteSequenceValue bytes:
                content = bytes.Value;
                return true;
            case PythonByteArrayValue mutable:
                content = mutable.Value;
                return true;
            case PythonMemoryViewValue view:
                return TryGetMemoryViewContent(view, out content);
            default:
                content = null!;
                return false;
        }
    }

    internal static byte[] GetBytes(PythonByteSequenceValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return (byte[])value.Value.Clone();
    }

    internal static byte[] GetUtf8(PythonTextValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Encoding.UTF8.GetBytes(value.Value);
    }

    internal static string GetTypeName(PythonValue value) =>
        value switch
        {
            PythonNoneValue => "NoneType",
            PythonEllipsisValue => "ellipsis",
            PythonNotImplementedValue => "NotImplementedType",
            PythonTruthValue => "bool",
            PythonWholeNumberValue => "int",
            PythonFloatingPointValue => "float",
            PythonComplexValue => "complex",
            PythonTextValue => "str",
            PythonByteSequenceValue => "bytes",
            PythonByteArrayValue => "bytearray",
            PythonMemoryViewValue => "memoryview",
            PythonDequeValue => "deque",
            PythonListValue => "list",
            PythonTupleValue => "tuple",
            PythonDictionaryValue => "dict",
            PythonMappingProxyValue => "mappingproxy",
            PythonSliceValue => "slice",
            PythonSetValue set => set.IsFrozen ? "frozenset" : "set",
            PythonDictionaryViewValue view => view.DisplayKind,
            PythonRangeValue => "range",
            PythonEnumerateSourceValue => "enumerate",
            PythonZipSourceValue => "zip",
            PythonMapSourceValue => "map",
            PythonFilterSourceValue => "filter",
            PythonGeneratorValue generatorValue => generatorValue.TypeName,
            PythonFileValue => "TextIOWrapper",
            PythonStreamValue => "TextIOWrapper",
            PythonTemplateValue => "Template",
            PythonInterpolationValue => "Interpolation",
            // CPython's `tp_name` for a PEP 604 union, as it appears in error messages.
            PythonTypeUnionValue => "typing.Union",
            PythonTypeAliasValue => "typing.TypeAliasType",
            // Not a builtin name, so `type()` falls through to the alias's own type.
            PythonGenericAliasValue => "types.GenericAlias",
            PythonIteratorValue => "iterator",
            PythonModuleValue => "module",
            PythonManagedTypeValue { Metaclass: PythonManagedTypeValue metaclass } =>
                metaclass.Name,
            PythonManagedTypeValue or PythonBuiltinTypeValue => "type",
            PythonSuperProxyValue => "super",
            PythonUnicodeErrorDescriptorValue => "member_descriptor",
            PythonTypeMetadataDescriptorValue { Name: "__base__" } => "member_descriptor",
            PythonTypeMetadataDescriptorValue => "getset_descriptor",
            PythonPropertyValue => "property",
            PythonTupleGetterValue => "_tuplegetter",
            PythonStaticMethodValue => "staticmethod",
            PythonClassMethodValue => "classmethod",
            PythonManagedObjectValue instance => instance.Type.ReportedName,
            PythonExternalObjectValue { Protocol: IPythonNamedExternalValue named } =>
                named.TypeName,
            PythonExternalObjectValue => "object",
            PythonProtocolFunctionValue { IsTypeMethodDescriptor: true } => "method_descriptor",
            PythonMemberDescriptorValue => "getset_descriptor",
            PythonMethodDescriptorValue { IsWrapper: true } => "wrapper_descriptor",
            PythonMethodDescriptorValue => "method_descriptor",
            PythonBoundMethodValue { Function.IsTypeMethodDescriptor: true } =>
                "builtin_function_or_method",
            // A method the runtime builds for a class written in Python is a function object,
            // which is what `type(Counter.update)` and `type(abc.abstractmethod)` report.
            PythonProtocolFunctionValue { IsPythonMethod: true } => "function",
            PythonBuiltinFunctionValue or PythonProtocolFunctionValue =>
                "builtin_function_or_method",
            PythonBoundMethodValue or PythonBoundUserMethodValue => "method",
            PythonExceptionTypeValue => "type",
            PythonExceptionValue exception => exception.TypeName,
            PythonFunctionValue => "function",
            _ => "object",
        };

    internal static PythonListValue CreateList(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        return new PythonListValue(
            Enumerable.Repeat<PythonValue>(PythonNoneValue.Instance, size).ToList()
        );
    }

    internal static PythonTupleValue CreateTuple(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        return new PythonTupleValue(
            Enumerable.Repeat<PythonValue>(PythonNoneValue.Instance, size).ToArray()
        );
    }

    internal static PythonDictionaryValue CreateDictionary() => new([]);

    internal static PythonModuleValue CreateModule(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new PythonModuleValue(name, new PythonGlobalNamespace());
    }

    internal static bool TryGetInstanceMethod(
        PythonManagedObjectValue instance,
        string name,
        out PythonValue method
    )
    {
        ArgumentNullException.ThrowIfNull(instance);
        return TryGetInstanceAttribute(instance, name, default, out method);
    }

    internal static PythonManagedTypeValue? GetManagedType(PythonValue value) =>
        value switch
        {
            PythonManagedObjectValue instance => instance.Type,
            PythonManagedTypeValue type => type.Metaclass as PythonManagedTypeValue,
            PythonExceptionValue exception => exception.ManagedType,
            _ => null,
        };

    internal static bool TryGetSpecialMethod(PythonValue value, string name, out PythonValue method)
    {
        if (GetManagedType(value) is { } type && TryGetTypeAttribute(type, name, out var attribute))
        {
            method = BindDescriptor(attribute, value, type, attributeName: name);
            return true;
        }
        method = null!;
        return false;
    }

    private static bool IsAttributeError(Exception error) =>
        PythonNamespaceMapping.IsPythonException(error, "AttributeError");

    private static bool IsDataDescriptor(PythonValue value) =>
        value switch
        {
            PythonDescriptorValue descriptor => descriptor.IsDataDescriptor,
            PythonPropertyValue
            or PythonTypeMetadataDescriptorValue
            or PythonTupleGetterValue
            or PythonUnicodeErrorDescriptorValue => true,
            _ => GetManagedType(value) is { } type
                && (
                    TryGetTypeAttribute(type, "__set__", out _)
                    || TryGetTypeAttribute(type, "__delete__", out _)
                ),
        };

    private static bool HasDescriptorGetter(PythonValue value) =>
        value
            is PythonDescriptorValue
                or PythonPropertyValue
                or PythonTypeMetadataDescriptorValue
                or PythonTupleGetterValue
                or PythonUnicodeErrorDescriptorValue
                or PythonProtocolFunctionValue { IsTypeMethodDescriptor: true }
        || GetManagedType(value) is { } type && TryGetTypeAttribute(type, "__get__", out _);

    private static bool TryInvokeUserDescriptorMutation(
        PythonValue descriptor,
        string operation,
        PythonValue[] arguments,
        TextSpan span
    )
    {
        if (GetManagedType(descriptor) is not { } type || !IsDataDescriptor(descriptor))
        {
            return false;
        }
        if (!TryGetTypeAttribute(type, operation, out var method))
        {
            throw Fault("DPY4023", operation, span, "AttributeError");
        }
        UserObjectProtocols.Dispatcher!.Invoke(
            BindDescriptor(method, descriptor, type, span, operation),
            arguments,
            span
        );
        return true;
    }

    /// <summary>Bind one class attribute using the instance and its dynamic owner.</summary>
    internal static PythonValue BindDescriptor(
        PythonValue value,
        PythonValue? instance,
        PythonValue owner,
        TextSpan span = default,
        string? attributeName = null
    )
    {
        // `dict.fromkeys` is a classmethod, so reached through a subclass it builds that
        // subclass: `D.fromkeys(['x'])` is a D, and `defaultdict.fromkeys('ab')` carries a
        // None factory. The class rides in the function's `BoundTo`, which its body reads.
        if (
            value is PythonBuiltinFunctionValue { Name: "fromkeys" } fromKeysBuiltin
            && owner is PythonManagedTypeValue declared
            && PythonSubclassStorage.StorageKindOf(declared) == "dict"
        )
        {
            // The function's own body reads `BoundTo`, so the replacement has to land on the
            // instance the body closed over rather than on a copy of it.
            fromKeysBuiltin.BoundTo = declared;
        }
        if (
            GetManagedType(value) is { } descriptorType
            && TryGetTypeAttribute(descriptorType, "__get__", out var getter)
        )
        {
            // CPython's descriptor-get slot calls the raw class entry with self,
            // instance and owner. Setter/deleter slots instead bind their methods.
            if (getter is PythonStaticMethodValue staticGetter)
            {
                getter = staticGetter.Function;
            }
            if (getter is PythonClassMethodValue)
            {
                throw Fault("DPY4009", "'classmethod' object is not callable", span, "TypeError");
            }
            return UserObjectProtocols.Dispatcher!.Invoke(
                getter,
                [value, instance ?? PythonNoneValue.Instance, owner],
                span
            );
        }

        return value switch
        {
            PythonTypeMetadataDescriptorValue descriptor => descriptor.Get(instance, owner, span),
            PythonUnicodeErrorDescriptorValue descriptor => descriptor.Get(instance, owner, span),
            PythonProtocolFunctionValue { IsTypeMethodDescriptor: true } descriptor =>
                PythonTypeMethodDescriptors.Bind(descriptor, instance, owner, span),
            PythonDescriptorValue descriptor when instance is not null => descriptor.Get(instance),
            PythonTupleGetterValue tupleGetter => GetTupleField(tupleGetter, instance),
            PythonPropertyValue property when instance is not null => GetPropertyValue(
                property,
                instance,
                attributeName ?? GetTypeName(owner),
                span
            ),
            PythonMethodDescriptorValue descriptor when instance is not null =>
                BindBuiltinDescriptor(descriptor, instance, owner, span),
            // A method a native type declares stays unbound when the type object itself is
            // the access' target, exactly as a C method read from its own type does.
            PythonProtocolFunctionValue { DeclaringType: not null } function
                when instance is PythonManagedTypeValue => function,
            PythonProtocolFunctionValue function when instance is not null =>
                new PythonBoundMethodValue(function.Name, instance, function)
                {
                    IsWrapper = function.IsSlotWrapper,
                },
            PythonFunctionValue function when instance is not null =>
                new PythonBoundUserMethodValue(function.Name, instance, function),
            PythonStaticMethodValue staticMethod => staticMethod.Function,
            PythonClassMethodValue { Function: PythonFunctionValue function } =>
                new PythonBoundUserMethodValue(function.Name, owner, function),
            PythonClassMethodValue { Function: PythonBuiltinFunctionValue builtin } =>
                PythonTypeProtocols.BindBuiltin(builtin, owner),
            PythonClassMethodValue { Function: PythonProtocolFunctionValue protocol } =>
                new PythonBoundMethodValue(protocol.Name, owner, protocol),
            PythonClassMethodValue classMethod => classMethod.Function,
            _ => value,
        };
    }

    /// <summary>
    /// `super()` resolution: search the attribute in the classes after the defining
    /// class in the instance's dynamic-type MRO (falling back to the defining class's
    /// own MRO when the instance is not a managed object of a related type).
    /// </summary>
    /// <summary>
    /// The attributes a `_tuplegetter` answers for itself: its documentation, the module it
    /// belongs to, and the three descriptor methods, which are what a user calls when the
    /// field is read or written directly.
    /// </summary>
    private static PythonValue? GetTupleGetterAttribute(PythonTupleGetterValue getter, string name)
    {
        switch (name)
        {
            case "__doc__":
                return new PythonTextValue(getter.Doc);
            case "__module__":
                return new PythonTextValue("collections");
            case "__get__":
            case "__set__":
            case "__delete__":
                return new PythonBoundMethodValue(
                    name,
                    getter,
                    new PythonProtocolFunctionValue(
                        name,
                        (target, arguments) =>
                        {
                            var field = target as PythonTupleGetterValue ?? getter;
                            return name switch
                            {
                                "__get__" => arguments.Count != 0
                                    ? GetTupleField(field, arguments[0])
                                    : throw Fault(
                                        "DPY4003",
                                        "__get__() takes at least 1 argument (0 given)",
                                        default,
                                        "TypeError"
                                    ),
                                "__set__" => throw TupleFieldNotWritable(default),
                                _ => throw Fault(
                                    "DPY4003",
                                    "can't delete attribute",
                                    default,
                                    "AttributeError"
                                ),
                            };
                        }
                    )
                )
                {
                    IsWrapper = true,
                };
            default:
                return null;
        }
    }

    /// <summary>
    /// `_tuplegetter.__get__`: the item at the field's index, read from the tuple the
    /// instance is built on. The descriptor belongs to one class, so the receiver is either
    /// an instance of it or the class the field was declared for.
    /// </summary>
    private static PythonValue GetTupleField(PythonTupleGetterValue getter, PythonValue? instance)
    {
        if (instance is null)
            return getter;
        var tuple =
            instance as PythonTupleValue ?? PythonSubclassStorage.Of(instance) as PythonTupleValue;
        if (tuple is null || getter.Index >= tuple.Elements.Length)
        {
            throw Fault(
                "DPY4023",
                $"descriptor '{getter.Name}' for 'tuple' objects doesn't apply to a "
                    + $"'{GetTypeName(instance)}' object",
                default,
                "AttributeError"
            );
        }
        return tuple.Elements[getter.Index];
    }

    private static PythonValue GetSuperResolutionType(PythonSuperProxyValue proxy)
    {
        if (
            PythonTypeProtocols.IsType(proxy.Instance)
            && PythonBuiltinTypes
                .GetMro(proxy.Instance)
                .Elements.Any(value => ReferenceEquals(value, proxy.DefiningType))
        )
            return proxy.Instance;
        var instanceType = PythonBuiltinTypes.GetRuntimeType(proxy.Instance);
        if (
            PythonBuiltinTypes
                .GetMro(instanceType)
                .Elements.Any(value => ReferenceEquals(value, proxy.DefiningType))
        )
            return instanceType;
        return proxy.DefiningType;
    }

    private static bool TryResolveSuperAttribute(
        PythonSuperProxyValue proxy,
        string name,
        out PythonValue value
    )
    {
        var type = GetSuperResolutionType(proxy);
        var mro = PythonBuiltinTypes.GetMro(type).Elements;
        var searching = false;
        foreach (var current in mro)
        {
            if (!searching)
            {
                searching = ReferenceEquals(current, proxy.DefiningType);
                continue;
            }
            if (TryGetOwnTypeAttribute(current, name, out value!))
                return true;
            // `super().__new__` reaches the builtin's own allocator, which is the one a
            // subclass of a storage builtin is created by; the ordinary lookup keeps it out
            // of the constructor path.
            if (name == "__new__" && current is PythonBuiltinTypeValue builtin)
            {
                try
                {
                    value = GetAttributeCore(builtin, name, default);
                    return true;
                }
                catch (PythonRuntimeException fault)
                    when (fault.PythonExceptionTypeName == "AttributeError") { }
            }
            if (
                ReferenceEquals(current, PythonBuiltinFunctions.Object)
                && PythonBuiltinFunctions.TryGetObjectProtocol(name, out value!)
            )
                return true;
        }
        value = null!;
        return false;
    }

    /// <summary>Reads one MRO entry without searching its bases.</summary>
    internal static bool TryGetOwnTypeAttribute(
        PythonValue type,
        string name,
        out PythonValue value
    )
    {
        if (type is PythonManagedTypeValue managed)
            return managed.Attributes.TryGetValue(name, out value!);
        if (
            type is PythonBuiltinTypeValue builtin
            && PythonSubclassStorage.Supports(builtin.Name)
            // Allocation stays with the runtime: the builtin's `__new__` requires a builtin
            // type, and a subclass is allocated from its own layout.
            && name != "__new__"
        )
        {
            // Whatever the builtin's type object answers — its methods, its slots, its
            // classmethods — is the subclass's own. `object`'s instance protocol is left to
            // the instance path, which would otherwise recurse through `__getattribute__`.
            PythonValue own;
            try
            {
                own = GetAttributeCore(builtin, name, default);
            }
            catch (PythonRuntimeException fault)
                when (fault.PythonExceptionTypeName == "AttributeError")
            {
                value = null!;
                return false;
            }
            if (
                own is PythonMethodDescriptorValue { OwnerName: "object" } descriptor
                && !PythonSlotMethods.IsObjectSlot(builtin.Name, descriptor.Name)
            )
            {
                value = null!;
                return false;
            }
            value = own;
            return true;
        }
        if (type is PythonExceptionTypeValue exception)
            return PythonExceptionProtocols.TryGetOwnAttribute(exception, name, out value!);
        if (ReferenceEquals(type, PythonBuiltinTypes.Type) && name != "__init_subclass__")
            return PythonTypeProtocols.TryGetAttribute(name, out value!);
        // Keep other object defaults in their existing explicit fallback paths: exposing
        // object.__new__/__init__ here would change ordinary constructor dispatch.
        if (ReferenceEquals(type, PythonBuiltinFunctions.Object) && name == "__init_subclass__")
        {
            value = PythonTypeProtocols.InitSubclass;
            return true;
        }
        value = null!;
        return false;
    }

    internal static bool TryGetTypeAttribute(
        PythonManagedTypeValue type,
        string name,
        out PythonValue value
    )
    {
        if (type.IsMroPending)
        {
            value = null!;
            return false;
        }
        if (type.ResolutionOrder is { } resolutionOrder)
        {
            foreach (var current in resolutionOrder)
            {
                if (TryGetOwnTypeAttribute(current, name, out value!))
                    return true;
            }
            value = null!;
            return false;
        }
        foreach (var current in type.Mro)
        {
            if (current.Attributes.TryGetValue(name, out value!))
                return true;
        }
        if (type.IsMetaclass && PythonTypeProtocols.TryGetAttribute(name, out value!))
            return true;
        if (name == "__init_subclass__")
        {
            value = PythonTypeProtocols.InitSubclass;
            return true;
        }
        value = null!;
        return false;
    }

    private static int GetListIndex(
        PythonListValue list,
        PythonValue index,
        TextSpan span,
        bool assignment = false
    )
    {
        BigInteger value;
        if (PromoteTruthValue(index) is PythonWholeNumberValue whole)
            value = whole.Value;
        else if (!UserObjectProtocols.TryConvertToIndex(index, span, out value))
            throw Fault(
                "DPY4011",
                $"list indices must be integers or slices, not {GetTypeName(index)}",
                span,
                "TypeError"
            );

        var minimum = IntPtr.Size == sizeof(long) ? new BigInteger(long.MinValue) : int.MinValue;
        var maximum = IntPtr.Size == sizeof(long) ? new BigInteger(long.MaxValue) : int.MaxValue;
        if (value < minimum || value > maximum)
            throw Fault(
                "DPY4012",
                $"cannot fit '{GetTypeName(index)}' into an index-sized integer",
                span,
                "IndexError"
            );

        // __index__ may resize the list, so normalize only after conversion completes.
        var count = list.Elements.Count;
        if (value < 0)
            value += count;
        if (value < 0 || value >= count)
            throw Fault(
                "DPY4012",
                assignment ? "list assignment index out of range" : "list index out of range",
                span,
                "IndexError"
            );
        return (int)value;
    }

    /// <summary>
    /// An index into a sequence of `count` elements, in CPython's own words — the sequence
    /// names itself, so a tuple index says `tuple indices must be integers or slices, not str`.
    /// </summary>
    internal static int GetSequenceIndex(
        PythonValue index,
        int count,
        TextSpan span,
        string typeName = "tuple"
    )
    {
        BigInteger value;
        if (UserObjectProtocols.TryConvertToIndex(index, span, out var userIndex))
        {
            value = userIndex;
        }
        else if (PromoteTruthValue(index) is PythonWholeNumberValue wholeNumber)
        {
            value = wholeNumber.Value;
        }
        else
        {
            throw Fault(
                "DPY4011",
                $"{typeName} indices must be integers or slices, not "
                    + ManagedObjectProtocols.GetTypeName(index),
                span,
                "TypeError"
            );
        }

        if (value < 0)
        {
            value += count;
        }

        if (value < 0 || value >= count)
        {
            throw Fault("DPY4012", "The sequence index is out of range.", span, "IndexError");
        }

        return (int)value;
    }

    /// <summary>
    /// `dict.fromkeys`: a classmethod, so it builds the class it was reached through —
    /// `D.fromkeys(['x'])` is a `D`, and `defaultdict.fromkeys('ab')` carries a None
    /// factory — and every key is set to the fill value in iteration order.
    /// </summary>
    internal static PythonValue DictionaryFromKeys(
        PythonValue source,
        PythonValue fill,
        TextSpan span,
        PythonValue? owner = null
    )
    {
        // An exact dictionary allocates its storage directly, which reserves the capacity
        // fromkeys would otherwise grow into.
        var target =
            owner is PythonManagedTypeValue declared
            && PythonSubclassStorage.StorageKindOf(declared) == "dict"
            && UserObjectProtocols.Dispatcher is { } dispatcher
                ? dispatcher.CallType(declared, [], [], [], span)
                : null;
        if (source is PythonDictionaryValue sourceDictionary)
        {
            var dictionary = target is null
                ? sourceDictionary.CreateFromKeysStorage(span)
                : PythonSubclassStorage.Of(target) as PythonDictionaryValue
                    ?? (PythonDictionaryValue)sourceDictionary.CreateFromKeysStorage(span);
            for (var position = 0; position < sourceDictionary.EntryCount; position++)
            {
                UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
                var item = sourceDictionary.GetEntry(position);
                if (item is not null)
                    SetDictionaryItemKnownHash(dictionary, item.Key, fill, item.KeyHash, span);
            }
            return target ?? dictionary;
        }
        if (source is PythonSetValue sourceSet)
        {
            var dictionary = target is null
                ? PythonDictionaryValue.CreateFromSetStorage(sourceSet.Elements.Count, span)
                : PythonSubclassStorage.Of(target) as PythonDictionaryValue
                    ?? PythonDictionaryValue.CreateFromSetStorage(sourceSet.Elements.Count, span);
            for (var position = 0; position < sourceSet.Entries.Count; position++)
            {
                UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
                var entry = sourceSet.Entries[position];
                SetDictionaryItemKnownHash(dictionary, entry.Value, fill, entry.Hash, span);
            }
            return target ?? dictionary;
        }
        var result = target is null
            ? new PythonDictionaryValue([])
            : PythonSubclassStorage.Of(target) as PythonDictionaryValue
                ?? new PythonDictionaryValue([]);
        var iterator = GetIterator(source, span);
        // Insert before requesting the next key: hashing may change the iterable
        // or fail, and fromkeys never requests a length hint.
        while (TryGetNext(iterator, out var key, span))
            SetDictionaryItem(result, key, fill, span);
        return target ?? result;
    }

    internal static void SetDictionaryItem(
        PythonDictionaryValue dictionary,
        PythonValue key,
        PythonValue value,
        TextSpan span
    )
    {
        if (!IsHashable(key))
        {
            throw Fault(
                "DPY4014",
                $"cannot use '{GetTypeName(key)}' as a dict key (unhashable type: '{GetTypeName(key)}')",
                span,
                "TypeError"
            );
        }

        SetDictionaryItemKnownHash(dictionary, key, value, GetKeyHash(key, "dict key", span), span);
    }

    /// <summary>
    /// The hash of a dictionary key or set element, with CPython's wording for a value that
    /// has none: `cannot use 'list' as a dict key (unhashable type: 'list')` on a lookup is
    /// the same refusal an insertion reports.
    /// </summary>
    private static BigInteger GetKeyHash(PythonValue value, string role, TextSpan span)
    {
        try
        {
            return ComputePythonHash(value, span);
        }
        catch (PythonRuntimeException error) when (error.PythonExceptionTypeName == "TypeError")
        {
            throw Fault(
                error.Code,
                $"cannot use '{GetTypeName(value)}' as a {role} ({error.Message})",
                span,
                "TypeError"
            );
        }
    }

    private static void SetDictionaryItemKnownHash(
        PythonDictionaryValue dictionary,
        PythonValue key,
        PythonValue value,
        BigInteger keyHash,
        TextSpan span
    )
    {
        if (TryFindDictionaryItem(dictionary, key, keyHash, out var item))
        {
            item.Value = value;
            return;
        }

        dictionary.AddItem(new PythonDictionaryItemValue(key, value, keyHash), span);
    }

    internal static void MergeDictionary(
        PythonDictionaryValue dictionary,
        PythonDictionaryValue source,
        TextSpan span
    )
    {
        if (ReferenceEquals(dictionary, source) || source.Items.Count == 0)
            return;
        if (dictionary.TryCloneForMerge(source, span))
            return;
        dictionary.PrepareMerge(source, span);
        var count = source.Items.Count;
        for (var position = 0; position < source.EntryCount; position++)
        {
            UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
            var item = source.GetEntry(position);
            if (item is null)
                continue;
            // Arguments capture the value before equality can call into either dictionary.
            SetDictionaryItemKnownHash(dictionary, item.Key, item.Value, item.KeyHash, span);
            if (source.Items.Count != count)
                throw Fault("DPY4016", "dict mutated during update", span, "RuntimeError");
        }
    }

    internal static bool TryFindDictionaryItem(
        PythonDictionaryValue dictionary,
        PythonValue key,
        out PythonDictionaryItemValue item
    ) => TryFindDictionaryItem(dictionary, key, GetKeyHash(key, "dict key", default), out item);

    internal static bool TryFindDictionaryItem(
        PythonDictionaryValue dictionary,
        PythonValue key,
        BigInteger keyHash,
        out PythonDictionaryItemValue item
    )
    {
        for (var index = 0; index < dictionary.Items.Count; index++)
        {
            var candidate = dictionary.Items[index];
            var version = dictionary.SizeVersion;
            // Identity does not bypass insertion hashes: one mutable-hash key
            // object can occupy multiple distinct entries in a Python dictionary.
            var matches =
                candidate.KeyHash == keyHash
                && (ReferenceEquals(candidate.Key, key) || AreEqual(candidate.Key, key));
            if (dictionary.SizeVersion != version)
            {
                // Equality may execute Python and structurally mutate this dictionary.
                index = -1;
                continue;
            }

            if (matches)
            {
                item = candidate;
                return true;
            }
        }

        item = null!;
        return false;
    }

    internal static bool IsHashable(PythonValue value) =>
        value switch
        {
            PythonSetValue set => set.IsFrozen,
            PythonListValue or PythonDictionaryValue or PythonByteArrayValue => false,
            // Tuples have a hash slot; hashing discovers unhashable children in order.
            PythonTupleValue => true,
            PythonManagedObjectValue instance => UserObjectProtocols.IsHashable(instance),
            _ => true,
        };

    internal static bool AreEqual(PythonValue left, PythonValue right)
    {
        if (left is PythonMappingProxyValue leftProxy)
        {
            return AreEqual(leftProxy.Mapping, right);
        }
        // A keys or items view compares as a set; a values view keeps the identity answer.
        if (PythonDictionaryViews.TryEquality(left, right, out var viewEqual))
            return viewEqual;
        if (PythonDictionaryViews.TryEquality(right, left, out var reflectedViewEqual))
            return reflectedViewEqual;
        if (UserObjectProtocols.TryAreEqual(left, right, out var userEqual))
        {
            return userEqual;
        }

        if (left is PythonExternalObjectValue leftExternal)
        {
            return leftExternal
                .Protocol.RichCompare(right, PythonRichComparison.Equal, default)
                .Value;
        }

        if (right is PythonExternalObjectValue rightExternal)
        {
            return rightExternal
                .Protocol.RichCompare(left, PythonRichComparison.Equal, default)
                .Value;
        }

        if (right is PythonMappingProxyValue rightProxy)
        {
            return AreEqual(rightProxy.Mapping, left);
        }

        if (left is PythonSetValue || right is PythonSetValue)
        {
            return left is PythonSetValue leftSet
                && right is PythonSetValue rightSet
                && leftSet.Elements.Count == rightSet.Elements.Count
                && PythonSetOperations.IsSubset(leftSet, rightSet);
        }

        left = PromoteTruthValue(left);
        right = PromoteTruthValue(right);

        if (IsNumeric(left) && IsNumeric(right))
        {
            if (left is PythonComplexValue leftComplex)
            {
                return ComplexEquals(leftComplex.Value, right);
            }

            if (right is PythonComplexValue rightComplex)
            {
                return ComplexEquals(rightComplex.Value, left);
            }

            return CompareRealNumbers(left, right) == 0;
        }

        return (left, right) switch
        {
            (PythonNoneValue, PythonNoneValue) => true,
            (PythonTextValue leftText, PythonTextValue rightText) => string.Equals(
                leftText.Value,
                rightText.Value,
                StringComparison.Ordinal
            ),
            // A subclass instance compares as the storage it carries: `D({'a': 1}) == {'a': 1}`.
            (PythonValue leftStorage, PythonValue rightStorage)
                when PythonSubclassStorage.Of(leftStorage) is not null
                    || PythonSubclassStorage.Of(rightStorage) is not null => AreEqual(
                PythonSubclassStorage.Resolve(leftStorage),
                PythonSubclassStorage.Resolve(rightStorage)
            ),
            (PythonRangeValue leftRange, PythonRangeValue rightRange) => RangesEqual(
                leftRange,
                rightRange
            ),
            (PythonDequeValue leftDeque, PythonDequeValue rightDeque) => DequesEqual(
                leftDeque,
                rightDeque
            ),
            (PythonByteSequenceValue leftBytes, PythonByteSequenceValue rightBytes) => leftBytes
                .Value.AsSpan()
                .SequenceEqual(rightBytes.Value),
            // A view compares by the bytes it exposes, against another view of the same
            // shape or any bytes-like value. Identity answers first, as it does for any
            // value, and a released view compares equal to nothing else rather than
            // reporting that it may not be read.
            (PythonMemoryViewValue leftView, PythonMemoryViewValue rightView)
                when ReferenceEquals(leftView, rightView) => true,
            (PythonMemoryViewValue { Released: true }, PythonValue) => false,
            (PythonValue, PythonMemoryViewValue { Released: true }) => false,
            (PythonMemoryViewValue leftView, PythonMemoryViewValue rightView)
                when !leftView.Released && !rightView.Released => leftView
                .Shape.AsSpan()
                .SequenceEqual(rightView.Shape)
                && leftView.Materialize().AsSpan().SequenceEqual(rightView.Materialize()),
            (PythonMemoryViewValue leftView, PythonValue rightValue)
                when !leftView.Released
                    && rightValue is not PythonMemoryViewValue { Released: true }
                    && TryGetByteContent(rightValue, out var rightViewBytes) => leftView
                .Materialize()
                .AsSpan()
                .SequenceEqual(rightViewBytes),
            (PythonValue leftValue, PythonMemoryViewValue rightView)
                when !rightView.Released
                    && leftValue is not PythonMemoryViewValue { Released: true }
                    && TryGetByteContent(leftValue, out var leftViewBytes) => leftViewBytes
                .AsSpan()
                .SequenceEqual(rightView.Materialize()),
            (PythonValue leftBytesLike, PythonValue rightBytesLike)
                when TryGetByteContent(leftBytesLike, out var leftContent)
                    && TryGetByteContent(rightBytesLike, out var rightContent) => leftContent
                .AsSpan()
                .SequenceEqual(rightContent),
            (PythonListValue leftList, PythonListValue rightList) => IsTrue(
                CompareSequenceValues(
                    leftList.Elements,
                    rightList.Elements,
                    PythonRichComparison.Equal,
                    default,
                    isList: true
                )
            ),
            (PythonTupleValue leftTuple, PythonTupleValue rightTuple) => IsTrue(
                CompareSequenceValues(
                    leftTuple.Elements,
                    rightTuple.Elements,
                    PythonRichComparison.Equal,
                    default,
                    isList: false
                )
            ),
            (PythonDictionaryValue leftDictionary, PythonDictionaryValue rightDictionary) =>
                AreDictionariesEqual(leftDictionary, rightDictionary),
            // Union order is not part of identity: `int | str` equals `str | int`.
            (PythonTypeUnionValue leftUnion, PythonTypeUnionValue rightUnion) =>
                leftUnion.SetEquals(rightUnion),
            // `list[int]` equals `list[int]`: same origin, same arguments in order.
            (PythonGenericAliasValue leftAlias, PythonGenericAliasValue rightAlias) =>
                ReferenceEquals(leftAlias.Origin, rightAlias.Origin)
                    && leftAlias.Arguments.Count == rightAlias.Arguments.Count
                    && leftAlias.Arguments.SequenceEqual(rightAlias.Arguments),
            _ => ReferenceEquals(left, right),
        };
    }

    private static PythonValue CompareSequenceValues(
        IReadOnlyList<PythonValue> left,
        IReadOnlyList<PythonValue> right,
        PythonRichComparison comparison,
        TextSpan span,
        bool isList
    )
    {
        if (_sequenceComparisonDepth >= 128)
            throw Fault(
                "DPY4005",
                "maximum recursion depth exceeded in comparison",
                span,
                "RecursionError"
            );
        _sequenceComparisonDepth++;
        try
        {
            if (
                isList
                && left.Count != right.Count
                && comparison is PythonRichComparison.Equal or PythonRichComparison.NotEqual
            )
                return PythonTruthValue.FromBoolean(comparison == PythonRichComparison.NotEqual);

            var index = 0;
            for (; index < left.Count && index < right.Count; index++)
            {
                UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
                var leftItem = left[index];
                var rightItem = right[index];
                if (!ReferenceEquals(leftItem, rightItem) && !AreEqual(leftItem, rightItem))
                    break;
            }

            // Equality and truth callbacks can resize either list, even when false.
            if (index >= left.Count || index >= right.Count)
                return RichCompareValue(
                    PythonWholeNumberValue.Create(left.Count),
                    PythonWholeNumberValue.Create(right.Count),
                    comparison,
                    span
                );
            if (comparison is PythonRichComparison.Equal or PythonRichComparison.NotEqual)
                return PythonTruthValue.FromBoolean(comparison == PythonRichComparison.NotEqual);

            // CPython rereads the current items before dispatching the requested operator.
            return RichCompareValue(left[index], right[index], comparison, span);
        }
        finally
        {
            _sequenceComparisonDepth--;
        }
    }

    private static bool AreDictionariesEqual(
        PythonDictionaryValue left,
        PythonDictionaryValue right
    )
    {
        if (left.Items.Count != right.Items.Count)
        {
            return false;
        }

        foreach (var item in left.Items)
        {
            if (
                !TryFindDictionaryItem(right, item.Key, out var rightItem)
                || !AreEqual(item.Value, rightItem.Value)
            )
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The operator a comparison refusal names.</summary>
    internal static string ComparisonSymbol(PythonRichComparison comparison) =>
        comparison switch
        {
            PythonRichComparison.LessThan => "<",
            PythonRichComparison.LessThanOrEqual => "<=",
            PythonRichComparison.GreaterThan => ">",
            PythonRichComparison.GreaterThanOrEqual => ">=",
            PythonRichComparison.Equal => "==",
            _ => "!=",
        };

    internal static int CompareOrdered(
        PythonValue left,
        PythonValue right,
        TextSpan span,
        string symbol = "<"
    )
    {
        if (left is PythonMappingProxyValue leftProxy)
        {
            return CompareOrdered(leftProxy.Mapping, right, span);
        }
        if (UserObjectProtocols.TryCompareOrdered(left, right, span, out var userOrdering))
        {
            return userOrdering;
        }

        if (right is PythonMappingProxyValue rightProxy)
        {
            return -CompareOrdered(rightProxy.Mapping, left, span);
        }

        left = PromoteTruthValue(left);
        right = PromoteTruthValue(right);

        if (IsNumeric(left) && IsNumeric(right))
        {
            if (left is PythonComplexValue || right is PythonComplexValue)
            {
                throw Fault("DPY4005", "Complex numbers cannot be ordered.", span, "TypeError");
            }

            // RichCompare handles unordered NaNs before reaching this total-order
            // adapter, which is also used by existing sequence/sort consumers.
            return CompareRealNumbers(left, right)
                ?? (
                    left is PythonFloatingPointValue { Value: var value } && double.IsNaN(value)
                        ? right is PythonFloatingPointValue { Value: var other }
                        && double.IsNaN(other)
                            ? 0
                            : -1
                        : 1
                );
        }

        return (left, right) switch
        {
            (PythonTextValue leftText, PythonTextValue rightText) => string.CompareOrdinal(
                leftText.Value,
                rightText.Value
            ),
            (PythonByteSequenceValue leftBytes, PythonByteSequenceValue rightBytes) => leftBytes
                .Value.AsSpan()
                .SequenceCompareTo(rightBytes.Value),
            (PythonValue leftBytesLike, PythonValue rightBytesLike)
                when OrderableBytesLike(leftBytesLike, rightBytesLike)
                    && TryGetByteContent(leftBytesLike, out var leftContent)
                    && TryGetByteContent(rightBytesLike, out var rightContent) => leftContent
                .AsSpan()
                .SequenceCompareTo(rightContent),
            (PythonTupleValue leftTuple, PythonTupleValue rightTuple) => CompareSequencesOrdered(
                leftTuple.Elements,
                rightTuple.Elements,
                span
            ),
            (PythonListValue leftList, PythonListValue rightList) => CompareSequencesOrdered(
                leftList.Elements,
                rightList.Elements,
                span
            ),
            (PythonDequeValue leftDeque, PythonDequeValue rightDeque) => CompareSequencesOrdered(
                leftDeque.Elements,
                rightDeque.Elements,
                span
            ),
            (PythonExternalObjectValue external, _) => CompareExternalOrdered(
                external,
                right,
                externalIsRight: false,
                span
            ),
            (_, PythonExternalObjectValue external) => CompareExternalOrdered(
                external,
                left,
                externalIsRight: true,
                span
            ),
            _ => throw Fault(
                "DPY4005",
                $"'{symbol}' not supported between instances of "
                    + $"'{PythonBoundDisplay.QualifiedTypeName(left)}' and "
                    + $"'{PythonBoundDisplay.QualifiedTypeName(right)}'",
                span,
                "TypeError"
            ),
        };
    }

    /// <summary>
    /// Which bytes-like values order against each other. A view has no ordering of its own:
    /// only a bytearray accepts one as its operand, comparing by the bytes it exposes, so
    /// `memoryview(b'a') &lt; bytearray(b'b')` answers and `memoryview(b'a') &lt; b'b'` does not.
    /// </summary>
    private static bool OrderableBytesLike(PythonValue left, PythonValue right) =>
        (left, right) switch
        {
            (PythonMemoryViewValue, PythonByteArrayValue) => true,
            (PythonByteArrayValue, PythonMemoryViewValue) => true,
            (PythonMemoryViewValue, _) or (_, PythonMemoryViewValue) => false,
            _ => true,
        };

    /// <summary>
    /// Two ranges are equal when they walk the same sequence: the same length, and — as far
    /// as the sequence can tell — the same first element and the same step.
    /// </summary>
    private static bool RangesEqual(PythonRangeValue left, PythonRangeValue right)
    {
        if (left.Count != right.Count)
            return false;
        if (left.Count.IsZero)
            return true;
        return left.Start == right.Start && (left.Count.IsOne || left.Step == right.Step);
    }

    /// <summary>Two deques are equal when they hold equal elements in the same order.</summary>
    private static bool DequesEqual(PythonDequeValue left, PythonDequeValue right)
    {
        if (left.Elements.Count != right.Elements.Count)
            return false;
        for (var index = 0; index < left.Elements.Count; index++)
        {
            if (!AreEqual(left.Elements[index], right.Elements[index]))
                return false;
        }
        return true;
    }

    private static int CompareSequencesOrdered(
        IReadOnlyList<PythonValue> left,
        IReadOnlyList<PythonValue> right,
        TextSpan span
    )
    {
        var count = Math.Min(left.Count, right.Count);
        for (var index = 0; index < count; index++)
        {
            if (AreEqual(left[index], right[index]))
            {
                continue;
            }

            return CompareOrdered(left[index], right[index], span);
        }

        return left.Count.CompareTo(right.Count);
    }

    private static int CompareExternalOrdered(
        PythonExternalObjectValue external,
        PythonValue other,
        bool externalIsRight,
        TextSpan span
    )
    {
        if (external.Protocol.RichCompare(other, PythonRichComparison.Equal, span).Value)
        {
            return 0;
        }

        // Both probes ask the native side about the external operand, so the comparison
        // direction flips when the external value sat on the right of the original pair.
        var leftOperandFirst = external
            .Protocol.RichCompare(
                other,
                externalIsRight ? PythonRichComparison.GreaterThan : PythonRichComparison.LessThan,
                span
            )
            .Value;
        return leftOperandFirst ? -1 : 1;
    }

    private static int GetByteHash(byte[] bytes)
    {
        var hash = new HashCode();
        foreach (var value in bytes)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    private static bool HasUnorderedFloatingPointOperand(PythonValue left, PythonValue right) =>
        left is PythonFloatingPointValue { Value: var leftValue } && double.IsNaN(leftValue)
        || right is PythonFloatingPointValue { Value: var rightValue } && double.IsNaN(rightValue);

    private static long GetTupleHash(PythonTupleValue tuple, TextSpan span)
    {
        if (tuple.CachedHash != -1)
            return tuple.CachedHash;

        // CPython's 64-bit tuple xxHash variant consumes full Python child hashes.
        const ulong prime1 = 11400714785074694791UL;
        const ulong prime2 = 14029467366897019727UL;
        const ulong prime5 = 2870177450012600261UL;
        unchecked
        {
            var accumulator = prime5;
            foreach (var value in tuple.Elements)
            {
                UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
                var lane = (ulong)(ComputePythonHash(value, span) & ulong.MaxValue);
                accumulator += lane * prime2;
                accumulator = BitOperations.RotateLeft(accumulator, 31);
                accumulator *= prime1;
            }
            accumulator += (ulong)tuple.Elements.Length ^ (prime5 ^ 3527539UL);
            if (accumulator == ulong.MaxValue)
                accumulator = 1546275796UL;

            // Publish only successful results. Reentrant successful hashes retain
            // their cache if this outer computation fails, or are replaced on success.
            tuple.CachedHash = (long)accumulator;
            return tuple.CachedHash;
        }
    }

    private static bool IsNumeric(PythonValue value) =>
        value is PythonWholeNumberValue or PythonFloatingPointValue or PythonComplexValue;

    private static PythonValue PromoteTruthValue(PythonValue value) =>
        value is PythonTruthValue truth
            ? PythonWholeNumberValue.Create(truth.Value ? BigInteger.One : BigInteger.Zero)
            : value;

    private static bool ComplexEquals(Complex value, PythonValue other) =>
        other switch
        {
            PythonComplexValue complex => value.Real == complex.Value.Real
                && value.Imaginary == complex.Value.Imaginary,
            PythonFloatingPointValue floating => value.Imaginary == 0
                && value.Real == floating.Value,
            PythonWholeNumberValue whole => value.Imaginary == 0
                && CompareIntegerToFloat(whole.Value, value.Real) == 0,
            _ => false,
        };

    private static int? CompareRealNumbers(PythonValue left, PythonValue right) =>
        (left, right) switch
        {
            (PythonWholeNumberValue leftWhole, PythonWholeNumberValue rightWhole) =>
                leftWhole.Value.CompareTo(rightWhole.Value),
            (PythonWholeNumberValue whole, PythonFloatingPointValue floating) =>
                CompareIntegerToFloat(whole.Value, floating.Value),
            (PythonFloatingPointValue floating, PythonWholeNumberValue whole) =>
                -CompareIntegerToFloat(whole.Value, floating.Value),
            (PythonFloatingPointValue leftFloat, PythonFloatingPointValue rightFloat) =>
                double.IsNaN(leftFloat.Value) || double.IsNaN(rightFloat.Value)
                    ? null
                    : leftFloat.Value.CompareTo(rightFloat.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(left)),
        };

    private static int? CompareIntegerToFloat(BigInteger integer, double floating)
    {
        if (double.IsNaN(floating))
            return null;
        if (double.IsPositiveInfinity(floating))
            return -1;
        if (double.IsNegativeInfinity(floating))
            return 1;

        // Convert the bounded float, never the arbitrary-precision integer. A finite
        // binary64 integer part needs at most 1024 bits and is represented exactly.
        var truncated = Math.Truncate(floating);
        var comparison = integer.CompareTo(new BigInteger(truncated));
        return comparison != 0 ? comparison : truncated.CompareTo(floating);
    }

    internal static PythonRuntimeException MissingAttribute(
        string typeName,
        string name,
        TextSpan span
    ) => Fault("DPY4022", $"'{typeName}' object has no attribute '{name}'", span, "AttributeError");

    /// <summary>
    /// A missing attribute on a class rather than on an instance: CPython words it with the
    /// class's own name, so `C.NOPE` reports `type object 'C'`, not `'C' object`.
    /// </summary>
    internal static PythonRuntimeException MissingTypeAttribute(
        string typeName,
        string name,
        TextSpan span
    ) =>
        Fault(
            "DPY4022",
            $"type object '{typeName}' has no attribute '{name}'",
            span,
            "AttributeError"
        );

    internal static PythonRuntimeException Fault(
        string code,
        string message,
        TextSpan span,
        string exceptionType
    ) => new(code, message, span, exceptionType);
}
