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
                    + $"positional arguments were given",
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

        var quoted = missing.Select(name => $"'{name}'").ToList();
        var tail = $"{quoted[^2]} and {quoted[^1]}";
        var lead = string.Join(", ", quoted.Take(quoted.Count - 2).Append(tail));
        return $"missing {missing.Count} required positional arguments: {lead}";
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
