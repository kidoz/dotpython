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
        globals.SetValue("total_ordering", CreateTotalOrdering());
        globals.SetValue("cmp_to_key", CreateCmpToKey());
        globals.SetValue("singledispatch", CreateSingleDispatch());
        globals.SetValue("singledispatchmethod", SingleDispatchMethodType);
        globals.SetValue("partialmethod", PartialMethodType);
        // CPython 3.14's own `__all__`; `Placeholder` is not part of this slice.
        globals.SetValue(
            "__all__",
            new PythonTupleValue([
                new PythonTextValue("update_wrapper"),
                new PythonTextValue("wraps"),
                new PythonTextValue("WRAPPER_ASSIGNMENTS"),
                new PythonTextValue("WRAPPER_UPDATES"),
                new PythonTextValue("total_ordering"),
                new PythonTextValue("cache"),
                new PythonTextValue("cmp_to_key"),
                new PythonTextValue("lru_cache"),
                new PythonTextValue("reduce"),
                new PythonTextValue("partial"),
                new PythonTextValue("partialmethod"),
                new PythonTextValue("singledispatch"),
                new PythonTextValue("singledispatchmethod"),
                new PythonTextValue("cached_property"),
            ])
        );
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
            (self, arguments) => GetCachedPropertySlot(self, arguments, default),
            (self, arguments, _, _) => GetCachedPropertySlot(self, arguments, default)
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
            "cached_property.__init__",
            arguments,
            keywordNames,
            keywordValues,
            ["func"],
            [null],
            default
        );
        var function = bound[0]!;

        // `func`, `attrname`, `__doc__` and `__module__` are plain instance attributes,
        // exactly as CPython's `__init__` leaves them: writable, `attrname` starts as
        // None, and nothing checks that the argument is callable — a value without a
        // `__module__` fails on the attribute read itself.
        var property = new PythonManagedObjectValue(type);
        property.Attributes["func"] = function;
        property.Attributes["attrname"] = PythonNoneValue.Instance;
        property.Attributes["__doc__"] = TryGetAttribute(function, "__doc__", out var doc)
            ? doc
            : PythonNoneValue.Instance;
        property.Attributes["__module__"] = ModuleOfCachedFunction(function);
        return property;
    }

    /// <summary>
    /// CPython's `__init__` copies `__module__` off the wrapped callable. Builtin
    /// functions in this runtime carry no `__module__` (CPython's carry `'builtins'`),
    /// so a callable that has none yields None rather than failing; anything else
    /// without the attribute fails with the attribute error, exactly as CPython's
    /// `self.__module__ = func.__module__` does.
    /// </summary>
    private static PythonValue ModuleOfCachedFunction(PythonValue function)
    {
        if (TryGetAttribute(function, "__module__", out var module))
        {
            return module;
        }

        if (ManagedObjectProtocols.IsCallable(function))
        {
            return PythonNoneValue.Instance;
        }

        return ManagedObjectProtocols.GetAttribute(function, "__module__", default);
    }

    private static void SetCachedPropertyName(PythonValue? self, PythonValue name)
    {
        if (self is not PythonManagedObjectValue property)
        {
            throw Error("descriptor 'cached_property' requires a cached_property object", default);
        }

        if (
            !property.Attributes.TryGetValue("attrname", out var current)
            || current is PythonNoneValue
        )
        {
            property.Attributes["attrname"] = name;
            return;
        }

        if (ManagedObjectProtocols.AreEqual(current, name))
        {
            return;
        }

        throw Error(
            "Cannot assign the same cached_property to two different names "
                + $"({current.ToRepresentationString()} and {name.ToRepresentationString()}).",
            default
        );
    }

    /// <summary>
    /// The <c>__get__</c> entry point: the runtime's descriptor machinery invokes the slot
    /// unbound, as <c>(value, instance, owner)</c>, while an explicit
    /// <c>descriptor.__get__(instance, owner)</c> call arrives bound with the descriptor
    /// as the receiver.
    /// </summary>
    private static PythonValue GetCachedPropertySlot(
        PythonValue? self,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count >= 3)
        {
            return GetCachedProperty(arguments[0], arguments[1], span);
        }

        var bound = BindNamed(
            "cached_property.__get__",
            arguments,
            [],
            [],
            ["instance", "owner"],
            [null, null],
            span
        );
        if (self is null)
        {
            return GetCachedProperty(arguments[0], arguments[1], span);
        }

        return GetCachedProperty(self, bound[0]!, span);
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
    // total_ordering
    // -------------------------------------------------------------------------

    private static PythonBuiltinFunctionValue CreateTotalOrdering() =>
        new PythonBuiltinFunctionValue(
            "total_ordering",
            (arguments, span) => TotalOrdering(arguments, [], [], span),
            (arguments, names, values, span) => TotalOrdering(arguments, names, values, span)
        );

    /// <summary>The four ordering operations, in CPython's `max()` preference order.</summary>
    private static readonly string[] OrderingOperations = ["__lt__", "__le__", "__gt__", "__ge__"];

    /// <summary>
    /// CPython's `_convert`: the operations derived from each root, and the expression each
    /// is derived with.
    /// </summary>
    private static readonly Dictionary<
        string,
        (string Name, OrderingDerivation Derivation)[]
    > OrderingConversions = new(StringComparer.Ordinal)
    {
        ["__lt__"] =
        [
            ("__gt__", OrderingDerivation.NegateAndNotEqual),
            ("__le__", OrderingDerivation.OrEqual),
            ("__ge__", OrderingDerivation.Negate),
        ],
        ["__le__"] =
        [
            ("__ge__", OrderingDerivation.NegateOrEqual),
            ("__lt__", OrderingDerivation.AndNotEqual),
            ("__gt__", OrderingDerivation.Negate),
        ],
        ["__gt__"] =
        [
            ("__lt__", OrderingDerivation.NegateAndNotEqual),
            ("__ge__", OrderingDerivation.OrEqual),
            ("__le__", OrderingDerivation.Negate),
        ],
        ["__ge__"] =
        [
            ("__le__", OrderingDerivation.NegateOrEqual),
            ("__gt__", OrderingDerivation.AndNotEqual),
            ("__lt__", OrderingDerivation.Negate),
        ],
    };

    /// <summary>How a derived comparison folds the root's result with `==`/`!=`.</summary>
    private enum OrderingDerivation
    {
        /// <summary>`not op_result`.</summary>
        Negate,

        /// <summary>`op_result or self == other`.</summary>
        OrEqual,

        /// <summary>`not op_result or self == other`.</summary>
        NegateOrEqual,

        /// <summary>`op_result and self != other`.</summary>
        AndNotEqual,

        /// <summary>`not op_result and self != other`.</summary>
        NegateAndNotEqual,
    }

    /// <summary>
    /// `total_ordering(cls)`: derives the ordering comparisons the class does not define
    /// from the strongest one it does.
    /// </summary>
    private static PythonValue TotalOrdering(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var cls = BindNamed(
            "total_ordering",
            positional,
            keywordNames,
            keywordValues,
            ["cls"],
            [null],
            span
        )[0]!;
        var roots = OrderingOperations
            .Where(operation => DefinesOrderingOperation(cls, operation))
            .ToList();
        if (roots.Count == 0)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "must define at least one ordering operation: < > <= >=",
                span,
                "ValueError"
            );
        }

        // CPython's `max(roots)` prefers `__lt__` to `__le__` to `__gt__` to `__ge__`,
        // which is the order `OrderingOperations` is already written in.
        var root = roots[0];
        foreach (var (name, derivation) in OrderingConversions[root])
        {
            if (!roots.Contains(name))
            {
                ManagedObjectProtocols.SetAttribute(
                    cls,
                    name,
                    CreateOrderingOperation(name, root, derivation),
                    span
                );
            }
        }

        return cls;
    }

    /// <summary>
    /// Whether <paramref name="cls"/> supplies an ordering comparison itself: CPython
    /// compares `getattr(cls, op)` against `object`'s, which for a class means the class's
    /// own dictionaries down to `object`, and for an instance is always a bound object
    /// that cannot be `object`'s.
    /// </summary>
    private static bool DefinesOrderingOperation(PythonValue cls, string name)
    {
        if (!PythonTypeProtocols.IsType(cls))
        {
            return true;
        }

        if (cls is PythonManagedTypeValue type)
        {
            foreach (var entry in type.Mro)
            {
                if (ReferenceEquals(entry, PythonBuiltinFunctions.Object))
                {
                    break;
                }

                if (ManagedObjectProtocols.TryGetOwnTypeAttribute(entry, name, out _))
                {
                    return true;
                }
            }

            return false;
        }

        // A builtin type carries its own comparison slots; `object` itself does not.
        return !ReferenceEquals(cls, PythonBuiltinFunctions.Object);
    }

    /// <summary>
    /// One derived comparison. CPython calls the root through `type(self).__op__` rather
    /// than the operator, so a `NotImplemented` result is passed straight out instead of
    /// being retried reflectively.
    /// </summary>
    private static PythonProtocolFunctionValue CreateOrderingOperation(
        string name,
        string root,
        OrderingDerivation derivation
    ) =>
        new(
            name,
            (self, arguments) =>
            {
                if (self is not PythonManagedObjectValue instance || arguments.Count == 0)
                {
                    return PythonNotImplementedValue.Instance;
                }

                var other = arguments[0];
                var result = InvokeOrderingRoot(instance, root, other);
                if (ReferenceEquals(result, PythonNotImplementedValue.Instance))
                {
                    return result;
                }

                var known = ManagedObjectProtocols.IsTrue(result);
                return derivation switch
                {
                    OrderingDerivation.Negate => PythonTruthValue.FromBoolean(!known),
                    OrderingDerivation.OrEqual => known
                        ? result
                        : EqualityOf(instance, other, PythonRichComparison.Equal),
                    OrderingDerivation.NegateOrEqual => known
                        ? EqualityOf(instance, other, PythonRichComparison.Equal)
                        : PythonTruthValue.True,
                    OrderingDerivation.AndNotEqual => known
                        ? EqualityOf(instance, other, PythonRichComparison.NotEqual)
                        : result,
                    _ => known
                        ? PythonTruthValue.False
                        : EqualityOf(instance, other, PythonRichComparison.NotEqual),
                };
            }
        );

    private static PythonValue InvokeOrderingRoot(
        PythonManagedObjectValue instance,
        string root,
        PythonValue other
    ) =>
        TryGetAttribute(instance.Type, root, out var method)
            ? Invoke(method, [instance, other], default)
            : PythonNotImplementedValue.Instance;

    private static PythonValue EqualityOf(
        PythonValue left,
        PythonValue right,
        PythonRichComparison comparison
    ) => ManagedObjectProtocols.RichCompareValue(left, right, comparison, default);

    // -------------------------------------------------------------------------
    // cmp_to_key
    // -------------------------------------------------------------------------

    private static PythonManagedTypeValue? _keyWrapperType;

    private static PythonManagedTypeValue KeyWrapperType =>
        _keyWrapperType ??= CreateKeyWrapperType();

    /// <summary>
    /// Where a wrapper keeps the comparison function it was built with. CPython's
    /// `KeyWrapper` is a C type with the comparison in a struct field, so no Python name
    /// reaches it; a name no identifier can spell keeps the same property here.
    /// </summary>
    private const string KeyComparison = "\0comparison";

    private static PythonBuiltinFunctionValue CreateCmpToKey() =>
        new PythonBuiltinFunctionValue(
            "cmp_to_key",
            (arguments, span) => CmpToKey(arguments, [], [], span),
            (arguments, names, values, span) => CmpToKey(arguments, names, values, span)
        );

    /// <summary>
    /// `cmp_to_key(mycmp)`: wraps the comparison in a key object. The arity wording comes
    /// from the C implementation's argument parsing, which distinguishes positional from
    /// keyword overflow.
    /// </summary>
    private static PythonManagedObjectValue CmpToKey(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    ) =>
        NewKey(
            SingleArgument("cmp_to_key", "mycmp", positional, keywordNames, keywordValues, span)
        );

    /// <summary>The prototype key: a wrapper carrying a comparison and no object yet.</summary>
    private static PythonManagedObjectValue NewKey(PythonValue comparison)
    {
        var key = new PythonManagedObjectValue(KeyWrapperType);
        key.Attributes[KeyComparison] = comparison;
        return key;
    }

    private static PythonManagedTypeValue CreateKeyWrapperType()
    {
        var type = new PythonManagedTypeValue("KeyWrapper")
        {
            Module = Module,
            QualName = "KeyWrapper",
        };
        // CPython's `K` declares `__slots__ = ['obj']`, so one declared member reproduces
        // both its attribute surface and its `no __dict__` refusal.
        type.Slots = PythonSlotLayout.Create(type, new PythonTextValue("obj"), default);
        type.Attributes["obj"] = PythonNoneValue.Instance;
        type.Attributes["__call__"] = new PythonProtocolFunctionValue(
            "__call__",
            (self, arguments) => CallKey(self, arguments, [], [], default),
            (self, arguments, names, values) => CallKey(self, arguments, names, values, default)
        );
        foreach (var (name, comparison) in KeyComparisons)
        {
            type.Attributes[name] = new PythonProtocolFunctionValue(
                name,
                (self, arguments) =>
                    CompareKeys(
                        self,
                        arguments.Count > 0 ? arguments[0] : PythonNoneValue.Instance,
                        comparison
                    )
            );
        }

        // `K.__hash__ = None`: ordering without hashing.
        type.Attributes["__hash__"] = PythonNoneValue.Instance;
        return type;
    }

    /// <summary>`K`'s six rich comparisons and the operator each applies to the comparison result.</summary>
    private static readonly (string Name, PythonRichComparison Comparison)[] KeyComparisons =
    [
        ("__lt__", PythonRichComparison.LessThan),
        ("__le__", PythonRichComparison.LessThanOrEqual),
        ("__gt__", PythonRichComparison.GreaterThan),
        ("__ge__", PythonRichComparison.GreaterThanOrEqual),
        ("__eq__", PythonRichComparison.Equal),
        ("__ne__", PythonRichComparison.NotEqual),
    ];

    /// <summary>
    /// `K(obj)`: the C type's factory call — every call makes a fresh wrapper sharing the
    /// prototype's comparison function.
    /// </summary>
    private static PythonManagedObjectValue CallKey(
        PythonValue? self,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var comparison = KeyComparisonOf(self);
        var obj = SingleArgument("K", "obj", positional, keywordNames, keywordValues, span);
        var key = new PythonManagedObjectValue(KeyWrapperType);
        key.Attributes[KeyComparison] = comparison;
        key.Attributes["obj"] = obj;
        return key;
    }

    private static PythonValue KeyComparisonOf(PythonValue? self) =>
        self is PythonManagedObjectValue key
        && key.Attributes.TryGetValue(KeyComparison, out var comparison)
            ? comparison
            : PythonNoneValue.Instance;

    /// <summary>
    /// One of `K`'s rich comparisons: `mycmp(self.obj, other.obj)` fed through the same
    /// operator against zero, so a comparison returning `NotImplemented` surfaces as the
    /// operator's own error.
    /// </summary>
    private static PythonValue CompareKeys(
        PythonValue? self,
        PythonValue other,
        PythonRichComparison comparison
    )
    {
        if (
            self is not PythonManagedObjectValue key
            || other is not PythonManagedObjectValue candidate
            || !ReferenceEquals(key.Type, KeyWrapperType)
            || !ReferenceEquals(candidate.Type, KeyWrapperType)
        )
        {
            throw Error("other argument must be K instance", default);
        }

        var result = Invoke(
            KeyComparisonOf(key),
            [KeyObjectOf(key), KeyObjectOf(candidate)],
            default
        );
        return ManagedObjectProtocols.RichCompareValue(
            result,
            new PythonWholeNumberValue(0),
            comparison,
            default
        );
    }

    /// <summary>
    /// `self.obj`, which for a wrapper that was never called is absent rather than `None`;
    /// the C type reports that miss as a bare `AttributeError: object`.
    /// </summary>
    private static PythonValue KeyObjectOf(PythonManagedObjectValue key) =>
        key.Attributes.TryGetValue("obj", out var obj)
            ? obj
            : throw ManagedObjectProtocols.Fault("DPY4023", "object", default, "AttributeError");

    // -------------------------------------------------------------------------
    // singledispatch
    // -------------------------------------------------------------------------

    private static PythonManagedTypeValue? _singleDispatchType;

    private static PythonManagedTypeValue SingleDispatchType =>
        _singleDispatchType ??= CreateSingleDispatchType();

    /// <summary>
    /// What a `singledispatch` wrapper carries: the default implementation, the registry
    /// of implementations, and the per-class cache `dispatch` fills in.
    /// </summary>
    private sealed class DispatchState
    {
        internal PythonValue Function { get; init; } = PythonNoneValue.Instance;

        internal PythonDictionaryValue Registry { get; } = new([]);

        internal PythonDictionaryValue DispatchCache { get; } = new([]);
    }

    private static PythonBuiltinFunctionValue CreateSingleDispatch() =>
        new PythonBuiltinFunctionValue(
            "singledispatch",
            (arguments, span) => SingleDispatch(arguments, [], [], span),
            (arguments, names, values, span) => SingleDispatch(arguments, names, values, span)
        );

    /// <summary>
    /// `singledispatch(func)`: the default implementation is registered for `object` and
    /// the wrapper carries `register`, `dispatch`, `registry` and `_clear_cache` before
    /// `update_wrapper` copies the wrapped function's own attributes over.
    /// </summary>
    private static PythonManagedObjectValue SingleDispatch(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var func = BindNamed(
            "singledispatch",
            positional,
            keywordNames,
            keywordValues,
            ["func"],
            [null],
            span
        )[0]!;
        return WrapSingleDispatch(func, span);
    }

    private static PythonManagedObjectValue WrapSingleDispatch(PythonValue func, TextSpan span)
    {
        var state = new DispatchState { Function = func };
        ManagedObjectProtocols.SetDictionaryItem(
            state.Registry,
            PythonBuiltinFunctions.Object,
            func,
            span
        );
        var wrapper = new PythonManagedObjectValue(SingleDispatchType, state);
        wrapper.Attributes["registry"] = new PythonMappingProxyValue(state.Registry);
        UpdateWrapper([wrapper, func], [], [], span);
        return wrapper;
    }

    private static PythonManagedTypeValue CreateSingleDispatchType()
    {
        var type = new PythonManagedTypeValue("_singledispatch_wrapper")
        {
            Module = Module,
            QualName = "singledispatch_wrapper",
        };
        type.Attributes["__call__"] = new PythonProtocolFunctionValue(
            "__call__",
            (self, arguments) => InvokeDispatched(self, arguments, [], [], default),
            (self, arguments, names, values) =>
                InvokeDispatched(self, arguments, names, values, default)
        );
        type.Attributes["register"] = new PythonProtocolFunctionValue(
            "register",
            (self, arguments) => RegisterDispatched(self, arguments, [], [], default),
            (self, arguments, names, values) =>
                RegisterDispatched(self, arguments, names, values, default)
        );
        type.Attributes["dispatch"] = new PythonProtocolFunctionValue(
            "dispatch",
            (self, arguments) => DispatchDispatched(self, arguments, [], [], default),
            (self, arguments, names, values) =>
                DispatchDispatched(self, arguments, names, values, default)
        );
        type.Attributes["_clear_cache"] = new PythonProtocolFunctionValue(
            "_clear_cache",
            (self, arguments) =>
            {
                if (arguments.Count > 0)
                {
                    throw Error(
                        $"_clear_cache() takes no arguments ({arguments.Count} given)",
                        default
                    );
                }

                DispatchStateOf(self).DispatchCache.ClearItems();
                return PythonNoneValue.Instance;
            }
        );
        return type;
    }

    /// <summary>`wrapper(*args, **kwargs)`: dispatch on the first argument's class.</summary>
    private static PythonValue InvokeDispatched(
        PythonValue? self,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (positional.Count == 0)
        {
            throw Error(
                $"{FunctionNameOf(DispatchStateOf(self).Function, "singledispatch function")} "
                    + "requires at least 1 positional argument",
                span
            );
        }

        var implementation = ImplementationFor(
            self,
            PythonBuiltinTypes.GetRuntimeType(positional[0]),
            span
        );
        return Invoke(implementation, positional, keywordNames, keywordValues, span);
    }

    /// <summary>`dispatch(cls)`: the registry's best match, cached per class.</summary>
    private static PythonValue DispatchDispatched(
        PythonValue? self,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var bound = BindNamed(
            "singledispatch.<locals>.dispatch",
            positional,
            keywordNames,
            keywordValues,
            ["cls"],
            [null],
            span
        );
        return ImplementationFor(self, bound[0]!, span);
    }

    private static PythonValue ImplementationFor(
        PythonValue? wrapper,
        PythonValue cls,
        TextSpan span
    )
    {
        var state = DispatchStateOf(wrapper);
        if (ManagedObjectProtocols.TryFindDictionaryItem(state.DispatchCache, cls, out var cached))
        {
            return cached.Value;
        }

        var implementation = ManagedObjectProtocols.TryFindDictionaryItem(
            state.Registry,
            cls,
            out var direct
        )
            ? direct.Value
            : FindImplementation(cls, state.Registry, span);
        ManagedObjectProtocols.SetDictionaryItem(state.DispatchCache, cls, implementation, span);
        return implementation;
    }

    /// <summary>
    /// `_find_impl`: the first registry class found walking the class's MRO. CPython folds
    /// abstract bases into that MRO first; this runtime has no `abc`, so the MRO itself is
    /// the whole search.
    /// </summary>
    private static PythonValue FindImplementation(
        PythonValue cls,
        PythonDictionaryValue registry,
        TextSpan span
    )
    {
        if (!PythonTypeProtocols.IsType(cls))
        {
            // CPython keys its dispatch cache by weak reference, which is where a
            // non-class argument fails first; this runtime has no weak references.
            throw Error(
                $"cannot create weak reference to "
                    + $"'{ManagedObjectProtocols.GetTypeName(cls)}' object",
                span
            );
        }

        foreach (var entry in PythonBuiltinTypes.GetMro(cls).Elements)
        {
            if (ManagedObjectProtocols.TryFindDictionaryItem(registry, entry, out var match))
            {
                return match.Value;
            }
        }

        // Every registry holds `object` and every MRO ends with it, so this is unreachable.
        return PythonNoneValue.Instance;
    }

    /// <summary>
    /// `register(cls, func=None)`: records an implementation for a class or a union of
    /// classes, or returns the decorator when no implementation was supplied.
    /// </summary>
    private static PythonValue RegisterDispatched(
        PythonValue? self,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        // `func` defaults to None, which is what makes `@fun.register` reachable: the
        // decorated function arrives as `cls`.
        var bound = BindNamed(
            "singledispatch.<locals>.register",
            positional,
            keywordNames,
            keywordValues,
            ["cls", "func"],
            [null, PythonNoneValue.Instance],
            span
        );
        var func = bound[1] is PythonNoneValue ? null : bound[1];
        return RegisterImplementation(self, bound[0]!, func, span);
    }

    private static PythonValue RegisterImplementation(
        PythonValue? wrapper,
        PythonValue cls,
        PythonValue? func,
        TextSpan span
    )
    {
        if (IsDispatchType(cls))
        {
            if (func is null)
            {
                // `lambda f: register(cls, f)`, the decorator form.
                return new PythonBuiltinFunctionValue(
                    "<lambda>",
                    (arguments, inner) =>
                    {
                        if (arguments.Count != 1)
                        {
                            throw Error(
                                "<lambda>() takes 1 positional argument "
                                    + $"but {arguments.Count} were given",
                                inner
                            );
                        }

                        return RegisterImplementation(wrapper, cls, arguments[0], inner);
                    }
                );
            }
        }
        else
        {
            if (func is not null)
            {
                throw Error(
                    $"Invalid first argument to `register()`. {cls.ToRepresentationString()} "
                        + "is not a class or union type.",
                    span
                );
            }

            // `register` on an annotated function reads the class from the first
            // annotation. This runtime keeps annotations unresolved, so a string
            // annotation reports the invalid-annotation error CPython reserves for a
            // value that is not a class.
            var annotation = FirstAnnotation(cls);
            if (annotation is not { } declared)
            {
                throw Error(
                    $"Invalid first argument to `register()`: {cls.ToRepresentationString()}. "
                        + "Use either `@register(some_class)` or plain `@register` on an "
                        + "annotated function.",
                    span
                );
            }

            func = cls;
            if (!IsDispatchType(declared.Value))
            {
                throw Error(
                    $"Invalid annotation for '{declared.Name}'. "
                        + $"{declared.Value.ToRepresentationString()} is not a class.",
                    span
                );
            }

            cls = declared.Value;
        }

        var registry = DispatchStateOf(wrapper).Registry;
        if (cls is PythonTypeUnionValue union)
        {
            foreach (var member in union.Members)
            {
                ManagedObjectProtocols.SetDictionaryItem(registry, member, func!, span);
            }
        }
        else
        {
            ManagedObjectProtocols.SetDictionaryItem(registry, cls, func!, span);
        }

        // CPython clears the dispatch cache after every registration.
        DispatchStateOf(wrapper).DispatchCache.ClearItems();
        return func!;
    }

    /// <summary>
    /// The first item of a function's annotations, which CPython resolves through
    /// `get_type_hints` and this runtime takes as written. Null when there are none, which
    /// is the case that reports `Invalid first argument`.
    /// </summary>
    private static (string Name, PythonValue Value)? FirstAnnotation(PythonValue func)
    {
        if (
            TryGetAttribute(func, "__annotations__", out var annotations)
            && annotations is PythonDictionaryValue dictionary
            && dictionary.Items.Count > 0
        )
        {
            var first = dictionary.Items[0];
            return (RequireText(first.Key, "register", default), first.Value);
        }

        return null;
    }

    /// <summary>Whether a first argument can be registered: a class, or a union of classes.</summary>
    private static bool IsDispatchType(PythonValue cls) =>
        PythonTypeProtocols.IsType(cls)
        || cls is PythonTypeUnionValue union && union.Members.All(PythonTypeProtocols.IsType);

    private static DispatchState DispatchStateOf(PythonValue? target) =>
        target is PythonManagedObjectValue { Payload: DispatchState state }
            ? state
            : throw Error(
                "descriptor 'singledispatch' requires a 'singledispatch' object",
                default
            );

    private static string FunctionNameOf(PythonValue function, string fallback) =>
        TryGetAttribute(function, "__name__", out var name) && name is PythonTextValue text
            ? text.Value
            : fallback;

    // -------------------------------------------------------------------------
    // singledispatchmethod
    // -------------------------------------------------------------------------

    private static PythonManagedTypeValue? _singleDispatchMethodType;
    private static PythonManagedTypeValue? _singleDispatchMethodGetType;

    private static PythonManagedTypeValue SingleDispatchMethodType =>
        _singleDispatchMethodType ??= CreateSingleDispatchMethodType();

    private static PythonManagedTypeValue SingleDispatchMethodGetType =>
        _singleDispatchMethodGetType ??= CreateSingleDispatchMethodGetType();

    /// <summary>A descriptor's state: the generic function and the function it wraps.</summary>
    private sealed class DispatchMethodState
    {
        internal DispatchMethodState(PythonValue dispatcher, PythonValue function)
        {
            Dispatcher = dispatcher;
            Function = function;
        }

        internal PythonValue Dispatcher { get; }

        internal PythonValue Function { get; }
    }

    /// <summary>The state of one attribute access: the descriptor, the instance and the owner.</summary>
    private sealed class DispatchMethodGetState
    {
        internal DispatchMethodGetState(
            DispatchMethodState unbound,
            PythonValue owner,
            PythonValue ownerClass
        )
        {
            Unbound = unbound;
            Object = owner;
            Class = ownerClass;
        }

        internal DispatchMethodState Unbound { get; }

        internal PythonValue Object { get; }

        internal PythonValue Class { get; }
    }

    private static PythonManagedTypeValue CreateSingleDispatchMethodType()
    {
        var type = new PythonManagedTypeValue("singledispatchmethod")
        {
            Module = Module,
            QualName = "singledispatchmethod",
        };
        type.Attributes["__new__"] = new PythonProtocolFunctionValue(
            "__new__",
            (_, arguments) => NewSingleDispatchMethod(type, arguments, [], []),
            (_, arguments, names, values) => NewSingleDispatchMethod(type, arguments, names, values)
        );
        type.Attributes["__get__"] = new PythonProtocolFunctionValue(
            "__get__",
            (_, arguments) => BindDispatchedMethod(arguments[0], arguments[1], arguments[2]),
            (_, arguments, _, _) => BindDispatchedMethod(arguments[0], arguments[1], arguments[2])
        );
        type.Attributes["register"] = new PythonProtocolFunctionValue(
            "register",
            (self, arguments) => RegisterDispatchedMethod(self, arguments, [], [], default),
            (self, arguments, names, values) =>
                RegisterDispatchedMethod(self, arguments, names, values, default)
        );
        type.Attributes["__isabstractmethod__"] = ReadOnlyMember(
            "__isabstractmethod__",
            self => DelegateToWrappedFunction(self, "__isabstractmethod__", PythonTruthValue.False)
        );
        type.Attributes["__repr__"] = new PythonProtocolFunctionValue(
            "__repr__",
            (self, _) =>
                new PythonTextValue(
                    $"<single dispatch method descriptor {DispatchMethodName(DispatchMethodStateOf(self).Function)}>"
                )
        );
        return type;
    }

    private static PythonManagedObjectValue NewSingleDispatchMethod(
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
            "singledispatchmethod.__init__",
            arguments,
            keywordNames,
            keywordValues,
            ["func"],
            [null],
            default
        );
        var func = bound[0]!;
        if (!ManagedObjectProtocols.IsCallable(func) && !TryGetAttribute(func, "__get__", out _))
        {
            throw Error(
                $"{func.ToRepresentationString()} is not callable or a descriptor",
                default
            );
        }

        var dispatcher = WrapSingleDispatch(func, default);
        var descriptor = new PythonManagedObjectValue(
            type,
            new DispatchMethodState(dispatcher, func)
        );
        descriptor.Attributes["dispatcher"] = dispatcher;
        descriptor.Attributes["func"] = func;
        return descriptor;
    }

    /// <summary>`_singledispatchmethod_get`: what one attribute access resolves to.</summary>
    private static PythonManagedObjectValue BindDispatchedMethod(
        PythonValue self,
        PythonValue instance,
        PythonValue ownerClass
    )
    {
        var bound = new PythonManagedObjectValue(
            SingleDispatchMethodGetType,
            new DispatchMethodGetState(DispatchMethodStateOf(self), instance, ownerClass)
        );
        bound.Attributes["_unbound"] = self;
        bound.Attributes["_obj"] = instance;
        bound.Attributes["_cls"] = ownerClass;
        return bound;
    }

    private static PythonManagedTypeValue CreateSingleDispatchMethodGetType()
    {
        var type = new PythonManagedTypeValue("_singledispatchmethod_get")
        {
            Module = Module,
            QualName = "singledispatchmethod_get",
        };
        type.Attributes["__call__"] = new PythonProtocolFunctionValue(
            "__call__",
            (self, arguments) => CallDispatchedMethod(self, arguments, [], [], default),
            (self, arguments, names, values) =>
                CallDispatchedMethod(self, arguments, names, values, default)
        );
        type.Attributes["__repr__"] = new PythonProtocolFunctionValue(
            "__repr__",
            (self, _) => new PythonTextValue(RepresentBoundMethod(self))
        );
        type.Attributes["register"] = new PythonProtocolFunctionValue(
            "register",
            (self, arguments) =>
                RegisterMethodImplementation(
                    DispatchMethodGetStateOf(self).Unbound,
                    arguments,
                    [],
                    [],
                    default
                ),
            (self, arguments, names, values) =>
                RegisterMethodImplementation(
                    DispatchMethodGetStateOf(self).Unbound,
                    arguments,
                    names,
                    values,
                    default
                )
        );
        // CPython resolves these lazily through `__getattr__`; the same values are
        // ordinary members here.
        type.Attributes["__wrapped__"] = ReadOnlyMember(
            "__wrapped__",
            self => DispatchMethodGetStateOf(self).Unbound.Function
        );
        type.Attributes["__name__"] = ReadOnlyMember(
            "__name__",
            self => DelegateToWrappedFunctionStrict(self, "__name__")
        );
        type.Attributes["__qualname__"] = ReadOnlyMember(
            "__qualname__",
            self => DelegateToWrappedFunctionStrict(self, "__qualname__")
        );
        type.Attributes["__isabstractmethod__"] = ReadOnlyMember(
            "__isabstractmethod__",
            self => DelegateToWrappedFunctionStrict(self, "__isabstractmethod__")
        );
        return type;
    }

    /// <summary>
    /// `_singledispatchmethod_get.__call__`: dispatch on the first argument and bind the
    /// implementation the way the wrapped descriptor would bind it.
    /// </summary>
    private static PythonValue CallDispatchedMethod(
        PythonValue? self,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var state = DispatchMethodGetStateOf(self);
        if (positional.Count == 0)
        {
            throw Error(
                $"{FunctionNameOf(state.Unbound.Function, "singledispatchmethod method")} "
                    + "requires at least 1 positional argument",
                span
            );
        }

        var implementation = ImplementationFor(
            state.Unbound.Dispatcher,
            PythonBuiltinTypes.GetRuntimeType(positional[0]),
            span
        );
        // CPython calls `implementation.__get__(self._obj, self._cls)`; a class-level
        // access binds nothing, which for this runtime means a C# `null` instance rather
        // than the Python `None` the state holds.
        var target = ManagedObjectProtocols.BindDescriptor(
            implementation,
            state.Object is PythonNoneValue ? null : state.Object,
            state.Class,
            span
        );
        return Invoke(target, positional, keywordNames, keywordValues, span);
    }

    private static PythonValue RegisterDispatchedMethod(
        PythonValue? descriptor,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    ) =>
        RegisterMethodImplementation(
            DispatchMethodStateOf(descriptor),
            positional,
            keywordNames,
            keywordValues,
            span
        );

    private static PythonValue RegisterMethodImplementation(
        DispatchMethodState state,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        // `method` defaults to None, so `@meth.register` sends the decorated function
        // through the annotation path exactly as the dispatcher's own `register` does.
        var bound = BindNamed(
            "register",
            positional,
            keywordNames,
            keywordValues,
            ["cls", "method"],
            [null, PythonNoneValue.Instance],
            span
        );
        return RegisterImplementation(
            state.Dispatcher,
            bound[0]!,
            bound[1] is PythonNoneValue ? null : bound[1],
            span
        );
    }

    private static string RepresentBoundMethod(PythonValue? self)
    {
        var state = DispatchMethodGetStateOf(self);
        var name = DispatchMethodName(state.Unbound.Function);
        return state.Object is PythonNoneValue
            ? $"<single dispatch method {name}>"
            : $"<bound single dispatch method {name} of {state.Object.ToRepresentationString()}>";
    }

    private static string DispatchMethodName(PythonValue function)
    {
        if (
            TryGetAttribute(function, "__qualname__", out var qualname)
            && qualname is PythonTextValue qualified
        )
        {
            return qualified.Value;
        }

        return FunctionNameOf(function, "?");
    }

    private static PythonValue DelegateToWrappedFunction(
        PythonValue self,
        string name,
        PythonValue fallback
    )
    {
        var function = DispatchMethodGetStateOf(self).Unbound.Function;
        return TryGetAttribute(function, name, out var value) ? value : fallback;
    }

    /// <summary>
    /// The same delegation without a fallback: `_singledispatchmethod_get.__getattr__` is a
    /// plain `getattr`, so an attribute the wrapped function does not have raises.
    /// </summary>
    private static PythonValue DelegateToWrappedFunctionStrict(PythonValue self, string name) =>
        ManagedObjectProtocols.GetAttribute(
            DispatchMethodGetStateOf(self).Unbound.Function,
            name,
            default
        );

    private static DispatchMethodState DispatchMethodStateOf(PythonValue? target) =>
        target is PythonManagedObjectValue { Payload: DispatchMethodState state }
            ? state
            : throw Error(
                "descriptor 'singledispatchmethod' requires a 'singledispatchmethod' object",
                default
            );

    private static DispatchMethodGetState DispatchMethodGetStateOf(PythonValue? target) =>
        target is PythonManagedObjectValue { Payload: DispatchMethodGetState state }
            ? state
            : throw Error(
                "descriptor '_singledispatchmethod_get' requires a bound dispatch method",
                default
            );

    /// <summary>
    /// Argument Clinic's parse for the module's single-argument factories: `mycmp`/`obj` is
    /// positional-or-keyword, exactly one value is accepted, and an overflow that is all
    /// keywords reports differently from one that includes a positional.
    /// </summary>
    private static PythonValue SingleArgument(
        string function,
        string parameter,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var given = positional.Count + keywordValues.Count;
        if (given > 1)
        {
            throw Error(
                positional.Count == 0
                    ? $"{function}() takes at most 1 keyword argument ({given} given)"
                    : $"{function}() takes at most 1 argument ({given} given)",
                span
            );
        }

        if (positional.Count == 1)
        {
            return positional[0];
        }

        if (keywordNames.Count == 1 && keywordNames[0] == parameter)
        {
            return keywordValues[0];
        }

        throw Error($"{function}() missing required argument '{parameter}' (pos 1)", span);
    }

    // -------------------------------------------------------------------------
    // partialmethod
    // -------------------------------------------------------------------------

    private static PythonManagedTypeValue? _partialMethodType;

    private static PythonManagedTypeValue PartialMethodType =>
        _partialMethodType ??= CreatePartialMethodType();

    private static PythonManagedTypeValue CreatePartialMethodType()
    {
        var type = new PythonManagedTypeValue("partialmethod")
        {
            Module = Module,
            QualName = "partialmethod",
        };
        type.Attributes["__new__"] = new PythonProtocolFunctionValue(
            "__new__",
            (_, arguments) => NewPartialMethod(type, arguments, [], []),
            (_, arguments, names, values) => NewPartialMethod(type, arguments, names, values)
        );
        type.Attributes["__module__"] = new PythonTextValue(Module);
        // The descriptor-get slot receives the partialmethod, the instance and the
        // owner; an explicit `descriptor.__get__(obj, cls)` call arrives bound with
        // the descriptor as the receiver.
        type.Attributes["__get__"] = new PythonProtocolFunctionValue(
            "__get__",
            (self, arguments) => GetPartialMethodSlot(self, arguments, default),
            (self, arguments, _, _) => GetPartialMethodSlot(self, arguments, default)
        );
        type.Attributes["__repr__"] = new PythonProtocolFunctionValue(
            "__repr__",
            (self, _) => new PythonTextValue(RepresentPartialMethod(self))
        );
        type.Attributes["__isabstractmethod__"] = ReadOnlyMember(
            "__isabstractmethod__",
            self =>
                TryGetAttribute(
                    PartialMethodMember(PartialMethodObject(self), "func"),
                    "__isabstractmethod__",
                    out var flag
                )
                    ? flag
                    : PythonTruthValue.False
        );
        return type;
    }

    /// <summary>
    /// CPython 3.14's `_partial_new`: `func` is positional-only, the extra arguments
    /// and keywords are stored for later application, and a nested `partialmethod`
    /// is flattened the way `partial` flattens a nested `partial`.
    /// </summary>
    private static PythonManagedObjectValue NewPartialMethod(
        PythonManagedTypeValue type,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var argumentStart = 0;
        if (
            positional.Count > 0
            && positional[0] is PythonManagedTypeValue leading
            && ReferenceEquals(leading, type)
        )
        {
            argumentStart = 1;
        }

        if (positional.Count <= argumentStart)
        {
            // CPython's unfilled positional-only `func` reports the implementation
            // function's own name, and the message reads the same here.
            throw Error("_partial_new() missing 1 required positional argument: 'func'", default);
        }

        var function = positional[argumentStart];
        // `func` may be any callable, or a descriptor (`classmethod`, `staticmethod`,
        // `property`, ...) that binding turns into one.
        if (!ManagedObjectProtocols.IsCallable(function) && !HasDescriptorGet(function))
        {
            throw Error(
                $"the first argument {function.ToRepresentationString()} must be a callable "
                    + "or a descriptor",
                default
            );
        }

        argumentStart++;
        var stored = new List<PythonValue>();
        var keywords = new PythonDictionaryValue([]);
        if (
            function is PythonManagedObjectValue nested
            && ReferenceEquals(nested.Type, PartialMethodType)
        )
        {
            function = PartialMethodMember(nested, "func");
            stored.AddRange(PartialMethodArgumentList(nested));
            foreach (var item in PartialMethodKeywordDictionary(nested).Items)
            {
                SetKeyword(keywords, ((PythonTextValue)item.Key).Value, item.Value);
            }
        }

        for (var index = argumentStart; index < positional.Count; index++)
        {
            stored.Add(positional[index]);
        }

        for (var index = 0; index < keywordNames.Count; index++)
        {
            SetKeyword(keywords, keywordNames[index], keywordValues[index]);
        }

        var method = new PythonManagedObjectValue(type);
        method.Attributes["func"] = function;
        method.Attributes["args"] = new PythonTupleValue([.. stored]);
        method.Attributes["keywords"] = keywords;
        // CPython 3.14 keeps its placeholder bookkeeping on the instance; without
        // `Placeholder` support both members hold the state it starts with.
        method.Attributes["_phcount"] = new PythonWholeNumberValue(0);
        method.Attributes["_merger"] = PythonNoneValue.Instance;
        return method;
    }

    /// <summary>
    /// The <c>__get__</c> entry point: the descriptor machinery invokes the slot
    /// unbound, as <c>(value, instance, owner)</c>, while an explicit
    /// <c>descriptor.__get__(obj, cls)</c> call arrives bound with the descriptor
    /// as the receiver.
    /// </summary>
    private static PythonValue GetPartialMethodSlot(
        PythonValue? self,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count >= 3)
        {
            return GetPartialMethod(arguments[0], arguments[1], arguments[2], span);
        }

        var bound = BindNamed(
            "partialmethod.__get__",
            arguments,
            [],
            [],
            ["instance", "owner"],
            [null, null],
            span
        );
        var owner = bound[1] ?? PythonNoneValue.Instance;
        if (self is null)
        {
            return GetPartialMethod(
                arguments[0],
                arguments.Count > 1 ? arguments[1] : PythonNoneValue.Instance,
                owner,
                span
            );
        }

        return GetPartialMethod(self, bound[0]!, owner, span);
    }

    /// <summary>
    /// CPython asks the wrapped callable for a bound value first
    /// (<c>func.__get__(obj, cls)</c>) and partials it whenever that produced a new
    /// object; anything the binding leaves untouched falls back to instance-method
    /// semantics.
    /// </summary>
    private static PythonValue GetPartialMethod(
        PythonValue descriptor,
        PythonValue instance,
        PythonValue owner,
        TextSpan span
    )
    {
        var method = PartialMethodObject(descriptor);
        var function = PartialMethodMember(method, "func");
        var boundFunction = ManagedObjectProtocols.BindDescriptor(
            function,
            instance is PythonNoneValue ? null : instance,
            owner,
            span
        );

        if (!ReferenceEquals(boundFunction, function))
        {
            var arguments = new List<PythonValue> { boundFunction };
            arguments.AddRange(PartialMethodArgumentList(method));
            var names = new List<string>();
            var values = new List<PythonValue>();
            foreach (var item in PartialMethodKeywordDictionary(method).Items)
            {
                names.Add(((PythonTextValue)item.Key).Value);
                values.Add(item.Value);
            }

            var partial = NewPartial(PartialType, arguments, names, values);
            // CPython copies `__self__` off the value the descriptor returned and
            // ignores a value that has none.
            if (boundFunction is PythonBoundUserMethodValue boundUser)
            {
                partial.Attributes["__self__"] = boundUser.Target;
            }
            else if (TryGetAttribute(boundFunction, "__self__", out var boundTarget))
            {
                partial.Attributes["__self__"] = boundTarget;
            }

            return partial;
        }

        return PartialMethodMethod(method, instance);
    }

    /// <summary>
    /// CPython's <c>_make_unbound_method</c>: a callable that takes the receiver as
    /// its first argument and calls the wrapped function with the stored arguments
    /// and keywords in front of the call's own.
    /// </summary>
    private static PythonValue PartialMethodMethod(
        PythonManagedObjectValue method,
        PythonValue instance
    )
    {
        var unbound = new PythonProtocolFunctionValue(
            "_method",
            (self, arguments) => InvokePartialMethod(method, self, arguments, [], [], default),
            (self, arguments, names, values) =>
                InvokePartialMethod(method, self, arguments, names, values, default)
        );

        if (instance is PythonNoneValue)
        {
            // Class access leaves the wrapper unbound: it still takes the receiver
            // as its first positional argument.
            return unbound;
        }

        return new PythonBoundMethodValue("_method", instance, unbound);
    }

    private static PythonValue InvokePartialMethod(
        PythonManagedObjectValue method,
        PythonValue? self,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        var arguments = new List<PythonValue>();
        var callArguments = new List<PythonValue>(positional);
        if (self is null)
        {
            if (callArguments.Count == 0)
            {
                throw Error(
                    "_method() missing 1 required positional argument: 'cls_or_self'",
                    span
                );
            }

            arguments.Add(callArguments[0]);
            callArguments.RemoveAt(0);
        }
        else
        {
            arguments.Add(self);
        }

        arguments.AddRange(PartialMethodArgumentList(method));
        arguments.AddRange(callArguments);

        // CPython merges `{**self.keywords, **keywords}`: the call's keywords win.
        var keywords = PartialMethodKeywordDictionary(method);
        var mergedNames = new List<string>();
        var mergedValues = new List<PythonValue>();
        foreach (var item in keywords.Items)
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

        return Invoke(
            PartialMethodMember(method, "func"),
            arguments,
            mergedNames,
            mergedValues,
            span
        );
    }

    /// <summary>
    /// <c>functools.partialmethod(func, ...)</c>; the stored arguments follow the
    /// callable and the keywords print last, exactly as CPython's `_partial_repr`
    /// renders them.
    /// </summary>
    private static string RepresentPartialMethod(PythonValue? self)
    {
        var method = PartialMethodObject(self);
        var parts = new List<string> { Represent(PartialMethodMember(method, "func"), []) };
        parts.AddRange(
            PartialMethodArgumentList(method).Select(argument => Represent(argument, []))
        );
        parts.AddRange(
            PartialMethodKeywordDictionary(method)
                .Items.Select(item =>
                    $"{((PythonTextValue)item.Key).Value}={Represent(item.Value, [])}"
                )
        );
        return $"functools.partialmethod({string.Join(", ", parts)})";
    }

    /// <summary>
    /// Whether the wrapped value carries a <c>__get__</c>, i.e. whether
    /// <c>partialmethod</c> may wrap it even though it is not callable.
    /// </summary>
    private static bool HasDescriptorGet(PythonValue value) =>
        value
            is PythonDescriptorValue
                or PythonPropertyValue
                or PythonTypeMetadataDescriptorValue
                or PythonUnicodeErrorDescriptorValue
                or PythonStaticMethodValue
                or PythonClassMethodValue
        || TryGetAttribute(value, "__get__", out _);

    private static PythonManagedObjectValue PartialMethodObject(PythonValue? target) =>
        target is PythonManagedObjectValue method && ReferenceEquals(method.Type, PartialMethodType)
            ? method
            : throw Error(
                "descriptor 'partialmethod' requires a 'functools.partialmethod' object",
                default
            );

    private static PythonValue PartialMethodMember(PythonManagedObjectValue method, string name) =>
        method.Attributes.TryGetValue(name, out var value)
            ? value
            : throw ManagedObjectProtocols.Fault(
                "DPY4023",
                $"'partialmethod' object has no attribute '{name}'",
                default,
                "AttributeError"
            );

    private static List<PythonValue> PartialMethodArgumentList(PythonManagedObjectValue method) =>
        PartialMethodMember(method, "args") is PythonTupleValue tuple
            ? [.. tuple.Elements]
            : throw Error("partialmethod 'args' must be a tuple", default);

    private static PythonDictionaryValue PartialMethodKeywordDictionary(
        PythonManagedObjectValue method
    ) =>
        PartialMethodMember(method, "keywords") is PythonDictionaryValue keywords
            ? keywords
            : throw Error("partialmethod 'keywords' must be a dictionary", default);

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
