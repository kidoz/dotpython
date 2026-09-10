using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal sealed class PythonOrderingComparer : IComparer<PythonValue>
{
    internal static readonly PythonOrderingComparer Instance = new();

    public int Compare(PythonValue? left, PythonValue? right) =>
        ManagedObjectProtocols.CompareOrdered(left!, right!, default);
}

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
            PythonBuiltinTypeValue builtinType => builtinType.Construct(arguments, span),
            PythonProtocolFunctionValue function => function.Invoke(null, arguments),
            PythonBoundMethodValue method => method.Function.Invoke(method.Target, arguments),
            PythonExternalObjectValue external => external.Protocol.Call(arguments, span),
            PythonManagedTypeValue type when type.Construct is not null => type.Construct(
                arguments
            ),
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
                $"'{instance.Type.Name}' object is not callable",
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

    internal static PythonValue GetAttributeCore(
        PythonValue target,
        string name,
        TextSpan span = default
    )
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (
            target
                is PythonManagedTypeValue
                {
                    Metaclass: PythonManagedTypeValue descriptorMetaclass
                } classObject
            && TryGetTypeAttribute(descriptorMetaclass, name, out var metaDescriptor)
            && IsDataDescriptor(metaDescriptor)
            && HasDescriptorGetter(metaDescriptor)
        )
        {
            return BindDescriptor(metaDescriptor, classObject, descriptorMetaclass, span, name);
        }

        if (name == "__class__" && target is not PythonManagedObjectValue)
        {
            return UserObjectProtocols.Dispatcher is null
                ? PythonBuiltinTypes.GetRuntimeType(target)
                : PythonBuiltinTypes.Type.Construct([target], span);
        }
        if (target is PythonBuiltinTypeValue or PythonManagedTypeValue or PythonExceptionTypeValue)
        {
            if (name == "__mro__")
                return PythonBuiltinTypes.GetMro(target);
            if (name == "__bases__")
                return PythonBuiltinTypes.GetBases(target);
            if (name == "__base__")
            {
                var bases = PythonBuiltinTypes.GetBases(target).Elements;
                return bases.Length == 0 ? PythonNoneValue.Instance : bases[0];
            }
        }

        switch (target)
        {
            case PythonModuleValue module when name == "__dict__":
                return module.Globals.Dictionary;
            case PythonModuleValue module
                when module.Globals.TryGetValue(name, out var moduleValue):
                return moduleValue;
            case PythonModuleValue module:
                throw Fault(
                    "DPY4022",
                    $"Module '{module.Name}' has no attribute '{name}'.",
                    span,
                    "AttributeError"
                );
            case PythonManagedObjectValue instance:
                return GetInstanceAttribute(instance, name, span);
            case PythonSuperProxyValue { Instance: PythonManagedTypeValue subtype } classProxy:
                if (TryResolveSuperAttribute(classProxy, name, out var classInherited))
                {
                    var instanceAccess =
                        !subtype.Mro.Contains(classProxy.DefiningType)
                        && subtype.Metaclass is PythonManagedTypeValue;
                    return BindDescriptor(
                        classInherited,
                        instanceAccess ? subtype : null,
                        instanceAccess ? subtype.Metaclass : subtype,
                        span,
                        name
                    );
                }

                if (
                    classProxy.DefiningType.IsMetaclass
                    && PythonTypeProtocols.TryGetAttribute(name, out var typeMember)
                )
                {
                    var instanceAccess = !subtype.Mro.Contains(classProxy.DefiningType);
                    return BindDescriptor(
                        typeMember,
                        instanceAccess ? subtype : null,
                        instanceAccess ? subtype.Metaclass : subtype,
                        span,
                        name
                    );
                }
                if (name == "__init_subclass__")
                {
                    return BindDescriptor(
                        PythonTypeProtocols.InitSubclass,
                        null,
                        subtype,
                        span,
                        name
                    );
                }

                if (PythonBuiltinFunctions.TryGetObjectProtocol(name, out var classObjectMember))
                {
                    return classObjectMember;
                }

                throw Fault(
                    "DPY4022",
                    $"'super' object has no attribute '{name}'.",
                    span,
                    "AttributeError"
                );
            case PythonSuperProxyValue proxy:
                if (TryResolveSuperAttribute(proxy, name, out var inherited))
                {
                    var owner = GetManagedType(proxy.Instance) ?? proxy.DefiningType;
                    return BindDescriptor(inherited, proxy.Instance, owner, span, name);
                }

                if (
                    proxy.Instance is PythonManagedObjectValue objectInstance
                    && PythonBuiltinFunctions.TryGetObjectProtocol(name, out var objectProtocol)
                )
                {
                    // The chain bottomed out at `object`: bind its default protocol.
                    return new PythonBoundMethodValue(
                        name,
                        objectInstance,
                        (PythonProtocolFunctionValue)objectProtocol
                    );
                }

                if (name == "__init__" && proxy.Instance is PythonExceptionValue exceptionSelf)
                {
                    // The chain bottomed out at a builtin exception base:
                    // BaseException.__init__ rebinds args and the derived message.
                    return new PythonBoundMethodValue(
                        "__init__",
                        exceptionSelf,
                        new PythonProtocolFunctionValue(
                            "__init__",
                            (_, initArguments) =>
                            {
                                ApplyBaseExceptionInit(exceptionSelf, initArguments);
                                return PythonNoneValue.Instance;
                            }
                        )
                    );
                }

                throw Fault(
                    "DPY4022",
                    $"'super' object has no attribute '{name}'.",
                    span,
                    "AttributeError"
                );
            case PythonManagedTypeValue type when name == "__dict__":
                return new PythonMappingProxyValue(type.Attributes.Dictionary);
            case PythonMappingProxyValue proxy:
                return PythonMappingProxies.GetAttribute(proxy, name, span);
            case PythonManagedTypeValue type when name == "__name__":
                return new PythonTextValue(type.Name);
            case PythonManagedTypeValue type when name == "__qualname__":
                return new PythonTextValue(type.QualName ?? type.Name);
            case PythonManagedTypeValue type when TryGetTypeAttribute(type, name, out var value):
                return BindDescriptor(value, null, type, span, name);
            case PythonManagedTypeValue type when name == "__module__":
                return type.Module is null
                    ? new PythonTextValue("builtins")
                    : new PythonTextValue(type.Module);
            case PythonManagedTypeValue { IsMetaclass: false }
                when PythonBuiltinFunctions.TryGetObjectProtocol(
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
                throw MissingAttribute(type.Name, name, span);
            case PythonExternalObjectValue external:
                return external.Protocol.GetAttribute(name, span);
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
            case PythonBuiltinTypeValue builtin when name == "__init_subclass__":
                return BindDescriptor(PythonTypeProtocols.InitSubclass, null, builtin, span, name);
            case PythonBuiltinTypeValue { Name: "object" }
                when PythonBuiltinFunctions.TryGetObjectProtocol(name, out var objectMember):
                return objectMember;
            case PythonFunctionValue function when name == "__name__" || name == "__qualname__":
                return new PythonTextValue(function.Name);
            case PythonFunctionValue function when name == "__module__":
                return function.Globals.TryGetValue("__name__", out var functionModule)
                    ? functionModule
                    : PythonNoneValue.Instance;
            case PythonStreamValue stream:
                return GetStreamAttribute(stream, name, span);
            case PythonBoundUserMethodValue boundUserMethod when name == "__name__":
                return new PythonTextValue(boundUserMethod.Function.Name);
            case PythonBoundMethodValue boundMethod when name == "__name__":
                return new PythonTextValue(boundMethod.Name);
            case PythonBuiltinFunctionValue builtinFunction when name == "__name__":
                return new PythonTextValue(builtinFunction.Name);
            case PythonBuiltinTypeValue builtinTypeValue when name == "__name__":
                return new PythonTextValue(builtinTypeValue.Name);
            case PythonBuiltinTypeValue builtinTypeValue when name == "__qualname__":
                return new PythonTextValue(builtinTypeValue.Name);
            case PythonBuiltinTypeValue builtinTypeValue when name == "__module__":
                return new PythonTextValue(builtinTypeValue.ModuleName);
            case PythonBuiltinTypeValue { MatchArguments: { Elements.Length: > 0 } matchArguments }
                when name == "__match_args__":
                return matchArguments;
            case PythonBuiltinTypeValue { Name: "dict" } when name == "fromkeys":
                return new PythonBuiltinFunctionValue(
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
                        var dictionary = new PythonDictionaryValue([]);
                        foreach (var key in MaterializeValues(arguments[0], callSpan))
                        {
                            SetDictionaryItem(dictionary, key, fill, callSpan);
                        }

                        return dictionary;
                    }
                );
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
            case PythonExceptionValue exceptionDictionary when name == "__dict__":
                return exceptionDictionary.Attributes.Dictionary;
            case PythonExceptionValue exceptionValue when name == "args":
                return new PythonTupleValue([.. exceptionValue.EffectiveArguments]);
            case PythonExceptionValue { TypeName: "SystemExit" } systemExit when name == "code":
                return systemExit.EffectiveArguments.Count == 0
                    ? PythonNoneValue.Instance
                    : systemExit.EffectiveArguments[0];
            case PythonExceptionValue stopIteration
                when name == "value"
                    && (
                        (
                            stopIteration.ManagedType is null
                            && stopIteration.TypeName == "StopIteration"
                        )
                        || stopIteration.ManagedType?.Mro.Any(type =>
                            type.Bases.Count == 0 && type.ExceptionBaseName == "StopIteration"
                        ) == true
                    ):
                return stopIteration.EffectiveArguments.Count == 0
                    ? PythonNoneValue.Instance
                    : stopIteration.EffectiveArguments[0];
            case PythonExceptionValue { GroupExceptions: not null } group when name == "message":
                return new PythonTextValue(group.Message);
            case PythonExceptionValue { GroupExceptions: { } nested } when name == "exceptions":
                return new PythonTupleValue([.. nested.Cast<PythonValue>()]);
            case PythonExceptionValue exceptionInstance
                when exceptionInstance.Attributes.TryGetValue(name, out var exceptionAttribute):
                return exceptionAttribute;
            case PythonExceptionValue contextSource when name == "__context__":
                return (PythonValue?)contextSource.Context ?? PythonNoneValue.Instance;
            case PythonExceptionValue causeSource when name == "__cause__":
                return (PythonValue?)causeSource.Cause ?? PythonNoneValue.Instance;
            case PythonExceptionValue suppressSource when name == "__suppress_context__":
                return PythonTruthValue.FromBoolean(suppressSource.SuppressContext);
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
                                ConvertToExceptionValue(
                                    RequireSingleArgument("athrow", arguments, methodSpan),
                                    methodSpan
                                )
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
            case var builtin when PythonBuiltinMethods.SupportsMethods(builtin):
                if (PythonBuiltinMethods.TryGet(builtin, name, out var method))
                {
                    return new PythonBoundMethodValue(name, builtin, method);
                }

                throw Fault(
                    "DPY4023",
                    $"'{GetTypeName(builtin)}' object has no attribute '{name}'",
                    span,
                    "AttributeError"
                );
            default:
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
                        return PythonWholeNumberValue.Create(text.Value.EnumerateRunes().Count());
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

        switch (target)
        {
            case PythonManagedTypeValue { Metaclass: PythonManagedTypeValue meta } type
                when TryGetTypeAttribute(meta, name, out var metaDescriptor)
                    && TrySetDescriptor(metaDescriptor, type, name, value, span):
                return;
            case PythonManagedTypeValue when name == "__dict__":
                throw Fault(
                    "DPY4023",
                    "attribute '__dict__' of 'type' objects is not writable",
                    span,
                    "AttributeError"
                );
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
            case PythonExceptionValue exceptionInstance when name == "__dict__":
                exceptionInstance.Attributes = new PythonAttributeDictionary(
                    RequireNamespaceDictionary(value, span)
                );
                return;
            case PythonExceptionValue exceptionInstance:
                exceptionInstance.Attributes[name] = value;
                return;
            case PythonManagedObjectValue instance:
                if (UserObjectProtocols.TrySetAttribute(instance, name, value, span))
                {
                    return;
                }

                SetInstanceAttribute(instance, name, value, span);
                return;
            case PythonManagedTypeValue type when name is "__name__" or "__qualname__":
                if (value is not PythonTextValue text)
                {
                    throw Fault(
                        "DPY4023",
                        $"can only assign string to {type.Name}.{name}, not '{GetTypeName(value)}'",
                        span,
                        "TypeError"
                    );
                }
                if (name == "__name__")
                {
                    if (text.Value.Contains('\0', StringComparison.Ordinal))
                    {
                        throw Fault(
                            "DPY4023",
                            "type name must not contain null characters",
                            span,
                            "ValueError"
                        );
                    }
                    type.Name = text.Value;
                }
                else
                {
                    type.QualName = text.Value;
                }
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

    /// <summary>
    /// The default attribute lookup for a managed instance (data descriptors, the
    /// instance dictionary, then bound type attributes) without `__getattr__` hooks.
    /// </summary>
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

        if (name == "__dict__" && !hasTypeValue)
        {
            value = instance.Attributes.Dictionary;
            return true;
        }

        if (instance.Attributes.TryGetValue(name, out value!))
        {
            return true;
        }

        if (hasTypeValue)
        {
            value = BindDescriptor(typeValue, instance, instance.Type, span, name);
            return true;
        }

        if (name == "__class__")
        {
            value = instance.Type;
            return true;
        }

        return false;
    }

    internal static PythonValue GetInstanceAttribute(
        PythonManagedObjectValue instance,
        string name,
        TextSpan span
    ) =>
        TryGetInstanceAttribute(instance, name, span, out var value)
            ? value
            : throw MissingAttribute(instance.Type.Name, name, span);

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

        if (name == "__dict__" && !TryGetTypeAttribute(instance.Type, name, out _))
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

        if (name == "__dict__" && !TryGetTypeAttribute(instance.Type, name, out _))
        {
            instance.Attributes = new PythonAttributeDictionary();
            return;
        }

        if (!instance.Attributes.Remove(name))
        {
            throw MissingAttribute(instance.Type.Name, name, span);
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
            case PythonDescriptorValue { IsDataDescriptor: true, Set: null }:
                throw Fault("DPY4023", $"Attribute '{name}' is read-only.", span, "AttributeError");
            case PythonDescriptorValue { IsDataDescriptor: true } descriptor:
                descriptor.Set!(instance, value);
                return true;
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

        switch (target)
        {
            case PythonManagedTypeValue { Metaclass: PythonManagedTypeValue meta } type
                when TryGetTypeAttribute(meta, name, out var metaDescriptor)
                    && TryDeleteDescriptor(metaDescriptor, type, name, span):
                return;
            case PythonManagedTypeValue when name == "__dict__":
                throw Fault(
                    "DPY4023",
                    "attribute '__dict__' of 'type' objects is not writable",
                    span,
                    "AttributeError"
                );
            case PythonMappingProxyValue:
                throw MissingAttribute("mappingproxy", name, span);

            case PythonModuleValue when name == "__dict__":
                throw Fault("DPY4022", "readonly attribute", span, "AttributeError");
            case PythonExceptionValue { ManagedType: { } exceptionClass } exception
                when TryGetTypeAttribute(exceptionClass, name, out var descriptor)
                    && TryDeleteDescriptor(descriptor, exception, name, span):
                return;
            case PythonExceptionValue when name == "__dict__":
                throw Fault("DPY4023", "cannot delete __dict__", span, "TypeError");
            case PythonModuleValue module when module.Globals.Remove(name):
                return;
            case PythonModuleValue module:
                throw Fault(
                    "DPY4022",
                    $"Module '{module.Name}' has no attribute '{name}'.",
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
            case PythonManagedTypeValue type
                when name is "__name__" or "__qualname__" or "__module__":
                throw Fault(
                    "DPY4023",
                    $"cannot delete '{name}' attribute of immutable type '{type.Name}'",
                    span,
                    "TypeError"
                );
            case PythonManagedTypeValue type when type.Attributes.Remove(name):
                return;
            case PythonManagedTypeValue type:
                throw MissingAttribute(type.Name, name, span);
            default:
                throw Fault(
                    "DPY4023",
                    "This value does not expose deletable managed attributes.",
                    span,
                    "AttributeError"
                );
        }
    }

    internal static int GetLength(PythonValue value, TextSpan span = default) =>
        value switch
        {
            PythonTextValue text => text.Value.EnumerateRunes().Count(),
            PythonByteSequenceValue bytes => bytes.Value.Length,
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
            PythonManagedObjectValue instance => UserObjectProtocols.TryGetLength(
                instance,
                span,
                out var userLength
            )
                ? userLength
                : throw Fault(
                    "DPY4011",
                    $"object of type '{instance.Type.Name}' has no len()",
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
        exception.Arguments = [.. arguments];
        exception.Message = arguments.Count switch
        {
            0 => string.Empty,
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

    internal static PythonExceptionValue ConvertToExceptionValue(
        PythonValue value,
        TextSpan span
    ) =>
        value switch
        {
            PythonExceptionValue raised => raised,
            PythonExceptionTypeValue type => new PythonExceptionValue(type.Name, string.Empty),
            PythonManagedTypeValue { ExceptionBaseName: not null } exceptionClass =>
                new PythonExceptionValue(exceptionClass.Name, string.Empty)
                {
                    ManagedType = exceptionClass,
                },
            _ => throw Fault(
                "DPY4003",
                "Exceptions must derive from BaseException.",
                span,
                "TypeError"
            ),
        };

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

        var exception = ConvertToExceptionValue(arguments[0], span);
        if (generator is { IsCoroutine: true, State: PythonGeneratorState.Completed })
        {
            throw ReusedCoroutineFault(span);
        }

        if (generator.State is PythonGeneratorState.Created or PythonGeneratorState.Completed)
        {
            // A fresh or exhausted generator never runs its body: it closes and the
            // exception propagates to the caller.
            generator.State = PythonGeneratorState.Completed;
            throw new PythonRaisedException(exception);
        }

        var advanced = generator.ResumeCore!(null, exception);
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
            var advanced = generator.ResumeCore!(
                null,
                new PythonExceptionValue("GeneratorExit", string.Empty)
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

        if (value is PythonManagedObjectValue instance && userIteration is not null)
        {
            return userIteration(instance, span);
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
            return new PythonIteratorValue(view, view.Dictionary.SizeVersion);
        }

        if (value is PythonFileValue file)
        {
            // A file is its own line cursor; the wrapper defers to it on each step.
            EnsureFileOpen(file, span);
            return new PythonIteratorValue(file, -1);
        }

        if (
            value
            is not (
                PythonListValue
                or PythonTupleValue
                or PythonDictionaryValue
                or PythonTextValue
                or PythonByteSequenceValue
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

        return new PythonIteratorValue(
            value,
            value is PythonDictionaryValue dictionary ? dictionary.SizeVersion : -1
        );
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
        switch (iterator.Iterable)
        {
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
            case PythonListValue list when iterator.Index < list.Elements.Count:
                value = list.Elements[iterator.Index++];
                return true;
            case PythonTupleValue tuple when iterator.Index < tuple.Elements.Length:
                value = tuple.Elements[iterator.Index++];
                return true;
            case PythonDictionaryViewValue view:
                if (view.Dictionary.SizeVersion != iterator.ExpectedDictionarySizeVersion)
                {
                    throw Fault(
                        "DPY4016",
                        "dictionary changed size during iteration",
                        span,
                        "RuntimeError"
                    );
                }
                if (iterator.Index < view.Dictionary.Items.Count)
                {
                    value = PythonMappingProxies.ViewItem(
                        view.Dictionary.Items[iterator.Index++],
                        view.Kind
                    );
                    return true;
                }
                iterator.IsExhausted = true;
                break;
            case PythonDictionaryValue dictionary:
                if (dictionary.SizeVersion != iterator.ExpectedDictionarySizeVersion)
                {
                    throw Fault(
                        "DPY4016",
                        "Dictionary size changed during iteration.",
                        span,
                        "RuntimeError"
                    );
                }

                if (iterator.Index < dictionary.Items.Count)
                {
                    value = dictionary.Items[iterator.Index++].Key;
                    return true;
                }

                iterator.IsExhausted = true;
                break;
            case PythonTextValue text:
            {
                var runes = text.Value.EnumerateRunes().ToArray();
                if (iterator.Index < runes.Length)
                {
                    value = new PythonTextValue(runes[iterator.Index++].ToString());
                    return true;
                }

                break;
            }
            case PythonByteSequenceValue bytes when iterator.Index < bytes.Value.Length:
                value = PythonWholeNumberValue.Create(bytes.Value[iterator.Index++]);
                return true;
            case PythonSetValue set when iterator.Index < set.Elements.Count:
                value = set.Elements[iterator.Index++];
                return true;
            case PythonRangeValue range:
            {
                var current = range.Start + range.Step * iterator.Index;
                if (range.Step > 0 ? current < range.Stop : current > range.Stop)
                {
                    iterator.Index++;
                    value = PythonWholeNumberValue.Create(current);
                    return true;
                }

                break;
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
                $"'{subscriptable.Type.Name}' object is not subscriptable",
                span,
                "TypeError"
            );
        }

        switch (target)
        {
            case PythonListValue list when index is PythonSliceValue slice:
            {
                var result = new List<PythonValue>();
                foreach (
                    var elementIndex in EnumerateSliceIndices(slice, list.Elements.Count, span)
                )
                {
                    result.Add(list.Elements[elementIndex]);
                }

                return new PythonListValue(result);
            }
            case PythonTupleValue tuple when index is PythonSliceValue slice:
            {
                var result = new List<PythonValue>();
                foreach (
                    var elementIndex in EnumerateSliceIndices(slice, tuple.Elements.Length, span)
                )
                {
                    result.Add(tuple.Elements[elementIndex]);
                }

                return new PythonTupleValue([.. result]);
            }
            case PythonTextValue text when index is PythonSliceValue slice:
            {
                var runes = text.Value.EnumerateRunes().ToArray();
                var builder = new StringBuilder();
                foreach (var elementIndex in EnumerateSliceIndices(slice, runes.Length, span))
                {
                    builder.Append(runes[elementIndex].ToString());
                }

                return new PythonTextValue(builder.ToString());
            }
            case PythonListValue list:
                return list.Elements[GetSequenceIndex(index, list.Elements.Count, span)];
            case PythonTupleValue tuple:
                return tuple.Elements[GetSequenceIndex(index, tuple.Elements.Length, span)];
            case PythonTextValue text:
            {
                var runes = text.Value.EnumerateRunes().ToArray();
                return new PythonTextValue(
                    runes[GetSequenceIndex(index, runes.Length, span)].ToString()
                );
            }
            case PythonByteSequenceValue bytes:
                return PythonWholeNumberValue.Create(
                    bytes.Value[GetSequenceIndex(index, bytes.Value.Length, span)]
                );
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
            case PythonDictionaryValue:
                throw MissingKey(index);
            case PythonExternalObjectValue external:
                return external.Protocol.GetItem(index, span);
            default:
                throw Fault("DPY4011", "This value is not subscriptable.", span, "TypeError");
        }
    }

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
                $"'{assignable.Type.Name}' object does not support item assignment",
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

            case PythonListValue list when index is PythonSliceValue slice:
                AssignListSlice(list, slice, value, span);
                return;
            case PythonListValue list:
                list.Elements[GetSequenceIndex(index, list.Elements.Count, span)] = value;
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
            container is PythonManagedObjectValue instance
            && !UserObjectProtocols.DefinesSpecialMethod(instance, "__iter__")
        )
        {
            throw Fault(
                "DPY4015",
                $"argument of type '{instance.Type.Name}' is not a container or iterable",
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
                    "A string membership test requires a string operand.",
                    span,
                    "TypeError"
                );
            }

            return text.Value.Contains(substring.Value, StringComparison.Ordinal);
        }

        if (container is PythonMappingProxyValue proxy)
        {
            return Contains(proxy.Mapping, item, span, userIteration);
        }
        if (container is PythonDictionaryValue dictionary)
        {
            return TryFindDictionaryItem(dictionary, item, out _);
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

    internal static IEnumerable<int> EnumerateSliceIndices(
        PythonSliceValue slice,
        int length,
        TextSpan span
    )
    {
        var (start, stop, step) = GetSliceIndices(slice, length, span);
        if (step > 0)
        {
            for (var index = start; index < stop; index += step)
            {
                yield return index;
            }
        }
        else
        {
            for (var index = start; index > stop; index += step)
            {
                yield return index;
            }
        }
    }

    internal static (int Start, int Stop, int Step) GetSliceIndices(
        PythonSliceValue slice,
        int length,
        TextSpan span
    )
    {
        var step = slice.Step is PythonNoneValue ? 1 : GetSliceBound(slice.Step, span);
        if (step == 0)
        {
            throw Fault("DPY4012", "The slice step cannot be zero.", span, "ValueError");
        }

        var start =
            slice.Start is PythonNoneValue
                ? (step > 0 ? 0 : length - 1)
                : AdjustSliceIndex(GetSliceBound(slice.Start, span), length, step);
        var stop =
            slice.Stop is PythonNoneValue
                ? (step > 0 ? length : -1)
                : AdjustSliceIndex(GetSliceBound(slice.Stop, span), length, step);
        return (start, stop, step);
    }

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
        var promoted = PromoteTruthValue(value);
        if (promoted is not PythonWholeNumberValue wholeNumber)
        {
            throw Fault("DPY4011", "Slice indices must be integers or None.", span, "TypeError");
        }

        if (wholeNumber.Value > int.MaxValue)
        {
            return int.MaxValue;
        }

        if (wholeNumber.Value < int.MinValue)
        {
            return int.MinValue;
        }

        return (int)wholeNumber.Value;
    }

    internal static void AssignListSlice(
        PythonListValue list,
        PythonSliceValue slice,
        PythonValue value,
        TextSpan span
    )
    {
        var values = MaterializeValues(value, span);
        var (start, stop, step) = GetSliceIndices(slice, list.Elements.Count, span);
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

        var indices = EnumerateSliceIndices(slice, list.Elements.Count, span).ToList();
        if (indices.Count != values.Count)
        {
            throw Fault(
                "DPY4012",
                $"An extended slice of size {indices.Count} cannot accept "
                    + $"{values.Count} value(s).",
                span,
                "ValueError"
            );
        }

        for (var position = 0; position < indices.Count; position++)
        {
            list.Elements[indices[position]] = values[position];
        }
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
                $"'{deletable.Type.Name}' object doesn't support item deletion",
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

            case PythonListValue list when index is PythonSliceValue slice:
            {
                var indices = EnumerateSliceIndices(slice, list.Elements.Count, span).ToList();
                indices.Sort();
                for (var position = indices.Count - 1; position >= 0; position--)
                {
                    list.Elements.RemoveAt(indices[position]);
                }

                return;
            }
            case PythonListValue list:
                list.Elements.RemoveAt(GetSequenceIndex(index, list.Elements.Count, span));
                return;
            case PythonDictionaryValue dictionary
                when TryFindDictionaryItem(dictionary, index, out var item):
                dictionary.Items.Remove(item);
                dictionary.SizeVersion++;
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
        var values = MaterializeValues(iterable, span, userIteration);
        list.Elements.AddRange(values);
    }

    internal static void RepeatListInPlace(PythonListValue list, PythonValue count, TextSpan span)
    {
        var promoted = PromoteTruthValue(count);
        if (promoted is not PythonWholeNumberValue wholeNumber)
        {
            throw Fault(
                "DPY4011",
                "A list can only be repeated by an integer count.",
                span,
                "TypeError"
            );
        }

        if (wholeNumber.Value <= 0)
        {
            list.Elements.Clear();
            return;
        }

        var snapshot = list.Elements.ToArray();
        for (var repetition = 1; repetition < wholeNumber.Value; repetition++)
        {
            list.Elements.AddRange(snapshot);
        }
    }

    internal static void AddToSet(PythonSetValue set, PythonValue value, TextSpan span)
    {
        if (!IsHashable(value))
        {
            throw Fault(
                "DPY4014",
                $"cannot use '{GetTypeName(value)}' as a set element (unhashable type: '{GetTypeName(value)}')",
                span,
                "TypeError"
            );
        }

        foreach (var element in set.Elements)
        {
            if (KeysMatch(element, value, span))
            {
                return;
            }
        }

        set.Elements.Add(value);
    }

    /// <summary>
    /// Key identity for hashed containers: identical, or hash-equal and `==`. Builtin
    /// values always hash consistently with equality, so the hash gate only matters for
    /// user classes whose `__eq__` and `__hash__` disagree (CPython keeps both entries).
    /// </summary>
    internal static bool KeysMatch(PythonValue candidate, PythonValue key, TextSpan span = default)
    {
        if (ReferenceEquals(candidate, key))
        {
            return true;
        }

        if (
            (candidate is PythonManagedObjectValue || key is PythonManagedObjectValue)
            && ComputePythonHash(candidate, span) != ComputePythonHash(key, span)
        )
        {
            return false;
        }

        return AreEqual(candidate, key);
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
        UserIterationDispatcher? userIteration = null
    )
    {
        var values = new List<PythonValue>();
        var iterator = GetIterator(iterable, span, userIteration);
        while (TryGetNext(iterator, out var value, span))
        {
            values.Add(value);
        }

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
    )
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left is PythonMappingProxyValue leftProxy)
        {
            return RichCompare(leftProxy.Mapping, right, comparison, span);
        }
        if (UserObjectProtocols.TryRichCompare(left, right, comparison, span, out var userResult))
        {
            return PythonTruthValue.FromBoolean(IsTrue(userResult));
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
            return RichCompare(rightProxy.Mapping, left, Reverse(comparison), span);
        }

        if (comparison is PythonRichComparison.Equal or PythonRichComparison.NotEqual)
        {
            var equals = AreEqual(left, right);
            return PythonTruthValue.FromBoolean(
                comparison == PythonRichComparison.Equal ? equals : !equals
            );
        }

        if (HasUnorderedFloatingPointOperand(left, right))
        {
            return PythonTruthValue.False;
        }

        var ordered = CompareOrdered(left, right, span);
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
    /// Computes the value returned by the Python <c>hash()</c> builtin. Integers follow
    /// CPython's modular algorithm exactly; native values delegate to their tp_hash through
    /// the Stable-ABI bridge; other hashable values reuse the runtime's internal hash.
    /// </summary>
    internal static BigInteger ComputePythonHash(PythonValue value, TextSpan span = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var modulus = (BigInteger.One << 61) - 1;
        BigInteger hash = value switch
        {
            PythonMappingProxyValue proxy => ComputePythonHash(proxy.Mapping, span),
            PythonTruthValue truth => truth.Value ? 1 : 0,
            PythonWholeNumberValue whole when whole.Value >= 0 => whole.Value % modulus,
            PythonWholeNumberValue whole => -((-whole.Value) % modulus),
            PythonExternalObjectValue external => external.Protocol.GetHash(span),
            PythonManagedObjectValue instance
                when UserObjectProtocols.TryGetHash(instance, span, out var userHash) => userHash,
            _ => GetPythonHash(value, span),
        };
        return hash == -1 ? -2 : hash;
    }

    internal static int GetPythonHash(PythonValue value, TextSpan span = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            PythonNoneValue => 0x1462_0a3,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            PythonWholeNumberValue whole => whole.Value.GetHashCode(),
            PythonFloatingPointValue floatingPoint => GetFloatingPointHash(floatingPoint.Value),
            PythonComplexValue complex => GetComplexHash(complex.Value),
            PythonTextValue text => StringComparer.Ordinal.GetHashCode(text.Value),
            PythonByteSequenceValue bytes => GetByteHash(bytes.Value),
            PythonTupleValue tuple => GetTupleHash(tuple, span),
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
            PythonSetValue { IsFrozen: true } frozen => GetFrozenSetHash(frozen, span),
            PythonMappingProxyValue proxy => GetPythonHash(proxy.Mapping, span),
            PythonListValue or PythonDictionaryValue or PythonSetValue => throw Fault(
                "DPY4014",
                $"unhashable type: '{GetTypeName(value)}'",
                span,
                "TypeError"
            ),
            _ => RuntimeHelpers.GetHashCode(value),
        };
    }

    private static int GetFrozenSetHash(PythonSetValue frozen, TextSpan span)
    {
        // Order-insensitive combination so equal frozensets hash equally.
        var hash = 0;
        foreach (var element in frozen.Elements)
        {
            hash ^= GetPythonHash(element, span);
        }

        return HashCode.Combine(hash, frozen.Elements.Count);
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
            PythonListValue => "list",
            PythonTupleValue => "tuple",
            PythonDictionaryValue => "dict",
            PythonMappingProxyValue => "mappingproxy",
            PythonSliceValue => "slice",
            PythonSetValue set => set.IsFrozen ? "frozenset" : "set",
            PythonDictionaryViewValue view => view.Kind,
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
            PythonIteratorValue => "iterator",
            PythonModuleValue => "module",
            PythonManagedTypeValue { Metaclass: PythonManagedTypeValue metaclass } =>
                metaclass.Name,
            PythonManagedTypeValue or PythonBuiltinTypeValue => "type",
            PythonSuperProxyValue => "super",
            PythonPropertyValue => "property",
            PythonStaticMethodValue => "staticmethod",
            PythonClassMethodValue => "classmethod",
            PythonManagedObjectValue instance => instance.Type.Name,
            PythonExternalObjectValue => "object",
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
        error switch
        {
            PythonRuntimeException fault => fault.PythonExceptionTypeName == "AttributeError",
            PythonRaisedException raised => raised.Value.TypeName == "AttributeError"
                || raised.Value.ManagedType?.Mro.Any(type =>
                    type.ExceptionBaseName == "AttributeError"
                ) == true,
            _ => false,
        };

    private static bool IsDataDescriptor(PythonValue value) =>
        value switch
        {
            PythonDescriptorValue descriptor => descriptor.IsDataDescriptor,
            PythonPropertyValue => true,
            _ => GetManagedType(value) is { } type
                && (
                    TryGetTypeAttribute(type, "__set__", out _)
                    || TryGetTypeAttribute(type, "__delete__", out _)
                ),
        };

    private static bool HasDescriptorGetter(PythonValue value) =>
        value is PythonDescriptorValue or PythonPropertyValue
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
            PythonDescriptorValue descriptor when instance is not null => descriptor.Get(instance),
            PythonPropertyValue property when instance is not null => GetPropertyValue(
                property,
                instance,
                attributeName ?? GetTypeName(owner),
                span
            ),
            PythonProtocolFunctionValue function when instance is not null =>
                new PythonBoundMethodValue(function.Name, instance, function),
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
    private static bool TryResolveSuperAttribute(
        PythonSuperProxyValue proxy,
        string name,
        out PythonValue value
    )
    {
        var mro = proxy.Instance switch
        {
            PythonManagedObjectValue managed when managed.Type.Mro.Contains(proxy.DefiningType) =>
                managed.Type.Mro,
            PythonExceptionValue { ManagedType: { } exceptionType }
                when exceptionType.Mro.Contains(proxy.DefiningType) => exceptionType.Mro,
            PythonManagedTypeValue subtype when subtype.Mro.Contains(proxy.DefiningType) =>
                subtype.Mro,
            PythonManagedTypeValue { Metaclass: PythonManagedTypeValue metaclass }
                when metaclass.Mro.Contains(proxy.DefiningType) => metaclass.Mro,
            _ => proxy.DefiningType.Mro,
        };
        var searching = false;
        foreach (var current in mro)
        {
            if (!searching)
            {
                searching = ReferenceEquals(current, proxy.DefiningType);
                continue;
            }

            if (current.Attributes.TryGetValue(name, out value!))
            {
                return true;
            }
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
        foreach (var current in type.Mro)
        {
            if (current.Attributes.TryGetValue(name, out value!))
            {
                return true;
            }
        }

        if (type.IsMetaclass && PythonTypeProtocols.TryGetAttribute(name, out value!))
        {
            return true;
        }
        if (name == "__init_subclass__")
        {
            value = PythonTypeProtocols.InitSubclass;
            return true;
        }
        value = null!;
        return false;
    }

    internal static int GetSequenceIndex(PythonValue index, int count, TextSpan span)
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
            throw Fault("DPY4011", "Sequence indices must be integers.", span, "TypeError");
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

        var keyHash = ComputePythonHash(key, span);
        if (TryFindDictionaryItem(dictionary, key, keyHash, out var item))
        {
            item.Value = value;
            return;
        }

        dictionary.Items.Add(new PythonDictionaryItemValue(key, value, keyHash));
        dictionary.SizeVersion++;
    }

    internal static bool TryFindDictionaryItem(
        PythonDictionaryValue dictionary,
        PythonValue key,
        out PythonDictionaryItemValue item
    ) => TryFindDictionaryItem(dictionary, key, ComputePythonHash(key), out item);

    private static bool TryFindDictionaryItem(
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
            // Retain existing builtin numeric equality semantics; user keys use
            // the insertion hash, never a callback re-entering namespace lookup.
            var matches =
                ReferenceEquals(candidate.Key, key)
                || (
                    (
                        candidate.Key is not PythonManagedObjectValue
                            && key is not PythonManagedObjectValue
                        || candidate.KeyHash == keyHash
                    ) && AreEqual(candidate.Key, key)
                );
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
            PythonListValue or PythonDictionaryValue => false,
            PythonTupleValue tuple => tuple.Elements.All(IsHashable),
            PythonManagedObjectValue instance => UserObjectProtocols.IsHashable(instance),
            _ => true,
        };

    internal static bool AreEqual(PythonValue left, PythonValue right)
    {
        if (left is PythonMappingProxyValue leftProxy)
        {
            return AreEqual(leftProxy.Mapping, right);
        }
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
                && leftSet.Elements.All(element =>
                    rightSet.Elements.Any(candidate => AreEqual(candidate, element))
                );
        }

        left = PromoteTruthValue(left);
        right = PromoteTruthValue(right);

        if (IsNumeric(left) && IsNumeric(right))
        {
            if (left is PythonComplexValue || right is PythonComplexValue)
            {
                return ToComplex(left) == ToComplex(right);
            }

            if (left is PythonFloatingPointValue || right is PythonFloatingPointValue)
            {
                return ToDouble(left) == ToDouble(right);
            }

            return ((PythonWholeNumberValue)left).Value == ((PythonWholeNumberValue)right).Value;
        }

        return (left, right) switch
        {
            (PythonNoneValue, PythonNoneValue) => true,
            (PythonTextValue leftText, PythonTextValue rightText) => string.Equals(
                leftText.Value,
                rightText.Value,
                StringComparison.Ordinal
            ),
            (PythonByteSequenceValue leftBytes, PythonByteSequenceValue rightBytes) => leftBytes
                .Value.AsSpan()
                .SequenceEqual(rightBytes.Value),
            (PythonListValue leftList, PythonListValue rightList) => AreSequencesEqual(
                leftList.Elements,
                rightList.Elements
            ),
            (PythonTupleValue leftTuple, PythonTupleValue rightTuple) => AreSequencesEqual(
                leftTuple.Elements,
                rightTuple.Elements
            ),
            (PythonDictionaryValue leftDictionary, PythonDictionaryValue rightDictionary) =>
                AreDictionariesEqual(leftDictionary, rightDictionary),
            _ => ReferenceEquals(left, right),
        };
    }

    private static bool AreSequencesEqual(
        IReadOnlyList<PythonValue> left,
        IReadOnlyList<PythonValue> right
    )
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!AreEqual(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
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

    internal static int CompareOrdered(PythonValue left, PythonValue right, TextSpan span)
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

            if (left is PythonFloatingPointValue || right is PythonFloatingPointValue)
            {
                var leftValue = ToDouble(left);
                var rightValue = ToDouble(right);
                return leftValue.CompareTo(rightValue);
            }

            return ((PythonWholeNumberValue)left).Value.CompareTo(
                ((PythonWholeNumberValue)right).Value
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
                "Values of these types cannot be ordered.",
                span,
                "TypeError"
            ),
        };
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

    private static int GetFloatingPointHash(double value) =>
        double.IsFinite(value) && double.IsInteger(value)
            ? new BigInteger(value).GetHashCode()
            : value.GetHashCode();

    private static int GetComplexHash(Complex value) =>
        value.Imaginary == 0 ? GetFloatingPointHash(value.Real) : value.GetHashCode();

    private static bool HasUnorderedFloatingPointOperand(PythonValue left, PythonValue right) =>
        left is PythonFloatingPointValue { Value: var leftValue } && double.IsNaN(leftValue)
        || right is PythonFloatingPointValue { Value: var rightValue } && double.IsNaN(rightValue);

    private static int GetTupleHash(PythonTupleValue tuple, TextSpan span)
    {
        var hash = new HashCode();
        foreach (var value in tuple.Elements)
        {
            hash.Add(GetPythonHash(value, span));
        }

        return hash.ToHashCode();
    }

    private static bool IsNumeric(PythonValue value) =>
        value is PythonWholeNumberValue or PythonFloatingPointValue or PythonComplexValue;

    private static PythonValue PromoteTruthValue(PythonValue value) =>
        value is PythonTruthValue truth
            ? PythonWholeNumberValue.Create(truth.Value ? BigInteger.One : BigInteger.Zero)
            : value;

    private static double ToDouble(PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole => (double)whole.Value,
            PythonFloatingPointValue floatingPoint => floatingPoint.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static Complex ToComplex(PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole => new Complex((double)whole.Value, 0),
            PythonFloatingPointValue floatingPoint => new Complex(floatingPoint.Value, 0),
            PythonComplexValue complex => complex.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    internal static PythonRuntimeException MissingAttribute(
        string typeName,
        string name,
        TextSpan span
    ) => Fault("DPY4022", $"'{typeName}' object has no attribute '{name}'", span, "AttributeError");

    internal static PythonRuntimeException Fault(
        string code,
        string message,
        TextSpan span,
        string exceptionType
    ) => new(code, message, span, exceptionType);
}
