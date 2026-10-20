using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The bounded <c>typing</c> re-export surface. Every name here delegates to machinery the
/// runtime already has: <c>Optional</c> and <c>Union</c> build the same PEP 604 union that
/// <c>int | str</c> builds, the container aliases build the same <c>GenericAlias</c> that
/// <c>list[int]</c> builds, and <c>get_type_hints</c> reads the annotations the deferred
/// annotate bodies already evaluate.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately absent: <c>TypeVar</c>, <c>ParamSpec</c>, <c>TypeVarTuple</c>,
/// <c>Generic</c>, <c>Protocol</c>, <c>overload</c> and <c>dataclass_transform</c>. Those
/// need subscriptable user classes and type-parameter objects, which a separate workstream
/// owns; a competing implementation here would collide with it. Also absent for the same
/// reason are the <c>collections.abc</c> re-exports (<c>Sequence</c>, <c>Mapping</c>,
/// <c>Iterable</c> and friends) and <c>Annotated</c>.
/// </para>
/// <para>
/// Known divergences from CPython, all deliberate: a parameterized container renders as
/// its builtin (<c>list[int]</c>, not <c>typing.List[int]</c>) because it is the shared
/// <c>GenericAlias</c>, and it compares equal to the builtin spelling where CPython keeps
/// them distinct; <c>Any</c> renders as a class and is an accepted <c>isinstance</c>
/// operand, where CPython prints <c>typing.Any</c> and raises; <c>get_origin</c> of
/// <c>Callable</c> reports <c>typing.Callable</c> because <c>collections.abc.Callable</c> is
/// not built; and <c>Union['X']</c> is <c>'X'</c> rather than <c>ForwardRef('X')</c>, since
/// the runtime has no <c>ForwardRef</c>.
/// </para>
/// </remarks>
internal static class PythonTyping
{
    private const string Module = "typing";

    /// <summary>`typing.Any`. A real type object, so `int | Any` builds a union the way CPython does.</summary>
    private static readonly PythonBuiltinTypeValue Any = new(
        "Any",
        (_, span) => throw Error("Any cannot be instantiated", span)
    )
    {
        ModuleName = Module,
    };

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue("Any", Any);
        globals.SetValue("TYPE_CHECKING", PythonTruthValue.False);
        // `typing.Text` is a plain alias for `str`, so it re-exports the builtin itself.
        globals.SetValue("Text", PythonBuiltinTypes.Str);

        // `typing.Union` is the union type itself, exactly as in CPython, so
        // `typing.Union is type(int | str)` holds and its repr is `<class 'typing.Union'>`.
        globals.SetValue("Union", PythonBuiltinTypes.Union);
        globals.SetValue("Optional", CreateForm(TypingFormKind.Optional));
        globals.SetValue("Final", CreateForm(TypingFormKind.Final));
        globals.SetValue("ClassVar", CreateForm(TypingFormKind.ClassVar));
        globals.SetValue("Literal", CreateForm(TypingFormKind.Literal));
        globals.SetValue("NoReturn", CreateForm(TypingFormKind.NoReturn));
        globals.SetValue("Never", CreateForm(TypingFormKind.Never));
        globals.SetValue("Self", CreateForm(TypingFormKind.Self));

        globals.SetValue(
            "List",
            CreateForm(TypingFormKind.Container, "List", PythonBuiltinTypes.List, "list")
        );
        globals.SetValue(
            "Dict",
            CreateForm(TypingFormKind.Container, "Dict", PythonBuiltinTypes.Dict, "dict", 2)
        );
        globals.SetValue(
            "Set",
            CreateForm(TypingFormKind.Container, "Set", PythonBuiltinTypes.Set, "set")
        );
        globals.SetValue(
            "FrozenSet",
            CreateForm(
                TypingFormKind.Container,
                "FrozenSet",
                PythonBuiltinTypes.Frozenset,
                "frozenset"
            )
        );
        globals.SetValue(
            "Tuple",
            CreateForm(TypingFormKind.Container, "Tuple", PythonBuiltinTypes.Tuple, "tuple", 0)
        );
        globals.SetValue(
            "Type",
            CreateForm(TypingFormKind.Container, "Type", PythonBuiltinTypes.Type, "type")
        );
        globals.SetValue("Callable", CreateForm(TypingFormKind.Callable));
        globals.SetValue("Annotated", CreateForm(TypingFormKind.Annotated));
        globals.SetValue("Generic", CreateForm(TypingFormKind.Generic));
        globals.SetValue("Protocol", CreateForm(TypingFormKind.Protocol));
        globals.SetValue(
            "runtime_checkable",
            new PythonBuiltinFunctionValue("runtime_checkable", RuntimeCheckable)
        );
        globals.SetValue("NoDefault", NoDefault);
        globals.SetValue(
            "TypeVar",
            new PythonBuiltinFunctionValue(
                "TypeVar",
                (arguments, span) => CreateTypeVar(arguments, [], [], span),
                (positional, names, values, span) => CreateTypeVar(positional, names, values, span)
            )
        );
        Overloads.Clear();
        globals.SetValue(
            "overload",
            new PythonBuiltinFunctionValue(
                "overload",
                (arguments, span) => Overload(arguments, span)
            )
        );
        globals.SetValue(
            "get_overloads",
            new PythonBuiltinFunctionValue(
                "get_overloads",
                (arguments, span) => GetOverloads(arguments, span)
            )
        );

        globals.SetValue(
            "cast",
            new PythonBuiltinFunctionValue("cast", Cast).WithSignature(
                ["typ", "val"],
                [null, null],
                positionalOnly: 2
            )
        );
        globals.SetValue(
            "get_origin",
            new PythonBuiltinFunctionValue(
                "get_origin",
                (arguments, span) => GetOrigin(RequireSingle("get_origin", arguments, span), span)
            )
        );
        globals.SetValue(
            "get_args",
            new PythonBuiltinFunctionValue(
                "get_args",
                (arguments, span) => GetArguments(RequireSingle("get_args", arguments, span))
            )
        );
        globals.SetValue(
            "get_type_hints",
            new PythonBuiltinFunctionValue("get_type_hints", GetTypeHints).WithSignature(
                ["obj", "globalns", "localns", "include_extras"],
                [null, PythonNoneValue.Instance, PythonNoneValue.Instance, PythonTruthValue.False]
            )
        );
    }

    /// <summary>
    /// `cast(typ, val)`: a runtime no-op that returns the value unchanged, exactly as CPython's
    /// does. Both parameters are positional-only, which is why the binder reports a keyword as
    /// unexpected.
    /// </summary>
    private static PythonValue Cast(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        if (arguments.Count < 2)
        {
            throw Error(
                arguments.Count == 0
                    ? "cast() missing 2 required positional arguments: 'typ' and 'val'"
                    : "cast() missing 1 required positional argument: 'val'",
                span
            );
        }
        if (arguments.Count > 2)
        {
            throw Error(
                $"cast() takes 2 positional arguments but {arguments.Count} were given",
                span
            );
        }
        return arguments[1];
    }

    private static PythonValue RequireSingle(
        string name,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count != 1)
        {
            throw Error(
                $"{name}() missing 1 required positional argument: 'tp'",
                span,
                "TypeError"
            );
        }
        return arguments[0];
    }

    /// <summary>
    /// `get_origin(tp)`: the unsubscripted origin. A union reports the `typing.Union` class the
    /// runtime gives a PEP 604 union, and a bare container re-export reports the builtin it
    /// stands for.
    /// </summary>
    private static PythonValue GetOrigin(PythonValue target, TextSpan span) =>
        target switch
        {
            PythonGenericAliasValue alias => alias.Origin,
            PythonExternalObjectValue { Protocol: AnnotatedAlias annotated } => annotated.Form,
            PythonTypeUnionValue => PythonBuiltinTypes.Union,
            PythonExternalObjectValue { Protocol: TypingForm form } => form.Origin
                ?? PythonNoneValue.Instance,
            _ => PythonNoneValue.Instance,
        };

    /// <summary>`get_args(tp)`: the subscription arguments, empty for anything unparameterized.</summary>
    private static PythonTupleValue GetArguments(PythonValue target) =>
        target switch
        {
            PythonGenericAliasValue alias => new PythonTupleValue([.. alias.Arguments]),
            PythonExternalObjectValue { Protocol: AnnotatedAlias annotated } =>
                new PythonTupleValue([.. annotated.Arguments]),
            PythonTypeUnionValue union => new PythonTupleValue([.. union.Members]),
            _ => new PythonTupleValue([]),
        };

    /// <summary>
    /// `get_type_hints(obj, globalns=None, localns=None, include_extras=False)`: the object's
    /// annotations. The runtime evaluates annotations when they are first read, so the two
    /// namespaces have nothing left to resolve and are accepted but unused; `include_extras` is
    /// likewise inert because `Annotated` is not part of this slice.
    /// </summary>
    private static PythonDictionaryValue GetTypeHints(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count == 0)
        {
            throw Error(
                "get_type_hints() missing 1 required positional argument: 'obj'",
                span,
                "TypeError"
            );
        }

        var annotations = arguments[0] switch
        {
            PythonFunctionValue function => ManagedObjectProtocols.GetFunctionAnnotations(
                function,
                span
            ),
            PythonManagedTypeValue type => ManagedObjectProtocols.GetTypeAnnotations(type, span),
            PythonModuleValue module => ManagedObjectProtocols.GetModuleAnnotations(module, span),
            // CPython reports no hints rather than an error for the builtins it can inspect.
            PythonBuiltinTypeValue
            or PythonBuiltinFunctionValue
            or PythonProtocolFunctionValue
            or PythonBoundMethodValue => null,
            _ => throw Error(
                $"{arguments[0].ToRepresentationString()} does not have annotations",
                span,
                "TypeError"
            ),
        };
        if (annotations is null)
        {
            return new PythonDictionaryValue([]);
        }

        // The runtime caches the evaluated mapping on the object, so hand back a copy the
        // way CPython hands back a fresh dict; a class or module whose stored mapping is
        // not a dictionary is refused with the same wording annotationlib uses.
        return ManagedObjectProtocols.CopyAnnotations(arguments[0], annotations, span);
    }

    /// <summary>`typing.NoDefault`: the sentinel a TypeVar without a default reports.</summary>
    private static readonly PythonValue NoDefault = new PythonExternalObjectValue(
        new NoDefaultSentinel()
    );

    private sealed class NoDefaultSentinel : PythonExternalObjectProtocol
    {
        private const string Display = "typing.NoDefault";

        PythonValue PythonExternalObjectProtocol.Call(
            IReadOnlyList<PythonValue> arguments,
            TextSpan span
        ) => throw Error($"Cannot instantiate {Display}", span);

        PythonValue PythonExternalObjectProtocol.CallWithKeywords(
            IReadOnlyList<PythonValue> arguments,
            IReadOnlyList<string> keywordNames,
            IReadOnlyList<PythonValue> keywordValues,
            TextSpan span
        ) => throw Error($"Cannot instantiate {Display}", span);

        PythonValue PythonExternalObjectProtocol.GetAttribute(string attribute, TextSpan span) =>
            attribute == "__module__"
                ? new PythonTextValue(Module)
                : throw ManagedObjectProtocols.Fault(
                    "DPY4022",
                    $"'{Display}' object has no attribute '{attribute}'",
                    span,
                    "AttributeError"
                );

        PythonValue PythonExternalObjectProtocol.GetItem(PythonValue index, TextSpan span) =>
            throw Error($"{Display} is not subscriptable", span);

        long PythonExternalObjectProtocol.GetHash(TextSpan span) => 0;

        int PythonExternalObjectProtocol.GetLength(TextSpan span) => 0;

        PythonTruthValue PythonExternalObjectProtocol.RichCompare(
            PythonValue other,
            PythonRichComparison comparison,
            TextSpan span
        ) =>
            comparison switch
            {
                PythonRichComparison.Equal => PythonTruthValue.FromBoolean(
                    ReferenceEquals(other, NoDefault)
                ),
                PythonRichComparison.NotEqual => PythonTruthValue.FromBoolean(
                    !ReferenceEquals(other, NoDefault)
                ),
                _ => PythonTruthValue.False,
            };

        bool PythonExternalObjectProtocol.IsInstanceOf(PythonValue value, TextSpan span) => false;

        string PythonExternalObjectProtocol.ToDisplayString() => Display;

        string PythonExternalObjectProtocol.ToRepresentationString() => Display;
    }

    private sealed record TypeVarState(
        string NameValue,
        PythonTupleValue Constraints,
        PythonValue Bound,
        bool Covariant,
        bool Contravariant,
        bool InferVariance,
        PythonValue Default
    );

    private static readonly PythonManagedTypeValue TypeVarType = CreateTypeVarType();

    private static PythonManagedTypeValue CreateTypeVarType()
    {
        var type = new PythonManagedTypeValue("TypeVar") { Module = Module };
        type.Attributes["__module__"] = new PythonTextValue(Module);
        type.Attributes["__qualname__"] = new PythonTextValue("TypeVar");
        type.Attributes["__repr__"] = PythonUserTypes.Method(
            "TypeVar",
            "__repr__",
            (self, _) => new PythonTextValue($"~{TypeVarOf(self!).NameValue}")
        );
        AddMember("__name__", state => new PythonTextValue(state.NameValue));
        AddMember("__constraints__", state => state.Constraints);
        AddMember("__bound__", state => state.Bound);
        AddMember("__covariant__", state => PythonTruthValue.FromBoolean(state.Covariant));
        AddMember("__contravariant__", state => PythonTruthValue.FromBoolean(state.Contravariant));
        AddMember("__infer_variance__", state => PythonTruthValue.FromBoolean(state.InferVariance));
        AddMember("__default__", state => state.Default);
        return type;

        void AddMember(string name, Func<TypeVarState, PythonValue> getter) =>
            type.Attributes[name] = new PythonPropertyValue(
                new PythonProtocolFunctionValue(
                    name,
                    (_, arguments) => getter(TypeVarOf(arguments[0]))
                ),
                null,
                null
            );
    }

    private static TypeVarState TypeVarOf(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: TypeVarState state }
            ? state
            : throw Error("the value is not a TypeVar", default);

    private static PythonManagedObjectValue CreateTypeVar(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        string? name = null;
        var constraints = new List<PythonValue>();
        PythonValue? bound = null;
        PythonValue? default_ = null;
        var covariant = false;
        var contravariant = false;
        var inferVariance = false;
        if (positional.Count > 0)
        {
            if (positional[0] is not PythonTextValue text)
            {
                throw Error(
                    "typevar() argument 'name' must be str, not "
                        + ManagedObjectProtocols.GetTypeName(positional[0]),
                    span
                );
            }
            name = text.Value;
            constraints.AddRange(positional.Skip(1));
        }
        for (var index = 0; index < names.Count; index++)
        {
            switch (names[index])
            {
                case "bound":
                    bound = values[index];
                    break;
                case "covariant":
                    covariant = ManagedObjectProtocols.IsTrue(values[index]);
                    break;
                case "contravariant":
                    contravariant = ManagedObjectProtocols.IsTrue(values[index]);
                    break;
                case "default":
                    default_ = values[index];
                    break;
                case "infer_variance":
                    inferVariance = ManagedObjectProtocols.IsTrue(values[index]);
                    break;
                default:
                    throw Error(
                        $"typevar() got an unexpected keyword argument '{names[index]}'",
                        span
                    );
            }
        }
        if (name is null)
        {
            throw Error("typevar() missing required argument 'name' (pos 1)", span);
        }
        return new PythonManagedObjectValue(
            TypeVarType,
            new TypeVarState(
                name,
                new PythonTupleValue([.. constraints]),
                bound ?? PythonNoneValue.Instance,
                covariant,
                contravariant,
                inferVariance,
                default_ ?? NoDefault
            )
        );
    }

    /// <summary>`Annotated[T, meta, ...]`: the alias keeps its origin form, its arguments and
    /// the `__metadata__` tuple CPython exposes.</summary>
    private sealed class AnnotatedAlias(PythonValue form, PythonValue[] arguments)
        : PythonExternalObjectProtocol
    {
        internal PythonValue Form { get; } = form;

        internal PythonValue[] Arguments { get; } = arguments;

        private string Render() =>
            $"{Form.ToDisplayString()}[{string.Join(", ", Arguments.Select(RenderArgument))}]";

        private static string RenderArgument(PythonValue argument) =>
            argument switch
            {
                PythonManagedTypeValue type => type.QualifiedDisplayName,
                PythonBuiltinTypeValue type => type.Name,
                _ => argument.ToRepresentationString(),
            };

        PythonValue PythonExternalObjectProtocol.Call(
            IReadOnlyList<PythonValue> arguments,
            TextSpan span
        ) => throw Error("Cannot instantiate typing.Annotated", span);

        PythonValue PythonExternalObjectProtocol.CallWithKeywords(
            IReadOnlyList<PythonValue> arguments,
            IReadOnlyList<string> keywordNames,
            IReadOnlyList<PythonValue> keywordValues,
            TextSpan span
        ) => throw Error("Cannot instantiate typing.Annotated", span);

        PythonValue PythonExternalObjectProtocol.GetAttribute(string attribute, TextSpan span) =>
            attribute switch
            {
                "__module__" => new PythonTextValue(Module),
                "__origin__" => Arguments[0],
                "__metadata__" => new PythonTupleValue([.. Arguments.Skip(1)]),
                // An Annotated alias answers an unknown attribute from the type it wraps,
                // which is where CPython's own fallback lands.
                _ => ManagedObjectProtocols.GetAttribute(Arguments[0], attribute, span),
            };

        PythonValue PythonExternalObjectProtocol.GetItem(PythonValue index, TextSpan span) =>
            throw Error("Cannot instantiate typing.Annotated", span);

        long PythonExternalObjectProtocol.GetHash(TextSpan span) =>
            StringComparer.Ordinal.GetHashCode(Render());

        int PythonExternalObjectProtocol.GetLength(TextSpan span) => Arguments.Length;

        PythonTruthValue PythonExternalObjectProtocol.RichCompare(
            PythonValue other,
            PythonRichComparison comparison,
            TextSpan span
        )
        {
            var equal =
                other is PythonExternalObjectValue { Protocol: AnnotatedAlias annotated }
                && Render() == annotated.Render();
            return comparison switch
            {
                PythonRichComparison.Equal => PythonTruthValue.FromBoolean(equal),
                PythonRichComparison.NotEqual => PythonTruthValue.FromBoolean(!equal),
                _ => PythonTruthValue.False,
            };
        }

        bool PythonExternalObjectProtocol.IsInstanceOf(PythonValue value, TextSpan span) => false;

        string PythonExternalObjectProtocol.ToDisplayString() => Render();

        string PythonExternalObjectProtocol.ToRepresentationString() => Render();
    }

    private static readonly Dictionary<string, List<PythonValue>> Overloads = new(
        StringComparer.Ordinal
    );

    /// <summary>The module and qualname CPython keys its overload registry by.</summary>
    private static string OverloadKey(PythonValue function)
    {
        static string Part(PythonValue value, string name)
        {
            try
            {
                return
                    ManagedObjectProtocols.GetAttribute(value, name, default)
                        is PythonTextValue text
                    ? text.Value
                    : string.Empty;
            }
            catch (PythonRuntimeException)
            {
                return string.Empty;
            }
            catch (PythonRaisedException)
            {
                return string.Empty;
            }
        }
        return Part(function, "__module__") + "." + Part(function, "__qualname__");
    }

    private static readonly PythonBuiltinFunctionValue OverloadDummy = new(
        "_overload_dummy",
        (_, _) =>
            throw Error(
                "You should not call an overloaded function. A series of "
                    + "@overload-decorated functions outside a stub module should always be "
                    + "followed by an implementation that is not @overload-ed.",
                default,
                "NotImplementedError"
            )
    );

    private static PythonBuiltinFunctionValue Overload(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count != 1)
        {
            throw Error("overload() missing 1 required positional argument: 'func'", span);
        }
        if (ManagedObjectProtocols.IsCallable(arguments[0]))
        {
            var key = OverloadKey(arguments[0]);
            if (!Overloads.TryGetValue(key, out var list))
            {
                list = [];
                Overloads[key] = list;
            }
            list.Add(arguments[0]);
        }
        return OverloadDummy;
    }

    private static PythonListValue GetOverloads(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        if (arguments.Count != 1)
        {
            throw Error("get_overloads() missing 1 required positional argument: 'func'", span);
        }
        return new PythonListValue(
            Overloads.TryGetValue(OverloadKey(arguments[0]), out var list) ? [.. list] : []
        );
    }

    /// <summary>
    /// A class's generic bases: the TypeVars collected from `Generic[...]`/`Protocol[...]`
    /// and from parameterized bases become its own `__parameters__` (possibly empty, as
    /// on a subclass of a generic), and a `Protocol` base marks the class. Every generic
    /// class answers `Class[args]` with a generic alias through the subscription hook.
    /// </summary>
    internal static void ApplyGenericBases(
        PythonManagedTypeValue type,
        PythonTupleValue? originalBases,
        PythonTupleValue resolvedBases
    )
    {
        var parameters = new List<PythonValue>();
        var listed = new List<PythonValue>();
        var aliased = new List<PythonValue>();
        var sawForm = false;
        var sawAlias = false;
        var protocol = false;
        var formName = "Generic";
        if (originalBases is not null)
        {
            foreach (var baseValue in originalBases.Elements)
            {
                if (
                    baseValue is PythonExternalObjectValue
                    {
                        Protocol: TypingForm { Kind: TypingFormKind.Generic }
                    }
                )
                {
                    throw Error("Cannot inherit from plain Generic", default);
                }
                if (TryGetGenericBase(baseValue, out var isProtocol, out var arguments))
                {
                    sawForm = true;
                    protocol |= isProtocol;
                    if (isProtocol)
                    {
                        formName = "Protocol";
                    }
                    listed.AddRange(arguments);
                    AddTypeVars(parameters, arguments);
                }
                else if (
                    baseValue is PythonGenericAliasValue { Origin: PythonManagedTypeValue } alias
                )
                {
                    sawAlias = true;
                    AddTypeVars(parameters, alias.Arguments);
                    aliased.AddRange(TypeVarArguments(alias.Arguments));
                }
            }
        }
        var inherited = false;
        var protocolBase = false;
        foreach (var baseValue in resolvedBases.Elements)
        {
            if (
                baseValue is PythonManagedTypeValue baseType
                && ManagedObjectProtocols.TryGetTypeAttribute(baseType, "__parameters__", out _)
            )
            {
                inherited = true;
            }
            if (
                baseValue is PythonManagedTypeValue protocolType
                && protocolType.Attributes.TryGetValue("_is_protocol", out var marker)
                && marker is PythonTruthValue { Value: true }
            )
            {
                protocolBase = true;
            }
        }
        if (sawForm)
        {
            var unlisted = aliased.Where(value => !listed.Contains(value)).ToArray();
            if (unlisted.Length != 0)
            {
                throw Error(
                    $"Some type variables ({JoinTypeVars(unlisted)}) are not listed in "
                        + $"{formName}[{JoinTypeVars(listed)}]",
                    default
                );
            }
        }
        if (protocol)
        {
            type.Attributes["_is_protocol"] = PythonTruthValue.True;
        }
        else if (protocolBase)
        {
            // A plain subclass of a protocol is concrete: CPython writes False into its
            // own dictionary rather than letting the marker be inherited.
            type.Attributes["_is_protocol"] = PythonTruthValue.False;
        }
        if (sawForm || sawAlias || inherited)
        {
            type.Attributes["__parameters__"] = new PythonTupleValue([.. parameters]);
            if (!type.Attributes.TryGetValue("__class_getitem__", out _))
            {
                type.Attributes["__class_getitem__"] = new PythonProtocolFunctionValue(
                    "__class_getitem__",
                    (_, arguments) => PythonGenericAliasValue.Create(type, arguments[0])
                )
                {
                    IsGenericPlaceholder = true,
                };
            }
        }
    }

    /// <summary>
    /// `Class[args]` for a generic class: the arguments must match its own
    /// `__parameters__` in number; a class with none is not generic at all.
    /// </summary>
    internal static PythonValue SubscribeGeneric(
        PythonManagedTypeValue type,
        PythonTupleValue parameters,
        PythonValue index,
        TextSpan span
    )
    {
        var arguments = index is PythonTupleValue tuple ? tuple.Elements : [index];
        var expected = parameters.Elements.Length;
        var display = type.ToRepresentationString();
        if (expected == 0)
        {
            throw Error($"{display} is not a generic class", span);
        }
        if (arguments.Length != expected)
        {
            throw Error(
                arguments.Length < expected
                    ? $"Too few arguments for {display}; actual {arguments.Length}, "
                        + $"expected at least {expected}"
                    : $"Too many arguments for {display}; actual {arguments.Length}, "
                        + $"expected {expected}",
                span
            );
        }
        return PythonGenericAliasValue.Create(type, index);
    }

    /// <summary>The TypeVars appearing in a type-argument list, in order.</summary>
    internal static PythonValue[] TypeVarArguments(IReadOnlyList<PythonValue> values)
    {
        var result = new List<PythonValue>();
        foreach (var value in values)
        {
            if (IsTypeVar(value))
            {
                result.Add(value);
            }
            else if (value is PythonGenericAliasValue alias)
            {
                result.AddRange(TypeVarArguments(alias.Arguments));
            }
        }
        return [.. result];
    }

    private static void AddTypeVars(List<PythonValue> target, IReadOnlyList<PythonValue> values)
    {
        foreach (var value in TypeVarArguments(values))
        {
            if (!target.Contains(value))
            {
                target.Add(value);
            }
        }
    }

    private static string JoinTypeVars(IEnumerable<PythonValue> values) =>
        string.Join(", ", values.Select(value => value.ToRepresentationString()));

    /// <summary>
    /// Whether a base expression is the unsubscripted `Generic` or `Protocol` name, which
    /// CPython treats as a plain class base rather than a recorded `__orig_bases__` entry.
    /// </summary>
    internal static bool IsBareGenericOrProtocolForm(PythonValue value) =>
        value
            is PythonExternalObjectValue
            {
                Protocol: TypingForm { Kind: TypingFormKind.Generic or TypingFormKind.Protocol }
            };

    /// <summary>Whether a base expression is a `Generic[...]`/`Protocol[...]` form.</summary>
    internal static bool TryGetGenericBase(
        PythonValue value,
        out bool protocol,
        out PythonValue[] arguments
    )
    {
        switch (value)
        {
            case PythonExternalObjectValue { Protocol: TypingForm form }
                when form.Kind is TypingFormKind.Generic or TypingFormKind.Protocol:
                protocol = form.Kind == TypingFormKind.Protocol;
                arguments = [];
                return true;
            case PythonExternalObjectValue { Protocol: GenericBaseValue generic }:
                protocol = generic.IsProtocol;
                arguments = generic.Arguments;
                return true;
            default:
                protocol = false;
                arguments = [];
                return false;
        }
    }

    private static bool IsTypeVar(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: TypeVarState };

    /// <summary>`runtime_checkable(ProtocolClass)`: marks the class and answers it unchanged.
    /// Structural `isinstance` checks are not part of this slice.</summary>
    private static PythonValue RuntimeCheckable(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        if (arguments.Count == 0)
        {
            throw Error("runtime_checkable() missing 1 required positional argument: 'cls'", span);
        }
        if (arguments.Count > 1)
        {
            throw Error(
                $"runtime_checkable() takes 1 positional argument but "
                    + $"{arguments.Count} were given",
                span
            );
        }
        if (!PythonTypeProtocols.IsType(arguments[0]))
        {
            throw Error("issubclass() arg 1 must be a class", span);
        }
        if (arguments[0] is PythonManagedTypeValue type)
        {
            type.Attributes["_is_runtime_protocol"] = PythonTruthValue.True;
        }
        return arguments[0];
    }

    private sealed class GenericBaseValue(
        PythonValue form,
        bool isProtocol,
        PythonValue[] arguments
    ) : PythonExternalObjectProtocol
    {
        internal PythonValue Form { get; } = form;

        internal bool IsProtocol { get; } = isProtocol;

        internal PythonValue[] Arguments { get; } = arguments;

        private string Render() =>
            $"{Form.ToDisplayString()}[{string.Join(", ", Arguments.Select(RenderArgument))}]";

        private static string RenderArgument(PythonValue argument) =>
            argument switch
            {
                PythonManagedTypeValue type => type.QualifiedDisplayName,
                PythonBuiltinTypeValue type => type.Name,
                _ => argument.ToDisplayString(),
            };

        PythonValue PythonExternalObjectProtocol.Call(
            IReadOnlyList<PythonValue> arguments,
            TextSpan span
        ) => throw Error($"Cannot instantiate {Render()}", span);

        PythonValue PythonExternalObjectProtocol.CallWithKeywords(
            IReadOnlyList<PythonValue> arguments,
            IReadOnlyList<string> keywordNames,
            IReadOnlyList<PythonValue> keywordValues,
            TextSpan span
        ) => throw Error($"Cannot instantiate {Render()}", span);

        PythonValue PythonExternalObjectProtocol.GetAttribute(string attribute, TextSpan span) =>
            attribute switch
            {
                "__module__" => new PythonTextValue(Module),
                "__origin__" => Form,
                "__args__" => new PythonTupleValue([.. Arguments]),
                "__parameters__" => new PythonTupleValue([.. Arguments.Where(IsTypeVar)]),
                "__mro_entries__" => new PythonBuiltinFunctionValue(
                    "__mro_entries__",
                    (_, _) => new PythonTupleValue([PythonBuiltinFunctions.Object])
                ),
                _ => throw ManagedObjectProtocols.Fault(
                    "DPY4022",
                    $"'{Render()}' object has no attribute '{attribute}'",
                    span,
                    "AttributeError"
                ),
            };

        PythonValue PythonExternalObjectProtocol.GetItem(PythonValue index, TextSpan span) =>
            throw Error($"{Render()} is not subscriptable", span);

        long PythonExternalObjectProtocol.GetHash(TextSpan span) =>
            StringComparer.Ordinal.GetHashCode(Render());

        int PythonExternalObjectProtocol.GetLength(TextSpan span) => Arguments.Length;

        PythonTruthValue PythonExternalObjectProtocol.RichCompare(
            PythonValue other,
            PythonRichComparison comparison,
            TextSpan span
        )
        {
            var equal =
                other is PythonExternalObjectValue { Protocol: GenericBaseValue generic }
                && Render() == generic.Render();
            return comparison switch
            {
                PythonRichComparison.Equal => PythonTruthValue.FromBoolean(equal),
                PythonRichComparison.NotEqual => PythonTruthValue.FromBoolean(!equal),
                _ => PythonTruthValue.False,
            };
        }

        bool PythonExternalObjectProtocol.IsInstanceOf(PythonValue value, TextSpan span) => false;

        string PythonExternalObjectProtocol.ToDisplayString() => Render();

        string PythonExternalObjectProtocol.ToRepresentationString() => Render();
    }

    private static PythonExternalObjectValue CreateForm(
        TypingFormKind kind,
        string? name = null,
        PythonBuiltinTypeValue? origin = null,
        string? originName = null,
        int arity = 1
    )
    {
        var form = new TypingForm(kind, name ?? kind.ToString(), origin, originName, arity);
        var value = new PythonExternalObjectValue(form);
        form.Self = value;
        return value;
    }

    /// <summary>The names `typing` exposes as subscriptable forms.</summary>
    private enum TypingFormKind
    {
        /// <summary>`Optional[X]`, which is `X | None`.</summary>
        Optional,

        /// <summary>`Final` and `Final[X]`.</summary>
        Final,

        /// <summary>`ClassVar` and `ClassVar[X]`.</summary>
        ClassVar,

        /// <summary>`Literal[...]`, whose arguments stay literal values.</summary>
        Literal,

        /// <summary>`NoReturn`, `Never` and `Self`: bare forms with nothing to subscribe.</summary>
        NoReturn,
        Never,
        Self,

        /// <summary>`List`, `Dict` and friends: aliases for a builtin container type.</summary>
        Container,

        /// <summary>`Callable[[arg, ...], result]`.</summary>
        Callable,

        /// <summary>`Annotated[type, metadata, ...]`.</summary>
        Annotated,

        /// <summary>`Generic[T, ...]` and `Protocol[T, ...]` bases.</summary>
        Generic,
        Protocol,
    }

    /// <summary>
    /// One subscriptable `typing` name. It renders and reports its module the way CPython's
    /// special forms do, and each subscript is decided by the kind: the union forms build a
    /// PEP 604 union while the rest build a `GenericAlias`.
    /// </summary>
    private sealed class TypingForm(
        TypingFormKind kind,
        string name,
        PythonBuiltinTypeValue? origin,
        string? originName,
        int arity
    ) : PythonExternalObjectProtocol
    {
        private readonly string _displayName = $"{Module}.{name}";

        internal TypingFormKind Kind { get; } = kind;

        /// <summary>The value that wraps this form, needed as the origin of its own aliases.</summary>
        internal PythonValue Self { get; set; } = null!;

        /// <summary>
        /// The unsubscripted origin: the builtin a container re-export stands for, the form
        /// itself for `Callable` (whose CPython origin, `collections.abc.Callable`, is not part
        /// of this slice), and null for the forms CPython reports no origin for.
        /// </summary>
        internal PythonValue? Origin => origin ?? (Kind == TypingFormKind.Callable ? Self : null);

        PythonValue PythonExternalObjectProtocol.Call(
            IReadOnlyList<PythonValue> arguments,
            TextSpan span
        ) => throw Instantiate(span);

        PythonValue PythonExternalObjectProtocol.CallWithKeywords(
            IReadOnlyList<PythonValue> arguments,
            IReadOnlyList<string> keywordNames,
            IReadOnlyList<PythonValue> keywordValues,
            TextSpan span
        ) => throw Instantiate(span);

        private PythonRuntimeException Instantiate(TextSpan span) =>
            Error(
                Kind switch
                {
                    TypingFormKind.Container =>
                        $"Type {name} cannot be instantiated; use {originName}() instead",
                    _ => $"Cannot instantiate typing.{name}",
                },
                span
            );

        PythonValue PythonExternalObjectProtocol.GetAttribute(string attribute, TextSpan span) =>
            attribute switch
            {
                "__module__" => new PythonTextValue(Module),
                "__mro_entries__" when Kind is TypingFormKind.Generic or TypingFormKind.Protocol =>
                    new PythonBuiltinFunctionValue(
                        "__mro_entries__",
                        (_, _) => new PythonTupleValue([PythonBuiltinFunctions.Object])
                    ),
                "__origin__" when Origin is not null => Origin,
                _ => throw ManagedObjectProtocols.Fault(
                    "DPY4022",
                    $"'{_displayName}' object has no attribute '{attribute}'",
                    span,
                    "AttributeError"
                ),
            };

        PythonValue PythonExternalObjectProtocol.GetItem(PythonValue index, TextSpan span)
        {
            PythonValue[] members = index is PythonTupleValue tuple ? tuple.Elements : [index];
            return Kind switch
            {
                TypingFormKind.Optional when members.Length == 1 => PythonTypeUnionValue.Combine(
                    Member(members[0]),
                    PythonBuiltinTypes.NoneType
                ),
                TypingFormKind.Optional => throw Error(
                    $"typing.Optional requires a single type. Got {Describe(members)}.",
                    span
                ),
                TypingFormKind.Final when members.Length == 1 => Alias(index),
                TypingFormKind.Final => throw Error(
                    $"typing.Final accepts only single type. Got {Describe(members)}.",
                    span
                ),
                TypingFormKind.ClassVar when members.Length == 1 => Alias(index),
                TypingFormKind.ClassVar => throw Error(
                    $"typing.ClassVar accepts only single type. Got {Describe(members)}.",
                    span
                ),
                TypingFormKind.Literal => Alias(index),
                TypingFormKind.NoReturn or TypingFormKind.Never or TypingFormKind.Self =>
                    throw Error($"typing.{name} is not subscriptable", span),
                TypingFormKind.Callable => SubscribeCallable(members, index, span),
                TypingFormKind.Generic or TypingFormKind.Protocol => members.All(IsTypeVar)
                    ? new PythonExternalObjectValue(
                        new GenericBaseValue(Self, Kind == TypingFormKind.Protocol, members)
                    )
                    : throw Error(
                        $"Parameters to {name}[...] must all be type variables "
                            + "or parameter specification variables.",
                        span
                    ),
                TypingFormKind.Annotated => members.Length >= 2
                    ? new PythonExternalObjectValue(new AnnotatedAlias(Self, members))
                    : throw Error(
                        "Annotated[...] should be used with at least two arguments "
                            + "(a type and an annotation).",
                        span
                    ),
                // An arity of zero is the unbounded one `Tuple` needs.
                _ when arity == 0 || members.Length == arity => Alias(index),
                _ => throw Error(
                    $"Too {(members.Length < arity ? "few" : "many")} arguments for typing.{name};"
                        + $" actual {members.Length}, expected {arity}",
                    span
                ),
            };
        }

        /// <summary>`Callable[[arg, ...], result]`: two arguments, the first a parameter list or `...`.</summary>
        private PythonGenericAliasValue SubscribeCallable(
            PythonValue[] members,
            PythonValue index,
            TextSpan span
        )
        {
            if (members.Length != 2 || members[0] is not (PythonListValue or PythonEllipsisValue))
            {
                throw Error("Callable must be used as Callable[[arg, ...], result].", span);
            }
            return Alias(index);
        }

        /// <summary>A parameterized form is its own origin, so `Final[int]` reports `typing.Final`.</summary>
        private PythonGenericAliasValue Alias(PythonValue index) =>
            PythonGenericAliasValue.Create(Origin ?? Self, index);

        /// <summary>`None` is written as `None` in a subscription but contributes `NoneType`.</summary>
        private static PythonValue Member(PythonValue member) =>
            member is PythonNoneValue ? PythonBuiltinTypes.NoneType : member;

        /// <summary>The `(...)` a CPython diagnostic prints for a rejected subscription.</summary>
        private static string Describe(PythonValue[] members) =>
            new PythonTupleValue([.. members]).ToRepresentationString();

        long PythonExternalObjectProtocol.GetHash(TextSpan span) =>
            StringComparer.Ordinal.GetHashCode(_displayName);

        int PythonExternalObjectProtocol.GetLength(TextSpan span) =>
            throw Error($"object of type '{_displayName}' has no len()", span);

        PythonTruthValue PythonExternalObjectProtocol.RichCompare(
            PythonValue other,
            PythonRichComparison comparison,
            TextSpan span
        ) =>
            comparison switch
            {
                PythonRichComparison.Equal => PythonTruthValue.FromBoolean(
                    ReferenceEquals(other, Self)
                ),
                PythonRichComparison.NotEqual => PythonTruthValue.FromBoolean(
                    !ReferenceEquals(other, Self)
                ),
                _ => throw Error(
                    $"'{_displayName}' not supported between instances of '{_displayName}'",
                    span
                ),
            };

        /// <summary>
        /// A container re-export answers a class check against the builtin it stands for, and
        /// the remaining special forms are refused the way CPython refuses them.
        /// </summary>
        bool PythonExternalObjectProtocol.IsInstanceOf(PythonValue value, TextSpan span) =>
            Kind switch
            {
                _ when origin is not null => PythonBuiltinTypes.IsInstance(value, origin),
                _ => throw Error($"typing.{name} cannot be used with isinstance()", span),
            };

        string PythonExternalObjectProtocol.ToDisplayString() => _displayName;

        // `Generic` and `Protocol` are classes in CPython, though the runtime models them
        // as forms; they alone carry a class repr.
        string PythonExternalObjectProtocol.ToRepresentationString() =>
            Kind is TypingFormKind.Generic or TypingFormKind.Protocol
                ? $"<class '{_displayName}'>"
                : _displayName;
    }

    private static PythonRuntimeException Error(
        string message,
        TextSpan span,
        string type = "TypeError"
    ) => ManagedObjectProtocols.Fault("DPY4039", message, span, type);
}
