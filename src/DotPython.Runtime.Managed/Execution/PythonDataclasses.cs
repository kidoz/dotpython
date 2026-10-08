// Field discovery, generated-method and error semantics follow CPython 3.14.7
// Lib/dataclasses.py:
// https://github.com/python/cpython/blob/v3.14.7/Lib/dataclasses.py
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The <c>dataclasses</c> module: <c>dataclass</c>, <c>field</c>, <c>fields</c>,
/// <c>asdict</c>, <c>astuple</c>, <c>replace</c>, <c>is_dataclass</c>,
/// <c>make_dataclass</c> and the field machinery they share.
/// </summary>
/// <remarks>
/// The generated methods are protocol functions rather than source-compiled functions, so
/// their representations differ from CPython's (<c>&lt;built-in function __init__&gt;</c>
/// instead of <c>&lt;function Point.__init__ at 0x…&gt;</c>). <c>slots=True</c> is refused
/// rather than approximated: the runtime cannot re-create the class around a slot layout
/// after the decorator has run. <c>asdict</c>/<c>astuple</c> recurse through dataclass
/// instances and exact lists, dicts and tuples, and leave every other value as it is
/// where CPython deep-copies it.
/// </remarks>
internal static class PythonDataclasses
{
    private const string Module = "dataclasses";
    private const string ErrorCode = "DPY4040";
    private const string SliceCode = "DPY4041";

    private const string FieldsName = "__dataclass_fields__";
    private const string ParamsName = "__dataclass_params__";
    private const string PostInitName = "__post_init__";

    /// <summary>The parameter names `dataclass()` accepts as keyword arguments.</summary>
    private static readonly string[] DataclassParameters =
    [
        "init",
        "repr",
        "eq",
        "order",
        "unsafe_hash",
        "frozen",
        "match_args",
        "kw_only",
        "slots",
        "weakref_slot",
    ];

    /// <summary>The parameter names `field()` accepts as keyword arguments.</summary>
    private static readonly string[] FieldParameters =
    [
        "default",
        "default_factory",
        "init",
        "repr",
        "hash",
        "compare",
        "metadata",
        "kw_only",
        "doc",
    ];

    /// <summary>`Field.__init__`'s positional parameters, in order.</summary>
    private static readonly string[] FieldInitParameters =
    [
        "default",
        "default_factory",
        "init",
        "repr",
        "hash",
        "compare",
        "metadata",
        "kw_only",
        "doc",
    ];

    private static readonly string[] MakeDataclassParameters =
    [
        "bases",
        "namespace",
        "init",
        "repr",
        "eq",
        "order",
        "unsafe_hash",
        "frozen",
        "match_args",
        "kw_only",
        "slots",
        "weakref_slot",
        "module",
        "decorator",
    ];

    private static readonly PythonManagedTypeValue MissingType = new("_MISSING_TYPE")
    {
        Module = Module,
    };
    private static readonly PythonManagedTypeValue KwOnlyType = new("_KW_ONLY_TYPE")
    {
        Module = Module,
    };
    private static readonly PythonManagedTypeValue FieldBaseType = CreateFieldBaseType();
    private static readonly PythonManagedTypeValue ParamsType = CreateParamsType();
    private static readonly PythonManagedTypeValue FieldType = CreateFieldType();
    private static readonly PythonManagedTypeValue FrozenInstanceErrorType =
        CreateFrozenInstanceErrorType();
    private static readonly PythonManagedObjectValue Missing = new(MissingType);
    private static readonly PythonManagedObjectValue KwOnlyMarker = new(KwOnlyType);
    private static readonly PythonManagedObjectValue PlainFieldMarker = CreateMarker("_FIELD");
    private static readonly PythonManagedObjectValue ClassVarMarker = CreateMarker(
        "_FIELD_CLASSVAR"
    );
    private static readonly PythonManagedObjectValue InitVarMarker = CreateMarker("_FIELD_INITVAR");
    private static readonly PythonMappingProxyValue EmptyMetadata = new(
        new PythonDictionaryValue([])
    );
    private static readonly PythonExternalObjectValue InitVarForm = CreateInitVarForm();

    /// <summary>
    /// The state of a `Field` the VM allocated on its own, e.g. through `Field(...)`.
    /// A field built by `field()` carries its state as the instance payload.
    /// </summary>
    private static readonly ConditionalWeakTable<
        PythonManagedObjectValue,
        FieldState
    > FieldStateTable = new();

    /// <summary>The <c>dataclass</c> function published by <see cref="Initialize"/>.</summary>
    private static PythonValue? _dataclassFunction;

    private static PythonManagedTypeValue CreateFieldBaseType()
    {
        var type = new PythonManagedTypeValue("_FIELD_BASE") { Module = Module };
        type.Attributes["__init__"] = new PythonProtocolFunctionValue(
            "__init__",
            (self, arguments) => InitializeMarker(self, arguments, [], [], default),
            (self, arguments, names, values) =>
                InitializeMarker(self, arguments, names, values, default)
        );
        type.Attributes["__repr__"] = new PythonProtocolFunctionValue(
            "__repr__",
            (self, _) =>
                self is PythonManagedObjectValue marker
                && marker.Attributes.TryGetValue("name", out var name)
                    ? name
                    : new PythonTextValue("_FIELD_BASE")
        );
        return type;
    }

    private static PythonManagedTypeValue CreateParamsType()
    {
        var type = new PythonManagedTypeValue("_DataclassParams") { Module = Module };
        type.Attributes["__repr__"] = new PythonProtocolFunctionValue(
            "__repr__",
            (self, _) => RepresentParams(self)
        );
        return type;
    }

    private static PythonManagedTypeValue CreateFieldType()
    {
        var type = new PythonManagedTypeValue("Field") { Module = Module };
        type.Attributes["__init__"] = new PythonProtocolFunctionValue(
            "__init__",
            (self, arguments) => InitializeField(self, arguments, [], [], default),
            (self, arguments, names, values) =>
                InitializeField(self, arguments, names, values, default)
        );
        type.Attributes["__repr__"] = new PythonProtocolFunctionValue(
            "__repr__",
            (self, _) => RepresentField(self)
        );
        type.Attributes["__set_name__"] = new PythonProtocolFunctionValue(
            "__set_name__",
            (self, arguments) => SetFieldName(self, arguments, default)
        );
        return type;
    }

    private static PythonExternalObjectValue CreateInitVarForm()
    {
        var protocol = new InitVarProtocol();
        var form = new PythonExternalObjectValue(protocol);
        protocol.Self = form;
        return form;
    }

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        _dataclassFunction = CreateDataclass();
        globals.SetValue("dataclass", _dataclassFunction);
        globals.SetValue("field", CreateFieldFunction());
        globals.SetValue("fields", CreateFields());
        globals.SetValue("asdict", CreateAsDict());
        globals.SetValue("astuple", CreateAsTuple());
        globals.SetValue("replace", CreateReplace());
        globals.SetValue("is_dataclass", CreateIsDataclass());
        globals.SetValue("make_dataclass", CreateMakeDataclass());
        globals.SetValue("FrozenInstanceError", FrozenInstanceErrorType);
        globals.SetValue("Field", FieldType);
        globals.SetValue("InitVar", InitVarForm);
        globals.SetValue("MISSING", Missing);
        globals.SetValue("KW_ONLY", KwOnlyMarker);
        globals.SetValue("_MISSING_TYPE", MissingType);
        globals.SetValue("_KW_ONLY_TYPE", KwOnlyType);
        globals.SetValue("_FIELD_BASE", FieldBaseType);
        globals.SetValue("_FIELD", PlainFieldMarker);
        globals.SetValue("_FIELD_CLASSVAR", ClassVarMarker);
        globals.SetValue("_FIELD_INITVAR", InitVarMarker);
        globals.SetValue("_DataclassParams", ParamsType);
    }

    // -----------------------------------------------------------------------------
    // Object model
    // -----------------------------------------------------------------------------

    /// <summary>The three marker kinds a `Field` can carry in `_field_type`.</summary>
    private enum FieldKind
    {
        Field,
        ClassVar,
        InitVar,
    }

    /// <summary>The mutable state a `Field` instance carries.</summary>
    private sealed class FieldState
    {
        internal string? Name;
        internal PythonValue Type = PythonNoneValue.Instance;
        internal PythonValue Default = Missing;
        internal PythonValue DefaultFactory = Missing;
        internal bool Init = true;
        internal bool Repr = true;
        internal PythonValue Hash = PythonNoneValue.Instance;
        internal bool Compare = true;
        internal PythonValue Metadata = EmptyMetadata;
        internal PythonValue KwOnly = Missing;
        internal PythonValue Doc = PythonNoneValue.Instance;

        /// <summary>The `_field_type` marker; null until `_get_field` classifies the field.</summary>
        internal FieldKind? Kind;
    }

    private static PythonManagedObjectValue CreateMarker(string name)
    {
        var marker = new PythonManagedObjectValue(FieldBaseType);
        marker.Attributes["name"] = new PythonTextValue(name);
        return marker;
    }

    private static PythonValue MarkerOf(FieldKind? kind) =>
        kind switch
        {
            FieldKind.ClassVar => ClassVarMarker,
            FieldKind.InitVar => InitVarMarker,
            FieldKind.Field => PlainFieldMarker,
            _ => PythonNoneValue.Instance,
        };

    private static bool IsMissing(PythonValue value) => ReferenceEquals(value, Missing);

    private static bool IsDataclassFunction(PythonValue value) =>
        _dataclassFunction is not null && ReferenceEquals(value, _dataclassFunction);

    private static bool IsFieldObject(PythonValue value) =>
        value is PythonManagedObjectValue { Type: var type } && ReferenceEquals(type, FieldType);

    private static FieldState? StateOf(PythonValue value) =>
        value switch
        {
            PythonManagedObjectValue { Payload: FieldState state } => state,
            PythonManagedObjectValue field when FieldStateTable.TryGetValue(field, out var table) =>
                table,
            _ => null,
        };

    /// <summary>Builds a `Field` object and mirrors its state into the instance dictionary.</summary>
    private static PythonManagedObjectValue CreateField(FieldState state)
    {
        var field = new PythonManagedObjectValue(FieldType, state);
        SyncField(field, state);
        return field;
    }

    private static void SyncField(PythonManagedObjectValue field, FieldState state)
    {
        field.Attributes["name"] = state.Name is null
            ? PythonNoneValue.Instance
            : new PythonTextValue(state.Name);
        field.Attributes["type"] = state.Type;
        field.Attributes["default"] = state.Default;
        field.Attributes["default_factory"] = state.DefaultFactory;
        field.Attributes["init"] = PythonTruthValue.FromBoolean(state.Init);
        field.Attributes["repr"] = PythonTruthValue.FromBoolean(state.Repr);
        field.Attributes["hash"] = state.Hash;
        field.Attributes["compare"] = PythonTruthValue.FromBoolean(state.Compare);
        field.Attributes["metadata"] = state.Metadata;
        field.Attributes["kw_only"] = state.KwOnly;
        field.Attributes["doc"] = state.Doc;
        field.Attributes["_field_type"] = MarkerOf(state.Kind);
    }

    private static PythonNoneValue InitializeField(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var bound = BindSelf(receiver, positional, "Field", span, out positional);
        var state = new FieldState();
        SetFieldDefaults(state);
        if (positional.Count > FieldInitParameters.Length)
        {
            throw Fault(
                $"{FormatPositionalCount(FieldInitParameters.Length, positional.Count, 0)}",
                span
            );
        }
        var slots = new PythonValue?[FieldInitParameters.Length];
        for (var index = 0; index < positional.Count; index++)
        {
            slots[index] = positional[index];
        }
        for (var index = 0; index < keywordNames.Count; index++)
        {
            var slot = Array.IndexOf(FieldInitParameters, keywordNames[index]);
            if (slot < 0)
            {
                throw Fault(
                    $"Field.__init__() got an unexpected keyword argument '{keywordNames[index]}'",
                    span
                );
            }
            if (slots[slot] is not null)
            {
                throw Fault(
                    $"Field.__init__() got multiple values for argument '{keywordNames[index]}'",
                    span
                );
            }
            slots[slot] = keywordValues[index];
        }
        var missing = new List<string>();
        for (var index = 0; index < slots.Length; index++)
        {
            if (slots[index] is null)
            {
                missing.Add(FieldInitParameters[index]);
            }
        }
        if (missing.Count != 0)
        {
            throw Fault(
                $"Field.__init__() missing {missing.Count} required positional argument{(missing.Count == 1 ? "" : "s")}: {JoinNames(missing)}",
                span
            );
        }
        state.Default = slots[0]!;
        state.DefaultFactory = slots[1]!;
        state.Init = ManagedObjectProtocols.IsTrue(slots[2]!);
        state.Repr = ManagedObjectProtocols.IsTrue(slots[3]!);
        state.Hash = slots[4]!;
        state.Compare = ManagedObjectProtocols.IsTrue(slots[5]!);
        state.Metadata =
            slots[6]! is PythonNoneValue ? EmptyMetadata : new PythonMappingProxyValue(slots[6]!);
        state.KwOnly = slots[7]!;
        state.Doc = slots[8]!;
        FieldStateTable.AddOrUpdate(bound, state);
        SyncField(bound, state);
        return PythonNoneValue.Instance;
    }

    private static void SetFieldDefaults(FieldState state)
    {
        state.Default = Missing;
        state.DefaultFactory = Missing;
        state.Init = true;
        state.Repr = true;
        state.Hash = PythonNoneValue.Instance;
        state.Compare = true;
        state.Metadata = EmptyMetadata;
        state.KwOnly = Missing;
        state.Doc = PythonNoneValue.Instance;
        state.Kind = null;
    }

    private static PythonNoneValue InitializeMarker(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var bound = BindSelf(receiver, positional, "_FIELD_BASE", span, out positional);
        if (keywordNames.Count != 0)
        {
            throw Fault(
                $"_FIELD_BASE.__init__() got an unexpected keyword argument '{keywordNames[0]}'",
                span
            );
        }
        if (positional.Count != 1)
        {
            throw Fault(
                $"_FIELD_BASE.__init__() takes 2 positional arguments but {positional.Count + 1} were given",
                span
            );
        }
        bound.Attributes["name"] = positional[0];
        return PythonNoneValue.Instance;
    }

    private static PythonNoneValue SetFieldName(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (receiver is not PythonManagedObjectValue field || StateOf(field) is not { } state)
        {
            return PythonNoneValue.Instance;
        }
        if (arguments.Count != 2)
        {
            throw Fault(
                $"__set_name__() takes 3 positional arguments but {arguments.Count + 1} were given",
                span
            );
        }
        if (
            UserObjectProtocols.TryGetSpecialMethod(
                state.Default,
                "__set_name__",
                out var setter,
                out _
            )
        )
        {
            ManagedObjectProtocols.Call(setter, [arguments[0], arguments[1]], span);
        }
        return PythonNoneValue.Instance;
    }

    private static PythonTextValue RepresentField(PythonValue? self)
    {
        if (self is not PythonManagedObjectValue field || StateOf(field) is not { } state)
        {
            return new PythonTextValue("<Field>");
        }
        if (!PythonRepresentationGuard.TryEnter(field))
        {
            return new PythonTextValue("...");
        }
        try
        {
            return new PythonTextValue(
                "Field("
                    + $"name={RepresentOrNull(state.Name)},"
                    + $"type={state.Type.ToRepresentationString()},"
                    + $"default={state.Default.ToRepresentationString()},"
                    + $"default_factory={state.DefaultFactory.ToRepresentationString()},"
                    + $"init={PythonTruthValue.FromBoolean(state.Init).ToRepresentationString()},"
                    + $"repr={PythonTruthValue.FromBoolean(state.Repr).ToRepresentationString()},"
                    + $"hash={state.Hash.ToRepresentationString()},"
                    + $"compare={PythonTruthValue.FromBoolean(state.Compare).ToRepresentationString()},"
                    + $"metadata={state.Metadata.ToRepresentationString()},"
                    + $"kw_only={state.KwOnly.ToRepresentationString()},"
                    + $"doc={state.Doc.ToRepresentationString()},"
                    + $"_field_type={MarkerOf(state.Kind).ToDisplayString()}"
                    + ")"
            );
        }
        finally
        {
            PythonRepresentationGuard.Exit(field);
        }
    }

    private static string RepresentOrNull(string? name) =>
        name is null ? "None" : new PythonTextValue(name).ToRepresentationString();

    private static PythonTextValue RepresentParams(PythonValue? self)
    {
        var builder = new StringBuilder("_DataclassParams(");
        var first = true;
        if (self is PythonManagedObjectValue parameters)
        {
            foreach (var item in parameters.Attributes)
            {
                if (!first)
                {
                    builder.Append(',');
                }
                first = false;
                builder.Append(item.Key).Append('=').Append(item.Value.ToRepresentationString());
            }
        }
        return new PythonTextValue(builder.Append(')').ToString());
    }

    // -----------------------------------------------------------------------------
    // FrozenInstanceError
    // -----------------------------------------------------------------------------

    /// <summary>
    /// <c>dataclasses.FrozenInstanceError</c>, the AttributeError subclass the frozen
    /// <c>__setattr__</c>/<c>__delattr__</c> pair raises. Built directly, the way
    /// <c>json.JSONDecodeError</c> is, because a native initializer runs without a
    /// class-body frame.
    /// </summary>
    private static PythonManagedTypeValue CreateFrozenInstanceErrorType()
    {
        var exceptionBase = PythonBuiltinTypes.GetExceptionType("AttributeError");
        var type = new PythonManagedTypeValue(
            "FrozenInstanceError",
            exceptionBaseName: "AttributeError"
        )
        {
            Module = Module,
            LayoutBase = exceptionBase,
        };
        type.Attributes["__module__"] = new PythonTextValue(Module);
        type.Attributes["__qualname__"] = new PythonTextValue("FrozenInstanceError");
        type.SetDeclaredBases(new PythonTupleValue([exceptionBase]));
        type.SetResolutionOrder(
            new PythonTupleValue([
                type,
                exceptionBase,
                PythonBuiltinTypes.GetExceptionType("Exception"),
                PythonBuiltinTypes.GetExceptionType("BaseException"),
                PythonBuiltinFunctions.Object,
            ])
        );
        return type;
    }

    private static PythonRaisedException FrozenFault(string message)
    {
        var exception = new PythonExceptionValue("FrozenInstanceError", message)
        {
            ManagedType = FrozenInstanceErrorType,
            Arguments = [new PythonTextValue(message)],
        };
        return new PythonRaisedException(exception);
    }

    // -----------------------------------------------------------------------------
    // InitVar
    // -----------------------------------------------------------------------------

    /// <summary>
    /// <c>dataclasses.InitVar</c>: a callable and subscriptable form. Both
    /// <c>InitVar(int)</c> and <c>InitVar[int]</c> produce the same subscription value,
    /// whose representation is <c>dataclasses.InitVar[int]</c>.
    /// </summary>
    private sealed class InitVarProtocol : PythonExternalObjectProtocol
    {
        private PythonValue? _type;

        internal PythonExternalObjectValue? Self { get; set; }

        private string Display =>
            _type is null ? $"{Module}.InitVar" : $"{Module}.InitVar[{TypeName(_type)}]";

        private static string TypeName(PythonValue value) =>
            value switch
            {
                PythonManagedTypeValue managed => managed.QualName ?? managed.Name,
                PythonBuiltinTypeValue builtin => builtin.Name,
                PythonExceptionTypeValue exception => exception.Name,
                _ => value.ToRepresentationString(),
            };

        private PythonExternalObjectValue RequiredForm(TextSpan span) =>
            Self ?? throw Fault("dataclasses.InitVar is not initialized", span);

        public PythonValue Call(IReadOnlyList<PythonValue> arguments, TextSpan span)
        {
            if (arguments.Count != 1)
            {
                throw Fault(
                    $"InitVar() takes 2 positional arguments but {arguments.Count + 1} were given",
                    span
                );
            }
            return new PythonExternalObjectValue(new InitVarProtocol { _type = arguments[0] });
        }

        public PythonValue CallWithKeywords(
            IReadOnlyList<PythonValue> arguments,
            IReadOnlyList<string> keywordNames,
            IReadOnlyList<PythonValue> keywordValues,
            TextSpan span
        )
        {
            if (keywordNames.Count != 0)
            {
                throw Fault(
                    $"InitVar() got an unexpected keyword argument '{keywordNames[0]}'",
                    span
                );
            }
            return Call(arguments, span);
        }

        public PythonValue GetAttribute(string name, TextSpan span) =>
            name switch
            {
                "__module__" => new PythonTextValue(Module),
                "__qualname__" => new PythonTextValue("InitVar"),
                "__name__" => new PythonTextValue("InitVar"),
                "__mro__" => throw Fault(
                    $"'{Display}' object has no attribute '__mro__'",
                    span,
                    "AttributeError"
                ),
                "type" when _type is not null => _type,
                "type" => throw Fault(
                    $"'{Display}' object has no attribute 'type'",
                    span,
                    "AttributeError"
                ),
                _ => throw Fault(
                    $"'{Display}' object has no attribute '{name}'",
                    span,
                    "AttributeError"
                ),
            };

        public PythonValue GetItem(PythonValue index, TextSpan span) => Call([index], span);

        public long GetHash(TextSpan span) => RuntimeHelpers.GetHashCode(this);

        public int GetLength(TextSpan span) =>
            throw Fault($"object of type '{Display}' has no len()", span);

        public PythonTruthValue RichCompare(
            PythonValue other,
            PythonRichComparison comparison,
            TextSpan span
        ) =>
            comparison switch
            {
                PythonRichComparison.Equal => PythonTruthValue.FromBoolean(
                    ReferenceEquals(other, RequiredForm(span))
                ),
                PythonRichComparison.NotEqual => PythonTruthValue.FromBoolean(
                    !ReferenceEquals(other, RequiredForm(span))
                ),
                _ => throw Fault(
                    $"'{Display}' not supported between instances of '{Display}'",
                    span
                ),
            };

        public bool IsInstanceOf(PythonValue value, TextSpan span) =>
            value is PythonExternalObjectValue { Protocol: InitVarProtocol };

        public string ToDisplayString() => Display;

        public string ToRepresentationString() => Display;
    }

    private static bool IsInitVarForm(PythonValue value) =>
        value is PythonExternalObjectValue { Protocol: InitVarProtocol };

    // -----------------------------------------------------------------------------
    // dataclass()
    // -----------------------------------------------------------------------------

    private static PythonBuiltinFunctionValue CreateDataclass() =>
        new PythonBuiltinFunctionValue(
            "dataclass",
            (arguments, span) => InvokeDataclass(arguments, [], [], span),
            (arguments, names, values, span) => InvokeDataclass(arguments, names, values, span)
        );

    private static PythonValue InvokeDataclass(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (positional.Count > 1)
        {
            throw Fault(
                "dataclass() takes from 0 to 1 positional arguments but "
                    + $"{positional.Count} were given",
                span
            );
        }
        for (var index = 0; index < keywordNames.Count; index++)
        {
            if (keywordNames[index] == "cls")
            {
                throw Fault(
                    "dataclass() got some positional-only arguments passed as keyword arguments: 'cls'",
                    span
                );
            }
        }
        var options = BindOptions(
            "dataclass",
            DataclassParameters,
            DataclassDefaults(),
            [],
            keywordNames,
            keywordValues,
            span
        );
        var wrap = new PythonBuiltinFunctionValue(
            "dataclass.<locals>.wrap",
            (arguments, innerSpan) =>
            {
                if (arguments.Count != 1)
                {
                    throw Fault(
                        "wrap() takes 1 positional argument but " + $"{arguments.Count} were given",
                        innerSpan
                    );
                }
                return ProcessClass(arguments[0], options, innerSpan);
            }
        );
        return positional.Count == 0 ? wrap : ProcessClass(positional[0], options, span);
    }

    /// <summary>The `@dataclass` parameter defaults, in <see cref="DataclassParameters"/> order.</summary>
    private static PythonValue[] DataclassDefaults() =>
        [
            PythonTruthValue.True,
            PythonTruthValue.True,
            PythonTruthValue.True,
            PythonTruthValue.False,
            PythonTruthValue.False,
            PythonTruthValue.False,
            PythonTruthValue.True,
            PythonTruthValue.False,
            PythonTruthValue.False,
            PythonTruthValue.False,
        ];

    private static PythonManagedTypeValue ProcessClass(
        PythonValue cls,
        PythonValue[] options,
        TextSpan span
    )
    {
        var init = ManagedObjectProtocols.IsTrue(options[0]);
        var repr = ManagedObjectProtocols.IsTrue(options[1]);
        var eq = ManagedObjectProtocols.IsTrue(options[2]);
        var order = ManagedObjectProtocols.IsTrue(options[3]);
        var unsafeHash = ManagedObjectProtocols.IsTrue(options[4]);
        var frozen = ManagedObjectProtocols.IsTrue(options[5]);
        var matchArgs = ManagedObjectProtocols.IsTrue(options[6]);
        var kwOnly = ManagedObjectProtocols.IsTrue(options[7]);
        var slots = ManagedObjectProtocols.IsTrue(options[8]);
        var weakrefSlot = ManagedObjectProtocols.IsTrue(options[9]);

        if (cls is not PythonManagedTypeValue type)
        {
            // CPython reaches these two attribute reads before it can fail; replaying the
            // lookups keeps the AttributeError text identical.
            ManagedObjectProtocols.GetAttribute(cls, "__module__", span);
            ManagedObjectProtocols.GetAttribute(cls, "__mro__", span);
            throw Fault(
                $"dataclass() should be called on a class, not '{ManagedObjectProtocols.GetTypeName(cls)}'",
                span
            );
        }

        var fields = new List<KeyValuePair<string, PythonManagedObjectValue>>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var anyFrozenBase = false;
        var allFrozenBases = (bool?)null;
        var hasDataclassBases = false;
        for (var position = type.Mro.Count - 1; position > 0; position--)
        {
            var baseType = type.Mro[position];
            if (
                !ManagedObjectProtocols.TryGetTypeAttribute(
                    baseType,
                    FieldsName,
                    out var baseFieldsValue
                ) || baseFieldsValue is not PythonDictionaryValue baseFields
            )
            {
                continue;
            }
            hasDataclassBases = true;
            foreach (var item in baseFields.Items)
            {
                if (item.Key is not PythonTextValue name || StateOf(item.Value) is not { } state)
                {
                    continue;
                }
                AddField(fields, index, name.Value, (PythonManagedObjectValue)item.Value);
            }
            allFrozenBases ??= true;
            var currentFrozen = false;
            if (
                ManagedObjectProtocols.TryGetTypeAttribute(baseType, ParamsName, out var baseParams)
                && baseParams is PythonManagedObjectValue parameterObject
                && parameterObject.Attributes.TryGetValue("frozen", out var frozenValue)
            )
            {
                currentFrozen = ManagedObjectProtocols.IsTrue(frozenValue);
            }
            allFrozenBases = allFrozenBases.Value && currentFrozen;
            anyFrozenBase = anyFrozenBase || currentFrozen;
        }

        // CPython reads cls.__dict__.get('__annotations__'); a namespace built by
        // type(name, bases, ns) carries that mapping verbatim, while a class statement
        // leaves it to the runtime's annotate callable.
        var annotations =
            type.Attributes.TryGetValue("__annotations__", out var directAnnotations)
            && directAnnotations is PythonDictionaryValue annotationMapping
                ? annotationMapping
                : ManagedObjectProtocols.GetTypeAnnotations(type, span);
        var classFields = new List<PythonManagedObjectValue>();
        var kwOnlySeen = false;
        var defaultKwOnly = kwOnly;
        foreach (var item in annotations.Items)
        {
            if (item.Key is not PythonTextValue annotationName)
            {
                continue;
            }
            var name = annotationName.Value;
            var annotation = item.Value;
            if (ReferenceEquals(annotation, KwOnlyMarker) || IsStringMarker(annotation, "KW_ONLY"))
            {
                if (kwOnlySeen)
                {
                    throw Fault(
                        $"'{name}' is KW_ONLY, but KW_ONLY has already been specified",
                        span
                    );
                }
                kwOnlySeen = true;
                defaultKwOnly = true;
                continue;
            }
            classFields.Add(GetField(type, name, annotation, defaultKwOnly, span));
        }

        foreach (var field in classFields)
        {
            var state = StateOf(field)!;
            AddField(fields, index, state.Name!, field);
            if (
                ManagedObjectProtocols.TryGetTypeAttribute(type, state.Name!, out var current)
                && IsFieldObject(current)
            )
            {
                if (IsMissing(state.Default))
                {
                    type.Attributes.Remove(state.Name!);
                }
                else
                {
                    type.Attributes[state.Name!] = state.Default;
                }
            }
        }

        foreach (var item in type.Attributes)
        {
            if (
                IsFieldObject(item.Value)
                && !annotations.Items.Any(entry =>
                    entry.Key is PythonTextValue text && text.Value == item.Key
                )
            )
            {
                throw Fault($"'{item.Key}' is a field but has no type annotation", span);
            }
        }

        if (hasDataclassBases)
        {
            if (anyFrozenBase && !frozen)
            {
                throw Fault("cannot inherit non-frozen dataclass from a frozen one", span);
            }
            if (allFrozenBases is false && frozen)
            {
                throw Fault("cannot inherit frozen dataclass from a non-frozen one", span);
            }
        }

        var fieldsDictionary = new PythonDictionaryValue([
            .. fields.Select(entry => new PythonDictionaryItemValue(
                new PythonTextValue(entry.Key),
                entry.Value
            )),
        ]);
        type.Attributes[FieldsName] = fieldsDictionary;
        type.Attributes[ParamsName] = CreateParams(options);

        var hasExplicitHash = true;
        if (!type.Attributes.TryGetValue("__hash__", out var classHash))
        {
            hasExplicitHash = false;
        }
        else if (classHash is PythonNoneValue && type.Attributes.TryGetValue("__eq__", out _))
        {
            hasExplicitHash = false;
        }

        if (order && !eq)
        {
            throw Fault("eq must be true if order is true", span, "ValueError");
        }

        var allInitFields = fields
            .Select(entry => entry.Value)
            .Where(field => StateOf(field)!.Kind is FieldKind.Field or FieldKind.InitVar)
            .ToList();
        var stdInitFields = allInitFields
            .Where(field =>
                StateOf(field)!.Init && !ManagedObjectProtocols.IsTrue(StateOf(field)!.KwOnly)
            )
            .ToList();
        var kwOnlyInitFields = allInitFields
            .Where(field =>
                StateOf(field)!.Init && ManagedObjectProtocols.IsTrue(StateOf(field)!.KwOnly)
            )
            .ToList();

        if (init)
        {
            // CPython validates the positional parameters while building __init__ and
            // does so whether or not the class brings its own method.
            PythonManagedObjectValue? seenDefault = null;
            foreach (var field in stdInitFields)
            {
                var state = StateOf(field)!;
                if (!IsMissing(state.Default) || !IsMissing(state.DefaultFactory))
                {
                    seenDefault = field;
                }
                else if (seenDefault is not null)
                {
                    throw Fault(
                        $"non-default argument '{state.Name}' follows default argument "
                            + $"'{StateOf(seenDefault)!.Name}'",
                        span,
                        "TypeError"
                    );
                }
            }
            var hasPostInit = ManagedObjectProtocols.TryGetTypeAttribute(type, PostInitName, out _);
            SetNewAttribute(
                type,
                "__init__",
                CreateInitFunction(
                    type,
                    allInitFields,
                    stdInitFields,
                    kwOnlyInitFields,
                    frozen,
                    hasPostInit
                )
            );
        }

        SetNewAttribute(type, "__replace__", CreateReplaceMethod());

        var fieldList = fields
            .Select(entry => entry.Value)
            .Where(field => StateOf(field)!.Kind == FieldKind.Field)
            .ToList();

        if (repr)
        {
            SetNewAttribute(type, "__repr__", CreateReprFunction(type, fieldList));
        }

        if (eq)
        {
            SetNewAttribute(type, "__eq__", CreateEqFunction(type, fieldList));
        }

        if (order)
        {
            foreach (
                var (name, comparison) in new[]
                {
                    ("__lt__", PythonRichComparison.LessThan),
                    ("__le__", PythonRichComparison.LessThanOrEqual),
                    ("__gt__", PythonRichComparison.GreaterThan),
                    ("__ge__", PythonRichComparison.GreaterThanOrEqual),
                }
            )
            {
                if (type.Attributes.TryGetValue(name, out _))
                {
                    throw Fault(
                        $"Cannot overwrite attribute {name} in class {type.Name} "
                            + "Consider using functools.total_ordering",
                        span
                    );
                }
                type.Attributes[name] = CreateOrderFunction(type, fieldList, comparison);
            }
        }

        if (frozen)
        {
            AddFrozenSetDelAttr(type, fieldList, span);
        }

        var hashAction = SelectHashAction(unsafeHash, eq, frozen, hasExplicitHash);
        switch (hashAction)
        {
            case HashAction.SetNone:
                type.Attributes["__hash__"] = PythonNoneValue.Instance;
                break;
            case HashAction.Add:
                type.Attributes["__hash__"] = CreateHashFunction(fieldList);
                break;
            case HashAction.Error:
                throw Fault($"Cannot overwrite attribute __hash__ in class {type.Name}", span);
        }

        if (
            !ManagedObjectProtocols.TryGetTypeAttribute(type, "__doc__", out var doc)
            || !ManagedObjectProtocols.IsTrue(doc)
        )
        {
            type.Attributes["__doc__"] = new PythonTextValue(
                type.Name + BuildInitSignature(stdInitFields, kwOnlyInitFields)
            );
        }

        if (matchArgs)
        {
            SetNewAttribute(
                type,
                "__match_args__",
                new PythonTupleValue([
                    .. stdInitFields
                        .Where(field => !ManagedObjectProtocols.IsTrue(StateOf(field)!.KwOnly))
                        .Select(field => (PythonValue)new PythonTextValue(StateOf(field)!.Name!)),
                ])
            );
        }

        if (weakrefSlot && !slots)
        {
            throw Fault("weakref_slot is True but slots is False", span);
        }
        if (slots)
        {
            throw new PythonRuntimeException(
                SliceCode,
                "dataclass(slots=True) is outside this runtime slice: the decorator cannot "
                    + "re-create the class around a slot layout.",
                span,
                "NotImplementedError"
            );
        }

        return type;
    }

    /// <summary>
    /// The `_DataclassParams` record `_process_class` stores as `__dataclass_params__`, in
    /// the declaration order the module's own `__repr__` walks.
    /// </summary>
    private static PythonManagedObjectValue CreateParams(PythonValue[] options)
    {
        var parameters = new PythonManagedObjectValue(ParamsType);
        for (var index = 0; index < DataclassParameters.Length; index++)
        {
            parameters.Attributes[DataclassParameters[index]] = options[index];
        }
        return parameters;
    }

    private static void AddField(
        List<KeyValuePair<string, PythonManagedObjectValue>> fields,
        Dictionary<string, int> index,
        string name,
        PythonManagedObjectValue field
    )
    {
        if (index.TryGetValue(name, out var position))
        {
            fields[position] = new KeyValuePair<string, PythonManagedObjectValue>(name, field);
            return;
        }
        index[name] = fields.Count;
        fields.Add(new KeyValuePair<string, PythonManagedObjectValue>(name, field));
    }

    private static void SetNewAttribute(PythonManagedTypeValue type, string name, PythonValue value)
    {
        if (type.Attributes.TryGetValue(name, out _))
        {
            return;
        }
        type.Attributes[name] = value;
    }

    private enum HashAction
    {
        None,
        SetNone,
        Add,
        Error,
    }

    /// <summary>CPython's `_hash_action` table, keyed by (unsafe_hash, eq, frozen, explicit).</summary>
    private static HashAction SelectHashAction(
        bool unsafeHash,
        bool eq,
        bool frozen,
        bool hasExplicitHash
    ) =>
        (unsafeHash, eq, frozen, hasExplicitHash) switch
        {
            (false, false, false, false) => HashAction.None,
            (false, false, false, true) => HashAction.None,
            (false, false, true, false) => HashAction.None,
            (false, false, true, true) => HashAction.None,
            (false, true, false, false) => HashAction.SetNone,
            (false, true, false, true) => HashAction.None,
            (false, true, true, false) => HashAction.Add,
            (false, true, true, true) => HashAction.None,
            (true, false, false, false) => HashAction.Add,
            (true, false, false, true) => HashAction.Error,
            (true, false, true, false) => HashAction.Add,
            (true, false, true, true) => HashAction.Error,
            (true, true, false, false) => HashAction.Add,
            (true, true, false, true) => HashAction.Error,
            (true, true, true, false) => HashAction.Add,
            _ => HashAction.Error,
        };

    // -----------------------------------------------------------------------------
    // _get_field
    // -----------------------------------------------------------------------------

    private static PythonManagedObjectValue GetField(
        PythonManagedTypeValue type,
        string name,
        PythonValue annotation,
        bool defaultKwOnly,
        TextSpan span
    )
    {
        PythonManagedObjectValue field;
        if (
            ManagedObjectProtocols.TryGetTypeAttribute(type, name, out var existing)
            && IsFieldObject(existing)
        )
        {
            field = (PythonManagedObjectValue)existing;
        }
        else
        {
            var state = new FieldState();
            SetFieldDefaults(state);
            state.Default = existing ?? Missing;
            field = CreateField(state);
        }
        var fieldState = StateOf(field)!;
        fieldState.Name = name;
        fieldState.Type = annotation;
        fieldState.Kind = FieldKind.Field;

        if (
            IsClassVarAnnotation(annotation)
            || annotation is PythonTextValue classVarText
                && IsStringMarker(classVarText, "ClassVar")
        )
        {
            fieldState.Kind = FieldKind.ClassVar;
        }
        if (
            fieldState.Kind == FieldKind.Field
            && (
                IsInitVarForm(annotation)
                || annotation is PythonTextValue initVarText
                    && IsStringMarker(initVarText, "InitVar")
            )
        )
        {
            fieldState.Kind = FieldKind.InitVar;
        }

        if (fieldState.Kind is FieldKind.ClassVar or FieldKind.InitVar)
        {
            if (!IsMissing(fieldState.DefaultFactory))
            {
                throw Fault($"field {name} cannot have a default factory", span);
            }
        }

        if (fieldState.Kind is FieldKind.Field or FieldKind.InitVar)
        {
            if (IsMissing(fieldState.KwOnly))
            {
                fieldState.KwOnly = PythonTruthValue.FromBoolean(defaultKwOnly);
            }
        }
        else if (!IsMissing(fieldState.KwOnly))
        {
            throw Fault($"field {name} is a ClassVar but specifies kw_only", span);
        }

        if (
            fieldState.Kind == FieldKind.Field
            && !IsMissing(fieldState.Default)
            && !ManagedObjectProtocols.IsHashable(fieldState.Default)
        )
        {
            throw Fault(
                $"mutable default {PythonBuiltinTypes.GetRuntimeType(fieldState.Default).ToDisplayString()} for field "
                    + $"{name} is not allowed: use default_factory",
                span,
                "ValueError"
            );
        }

        SyncField(field, fieldState);
        return field;
    }

    /// <summary>`a_type is typing.ClassVar` or `typing.get_origin(a_type) is typing.ClassVar`.</summary>
    private static bool IsClassVarAnnotation(PythonValue annotation) =>
        annotation switch
        {
            PythonGenericAliasValue { Origin: { } origin } => origin.ToDisplayString()
                == "typing.ClassVar",
            // The bare form is the special form itself, not a generic alias.
            PythonExternalObjectValue form => form.ToDisplayString() == "typing.ClassVar",
            _ => false,
        };

    /// <summary>
    /// The bounded stand-in for CPython's string-annotation regex: a bare or dotted
    /// <c>ClassVar</c>/<c>InitVar</c>, optionally parameterized. CPython additionally proves
    /// the name resolves to the dataclasses/typing object; this runtime does not evaluate
    /// forward references.
    /// </summary>
    private static bool IsStringMarker(PythonValue annotation, string marker)
    {
        if (annotation is not PythonTextValue annotationText)
        {
            return false;
        }
        var text = annotationText.Value.Trim();
        var bracket = text.IndexOf('[', StringComparison.Ordinal);
        if (bracket >= 0)
        {
            text = text[..bracket];
        }
        text = text.Trim();
        if (text == marker)
        {
            return true;
        }
        var dot = text.LastIndexOf('.');
        return dot >= 0 && text[(dot + 1)..] == marker;
    }

    // -----------------------------------------------------------------------------
    // Generated methods
    // -----------------------------------------------------------------------------

    private sealed record InitPlan(
        PythonManagedTypeValue Type,
        List<PythonManagedObjectValue> AllFields,
        List<PythonManagedObjectValue> StdFields,
        List<PythonManagedObjectValue> KwOnlyFields,
        bool Frozen,
        bool HasPostInit
    );

    private static PythonProtocolFunctionValue CreateInitFunction(
        PythonManagedTypeValue type,
        List<PythonManagedObjectValue> allFields,
        List<PythonManagedObjectValue> stdFields,
        List<PythonManagedObjectValue> kwOnlyFields,
        bool frozen,
        bool hasPostInit
    )
    {
        var plan = new InitPlan(type, allFields, stdFields, kwOnlyFields, frozen, hasPostInit);
        return new PythonProtocolFunctionValue(
            "__init__",
            (self, arguments) => RunInit(plan, self, arguments, [], [], default),
            (self, arguments, names, values) =>
                RunInit(plan, self, arguments, names, values, default)
        );
    }

    private static PythonNoneValue RunInit(
        InitPlan plan,
        PythonValue? receiver,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var self = (PythonManagedObjectValue)BindSelf(
            receiver,
            positional,
            plan.Type.Name,
            span,
            out positional
        );
        var std = plan.StdFields;
        var kwOnly = plan.KwOnlyFields;
        var slots = new PythonValue?[std.Count + kwOnly.Count];
        var stored = Math.Min(positional.Count, std.Count);
        for (var index = 0; index < stored; index++)
        {
            slots[index] = positional[index];
        }
        for (var index = 0; index < keywordNames.Count; index++)
        {
            var name = keywordNames[index];
            var position = IndexOfName(std, name);
            if (position >= 0)
            {
                if (slots[position] is not null)
                {
                    throw Fault(
                        $"{QualifiedInitName(plan.Type)}() got multiple values for argument '{name}'",
                        span
                    );
                }
                slots[position] = keywordValues[index];
                continue;
            }
            var keywordPosition = IndexOfName(kwOnly, name);
            if (keywordPosition < 0)
            {
                throw Fault(
                    $"{QualifiedInitName(plan.Type)}() got an unexpected keyword argument '{name}'",
                    span
                );
            }
            slots[std.Count + keywordPosition] = keywordValues[index];
        }
        if (positional.Count > std.Count)
        {
            throw Fault(
                FormatInitTooManyPositional(plan, positional.Count + 1, slots, std.Count),
                span
            );
        }
        ReportMissing(plan, slots, std.Count, span);

        for (var index = 0; index < plan.AllFields.Count; index++)
        {
            var field = plan.AllFields[index];
            var state = StateOf(field)!;
            if (state.Kind == FieldKind.InitVar)
            {
                // InitVar values are only handed to __post_init__.
                continue;
            }
            if (!state.Init)
            {
                // Not an init parameter: only a default factory is called, and the
                // value it returns is stored. A plain default stays a class attribute.
                if (IsMissing(state.DefaultFactory))
                {
                    continue;
                }
                var factoryValue = CallUserCallable(state.DefaultFactory, [], span);
                if (plan.Frozen)
                {
                    ManagedObjectProtocols.SetInstanceAttribute(
                        self,
                        state.Name!,
                        factoryValue,
                        span
                    );
                }
                else
                {
                    ManagedObjectProtocols.SetAttribute(self, state.Name!, factoryValue, span);
                }
                continue;
            }
            var position = IndexOfName(std, state.Name!);
            var slot = position >= 0 ? position : std.Count + IndexOfName(kwOnly, state.Name!);
            var supplied = slots[slot];
            var value = supplied;
            if (value is null)
            {
                if (!IsMissing(state.DefaultFactory))
                {
                    value = CallUserCallable(state.DefaultFactory, [], span);
                }
                else if (!IsMissing(state.Default))
                {
                    value = state.Default;
                }
            }
            if (value is null)
            {
                continue;
            }
            if (plan.Frozen)
            {
                ManagedObjectProtocols.SetInstanceAttribute(self, state.Name!, value, span);
            }
            else
            {
                ManagedObjectProtocols.SetAttribute(self, state.Name!, value, span);
            }
        }

        if (!plan.HasPostInit)
        {
            return PythonNoneValue.Instance;
        }
        var initVarValues = new List<PythonValue>();
        foreach (var field in plan.AllFields)
        {
            var state = StateOf(field)!;
            if (state.Kind != FieldKind.InitVar)
            {
                continue;
            }
            if (!state.Init)
            {
                // CPython's generated __post_init__ call still names an init=False
                // InitVar, which its __init__ never binds.
                throw Fault($"name '{state.Name}' is not defined", span, "NameError");
            }
            var position = IndexOfName(std, state.Name!);
            var slot = position >= 0 ? position : std.Count + IndexOfName(kwOnly, state.Name!);
            initVarValues.Add(
                slots[slot]
                    ?? (
                        !IsMissing(state.DefaultFactory)
                            ? CallUserCallable(state.DefaultFactory, [], span)
                            : state.Default
                    )
            );
        }
        var postInit = ManagedObjectProtocols.GetAttribute(plan.Type, PostInitName, span);
        CallUserCallable(
            ManagedObjectProtocols.BindDescriptor(
                postInit,
                self,
                plan.Type,
                attributeName: PostInitName
            ),
            [.. initVarValues],
            span
        );
        return PythonNoneValue.Instance;
    }

    /// <summary>
    /// Calls a value the decorated class supplied — a default factory, `__post_init__`,
    /// an asdict factory or a make_dataclass decorator. A source function or bound method
    /// needs the running VM's frame machinery; everything else takes the protocol path.
    /// </summary>
    private static PythonValue CallUserCallable(
        PythonValue callable,
        PythonValue[] arguments,
        TextSpan span
    ) => CallUserCallable(callable, arguments, [], [], span);

    /// <summary>The keyword-argument form of <see cref="CallUserCallable(PythonValue, PythonValue[], TextSpan)"/>.</summary>
    private static PythonValue CallUserCallable(
        PythonValue callable,
        PythonValue[] arguments,
        List<string> keywordNames,
        List<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (callable is PythonFunctionValue or PythonBoundUserMethodValue)
        {
            if (UserObjectProtocols.Dispatcher is { } dispatcher)
            {
                return dispatcher.InvokeWithKeywords(
                    callable,
                    arguments,
                    keywordNames,
                    keywordValues,
                    span
                );
            }
        }
        else if (keywordNames.Count != 0)
        {
            return callable switch
            {
                PythonBuiltinFunctionValue { InvokeWithKeywords: { } builtin } => builtin(
                    arguments,
                    keywordNames,
                    keywordValues,
                    span
                ),
                PythonBoundMethodValue { Function.InvokeWithKeywords: { } method } bound => method(
                    bound.Target,
                    arguments,
                    keywordNames,
                    keywordValues
                ),
                PythonProtocolFunctionValue { InvokeWithKeywords: { } protocol } => protocol(
                    null,
                    arguments,
                    keywordNames,
                    keywordValues
                ),
                _ => throw Fault(
                    "Keyword arguments are not supported for this callable in this runtime slice.",
                    span,
                    "TypeError"
                ),
            };
        }
        return ManagedObjectProtocols.Call(callable, arguments, span);
    }

    private static int IndexOfName(List<PythonManagedObjectValue> fields, string name)
    {
        for (var index = 0; index < fields.Count; index++)
        {
            if (StateOf(fields[index])!.Name == name)
            {
                return index;
            }
        }
        return -1;
    }

    private static string QualifiedInitName(PythonManagedTypeValue type) =>
        $"{type.QualName ?? type.Name}.__init__";

    /// <summary>
    /// CPython's too-many-positional diagnostic: the unbound `__init__`'s argument counts,
    /// with keyword-only parameters that were supplied named as a suffix.
    /// </summary>
    private static string FormatInitTooManyPositional(
        InitPlan plan,
        int given,
        PythonValue?[] slots,
        int stdCount
    )
    {
        var argumentCount = stdCount + 1;
        var defaultCount = plan.StdFields.Count(field =>
        {
            var state = StateOf(field)!;
            return !IsMissing(state.Default) || !IsMissing(state.DefaultFactory);
        });
        var minimum = argumentCount - defaultCount;
        var keywordOnlyGiven = slots.Skip(stdCount).Count(slot => slot is not null);
        var builder = new StringBuilder(QualifiedInitName(plan.Type)).Append("() takes ");
        if (minimum == argumentCount)
        {
            builder
                .Append(argumentCount)
                .Append(argumentCount == 1 ? " positional argument" : " positional arguments");
        }
        else
        {
            builder
                .Append("from ")
                .Append(minimum)
                .Append(" to ")
                .Append(argumentCount)
                .Append(" positional arguments");
        }
        builder.Append(" but ").Append(given);
        if (keywordOnlyGiven == 0)
        {
            builder.Append(given == 1 ? " was given" : " were given");
        }
        else
        {
            builder
                .Append(" positional arguments (and ")
                .Append(keywordOnlyGiven)
                .Append(" keyword-only argument")
                .Append(keywordOnlyGiven == 1 ? "" : "s")
                .Append(") were given");
        }
        return builder.ToString();
    }

    private static void ReportMissing(
        InitPlan plan,
        PythonValue?[] slots,
        int stdCount,
        TextSpan span
    )
    {
        var missing = new List<string>();
        for (var index = 0; index < stdCount; index++)
        {
            var state = StateOf(plan.StdFields[index])!;
            if (slots[index] is null && IsMissing(state.Default) && IsMissing(state.DefaultFactory))
            {
                missing.Add(state.Name!);
            }
        }
        if (missing.Count != 0)
        {
            throw Fault(
                $"{QualifiedInitName(plan.Type)}() missing {missing.Count} required positional "
                    + $"argument{(missing.Count == 1 ? "" : "s")}: {JoinNames(missing)}",
                span
            );
        }
        for (var index = stdCount; index < slots.Length; index++)
        {
            var state = StateOf(plan.KwOnlyFields[index - stdCount])!;
            if (slots[index] is null && IsMissing(state.Default) && IsMissing(state.DefaultFactory))
            {
                missing.Add(state.Name!);
            }
        }
        if (missing.Count != 0)
        {
            throw Fault(
                $"{QualifiedInitName(plan.Type)}() missing {missing.Count} required keyword-only "
                    + $"argument{(missing.Count == 1 ? "" : "s")}: {JoinNames(missing)}",
                span
            );
        }
    }

    private static PythonProtocolFunctionValue CreateReprFunction(
        PythonManagedTypeValue type,
        List<PythonManagedObjectValue> fields
    )
    {
        var visible = fields
            .Where(field => StateOf(field)!.Repr)
            .Select(field => StateOf(field)!.Name!)
            .ToList();
        return new PythonProtocolFunctionValue(
            "__repr__",
            (self, _) => RepresentInstance(type, visible, self)
        );
    }

    private static PythonTextValue RepresentInstance(
        PythonManagedTypeValue type,
        List<string> visible,
        PythonValue? self
    )
    {
        if (self is not PythonManagedObjectValue instance)
        {
            throw Fault(
                $"descriptor '__repr__' for '{type.Name}' objects doesn't apply to a "
                    + $"'{ManagedObjectProtocols.GetTypeName(self ?? PythonNoneValue.Instance)}' object",
                default
            );
        }
        if (!PythonRepresentationGuard.TryEnter(instance))
        {
            return new PythonTextValue("...");
        }
        try
        {
            var builder = new StringBuilder();
            builder.Append(type.QualName ?? type.Name).Append('(');
            for (var index = 0; index < visible.Count; index++)
            {
                if (index != 0)
                {
                    builder.Append(", ");
                }
                builder
                    .Append(visible[index])
                    .Append('=')
                    .Append(
                        ManagedObjectProtocols
                            .GetAttribute(instance, visible[index], default)
                            .ToRepresentationString()
                    );
            }
            return new PythonTextValue(builder.Append(')').ToString());
        }
        finally
        {
            PythonRepresentationGuard.Exit(instance);
        }
    }

    private static PythonProtocolFunctionValue CreateEqFunction(
        PythonManagedTypeValue type,
        List<PythonManagedObjectValue> fields
    )
    {
        var compared = fields
            .Where(field => StateOf(field)!.Compare)
            .Select(field => StateOf(field)!.Name!)
            .ToList();
        return new PythonProtocolFunctionValue(
            "__eq__",
            (self, arguments) =>
            {
                var other = arguments.Count == 1 ? arguments[0] : Missing;
                if (ReferenceEquals(self, other))
                {
                    return PythonTruthValue.True;
                }
                if (!IsSameRuntimeClass(type, other))
                {
                    return PythonNotImplementedValue.Instance;
                }
                if (self is not PythonManagedObjectValue instance)
                {
                    return PythonNotImplementedValue.Instance;
                }
                foreach (var name in compared)
                {
                    if (
                        !ManagedObjectProtocols.AreEqual(
                            ManagedObjectProtocols.GetAttribute(instance, name, default),
                            ManagedObjectProtocols.GetAttribute(other, name, default)
                        )
                    )
                    {
                        return PythonTruthValue.False;
                    }
                }
                return PythonTruthValue.True;
            }
        );
    }

    private static PythonProtocolFunctionValue CreateOrderFunction(
        PythonManagedTypeValue type,
        List<PythonManagedObjectValue> fields,
        PythonRichComparison comparison
    )
    {
        var compared = fields
            .Where(field => StateOf(field)!.Compare)
            .Select(field => StateOf(field)!.Name!)
            .ToList();
        return new PythonProtocolFunctionValue(
            comparison switch
            {
                PythonRichComparison.LessThan => "__lt__",
                PythonRichComparison.LessThanOrEqual => "__le__",
                PythonRichComparison.GreaterThan => "__gt__",
                _ => "__ge__",
            },
            (self, arguments) =>
            {
                var other = arguments.Count == 1 ? arguments[0] : Missing;
                if (!IsSameRuntimeClass(type, other))
                {
                    return PythonNotImplementedValue.Instance;
                }
                if (self is not PythonManagedObjectValue instance)
                {
                    return PythonNotImplementedValue.Instance;
                }
                return ManagedObjectProtocols.RichCompareValue(
                    BuildFieldTuple(instance, compared),
                    BuildFieldTuple(other, compared),
                    comparison,
                    default
                );
            }
        );
    }

    private static PythonTupleValue BuildFieldTuple(PythonValue instance, List<string> names) =>
        new([
            .. names.Select(name => ManagedObjectProtocols.GetAttribute(instance, name, default)),
        ]);

    private static bool IsSameRuntimeClass(PythonManagedTypeValue type, PythonValue other) =>
        other is PythonManagedObjectValue instance
        && PythonBuiltinTypes.GetRuntimeType(instance) is PythonManagedTypeValue runtime
        && ReferenceEquals(runtime, type);

    private static PythonProtocolFunctionValue CreateHashFunction(
        List<PythonManagedObjectValue> fields
    )
    {
        var hashed = fields
            .Where(field =>
            {
                var state = StateOf(field)!;
                return state.Hash is PythonNoneValue
                    ? state.Compare
                    : ManagedObjectProtocols.IsTrue(state.Hash);
            })
            .Select(field => StateOf(field)!.Name!)
            .ToList();
        return new PythonProtocolFunctionValue(
            "__hash__",
            (self, _) =>
            {
                if (self is not PythonManagedObjectValue instance)
                {
                    throw Fault(
                        $"unhashable type: '{ManagedObjectProtocols.GetTypeName(self ?? PythonNoneValue.Instance)}'",
                        default
                    );
                }
                return PythonWholeNumberValue.Create(
                    ManagedObjectProtocols.ComputePythonHash(
                        BuildFieldTuple(instance, hashed),
                        default
                    )
                );
            }
        );
    }

    private static void AddFrozenSetDelAttr(
        PythonManagedTypeValue type,
        List<PythonManagedObjectValue> fields,
        TextSpan span
    )
    {
        var names = fields.Select(field => StateOf(field)!.Name!).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, assign) in new[] { ("__setattr__", true), ("__delattr__", false) })
        {
            if (type.Attributes.TryGetValue(name, out _))
            {
                throw Fault($"Cannot overwrite attribute {name} in class {type.Name}", span);
            }
            var isAssign = assign;
            type.Attributes[name] = isAssign
                ? CreateFrozenSetAttr(type, names)
                : CreateFrozenDelAttr(type, names);
        }
    }

    private static PythonProtocolFunctionValue CreateFrozenSetAttr(
        PythonManagedTypeValue type,
        HashSet<string> fieldNames
    ) =>
        new(
            "__setattr__",
            (self, arguments) =>
            {
                if (arguments.Count != 2)
                {
                    throw Fault(
                        $"__setattr__() takes 3 positional arguments but {arguments.Count + 1} were given",
                        default
                    );
                }
                return FrozenAssignment(type, fieldNames, self, arguments[0], isDelete: false)!
                    ?? (
                        arguments[0] is PythonTextValue name
                            ? AssignThroughObject(self, name.Value, arguments[1])
                            : throw Fault("attribute name must be a string", default)
                    );
            }
        );

    private static PythonProtocolFunctionValue CreateFrozenDelAttr(
        PythonManagedTypeValue type,
        HashSet<string> fieldNames
    ) =>
        new(
            "__delattr__",
            (self, arguments) =>
            {
                if (arguments.Count != 1)
                {
                    throw Fault(
                        $"__delattr__() takes 2 positional arguments but {arguments.Count + 1} were given",
                        default
                    );
                }
                FrozenAssignment(type, fieldNames, self, arguments[0], isDelete: true);
                if (arguments[0] is not PythonTextValue name)
                {
                    throw Fault("attribute name must be a string", default);
                }
                if (self is PythonManagedObjectValue instance)
                {
                    ManagedObjectProtocols.DeleteInstanceAttribute(instance, name.Value, default);
                }
                return PythonNoneValue.Instance;
            }
        );

    private static PythonValue? FrozenAssignment(
        PythonManagedTypeValue type,
        HashSet<string> fieldNames,
        PythonValue? self,
        PythonValue attributeName,
        bool isDelete
    )
    {
        if (attributeName is not PythonTextValue name)
        {
            return null;
        }
        if (
            !ReferenceEquals(
                self is PythonManagedObjectValue instance
                    ? PythonBuiltinTypes.GetRuntimeType(instance)
                    : null,
                type
            ) && !fieldNames.Contains(name.Value)
        )
        {
            return null;
        }
        throw FrozenFault($"cannot {(isDelete ? "delete" : "assign to")} field '{name.Value}'");
    }

    private static PythonNoneValue AssignThroughObject(
        PythonValue? self,
        string name,
        PythonValue value
    )
    {
        if (self is not PythonManagedObjectValue instance)
        {
            throw Fault("__setattr__() needs a managed instance", default);
        }
        ManagedObjectProtocols.SetInstanceAttribute(instance, name, value, default);
        return PythonNoneValue.Instance;
    }

    private static PythonProtocolFunctionValue CreateReplaceMethod() =>
        new PythonProtocolFunctionValue(
            "__replace__",
            (self, _) => throw Fault("__replace__() takes no positional arguments", default),
            (self, positional, names, values) =>
                RunReplace(self, positional, names, values, default)
        );

    private static PythonValue RunReplace(
        PythonValue? self,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (positional.Count != 0)
        {
            throw Fault(
                $"__replace__() takes 1 positional argument but {positional.Count + 1} were given",
                span
            );
        }
        if (self is not PythonManagedObjectValue instance)
        {
            throw Fault("replace() should be called on dataclass instances", span);
        }
        if (
            PythonBuiltinTypes.GetRuntimeType(instance) is not PythonManagedTypeValue type
            || !ManagedObjectProtocols.TryGetTypeAttribute(type, FieldsName, out var fieldsValue)
            || fieldsValue is not PythonDictionaryValue fields
        )
        {
            throw Fault("replace() should be called on dataclass instances", span);
        }
        var changes = new List<KeyValuePair<string, PythonValue>>();
        for (var index = 0; index < keywordNames.Count; index++)
        {
            changes.Add(
                new KeyValuePair<string, PythonValue>(keywordNames[index], keywordValues[index])
            );
        }
        foreach (var item in fields.Items)
        {
            if (item.Key is not PythonTextValue name || StateOf(item.Value) is not { } state)
            {
                continue;
            }
            if (state.Kind == FieldKind.ClassVar)
            {
                continue;
            }
            var existing = changes.FindIndex(entry => entry.Key == name.Value);
            if (!state.Init)
            {
                if (existing >= 0)
                {
                    throw Fault(
                        $"field {name.Value} is declared with init=False, it cannot be specified with replace()",
                        span
                    );
                }
                continue;
            }
            if (existing < 0)
            {
                if (state.Kind == FieldKind.InitVar && IsMissing(state.Default))
                {
                    throw Fault($"InitVar '{name.Value}' must be specified with replace()", span);
                }
                changes.Add(
                    new KeyValuePair<string, PythonValue>(
                        name.Value,
                        ManagedObjectProtocols.GetAttribute(instance, name.Value, span)
                    )
                );
            }
        }
        var names = changes.Select(entry => entry.Key).ToList();
        var values = changes.Select(entry => entry.Value).ToList();
        return UserObjectProtocols.Dispatcher!.CallType(type, [], names, values, span);
    }

    // -----------------------------------------------------------------------------
    // field()
    // -----------------------------------------------------------------------------

    private static PythonBuiltinFunctionValue CreateFieldFunction() =>
        new PythonBuiltinFunctionValue(
            "field",
            (arguments, span) =>
                arguments.Count == 0
                    ? InvokeField([], [], [], span)
                    : throw Fault($"field() {FormatPositionalCount(0, arguments.Count, 0)}", span),
            (arguments, names, values, span) => InvokeField(arguments, names, values, span)
        );

    private static PythonManagedObjectValue InvokeField(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (positional.Count != 0)
        {
            throw Fault($"field() {FormatPositionalCount(0, positional.Count, 0)}", span);
        }
        var options = BindOptions(
            "field",
            FieldParameters,
            [
                Missing,
                Missing,
                PythonTruthValue.True,
                PythonTruthValue.True,
                PythonNoneValue.Instance,
                PythonTruthValue.True,
                PythonNoneValue.Instance,
                Missing,
                PythonNoneValue.Instance,
            ],
            positional,
            keywordNames,
            keywordValues,
            span
        );
        if (!IsMissing(options[0]) && !IsMissing(options[1]))
        {
            throw Fault("cannot specify both default and default_factory", span, "ValueError");
        }
        var state = new FieldState();
        SetFieldDefaults(state);
        state.Default = options[0];
        state.DefaultFactory = options[1];
        state.Init = ManagedObjectProtocols.IsTrue(options[2]);
        state.Repr = ManagedObjectProtocols.IsTrue(options[3]);
        state.Hash = options[4];
        state.Compare = ManagedObjectProtocols.IsTrue(options[5]);
        state.Metadata =
            options[6] is PythonNoneValue ? EmptyMetadata : new PythonMappingProxyValue(options[6]);
        state.KwOnly = options[7];
        state.Doc = options[8];
        return CreateField(state);
    }

    // -----------------------------------------------------------------------------
    // fields() / is_dataclass()
    // -----------------------------------------------------------------------------

    private static PythonBuiltinFunctionValue CreateFields() =>
        new PythonBuiltinFunctionValue(
            "fields",
            (arguments, span) =>
            {
                if (arguments.Count != 1)
                {
                    throw Fault(
                        arguments.Count == 0
                            ? "fields() missing 1 required positional argument: 'class_or_instance'"
                            : $"fields() takes 1 positional argument but {arguments.Count} were given",
                        span
                    );
                }
                return FieldsTuple(arguments[0], span);
            }
        );

    private static PythonBuiltinFunctionValue CreateIsDataclass() =>
        new PythonBuiltinFunctionValue(
            "is_dataclass",
            (arguments, span) =>
            {
                if (arguments.Count != 1)
                {
                    throw Fault(
                        arguments.Count == 0
                            ? "is_dataclass() missing 1 required positional argument: 'obj'"
                            : $"is_dataclass() takes 1 positional argument but {arguments.Count} were given",
                        span
                    );
                }
                var cls = arguments[0] is PythonManagedTypeValue type
                    ? type
                    : ManagedObjectProtocols.GetManagedType(arguments[0]);
                return PythonTruthValue.FromBoolean(
                    cls is not null
                        && ManagedObjectProtocols.TryGetTypeAttribute(cls, FieldsName, out _)
                );
            }
        );

    private static PythonTupleValue FieldsTuple(PythonValue target, TextSpan span)
    {
        var type = target is PythonManagedTypeValue asType
            ? asType
            : ManagedObjectProtocols.GetManagedType(target);
        if (
            type is null
            || !ManagedObjectProtocols.TryGetTypeAttribute(type, FieldsName, out var fieldsValue)
            || fieldsValue is not PythonDictionaryValue fields
        )
        {
            throw Fault("must be called with a dataclass type or instance", span);
        }
        return new PythonTupleValue([
            .. fields
                .Items.Where(item => StateOf(item.Value)!.Kind == FieldKind.Field)
                .Select(item => item.Value),
        ]);
    }

    // -----------------------------------------------------------------------------
    // asdict() / astuple()
    // -----------------------------------------------------------------------------

    private static PythonBuiltinFunctionValue CreateAsDict() =>
        new PythonBuiltinFunctionValue(
            "asdict",
            (arguments, span) => InvokeAsDict(arguments, [], [], span),
            (arguments, names, values, span) => InvokeAsDict(arguments, names, values, span)
        );

    private static PythonBuiltinFunctionValue CreateAsTuple() =>
        new PythonBuiltinFunctionValue(
            "astuple",
            (arguments, span) => InvokeAsTuple(arguments, [], [], span),
            (arguments, names, values, span) => InvokeAsTuple(arguments, names, values, span)
        );

    private static PythonValue InvokeAsDict(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (positional.Count != 1)
        {
            throw Fault(
                positional.Count == 0
                    ? "asdict() missing 1 required positional argument: 'obj'"
                    : $"asdict() takes 1 positional argument but {positional.Count} were given",
                span
            );
        }
        PythonValue factory = PythonBuiltinTypes.Dict;
        for (var index = 0; index < keywordNames.Count; index++)
        {
            if (keywordNames[index] != "dict_factory")
            {
                throw Fault(
                    $"asdict() got an unexpected keyword argument '{keywordNames[index]}'",
                    span
                );
            }
            factory = keywordValues[index];
        }
        if (!IsDataclassInstance(positional[0]))
        {
            throw Fault("asdict() should be called on dataclass instances", span);
        }
        return AsDictInner(positional[0], factory, span);
    }

    private static PythonValue InvokeAsTuple(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (positional.Count != 1)
        {
            throw Fault(
                positional.Count == 0
                    ? "astuple() missing 1 required positional argument: 'obj'"
                    : $"astuple() takes 1 positional argument but {positional.Count} were given",
                span
            );
        }
        PythonValue factory = PythonBuiltinTypes.Tuple;
        for (var index = 0; index < keywordNames.Count; index++)
        {
            if (keywordNames[index] != "tuple_factory")
            {
                throw Fault(
                    $"astuple() got an unexpected keyword argument '{keywordNames[index]}'",
                    span
                );
            }
            factory = keywordValues[index];
        }
        if (!IsDataclassInstance(positional[0]))
        {
            throw Fault("astuple() should be called on dataclass instances", span);
        }
        return AsTupleInner(positional[0], factory, span);
    }

    private static bool IsDataclassInstance(PythonValue value) =>
        ManagedObjectProtocols.GetManagedType(value) is { } type
        && ManagedObjectProtocols.TryGetTypeAttribute(type, FieldsName, out _);

    private static PythonValue AsDictInner(PythonValue value, PythonValue factory, TextSpan span)
    {
        if (value is PythonManagedObjectValue instance && IsDataclassInstance(instance))
        {
            var items = new List<PythonValue>();
            foreach (var field in FieldsTuple(value, span).Elements)
            {
                var state = StateOf(field)!;
                items.Add(
                    new PythonTupleValue([
                        new PythonTextValue(state.Name!),
                        AsDictInner(
                            ManagedObjectProtocols.GetAttribute(instance, state.Name!, span),
                            factory,
                            span
                        ),
                    ])
                );
            }
            return CallUserCallable(factory, [new PythonListValue(items)], span);
        }
        return value switch
        {
            PythonListValue list => new PythonListValue([
                .. list.Elements.Select(element => AsDictInner(element, factory, span)),
            ]),
            PythonDictionaryValue dictionary => new PythonDictionaryValue([
                .. dictionary.Items.Select(item => new PythonDictionaryItemValue(
                    AsDictInner(item.Key, factory, span),
                    AsDictInner(item.Value, factory, span)
                )),
            ]),
            PythonTupleValue tuple => new PythonTupleValue([
                .. tuple.Elements.Select(element => AsDictInner(element, factory, span)),
            ]),
            // Not a container CPython would rebuild; deep-copying it is the caller's own
            // copy.deepcopy surface, which this slice leaves the object itself for.
            _ => value,
        };
    }

    private static PythonValue AsTupleInner(PythonValue value, PythonValue factory, TextSpan span)
    {
        if (value is PythonManagedObjectValue instance && IsDataclassInstance(instance))
        {
            var items = new List<PythonValue>();
            foreach (var field in FieldsTuple(value, span).Elements)
            {
                var state = StateOf(field)!;
                items.Add(
                    AsTupleInner(
                        ManagedObjectProtocols.GetAttribute(instance, state.Name!, span),
                        factory,
                        span
                    )
                );
            }
            return CallUserCallable(factory, [new PythonListValue(items)], span);
        }
        return value switch
        {
            PythonListValue list => new PythonListValue([
                .. list.Elements.Select(element => AsTupleInner(element, factory, span)),
            ]),
            PythonDictionaryValue dictionary => new PythonDictionaryValue([
                .. dictionary.Items.Select(item => new PythonDictionaryItemValue(
                    AsTupleInner(item.Key, factory, span),
                    AsTupleInner(item.Value, factory, span)
                )),
            ]),
            PythonTupleValue tuple => new PythonTupleValue([
                .. tuple.Elements.Select(element => AsTupleInner(element, factory, span)),
            ]),
            _ => value,
        };
    }

    // -----------------------------------------------------------------------------
    // replace()
    // -----------------------------------------------------------------------------

    private static PythonBuiltinFunctionValue CreateReplace() =>
        new PythonBuiltinFunctionValue(
            "replace",
            (arguments, span) => InvokeReplace(arguments, [], [], span),
            (arguments, names, values, span) => InvokeReplace(arguments, names, values, span)
        );

    private static PythonValue InvokeReplace(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (positional.Count == 0)
        {
            throw Fault("replace() missing 1 required positional argument: 'obj'", span);
        }
        if (positional.Count > 1)
        {
            throw Fault(
                $"replace() takes 1 positional argument but {positional.Count} were given",
                span
            );
        }
        if (!IsDataclassInstance(positional[0]))
        {
            throw Fault("replace() should be called on dataclass instances", span);
        }
        return RunReplace(positional[0], [], keywordNames, keywordValues, span);
    }

    // -----------------------------------------------------------------------------
    // make_dataclass()
    // -----------------------------------------------------------------------------

    private static PythonBuiltinFunctionValue CreateMakeDataclass() =>
        new PythonBuiltinFunctionValue(
            "make_dataclass",
            (arguments, span) => InvokeMakeDataclass(arguments, [], [], span),
            (arguments, names, values, span) => InvokeMakeDataclass(arguments, names, values, span)
        );

    private static PythonValue InvokeMakeDataclass(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (positional.Count > 2)
        {
            var keywordOnly = keywordNames.Count(name => name is not "cls_name" and not "fields");
            if (keywordOnly == 0)
            {
                throw Fault(
                    $"make_dataclass() takes 2 positional arguments but {positional.Count} were given",
                    span
                );
            }
            throw Fault(
                $"make_dataclass() takes 2 positional arguments but {positional.Count} positional "
                    + $"arguments (and {keywordOnly} keyword-only argument{(keywordOnly == 1 ? "" : "s")}) were given",
                span
            );
        }
        // cls_name and fields are ordinary positional-or-keyword parameters; the
        // dataclass options after them are keyword-only.
        var clsName = positional.Count > 0 ? positional[0] : null;
        var fieldsValue = positional.Count > 1 ? positional[1] : null;
        var optionNames = new List<string>();
        var optionValues = new List<PythonValue>();
        for (var index = 0; index < keywordNames.Count; index++)
        {
            switch (keywordNames[index])
            {
                case "cls_name" when clsName is null:
                    clsName = keywordValues[index];
                    break;
                case "fields" when fieldsValue is null:
                    fieldsValue = keywordValues[index];
                    break;
                case "cls_name":
                case "fields":
                    throw Fault(
                        $"make_dataclass() got multiple values for argument '{keywordNames[index]}'",
                        span
                    );
                default:
                    optionNames.Add(keywordNames[index]);
                    optionValues.Add(keywordValues[index]);
                    break;
            }
        }
        var missing = new List<string>();
        if (clsName is null)
        {
            missing.Add("cls_name");
        }
        if (fieldsValue is null)
        {
            missing.Add("fields");
        }
        if (missing.Count != 0)
        {
            throw Fault(
                $"make_dataclass() missing {missing.Count} required positional "
                    + $"argument{(missing.Count == 1 ? "" : "s")}: {JoinNames(missing)}",
                span
            );
        }
        var options = BindOptions(
            "make_dataclass",
            MakeDataclassParameters,
            [
                new PythonTupleValue([]),
                PythonNoneValue.Instance,
                PythonTruthValue.True,
                PythonTruthValue.True,
                PythonTruthValue.True,
                PythonTruthValue.False,
                PythonTruthValue.False,
                PythonTruthValue.False,
                PythonTruthValue.True,
                PythonTruthValue.False,
                PythonTruthValue.False,
                PythonTruthValue.False,
                PythonNoneValue.Instance,
                Missing,
            ],
            [],
            optionNames,
            optionValues,
            span
        );
        return BuildDataclassFromSpec(clsName!, fieldsValue!, options, span);
    }

    private static PythonValue BuildDataclassFromSpec(
        PythonValue nameValue,
        PythonValue specsValue,
        PythonValue[] options,
        TextSpan span
    )
    {
        // A non-string class name is left to the type constructor, which produces
        // CPython's `type.__new__() argument 1 must be str, not …` diagnostic.
        var className = nameValue;
        // CPython folds the namespace in with `ns.update(namespace)`, so a mapping
        // or any iterable of key/value pairs is accepted; `dict()` is the same call.
        var namespaceValue = options[1] switch
        {
            PythonNoneValue => new PythonDictionaryValue([]),
            PythonDictionaryValue dictionary => dictionary,
            var other => (PythonDictionaryValue)
                ManagedObjectProtocols.Call(PythonBuiltinTypes.Dict, [other], span),
        };

        var annotations = new List<KeyValuePair<string, PythonValue>>();
        var defaults = new List<KeyValuePair<string, PythonValue>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in EnumerateSpecs(specsValue, span))
        {
            PythonValue fieldNameValue = PythonNoneValue.Instance;
            PythonValue annotation = AnnotationAnyType;
            PythonValue? specDefault = null;
            switch (spec)
            {
                case PythonTextValue text:
                    fieldNameValue = text;
                    break;
                case PythonTupleValue { Elements.Length: 2 } pair:
                    fieldNameValue = pair.Elements[0];
                    annotation = pair.Elements[1];
                    break;
                case PythonTupleValue { Elements.Length: 3 } triple:
                    fieldNameValue = triple.Elements[0];
                    annotation = triple.Elements[1];
                    specDefault = triple.Elements[2];
                    break;
                case PythonTupleValue badTuple:
                    throw Fault($"Invalid field: {badTuple.ToRepresentationString()}", span);
                default:
                {
                    // CPython measures a non-string item with len() before unpacking
                    // it, so an unmeasured item fails there rather than as a field.
                    var length = PythonLengthHints.GetSequenceLength(spec, span);
                    var parts = EnumerateSpecs(spec, span);
                    if (length == 2 && parts.Count == 2)
                    {
                        fieldNameValue = parts[0];
                        annotation = parts[1];
                    }
                    else if (length == 3 && parts.Count == 3)
                    {
                        fieldNameValue = parts[0];
                        annotation = parts[1];
                        specDefault = parts[2];
                    }
                    else
                    {
                        throw Fault($"Invalid field: {spec.ToRepresentationString()}", span);
                    }
                    break;
                }
            }
            if (
                fieldNameValue is not PythonTextValue nameText
                || nameText.Value.Length == 0
                || !PythonIdentifier.IsIdentifier(nameText.Value)
            )
            {
                throw Fault(
                    $"Field names must be valid identifiers: {fieldNameValue.ToRepresentationString()}",
                    span
                );
            }
            var name = nameText.Value;
            if (specDefault is not null)
            {
                defaults.Add(new KeyValuePair<string, PythonValue>(name, specDefault));
            }
            if (IsKeyword(name))
            {
                throw Fault($"Field names must not be keywords: '{name}'", span);
            }
            if (!seen.Add(name))
            {
                throw Fault($"Field name duplicated: '{name}'", span);
            }
            annotations.Add(new KeyValuePair<string, PythonValue>(name, annotation));
        }

        var namespaceItems = new List<PythonDictionaryItemValue>([.. namespaceValue.Items]);
        foreach (var entry in defaults)
        {
            namespaceItems.Add(
                new PythonDictionaryItemValue(new PythonTextValue(entry.Key), entry.Value)
            );
        }
        var annotationItems = annotations
            .Select(entry => new PythonDictionaryItemValue(
                new PythonTextValue(entry.Key),
                entry.Value
            ))
            .ToList();
        namespaceItems.Add(
            new PythonDictionaryItemValue(
                new PythonTextValue("__annotate_func__"),
                new PythonProtocolFunctionValue(
                    "__annotate_func__",
                    (_, arguments) =>
                        arguments.Count == 1 && arguments[0] is PythonWholeNumberValue
                            ? new PythonDictionaryValue(annotationItems)
                            : throw Fault("make_dataclass annotation format is not supported", span)
                )
            )
        );
        if (options[12] is not PythonNoneValue)
        {
            namespaceItems.Add(
                new PythonDictionaryItemValue(new PythonTextValue("__module__"), options[12])
            );
        }

        var created = UserObjectProtocols.Dispatcher!.ConstructType(
            [className, options[0], new PythonDictionaryValue(namespaceItems)],
            span
        );
        var decorator =
            options[13] is PythonNoneValue || IsMissing(options[13])
                ? CreateDataclass()
                : options[13];
        var decoratorNames = new List<string>
        {
            "init",
            "repr",
            "eq",
            "order",
            "unsafe_hash",
            "frozen",
            "match_args",
            "kw_only",
            "slots",
            "weakref_slot",
        };
        var decoratorValues = new List<PythonValue>
        {
            options[2],
            options[3],
            options[4],
            options[5],
            options[6],
            options[7],
            options[8],
            options[9],
            options[10],
            options[11],
        };
        return CallDecorator(decorator, created, decoratorNames, decoratorValues, span);
    }

    /// <summary>
    /// Applies the decorator. `dataclasses.dataclass` itself is recognized and called with
    /// the keyword parameters `make_dataclass` derived; any other callable is called the
    /// same way CPython calls it: <c>decorator(cls, init=…, repr=…, …)</c>.
    /// </summary>
    private static PythonValue CallDecorator(
        PythonValue decorator,
        PythonValue target,
        List<string> names,
        List<PythonValue> values,
        TextSpan span
    )
    {
        if (IsDataclassFunction(decorator))
        {
            return InvokeDataclass([target], names, values, span);
        }
        return CallUserCallable(decorator, [target], names, values, span);
    }

    private static List<PythonValue> EnumerateSpecs(PythonValue specs, TextSpan span)
    {
        var values = new List<PythonValue>();
        var iterator = ManagedObjectProtocols.GetIterator(specs, span);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var item, span))
        {
            values.Add(item);
        }
        return values;
    }

    /// <summary>
    /// The stand-in for `typing.Any` that `make_dataclass` annotates a bare name with.
    /// It is a type object that renders as <c>&lt;class 'typing.Any'&gt;</c>; identity with
    /// `typing.Any` itself is not preserved.
    /// </summary>
    private static readonly PythonBuiltinTypeValue AnnotationAnyType = new(
        "Any",
        (_, span) => throw Fault("Any cannot be instantiated", span)
    )
    {
        ModuleName = "typing",
    };

    /// <summary>Python 3.14's reserved words, as `keyword.iskeyword` reports them.</summary>
    private static bool IsKeyword(string name) =>
        name
            is "False"
                or "None"
                or "True"
                or "and"
                or "as"
                or "assert"
                or "async"
                or "await"
                or "break"
                or "class"
                or "continue"
                or "def"
                or "del"
                or "elif"
                or "else"
                or "except"
                or "finally"
                or "for"
                or "from"
                or "global"
                or "if"
                or "import"
                or "in"
                or "is"
                or "lambda"
                or "nonlocal"
                or "not"
                or "or"
                or "pass"
                or "raise"
                or "return"
                or "try"
                or "while"
                or "with"
                or "yield";

    // -----------------------------------------------------------------------------
    // Shared helpers
    // -----------------------------------------------------------------------------

    private static PythonManagedObjectValue BindSelf(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> positional,
        string typeName,
        TextSpan span,
        out IReadOnlyList<PythonValue> rest
    )
    {
        if (receiver is PythonManagedObjectValue bound)
        {
            rest = positional;
            return bound;
        }
        if (receiver is null && positional.Count == 0)
        {
            throw Fault($"descriptor '__init__' of '{typeName}' object needs an argument", span);
        }
        var self = receiver ?? positional[0];
        if (self is PythonManagedObjectValue instance)
        {
            rest = receiver is null ? [.. positional.Skip(1)] : positional;
            return instance;
        }
        throw Fault(
            $"descriptor '__init__' for '{typeName}' objects doesn't apply to a "
                + $"'{ManagedObjectProtocols.GetTypeName(self)}' object",
            span
        );
    }

    /// <summary>Binds a native function whose parameters are all keyword-or-positional.</summary>
    private static PythonValue[] BindOptions(
        string functionName,
        string[] parameters,
        PythonValue[] defaults,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (positional.Count > parameters.Length)
        {
            throw Fault(
                $"{functionName}() {FormatPositionalCount(parameters.Length, positional.Count, 0)}",
                span
            );
        }
        var slots = new PythonValue?[parameters.Length];
        for (var index = 0; index < positional.Count; index++)
        {
            slots[index] = positional[index];
        }
        for (var index = 0; index < keywordNames.Count; index++)
        {
            var slot = Array.IndexOf(parameters, keywordNames[index]);
            if (slot < 0)
            {
                throw Fault(
                    $"{functionName}() got an unexpected keyword argument '{keywordNames[index]}'",
                    span
                );
            }
            if (slots[slot] is not null)
            {
                throw Fault(
                    $"{functionName}() got multiple values for argument '{keywordNames[index]}'",
                    span
                );
            }
            slots[slot] = keywordValues[index];
        }
        for (var index = 0; index < slots.Length; index++)
        {
            slots[index] ??= defaults[index];
        }
        return [.. slots.Select(slot => slot!)];
    }

    /// <summary>CPython's positional-count diagnostic for an ordinary function.</summary>
    private static string FormatPositionalCount(int maximum, int given, int minimum)
    {
        var head =
            minimum == maximum
                ? $"takes {maximum} positional argument{(maximum == 1 ? "" : "s")}"
                : $"takes from {minimum} to {maximum} positional arguments";
        return $"{head} but {given} {(given == 1 ? "was" : "were")} given";
    }

    /// <summary>`'a'`, `'a' and 'b'`, `'a', 'b', and 'c'` — the quoted list CPython prints.</summary>
    private static string JoinNames(List<string> names)
    {
        var quoted = names.Select(name => $"'{name}'").ToList();
        return quoted.Count switch
        {
            0 => string.Empty,
            1 => quoted[0],
            2 => $"{quoted[0]} and {quoted[1]}",
            _ => $"{string.Join(", ", quoted.Take(quoted.Count - 1))}, and {quoted[^1]}",
        };
    }

    /// <summary>
    /// The `__doc__` signature CPython builds from `inspect.signature(cls)`: the init
    /// parameters with their annotations, `<factory>` for a default factory, and a bare
    /// `*` before the first keyword-only parameter.
    /// </summary>
    private static string BuildInitSignature(
        List<PythonManagedObjectValue> stdFields,
        List<PythonManagedObjectValue> kwOnlyFields
    )
    {
        var parts = new List<string>();
        foreach (var field in stdFields)
        {
            parts.Add(InitParameterText(field));
        }
        if (kwOnlyFields.Count != 0)
        {
            parts.Add("*");
            foreach (var field in kwOnlyFields)
            {
                parts.Add(InitParameterText(field));
            }
        }
        return $"({string.Join(", ", parts)})";
    }

    private static string InitParameterText(PythonManagedObjectValue field)
    {
        var state = StateOf(field)!;
        var builder = new StringBuilder(state.Name)
            .Append(": ")
            .Append(FormatAnnotation(state.Type));
        if (!IsMissing(state.DefaultFactory))
        {
            builder.Append(" = <factory>");
        }
        else if (!IsMissing(state.Default))
        {
            builder.Append(" = ").Append(state.Default.ToRepresentationString());
        }
        return builder.ToString();
    }

    private static string FormatAnnotation(PythonValue annotation) =>
        annotation switch
        {
            PythonManagedTypeValue managed => managed.Module is null or "builtins"
                ? managed.QualName ?? managed.Name
                : $"{managed.Module}.{managed.QualName}",
            PythonBuiltinTypeValue builtin => builtin.ModuleName is null or "builtins"
                ? builtin.Name
                : $"{builtin.ModuleName}.{builtin.Name}",
            PythonExceptionTypeValue exception => exception.Name,
            _ => annotation.ToRepresentationString(),
        };

    private static PythonRuntimeException Fault(
        string message,
        TextSpan span,
        string type = "TypeError"
    ) => ManagedObjectProtocols.Fault(ErrorCode, message, span, type);
}
