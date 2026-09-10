using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Inherited type slots, kept separate from VM-owned class construction.</summary>
internal static class PythonTypeProtocols
{
    internal static bool IsType(PythonValue value) =>
        value is PythonManagedTypeValue or PythonBuiltinTypeValue or PythonExceptionTypeValue;

    internal static bool IsMetaclass(PythonValue value) =>
        ReferenceEquals(value, PythonBuiltinTypes.Type)
        || value is PythonManagedTypeValue { IsMetaclass: true };

    private static readonly Lazy<Dictionary<string, PythonValue>> OwnAttributes = new(
        CreateOwnAttributes
    );
    private static readonly Lazy<PythonDictionaryValue> Dictionary = new(CreateDictionary);

    internal static PythonDictionaryValue GetDictionary() => Dictionary.Value;

    internal static bool TryGetAttribute(string name, out PythonValue value)
    {
        if (OwnAttributes.Value.TryGetValue(name, out value!))
            return true;
        if (name == "__init_subclass__")
        {
            value = InitSubclass;
            return true;
        }
        return false;
    }

    private static Dictionary<string, PythonValue> CreateOwnAttributes()
    {
        var attributes = new Dictionary<string, PythonValue>(StringComparer.Ordinal);
        foreach (
            var name in new[]
            {
                "__new__",
                "__init__",
                "__call__",
                "__prepare__",
                "__getattribute__",
                "__setattr__",
                "__delattr__",
                "mro",
            }
        )
        {
            CreateAttribute(name, out var value);
            attributes.Add(name, value);
        }
        foreach (var name in PythonTypeMetadata.Names)
            attributes.Add(name, new PythonTypeMetadataDescriptorValue(name));
        return attributes;
    }

    private static PythonDictionaryValue CreateDictionary()
    {
        var dictionary = new PythonDictionaryValue([]);
        foreach (var (name, value) in OwnAttributes.Value)
            ManagedObjectProtocols.SetDictionaryItem(
                dictionary,
                new PythonTextValue(name),
                value,
                default
            );
        return dictionary;
    }

    private static bool CreateAttribute(string name, out PythonValue value)
    {
        value = name switch
        {
            "__new__" => New,
            "__init__" => Slot(name, Initialize),
            "__call__" => Slot(
                name,
                (self, args, names, values, span) =>
                    UserObjectProtocols.Dispatcher!.CallType(self, args, names, values, span)
            ),
            "__prepare__" => new PythonClassMethodValue(Prepare),
            "__init_subclass__" => InitSubclass,
            "mro" => Mro,
            "__getattribute__" => Slot(
                name,
                (self, args, names, _, span) =>
                {
                    RequireArguments(name, args, names, 1, span);
                    return ManagedObjectProtocols.GetAttributeCore(
                        self,
                        RequireName(args[0], span),
                        span
                    );
                }
            ),
            "__setattr__" => Slot(
                name,
                (self, args, names, _, span) =>
                {
                    RequireArguments(name, args, names, 2, span);
                    ManagedObjectProtocols.SetAttributeCore(
                        self,
                        RequireName(args[0], span),
                        args[1],
                        span
                    );
                    return PythonNoneValue.Instance;
                }
            ),
            "__delattr__" => Slot(
                name,
                (self, args, names, _, span) =>
                {
                    RequireArguments(name, args, names, 1, span);
                    ManagedObjectProtocols.DeleteAttributeCore(
                        self,
                        RequireName(args[0], span),
                        span
                    );
                    return PythonNoneValue.Instance;
                }
            ),
            _ => null!,
        };
        return value is not null;
    }

    internal static PythonValue InitSubclass { get; } =
        new PythonClassMethodValue(
            Function(
                "__init_subclass__",
                (arguments, names, _, span) =>
                {
                    if (arguments.Count != 1 || names.Count != 0)
                    {
                        var owner =
                            arguments.Count > 0
                                ? arguments[0] switch
                                {
                                    PythonManagedTypeValue type => type.Name,
                                    PythonBuiltinTypeValue builtin => builtin.Name,
                                    _ => "object",
                                }
                                : "object";
                        throw Error(
                            $"{owner}.__init_subclass__() takes no keyword arguments",
                            span
                        );
                    }
                    return PythonNoneValue.Instance;
                }
            )
        );

    private static PythonBuiltinFunctionValue New { get; } =
        Function(
            "__new__",
            (arguments, names, values, span) =>
            {
                if (arguments.Count == 0)
                    throw Error("type.__new__(): not enough arguments", span);
                if (!IsType(arguments[0]))
                    throw Error(
                        $"type.__new__(X): X is not a type object ({ManagedObjectProtocols.GetTypeName(arguments[0])})",
                        span
                    );
                if (
                    !PythonBuiltinTypes
                        .GetMro(arguments[0])
                        .Elements.Any(value => ReferenceEquals(value, PythonBuiltinTypes.Type))
                )
                {
                    var typeName = arguments[0] switch
                    {
                        PythonManagedTypeValue managed => managed.Name,
                        PythonBuiltinTypeValue builtin => builtin.Name,
                        PythonExceptionTypeValue exception => exception.Name,
                        _ => "X",
                    };
                    throw Error(
                        $"type.__new__({typeName}): {typeName} is not a subtype of type",
                        span
                    );
                }
                return UserObjectProtocols.Dispatcher!.CreateType(
                    arguments[0],
                    arguments.Skip(1).ToArray(),
                    names,
                    values,
                    span
                );
            }
        );

    private static PythonBuiltinFunctionValue Prepare { get; } =
        Function("__prepare__", (_, _, _, _) => new PythonDictionaryValue([]));

    private static PythonProtocolFunctionValue Mro { get; } = CreateMro();

    private static PythonProtocolFunctionValue CreateMro()
    {
        static PythonValue Invoke(
            PythonValue? receiver,
            IReadOnlyList<PythonValue> arguments,
            IReadOnlyList<string> names,
            IReadOnlyList<PythonValue> values
        )
        {
            var bound = receiver is not null;
            if (receiver is null)
            {
                if (arguments.Count == 0)
                    throw Error("unbound method type.mro() needs an argument", default);
                receiver = arguments[0];
                arguments = arguments.Skip(1).ToArray();
            }
            if (!IsType(receiver))
                throw Error(
                    $"descriptor 'mro' for 'type' objects doesn't apply to a '{ManagedObjectProtocols.GetTypeName(receiver)}' object",
                    default
                );
            var ownerName = bound
                ? receiver switch
                {
                    PythonManagedTypeValue type => type.QualName ?? type.Name,
                    PythonBuiltinTypeValue type => type.Name,
                    PythonExceptionTypeValue type => type.Name,
                    _ => "type",
                }
                : "type";
            if (names.Count != 0)
                throw Error($"{ownerName}.mro() takes no keyword arguments", default);
            if (arguments.Count != 0)
                throw Error(
                    $"{ownerName}.mro() takes no arguments ({arguments.Count} given)",
                    default
                );
            return PythonTypeMro.Compute(receiver, default);
        }
        return new("mro", (self, arguments) => Invoke(self, arguments, [], []), Invoke);
    }

    private static PythonValue Initialize(
        PythonValue self,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        if (arguments.Count is not (1 or 3))
            throw Error("type.__init__() takes 1 or 3 arguments", span);
        if (arguments.Count == 1 && names.Count != 0)
            throw Error("type.__init__() takes no keyword arguments", span);
        return PythonNoneValue.Instance;
    }

    private static PythonProtocolFunctionValue Slot(
        string name,
        Func<
            PythonValue,
            IReadOnlyList<PythonValue>,
            IReadOnlyList<string>,
            IReadOnlyList<PythonValue>,
            TextSpan,
            PythonValue
        > invoke
    )
    {
        PythonValue Call(
            PythonValue? receiver,
            IReadOnlyList<PythonValue> arguments,
            IReadOnlyList<string> names,
            IReadOnlyList<PythonValue> values
        )
        {
            if (receiver is null)
            {
                if (arguments.Count == 0)
                    throw Error($"descriptor '{name}' of 'type' object needs an argument", default);
                receiver = arguments[0];
                arguments = arguments.Skip(1).ToArray();
            }
            if (!IsType(receiver))
                throw Error(
                    $"descriptor '{name}' requires a 'type' object but received a '{ManagedObjectProtocols.GetTypeName(receiver)}'",
                    default
                );
            return invoke(receiver, arguments, names, values, default);
        }
        return new(name, (self, args) => Call(self, args, [], []), Call);
    }

    internal static PythonBuiltinFunctionValue BindBuiltin(
        PythonBuiltinFunctionValue function,
        PythonValue receiver
    ) =>
        new(
            function.Name,
            (arguments, span) => function.Invoke([receiver, .. arguments], span),
            function.InvokeWithKeywords is { } keywords
                ? (arguments, names, values, span) =>
                    keywords([receiver, .. arguments], names, values, span)
                : null
        );

    private static PythonBuiltinFunctionValue Function(string name, BuiltinKeywordInvoker invoke) =>
        new(name, (arguments, span) => invoke(arguments, [], [], span), invoke);

    private static string RequireName(PythonValue name, TextSpan span) =>
        name is PythonTextValue text
            ? text.Value
            : throw Error("attribute name must be string", span);

    private static void RequireArguments(
        string name,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> names,
        int count,
        TextSpan span
    )
    {
        if (names.Count != 0 || arguments.Count != count)
            throw Error(
                $"type.{name}() takes exactly {count + 1} arguments ({arguments.Count + 1} given)",
                span
            );
    }

    private static PythonRuntimeException Error(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, "TypeError");
}
