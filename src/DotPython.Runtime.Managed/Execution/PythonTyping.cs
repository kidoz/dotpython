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
/// <c>GenericAlias</c>; <c>Any</c> renders as a class and is an accepted <c>isinstance</c>
/// operand, where CPython prints <c>typing.Any</c> and raises; <c>repr(typing.Union)</c> is
/// <c>typing.Union</c> rather than <c>&lt;class 'typing.Union'&gt;</c>; <c>get_origin</c> of
/// <c>Callable</c> reports <c>typing.Callable</c> because <c>collections.abc.Callable</c> is
/// not built; and a union still exposes no <c>__origin__</c> or <c>__args__</c>, which
/// <c>get_origin</c> and <c>get_args</c> answer instead.
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

        var union = CreateForm(TypingFormKind.Union);
        globals.SetValue("Union", union);
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

        // The runtime caches the evaluated mapping on the object, so hand back a copy the way
        // CPython hands back a fresh dict.
        var result = new PythonDictionaryValue([]);
        foreach (var item in annotations.Items)
        {
            ManagedObjectProtocols.SetDictionaryItem(result, item.Key, item.Value, span);
        }
        return result;
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

        /// <summary>`Union[...]`, which is the members joined by `|`.</summary>
        Union,

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

        /// <summary>The value that wraps this form, needed as the origin of its own aliases.</summary>
        internal PythonValue Self { get; set; } = null!;

        /// <summary>
        /// The unsubscripted origin: the builtin a container re-export stands for, the form
        /// itself for `Callable` (whose CPython origin, `collections.abc.Callable`, is not part
        /// of this slice), and null for the forms CPython reports no origin for.
        /// </summary>
        internal PythonValue? Origin => origin ?? (kind == TypingFormKind.Callable ? Self : null);

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
                kind switch
                {
                    TypingFormKind.Union => "cannot create 'typing.Union' instances",
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
            return kind switch
            {
                TypingFormKind.Optional when members.Length == 1 => PythonTypeUnionValue.Combine(
                    Member(members[0]),
                    PythonBuiltinTypes.NoneType
                ),
                TypingFormKind.Optional => throw Error(
                    $"typing.Optional requires a single type. Got {Describe(members)}.",
                    span
                ),
                TypingFormKind.Union when members.Length != 0 => Union(members),
                TypingFormKind.Union => throw Error("Cannot take a Union of no types.", span),
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

        private static PythonValue Union(IReadOnlyList<PythonValue> members)
        {
            var combined = (PythonValue?)null;
            foreach (var member in members)
            {
                var next = Member(member);
                combined = combined is null ? next : PythonTypeUnionValue.Combine(combined, next);
            }
            return combined!;
        }

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
        /// A container re-export answers a class check against the builtin it stands for, the
        /// union class answers `False` the way any other class does, and the remaining special
        /// forms are refused the way CPython refuses them.
        /// </summary>
        bool PythonExternalObjectProtocol.IsInstanceOf(PythonValue value, TextSpan span) =>
            kind switch
            {
                _ when origin is not null => PythonBuiltinTypes.IsInstance(value, origin),
                TypingFormKind.Union => false,
                _ => throw Error($"typing.{name} cannot be used with isinstance()", span),
            };

        string PythonExternalObjectProtocol.ToDisplayString() => _displayName;

        string PythonExternalObjectProtocol.ToRepresentationString() => _displayName;
    }

    private static PythonRuntimeException Error(
        string message,
        TextSpan span,
        string type = "TypeError"
    ) => ManagedObjectProtocols.Fault("DPY4039", message, span, type);
}
