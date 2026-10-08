using System.Globalization;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The managed <c>functools</c> slice: <c>reduce</c>, <c>partial</c>,
/// <c>update_wrapper</c>/<c>wraps</c>, <c>lru_cache</c>/<c>cache</c>,
/// <c>cached_property</c>, <c>total_ordering</c>, <c>cmp_to_key</c> and
/// <c>singledispatch</c>.
/// </summary>
/// <remarks>
/// This is a native implementation written against the runtime's object model,
/// not a port of CPython's <c>functools.py</c>: the managed slice has no
/// <c>collections</c>, <c>operator</c>, <c>weakref</c> or <c>sys.modules</c>.
/// Error messages track CPython 3.14 so the differential suite can compare
/// stdout and stderr verbatim.
/// </remarks>
internal static class PythonFunctools
{
    private const string Module = "functools";

    private static PythonManagedTypeValue? _partialType;

    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue("reduce", CreateReduce());
        globals.SetValue("partial", PartialType);
        globals.SetValue("update_wrapper", CreateUpdateWrapper());
        globals.SetValue("wraps", CreateWraps());
        globals.SetValue(
            "WRAPPER_ASSIGNMENTS",
            new PythonTupleValue([
                .. WrapperAssignments.Select(name => (PythonValue)new PythonTextValue(name)),
            ])
        );
        globals.SetValue(
            "WRAPPER_UPDATES",
            new PythonTupleValue([new PythonTextValue("__dict__")])
        );
        globals.SetValue("lru_cache", CreateLruCache());
        globals.SetValue("cache", CreateLruCacheWithoutLimit());
        globals.SetValue("_CacheInfo", CacheInfoType);
        globals.SetValue("cached_property", CachedPropertyType);
    }

    /// <summary>CPython 3.14's default <c>WRAPPER_ASSIGNMENTS</c>.</summary>
    private static readonly string[] WrapperAssignments =
    [
        "__module__",
        "__name__",
        "__qualname__",
        "__doc__",
        "__annotate__",
        "__type_params__",
    ];

    // -------------------------------------------------------------------------
    // reduce
    // -------------------------------------------------------------------------

    private static PythonBuiltinFunctionValue CreateReduce() =>
        new PythonBuiltinFunctionValue(
            "reduce",
            (arguments, span) => Reduce(arguments, [], [], span),
            (arguments, names, values, span) => Reduce(arguments, names, values, span)
        );

    /// <summary>
    /// <c>reduce(function, sequence[, initial])</c>. The first two parameters are
    /// positional-only; only <c>initial</c> may be passed by keyword, and CPython
    /// counts it towards the three-argument maximum.
    /// </summary>
    private static PythonValue Reduce(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        // CPython's C `reduce` reports the arity of the call before it looks at which
        // keywords were passed, and its "too many" budget counts every argument.
        if (positional.Count < 2)
        {
            throw Error(
                $"reduce() takes at least 2 positional arguments ({positional.Count} given)",
                span
            );
        }

        var total = positional.Count + keywordNames.Count;
        if (total > 3)
        {
            throw Error($"reduce() takes at most 3 arguments ({total} given)", span);
        }

        var hasInitialKeyword = false;
        PythonValue initial = PythonNoneValue.Instance;
        for (var index = 0; index < keywordNames.Count; index++)
        {
            if (keywordNames[index] != "initial")
            {
                throw Error(
                    $"reduce() got an unexpected keyword argument '{keywordNames[index]}'",
                    span
                );
            }

            hasInitialKeyword = true;
            initial = keywordValues[index];
        }

        var function = positional[0];
        if (!ManagedObjectProtocols.IsCallable(function))
        {
            throw Error(
                $"'{ManagedObjectProtocols.GetTypeName(function)}' object is not callable",
                span
            );
        }

        var iterator = ManagedObjectProtocols.GetIterator(positional[1], span);
        PythonValue accumulator;
        if (hasInitialKeyword || positional.Count == 3)
        {
            accumulator = positional.Count == 3 ? positional[2] : initial;
        }
        else
        {
            if (!ManagedObjectProtocols.TryGetNext(iterator, out accumulator, span))
            {
                throw Error("reduce() of empty iterable with no initial value", span);
            }
        }

        while (ManagedObjectProtocols.TryGetNext(iterator, out var element, span))
        {
            accumulator = Invoke(function, [accumulator, element], span);
        }

        return accumulator;
    }

    // -------------------------------------------------------------------------
    // partial
    // -------------------------------------------------------------------------

    /// <summary>The per-instance state of a <c>functools.partial</c>.</summary>
    private sealed class PartialState
    {
        internal PythonValue Func { get; set; } = PythonNoneValue.Instance;

        internal PythonValue[] Args { get; set; } = [];

        /// <summary>The stored keyword dictionary; the Python-visible object itself.</summary>
        internal PythonDictionaryValue Keywords { get; set; } = new([]);

        internal PartialState Clone() =>
            new()
            {
                Func = Func,
                Args = Args,
                Keywords = Keywords,
            };
    }

    private static PythonManagedTypeValue PartialType => _partialType ??= CreatePartialType();

    private static PythonManagedTypeValue CreatePartialType()
    {
        var type = new PythonManagedTypeValue("partial") { Module = Module, QualName = "partial" };
        type.Attributes["__new__"] = new PythonProtocolFunctionValue(
            "__new__",
            (_, arguments) => NewPartial(type, arguments, [], []),
            (_, arguments, names, values) => NewPartial(type, arguments, names, values)
        );
        type.Attributes["__module__"] = new PythonTextValue(Module);
        // CPython exposes `func`, `args` and `keywords` as read-only getset members:
        // both assignment and deletion report `AttributeError: readonly attribute`.
        type.Attributes["func"] = ReadOnlyMember("func", target => State(target).Func);
        type.Attributes["args"] = ReadOnlyMember(
            "args",
            target => new PythonTupleValue([.. State(target).Args])
        );
        type.Attributes["keywords"] = ReadOnlyMember("keywords", target => State(target).Keywords);
        type.Attributes["__call__"] = new PythonProtocolFunctionValue(
            "__call__",
            (self, arguments) => InvokePartial(self, arguments, [], []),
            (self, arguments, names, values) => InvokePartial(self, arguments, names, values)
        );
        type.Attributes["__repr__"] = new PythonProtocolFunctionValue(
            "__repr__",
            (self, _) => new PythonTextValue(RepresentPartial(self, []))
        );
        // The descriptor-get slot receives the partial, the instance (None for a
        // class-level access) and the owner as ordinary arguments, never as `self`.
        type.Attributes["__get__"] = new PythonProtocolFunctionValue(
            "__get__",
            (_, arguments) => BindPartial(arguments[0], arguments[1]),
            (_, arguments, _, _) => BindPartial(arguments[0], arguments[1])
        );
        return type;
    }

    /// <summary>
    /// A member that reads through <paramref name="get"/> and rejects both assignment
    /// and deletion with CPython's `readonly attribute` diagnostic.
    /// </summary>
    private static PythonPropertyValue ReadOnlyMember(
        string name,
        Func<PythonValue, PythonValue> get
    ) =>
        new(
            new PythonProtocolFunctionValue(name, (_, arguments) => get(arguments[0])),
            new PythonProtocolFunctionValue("setter", (_, _) => throw ReadOnly()),
            new PythonProtocolFunctionValue("deleter", (_, _) => throw ReadOnly())
        );

    private static PythonRuntimeException ReadOnly() =>
        ManagedObjectProtocols.Fault("DPY4023", "readonly attribute", default, "AttributeError");

    private static PartialState State(PythonValue? target) =>
        target is PythonManagedObjectValue { Payload: PartialState state }
            ? state
            : throw Error("descriptor 'partial' requires a 'functools.partial' object", default);

    private static PythonManagedObjectValue NewPartial(
        PythonManagedTypeValue type,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        // `type.__call__` invokes `partial.__new__(partial, *args, **kwargs)`, so the
        // type leads the positional list exactly as it does for any other class.
        if (positional.Count == 0)
        {
            throw Error("type 'partial' takes at least one argument", default);
        }

        var argumentStart = 0;
        if (positional[0] is PythonManagedTypeValue leading && ReferenceEquals(leading, type))
        {
            argumentStart = 1;
        }

        if (positional.Count <= argumentStart)
        {
            throw Error("type 'partial' takes at least one argument", default);
        }

        var function = positional[argumentStart];
        if (!ManagedObjectProtocols.IsCallable(function))
        {
            throw Error("the first argument must be callable", default);
        }

        var state = new PartialState { Func = function };
        // Flattening a nested partial mirrors CPython 3.14: the outer call reuses the
        // inner callable, its positional arguments and its keywords as a base.
        argumentStart++;
        if (function is PythonManagedObjectValue { Payload: PartialState inner })
        {
            state.Func = inner.Func;
            state.Args = [.. inner.Args];
            state.Keywords = inner.Keywords;
        }

        var extra = new List<PythonValue>(state.Args);
        for (var index = argumentStart; index < positional.Count; index++)
        {
            extra.Add(positional[index]);
        }

        state.Args = [.. extra];
        for (var index = 0; index < keywordNames.Count; index++)
        {
            SetKeyword(state.Keywords, keywordNames[index], keywordValues[index]);
        }

        return new PythonManagedObjectValue(type, state);
    }

    private static PythonValue InvokePartial(
        PythonValue? self,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var state = State(self);
        var arguments = new List<PythonValue>(state.Args);
        arguments.AddRange(positional);

        var mergedNames = new List<string>();
        var mergedValues = new List<PythonValue>();
        foreach (var item in state.Keywords.Items)
        {
            mergedNames.Add(((PythonTextValue)item.Key).Value);
            mergedValues.Add(item.Value);
        }

        for (var index = 0; index < keywordNames.Count; index++)
        {
            var existing = mergedNames.IndexOf(keywordNames[index]);
            if (existing >= 0)
            {
                mergedValues[existing] = keywordValues[index];
            }
            else
            {
                mergedNames.Add(keywordNames[index]);
                mergedValues.Add(keywordValues[index]);
            }
        }

        return Invoke(state.Func, arguments, mergedNames, mergedValues, default);
    }

    private static PythonValue BindPartial(PythonValue self, PythonValue instance)
    {
        if (instance is PythonNoneValue)
        {
            // Attribute access on the class leaves the partial unbound, exactly as
            // CPython's `partial.__get__(None, owner)` does.
            return self;
        }

        // `partial.__get__` binds the partial to the instance, so the receiver leads
        // every later call's arguments.
        var target = self;
        return new PythonProtocolFunctionValue(
            "partial",
            (_, callArguments) => InvokePartial(target, [instance, .. callArguments], [], []),
            (_, callArguments, names, values) =>
                InvokePartial(target, [instance, .. callArguments], names, values)
        );
    }

    /// <summary>
    /// <c>functools.partial(func, ...)</c>. Nested partials print flattened, matching
    /// CPython 3.14's C implementation.
    /// </summary>
    private static string RepresentPartial(PythonValue? self, List<PythonValue> visited)
    {
        var state = State(self);
        visited.Add(self!);
        var parts = new List<string>();
        var function = state.Func;
        var arguments = new List<PythonValue>(state.Args);
        var keywords = new List<PythonDictionaryItemValue>(state.Keywords.Items);
        while (function is PythonManagedObjectValue { Payload: PartialState inner })
        {
            arguments.InsertRange(0, inner.Args);
            keywords.InsertRange(0, inner.Keywords.Items);
            function = inner.Func;
        }

        parts.Add(Represent(function, visited));
        parts.AddRange(arguments.Select(argument => Represent(argument, visited)));
        parts.AddRange(
            keywords.Select(item =>
                $"{((PythonTextValue)item.Key).Value}={Represent(item.Value, visited)}"
            )
        );
        visited.RemoveAt(visited.Count - 1);
        return $"functools.partial({string.Join(", ", parts)})";
    }

    /// <summary>
    /// <c>repr()</c> for a value, printing <c>...</c> for a value already being
    /// rendered so a self-referential partial cannot loop.
    /// </summary>
    private static string Represent(PythonValue value, List<PythonValue> visited)
    {
        if (visited.Any(candidate => ReferenceEquals(candidate, value)))
        {
            return "...";
        }

        if (value is PythonManagedObjectValue { Payload: PartialState })
        {
            return RepresentPartial(value, visited);
        }

        return value.ToRepresentationString();
    }

    // -------------------------------------------------------------------------
    // update_wrapper / wraps
    // -------------------------------------------------------------------------

    private static PythonBuiltinFunctionValue CreateUpdateWrapper() =>
        new PythonBuiltinFunctionValue(
            "update_wrapper",
            (arguments, span) => UpdateWrapper(arguments, [], [], span),
            (arguments, names, values, span) => UpdateWrapper(arguments, names, values, span)
        );

    private static PythonBuiltinFunctionValue CreateWraps() =>
        new PythonBuiltinFunctionValue(
            "wraps",
            (arguments, span) => WrapDecorator(arguments, [], [], span),
            (arguments, names, values, span) => WrapDecorator(arguments, names, values, span)
        );

    /// <summary>
    /// <c>update_wrapper(wrapper, wrapped, assigned=WRAPPER_ASSIGNMENTS,
    /// updated=WRAPPER_UPDATES)</c>. Attributes the wrapped object does not carry
    /// are skipped, and <c>__wrapped__</c> is assigned last so an update of the
    /// wrapper's dictionary cannot overwrite it.
    /// </summary>
    private static PythonValue UpdateWrapper(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var bound = BindNamed(
            "update_wrapper",
            positional,
            keywordNames,
            keywordValues,
            ["wrapper", "wrapped", "assigned", "updated"],
            [null, null, DefaultAssignments(), DefaultUpdates()],
            span
        );
        PythonValue wrapper = bound[0]!;
        PythonValue wrapped = bound[1]!;
        PythonValue assigned = bound[2] ?? DefaultAssignments();
        PythonValue updated = bound[3] ?? DefaultUpdates();

        foreach (var attribute in SequenceOf(assigned, span))
        {
            var name = RequireText(attribute, "update_wrapper", span);
            if (TryGetAttribute(wrapped, name, out var value))
            {
                ManagedObjectProtocols.SetAttribute(wrapper, name, value, span);
            }
        }

        foreach (var attribute in SequenceOf(updated, span))
        {
            var name = RequireText(attribute, "update_wrapper", span);
            var destination = ManagedObjectProtocols.GetAttribute(wrapper, name, span);
            if (TryGetAttribute(wrapped, name, out var source))
            {
                UpdateDictionary(destination, source, span);
            }
        }

        ManagedObjectProtocols.SetAttribute(wrapper, "__wrapped__", wrapped, span);
        return wrapper;
    }

    /// <summary><c>wraps(wrapped, ...)</c> returns <c>partial(update_wrapper, ...)</c> as CPython does.</summary>
    private static PythonManagedObjectValue WrapDecorator(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        // `wraps` is a partial application of `update_wrapper`: it fills in all three
        // parameters, so the returned partial always carries them as keywords.
        var bound = BindNamed(
            "wraps",
            positional,
            keywordNames,
            keywordValues,
            ["wrapped", "assigned", "updated"],
            [null, DefaultAssignments(), DefaultUpdates()],
            span
        );
        var state = new PartialState { Func = UpdateWrapperTarget };
        SetKeyword(state.Keywords, "wrapped", bound[0]!);
        SetKeyword(state.Keywords, "assigned", bound[1] ?? PythonNoneValue.Instance);
        SetKeyword(state.Keywords, "updated", bound[2] ?? PythonNoneValue.Instance);
        return new PythonManagedObjectValue(PartialType, state);
    }

    /// <summary>The <c>update_wrapper</c> callable <c>wraps</c> partially applies.</summary>
    private static readonly PythonBuiltinFunctionValue UpdateWrapperTarget = CreateUpdateWrapper();

    // -------------------------------------------------------------------------
    // lru_cache / cache
    // -------------------------------------------------------------------------

    private static PythonManagedTypeValue? _lruWrapperType;
    private static PythonManagedTypeValue? _cacheInfoType;

    private static PythonManagedTypeValue LruWrapperType =>
        _lruWrapperType ??= CreateLruWrapperType();

    private static PythonManagedTypeValue CacheInfoType => _cacheInfoType ??= CreateCacheInfoType();

    /// <summary>
    /// What a <c>_lru_cache_wrapper</c> carries: the callable it decorates, its
    /// bounded size, whether types participate in the key, and the cache itself.
    /// </summary>
    private sealed class LruState
    {
        internal PythonValue Function = PythonNoneValue.Instance;
        internal int? MaxSize = 128;
        internal bool Typed;
        internal readonly PythonDictionaryValue Cache = new([]);
        internal readonly LinkedList<CacheEntry> Order = new();
        internal readonly Dictionary<long, LinkedListNode<CacheEntry>> Nodes = [];
        internal long NextSequence;
        internal long Hits;
        internal long Misses;
    }

    /// <summary>One cache slot: its recency ticket, the key it was stored under and its result.</summary>
    private sealed class CacheEntry
    {
        internal long Sequence;
        internal PythonValue Key = PythonNoneValue.Instance;
        internal PythonValue Result = PythonNoneValue.Instance;
    }

    /// <summary>
    /// CPython's sentinel that separates positional from keyword arguments in a cache
    /// key: an object equal to nothing but itself.
    /// </summary>
    private static readonly PythonValue KeywordMark = new PythonManagedObjectValue(
        new PythonManagedTypeValue("_kwd_mark") { Module = Module }
    );

    private static PythonManagedTypeValue CreateLruWrapperType()
    {
        var type = new PythonManagedTypeValue("_lru_cache_wrapper")
        {
            Module = Module,
            QualName = "_lru_cache_wrapper",
        };
        type.Attributes["__call__"] = new PythonProtocolFunctionValue(
            "__call__",
            (self, arguments) => InvokeCached(self, arguments, [], [], default),
            (self, arguments, names, values) =>
                InvokeCached(self, arguments, names, values, default)
        );
        type.Attributes["cache_info"] = new PythonProtocolFunctionValue(
            "cache_info",
            (self, arguments) =>
            {
                RejectArguments("cache_info", arguments);
                return CacheInformation(LruStateOf(self));
            }
        );
        type.Attributes["cache_clear"] = new PythonProtocolFunctionValue(
            "cache_clear",
            (self, arguments) =>
            {
                RejectArguments("cache_clear", arguments);
                ClearCache(LruStateOf(self));
                return PythonNoneValue.Instance;
            }
        );
        // `_lru_cache_wrapper.__get__` binds an accessed-through-instance wrapper the
        // way a function does, so methods decorated with `lru_cache` receive `self`.
        type.Attributes["__get__"] = new PythonProtocolFunctionValue(
            "__get__",
            (_, arguments) => BindCachedWrapper(arguments[0], arguments[1]),
            (_, arguments, _, _) => BindCachedWrapper(arguments[0], arguments[1])
        );
        return type;
    }

    private static void RejectArguments(string name, IReadOnlyList<PythonValue> arguments)
    {
        if (arguments.Count > 0)
        {
            throw Error(
                $"_lru_cache_wrapper.{name}() takes no arguments ({arguments.Count} given)",
                default
            );
        }
    }

    private static PythonValue BindCachedWrapper(PythonValue self, PythonValue instance)
    {
        if (instance is PythonNoneValue)
        {
            // Attribute access on the class leaves the wrapper unbound, as in CPython.
            return self;
        }

        var wrapper = self;
        return new PythonBoundMethodValue(
            ManagedObjectProtocols.GetAttribute(wrapper, "__name__", default)
                is PythonTextValue name
                ? name.Value
                : "__call__",
            wrapper,
            new PythonProtocolFunctionValue(
                "__call__",
                (_, arguments) => InvokeCached(wrapper, [instance, .. arguments], [], [], default),
                (_, arguments, names, values) =>
                    InvokeCached(wrapper, [instance, .. arguments], names, values, default)
            )
        );
    }

    private static PythonBuiltinFunctionValue CreateLruCache() =>
        new PythonBuiltinFunctionValue(
            "lru_cache",
            (arguments, span) => LruCache(arguments, [], [], span),
            (arguments, names, values, span) => LruCache(arguments, names, values, span)
        );

    private static PythonBuiltinFunctionValue CreateLruCacheWithoutLimit() =>
        new PythonBuiltinFunctionValue(
            "cache",
            (arguments, span) => Cache(arguments, [], [], span),
            (arguments, names, values, span) => Cache(arguments, names, values, span)
        );

    /// <summary>
    /// <c>cache(user_function)</c>, CPython's <c>lru_cache(maxsize=None)</c> under
    /// another name. It takes exactly the decorated callable.
    /// </summary>
    private static PythonManagedObjectValue Cache(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var bound = BindNamed(
            "cache",
            positional,
            keywordNames,
            keywordValues,
            ["user_function"],
            [null],
            span
        );
        return DecorateWithCache(bound[0]!, null, typed: false, span);
    }

    /// <summary>
    /// <c>lru_cache(maxsize=128, typed=False)</c> in its three documented shapes: a
    /// bare decorator (<c>@lru_cache</c>), a configured decorator
    /// (<c>@lru_cache(maxsize=…)</c>) and a direct call.
    /// </summary>
    private static PythonValue LruCache(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var bound = BindNamed(
            "lru_cache",
            positional,
            keywordNames,
            keywordValues,
            ["maxsize", "typed"],
            [new PythonWholeNumberValue(128), PythonTruthValue.False],
            span
        );
        var maxsize = bound[0]!;
        var typedArgument = bound[1]!;
        var typed = ManagedObjectProtocols.IsTrue(typedArgument);

        if (TryWholeNumber(maxsize, out var limit))
        {
            return CreateLruDecorator(limit, typed, span);
        }

        // `lru_cache(f)` is `lru_cache()` applied at once, but only when `typed` is
        // still the boolean the signature declares.
        if (ManagedObjectProtocols.IsCallable(maxsize) && typedArgument is PythonTruthValue)
        {
            return DecorateWithCache(maxsize, 128, typed, span);
        }

        if (maxsize is not PythonNoneValue)
        {
            throw Error("Expected first argument to be an integer, a callable, or None", span);
        }

        return CreateLruDecorator(null, typed, span);
    }

    /// <summary>
    /// <c>isinstance(maxsize, int)</c>, which also admits the booleans: a negative
    /// limit collapses to zero, as CPython's does.
    /// </summary>
    private static bool TryWholeNumber(PythonValue value, out int limit)
    {
        switch (value)
        {
            case PythonWholeNumberValue whole when whole.Value < 0:
                limit = 0;
                return true;
            case PythonWholeNumberValue whole:
                limit = whole.Value > int.MaxValue ? int.MaxValue : (int)whole.Value;
                return true;
            case PythonTruthValue truth:
                limit = truth.Value ? 1 : 0;
                return true;
            default:
                limit = 0;
                return false;
        }
    }

    /// <summary>
    /// The decorator <c>lru_cache(...)</c> hands back before a callable arrives, named
    /// for CPython's nested <c>decorating_function</c> so its diagnostics match.
    /// </summary>
    private static PythonProtocolFunctionValue CreateLruDecorator(
        int? maxSize,
        bool typed,
        TextSpan span
    )
    {
        PythonValue Decorate(
            IReadOnlyList<PythonValue> arguments,
            IReadOnlyList<string> keywordNames,
            IReadOnlyList<PythonValue> keywordValues
        )
        {
            var bound = BindNamed(
                // CPython's wording names the nested function's qualname.
                "lru_cache.<locals>.decorating_function",
                arguments,
                keywordNames,
                keywordValues,
                ["user_function"],
                [null],
                span
            );
            return DecorateWithCache(bound[0]!, maxSize, typed, span);
        }

        return new PythonProtocolFunctionValue(
            "lru_cache.<locals>.decorating_function",
            (_, arguments) => Decorate(arguments, [], []),
            (_, arguments, keywordNames, keywordValues) =>
                Decorate(arguments, keywordNames, keywordValues)
        );
    }

    private static PythonManagedObjectValue DecorateWithCache(
        PythonValue function,
        int? maxSize,
        bool typed,
        TextSpan span
    )
    {
        var state = new LruState
        {
            Function = function,
            MaxSize = maxSize,
            Typed = typed,
        };
        var wrapper = new PythonManagedObjectValue(LruWrapperType, state);
        // CPython installs `cache_parameters` in the wrapper's dictionary, so the
        // merged `update_wrapper` pass below cannot overwrite it.
        wrapper.Attributes["cache_parameters"] = new PythonProtocolFunctionValue(
            "cache_parameters",
            (_, _) => CacheParameters(state)
        );
        UpdateWrapper([wrapper, function], [], [], span);
        return wrapper;
    }

    private static PythonDictionaryValue CacheParameters(LruState state)
    {
        List<PythonDictionaryItemValue> items =
        [
            new(new PythonTextValue("maxsize"), MaximumSize(state)),
            new(new PythonTextValue("typed"), PythonTruthValue.FromBoolean(state.Typed)),
        ];
        return new PythonDictionaryValue([.. items]);
    }

    private static PythonValue MaximumSize(LruState state) =>
        state.MaxSize is { } size ? new PythonWholeNumberValue(size) : PythonNoneValue.Instance;

    private static LruState LruStateOf(PythonValue? target) =>
        target is PythonManagedObjectValue { Payload: LruState state }
            ? state
            : throw Error("descriptor '_lru_cache_wrapper' requires a cache object", default);

    private static void ClearCache(LruState state)
    {
        state.Cache.ClearItems();
        state.Order.Clear();
        state.Nodes.Clear();
        state.Hits = 0;
        state.Misses = 0;
    }

    private static PythonManagedObjectValue CacheInformation(LruState state) =>
        new PythonManagedObjectValue(
            CacheInfoType,
            new CacheInfoPayload(state.Hits, state.Misses, state.MaxSize, state.Order.Count)
        );

    private static PythonValue InvokeCached(
        PythonValue? self,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var state = LruStateOf(self);
        var key = MakeCacheKey(positional, keywordNames, keywordValues, state.Typed);
        // Hashing reports an unhashable argument with CPython's own wording; the
        // dictionary's own diagnostic for the same condition reads differently.
        ManagedObjectProtocols.ComputePythonHash(key, span);
        if (ManagedObjectProtocols.TryFindDictionaryItem(state.Cache, key, out var found))
        {
            var sequence = ((PythonWholeNumberValue)found.Value).Value;
            var node = state.Nodes[(long)sequence];
            state.Order.Remove(node);
            state.Order.AddLast(node);
            state.Hits++;
            return node.Value.Result;
        }

        state.Misses++;
        var result = Invoke(state.Function, positional, keywordNames, keywordValues, span);
        Store(state, key, result, span);
        return result;
    }

    private static void Store(LruState state, PythonValue key, PythonValue result, TextSpan span)
    {
        if (state.MaxSize is 0)
        {
            // A zero-sized cache retains nothing and never evicts.
            return;
        }

        var sequence = state.NextSequence++;
        var node = state.Order.AddLast(
            new CacheEntry
            {
                Sequence = sequence,
                Key = key,
                Result = result,
            }
        );
        state.Nodes[sequence] = node;
        ManagedObjectProtocols.SetDictionaryItem(
            state.Cache,
            key,
            new PythonWholeNumberValue(sequence),
            span
        );

        if (state.MaxSize is { } limit && state.Order.Count > limit)
        {
            EvictOldest(state);
        }
    }

    private static void EvictOldest(LruState state)
    {
        var oldest = state.Order.First!;
        state.Order.RemoveFirst();
        state.Nodes.Remove(oldest.Value.Sequence);
        if (
            ManagedObjectProtocols.TryFindDictionaryItem(
                state.Cache,
                oldest.Value.Key,
                out var item
            )
        )
        {
            state.Cache.RemoveItem(item);
        }
    }

    /// <summary>
    /// CPython's <c>_make_key</c>: the arguments, then the keyword pairs preceded by a
    /// sentinel, then — when <c>typed</c> — the type of every value. A single positional
    /// argument of type <c>int</c> or <c>str</c> is its own key, which is why
    /// <c>f(1)</c> and <c>f(True)</c> occupy different slots.
    /// </summary>
    private static PythonValue MakeCacheKey(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        bool typed
    )
    {
        List<PythonValue> key = [.. positional];
        if (keywordNames.Count > 0)
        {
            key.Add(KeywordMark);
            for (var index = 0; index < keywordNames.Count; index++)
            {
                key.Add(new PythonTextValue(keywordNames[index]));
                key.Add(keywordValues[index]);
            }
        }

        if (typed)
        {
            foreach (var argument in positional)
            {
                key.Add(PythonBuiltinTypes.GetRuntimeType(argument));
            }

            if (keywordNames.Count > 0)
            {
                foreach (var value in keywordValues)
                {
                    key.Add(PythonBuiltinTypes.GetRuntimeType(value));
                }
            }
        }
        else if (key.Count == 1 && key[0] is PythonWholeNumberValue or PythonTextValue)
        {
            return key[0];
        }

        return new PythonTupleValue([.. key]);
    }

    private static PythonManagedTypeValue CreateCacheInfoType()
    {
        var type = new PythonManagedTypeValue("CacheInfo")
        {
            Module = Module,
            QualName = "CacheInfo",
        };
        type.Attributes["__new__"] = new PythonProtocolFunctionValue(
            "__new__",
            (_, arguments) => NewCacheInfo(type, arguments, [], []),
            (_, arguments, names, values) => NewCacheInfo(type, arguments, names, values)
        );
        type.Attributes["hits"] = ReadOnlyMember(
            "hits",
            target => new PythonWholeNumberValue(CacheInfoOf(target).Hits)
        );
        type.Attributes["misses"] = ReadOnlyMember(
            "misses",
            target => new PythonWholeNumberValue(CacheInfoOf(target).Misses)
        );
        type.Attributes["maxsize"] = ReadOnlyMember(
            "maxsize",
            target =>
                CacheInfoOf(target).MaxSize is { } size
                    ? new PythonWholeNumberValue(size)
                    : PythonNoneValue.Instance
        );
        type.Attributes["currsize"] = ReadOnlyMember(
            "currsize",
            target => new PythonWholeNumberValue(CacheInfoOf(target).CurrentSize)
        );
        type.Attributes["_fields"] = new PythonTupleValue([
            new PythonTextValue("hits"),
            new PythonTextValue("misses"),
            new PythonTextValue("maxsize"),
            new PythonTextValue("currsize"),
        ]);
        // A namedtuple has no instance dictionary, so its instances accept only the
        // four slots the fields describe.
        type.Slots = PythonSlotLayout.Create(type, new PythonTupleValue([]), default);
        // `CacheInfo` is a namedtuple, so it also carries tuple's search methods.
        type.Attributes["count"] = new PythonProtocolFunctionValue(
            "count",
            (self, arguments) => CacheInfoCount(self!, arguments, default)
        );
        type.Attributes["index"] = new PythonProtocolFunctionValue(
            "index",
            (self, arguments) => CacheInfoIndexOf(self!, arguments, default)
        );
        type.Attributes["__repr__"] = new PythonProtocolFunctionValue(
            "__repr__",
            (self, _) => new PythonTextValue(RepresentCacheInfo(CacheInfoOf(self!)))
        );
        type.Attributes["__len__"] = new PythonProtocolFunctionValue(
            "__len__",
            (_, _) => new PythonWholeNumberValue(CacheInfoFields.Length)
        );
        type.Attributes["__iter__"] = new PythonProtocolFunctionValue(
            "__iter__",
            (self, _) =>
                ManagedObjectProtocols.GetIterator(
                    new PythonTupleValue(CacheInfoValues(self!)),
                    default
                )
        );
        type.Attributes["__getitem__"] = new PythonProtocolFunctionValue(
            "__getitem__",
            (self, arguments) => CacheInfoItem(self!, arguments[0], default)
        );
        // A namedtuple compares, and hashes, as the tuple of its values.
        type.Attributes["__eq__"] = new PythonProtocolFunctionValue(
            "__eq__",
            (self, arguments) => PythonTruthValue.FromBoolean(IsSameCacheInfo(self!, arguments[0]))
        );
        type.Attributes["__hash__"] = new PythonProtocolFunctionValue(
            "__hash__",
            (self, _) =>
                new PythonWholeNumberValue(
                    ManagedObjectProtocols.ComputePythonHash(
                        new PythonTupleValue(CacheInfoValues(self!)),
                        default
                    )
                )
        );
        type.Attributes["_asdict"] = new PythonProtocolFunctionValue(
            "_asdict",
            (self, _) => CacheInfoMapping(CacheInfoOf(self!))
        );
        type.Attributes["_replace"] = new PythonProtocolFunctionValue(
            "_replace",
            (self, _) => self!,
            (self, _, names, values) => ReplaceCacheInfo(self!, names, values)
        );
        return type;
    }

    private static readonly string[] CacheInfoFields = ["hits", "misses", "maxsize", "currsize"];

    private sealed record CacheInfoPayload(long Hits, long Misses, int? MaxSize, long CurrentSize);

    private static CacheInfoPayload CacheInfoOf(PythonValue? target) =>
        target is PythonManagedObjectValue { Payload: CacheInfoPayload payload }
            ? payload
            : throw Error("descriptor 'CacheInfo' requires a CacheInfo object", default);

    private static PythonManagedObjectValue NewCacheInfo(
        PythonManagedTypeValue type,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var arguments = positional;
        if (
            positional.Count > 0
            && positional[0] is PythonManagedTypeValue leading
            && ReferenceEquals(leading, type)
        )
        {
            arguments = [.. positional.Skip(1)];
        }

        var bound = BindNamed(
            // CPython's namedtuple names its constructor `CacheInfo.__new__` in
            // argument diagnostics.
            "CacheInfo.__new__",
            arguments,
            keywordNames,
            keywordValues,
            CacheInfoFields,
            [null, null, null, null],
            default
        );
        return new PythonManagedObjectValue(
            type,
            new CacheInfoPayload(
                Whole(bound[0]!, "hits"),
                Whole(bound[1]!, "misses"),
                bound[2] is PythonNoneValue ? null : (int)Whole(bound[2]!, "maxsize"),
                Whole(bound[3]!, "currsize")
            )
        );
    }

    private static long Whole(PythonValue value, string field) =>
        value is PythonWholeNumberValue number
            ? (long)number.Value
            : throw Error($"'{field}' must be an integer", default);

    private static string RepresentCacheInfo(CacheInfoPayload info) =>
        $"CacheInfo(hits={info.Hits}, misses={info.Misses}, "
        + $"maxsize={(info.MaxSize is { } size ? size.ToString(CultureInfo.InvariantCulture) : "None")}, "
        + $"currsize={info.CurrentSize})";

    private static PythonValue[] CacheInfoValues(PythonValue target)
    {
        var info = CacheInfoOf(target);
        return
        [
            new PythonWholeNumberValue(info.Hits),
            new PythonWholeNumberValue(info.Misses),
            info.MaxSize is { } size ? new PythonWholeNumberValue(size) : PythonNoneValue.Instance,
            new PythonWholeNumberValue(info.CurrentSize),
        ];
    }

    /// <summary>The namedtuple's positional and negative-index access.</summary>
    private static PythonValue CacheInfoItem(PythonValue target, PythonValue index, TextSpan span)
    {
        if (index is not PythonWholeNumberValue number)
        {
            throw Error(
                $"tuple indices must be integers or slices, not "
                    + ManagedObjectProtocols.GetTypeName(index),
                span
            );
        }

        var position = (long)number.Value;
        if (position < 0)
        {
            position += CacheInfoFields.Length;
        }

        if (position < 0 || position >= CacheInfoFields.Length)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4010",
                "tuple index out of range",
                span,
                "IndexError"
            );
        }

        return CacheInfoValues(target)[position];
    }

    private static PythonWholeNumberValue CacheInfoCount(
        PythonValue target,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count != 1)
        {
            throw Error(
                $"tuple.count() takes exactly one argument ({arguments.Count} given)",
                span
            );
        }

        return new PythonWholeNumberValue(
            CacheInfoValues(target)
                .Count(value => ManagedObjectProtocols.AreEqual(value, arguments[0]))
        );
    }

    private static PythonWholeNumberValue CacheInfoIndexOf(
        PythonValue target,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count is 0 or > 3)
        {
            var wording =
                arguments.Count == 0 ? "takes at least 1 argument" : "takes at most 3 arguments";
            throw Error($"tuple.index() {wording} ({arguments.Count} given)", span);
        }

        var values = CacheInfoValues(target);
        var start = SliceBound(arguments, 1, 0, values.Length, span);
        var stop = SliceBound(arguments, 2, values.Length, values.Length, span);
        for (var index = start; index < stop; index++)
        {
            if (ManagedObjectProtocols.AreEqual(values[index], arguments[0]))
            {
                return new PythonWholeNumberValue(index);
            }
        }

        throw ManagedObjectProtocols.Fault(
            "DPY4003",
            "tuple.index(x): x not in tuple",
            span,
            "ValueError"
        );
    }

    /// <summary>Binds <c>tuple.index</c>'s optional slice bounds, clamping as CPython does.</summary>
    private static int SliceBound(
        IReadOnlyList<PythonValue> arguments,
        int position,
        int fallback,
        int length,
        TextSpan span
    )
    {
        if (position >= arguments.Count)
        {
            return fallback;
        }

        var bound = (int)Whole(arguments[position], "index");
        if (bound < 0)
        {
            bound += length;
        }

        return Math.Clamp(bound, 0, length);
    }

    /// <summary>
    /// A <c>CacheInfo</c> is a tuple of its four values, so it compares equal to any
    /// tuple holding them, not only to another <c>CacheInfo</c>.
    /// </summary>
    private static bool IsSameCacheInfo(PythonValue target, PythonValue other)
    {
        if (other is PythonManagedObjectValue { Payload: CacheInfoPayload payload })
        {
            return CacheInfoOf(target) == payload;
        }

        if (other is not PythonTupleValue tuple)
        {
            return false;
        }

        var values = CacheInfoValues(target);
        if (tuple.Elements.Length != values.Length)
        {
            return false;
        }

        for (var index = 0; index < values.Length; index++)
        {
            if (!ManagedObjectProtocols.AreEqual(values[index], tuple.Elements[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static PythonDictionaryValue CacheInfoMapping(CacheInfoPayload info)
    {
        List<PythonDictionaryItemValue> items = [];
        var values = CacheInfoValues(new PythonManagedObjectValue(CacheInfoType, info));
        for (var index = 0; index < CacheInfoFields.Length; index++)
        {
            items.Add(
                new PythonDictionaryItemValue(
                    new PythonTextValue(CacheInfoFields[index]),
                    values[index]
                )
            );
        }

        return new PythonDictionaryValue([.. items]);
    }

    private static PythonManagedObjectValue ReplaceCacheInfo(
        PythonValue target,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values
    )
    {
        var info = CacheInfoOf(target);
        var slots = new PythonValue[]
        {
            new PythonWholeNumberValue(info.Hits),
            new PythonWholeNumberValue(info.Misses),
            info.MaxSize is { } size ? new PythonWholeNumberValue(size) : PythonNoneValue.Instance,
            new PythonWholeNumberValue(info.CurrentSize),
        };
        var unexpected = names.Where(name => Array.IndexOf(CacheInfoFields, name) < 0).ToArray();
        if (unexpected.Length > 0)
        {
            // The namedtuple's own wording, with the offending names in a Python list.
            var listed = new PythonListValue([
                .. unexpected.Select(name => (PythonValue)new PythonTextValue(name)),
            ]);
            throw Error($"Got unexpected field names: {listed.ToRepresentationString()}", default);
        }

        for (var index = 0; index < names.Count; index++)
        {
            slots[Array.IndexOf(CacheInfoFields, names[index])] = values[index];
        }

        return new PythonManagedObjectValue(
            CacheInfoType,
            new CacheInfoPayload(
                Whole(slots[0], "hits"),
                Whole(slots[1], "misses"),
                slots[2] is PythonNoneValue ? null : (int)Whole(slots[2], "maxsize"),
                Whole(slots[3], "currsize")
            )
        );
    }

    // -------------------------------------------------------------------------
    // cached_property
    // -------------------------------------------------------------------------

    private static PythonManagedTypeValue? _cachedPropertyType;

    private static PythonManagedTypeValue CachedPropertyType =>
        _cachedPropertyType ??= CreateCachedPropertyType();

    private static PythonManagedTypeValue CreateCachedPropertyType()
    {
        var type = new PythonManagedTypeValue("cached_property")
        {
            Module = Module,
            QualName = "cached_property",
        };
        type.Attributes["__new__"] = new PythonProtocolFunctionValue(
            "__new__",
            (_, arguments) => NewCachedProperty(type, arguments, [], []),
            (_, arguments, names, values) => NewCachedProperty(type, arguments, names, values)
        );
        type.Attributes["__get__"] = new PythonProtocolFunctionValue(
            "__get__",
            (_, arguments) => GetCachedProperty(arguments[0], arguments[1], default),
            (_, arguments, _, _) => GetCachedProperty(arguments[0], arguments[1], default)
        );
        type.Attributes["__set_name__"] = new PythonProtocolFunctionValue(
            "__set_name__",
            (self, arguments) =>
            {
                SetCachedPropertyName(self, arguments[1]);
                return PythonNoneValue.Instance;
            },
            (self, arguments, _, _) =>
            {
                SetCachedPropertyName(self, arguments[1]);
                return PythonNoneValue.Instance;
            }
        );
        return type;
    }

    private static PythonManagedObjectValue NewCachedProperty(
        PythonManagedTypeValue type,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var arguments = positional;
        if (
            positional.Count > 0
            && positional[0] is PythonManagedTypeValue leading
            && ReferenceEquals(leading, type)
        )
        {
            arguments = [.. positional.Skip(1)];
        }

        var bound = BindNamed(
            "cached_property",
            arguments,
            keywordNames,
            keywordValues,
            ["func"],
            [null],
            default
        );
        var function = bound[0]!;
        if (!ManagedObjectProtocols.IsCallable(function))
        {
            throw Error("cached_property() argument must be callable", default);
        }

        // `func`, `attrname` and `__doc__` are plain instance attributes, exactly as
        // CPython's `__init__` leaves them: writable, and `attrname` starts as None.
        var property = new PythonManagedObjectValue(type);
        property.Attributes["func"] = function;
        property.Attributes["attrname"] = PythonNoneValue.Instance;
        property.Attributes["__doc__"] = TryGetAttribute(function, "__doc__", out var doc)
            ? doc
            : PythonNoneValue.Instance;
        return property;
    }

    private static void SetCachedPropertyName(PythonValue? self, PythonValue name)
    {
        if (self is not PythonManagedObjectValue property)
        {
            throw Error("descriptor 'cached_property' requires a cached_property object", default);
        }

        property.Attributes["attrname"] = name;
    }

    /// <summary>
    /// <c>cached_property.__get__</c>: the first access computes the value and writes it
    /// into the instance dictionary, where later lookups find it without the descriptor
    /// running — it is deliberately not a data descriptor.
    /// </summary>
    private static PythonValue GetCachedProperty(
        PythonValue self,
        PythonValue instance,
        TextSpan span
    )
    {
        if (instance is PythonNoneValue)
        {
            return self;
        }

        var name = ManagedObjectProtocols.GetAttribute(self, "attrname", span);
        if (name is not PythonTextValue attributeName)
        {
            throw Error(
                "Cannot use cached_property instance without calling __set_name__ on it.",
                span
            );
        }

        if (!TryGetAttribute(instance, "__dict__", out _))
        {
            throw Error(
                $"No '__dict__' attribute on "
                    + $"'{ManagedObjectProtocols.GetTypeName(instance)}' instance to cache "
                    + $"'{attributeName.Value}' property.",
                span
            );
        }

        var function = ManagedObjectProtocols.GetAttribute(self, "func", span);
        var value = Invoke(function, [instance], span);
        ManagedObjectProtocols.SetAttribute(instance, attributeName.Value, value, span);
        return value;
    }

    // -------------------------------------------------------------------------
    // Shared helpers
    // -------------------------------------------------------------------------

    /// <summary>`functools.reduce` and friends share this call fixture.</summary>
    private static PythonTupleValue DefaultAssignments() =>
        new([.. WrapperAssignments.Select(name => (PythonValue)new PythonTextValue(name))]);

    private static PythonTupleValue DefaultUpdates() => new([new PythonTextValue("__dict__")]);

    /// <summary>Adds or replaces a keyword entry in a partial's stored dictionary.</summary>
    private static void SetKeyword(PythonDictionaryValue keywords, string name, PythonValue value)
    {
        var key = new PythonTextValue(name);
        foreach (var item in keywords.Items)
        {
            if (item.Key is PythonTextValue existing && existing.Value == name)
            {
                item.Value = value;
                return;
            }
        }

        keywords.AddItem(new PythonDictionaryItemValue(key, value));
    }

    /// <summary>Reads an attribute, reporting a miss instead of raising.</summary>
    private static bool TryGetAttribute(PythonValue target, string name, out PythonValue value)
    {
        try
        {
            value = ManagedObjectProtocols.GetAttribute(target, name, default);
            return true;
        }
        catch (Exception error) when (IsAttributeError(error))
        {
            value = PythonNoneValue.Instance;
            return false;
        }
    }

    private static bool IsAttributeError(Exception error) =>
        PythonNamespaceMapping.IsPythonException(error, "AttributeError");

    /// <summary>Applies `destination.update(source)` for a mapping argument.</summary>
    private static void UpdateDictionary(PythonValue destination, PythonValue source, TextSpan span)
    {
        if (destination is PythonDictionaryValue target && source is PythonDictionaryValue items)
        {
            // `dict.update` semantics for the `WRAPPER_UPDATES` default of `__dict__`.
            ManagedObjectProtocols.MergeDictionary(target, items, span);
            return;
        }

        if (ManagedObjectProtocols.TryGetSpecialMethod(destination, "update", out var update))
        {
            Invoke(update, [source], span);
            return;
        }

        throw Error(
            $"'{ManagedObjectProtocols.GetTypeName(destination)}' object has no attribute 'update'",
            span
        );
    }

    private static IEnumerable<PythonValue> SequenceOf(PythonValue value, TextSpan span)
    {
        var iterator = ManagedObjectProtocols.GetIterator(value, span);
        while (ManagedObjectProtocols.TryGetNext(iterator, out var item, span))
        {
            yield return item;
        }
    }

    private static string RequireText(PythonValue value, string function, TextSpan span) =>
        value is PythonTextValue text
            ? text.Value
            : throw Error(
                $"{function}() argument names must be strings, not "
                    + $"'{ManagedObjectProtocols.GetTypeName(value)}'",
                span
            );

    /// <summary>
    /// Binds a positional-or-keyword signature with defaults, mirroring
    /// <c>PyArg_ParseTupleAndKeywords</c>: too many positionals and unexpected or
    /// repeated keywords all report through the same messages.
    /// </summary>
    private static PythonValue?[] BindNamed(
        string function,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        string[] parameters,
        PythonValue?[] defaults,
        TextSpan span
    )
    {
        var bound = new PythonValue?[parameters.Length];
        if (positional.Count > parameters.Length)
        {
            throw Error(
                $"{function}() takes {DescriptiveArity(parameters.Length)} but {positional.Count} "
                    + "were given",
                span
            );
        }

        for (var index = 0; index < positional.Count; index++)
        {
            bound[index] = positional[index];
        }

        for (var index = 0; index < keywordNames.Count; index++)
        {
            var slot = Array.IndexOf(parameters, keywordNames[index]);
            if (slot < 0)
            {
                throw Error(
                    $"{function}() got an unexpected keyword argument '{keywordNames[index]}'",
                    span
                );
            }

            if (bound[slot] is not null)
            {
                throw Error(
                    $"{function}() got multiple values for argument '{keywordNames[index]}'",
                    span
                );
            }

            bound[slot] = keywordValues[index];
        }

        for (var index = 0; index < parameters.Length; index++)
        {
            if (bound[index] is null && defaults[index] is { } only)
            {
                bound[index] = only;
            }
        }

        List<string> missing = [];
        for (var index = 0; index < parameters.Length; index++)
        {
            if (bound[index] is null)
            {
                missing.Add(parameters[index]);
            }
        }

        if (missing.Count > 0)
        {
            throw Error($"{function}() {MissingArguments(missing)}", span);
        }

        return bound;
    }

    /// <summary>CPython's own wording for an unfilled required parameter list.</summary>
    private static string MissingArguments(List<string> missing)
    {
        if (missing.Count == 1)
        {
            return $"missing 1 required positional argument: '{missing[0]}'";
        }

        // CPython separates the last two names with ", and" once three or more are
        // missing: "missing 3 required positional arguments: 'a', 'b', and 'c'".
        var quoted = missing.Select(name => $"'{name}'").ToList();
        var joiner = quoted.Count > 2 ? ", and " : " and ";
        return $"missing {missing.Count} required positional arguments: "
            + string.Join(", ", quoted.Take(quoted.Count - 1))
            + joiner
            + quoted[^1];
    }

    private static string DescriptiveArity(int count) =>
        count switch
        {
            1 => "1 positional argument",
            _ => $"{count} positional arguments",
        };

    private static PythonValue Invoke(
        PythonValue callable,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    ) => Invoke(callable, arguments, [], [], span);

    // The forwarded lists are interface-typed where they come from the builtin
    // delegate and concrete List<> where this module builds them; the shared
    // interface keeps one call path for both.
#pragma warning disable CA1859
    private static PythonValue Invoke(
        PythonValue callable,
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
#pragma warning restore CA1859
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

        if (keywordNames.Count == 0)
        {
            return ManagedObjectProtocols.Call(callable, arguments, span);
        }

        throw ManagedObjectProtocols.Fault(
            "DPY4009",
            "Keyword arguments are not supported for this callable in this runtime slice.",
            span,
            "TypeError"
        );
    }

    private static PythonRuntimeException Error(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4003", message, span, "TypeError");
}
