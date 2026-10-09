// The `enum` module.
//
// Member creation, aliasing, lookup and the error surface follow CPython 3.14.7
// Lib/enum.py (PSF-2.0), re-implemented natively on the managed runtime rather than
// by porting the Python source. `EnumType` is a managed metaclass whose `__call__`
// performs value lookup and whose `__new__` performs member population, so
// `type(Color)` and `repr(Color)` match CPython and `Color(1)` never constructs an
// instance. Enum members are managed instances carrying a `MemberState` payload;
// `name`/`value`/iteration/containment are properties and protocols on `Enum` and
// `EnumType` exactly where CPython defines them.
//
// Bounded by design, and stated in the accompanying report:
//  * The data type of an `IntEnum`/`StrEnum`/`IntFlag` is recorded as `_member_type_`
//    and its behaviour is delegated to explicitly, but the builtin type itself is not
//    part of the class's method resolution order, so `isinstance(member, int)` is
//    False and `IntEnum.__mro__`/`__bases__` omit `int` (CPython reports them).
//  * `EnumDict` is a name-only stand-in type (the class body runs against a real
//    dictionary so `__classcell__` and friends keep working), so `issubclass(EnumDict,
//    dict)` is False where CPython reports True.
//  * `enum.property` is a managed descriptor rather than a `DynamicClassAttribute`.
//  * A name assigned twice in an enum body silently keeps the last value instead of
//    raising TypeError (the namespace is a plain dictionary, so the earlier binding is
//    not observable to the runtime).

using System.Numerics;
using System.Runtime.CompilerServices;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal static class PythonEnum
{
    private const string ErrorCode = "DPY4050";
    private const string Module = "enum";

    /// <summary>Marks the dictionaries `EnumType.__prepare__` handed to a class body.</summary>
    private static readonly ConditionalWeakTable<PythonDictionaryValue, NamespaceMarker> Namespaces =
        new();

    // `PythonManagedObjectValue.Payload` is read-only and a user `__new__` may hand
    // back an object this module never built, so the state rides in weak tables
    // keyed by the instance instead (managed object identity is by reference).
    private static readonly ConditionalWeakTable<
        PythonManagedObjectValue,
        MemberState
    > MemberStates = new();
    private static readonly ConditionalWeakTable<
        PythonManagedObjectValue,
        AutoState
    > AutoStates = new();
    private static readonly ConditionalWeakTable<
        PythonManagedObjectValue,
        PropertyState
    > PropertyStates = new();
    private static readonly ConditionalWeakTable<
        PythonManagedObjectValue,
        PythonValue
    > WrapperStates = new();

    // Declared before the module types below: their field initializers record the
    // per-class metadata this table holds.
    private static readonly ConditionalWeakTable<PythonManagedTypeValue, EnumInfo> Infos = new();

    private sealed class NamespaceMarker
    {
        internal string? ClassName;
    }

    /// <summary>The member payload carried by every enum member instance.</summary>
    private sealed class MemberState
    {
        internal string? Name;
        internal PythonValue Value = PythonNoneValue.Instance;
        internal int SortOrder;
    }

    /// <summary>The payload of an `enum.property` descriptor.</summary>
    private sealed class PropertyState
    {
        internal PythonValue? Fget;
        internal PythonValue? Fset;
        internal PythonValue? Fdel;
        internal string? Name;
        internal string? ClsName;
        internal PythonValue? Member;
        internal string? AttrType;
        internal PythonManagedTypeValue? ClsType;
    }

    /// <summary>The payload of an `auto()` sentinel; `value` starts as `_auto_null`.</summary>
    private sealed class AutoState
    {
        internal PythonValue Value = PythonNoneValue.Instance;
    }

    // The hand-built module types. Every enum class is an instance of EnumTypeType;
    // EnumTypeType itself is an instance of the builtin `type`.
    private static readonly PythonManagedTypeValue EnumTypeType = BuildBareType(
        "EnumType",
        isMetaclass: true
    );
    private static readonly PythonManagedTypeValue EnumDictType = BuildBareType(
        "EnumDict",
        isMetaclass: false
    );
    private static readonly PythonManagedTypeValue PropertyType = BuildBareType(
        "property",
        isMetaclass: false
    );
    private static readonly PythonManagedTypeValue AutoType = BuildBareType(
        "auto",
        isMetaclass: false
    );
    private static readonly PythonManagedTypeValue MemberType = BuildBareType(
        "member",
        isMetaclass: false
    );
    private static readonly PythonManagedTypeValue NonMemberType = BuildBareType(
        "nonmember",
        isMetaclass: false
    );

    private static readonly PythonManagedTypeValue EnumClass = BuildEnumClass("Enum", []);
    private static readonly PythonManagedTypeValue ReprEnumClass = BuildEnumClass(
        "ReprEnum",
        [EnumClass]
    );
    private static readonly PythonManagedTypeValue IntEnumClass = BuildEnumClass(
        "IntEnum",
        [ReprEnumClass]
    );
    private static readonly PythonManagedTypeValue StrEnumClass = BuildEnumClass(
        "StrEnum",
        [ReprEnumClass]
    );

    private static readonly PythonManagedTypeValue FlagBoundaryClass = BuildEnumClass(
        "FlagBoundary",
        [StrEnumClass]
    );
    private static readonly PythonManagedObjectValue BoundaryStrict = AddModuleMember(
        FlagBoundaryClass,
        "STRICT",
        new PythonTextValue("strict")
    );
    private static readonly PythonManagedObjectValue BoundaryConform = AddModuleMember(
        FlagBoundaryClass,
        "CONFORM",
        new PythonTextValue("conform")
    );
    private static readonly PythonManagedObjectValue BoundaryEject = AddModuleMember(
        FlagBoundaryClass,
        "EJECT",
        new PythonTextValue("eject")
    );
    private static readonly PythonManagedObjectValue BoundaryKeep = AddModuleMember(
        FlagBoundaryClass,
        "KEEP",
        new PythonTextValue("keep")
    );

    private static readonly PythonManagedTypeValue EnumCheckClass = BuildEnumClass(
        "EnumCheck",
        [StrEnumClass]
    );

    private static readonly PythonManagedTypeValue FlagClass = BuildEnumClass("Flag", [EnumClass]);
    private static readonly PythonManagedTypeValue IntFlagClass = BuildEnumClass(
        "IntFlag",
        [ReprEnumClass, FlagClass]
    );

    private static readonly PythonManagedObjectValue AutoNull = new(
        PythonBuiltinFunctions.ObjectType
    );
    private static readonly PythonManagedObjectValue NotGiven = new(
        PythonBuiltinFunctions.ObjectType
    );

    /// <summary>`auto(5)` and friends keep the value in an `AutoState` payload.</summary>
    private static readonly PythonValue AutoProtocolGetValue = Protocol(
        "value",
        (target, _) => AutoStateOf(target!)?.Value ?? PythonNoneValue.Instance
    );

    /// <summary>`Enum._generate_next_value_`, the staticmethod every enum inherits.</summary>
    private static readonly PythonValue DefaultGnvFunction = new PythonStaticMethodValue(
        Protocol("_generate_next_value_", DefaultGenerateNextValue)
    );

    /// <summary>Field initializers run in textual order; this one installs everything.</summary>
    private static readonly bool Installed = InstallAll();

    private static bool InstallAll()
    {
        // A subclass's `_get_mixins_` reads the data type out of `_member_type_`, so the
        // module's own classes publish theirs exactly like a user-declared enum would.
        SetMemberType(EnumClass, PythonBuiltinFunctions.Object);
        SetMemberType(ReprEnumClass, PythonBuiltinFunctions.Object);
        SetMemberType(IntEnumClass, PythonBuiltinTypes.Int);
        SetMemberType(StrEnumClass, PythonBuiltinTypes.Str);
        SetMemberType(FlagClass, PythonBuiltinFunctions.Object);
        SetMemberType(IntFlagClass, PythonBuiltinTypes.Int);
        SetMemberType(FlagBoundaryClass, PythonBuiltinTypes.Str);
        SetMemberType(EnumCheckClass, PythonBuiltinTypes.Str);
        SetBoundary(FlagClass, BoundaryStrict);
        SetBoundary(IntFlagClass, BoundaryKeep);

        InstallMetaclassProtocols();
        InstallClassProtocols(EnumClass);
        InstallClassProtocols(ReprEnumClass);
        InstallClassProtocols(IntEnumClass);
        InstallClassProtocols(StrEnumClass);
        InstallClassProtocols(FlagClass);
        InstallClassProtocols(IntFlagClass);
        // The default `_generate_next_value_` lives on `Enum` alone: `Flag` and
        // `StrEnum` install their own, and a subclass inherits whichever applies.
        Install(
            EnumClass,
            "_generate_next_value_",
            new PythonStaticMethodValue(Protocol("_generate_next_value_", DefaultGenerateNextValue))
        );
        InstallFlagProtocols();
        InstallMixinProtocols();
        InstallAutoProtocols();
        InstallPropertyProtocols();
        InstallWrapperProtocols(MemberType);
        InstallWrapperProtocols(NonMemberType);
        AddModuleMember(EnumCheckClass, "UNIQUE", new PythonTextValue("one name per value"));
        AddModuleMember(
            EnumCheckClass,
            "CONTINUOUS",
            new PythonTextValue("all values must be consecutive")
        );
        AddModuleMember(
            EnumCheckClass,
            "NAMED_FLAGS",
            new PythonTextValue("all bits must be named")
        );
        return true;
    }

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        _ = Installed;
        globals.SetValue("EnumType", EnumTypeType);
        globals.SetValue("EnumMeta", EnumTypeType);
        globals.SetValue("EnumDict", EnumDictType);
        globals.SetValue("Enum", EnumClass);
        globals.SetValue("ReprEnum", ReprEnumClass);
        globals.SetValue("IntEnum", IntEnumClass);
        globals.SetValue("StrEnum", StrEnumClass);
        globals.SetValue("Flag", FlagClass);
        globals.SetValue("IntFlag", IntFlagClass);
        globals.SetValue("FlagBoundary", FlagBoundaryClass);
        globals.SetValue("EnumCheck", EnumCheckClass);
        globals.SetValue("property", PropertyType);
        globals.SetValue("auto", AutoType);
        globals.SetValue("member", MemberType);
        globals.SetValue("nonmember", NonMemberType);
        globals.SetValue("unique", CreateUnique());
        globals.SetValue("show_flag_values", CreateShowFlagValues());
        globals.SetValue("_auto_null", AutoNull);
        globals.SetValue("_not_given", NotGiven);
        globals.SetValue("STRICT", BoundaryStrict);
        globals.SetValue("CONFORM", BoundaryConform);
        globals.SetValue("EJECT", BoundaryEject);
        globals.SetValue("KEEP", BoundaryKeep);
    }

    // -------------------------------------------------------------------------
    // Construction helpers
    // -------------------------------------------------------------------------

    private static PythonManagedTypeValue BuildBareType(string name, bool isMetaclass)
    {
        // Only EnumType itself is a metaclass; the rest are ordinary classes whose
        // instances are made the usual way (so `enum.auto()` and `enum.property(...)`
        // construct through `__init__`, and `repr` reads `<class 'enum.property'>`).
        var type = new PythonManagedTypeValue(name, PythonBuiltinFunctions.ObjectType)
        {
            Module = Module,
            Metaclass = PythonBuiltinTypes.Type,
            IsMetaclass = isMetaclass,
        };
        type.SetDeclaredBases(new PythonTupleValue([PythonBuiltinFunctions.Object]));
        type.SetResolutionOrder(new PythonTupleValue([type, PythonBuiltinFunctions.Object]));
        return type;
    }

    private static PythonManagedTypeValue BuildEnumClass(
        string name,
        IReadOnlyList<PythonValue> declaredBases
    )
    {
        var bases = declaredBases.Count == 0 ? [PythonBuiltinFunctions.Object] : declaredBases;
        var linearized = PythonTypeMro.LinearizeBases(bases, default);
        var type = new PythonManagedTypeValue(name, [], [])
        {
            Module = Module,
            Metaclass = EnumTypeType,
        };
        type.SetDeclaredBases(new PythonTupleValue([.. bases]));
        type.SetResolutionOrder(new PythonTupleValue([type, .. linearized]));
        return type;
    }

    /// <summary>Adds a member to one of the module's own classes.</summary>
    private static PythonManagedObjectValue AddModuleMember(
        PythonManagedTypeValue type,
        string name,
        PythonValue value
    )
    {
        var info = EnsureInfo(type);
        var member = CreateMember(type, info, name, value, isFlag: false, default);
        SetMemberAttribute(type, name, member);
        return member;
    }

    /// <summary>Publishes the bookkeeping structures CPython keeps in the class dict.</summary>
    private static void PublishInfo(PythonManagedTypeValue type, EnumInfo info)
    {
        type.Attributes["_member_names_"] = info.MemberNames;
        type.Attributes["_member_map_"] = info.MemberMap;
        type.Attributes["_value2member_map_"] = info.ValueMap;
        type.Attributes["_hashable_values_"] = info.HashableValues;
        type.Attributes["_unhashable_values_"] = info.UnhashableValues;
        type.Attributes["_unhashable_values_map_"] = info.UnhashableValuesMap;
        type.Attributes["_member_type_"] = info.MemberType;
        type.Attributes["_value_repr_"] = PythonNoneValue.Instance;
        type.Attributes["_use_args_"] = PythonTruthValue.FromBoolean(info.UseArgs);
        type.Attributes["_flag_mask_"] = PythonWholeNumberValue.Create(info.FlagMask);
        type.Attributes["_singles_mask_"] = PythonWholeNumberValue.Create(info.SinglesMask);
        type.Attributes["_all_bits_"] = PythonWholeNumberValue.Create(info.AllBits);
        type.Attributes["_inverted_"] = PythonNoneValue.Instance;
        if (info.MemberType is PythonBuiltinTypeValue dataType)
        {
            type.Attributes["__new__"] = dataType;
        }
    }

    // -------------------------------------------------------------------------
    // Per-class bookkeeping
    // -------------------------------------------------------------------------

    private sealed class EnumInfo
    {
        internal PythonDictionaryValue MemberMap = new([]);
        internal PythonDictionaryValue ValueMap = new([]);
        internal PythonListValue MemberNames = new([]);
        internal PythonListValue HashableValues = new([]);
        internal PythonListValue UnhashableValues = new([]);
        internal PythonDictionaryValue UnhashableValuesMap = new([]);
        internal PythonValue MemberType = PythonBuiltinFunctions.Object;
        internal PythonManagedTypeValue? FirstEnum;
        internal PythonValue? NewMember;
        internal bool UseArgs;
        internal bool SaveNew;
        internal PythonValue Boundary = PythonNoneValue.Instance;
        internal BigInteger FlagMask;
        internal BigInteger SinglesMask;
        internal BigInteger AllBits;
    }

    private static EnumInfo EnsureInfo(PythonManagedTypeValue type)
    {
        if (Infos.TryGetValue(type, out var existing))
        {
            return existing;
        }

        var info = new EnumInfo();
        Infos.Add(type, info);
        return info;
    }

    private static bool TryGetInfo(PythonManagedTypeValue type, out EnumInfo info) =>
        Infos.TryGetValue(type, out info!);

    internal static bool IsEnumClass(PythonValue value) =>
        value is PythonManagedTypeValue type && IsEnumClass(type);

    /// <summary>
    /// An `IntEnum`/`StrEnum` member answers `isinstance` as the data type it mixes in,
    /// which is what CPython sees because its members really are that type.
    /// </summary>
    internal static bool IsInstanceOfMemberType(PythonValue value, string typeName) =>
        value is PythonManagedObjectValue { Type: { } type }
        && IsEnumClass(type)
        && EnsureInfo(type).MemberType is PythonBuiltinTypeValue builtin
        && builtin.Name == typeName;

    internal static bool IsEnumClass(PythonManagedTypeValue type)
    {
        if (ReferenceEquals(type, EnumClass))
        {
            return true;
        }

        foreach (var entry in type.Mro)
        {
            if (ReferenceEquals(entry, EnumClass))
            {
                return true;
            }
        }

        return type.Attributes.TryGetValue("_member_type_", out _);
    }

    internal static bool IsFlagClass(PythonManagedTypeValue type)
    {
        if (ReferenceEquals(type, FlagClass))
        {
            return true;
        }

        foreach (var entry in type.Mro)
        {
            if (ReferenceEquals(entry, FlagClass))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReprEnum(PythonManagedTypeValue type)
    {
        if (ReferenceEquals(type, ReprEnumClass))
        {
            return true;
        }

        foreach (var entry in type.Mro)
        {
            if (ReferenceEquals(entry, ReprEnumClass))
            {
                return true;
            }
        }

        return false;
    }

    private static PythonValue? LookupClassValue(PythonManagedTypeValue type, string name)
    {
        foreach (var entry in type.Mro)
        {
            if (entry.Attributes.TryGetValue(name, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static PythonValue LookupBoundClassMethod(PythonManagedTypeValue type, string name) =>
        LookupClassValue(type, name) ?? PythonNoneValue.Instance;

    /// <summary>Records the data type mixed into a class of the module's own.</summary>
    private static void SetMemberType(PythonManagedTypeValue type, PythonValue memberType)
    {
        EnsureInfo(type).MemberType = memberType;
        type.Attributes["_member_type_"] = memberType;
    }

    private static void SetBoundary(PythonManagedTypeValue type, PythonManagedObjectValue boundary)
    {
        type.Attributes["_boundary_"] = boundary;
        if (TryGetInfo(type, out var info))
        {
            info.Boundary = boundary;
        }
    }

    private static PythonValue GetBoundary(PythonManagedTypeValue type) =>
        LookupClassValue(type, "_boundary_") ?? BoundaryStrict;

    // -------------------------------------------------------------------------
    // Small shared helpers
    // -------------------------------------------------------------------------

    private static PythonRuntimeException Fault(
        string message,
        TextSpan span,
        string type = "TypeError"
    ) => ManagedObjectProtocols.Fault(ErrorCode, message, span, type);

    private static PythonValue Invoke(
        PythonValue callable,
        PythonValue[] arguments,
        TextSpan span
    ) =>
        UserObjectProtocols.Dispatcher is { } dispatcher
            ? dispatcher.Invoke(callable, arguments, span)
            : ManagedObjectProtocols.Call(callable, arguments, span);

    private static PythonValue InvokeWithKeywords(
        PythonValue callable,
        PythonValue[] arguments,
        string[] keywordNames,
        PythonValue[] keywordValues,
        TextSpan span
    ) =>
        UserObjectProtocols.Dispatcher is { } dispatcher
            ? dispatcher.InvokeWithKeywords(
                callable,
                arguments,
                keywordNames,
                keywordValues,
                span
            )
            : ManagedObjectProtocols.Call(callable, arguments, span);

    private static PythonTextValue Format(PythonValue value, string specification) =>
        new(PythonValueFormatter.Format(value, specification, default));

    private static PythonTextValue Text(string value) => new(value);

    private static PythonProtocolFunctionValue Protocol(
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> invoke
    ) => new(name, invoke);

    private static PythonProtocolFunctionValue Protocol(
        string name,
        ProtocolKeywordInvoker invoke
    ) => new(name, (target, arguments) => invoke(target, arguments, [], []), invoke);

    private static void Install(PythonManagedTypeValue type, string name, PythonValue value) =>
        type.Attributes[name] = value;

    private static bool TryLookupKey(
        PythonDictionaryValue dictionary,
        PythonValue key,
        out PythonValue value
    )
    {
        if (!ManagedObjectProtocols.IsHashable(key))
        {
            value = PythonNoneValue.Instance;
            return false;
        }

        if (ManagedObjectProtocols.TryFindDictionaryItem(dictionary, key, out var item))
        {
            value = item.Value;
            return true;
        }

        value = PythonNoneValue.Instance;
        return false;
    }

    private static bool TryLookupName(
        PythonDictionaryValue dictionary,
        string key,
        out PythonValue value
    ) => TryLookupKey(dictionary, Text(key), out value);

    private static bool IsObjectMemberType(PythonValue memberType) =>
        memberType is PythonBuiltinTypeValue { Name: "object" };

    private static bool IsIntValue(PythonValue value) =>
        value is PythonWholeNumberValue or PythonTruthValue || TryMemberInteger(value, out _);

    /// <summary>
    /// An `int`-mixin member (`IntEnum`/`IntFlag`) stands in for the integer it
    /// wraps, both as an operand and as a value the module itself compares.
    /// </summary>
    private static bool TryMemberInteger(PythonValue value, out BigInteger result)
    {
        result = BigInteger.Zero;
        if (!IsInstanceOfMemberType(value, "int"))
        {
            return false;
        }

        switch (StateOf(value)?.Value)
        {
            case PythonWholeNumberValue whole:
                result = whole.Value;
                return true;
            case PythonTruthValue truth:
                result = truth.Value ? BigInteger.One : BigInteger.Zero;
                return true;
            default:
                return false;
        }
    }

    private static BigInteger AsBigInteger(PythonValue value) =>
        value switch
        {
            PythonWholeNumberValue whole => whole.Value,
            PythonTruthValue truth => truth.Value ? BigInteger.One : BigInteger.Zero,
            _ => TryMemberInteger(value, out var memberValue) ? memberValue : BigInteger.Zero,
        };

    private static bool IsSingleBit(BigInteger value) =>
        value > BigInteger.Zero && (value & (value - BigInteger.One)).IsZero;

    private static MemberState? StateOf(PythonValue value) =>
        value is PythonManagedObjectValue instance
        && MemberStates.TryGetValue(instance, out var state)
            ? state
            : null;

    private static void SetMemberState(PythonValue value, MemberState state)
    {
        if (value is not PythonManagedObjectValue instance)
        {
            return;
        }

        MemberStates.Remove(instance);
        MemberStates.Add(instance, state);
    }

    private static AutoState? AutoStateOf(PythonValue value) =>
        value is PythonManagedObjectValue instance
        && AutoStates.TryGetValue(instance, out var state)
            ? state
            : null;

    private static void SetAutoState(PythonValue value, AutoState state)
    {
        if (value is not PythonManagedObjectValue instance)
        {
            return;
        }

        AutoStates.Remove(instance);
        AutoStates.Add(instance, state);
    }

    private static PropertyState? PropertyStateOf(PythonValue value) =>
        value is PythonManagedObjectValue instance
        && PropertyStates.TryGetValue(instance, out var state)
            ? state
            : null;

    private static bool IsDescriptor(PythonValue value) =>
        value
            is PythonFunctionValue
                or PythonPropertyValue
                or PythonDescriptorValue
                or PythonProtocolFunctionValue
                or PythonClassMethodValue
                or PythonStaticMethodValue
                or PythonTypeMetadataDescriptorValue
        || (
            ManagedObjectProtocols.GetManagedType(value) is { } type
            && (
                ManagedObjectProtocols.TryGetTypeAttribute(type, "__get__", out _)
                || ManagedObjectProtocols.TryGetTypeAttribute(type, "__set__", out _)
                || ManagedObjectProtocols.TryGetTypeAttribute(type, "__delete__", out _)
            )
        );

    private static bool IsPrivateName(string className, string name)
    {
        var pattern = $"_{className}__";
        return name.Length > pattern.Length
            && name.StartsWith(pattern, StringComparison.Ordinal)
            && (name[^1] != '_' || name[^2] != '_');
    }

    private static bool IsSunder(string name) =>
        name.Length > 2
        && name[0] == '_'
        && name[^1] == '_'
        && name[1] != '_'
        && name[^2] != '_';

    private static bool IsDunder(string name) =>
        name.Length > 4
        && name.StartsWith("__", StringComparison.Ordinal)
        && name.EndsWith("__", StringComparison.Ordinal)
        && name[2] != '_'
        && name[^3] != '_';

    private static bool IsInternalClass(string className, PythonValue value)
    {
        if (value is not PythonManagedTypeValue type)
        {
            return false;
        }

        var qualName = type.QualName ?? type.Name;
        var expected = $"{className}.{type.Name}";
        return qualName == expected || qualName.EndsWith($".{expected}", StringComparison.Ordinal);
    }

    private static PythonValue UnwrapStatic(PythonValue value) =>
        value is PythonStaticMethodValue staticMethod ? staticMethod.Function : value;

    private static PythonValue UnwrapWrapper(PythonValue value) =>
        value is PythonManagedObjectValue instance
        && WrapperStates.TryGetValue(instance, out var payload)
            ? payload
            : value;

    private static void SetWrapperState(PythonValue value, PythonValue payload)
    {
        if (value is not PythonManagedObjectValue instance)
        {
            return;
        }

        WrapperStates.Remove(instance);
        WrapperStates.Add(instance, payload);
    }

    // -------------------------------------------------------------------------
    // EnumType, the metaclass
    // -------------------------------------------------------------------------

    private static void InstallMetaclassProtocols()
    {
        Install(EnumTypeType, "__prepare__", Protocol("__prepare__", PrepareNamespace));
        Install(EnumTypeType, "__new__", Protocol("__new__", EnumTypeNew));
        Install(EnumTypeType, "__init__", Protocol("__init__", IgnoreInitializer));
        Install(EnumTypeType, "__call__", Protocol("__call__", EnumTypeCall));
        Install(EnumTypeType, "mro", Protocol("mro", MetaclassMro));
        Install(EnumTypeType, "__bool__", Protocol("__bool__", (_, _) => PythonTruthValue.True));
        Install(EnumTypeType, "__repr__", Protocol("__repr__", DescribeClassTarget));
        Install(EnumTypeType, "__getitem__", Protocol("__getitem__", MetaclassGetItem));
        Install(EnumTypeType, "__iter__", Protocol("__iter__", MetaclassIter));
        Install(EnumTypeType, "__len__", Protocol("__len__", MetaclassLen));
        Install(EnumTypeType, "__contains__", Protocol("__contains__", MetaclassContains));
        Install(EnumTypeType, "__reversed__", Protocol("__reversed__", MetaclassReversed));
        Install(EnumTypeType, "__setattr__", Protocol("__setattr__", MetaclassSetAttr));
        Install(EnumTypeType, "__delattr__", Protocol("__delattr__", MetaclassDeleteAttr));
        Install(
            EnumTypeType,
            "__members__",
            new PythonDescriptorValue(
                "__members__",
                target =>
                    new PythonMappingProxyValue(MemberMapOf((PythonManagedTypeValue)target))
            )
        );
    }

    private static PythonDictionaryValue MemberMapOf(PythonManagedTypeValue type) =>
        TryGetInfo(type, out var info)
            ? info.MemberMap
            : new PythonDictionaryValue([]);

    private static PythonValue IgnoreInitializer(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    ) => PythonNoneValue.Instance;

    private static PythonValue PrepareNamespace(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var name = positional.Count > 0 && positional[0] is PythonTextValue text ? text.Value : "";
        var bases = positional.Count > 1 && positional[1] is PythonTupleValue tuple
            ? tuple.Elements
            : [];
        CheckForExistingMembers(name, bases);
        var prepared = new PythonDictionaryValue([]);
        prepared.AddItem(new PythonDictionaryItemValue(Text("__module__"), PythonNoneValue.Instance));
        Namespaces.Add(prepared, new NamespaceMarker { ClassName = name });
        return prepared;
    }

    private static void CheckForExistingMembers(string className, IReadOnlyList<PythonValue> bases)
    {
        foreach (var chain in bases)
        {
            foreach (var entry in PythonBuiltinTypes.GetMro(chain).Elements)
            {
                if (
                    entry is PythonManagedTypeValue managed
                    && IsEnumClass(managed)
                    && MemberNamesOf(managed).Elements.Count != 0
                )
                {
                    throw Fault(
                        $"<enum '{className}'> cannot extend {managed.ToRepresentationString()}",
                        default,
                        "TypeError"
                    );
                }
            }
        }
    }

    private static PythonListValue MemberNamesOf(PythonManagedTypeValue type) =>
        LookupClassValue(type, "_member_names_") as PythonListValue ?? new PythonListValue([]);

    private static PythonValue DescribeClassTarget(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    ) => new PythonTextValue(DescribeClass((PythonManagedTypeValue)target!));

    private static string DescribeClass(PythonManagedTypeValue type) =>
        IsFlagClass(type) ? $"<flag '{QualifiedName(type)}'>" : $"<enum '{QualifiedName(type)}'>";

    private static string QualifiedName(PythonManagedTypeValue type) =>
        string.Join(".", type.Qualifiers());

    private static PythonValue EnumTypeNew(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = target;
        // type.__call__ passes the metaclass as the first argument of __new__.
        var offset = positional.Count == 4 ? 1 : 0;
        if (positional.Count - offset != 3)
        {
            throw Fault(
                $"EnumType.__new__() takes exactly 3 arguments ({positional.Count} given)",
                default
            );
        }

        var className = positional[offset] is PythonTextValue name ? name.Value : "";
        var bases = positional[offset + 1] as PythonTupleValue ?? new PythonTupleValue([]);
        if (positional[offset + 2] is not PythonDictionaryValue classBody)
        {
            throw Fault("'dict' object has no attribute '_member_names'", default, "AttributeError");
        }

        if (!Namespaces.TryGetValue(classBody, out var marker))
        {
            throw Fault("'dict' object has no attribute '_member_names'", default, "AttributeError");
        }

        marker.ClassName = className;
        return CreateEnumClass(className, bases.Elements, classBody, default);
    }

    private static PythonManagedTypeValue CreateEnumClass(
        string className,
        IReadOnlyList<PythonValue> declaredBases,
        PythonDictionaryValue classBody,
        TextSpan span
    )
    {
        var (memberType, firstEnum) = FindMixins(className, declaredBases, span);
        var effectiveBases = new List<PythonValue>();
        foreach (var baseValue in declaredBases)
        {
            if (
                baseValue is PythonBuiltinTypeValue
                || baseValue
                    is PythonManagedTypeValue
                        {
                            Name: "object"
                        }
            )
            {
                continue;
            }

            effectiveBases.Add(baseValue);
        }

        if (effectiveBases.Count == 0)
        {
            effectiveBases.Add(EnumClass);
        }

        var enumClass = (PythonManagedTypeValue)
            UserObjectProtocols.Dispatcher!.CreateType(
                EnumTypeType,
                [
                    new PythonTextValue(className),
                    new PythonTupleValue([.. effectiveBases]),
                    classBody,
                ],
                [],
                [],
                span
            );

        var info = EnsureInfo(enumClass);
        info.MemberType = memberType;
        info.FirstEnum = firstEnum;
        var boundary = LookupClassValue(enumClass, "_boundary_");
        info.Boundary = boundary ?? BoundaryStrict;
        FindNew(enumClass, info, classBody);
        PopulateMembers(enumClass, info, classBody, span);
        CompleteClass(enumClass, info, classBody, span);
        return enumClass;
    }

    /// <summary>The data type mixed into the enum, and the last declared enum base.</summary>
    private static (PythonValue MemberType, PythonManagedTypeValue? FirstEnum) FindMixins(
        string className,
        IReadOnlyList<PythonValue> bases,
        TextSpan span
    )
    {
        PythonManagedTypeValue? firstEnum = null;
        foreach (var chain in bases)
        {
            if (chain is PythonManagedTypeValue managed && IsEnumClass(managed))
            {
                firstEnum = managed;
            }
        }

        if (firstEnum is null && bases.Count != 0)
        {
            throw Fault(
                "new enumerations should be created as `EnumName([mixin_type, ...] [data_type,] enum_type)`",
                span
            );
        }

        var dataTypes = new List<PythonValue>();
        foreach (var chain in bases)
        {
            PythonValue? candidate = null;
            foreach (var baseValue in PythonBuiltinTypes.GetMro(chain).Elements)
            {
                if (baseValue is PythonBuiltinTypeValue { Name: "object" })
                {
                    continue;
                }

                if (baseValue is PythonManagedTypeValue enumBase && IsEnumClass(enumBase))
                {
                    var inherited =
                        LookupClassValue(enumBase, "_member_type_") ?? PythonBuiltinFunctions.Object;
                    if (!IsObjectMemberType(inherited))
                    {
                        AddDataType(dataTypes, inherited);
                    }

                    break;
                }

                if (
                    baseValue is PythonBuiltinTypeValue builtin
                    && !ReferenceEquals(builtin, PythonBuiltinTypes.Type)
                )
                {
                    AddDataType(dataTypes, candidate ?? builtin);
                    break;
                }

                if (baseValue is PythonManagedTypeValue other)
                {
                    candidate ??= other;
                    if (other.Attributes.TryGetValue("__new__", out _))
                    {
                        AddDataType(dataTypes, candidate);
                        break;
                    }
                }
            }
        }

        if (dataTypes.Count > 1)
        {
            throw Fault(
                $"too many data types for {Text(className).ToRepresentationString()}: {dataTypes.Count}",
                span
            );
        }

        return (dataTypes.Count == 0 ? PythonBuiltinFunctions.Object : dataTypes[0], firstEnum);
    }

    private static void AddDataType(List<PythonValue> dataTypes, PythonValue candidate)
    {
        if (!dataTypes.Any(entry => ReferenceEquals(entry, candidate)))
        {
            dataTypes.Add(candidate);
        }
    }

    private static void FindNew(
        PythonManagedTypeValue enumClass,
        EnumInfo info,
        PythonDictionaryValue classBody
    )
    {
        PythonValue? declared = null;
        if (TryLookupName(classBody, "__new__", out var namespaceNew))
        {
            declared = UnwrapStatic(namespaceNew);
        }

        if (declared is not null)
        {
            info.NewMember = declared;
            info.UseArgs = true;
            info.SaveNew = info.FirstEnum is not null;
            if (info.SaveNew)
            {
                enumClass.Attributes["__new_member__"] = declared;
            }

            return;
        }

        if (info.FirstEnum is not null)
        {
            var saved = LookupClassValue(info.FirstEnum, "__new_member__");
            if (saved is not null && saved is not PythonNoneValue)
            {
                info.NewMember = saved;
                info.UseArgs = true;
                enumClass.Attributes["__new_member__"] = saved;
                return;
            }
        }

        info.UseArgs = !IsObjectMemberType(info.MemberType);
    }

    private static readonly HashSet<string> ReservedSunders =
    [
        "_order_",
        "_generate_next_value_",
        "_numeric_repr_",
        "_missing_",
        "_ignore_",
        "_iter_member_",
        "_iter_member_by_value_",
        "_iter_member_by_def_",
        "_add_alias_",
        "_add_value_alias_",
    ];

    private static void PopulateMembers(
        PythonManagedTypeValue enumClass,
        EnumInfo info,
        PythonDictionaryValue classBody,
        TextSpan span
    )
    {
        var ignored = new List<string>();
        string[]? order = null;
        var isFlag = IsFlagClass(enumClass);
        var lastValues = new List<PythonValue>();
        var autoCalled = false;
        PythonValue? generateNextValue = null;
        var pending = new List<(string Name, PythonValue Value)>();

        // The class namespace lives in `enumClass.Attributes` now (the metaclass
        // `__new__` runs after `type.__new__` copied it), and members are created
        // there so `__init_subclass__` and attribute lookups see them.
        foreach (var name in enumClass.Attributes.Select(item => item.Key).ToArray())
        {
            if (!enumClass.Attributes.TryGetValue(name, out var value))
            {
                continue;
            }

            if (IsPrivateName(enumClass.Name, name))
            {
                continue;
            }

            if (IsSunder(name))
            {
                if (name == "_generate_next_value_")
                {
                    if (autoCalled)
                    {
                        throw Fault("_generate_next_value_ must be defined before members", span);
                    }

                    generateNextValue = UnwrapStatic(value);
                }
                else if (name == "_order_")
                {
                    order = ReadIgnoreList(value, span).ToArray();
                }
                else if (name == "_ignore_")
                {
                    ignored.AddRange(ReadIgnoreList(value, span));
                    var already = ignored
                        .Where(entry => pending.Any(item => item.Name == entry))
                        .ToArray();
                    if (already.Length != 0)
                    {
                        throw Fault(
                            $"_ignore_ cannot specify already set names: {{{string.Join(", ", already.Select(entry => $"'{entry}'"))}}}",
                            span,
                            "ValueError"
                        );
                    }
                }
                else if (
                    !name.StartsWith("_repr_", StringComparison.Ordinal)
                    && !ReservedSunders.Contains(name)
                )
                {
                    throw Fault(
                        $"_sunder_ names, such as '{name}', are reserved for future Enum use",
                        span,
                        "ValueError"
                    );
                }

                continue;
            }

            if (IsDunder(name))
            {
                continue;
            }

            if (name.Length == 0 || name == "mro")
            {
                throw Fault($"invalid enum member name(s) '{name}'", span, "ValueError");
            }

            if (ignored.Contains(name))
            {
                continue;
            }

            if (
                value is PythonManagedObjectValue { Type: var wrapper }
                && ReferenceEquals(wrapper, NonMemberType)
            )
            {
                enumClass.Attributes[name] = UnwrapWrapper(value);
                continue;
            }

            if (IsDescriptor(value) || IsInternalClass(enumClass.Name, value))
            {
                continue;
            }

            if (
                value is PythonManagedObjectValue { Type: var memberWrapper }
                && ReferenceEquals(memberWrapper, MemberType)
            )
            {
                value = UnwrapWrapper(value);
            }

            value = ResolveAutoValue(
                enumClass,
                info,
                name,
                value,
                ref autoCalled,
                lastValues,
                generateNextValue,
                span
            );
            pending.Add((name, value));
            lastValues.Add(value);
        }

        // `_EnumDict.__setitem__` marked these for removal in `EnumType.__new__`.
        foreach (var name in ignored)
        {
            enumClass.Attributes.Remove(name);
        }

        enumClass.Attributes.Remove("_ignore_");
        enumClass.Attributes.Remove("_order_");

        foreach (var (name, value) in pending)
        {
            CreateMember(enumClass, info, name, value, isFlag, span);
        }

        ValidateOrder(enumClass, info, order, span);
    }

    private static void ValidateOrder(
        PythonManagedTypeValue enumClass,
        EnumInfo info,
        string[]? order,
        TextSpan span
    )
    {
        if (order is null)
        {
            return;
        }

        var isFlag = IsFlagClass(enumClass);
        var filtered = new List<string>();
        foreach (var entry in order)
        {
            if (!TryLookupName(info.MemberMap, entry, out var member))
            {
                filtered.Add(entry);
                continue;
            }

            var state = StateOf(member);
            var value = state?.Value ?? PythonNoneValue.Instance;
            if (isFlag && IsIntValue(value) && !IsSingleBit(AsBigInteger(value)))
            {
                continue;
            }

            if (state is not null && state.Name == entry)
            {
                filtered.Add(entry);
            }
        }

        var actual = info
            .MemberNames.Elements.Select(entry => ((PythonTextValue)entry).Value)
            .ToArray();
        if (!filtered.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw Fault(
                $"member order does not match _order_:\n  {FormatList(actual)}\n  {FormatList(filtered)}",
                span
            );
        }
    }

    private static string FormatList(IEnumerable<string> values) =>
        $"[{string.Join(", ", values.Select(value => $"'{value}'"))}]";

    private static IEnumerable<string> ReadIgnoreList(PythonValue value, TextSpan span)
    {
        if (value is PythonTextValue text)
        {
            return text.Value
                .Replace(',', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
        }

        var result = new List<string>();
        foreach (var entry in ManagedObjectProtocols.MaterializeValues(value, span, null))
        {
            result.Add(entry is PythonTextValue name ? name.Value : entry.ToString()!);
        }

        return result;
    }

    private static PythonValue ResolveAutoValue(
        PythonManagedTypeValue enumClass,
        EnumInfo info,
        string name,
        PythonValue value,
        ref bool autoCalled,
        List<PythonValue> lastValues,
        PythonValue? generateNextValue,
        TextSpan span
    )
    {
        if (AutoStateOf(value) is { } single)
        {
            return ResolveOne(info, enumClass, name, single, lastValues, generateNextValue, span);
        }

        if (value is not PythonTupleValue tuple)
        {
            return value;
        }

        var autoValues = new List<PythonValue>();
        var any = false;
        foreach (var element in tuple.Elements)
        {
            if (AutoStateOf(element) is { } state)
            {
                any = true;
                autoValues.Add(
                    ResolveOne(info, enumClass, name, state, lastValues, generateNextValue, span)
                );
                continue;
            }

            autoValues.Add(element);
        }

        return any ? new PythonTupleValue([.. autoValues]) : value;
    }

    private static PythonValue ResolveOne(
        EnumInfo info,
        PythonManagedTypeValue enumClass,
        string name,
        AutoState state,
        List<PythonValue> lastValues,
        PythonValue? generateNextValue,
        TextSpan span
    )
    {
        if (!ReferenceEquals(state.Value, AutoNull))
        {
            return state.Value;
        }

        var generator = generateNextValue ?? LookupClassValue(enumClass, "_generate_next_value_");
        var generated = Invoke(
            generator ?? DefaultGnvFunction,
            [
                Text(name),
                PythonWholeNumberValue.Create(1),
                PythonWholeNumberValue.Create(lastValues.Count),
                new PythonListValue([.. lastValues]),
            ],
            span
        );
        state.Value = generated;
        return generated;
    }

    /// <summary>Mirrors the `Enum._generate_next_value_` staticmethod.</summary>
    private static PythonValue DefaultGenerateNextValue(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var start = positional.Count > 1 ? positional[1] : PythonWholeNumberValue.Create(1);
        List<PythonValue> lastValues =
            positional.Count > 3 && positional[3] is PythonListValue list ? list.Elements : [];
        if (lastValues.Count == 0)
        {
            return start;
        }

        var numeric = true;
        var hasMaximum = false;
        var maximum = BigInteger.Zero;
        foreach (var candidate in lastValues)
        {
            if (!IsIntValue(candidate))
            {
                numeric = false;
                continue;
            }

            var value = AsBigInteger(candidate);
            if (!hasMaximum || value > maximum)
            {
                maximum = value;
                hasMaximum = true;
            }
        }

        if (!numeric)
        {
            throw lastValues.Count == 1
                ? Fault($"unable to increment {lastValues[0].ToRepresentationString()}", default)
                : Fault("unable to sort non-numeric values", default);
        }

        return PythonWholeNumberValue.Create(maximum + BigInteger.One);
    }

    private static PythonValue GenerateNextValue(
        PythonManagedTypeValue enumClass,
        EnumInfo info,
        string name,
        List<PythonValue> lastValues,
        PythonValue? generateNextValue,
        TextSpan span,
        PythonValue start
    )
    {
        _ = info;
        var generator = generateNextValue ?? LookupClassValue(enumClass, "_generate_next_value_");
        return Invoke(
            generator ?? DefaultGnvFunction,
            [
                Text(name),
                start,
                PythonWholeNumberValue.Create(lastValues.Count),
                new PythonListValue([.. lastValues]),
            ],
            span
        );
    }

    /// <summary>Mirrors `_proto_member.__set_name__`.</summary>
    private static PythonManagedObjectValue CreateMember(
        PythonManagedTypeValue enumClass,
        EnumInfo info,
        string name,
        PythonValue rawValue,
        bool isFlag,
        TextSpan span
    )
    {
        var args = rawValue is PythonTupleValue tuple ? tuple.Elements : [rawValue];
        if (info.MemberType is PythonBuiltinTypeValue { Name: "tuple" })
        {
            args = [new PythonTupleValue([.. args])];
        }

        PythonManagedObjectValue member;
        if (info.UseArgs && info.NewMember is not null)
        {
            var created = Invoke(info.NewMember, [enumClass, .. args], span);
            member =
                created as PythonManagedObjectValue
                ?? throw Fault("_value_ not set in __new__, unable to create it", span);
        }
        else
        {
            member = new PythonManagedObjectValue(enumClass);
        }

        if (!member.Attributes.TryGetValue("_value_", out var value))
        {
            if (IsObjectMemberType(info.MemberType))
            {
                value = rawValue;
            }
            else
            {
                value = CreateMemberValue(info, enumClass, name, args, rawValue, span);
            }

            member.Attributes["_value_"] = value;
        }

        member.Attributes["_name_"] = Text(name);
        var state = new MemberState { Name = name, Value = value };
        SetMemberState(member, state);
        member.Attributes["__objclass__"] = enumClass;
        state.SortOrder = info.MemberNames.Elements.Count;
        member.Attributes["_sort_order_"] = PythonWholeNumberValue.Create(state.SortOrder);

        if (info.UseArgs && info.NewMember is not null)
        {
            var initializer = LookupClassValue(enumClass, "__init__");
            if (initializer is not null && initializer is not PythonNoneValue)
            {
                Invoke(initializer, [member, .. args], span);
            }
        }

        if (isFlag && IsIntValue(value))
        {
            var mask = AsBigInteger(value);
            info.FlagMask |= mask;
            if (IsSingleBit(mask))
            {
                info.SinglesMask |= mask;
            }

            info.AllBits = (BigInteger.One << (int)info.FlagMask.GetBitLength()) - BigInteger.One;
            PublishFlagMasks(enumClass, info);
        }

        var canonical = ResolveAlias(info, value);
        if (canonical is not null)
        {
            member = canonical;
        }
        else if (!IsFlagClass(enumClass) || !IsIntValue(value) || IsSingleBit(AsBigInteger(value)))
        {
            info.MemberNames.Elements.Add(Text(name));
        }

        AddMemberAttribute(enumClass, info, name, member);
        MergeValueMap(info, value, member, name);
        return member;
    }

    private static PythonValue CreateMemberValue(
        EnumInfo info,
        PythonManagedTypeValue enumClass,
        string name,
        IReadOnlyList<PythonValue> args,
        PythonValue rawValue,
        TextSpan span
    )
    {
        try
        {
            return Invoke(info.MemberType, [.. args], span);
        }
        catch (PythonRuntimeException)
        {
            throw Fault("_value_ not set in __new__, unable to create it", span);
        }
        catch (InvalidOperationException)
        {
            throw Fault("_value_ not set in __new__, unable to create it", span);
        }
    }

    private static void PublishFlagMasks(PythonManagedTypeValue enumClass, EnumInfo info)
    {
        enumClass.Attributes["_flag_mask_"] = PythonWholeNumberValue.Create(info.FlagMask);
        enumClass.Attributes["_singles_mask_"] = PythonWholeNumberValue.Create(info.SinglesMask);
        enumClass.Attributes["_all_bits_"] = PythonWholeNumberValue.Create(info.AllBits);
    }

    private static PythonManagedObjectValue? ResolveAlias(EnumInfo info, PythonValue value)
    {
        if (TryLookupKey(info.ValueMap, value, out var known) && known is not PythonNoneValue)
        {
            return (PythonManagedObjectValue)known;
        }

        foreach (var item in info.UnhashableValuesMap.Items)
        {
            if (item.Value is not PythonListValue list)
            {
                continue;
            }

            foreach (var candidate in list.Elements)
            {
                if (ManagedObjectProtocols.AreEqual(candidate, value))
                {
                    return (PythonManagedObjectValue)LookupAliasMember(info, item.Key);
                }
            }
        }

        foreach (var item in info.MemberMap.Items)
        {
            if (
                item.Value is PythonManagedObjectValue candidate
                && StateOf(candidate) is { } state
                && ManagedObjectProtocols.AreEqual(state.Value, value)
                && ManagedObjectProtocols.IsHashable(value) == false
            )
            {
                return candidate;
            }
        }

        return null;
    }

    private static PythonValue LookupAliasMember(EnumInfo info, PythonValue name) =>
        TryLookupName(info.MemberMap, ((PythonTextValue)name).Value, out var member)
            ? member
            : PythonNoneValue.Instance;

    private static void MergeValueMap(
        EnumInfo info,
        PythonValue value,
        PythonManagedObjectValue member,
        string name
    )
    {
        if (!ManagedObjectProtocols.IsHashable(value))
        {
            info.UnhashableValues.Elements.Add(value);
            if (!TryLookupKey(info.UnhashableValuesMap, Text(name), out var list))
            {
                list = new PythonListValue([]);
                info.UnhashableValuesMap.AddItem(new PythonDictionaryItemValue(Text(name), list));
            }

            ((PythonListValue)list).Elements.Add(value);
            return;
        }

        if (!TryLookupKey(info.ValueMap, value, out _))
        {
            info.ValueMap.AddItem(new PythonDictionaryItemValue(value, member));
        }

        if (!info.HashableValues.Elements.Any(entry => ManagedObjectProtocols.AreEqual(entry, value)))
        {
            info.HashableValues.Elements.Add(value);
        }
    }

    private static void SetMemberAttribute(
        PythonManagedTypeValue enumClass,
        string name,
        PythonManagedObjectValue member
    ) => enumClass.Attributes[name] = member;

    /// <summary>Mirrors `EnumType._add_member_`, including the descriptor redirect.</summary>
    private static void AddMemberAttribute(
        PythonManagedTypeValue enumClass,
        EnumInfo info,
        string name,
        PythonManagedObjectValue member
    )
    {
        PythonValue? found = null;
        string? descriptorType = null;
        PythonManagedTypeValue? classType = null;
        var skip = true;
        foreach (var baseValue in enumClass.ResolutionOrder!)
        {
            if (skip)
            {
                skip = false;
                continue;
            }

            PythonValue? attribute = null;
            if (baseValue is PythonManagedTypeValue managed)
            {
                managed.Attributes.TryGetValue(name, out attribute);
            }

            if (attribute is null)
            {
                continue;
            }

            if (
                attribute is PythonManagedObjectValue { Type: var attrType }
                && ReferenceEquals(attrType, PropertyType)
            )
            {
                found = attribute;
                classType = baseValue as PythonManagedTypeValue;
                descriptorType = "enum";
                break;
            }

            if (IsDescriptor(attribute))
            {
                found ??= attribute;
                classType ??= baseValue as PythonManagedTypeValue;
                descriptorType ??= "desc";
                continue;
            }

            descriptorType = "attr";
            classType = baseValue as PythonManagedTypeValue;
        }

        if (found is not null)
        {
            var redirect = new PropertyState
            {
                Member = member,
                Name = name,
                ClsName = enumClass.Name,
                AttrType = descriptorType,
                ClsType = classType,
            };
            if (PropertyStateOf(found) is { } source)
            {
                redirect.Fget = source.Fget;
                redirect.Fset = source.Fset;
                redirect.Fdel = source.Fdel;
            }

            enumClass.Attributes[name] = MakeProperty(redirect);
        }
        else
        {
            enumClass.Attributes[name] = member;
        }

        if (!TryLookupName(info.MemberMap, name, out _))
        {
            info.MemberMap.AddItem(new PythonDictionaryItemValue(Text(name), member));
        }
    }

    private static void CompleteClass(
        PythonManagedTypeValue enumClass,
        EnumInfo info,
        PythonDictionaryValue classBody,
        TextSpan span
    )
    {
        _ = classBody;
        PublishInfo(enumClass, info);
        if (IsReprEnum(enumClass))
        {
            var memberType = info.MemberType;
            if (IsObjectMemberType(memberType))
            {
                throw Fault(
                    "ReprEnum subclasses must be mixed with a data type (i.e. int, str, float, etc.)",
                    span
                );
            }

            InstallMixedInFormatting(enumClass, memberType, span);
        }

        if (IsFlagClass(enumClass))
        {
            PublishFlagMasks(enumClass, info);
            if (LookupClassValue(enumClass, "_generate_next_value_") is null)
            {
                Install(
                    enumClass,
                    "_generate_next_value_",
                    Protocol("_generate_next_value_", FlagGenerateNextValue)
                );
            }
        }
    }

    private static PythonValue FlagGenerateNextValue(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var start = positional.Count > 1 ? positional[1] : PythonNoneValue.Instance;
        var count = (positional.Count > 2 ? positional[2] : PythonNoneValue.Instance) as
            PythonWholeNumberValue;
        var lastValues = positional.Count > 3 ? positional[3] as PythonListValue : null;
        if ((count?.Value ?? BigInteger.Zero).IsZero)
        {
            return start is PythonNoneValue ? PythonWholeNumberValue.Create(1) : start;
        }

        var highest = BigInteger.MinusOne;
        if (lastValues is not null)
        {
            foreach (var entry in lastValues.Elements)
            {
                if (entry is PythonWholeNumberValue whole && whole.Value > highest)
                {
                    highest = whole.Value;
                }
            }
        }

        if (highest < BigInteger.Zero)
        {
            throw Fault("invalid flag value", default);
        }

        return PythonWholeNumberValue.Create(BigInteger.One << (int)(highest.GetBitLength()));
    }

    private static void InstallMixedInFormatting(
        PythonManagedTypeValue enumClass,
        PythonValue memberType,
        TextSpan span
    )
    {
        _ = span;
        enumClass.Attributes["__format__"] = Protocol("__format__", DelegateToValueFormat);
        enumClass.Attributes["__str__"] = Protocol("__str__", DelegateToValueStr);
        _ = memberType;
    }

    private static PythonValue DelegateToValueFormat(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var specification = positional.Count > 0 ? positional[0] : Text("");
        var text = specification is PythonTextValue spec ? spec.Value : "";
        return Format(StateOf(member)!.Value, text);
    }

    private static PythonValue DelegateToValueStr(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    ) =>
        new PythonTextValue(
            StateOf((PythonManagedObjectValue)target!)!.Value.ToDisplayString()
        );

    // -------------------------------------------------------------------------
    // Metaclass call: value lookup and the functional API
    // -------------------------------------------------------------------------

    private static PythonValue EnumTypeCall(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var cls = (PythonManagedTypeValue)target!;
        if (positional.Count == 0)
        {
            throw Fault("EnumType.__call__() missing 1 required positional argument: 'value'", default);
        }

        PythonValue? module = null;
        PythonValue? qualname = null;
        PythonValue? dataType = null;
        PythonValue? start = null;
        PythonValue? names = positional.Count > 1 ? positional[1] : null;
        for (var index = 0; index < keywordNames.Count; index++)
        {
            var value = keywordValues[index];
            switch (keywordNames[index])
            {
                case "module":
                    module = value;
                    break;
                case "qualname":
                    qualname = value;
                    break;
                case "type":
                    dataType = value;
                    break;
                case "start":
                    start = value;
                    break;
                case "boundary":
                case "_simple":
                    break;
                default:
                    throw Fault(
                        $"EnumType.__call__() got an unexpected keyword argument '{keywordNames[index]}'",
                        default
                    );
            }
        }

        var info = EnsureInfo(cls);
        if (info.MemberMap.Items.Count != 0 || info.MemberNames.Elements.Count != 0)
        {
            var lookup = positional[0];
            if (names is not null)
            {
                lookup = new PythonTupleValue([positional[0], names, .. positional.Skip(2)]);
            }

            return LookupMember(cls, info, lookup, default);
        }

        if (names is null && dataType is null)
        {
            throw Fault(
                $"{DescribeClass(cls)} has no members; specify `names=()` if you meant to create a new, empty, enum",
                default
            );
        }

        if (positional[0] is not PythonTextValue className)
        {
            throw Fault(
                $"Invalid enum name {positional[0].ToRepresentationString()}",
                default
            );
        }

        return CreateFunctional(
            cls,
            className.Value,
            names,
            module as PythonTextValue,
            qualname as PythonTextValue,
            dataType,
            start,
            default
        );
    }

    /// <summary>Mirrors `Enum.__new__`'s value lookup.</summary>
    private static PythonValue LookupMember(
        PythonManagedTypeValue cls,
        EnumInfo info,
        PythonValue value,
        TextSpan span
    )
    {
        if (value is PythonManagedObjectValue instance && ReferenceEquals(instance.Type, cls))
        {
            return value;
        }

        if (TryLookupKey(info.ValueMap, value, out var found))
        {
            return found;
        }

        if (!ManagedObjectProtocols.IsHashable(value))
        {
            foreach (var item in info.UnhashableValuesMap.Items)
            {
                if (item.Value is PythonListValue list)
                {
                    foreach (var candidate in list.Elements)
                    {
                        if (ManagedObjectProtocols.AreEqual(candidate, value))
                        {
                            if (TryLookupName(info.MemberMap, ((PythonTextValue)item.Key).Value, out var byName))
                            {
                                return byName;
                            }
                        }
                    }
                }
            }

            foreach (var item in info.ValueMap.Items)
            {
                if (StateOf(item.Value) is { } state && ManagedObjectProtocols.AreEqual(state.Value, value))
                {
                    return item.Value;
                }
            }
        }

        if (info.MemberNames.Elements.Count == 0 && info.MemberMap.Items.Count == 0)
        {
            throw Fault($"{DescribeClass(cls)} has no members defined", span);
        }

        Exception? hookError = null;
        PythonValue? result = null;
        var missing = LookupMissingHook(cls, span);
        if (missing is not null and not PythonNoneValue)
        {
            try
            {
                // `cls._missing_(value)`: the attribute binds itself, so a plain
                // function sees the value as its first argument, as CPython does.
                result = Invoke(missing, [value], span);
            }
            catch (Exception error) when (error is PythonRuntimeException or PythonRaisedException)
            {
                hookError = error;
            }
        }

        if (result is PythonManagedObjectValue candidateMember && ReferenceEquals(candidateMember.Type, cls))
        {
            return candidateMember;
        }

        if (
            IsFlagClass(cls)
            && ReferenceEquals(GetBoundary(cls), BoundaryEject)
            && result is PythonWholeNumberValue or PythonTruthValue
        )
        {
            return result!;
        }

        if (hookError is not null)
        {
            throw hookError;
        }

        // A hook that ran and returned None leaves the value a plain miss, as in CPython;
        // anything else it returned was meant to be a member but is not one.
        if (result is null or PythonNoneValue)
        {
            throw Fault(
                $"{value.ToRepresentationString()} is not a valid {QualifiedName(cls)}",
                span,
                "ValueError"
            );
        }

        throw Fault(
            $"error in {cls.Name}._missing_: returned {result.ToRepresentationString()} instead of None or a valid member",
            span
        );
    }

    /// <summary>Mirrors `EnumType._create_`.</summary>
    private static PythonManagedTypeValue CreateFunctional(
        PythonManagedTypeValue baseClass,
        string className,
        PythonValue? names,
        PythonTextValue? module,
        PythonTextValue? qualname,
        PythonValue? dataType,
        PythonValue? start,
        TextSpan span
    )
    {
        var bases = new List<PythonValue> { baseClass };
        if (dataType is not null)
        {
            bases.Insert(0, dataType);
        }

        var prepared = (PythonDictionaryValue)PrepareNamespace(
            null,
            [Text(className), new PythonTupleValue([.. bases])]
        );
        var startValue = start ?? PythonWholeNumberValue.Create(1);
        var entries = new List<(string Name, PythonValue Value)>();
        var info = EnsureInfo(baseClass);
        var firstEnum = baseClass;
        var memberType =
            dataType
            ?? LookupClassValue(firstEnum, "_member_type_")
            ?? PythonBuiltinFunctions.Object;
        var generator = LookupClassValue(firstEnum, "_generate_next_value_");

        if (names is PythonTextValue text)
        {
            foreach (
                var name in text.Value
                    .Replace(',', ' ')
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            )
            {
                entries.Add((name, PythonNoneValue.Instance));
            }
        }
        else if (names is PythonTupleValue or PythonListValue)
        {
            IReadOnlyList<PythonValue> elements = names is PythonTupleValue tuple
                ? tuple.Elements
                : ((PythonListValue)names).Elements;
            foreach (var element in elements)
            {
                if (element is PythonTextValue elementName)
                {
                    entries.Add((elementName.Value, PythonNoneValue.Instance));
                    continue;
                }

                if (element is PythonTupleValue pair && pair.Elements.Length == 2)
                {
                    entries.Add(
                        (((PythonTextValue)pair.Elements[0]).Value, pair.Elements[1])
                    );
                    continue;
                }

                throw Fault(
                    $"Invalid enum member name or value: {element.ToRepresentationString()}",
                    span,
                    "ValueError"
                );
            }
        }
        else if (names is PythonDictionaryValue mapping)
        {
            foreach (var item in mapping.Items)
            {
                entries.Add((((PythonTextValue)item.Key).Value, item.Value));
            }
        }
        else if (names is not null)
        {
            foreach (var element in ManagedObjectProtocols.MaterializeValues(names, span, null))
            {
                if (element is PythonTextValue elementName)
                {
                    entries.Add((elementName.Value, PythonNoneValue.Instance));
                    continue;
                }

                if (element is PythonTupleValue pair && pair.Elements.Length == 2)
                {
                    entries.Add((((PythonTextValue)pair.Elements[0]).Value, pair.Elements[1]));
                    continue;
                }

                throw Fault(
                    $"Invalid enum member name or value: {element.ToRepresentationString()}",
                    span,
                    "ValueError"
                );
            }
        }

        var lastValues = new List<PythonValue>();
        var generated = new List<(string Name, PythonValue Value)>();
        foreach (var (name, value) in entries)
        {
            if (ReferenceEquals(value, PythonNoneValue.Instance))
            {
                var next = GenerateNextValue(
                    firstEnum,
                    info,
                    name,
                    lastValues,
                    generator,
                    span,
                    startValue
                );
                generated.Add((name, next));
                lastValues.Add(next);
                continue;
            }

            generated.Add((name, value));
            lastValues.Add(value);
        }

        foreach (var (name, value) in generated)
        {
            if (TryLookupName(prepared, name, out var existing))
            {
                throw Fault(
                    $"{Text(name).ToRepresentationString()} already defined as {existing.ToRepresentationString()}",
                    span,
                    "TypeError"
                );
            }

            prepared.AddItem(new PythonDictionaryItemValue(Text(name), value));
        }

        if (module is not null)
        {
            // `__prepare__` already seeds `__module__`; the functional API's explicit
            // module replaces it rather than adding a second item.
            ManagedObjectProtocols.SetDictionaryItem(prepared, Text("__module__"), module, span);
        }
        else
        {
            // Without one, `type.__new__` names the caller's module, but only when the
            // namespace does not already carry the key.
            ManagedObjectProtocols.DeleteItem(prepared, Text("__module__"), span);
        }

        if (qualname is not null)
        {
            ManagedObjectProtocols.SetDictionaryItem(prepared, Text("__qualname__"), qualname, span);
        }

        // A data type given to the functional API behaves like a mixed-in one: it is
        // not a declared base here, the way `int` is for a class statement.
        var createBases = new List<PythonValue>();
        foreach (var entry in bases)
        {
            if (entry is not PythonBuiltinTypeValue)
            {
                createBases.Add(entry);
            }
        }

        var created = (PythonManagedTypeValue)
            UserObjectProtocols.Dispatcher!.CreateType(
                EnumTypeType,
                [Text(className), new PythonTupleValue([.. createBases]), prepared],
                [],
                [],
                span
            );
        var createdInfo = EnsureInfo(created);
        createdInfo.MemberType = memberType;
        createdInfo.FirstEnum = firstEnum;
        createdInfo.Boundary = LookupClassValue(created, "_boundary_") ?? BoundaryStrict;
        FindNew(created, createdInfo, prepared);
        PopulateMembers(created, createdInfo, prepared, span);
        CompleteClass(created, createdInfo, prepared, span);
        return created;
    }

    private static PythonValue MetaclassMro(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        return PythonTypeMro.Compute(target!, default);
    }

    private static PythonValue MetaclassLen(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        return PythonWholeNumberValue.Create(MemberNamesOf((PythonManagedTypeValue)target!).Elements.Count);
    }

    private static PythonValue MetaclassIter(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        var cls = (PythonManagedTypeValue)target!;
        var members = new List<PythonValue>();
        foreach (var name in MemberNamesOf(cls).Elements)
        {
            if (TryLookupName(MemberMapOf(cls), ((PythonTextValue)name).Value, out var member))
            {
                members.Add(member);
            }
        }

        return new PythonIteratorValue(new PythonListValue(members), -1);
    }

    private static PythonValue MetaclassReversed(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        var cls = (PythonManagedTypeValue)target!;
        var members = new List<PythonValue>();
        foreach (var name in MemberNamesOf(cls).Elements)
        {
            if (TryLookupName(MemberMapOf(cls), ((PythonTextValue)name).Value, out var member))
            {
                members.Add(member);
            }
        }

        members.Reverse();
        return new PythonIteratorValue(new PythonListValue(members), -1);
    }

    private static PythonValue MetaclassGetItem(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var cls = (PythonManagedTypeValue)target!;
        var name = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (TryLookupKey(MemberMapOf(cls), name, out var member))
        {
            return member;
        }

        throw Fault(
            $"{name.ToRepresentationString()}",
            default,
            "KeyError"
        );
    }

    private static PythonValue MetaclassContains(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var cls = (PythonManagedTypeValue)target!;
        var item = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        return PythonTruthValue.FromBoolean(ContainsMember(cls, item, default));
    }

    private static bool ContainsMember(
        PythonManagedTypeValue cls,
        PythonValue item,
        TextSpan span
    )
    {
        if (item is PythonManagedObjectValue member && ReferenceEquals(member.Type, cls))
        {
            return true;
        }

        var info = EnsureInfo(cls);
        if (IsFlagClass(cls))
        {
            try
            {
                if (LookupMissingHook(cls, span) is { } missing && missing is not PythonNoneValue)
                {
                    var result = Invoke(missing, [item], span);
                    if (result is PythonManagedObjectValue created && ReferenceEquals(created.Type, cls))
                    {
                        return true;
                    }
                }
            }
            catch (Exception error)
                when (error is PythonRuntimeException or PythonRaisedException
                    && PythonNamespaceMapping.IsPythonException(error, "ValueError")
                )
            {
                // A boundary violation simply means the value is not a member.
            }
        }

        foreach (var hashable in info.HashableValues.Elements)
        {
            if (ManagedObjectProtocols.AreEqual(hashable, item))
            {
                return true;
            }
        }

        foreach (var unhashable in info.UnhashableValues.Elements)
        {
            if (ManagedObjectProtocols.AreEqual(unhashable, item))
            {
                return true;
            }
        }

        return false;
    }

    private static PythonValue MetaclassSetAttr(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var cls = (PythonManagedTypeValue)target!;
        var name = positional.Count > 0 && positional[0] is PythonTextValue text ? text.Value : "";
        var value = positional.Count > 1 ? positional[1] : PythonNoneValue.Instance;
        if (TryLookupName(MemberMapOf(cls), name, out _))
        {
            throw Fault($"cannot reassign member '{name}'", default, "AttributeError");
        }

        if (value is PythonNoneValue && name is "__hash__" or "__eq__")
        {
            cls.Attributes[name] = value;
            return PythonNoneValue.Instance;
        }

        cls.Attributes[name] = value;
        return PythonNoneValue.Instance;
    }

    private static PythonValue MetaclassDeleteAttr(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var cls = (PythonManagedTypeValue)target!;
        var name = positional.Count > 0 && positional[0] is PythonTextValue text ? text.Value : "";
        if (TryLookupName(MemberMapOf(cls), name, out _))
        {
            throw Fault($"'{cls.Name}' cannot delete member '{name}'.", default, "AttributeError");
        }

        if (!cls.Attributes.TryGetValue(name, out _))
        {
            throw Fault(
                $"type object '{cls.Name}' has no attribute '{name}'",
                default,
                "AttributeError"
            );
        }

        cls.Attributes.Remove(name);
        return PythonNoneValue.Instance;
    }

    // -------------------------------------------------------------------------
    // Enum class protocols
    // -------------------------------------------------------------------------

    private static void InstallClassProtocols(PythonManagedTypeValue enumClass)
    {
        Install(
            enumClass,
            "name",
            MakeProperty(
                new PropertyState
                {
                    Fget = Protocol("name", MemberName),
                    Name = "name",
                    ClsName = "Enum",
                }
            )
        );
        Install(
            enumClass,
            "value",
            MakeProperty(
                new PropertyState
                {
                    Fget = Protocol("value", MemberValue),
                    Name = "value",
                    ClsName = "Enum",
                }
            )
        );
        Install(enumClass, "__repr__", Protocol("__repr__", MemberRepr));
        Install(enumClass, "__str__", Protocol("__str__", MemberStr));
        Install(enumClass, "__hash__", Protocol("__hash__", MemberHash));
        Install(
            enumClass,
            "_missing_",
            new PythonClassMethodValue(Protocol("_missing_", MissingDefault))
        );
        Install(
            enumClass,
            "__new__",
            new PythonStaticMethodValue(Protocol("__new__", EnumNew))
        );
        Install(
            enumClass,
            "_find_new_",
            new PythonClassMethodValue(Protocol("_find_new_", FindNewHook))
        );
        Install(
            enumClass,
            "_add_member_",
            new PythonClassMethodValue(Protocol("_add_member_", AddMemberHook))
        );
        Install(
            enumClass,
            "_add_alias_",
            new PythonClassMethodValue(Protocol("_add_alias_", AddAliasHook))
        );
        Install(
            enumClass,
            "_check_for_existing_members_",
            new PythonClassMethodValue(
                Protocol("_check_for_existing_members_", CheckForExistingMembersHook)
            )
        );
        Install(
            enumClass,
            "_get_mixins_",
            new PythonClassMethodValue(Protocol("_get_mixins_", GetMixinsHook))
        );
    }

    private static PythonValue MemberName(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        // A property getter runs unbound, so the member is the first positional.
        var state = StateOf(target ?? positional[0]);
        return state?.Name is { } name ? Text(name) : PythonNoneValue.Instance;
    }

    private static PythonValue MemberValue(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        return StateOf(target ?? positional[0])?.Value ?? PythonNoneValue.Instance;
    }

    private static PythonValue MemberRepr(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        var member = (PythonManagedObjectValue)target!;
        var state = StateOf(member)!;
        if (state.Name is null)
        {
            return Text($"<{member.Type.Name}: {state.Value.ToRepresentationString()}>");
        }

        return Text($"<{member.Type.Name}.{state.Name}: {state.Value.ToRepresentationString()}>");
    }

    private static PythonValue MemberStr(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        var member = (PythonManagedObjectValue)target!;
        var state = StateOf(member)!;
        if (state.Name is null)
        {
            return Text($"{member.Type.Name}({state.Value.ToRepresentationString()})");
        }

        return Text($"{member.Type.Name}.{state.Name}");
    }

    /// <summary>`hash(member)` is `hash(member._name_)`, as in CPython.</summary>
    private static PythonValue MemberHash(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        var state = StateOf(target!);
        return PythonWholeNumberValue.Create(
            ManagedObjectProtocols.ComputePythonHash(Text(state?.Name ?? string.Empty))
        );
    }

    private static PythonValue MissingDefault(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = target;
        _ = positional;
        return PythonNoneValue.Instance;
    }

    private static PythonValue EnumNew(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        throw Fault(
            "do not use `super().__new__; call the appropriate __new__ directly",
            default
        );
    }

    private static PythonValue FindNewHook(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var cls = (PythonManagedTypeValue)(positional.Count > 0 ? positional[0] : target!)!;
        var info = EnsureInfo(cls);
        var property = new PythonPropertyValue(
            Protocol("__new__", (_, _) => info.NewMember ?? PythonBuiltinFunctions.Object),
            null,
            null
        );
        return new PythonTupleValue([info.UseArgs ? PythonTruthValue.True : PythonTruthValue.False]);
    }

    private static PythonValue AddMemberHook(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var cls = (PythonManagedTypeValue)(positional.Count > 1 ? positional[0] : target!)!;
        var name = positional.Count > 1 ? positional[1] : positional[0];
        var member = positional.Count > 1 ? positional[2] : positional[1];
        var info = EnsureInfo(cls);
        AddMemberAttribute(cls, info, ((PythonTextValue)name).Value, (PythonManagedObjectValue)member);
        return PythonNoneValue.Instance;
    }

    private static PythonValue AddAliasHook(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var member = (positional.Count > 1 ? positional[0] : target!) as PythonManagedObjectValue;
        var name = ((PythonTextValue)(positional.Count > 1 ? positional[1] : positional[0])).Value;
        if (member is null)
        {
            return PythonNoneValue.Instance;
        }

        var info = EnsureInfo(member.Type);
        AddMemberAttribute(member.Type, info, name, member);
        return PythonNoneValue.Instance;
    }

    private static PythonValue CheckForExistingMembersHook(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = target;
        _ = positional;
        return PythonNoneValue.Instance;
    }

    private static PythonValue GetMixinsHook(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        var cls = (PythonManagedTypeValue)target!;
        var info = EnsureInfo(cls);
        return new PythonTupleValue([info.MemberType, info.FirstEnum ?? EnumClass]);
    }

    // -------------------------------------------------------------------------
    // Flag protocols
    // -------------------------------------------------------------------------

    private static void InstallFlagProtocols()
    {
        // `IntFlag` shadows `Flag` in its own MRO, so the flag behaviour is installed on
        // both: CPython's IntFlag simply inherits it.
        foreach (var flagClass in new[] { FlagClass, IntFlagClass })
        {
            Install(
                flagClass,
                "_missing_",
                new PythonClassMethodValue(Protocol("_missing_", FlagMissing))
            );
            Install(
                flagClass,
                "_generate_next_value_",
                new PythonStaticMethodValue(Protocol("_generate_next_value_", FlagGenerateNextValue))
            );
            Install(
                flagClass,
                "_iter_member_",
                new PythonClassMethodValue(Protocol("_iter_member_", FlagIterMember))
            );
            Install(flagClass, "__iter__", Protocol("__iter__", FlagIterate));
            Install(flagClass, "__len__", Protocol("__len__", FlagLength));
            Install(flagClass, "__bool__", Protocol("__bool__", FlagBool));
            Install(flagClass, "__contains__", Protocol("__contains__", FlagContains));
            Install(flagClass, "__or__", Protocol("__or__", FlagOr));
            Install(flagClass, "__ror__", Protocol("__or__", FlagOr));
            Install(flagClass, "__and__", Protocol("__and__", FlagAnd));
            Install(flagClass, "__rand__", Protocol("__and__", FlagAnd));
            Install(flagClass, "__xor__", Protocol("__xor__", FlagXor));
            Install(flagClass, "__rxor__", Protocol("__xor__", FlagXor));
            Install(flagClass, "__invert__", Protocol("__invert__", FlagInvert));
        }
    }

    private static PythonValue FlagMissing(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var cls = (PythonManagedTypeValue)(positional.Count > 1 ? positional[0] : target!)!;
        var value = positional.Count > 1 ? positional[1] : positional[0];
        var info = EnsureInfo(cls);
        if (!IsIntValue(value))
        {
            throw Fault(
                $"{value.ToRepresentationString()} is not a valid {QualifiedName(cls)}",
                default,
                "ValueError"
            );
        }

        var raw = AsBigInteger(value);
        var flagMask = info.FlagMask;
        var singlesMask = info.SinglesMask;
        var allBits = info.AllBits;
        BigInteger? negative = null;
        if (!(~allBits <= raw && raw <= allBits) || (raw & (allBits ^ flagMask)) != 0)
        {
            var boundary = GetBoundary(cls);
            if (ReferenceEquals(boundary, BoundaryStrict))
            {
                var maxBits = Math.Max(raw.GetBitLength(), flagMask.GetBitLength());
                throw Fault(
                    $"{DescribeClass(cls)} invalid value {raw}\n    given {Binary(raw, maxBits)}\n  allowed {Binary(flagMask, maxBits)}",
                    default,
                    "ValueError"
                );
            }

            if (ReferenceEquals(boundary, BoundaryConform))
            {
                raw &= flagMask;
            }
            else if (ReferenceEquals(boundary, BoundaryEject))
            {
                return value;
            }
            else if (ReferenceEquals(boundary, BoundaryKeep))
            {
                if (raw < 0)
                {
                    raw = BigInteger.Max(allBits + 1, BigInteger.One << (int)raw.GetBitLength()) + raw;
                }
            }
            else
            {
                throw Fault(
                    $"{DescribeClass(cls)} unknown flag boundary {boundary.ToRepresentationString()}",
                    default,
                    "ValueError"
                );
            }
        }

        if (raw < 0)
        {
            negative = raw;
            raw = allBits + 1 + raw;
        }

        var unknown = raw & ~flagMask;
        var aliases = raw & ~singlesMask;
        var memberValue = raw & singlesMask;
        if (unknown != 0 && !ReferenceEquals(GetBoundary(cls), BoundaryKeep))
        {
            throw Fault(
                $"{cls.Name}({raw}) -->  unknown values {unknown} [{Binary(unknown, 0)}]",
                default,
                "ValueError"
            );
        }

        var pseudo = new PythonManagedObjectValue(cls);
        SetMemberState(
            pseudo,
            new MemberState { Name = null, Value = PythonWholeNumberValue.Create(raw) }
        );
        pseudo.Attributes["_value_"] = PythonWholeNumberValue.Create(raw);
        pseudo.Attributes["_name_"] = PythonNoneValue.Instance;
        pseudo.Attributes["__objclass__"] = cls;
        var members = new List<PythonManagedObjectValue>();
        var combined = BigInteger.Zero;
        foreach (var member in IterateFlagMembers(cls, info, memberValue))
        {
            members.Add(member);
            combined |= AsBigInteger(StateOf(member)!.Value);
        }

        if (memberValue != 0 || aliases != 0)
        {
            var combinedValue = combined;
            if (aliases != 0)
            {
                var target2 = memberValue | aliases;
                foreach (var item in info.MemberMap.Items)
                {
                    if (item.Value is not PythonManagedObjectValue candidate || members.Contains(candidate))
                    {
                        continue;
                    }

                    var candidateValue = AsBigInteger(StateOf(candidate)!.Value);
                    if (candidateValue != 0 && (candidateValue & target2) == candidateValue)
                    {
                        members.Add(candidate);
                        combinedValue |= candidateValue;
                    }
                }
            }

            var remainder = (memberValue | aliases) ^ combinedValue;
            var name = string.Join("|", members.Select(entry => StateOf(entry)!.Name));
            if (combinedValue.IsZero)
            {
                name = null!;
            }
            else if (remainder != 0 && ReferenceEquals(GetBoundary(cls), BoundaryStrict))
            {
                throw Fault($"{DescribeClass(cls)}: no members with value {remainder}", default, "ValueError");
            }
            else if (remainder != 0)
            {
                name = $"{name}|{remainder}";
            }

            SetMemberState(pseudo, name, raw);
        }
        else
        {
            SetMemberState(pseudo, null, raw);
        }

        if (TryLookupKey(info.ValueMap, PythonWholeNumberValue.Create(raw), out var existing))
        {
            return existing;
        }

        info.ValueMap.AddItem(
            new PythonDictionaryItemValue(PythonWholeNumberValue.Create(raw), pseudo)
        );
        if (negative is not null)
        {
            var negativeValue = PythonWholeNumberValue.Create(negative.Value);
            if (!TryLookupKey(info.ValueMap, negativeValue, out _))
            {
                info.ValueMap.AddItem(new PythonDictionaryItemValue(negativeValue, pseudo));
            }
        }

        if (!info.HashableValues.Elements.Any(entry => AsBigInteger(entry) == raw))
        {
            info.HashableValues.Elements.Add(PythonWholeNumberValue.Create(raw));
        }

        return pseudo;
    }

    private static void SetMemberState(PythonManagedObjectValue member, string? name, BigInteger value)
    {
        MemberStates.Remove(member);
        MemberStates.Add(
            member,
            new MemberState { Name = name, Value = PythonWholeNumberValue.Create(value) }
        );
        member.Attributes["_name_"] = name is null ? PythonNoneValue.Instance : Text(name);
        member.Attributes["_value_"] = PythonWholeNumberValue.Create(value);
    }

    private static string Binary(BigInteger value, long maxBits)
    {
        var text = value >= 0
            ? "0b0" + ToBinaryDigits(value)
            : "0b1" + ToBinaryDigits(~value ^ (BigInteger.One << (int)value.GetBitLength()) - 1);
        var sign = text[..3];
        var digits = text[3..];
        if (maxBits > digits.Length)
        {
            digits = new string(sign[^1], (int)maxBits - digits.Length) + digits;
        }

        return $"{sign} {digits}";
    }

    private static string ToBinaryDigits(BigInteger value)
    {
        if (value.IsZero)
        {
            return "0";
        }

        var digits = new System.Text.StringBuilder();
        while (value > 0)
        {
            digits.Insert(0, (value & 1).IsZero ? '0' : '1');
            value >>= 1;
        }

        return digits.ToString();
    }

    private static IEnumerable<PythonManagedObjectValue> IterateFlagMembers(
        PythonManagedTypeValue cls,
        EnumInfo info,
        BigInteger value
    )
    {
        _ = cls;
        var result = new List<PythonManagedObjectValue>();
        var bit = BigInteger.One;
        while (bit <= value)
        {
            if ((value & bit) != 0)
            {
                if (TryLookupKey(info.ValueMap, PythonWholeNumberValue.Create(bit), out var member) && member is PythonManagedObjectValue found)
                {
                    result.Add(found);
                }
            }

            bit <<= 1;
        }

        foreach (var entry in result.OrderBy(entry => StateOf(entry)!.SortOrder))
        {
            yield return entry;
        }
    }

    private static PythonValue FlagIterMember(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var cls = (PythonManagedTypeValue)(positional.Count > 1 ? positional[0] : target!)!;
        var value = positional.Count > 1 ? positional[1] : positional[0];
        var info = EnsureInfo(cls);
        var members = new List<PythonValue>();
        foreach (var member in IterateFlagMembers(cls, info, AsBigInteger(value)))
        {
            members.Add(member);
        }

        return new PythonListValue(members);
    }

    private static PythonValue FlagIterate(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        var member = (PythonManagedObjectValue)target!;
        var info = EnsureInfo(member.Type);
        var members = new List<PythonValue>();
        foreach (var entry in IterateFlagMembers(member.Type, info, AsBigInteger(StateOf(member)!.Value)))
        {
            members.Add(entry);
        }

        return new PythonIteratorValue(new PythonListValue(members), -1);
    }

    private static PythonValue FlagLength(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        var value = AsBigInteger(StateOf(target!)!.Value);
        var count = 0;
        while (value > 0)
        {
            count += (int)(value & 1);
            value >>= 1;
        }

        return PythonWholeNumberValue.Create(count);
    }

    private static PythonValue FlagBool(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        return PythonTruthValue.FromBoolean(!AsBigInteger(StateOf(target!)!.Value).IsZero);
    }

    private static PythonValue FlagContains(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (other is not PythonManagedObjectValue candidate || !ReferenceEquals(candidate.Type, member.Type))
        {
            throw Fault(
                $"unsupported operand type(s) for 'in': '{PythonBuiltinTypes.GetRuntimeTypeName(other)}' and '{member.Type.Name}'",
                default
            );
        }

        var mine = AsBigInteger(StateOf(member)!.Value);
        var theirs = AsBigInteger(StateOf(candidate)!.Value);
        return PythonTruthValue.FromBoolean((theirs & mine) == theirs);
    }

    private static PythonValue FlagOr(PythonValue? target, IReadOnlyList<PythonValue> positional)
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (FlagOperandValue(member.Type, other) is not { } otherValue)
        {
            return PythonNotImplementedValue.Instance;
        }

        return LookupMemberOrFault(
            member.Type,
            PythonWholeNumberValue.Create(AsBigInteger(StateOf(member)!.Value) | otherValue)
        );
    }

    private static PythonValue FlagAnd(PythonValue? target, IReadOnlyList<PythonValue> positional)
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (FlagOperandValue(member.Type, other) is not { } otherValue)
        {
            return PythonNotImplementedValue.Instance;
        }

        return LookupMemberOrFault(
            member.Type,
            PythonWholeNumberValue.Create(AsBigInteger(StateOf(member)!.Value) & otherValue)
        );
    }

    private static PythonValue FlagXor(PythonValue? target, IReadOnlyList<PythonValue> positional)
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (FlagOperandValue(member.Type, other) is not { } otherValue)
        {
            return PythonNotImplementedValue.Instance;
        }

        return LookupMemberOrFault(
            member.Type,
            PythonWholeNumberValue.Create(AsBigInteger(StateOf(member)!.Value) ^ otherValue)
        );
    }

    private static PythonValue FlagInvert(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        var member = (PythonManagedObjectValue)target!;
        var info = EnsureInfo(member.Type);
        var value = AsBigInteger(StateOf(member)!.Value);
        var boundary = GetBoundary(member.Type);
        var inverted =
            ReferenceEquals(boundary, BoundaryEject) || ReferenceEquals(boundary, BoundaryKeep)
                ? info.AllBits ^ value
                : info.SinglesMask & ~value;
        return LookupMemberOrFault(member.Type, PythonWholeNumberValue.Create(inverted));
    }

    /// <summary>Mirrors `Flag._get_value`: null means the operand is not usable.</summary>
    private static BigInteger? FlagOperandValue(PythonManagedTypeValue cls, PythonValue other)
    {
        if (other is PythonManagedObjectValue candidate && ReferenceEquals(candidate.Type, cls))
        {
            return AsBigInteger(StateOf(candidate)!.Value);
        }

        var info = EnsureInfo(cls);
        if (!IsObjectMemberType(info.MemberType) && IsIntValue(other))
        {
            return AsBigInteger(other);
        }

        return null;
    }

    /// <summary>
    /// The operator entry point of a value lookup: `Flag` combinations and `invert`
    /// go through the same `_missing_` path a class call does.
    /// </summary>
    private static PythonValue LookupMemberOrFault(PythonManagedTypeValue cls, PythonValue value) =>
        LookupMember(cls, EnsureInfo(cls), value, default);

    /// <summary>
    /// `cls._missing_` as an attribute of the enum class, so a classmethod binds the
    /// class, a static method stays bare and a plain function stays unbound.
    /// </summary>
    private static PythonValue? LookupMissingHook(PythonManagedTypeValue cls, TextSpan span)
    {
        try
        {
            return ManagedObjectProtocols.GetAttribute(cls, "_missing_", span);
        }
        catch (Exception error)
            when (error is PythonRuntimeException or PythonRaisedException
                && PythonNamespaceMapping.IsPythonException(error, "AttributeError")
            )
        {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Mixin protocols: IntEnum and StrEnum delegate to their data type
    // -------------------------------------------------------------------------

    private static void InstallMixinProtocols()
    {
        foreach (var enumClass in new[] { IntEnumClass, IntFlagClass })
        {
            Install(enumClass, "__int__", Protocol("__int__", MixinInt));
            Install(enumClass, "__index__", Protocol("__index__", MixinInt));
            Install(enumClass, "__float__", Protocol("__float__", MixinFloat));
            Install(enumClass, "__eq__", Protocol("__eq__", MixinEqual));
            Install(enumClass, "__ne__", Protocol("__ne__", MixinNotEqual));
            Install(enumClass, "__hash__", Protocol("__hash__", MixinHash));
            // Members are not real ints here, so the int operators the mixin would
            // inherit are provided explicitly, in both operand orders.
            Install(enumClass, "__add__", Protocol("__add__", MixinAdd));
            Install(enumClass, "__radd__", Protocol("__radd__", MixinAdd));
            Install(enumClass, "__mul__", Protocol("__mul__", MixinMultiply));
            Install(enumClass, "__rmul__", Protocol("__rmul__", MixinMultiply));
            if (ReferenceEquals(enumClass, IntEnumClass))
            {
                // `IntFlag` keeps the flag operators, which return members.
                Install(enumClass, "__and__", Protocol("__and__", MixinBitAnd));
                Install(enumClass, "__rand__", Protocol("__rand__", MixinBitAnd));
                Install(enumClass, "__or__", Protocol("__or__", MixinBitOr));
                Install(enumClass, "__ror__", Protocol("__ror__", MixinBitOr));
                Install(enumClass, "__xor__", Protocol("__xor__", MixinBitXor));
                Install(enumClass, "__rxor__", Protocol("__rxor__", MixinBitXor));
            }
            Install(
                enumClass,
                "__sub__",
                Protocol("__sub__", (target, positional) => MixinSubtract(target, positional, false))
            );
            Install(
                enumClass,
                "__rsub__",
                Protocol("__rsub__", (target, positional) => MixinSubtract(target, positional, true))
            );
            Install(
                enumClass,
                "__floordiv__",
                Protocol(
                    "__floordiv__",
                    (target, positional) => MixinFloorDivide(target, positional, false)
                )
            );
            Install(
                enumClass,
                "__rfloordiv__",
                Protocol(
                    "__rfloordiv__",
                    (target, positional) => MixinFloorDivide(target, positional, true)
                )
            );
            Install(
                enumClass,
                "__mod__",
                Protocol("__mod__", (target, positional) => MixinModulo(target, positional, false))
            );
            Install(
                enumClass,
                "__rmod__",
                Protocol("__rmod__", (target, positional) => MixinModulo(target, positional, true))
            );
            Install(
                enumClass,
                "__truediv__",
                Protocol(
                    "__truediv__",
                    (target, positional) => MixinTrueDivide(target, positional, false)
                )
            );
            Install(
                enumClass,
                "__rtruediv__",
                Protocol(
                    "__rtruediv__",
                    (target, positional) => MixinTrueDivide(target, positional, true)
                )
            );
            Install(
                enumClass,
                "__pow__",
                Protocol("__pow__", (target, positional) => MixinPower(target, positional, false))
            );
            Install(
                enumClass,
                "__rpow__",
                Protocol("__rpow__", (target, positional) => MixinPower(target, positional, true))
            );
            Install(
                enumClass,
                "__lshift__",
                Protocol("__lshift__", (target, positional) => MixinShift(target, positional, false, true))
            );
            Install(
                enumClass,
                "__rlshift__",
                Protocol("__rlshift__", (target, positional) => MixinShift(target, positional, true, true))
            );
            Install(
                enumClass,
                "__rshift__",
                Protocol("__rshift__", (target, positional) => MixinShift(target, positional, false, false))
            );
            Install(
                enumClass,
                "__rrshift__",
                Protocol("__rrshift__", (target, positional) => MixinShift(target, positional, true, false))
            );
            Install(enumClass, "__neg__", Protocol("__neg__", MixinNegate));
            Install(enumClass, "__pos__", Protocol("__pos__", MixinPositive));
            Install(enumClass, "__abs__", Protocol("__abs__", MixinAbsolute));
            if (ReferenceEquals(enumClass, IntEnumClass))
            {
                // `IntFlag` keeps `~`, which returns a flag.
                Install(enumClass, "__invert__", Protocol("__invert__", MixinInvert));
            }
            Install(
                enumClass,
                "__lt__",
                Protocol("__lt__", (target, positional) => MixinCompare(target, positional, "<"))
            );
            Install(
                enumClass,
                "__le__",
                Protocol("__le__", (target, positional) => MixinCompare(target, positional, "<="))
            );
            Install(
                enumClass,
                "__gt__",
                Protocol("__gt__", (target, positional) => MixinCompare(target, positional, ">"))
            );
            Install(
                enumClass,
                "__ge__",
                Protocol("__ge__", (target, positional) => MixinCompare(target, positional, ">="))
            );
        }

        Install(StrEnumClass, "__getattr__", Protocol("__getattr__", StrEnumGetAttribute));
        Install(StrEnumClass, "__str__", Protocol("__str__", DelegateToValueStr));
        Install(StrEnumClass, "__format__", Protocol("__format__", DelegateToValueFormat));
        Install(StrEnumClass, "__eq__", Protocol("__eq__", StrMixinEqual));
        Install(StrEnumClass, "__ne__", Protocol("__ne__", StrMixinNotEqual));
        Install(StrEnumClass, "__hash__", Protocol("__hash__", StrMixinHash));
        Install(StrEnumClass, "__len__", Protocol("__len__", StrMixinLength));
        Install(StrEnumClass, "__iter__", Protocol("__iter__", StrMixinIterate));
        Install(
            StrEnumClass,
            "__lt__",
            Protocol("__lt__", (target, positional) => StrMixinCompare(target, positional, "<"))
        );
        Install(
            StrEnumClass,
            "__le__",
            Protocol("__le__", (target, positional) => StrMixinCompare(target, positional, "<="))
        );
        Install(
            StrEnumClass,
            "__gt__",
            Protocol("__gt__", (target, positional) => StrMixinCompare(target, positional, ">"))
        );
        Install(
            StrEnumClass,
            "__ge__",
            Protocol("__ge__", (target, positional) => StrMixinCompare(target, positional, ">="))
        );
        Install(StrEnumClass, "__contains__", Protocol("__contains__", StrMixinContains));
        Install(
            StrEnumClass,
            "__add__",
            Protocol("__add__", (target, positional) => StrMixinConcat(target, positional, false))
        );
        Install(
            StrEnumClass,
            "__radd__",
            Protocol("__radd__", (target, positional) => StrMixinConcat(target, positional, true))
        );
        Install(
            StrEnumClass,
            "_generate_next_value_",
            new PythonStaticMethodValue(Protocol("_generate_next_value_", StrEnumGenerateNextValue))
        );
    }

    /// <summary>`StrEnum._generate_next_value_` is the lower-cased member name.</summary>
    private static PythonValue StrEnumGenerateNextValue(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = target;
        var name = positional.Count > 0 && positional[0] is PythonTextValue text ? text.Value : "";
        return Text(PythonUnicodeCase.ToLower(name));
    }

    private static PythonValue MixinInt(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        return PythonWholeNumberValue.Create(AsBigInteger(StateOf(target!)!.Value));
    }

    private static PythonValue MixinFloat(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        var value = StateOf(target!)!.Value;
        return value is PythonFloatingPointValue floating
            ? floating
            : new PythonFloatingPointValue((double)AsBigInteger(value));
    }

    private static PythonValue MixinEqual(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        var value = StateOf(member)!.Value;
        if (other is PythonManagedObjectValue candidate && ReferenceEquals(candidate.Type, member.Type))
        {
            return PythonTruthValue.FromBoolean(
                AsBigInteger(value) == AsBigInteger(StateOf(candidate)!.Value)
            );
        }

        return PythonTruthValue.FromBoolean(
            IsIntValue(other) && AsBigInteger(value) == AsBigInteger(other)
        );
    }

    private static PythonValue MixinNotEqual(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    ) =>
        PythonTruthValue.FromBoolean(
            ((PythonTruthValue)MixinEqual(target, positional)).Value == false
        );

    /// <summary>`hash(IntEnum.A)` is `hash(1)`: the mixin hashes like its data type.</summary>
    private static PythonValue MixinHash(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        return PythonWholeNumberValue.Create(
            ManagedObjectProtocols.ComputePythonHash(
                PythonWholeNumberValue.Create(AsBigInteger(StateOf(target!)!.Value))
            )
        );
    }

    /// <summary>A mixed-in int operator; the value is an ordinary int, not a member.</summary>
    private static BigInteger? MixinOperand(PythonManagedObjectValue member, PythonValue other) =>
        IsIntValue(other) ? AsBigInteger(other) : null;

    private static PythonValue MixinAdd(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (MixinOperand(member, other) is not { } right)
        {
            return PythonNotImplementedValue.Instance;
        }

        return PythonWholeNumberValue.Create(AsBigInteger(StateOf(member)!.Value) + right);
    }

    private static PythonValue MixinMultiply(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (MixinOperand(member, other) is not { } right)
        {
            return PythonNotImplementedValue.Instance;
        }

        return PythonWholeNumberValue.Create(AsBigInteger(StateOf(member)!.Value) * right);
    }

    private static PythonValue MixinBitAnd(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (MixinOperand(member, other) is not { } right)
        {
            return PythonNotImplementedValue.Instance;
        }

        return PythonWholeNumberValue.Create(AsBigInteger(StateOf(member)!.Value) & right);
    }

    private static PythonValue MixinBitOr(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (MixinOperand(member, other) is not { } right)
        {
            return PythonNotImplementedValue.Instance;
        }

        return PythonWholeNumberValue.Create(AsBigInteger(StateOf(member)!.Value) | right);
    }

    private static PythonValue MixinBitXor(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (MixinOperand(member, other) is not { } right)
        {
            return PythonNotImplementedValue.Instance;
        }

        return PythonWholeNumberValue.Create(AsBigInteger(StateOf(member)!.Value) ^ right);
    }

    private static PythonValue MixinSubtract(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional,
        bool reflected
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (MixinOperand(member, other) is not { } right)
        {
            return PythonNotImplementedValue.Instance;
        }

        var value = AsBigInteger(StateOf(member)!.Value);
        return PythonWholeNumberValue.Create(reflected ? right - value : value - right);
    }

    private static PythonValue MixinFloorDivide(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional,
        bool reflected
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (MixinOperand(member, other) is not { } right)
        {
            return PythonNotImplementedValue.Instance;
        }

        var value = AsBigInteger(StateOf(member)!.Value);
        return PythonWholeNumberValue.Create(
            FloorDivide(reflected ? right : value, reflected ? value : right)
        );
    }

    private static PythonValue MixinModulo(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional,
        bool reflected
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (MixinOperand(member, other) is not { } right)
        {
            return PythonNotImplementedValue.Instance;
        }

        var value = AsBigInteger(StateOf(member)!.Value);
        var left = reflected ? right : value;
        var divisor = reflected ? value : right;
        return PythonWholeNumberValue.Create(left - (FloorDivide(left, divisor) * divisor));
    }

    private static PythonValue MixinTrueDivide(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional,
        bool reflected
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (MixinOperand(member, other) is not { } right)
        {
            return PythonNotImplementedValue.Instance;
        }

        var value = AsBigInteger(StateOf(member)!.Value);
        var numerator = reflected ? right : value;
        var denominator = reflected ? value : right;
        if (denominator.IsZero)
        {
            throw Fault("division by zero", default, "ZeroDivisionError");
        }

        return new PythonFloatingPointValue((double)numerator / (double)denominator);
    }

    private static PythonValue MixinPower(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional,
        bool reflected
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (MixinOperand(member, other) is not { } right)
        {
            return PythonNotImplementedValue.Instance;
        }

        var value = AsBigInteger(StateOf(member)!.Value);
        var baseValue = reflected ? right : value;
        var exponent = reflected ? value : right;
        if (exponent.Sign < 0)
        {
            if (baseValue.IsZero)
            {
                throw Fault("0.0 cannot be raised to a negative power", default, "ZeroDivisionError");
            }

            return new PythonFloatingPointValue(Math.Pow((double)baseValue, (double)exponent));
        }

        if (exponent > int.MaxValue)
        {
            throw Fault("exponent too large", default, "OverflowError");
        }

        return PythonWholeNumberValue.Create(BigInteger.Pow(baseValue, (int)exponent));
    }

    private static PythonValue MixinShift(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional,
        bool reflected,
        bool left
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (MixinOperand(member, other) is not { } right)
        {
            return PythonNotImplementedValue.Instance;
        }

        var value = AsBigInteger(StateOf(member)!.Value);
        var baseValue = reflected ? right : value;
        var amount = reflected ? value : right;
        if (amount.Sign < 0)
        {
            throw Fault("negative shift count", default, "ValueError");
        }

        if (amount > int.MaxValue)
        {
            throw Fault("shift count too large", default, "OverflowError");
        }

        return PythonWholeNumberValue.Create(
            left ? baseValue << (int)amount : baseValue >> (int)amount
        );
    }

    private static PythonValue MixinNegate(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        return PythonWholeNumberValue.Create(-AsBigInteger(StateOf(target!)!.Value));
    }

    private static PythonValue MixinPositive(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        return PythonWholeNumberValue.Create(AsBigInteger(StateOf(target!)!.Value));
    }

    private static PythonValue MixinAbsolute(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        return PythonWholeNumberValue.Create(BigInteger.Abs(AsBigInteger(StateOf(target!)!.Value)));
    }

    private static PythonValue MixinInvert(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        return PythonWholeNumberValue.Create(~AsBigInteger(StateOf(target!)!.Value));
    }

    /// <summary>`//` rounds towards negative infinity, unlike BigInteger division.</summary>
    private static BigInteger FloorDivide(BigInteger left, BigInteger right)
    {
        if (right.IsZero)
        {
            throw Fault("integer division or modulo by zero", default, "ZeroDivisionError");
        }

        var quotient = BigInteger.DivRem(left, right, out var remainder);
        if (!remainder.IsZero && remainder.Sign != right.Sign)
        {
            quotient -= BigInteger.One;
        }

        return quotient;
    }

    private static PythonValue MixinCompare(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional,
        string comparison
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (MixinOperand(member, other) is not { } right)
        {
            return PythonNotImplementedValue.Instance;
        }

        var left = AsBigInteger(StateOf(member)!.Value);
        return PythonTruthValue.FromBoolean(
            comparison switch
            {
                "<" => left < right,
                "<=" => left <= right,
                ">" => left > right,
                _ => left >= right,
            }
        );
    }

    /// <summary>A `StrEnum` member compares as the string it wraps.</summary>
    private static PythonValue StrMixinEqual(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (other is PythonManagedObjectValue candidate && ReferenceEquals(candidate.Type, member.Type))
        {
            other = StateOf(candidate)!.Value;
        }

        return PythonTruthValue.FromBoolean(
            other is PythonTextValue text && text.Value == StateOf(member)!.Value.ToDisplayString()
        );
    }

    private static PythonValue StrMixinNotEqual(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    ) =>
        PythonTruthValue.FromBoolean(
            ((PythonTruthValue)StrMixinEqual(target, positional)).Value == false
        );

    /// <summary>`hash(StrE.A)` is `hash('a')`: the mixin hashes like its data type.</summary>
    private static PythonValue StrMixinHash(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        return PythonWholeNumberValue.Create(
            ManagedObjectProtocols.ComputePythonHash(StateOf(target!)!.Value)
        );
    }

    private static PythonValue StrMixinLength(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        return PythonWholeNumberValue.Create(
            ManagedObjectProtocols.GetLength(StateOf(target!)!.Value)
        );
    }

    /// <summary>A `StrEnum` member orders as the string it wraps.</summary>
    private static PythonValue StrMixinCompare(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional,
        string comparison
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (other is PythonManagedObjectValue candidate && ReferenceEquals(candidate.Type, member.Type))
        {
            other = StateOf(candidate)!.Value;
        }

        if (other is not PythonTextValue text)
        {
            return PythonNotImplementedValue.Instance;
        }

        var order = ManagedObjectProtocols.CompareOrdered(
            StateOf(member)!.Value,
            text,
            default
        );
        return PythonTruthValue.FromBoolean(
            comparison switch
            {
                "<" => order < 0,
                "<=" => order <= 0,
                ">" => order > 0,
                _ => order >= 0,
            }
        );
    }

    private static PythonValue StrMixinIterate(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        return ManagedObjectProtocols.GetIterator(StateOf(target!)!.Value);
    }

    private static PythonValue StrMixinContains(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        return PythonTruthValue.FromBoolean(
            other is PythonTextValue && ManagedObjectProtocols.Contains(StateOf(target!)!.Value, other)
        );
    }

    private static PythonValue StrMixinConcat(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional,
        bool reflected
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var other = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        if (other is not PythonTextValue text)
        {
            return PythonNotImplementedValue.Instance;
        }

        var value = StateOf(member)!.Value.ToDisplayString();
        return new PythonTextValue(reflected ? text.Value + value : value + text.Value);
    }

    /// <summary>
    /// `str` methods stay available on a member: the attribute is looked up on the
    /// wrapped value and bound to it, and a miss keeps the enum's own wording.
    /// </summary>
    private static PythonValue StrEnumGetAttribute(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var member = (PythonManagedObjectValue)target!;
        var name = positional.Count > 0 && positional[0] is PythonTextValue text
            ? text.Value
            : "";
        var value = StateOf(member)!.Value;
        try
        {
            var attribute = ManagedObjectProtocols.GetAttribute(value, name, default);
            return ManagedObjectProtocols.BindDescriptor(attribute, value, value, default, name);
        }
        catch (Exception error)
            when (error is PythonRuntimeException or PythonRaisedException
                && PythonNamespaceMapping.IsPythonException(error, "AttributeError")
            )
        {
            throw ManagedObjectProtocols.Fault(
                ErrorCode,
                $"'{member.Type.Name}' object has no attribute '{name}'",
                default,
                "AttributeError"
            );
        }
    }

    // -------------------------------------------------------------------------
    // `auto`, `member`, `nonmember`, and `enum.property`
    // -------------------------------------------------------------------------

    private static void InstallAutoProtocols()
    {
        Install(AutoType, "__init__", Protocol("__init__", AutoInitialize));
        Install(AutoType, "__repr__", Protocol("__repr__", AutoRepr));
        Install(AutoType, "value", AutoProtocolGetValue);
    }

    private static PythonValue AutoInitialize(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        if (target is not PythonManagedObjectValue auto)
        {
            return PythonNoneValue.Instance;
        }

        var state = AutoStateOf(auto);
        if (state is null)
        {
            state = new AutoState();
            SetAutoState(auto, state);
        }

        var value = positional.Count > 0 ? positional[0] : AutoNull;
        if (value is PythonManagedObjectValue provided && ReferenceEquals(provided.Type, AutoType))
        {
            value = AutoStateOf(provided)?.Value ?? AutoNull;
        }

        state.Value = value;
        return PythonNoneValue.Instance;
    }

    private static PythonValue AutoRepr(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        var state = AutoStateOf(target!);
        var value = state is null || ReferenceEquals(state.Value, AutoNull)
            ? "_auto_null"
            : state.Value.ToRepresentationString();
        return Text($"auto({value})");
    }

    private static void InstallWrapperProtocols(PythonManagedTypeValue wrapper)
    {
        Install(wrapper, "__init__", Protocol("__init__", WrapperInitialize));
        Install(wrapper, "__repr__", Protocol("__repr__", WrapperRepr));
        Install(wrapper, "value", Protocol("value", WrapperValue));
    }

    private static PythonValue WrapperInitialize(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var instance = (PythonManagedObjectValue)target!;
        var value = positional.Count > 0 ? positional[0] : PythonNoneValue.Instance;
        SetWrapperState(instance, value);
        return PythonNoneValue.Instance;
    }

    private static PythonValue WrapperRepr(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        var instance = (PythonManagedObjectValue)target!;
        return Text($"<enum.member object at 0x{RuntimeHelpers.GetHashCode(instance):x}>");
    }

    private static PythonValue WrapperValue(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = positional;
        var instance = (PythonManagedObjectValue)target!;
        return UnwrapWrapper(instance);
    }

    private static void InstallPropertyProtocols()
    {
        Install(PropertyType, "__get__", Protocol("__get__", PropertyGet));
        Install(PropertyType, "__set__", Protocol("__set__", PropertySet));
        Install(PropertyType, "__delete__", Protocol("__delete__", PropertyDelete));
        Install(PropertyType, "__init__", Protocol("__init__", PropertyInitialize));
        Install(PropertyType, "__set_name__", Protocol("__set_name__", PropertySetName));
        Install(PropertyType, "__doc__", new PythonTextValue(string.Empty));
        Install(
            PropertyType,
            "fget",
            new PythonPropertyValue(Protocol("fget", PropertyFget), null, null)
        );
        Install(
            PropertyType,
            "fset",
            new PythonPropertyValue(Protocol("fset", PropertyFset), null, null)
        );
        Install(
            PropertyType,
            "fdel",
            new PythonPropertyValue(Protocol("fdel", PropertyFdel), null, null)
        );
    }

    private static PythonValue PropertyInitialize(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var state = PropertyStateOf(target!);
        if (state is not null)
        {
            state.Fget = positional.Count > 0 ? positional[0] : null;
            state.Fset = positional.Count > 1 ? positional[1] : null;
            state.Fdel = positional.Count > 2 ? positional[2] : null;
        }

        return PythonNoneValue.Instance;
    }

    /// <summary>Mirrors `DynamicClassAttribute.__set_name__`.</summary>
    private static PythonValue PropertySetName(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var state = PropertyStateOf(target!);
        if (state is not null)
        {
            state.ClsName =
                positional.Count > 0 && positional[0] is PythonManagedTypeValue owner
                    ? owner.Name
                    : null;
            state.Name =
                positional.Count > 1 && positional[1] is PythonTextValue name ? name.Value : null;
        }

        return PythonNoneValue.Instance;
    }

    private static PythonValue PropertyFget(PythonValue? target, IReadOnlyList<PythonValue> positional) =>
        PropertyStateOf((target ?? positional[0])!)?.Fget ?? PythonNoneValue.Instance;

    private static PythonValue PropertyFset(PythonValue? target, IReadOnlyList<PythonValue> positional) =>
        PropertyStateOf((target ?? positional[0])!)?.Fset ?? PythonNoneValue.Instance;

    private static PythonValue PropertyFdel(PythonValue? target, IReadOnlyList<PythonValue> positional) =>
        PropertyStateOf((target ?? positional[0])!)?.Fdel ?? PythonNoneValue.Instance;

    /// <summary>
    /// Mirrors `enum.property.__get__`. The descriptor-get slot calls the raw class
    /// entry with `[self, instance, owner]` and no bound target.
    /// </summary>
    private static PythonValue PropertyGet(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        _ = target;
        var property = (PythonManagedObjectValue)positional[0];
        var state = PropertyStateOf(property)!;
        var instance = positional.Count > 1 ? positional[1] : PythonNoneValue.Instance;
        var owner = positional.Count > 2 ? positional[2] : PythonNoneValue.Instance;
        if (instance is PythonNoneValue)
        {
            if (state.Member is not null)
            {
                return state.Member;
            }

            throw Fault(
                $"{DescribeClass((PythonManagedTypeValue)owner)} has no attribute '{state.Name}'",
                default,
                "AttributeError"
            );
        }

        if (state.Fget is not null)
        {
            return Invoke(state.Fget, [instance], default);
        }

        if (state.AttrType == "attr" && state.ClsType is not null)
        {
            return ManagedObjectProtocols.GetAttribute(
                state.ClsType,
                state.Name ?? "",
                default
            );
        }

        if (state.AttrType == "desc")
        {
            var underlying = StateOf(instance)?.Value ?? PythonNoneValue.Instance;
            return ManagedObjectProtocols.GetAttribute(underlying, state.Name ?? "", default);
        }

        if (
            state.ClsType is not null
            && TryLookupName(MemberMapOf(state.ClsType), state.Name ?? "", out var member)
        )
        {
            return member;
        }

        throw Fault(
            $"{state.ClsName} has no attribute '{state.Name}'",
            default,
            "AttributeError"
        );
    }

    /// <summary>
    /// Mirrors `enum.property.__set__`. The setter slot binds the raw class entry,
    /// so the descriptor itself arrives as the target.
    /// </summary>
    private static PythonValue PropertySet(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var state = PropertyStateOf(target!);
        if (state?.Fset is { } setter)
        {
            return Invoke(setter, [.. positional], default);
        }

        throw Fault(
            $"<enum '{state?.ClsName}'> cannot set attribute '{state?.Name}'",
            default,
            "AttributeError"
        );
    }

    /// <summary>Mirrors `enum.property.__delete__`.</summary>
    private static PythonValue PropertyDelete(
        PythonValue? target,
        IReadOnlyList<PythonValue> positional
    )
    {
        var state = PropertyStateOf(target!);
        if (state?.Fdel is { } deleter)
        {
            return Invoke(deleter, [.. positional], default);
        }

        throw Fault(
            $"<enum '{state?.ClsName}'> cannot delete attribute '{state?.Name}'",
            default,
            "AttributeError"
        );
    }

    /// <summary>Creates an `enum.property` from a getter, mirroring `@property` use.</summary>
    internal static PythonValue CreateProperty(
        PythonValue? getter,
        PythonValue? setter,
        PythonValue? deleter
    ) => MakeProperty(new PropertyState { Fget = getter, Fset = setter, Fdel = deleter });

    private static PythonManagedObjectValue MakeProperty(PropertyState state)
    {
        var property = new PythonManagedObjectValue(PropertyType);
        PropertyStates.Add(property, state);
        return property;
    }

    // -------------------------------------------------------------------------
    // Module-level functions
    // -------------------------------------------------------------------------

    private static PythonBuiltinFunctionValue CreateUnique() =>
        new PythonBuiltinFunctionValue(
            "unique",
            (arguments, span) =>
            {
                if (arguments.Count != 1 || arguments[0] is not PythonManagedTypeValue enumeration)
                {
                    throw Fault("unique() takes exactly one enum class", span);
                }

                var info = EnsureInfo(enumeration);
                var duplicates = new List<string>();
                foreach (var item in info.MemberMap.Items)
                {
                    if (item.Value is not PythonManagedObjectValue member)
                    {
                        continue;
                    }

                    var state = StateOf(member)!;
                    var name = ((PythonTextValue)item.Key).Value;
                    if (state.Name != name)
                    {
                        duplicates.Add($"{name} -> {state.Name}");
                    }
                }

                if (duplicates.Count != 0)
                {
                    throw Fault(
                        $"duplicate values found in {DescribeClass(enumeration)}: {string.Join(", ", duplicates)}",
                        span,
                        "ValueError"
                    );
                }

                return enumeration;
            }
        );

    private static PythonBuiltinFunctionValue CreateShowFlagValues() =>
        new PythonBuiltinFunctionValue(
            "show_flag_values",
            (arguments, span) =>
            {
                var value = arguments.Count > 0 ? arguments[0] : PythonNoneValue.Instance;
                var raw = IsIntValue(value)
                    ? AsBigInteger(value)
                    : AsBigInteger(StateOf(value)?.Value ?? PythonNoneValue.Instance);
                var bits = new List<PythonValue>();
                var bit = BigInteger.One;
                while (bit <= raw)
                {
                    if ((raw & bit) != 0)
                    {
                        bits.Add(PythonWholeNumberValue.Create(bit));
                    }

                    bit <<= 1;
                }

                _ = span;
                return new PythonListValue(bits);
            }
        );

    /// <summary>The `repr()`/`str()` text of an enum class, for the value layer.</summary>
    internal static bool TryDescribeType(PythonManagedTypeValue type, out string description)
    {
        if (!IsEnumClass(type))
        {
            description = string.Empty;
            return false;
        }

        description = DescribeClass(type);
        return true;
    }

    /// <summary>`len(EnumClass)`: the number of canonical members.</summary>
    internal static bool TryGetTypeLength(
        PythonManagedTypeValue type,
        out int length,
        TextSpan span
    )
    {
        _ = span;
        if (!IsEnumClass(type))
        {
            length = 0;
            return false;
        }

        length = MemberNamesOf(type).Elements.Count;
        return true;
    }

    /// <summary>`iter(EnumClass)`: canonical members in definition order.</summary>
    internal static PythonIteratorValue? TryGetTypeIterator(PythonManagedTypeValue type)
    {
        if (!IsEnumClass(type))
        {
            return null;
        }

        var members = new List<PythonValue>();
        foreach (var name in MemberNamesOf(type).Elements)
        {
            if (TryLookupName(MemberMapOf(type), ((PythonTextValue)name).Value, out var member))
            {
                members.Add(member);
            }
        }

        return new PythonIteratorValue(new PythonListValue(members), -1);
    }

    /// <summary>`EnumClass[name]`.</summary>
    internal static PythonValue? TryGetTypeItem(
        PythonManagedTypeValue type,
        PythonValue index,
        TextSpan span
    )
    {
        if (!IsEnumClass(type))
        {
            return null;
        }

        if (index is PythonTextValue text && TryLookupName(MemberMapOf(type), text.Value, out var member))
        {
            return member;
        }

        throw Fault(
            $"{index.ToRepresentationString()}",
            span,
            "KeyError"
        );
    }

    /// <summary>`value in EnumClass`.</summary>
    internal static bool? TryContains(
        PythonManagedTypeValue type,
        PythonValue item,
        TextSpan span
    ) => IsEnumClass(type) ? ContainsMember(type, item, span) : null;

    /// <summary>`reversed(EnumClass)`: canonical members in reverse definition order.</summary>
    internal static PythonListValue? TryGetTypeReversed(PythonManagedTypeValue type)
    {
        if (!IsEnumClass(type))
        {
            return null;
        }

        var members = new List<PythonValue>();
        foreach (var name in MemberNamesOf(type).Elements)
        {
            if (TryLookupName(MemberMapOf(type), ((PythonTextValue)name).Value, out var member))
            {
                members.Add(member);
            }
        }

        members.Reverse();
        return new PythonListValue(members);
    }

    /// <summary>The `__module__.__qualname__` path of a class, for repr text.</summary>
    private static string[] Qualifiers(this PythonManagedTypeValue type)
    {
        var qualName = type.QualName ?? type.Name;
        return qualName == type.Name ? [type.Name] : qualName.Split('.');
    }
}
