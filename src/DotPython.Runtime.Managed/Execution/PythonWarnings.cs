using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The <c>warnings</c> module, following CPython 3.14.7 <c>_py_warnings.py</c> and the
/// <c>_warnings</c> extension the release actually installs.
/// </summary>
/// <remarks>
/// The filter list is the module's own <c>filters</c> global, read every time so that user
/// code can replace it, and a <c>catch_warnings</c> context holds a copy of it beside the
/// log it records into — which is the context-aware model 3.14 runs with, where a filter
/// installed inside the context is gone once the context is left. Message and module
/// patterns are matched by the host language's regular expressions in place of <c>re</c>,
/// anchored at the start the way <c>re.match</c> is.
/// </remarks>
internal static class PythonWarnings
{
    /// <summary>The names `__all__` publishes, in CPython's order.</summary>
    private static readonly string[] ExportedNames =
    [
        "warn",
        "warn_explicit",
        "showwarning",
        "formatwarning",
        "filterwarnings",
        "simplefilter",
        "resetwarnings",
        "catch_warnings",
        "deprecated",
    ];

    /// <summary>The actions a filter may name, with `all` standing for `always`.</summary>
    private static readonly string[] Actions =
    [
        "error",
        "ignore",
        "always",
        "all",
        "default",
        "module",
        "once",
    ];

    private sealed class Context
    {
        internal PythonListValue Filters { get; set; } = null!;

        /// <summary>The log a recording context appends to, or null when nothing records.</summary>
        internal PythonListValue? Log { get; set; }
    }

    private static PythonGlobalNamespace? _module;
    private static PythonListValue? _filters;
    private static PythonDictionaryValue? _onceRegistry;
    private static readonly List<Context> Contexts = [];
    private static int _filtersVersion;
    private static PythonManagedTypeValue? _warningMessage;
    private static PythonManagedTypeValue? _catchWarnings;
    private static PythonManagedTypeValue? _deprecated;
    private static PythonProtocolFunctionValue? _showWarning;
    private static PythonProtocolFunctionValue? _formatWarning;
    private static readonly ConditionalWeakTable<PythonManagedObjectValue, ContextState> States =
        new();

    private sealed class ContextState
    {
        internal bool Entered { get; set; }

        internal bool Record { get; set; }

        internal PythonProtocolFunctionValue? SavedShowWarning { get; set; }

        internal PythonListValue? Log { get; set; }
    }

    /// <summary>The module's names.</summary>
    internal static void Initialize(PythonGlobalNamespace globals)
    {
        _module = globals;
        globals.SetValue("__doc__", new PythonTextValue(ModuleDoc));
        globals.SetValue("__all__", new PythonListValue([.. Names()]));
        globals.SetValue("filters", Filters());
        globals.SetValue("defaultaction", new PythonTextValue("default"));
        globals.SetValue("onceregistry", OnceRegistry());
        globals.SetValue("_filters_version", PythonWholeNumberValue.Create(1));
        globals.SetValue("_lock", new PythonManagedObjectValue(ReentrantLockType));
        globals.SetValue("WarningMessage", WarningMessage);
        globals.SetValue("catch_warnings", CatchWarnings);
        globals.SetValue("deprecated", Deprecated);
        globals.SetValue("warn", Warn());
        globals.SetValue("filterwarnings", FilterWarnings());
        globals.SetValue("simplefilter", SimpleFilter());
        globals.SetValue("resetwarnings", Function("resetwarnings", ResetWarnings));
        globals.SetValue("warn_explicit", WarnExplicitFunction());
        globals.SetValue("showwarning", ShowWarningOriginal);
        globals.SetValue("formatwarning", FormatWarningOriginal);
        globals.SetValue("_showwarning_orig", ShowWarningOriginal);
        globals.SetValue("_formatwarning_orig", FormatWarningOriginal);
        globals.SetValue(
            "_showwarnmsg",
            Function("_showwarnmsg", (arguments, span) => ShowWarnMsg(Message(arguments, 0)))
        );
        globals.SetValue(
            "_showwarnmsg_impl",
            Function(
                "_showwarnmsg_impl",
                (arguments, span) => ShowWarnMsgImpl(Message(arguments, 0))
            )
        );
        globals.SetValue(
            "_formatwarnmsg",
            Function(
                "_formatwarnmsg",
                (arguments, span) => new PythonTextValue(FormatWarnMsg(Message(arguments, 0)))
            )
        );
        globals.SetValue(
            "_formatwarnmsg_impl",
            Function(
                "_formatwarnmsg_impl",
                (arguments, span) => new PythonTextValue(FormatWarnMsg(Message(arguments, 0)))
            )
        );
        globals.SetValue("_getaction", Function("_getaction", GetAction));
        globals.SetValue("_getcategory", Function("_getcategory", GetCategory));
        globals.SetValue("_add_filter", AddFilterFunction());
        globals.SetValue("_get_filters", Function("_get_filters", (_, _) => CurrentFilters()));
        globals.SetValue(
            "_filters_mutated",
            Function(
                "_filters_mutated",
                (arguments, span) =>
                {
                    Mutated();
                    return PythonNoneValue.Instance;
                }
            )
        );
        globals.SetValue(
            "_is_internal_filename",
            Function(
                "_is_internal_filename",
                (arguments, span) =>
                    IsInternalFileName(RequireText(arguments, 0, "_is_internal_filename"))
                        ? PythonTruthValue.True
                        : PythonTruthValue.False
            )
        );
        globals.SetValue(
            "_is_filename_to_skip",
            Function("_is_filename_to_skip", (arguments, span) => IsFileNameToSkip(arguments))
        );
        globals.SetValue(
            "_next_external_frame",
            Function("_next_external_frame", (arguments, span) => PythonNoneValue.Instance)
        );
        SetupDefaults();
        // The filters a fresh interpreter starts with do not count as a mutation: a
        // registry is stamped with the number of changes made since, which is why the
        // counter starts at zero while the module's own `_filters_version` stays 1.
        _filtersVersion = 0;
    }

    private static IEnumerable<PythonValue> Names() =>
        ExportedNames.Select(name => (PythonValue)new PythonTextValue(name));

    /// <summary>The filters a fresh interpreter starts with, which is `_setup_defaults`.</summary>
    private static void SetupDefaults()
    {
        AddFilter(
            "default",
            null,
            Category("DeprecationWarning"),
            new PythonTextValue("__main__"),
            0,
            append: true
        );
        AddFilter("ignore", null, Category("DeprecationWarning"), null, 0, append: true);
        AddFilter("ignore", null, Category("PendingDeprecationWarning"), null, 0, append: true);
        AddFilter("ignore", null, Category("ImportWarning"), null, 0, append: true);
        AddFilter("ignore", null, Category("ResourceWarning"), null, 0, append: true);
    }

    private static PythonExceptionTypeValue Category(string name) =>
        (PythonExceptionTypeValue)PythonBuiltinTypes.GetExceptionType(name);

    // -------------------------------------------------------------------------
    // The filter list
    // -------------------------------------------------------------------------

    /// <summary>The module's own `filters` list, which is what the module global holds.</summary>
    private static PythonListValue Filters() => _filters ??= new PythonListValue([]);

    /// <summary>
    /// The filter list in force: the module global, or the copy a `catch_warnings` context
    /// installed — read each time, so replacing `warnings.filters` is honoured.
    /// </summary>
    private static PythonListValue CurrentFilters()
    {
        if (Contexts.Count != 0)
            return Contexts[^1].Filters;
        if (_module is { } globals && globals.TryGetValue("filters", out var filters))
            return filters as PythonListValue ?? Filters();
        return new PythonListValue([]);
    }

    private static PythonDictionaryValue OnceRegistry() =>
        _onceRegistry ??= new PythonDictionaryValue([]);

    /// <summary>The registry a `warn` call uses, taken from the calling module's globals.</summary>
    private static PythonDictionaryValue RegistryFor(PythonGlobalNamespace? globals)
    {
        if (globals is null)
            return new PythonDictionaryValue([]);
        if (
            globals.TryGetValue("__warningregistry__", out var registry)
            && registry is PythonDictionaryValue existing
        )
            return existing;
        var created = new PythonDictionaryValue([]);
        globals.SetValue("__warningregistry__", created);
        return created;
    }

    private static void Mutated() => _filtersVersion++;

    /// <summary>`_add_filter`: the entry goes to the front, or to the back when appending,
    /// and a duplicate is dropped when it goes to the front.</summary>
    private static void AddFilter(
        string action,
        PythonValue? message,
        PythonValue category,
        PythonValue? module,
        int lineno,
        bool append
    )
    {
        var filters = CurrentFilters();
        var item = new PythonTupleValue([
            new PythonTextValue(action),
            message ?? PythonNoneValue.Instance,
            category,
            module ?? PythonNoneValue.Instance,
            PythonWholeNumberValue.Create(lineno),
        ]);
        if (append)
        {
            if (!filters.Elements.Any(existing => ManagedObjectProtocols.AreEqual(existing, item)))
                filters.Elements.Add(item);
        }
        else
        {
            filters.Elements.RemoveAll(existing => ManagedObjectProtocols.AreEqual(existing, item));
            filters.Elements.Insert(0, item);
        }
        Mutated();
    }

    /// <summary>`filterwarnings(action, message="", category=Warning, module="", lineno=0, append=False)`.</summary>
    private static PythonBuiltinFunctionValue FilterWarnings() =>
        new PythonBuiltinFunctionValue(
            "filterwarnings",
            (arguments, span) => FilterWarningsCall(arguments, [], [], span),
            (positional, names, values, span) => FilterWarningsCall(positional, names, values, span)
        );

    private static PythonNoneValue FilterWarningsCall(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var bound = BindCall(
            "filterwarnings",
            ["action", "message", "category", "module", "lineno", "append"],
            [
                null,
                new PythonTextValue(string.Empty),
                Category("Warning"),
                new PythonTextValue(string.Empty),
                PythonWholeNumberValue.Create(0),
                PythonTruthValue.False,
            ],
            positional,
            names,
            values,
            span
        );
        var action = RequireText(bound[0], "action", span);
        RequireAction(action);
        var message = RequireText(bound[1], "message", span);
        if (!IsWarningSubclass(bound[2]))
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "category must be a Warning subclass",
                span,
                "TypeError"
            );
        var module = RequireText(bound[3], "module", span);
        var lineno = Lineno(bound[4], "lineno", span);
        var append = ManagedObjectProtocols.IsTrue(bound[5]);
        AddFilter(
            action,
            message.Length == 0 ? null : Pattern(message, ignoreCase: true),
            bound[2],
            module.Length == 0 ? null : Pattern(module),
            lineno,
            append
        );
        return PythonNoneValue.Instance;
    }

    /// <summary>`simplefilter(action, category=Warning, lineno=0, append=False)`.</summary>
    private static PythonBuiltinFunctionValue SimpleFilter() =>
        new PythonBuiltinFunctionValue(
            "simplefilter",
            (arguments, span) => SimpleFilterCall(arguments, [], [], span),
            (positional, names, values, span) => SimpleFilterCall(positional, names, values, span)
        );

    private static PythonNoneValue SimpleFilterCall(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var bound = BindCall(
            "simplefilter",
            ["action", "category", "lineno", "append"],
            [null, Category("Warning"), PythonWholeNumberValue.Create(0), PythonTruthValue.False],
            positional,
            names,
            values,
            span
        );
        var action = RequireText(bound[0], "action", span);
        RequireAction(action);
        AddFilter(
            action,
            null,
            bound[1],
            null,
            Lineno(bound[2], "lineno", span),
            ManagedObjectProtocols.IsTrue(bound[3])
        );
        return PythonNoneValue.Instance;
    }

    /// <summary>`resetwarnings()`: the filter list emptied.</summary>
    private static PythonNoneValue ResetWarnings(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        CurrentFilters().Elements.Clear();
        Mutated();
        return PythonNoneValue.Instance;
    }

    /// <summary>The function `_add_filter(action, message, category, module, lineno, append)`.</summary>
    private static PythonBuiltinFunctionValue AddFilterFunction() =>
        new(
            "_add_filter",
            (arguments, span) => AddFilterCall(arguments, [], [], span),
            (positional, names, values, span) => AddFilterCall(positional, names, values, span)
        );

    private static PythonNoneValue AddFilterCall(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var append = false;
        for (var index = 0; index < names.Count; index++)
        {
            if (names[index] == "append")
                append = ManagedObjectProtocols.IsTrue(values[index]);
        }
        if (positional.Count != 5)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"_add_filter() takes exactly 5 positional arguments ({positional.Count} given)",
                span,
                "TypeError"
            );
        AddFilter(
            RequireText(positional[0], "_add_filter", span),
            positional[1] is PythonNoneValue ? null : positional[1],
            positional[2],
            positional[3] is PythonNoneValue ? null : positional[3],
            Lineno(positional[4], span),
            append
        );
        return PythonNoneValue.Instance;
    }

    /// <summary>`_getaction(action)`: the action an abbreviation names.</summary>
    private static PythonValue GetAction(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var action = RequireText(arguments, 0, "_getaction");
        if (action.Length == 0)
            return new PythonTextValue("default");
        foreach (
            var candidate in new[]
            {
                "default",
                "always",
                "all",
                "ignore",
                "module",
                "once",
                "error",
            }
        )
        {
            if (candidate.StartsWith(action, StringComparison.Ordinal))
                return new PythonTextValue(candidate);
        }
        throw OptionError($"invalid action: '{action}'");
    }

    /// <summary>`_getcategory(category)`: the category a name stands for.</summary>
    private static PythonValue GetCategory(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var name = RequireText(arguments, 0, "_getcategory");
        if (name.Length == 0)
            return Category("Warning");
        if (!name.Contains('.', StringComparison.Ordinal))
            return BuiltinCategory(name);
        var separator = name.LastIndexOf('.');
        var module = name[..separator];
        if (module != "builtins")
            throw OptionError($"invalid module name: '{module}'");
        return BuiltinCategory(name[(separator + 1)..]);
    }

    private static PythonValue BuiltinCategory(string name)
    {
        if (PythonVirtualMachine.GetBuiltinExceptionBase(name) is null)
            throw OptionError($"unknown warning category: '{name}'");
        return PythonBuiltinTypes.GetExceptionType(name);
    }

    private static PythonRuntimeException OptionError(string message) =>
        ManagedObjectProtocols.Fault("DPY4003", message, default, "ValueError");

    // -------------------------------------------------------------------------
    // warn and warn_explicit
    // -------------------------------------------------------------------------

    private static PythonBuiltinFunctionValue Function(
        string name,
        Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> body
    ) => new(name, body);

    private static PythonBuiltinFunctionValue Warn() =>
        new(
            "warn",
            (arguments, span) => WarnCall(arguments, [], [], span),
            (positional, names, values, span) => WarnCall(positional, names, values, span)
        );

    private static PythonNoneValue WarnCall(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var bound = BindCall(
            "warn",
            ["message", "category", "stacklevel", "source", "skip_file_prefixes"],
            [
                null,
                PythonNoneValue.Instance,
                PythonWholeNumberValue.Create(1),
                PythonNoneValue.Instance,
                PythonNoneValue.Instance,
            ],
            positional,
            names,
            values,
            span
        );
        return WarnCore(bound[0], bound[1], Lineno(bound[2], span), bound[3], bound[4], span);
    }

    private static PythonNoneValue WarnCore(
        PythonValue message,
        PythonValue category,
        int stacklevel,
        PythonValue source,
        PythonValue skipPrefixes,
        TextSpan span
    )
    {
        if (
            message is PythonExceptionValue instance
            && IsWarningSubclass(PythonBuiltinTypes.GetRuntimeType(instance))
        )
            category = PythonBuiltinTypes.GetRuntimeType(instance);
        if (category is PythonNoneValue)
            category = Category("UserWarning");
        if (!IsWarningSubclass(category))
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "category must be a Warning subclass, "
                    + $"not '{ManagedObjectProtocols.GetTypeName(category)}'",
                span,
                "TypeError"
            );
        if (skipPrefixes is PythonNoneValue)
            skipPrefixes = new PythonTupleValue([]);
        if (skipPrefixes is not PythonTupleValue skip)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "warn() argument 'skip_file_prefixes' must be tuple, "
                    + $"not {ManagedObjectProtocols.GetTypeName(skipPrefixes)}",
                span,
                "TypeError"
            );
        if (skip.Elements.Length != 0)
            stacklevel = Math.Max(2, stacklevel);

        var location = Locate(stacklevel, skip);
        var registry = RegistryFor(location.Globals);
        return WarnExplicit(
            message,
            category,
            new PythonTextValue(location.FileName),
            location.Line,
            new PythonTextValue(location.Module),
            registry,
            null,
            source is PythonNoneValue ? null : source,
            span
        );
    }

    /// <summary>
    /// The frame a warning is reported at: `level` frames up, with the frames of internal
    /// files and of the skipped prefixes passed over.
    /// </summary>
    private static PythonFrameLocation Locate(int level, PythonTupleValue skipPrefixes)
    {
        var dispatcher = UserObjectProtocols.Dispatcher!;
        var seen = 0;
        for (var frame = 1; frame <= 512; frame++)
        {
            if (dispatcher.CallerLocation(frame) is not { } location)
                break;
            if (frame > 1 && IsInternalFileName(location.FileName))
                continue;
            if (frame > 1 && IsFileNameToSkip(location.FileName, skipPrefixes))
                continue;
            seen++;
            if (seen >= Math.Max(1, level))
                return location;
        }
        return new PythonFrameLocation("<sys>", 0, "sys", null);
    }

    /// <summary>`warn_explicit(message, category, filename, lineno, module=None, registry=None, module_globals=None, source=None)`.</summary>
    private static PythonBuiltinFunctionValue WarnExplicitFunction() =>
        new(
            "warn_explicit",
            (arguments, span) => WarnExplicitCall(arguments, [], [], span),
            (positional, names, values, span) => WarnExplicitCall(positional, names, values, span)
        );

    private static PythonNoneValue WarnExplicitCall(
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        var bound = BindCall(
            "warn_explicit",
            [
                "message",
                "category",
                "filename",
                "lineno",
                "module",
                "registry",
                "module_globals",
                "source",
            ],
            [
                null,
                null,
                null,
                null,
                PythonNoneValue.Instance,
                PythonNoneValue.Instance,
                PythonNoneValue.Instance,
                PythonNoneValue.Instance,
            ],
            positional,
            names,
            values,
            span
        );
        return WarnExplicit(
            bound[0],
            bound[1],
            bound[2],
            Lineno(bound[3], span),
            bound[4],
            bound[5] is PythonNoneValue ? null : bound[5],
            bound[6] is PythonNoneValue ? null : bound[6],
            bound[7] is PythonNoneValue ? null : bound[7],
            span
        );
    }

    /// <summary>The body both entry points share, which is `warn_explicit` in the source.</summary>
    private static PythonNoneValue WarnExplicit(
        PythonValue message,
        PythonValue category,
        PythonValue filename,
        int lineno,
        PythonValue? module,
        PythonValue? registry,
        PythonValue? moduleGlobals,
        PythonValue? source,
        TextSpan span
    )
    {
        var text = filename is PythonTextValue fileName ? fileName.Value : null;
        if (text is null)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "filename must be a str",
                span,
                "TypeError"
            );
        if (module is null or PythonNoneValue)
        {
            var derived = text.Length != 0 ? text : "<unknown>";
            if (derived.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                derived = derived[..^3];
            module = new PythonTextValue(derived);
        }
        else if (module is not PythonTextValue)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "module must be a str",
                span,
                "TypeError"
            );
        }
        string messageText;
        if (
            message is PythonExceptionValue instance
            && IsWarningSubclass(PythonBuiltinTypes.GetRuntimeType(instance))
        )
        {
            messageText = instance.ToDisplayString();
            category = PythonBuiltinTypes.GetRuntimeType(instance);
        }
        else
        {
            if (message is not PythonTextValue messageValue)
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    "message must be a str or a Warning instance",
                    span,
                    "TypeError"
                );
            messageText = messageValue.Value;
            message = Construct(category, messageText, span);
        }

        PythonValue registryValue =
            registry as PythonDictionaryValue ?? new PythonDictionaryValue([]);
        var key = new PythonTupleValue([
            new PythonTextValue(messageText),
            category,
            PythonWholeNumberValue.Create(lineno),
        ]);
        // The registry is stamped with the filter version it was filled at, and a filter
        // that changed since empties it — which is how a warning reappears after the
        // filters move.
        if (
            DictionaryGet(registryValue, new PythonTextValue("version"))
                is not PythonWholeNumberValue stamp
            || (long)stamp.Value != _filtersVersion
        )
        {
            if (registryValue is PythonDictionaryValue stale)
                stale.ClearItems();
            DictionarySet(
                registryValue,
                new PythonTextValue("version"),
                PythonWholeNumberValue.Create(_filtersVersion)
            );
        }
        if (RegistryHas(registryValue, key))
            return PythonNoneValue.Instance;

        var action = "default";
        var matched = false;
        foreach (var item in CurrentFilters().Elements)
        {
            if (item is not PythonTupleValue { Elements.Length: 5 } filter)
                continue;
            var candaction = (PythonTextValue)filter.Elements[0];
            if (
                (filter.Elements[1] is PythonNoneValue || Matches(filter.Elements[1], messageText))
                && IsSubclass(category, filter.Elements[2])
                && (
                    filter.Elements[3] is PythonNoneValue
                    || Matches(filter.Elements[3], ModuleName(module))
                )
                && (
                    filter.Elements[4] is PythonWholeNumberValue line
                    && (line.Value.IsZero || (long)line.Value == lineno)
                )
            )
            {
                action = candaction.Value;
                matched = true;
                break;
            }
        }
        if (!matched)
            action = DefaultAction();
        if (action == "ignore")
            return PythonNoneValue.Instance;
        if (action == "error")
            throw new PythonRaisedException(RequireException(message, span));

        switch (action)
        {
            case "once":
                RegistryAdd(registryValue, key);
                var onceKey = new PythonTupleValue([new PythonTextValue(messageText), category]);
                if (RegistryHas(OnceRegistry(), onceKey))
                    return PythonNoneValue.Instance;
                RegistryAdd(OnceRegistry(), onceKey);
                break;
            case "always":
            case "all":
                break;
            case "module":
                RegistryAdd(registryValue, key);
                var altKey = new PythonTupleValue([
                    new PythonTextValue(messageText),
                    category,
                    PythonWholeNumberValue.Create(0),
                ]);
                if (RegistryHas(registryValue, altKey))
                    return PythonNoneValue.Instance;
                RegistryAdd(registryValue, altKey);
                break;
            case "default":
                RegistryAdd(registryValue, key);
                break;
            default:
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"Unrecognized action ('{action}') in warnings.filters",
                    span,
                    "RuntimeError"
                );
        }

        var entry = Allocate(WarningMessage, span);
        InitializeMessage(entry, message, category, filename, lineno, null, null, source);
        ShowWarnMsg(entry);
        return PythonNoneValue.Instance;
    }

    private static string DefaultAction() =>
        _module is { } globals
        && globals.TryGetValue("defaultaction", out var action)
        && action is PythonTextValue text
            ? text.Value
            : "default";

    private static string ModuleName(PythonValue module) =>
        module is PythonTextValue text ? text.Value : string.Empty;

    private static PythonExceptionValue RequireException(PythonValue value, TextSpan span) =>
        value as PythonExceptionValue
        ?? throw ManagedObjectProtocols.Fault(
            "DPY4003",
            $"exceptions must derive from BaseException, not {ManagedObjectProtocols.GetTypeName(value)}",
            span,
            "TypeError"
        );

    private static PythonValue Construct(PythonValue category, string text, TextSpan span) =>
        UserObjectProtocols.Dispatcher!.Invoke(category, [new PythonTextValue(text)], span);

    /// <summary>`registry.get(key)`: the value the key holds, or null when it holds none.</summary>
    private static PythonValue? DictionaryGet(PythonValue dictionary, PythonValue key)
    {
        if (dictionary is not PythonDictionaryValue entries)
            return null;
        foreach (var item in entries.Items)
        {
            if (ManagedObjectProtocols.AreEqual(item.Key, key))
                return item.Value;
        }
        return null;
    }

    private static void DictionarySet(PythonValue dictionary, PythonValue key, PythonValue value) =>
        ManagedObjectProtocols.SetItem(dictionary, key, value);

    private static bool RegistryHas(PythonValue registry, PythonValue key) =>
        DictionaryGet(registry, key) is { } existing && ManagedObjectProtocols.IsTrue(existing);

    private static void RegistryAdd(PythonValue registry, PythonValue key) =>
        DictionarySet(registry, key, PythonTruthValue.True);

    // -------------------------------------------------------------------------
    // Showing and formatting
    // -------------------------------------------------------------------------

    /// <summary>`showwarning(message, category, filename, lineno, file=None, line=None)`.</summary>
    private static PythonProtocolFunctionValue ShowWarningOriginal =>
        _showWarning ??= new PythonProtocolFunctionValue(
            "showwarning",
            (_, arguments) =>
            {
                var entry = Allocate(WarningMessage, default);
                InitializeMessage(
                    entry,
                    arguments.Count > 0 ? arguments[0] : PythonNoneValue.Instance,
                    arguments.Count > 1 ? arguments[1] : Category("UserWarning"),
                    arguments.Count > 2 ? arguments[2] : new PythonTextValue("<unknown>"),
                    arguments.Count > 3 ? (int)Lineno(arguments[3], default) : 0,
                    arguments.Count > 4 ? arguments[4] : PythonNoneValue.Instance,
                    arguments.Count > 5 ? arguments[5] : PythonNoneValue.Instance,
                    PythonNoneValue.Instance
                );
                ShowWarnMsgImpl(entry);
                return PythonNoneValue.Instance;
            }
        )
        {
            IsPythonMethod = true,
            Module = "warnings",
        }.WithSignature(
            ["message", "category", "filename", "lineno", "file", "line"],
            [null, null, null, null, PythonNoneValue.Instance, PythonNoneValue.Instance],
            positionalOnly: 4
        );

    /// <summary>`formatwarning(message, category, filename, lineno, line=None)`.</summary>
    private static PythonProtocolFunctionValue FormatWarningOriginal =>
        _formatWarning ??= new PythonProtocolFunctionValue(
            "formatwarning",
            (_, arguments) =>
            {
                var entry = Allocate(WarningMessage, default);
                InitializeMessage(
                    entry,
                    arguments.Count > 0 ? arguments[0] : PythonNoneValue.Instance,
                    arguments.Count > 1 ? arguments[1] : Category("UserWarning"),
                    arguments.Count > 2 ? arguments[2] : new PythonTextValue("<unknown>"),
                    arguments.Count > 3 ? (int)Lineno(arguments[3], default) : 0,
                    PythonNoneValue.Instance,
                    arguments.Count > 4 ? arguments[4] : PythonNoneValue.Instance,
                    PythonNoneValue.Instance
                );
                return new PythonTextValue(FormatWarnMsg(entry));
            }
        )
        {
            IsPythonMethod = true,
            Module = "warnings",
        }.WithSignature(
            ["message", "category", "filename", "lineno", "line"],
            [null, null, null, null, PythonNoneValue.Instance],
            positionalOnly: 4
        );

    /// <summary>`_showwarnmsg(msg)`: the hook the user may have replaced, or the default.</summary>
    private static PythonNoneValue ShowWarnMsg(PythonValue message)
    {
        if (
            _module is { } globals
            && globals.TryGetValue("showwarning", out var hook)
            && hook is not PythonNoneValue
            && !ReferenceEquals(hook, ShowWarningOriginal)
        )
        {
            if (!ManagedObjectProtocols.IsCallable(hook))
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    "warnings.showwarning() must be set to a function or method",
                    default,
                    "TypeError"
                );
            UserObjectProtocols.Dispatcher!.Invoke(
                hook,
                [
                    Attribute(message, "message"),
                    Attribute(message, "category"),
                    Attribute(message, "filename"),
                    Attribute(message, "lineno"),
                    Attribute(message, "file"),
                    Attribute(message, "line"),
                ],
                default
            );
            return PythonNoneValue.Instance;
        }
        return ShowWarnMsgImpl(message);
    }

    /// <summary>`_showwarnmsg_impl(msg)`: the log of a recording context, or stderr.</summary>
    private static PythonNoneValue ShowWarnMsgImpl(PythonValue message)
    {
        if (Contexts.Count != 0 && Contexts[^1].Log is { } log)
        {
            log.Elements.Add(message);
            return PythonNoneValue.Instance;
        }
        var text = FormatWarnMsg(message);
        if (Attribute(message, "file") is { } file && file is not PythonNoneValue)
        {
            UserObjectProtocols.Dispatcher!.Invoke(
                ManagedObjectProtocols.GetAttribute(file, "write"),
                [new PythonTextValue(text)],
                default
            );
            return PythonNoneValue.Instance;
        }
        UserObjectProtocols.Dispatcher!.StandardError.Write(text);
        return PythonNoneValue.Instance;
    }

    /// <summary>`_formatwarnmsg(msg)`: the user's own `formatwarning`, or the default.</summary>
    private static string FormatWarnMsg(PythonValue message)
    {
        if (
            _module is { } globals
            && globals.TryGetValue("formatwarning", out var hook)
            && hook is not PythonNoneValue
            && !ReferenceEquals(hook, FormatWarningOriginal)
        )
        {
            var formatted = UserObjectProtocols.Dispatcher!.Invoke(
                hook,
                [
                    Attribute(message, "message"),
                    Attribute(message, "category"),
                    Attribute(message, "filename"),
                    Attribute(message, "lineno"),
                    Attribute(message, "line"),
                ],
                default
            );
            return formatted is PythonTextValue text ? text.Value : string.Empty;
        }
        return FormatWarnMsgImpl(message);
    }

    /// <summary>
    /// `_formatwarnmsg_impl(msg)`: `file:line: Category: message`, and the source line
    /// under it when one can be found.
    /// </summary>
    private static string FormatWarnMsgImpl(PythonValue message)
    {
        var category = Attribute(message, "category");
        var categoryName =
            category is PythonExceptionTypeValue typeName ? typeName.Name
            : category is PythonManagedTypeValue managedName ? managedName.Name
            : "Warning";
        var text = Attribute(message, "message");
        var textValue = text is PythonTextValue str ? str.Value : text.ToDisplayString();
        var filename = Attribute(message, "filename") is PythonTextValue file
            ? file.Value
            : "<unknown>";
        var lineno = Attribute(message, "lineno") is PythonWholeNumberValue line
            ? (long)line.Value
            : 0;
        var formatted = $"{filename}:{lineno}: {categoryName}: {textValue}\n";
        string? sourceLine = Attribute(message, "line") switch
        {
            PythonTextValue given => given.Value,
            _ => PythonSourceLocations.TryGetLine(filename, (int)lineno),
        };
        if (!string.IsNullOrEmpty(sourceLine))
            formatted += $"  {sourceLine.Trim()}\n";
        return formatted;
    }

    private static PythonValue Attribute(PythonValue instance, string name) =>
        ManagedObjectProtocols.GetAttribute(instance, name);

    // -------------------------------------------------------------------------
    // WarningMessage
    // -------------------------------------------------------------------------

    private static PythonManagedTypeValue WarningMessage =>
        _warningMessage ??= CreateWarningMessage();

    private static PythonManagedTypeValue CreateWarningMessage()
    {
        var owner = "WarningMessage";
        var type = new PythonManagedTypeValue(owner) { Module = "warnings", QualName = owner };
        type.SetDeclaredBases(new PythonTupleValue([PythonBuiltinFunctions.Object]));
        type.SetResolutionOrder(new PythonTupleValue([type, PythonBuiltinFunctions.Object]));
        type.Attributes["__module__"] = new PythonTextValue("warnings");
        type.Attributes["__doc__"] = new PythonTextValue(
            "The arguments to `showwarning()`, as one object.\n"
        );
        type.Attributes["_WARNING_DETAILS"] = new PythonTupleValue([
            new PythonTextValue("message"),
            new PythonTextValue("category"),
            new PythonTextValue("filename"),
            new PythonTextValue("lineno"),
            new PythonTextValue("file"),
            new PythonTextValue("line"),
            new PythonTextValue("source"),
        ]);
        type.Attributes["__init__"] = PythonUserTypes.Method(
            owner,
            "__init__",
            (receiver, arguments) =>
            {
                var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
                InitializeMessage(
                    self,
                    rest.Count > 0 ? rest[0] : PythonNoneValue.Instance,
                    rest.Count > 1 ? rest[1] : Category("UserWarning"),
                    rest.Count > 2 ? rest[2] : new PythonTextValue("<unknown>"),
                    rest.Count > 3 ? (int)Lineno(rest[3], default) : 0,
                    rest.Count > 4 ? rest[4] : PythonNoneValue.Instance,
                    rest.Count > 5 ? rest[5] : PythonNoneValue.Instance,
                    rest.Count > 6 ? rest[6] : PythonNoneValue.Instance
                );
                return PythonNoneValue.Instance;
            },
            parameters: ["message", "category", "filename", "lineno", "file", "line", "source"],
            defaults:
            [
                null,
                null,
                null,
                null,
                PythonNoneValue.Instance,
                PythonNoneValue.Instance,
                PythonNoneValue.Instance,
            ],
            positionalOnly: 4
        );
        type.Attributes["__str__"] = PythonUserTypes.Method(
            owner,
            "__str__",
            (receiver, arguments) =>
            {
                var (self, _) = PythonUserTypes.Bound(receiver, arguments);
                var category = Attribute(self, "category");
                var categoryName =
                    category is PythonExceptionTypeValue named ? named.Name
                    : category is PythonManagedTypeValue managed ? managed.Name
                    : "None";
                var filename = Attribute(self, "filename");
                var lineno = Attribute(self, "lineno");
                var line = Attribute(self, "line");
                return new PythonTextValue(
                    "{message : "
                        + Attribute(self, "message").ToRepresentationString()
                        + ", category : '"
                        + categoryName
                        + "', filename : "
                        + filename.ToRepresentationString()
                        + ", lineno : "
                        + lineno.ToDisplayString()
                        + ", line : "
                        + line.ToRepresentationString()
                        + "}"
                );
            }
        );
        type.Attributes["__repr__"] = PythonUserTypes.Method(
            owner,
            "__repr__",
            (receiver, arguments) =>
            {
                var (self, _) = PythonUserTypes.Bound(receiver, arguments);
                var text = UserObjectProtocols.Dispatcher!.Invoke(
                    ManagedObjectProtocols.GetAttribute(self, "__str__"),
                    [],
                    default
                );
                var type = PythonBuiltinTypes.GetRuntimeType(self);
                var qualname = ManagedObjectProtocols.GetAttribute(type, "__qualname__");
                return new PythonTextValue(
                    $"<{(qualname is PythonTextValue name ? name.Value : owner)} {text.ToDisplayString()}>"
                );
            }
        );
        PythonAbc.Refresh(type);
        return type;
    }

    /// <summary>Sets the seven details a `WarningMessage` carries.</summary>
    private static void InitializeMessage(
        PythonValue entry,
        PythonValue message,
        PythonValue category,
        PythonValue filename,
        int lineno,
        PythonValue? file,
        PythonValue? line,
        PythonValue? source
    )
    {
        ManagedObjectProtocols.SetAttribute(entry, "message", message);
        ManagedObjectProtocols.SetAttribute(entry, "category", category);
        ManagedObjectProtocols.SetAttribute(entry, "filename", filename);
        ManagedObjectProtocols.SetAttribute(entry, "lineno", PythonWholeNumberValue.Create(lineno));
        ManagedObjectProtocols.SetAttribute(entry, "file", file ?? PythonNoneValue.Instance);
        ManagedObjectProtocols.SetAttribute(entry, "line", line ?? PythonNoneValue.Instance);
        ManagedObjectProtocols.SetAttribute(entry, "source", source ?? PythonNoneValue.Instance);
        ManagedObjectProtocols.SetAttribute(
            entry,
            "_category_name",
            new PythonTextValue(
                category is PythonExceptionTypeValue named ? named.Name
                : category is PythonManagedTypeValue managed ? managed.Name
                : string.Empty
            )
        );
    }

    private static PythonValue Message(IReadOnlyList<PythonValue> arguments, int index) =>
        arguments.Count > index ? arguments[index] : PythonNoneValue.Instance;

    private static PythonManagedObjectValue Allocate(PythonManagedTypeValue type, TextSpan span)
    {
        if (PythonCollectionsAbc.RefuseInstantiation(type, span) is { } refusal)
            throw refusal;
        return new PythonManagedObjectValue(type, PythonSubclassStorage.Allocate(type));
    }

    // -------------------------------------------------------------------------
    // catch_warnings and deprecated
    // -------------------------------------------------------------------------

    private static PythonManagedTypeValue CatchWarnings => _catchWarnings ??= CreateCatchWarnings();

    private static PythonManagedTypeValue CreateCatchWarnings()
    {
        var owner = "catch_warnings";
        var type = new PythonManagedTypeValue(owner) { Module = "warnings", QualName = owner };
        type.SetDeclaredBases(new PythonTupleValue([PythonBuiltinFunctions.Object]));
        type.SetResolutionOrder(new PythonTupleValue([type, PythonBuiltinFunctions.Object]));
        type.Attributes["__module__"] = new PythonTextValue("warnings");
        type.Attributes["__doc__"] = new PythonTextValue(
            "A context manager that copies and restores the warnings filter upon\n"
                + "exiting the context.\n"
        );
        type.Attributes["__init__"] = PythonUserTypes.Method(
            owner,
            "__init__",
            (receiver, arguments) => CatchWarningsInit(receiver, arguments, [], []),
            (receiver, positional, names, values) =>
                CatchWarningsInit(receiver, positional, names, values),
            ["record", "module", "action", "category", "lineno", "append"],
            [
                PythonTruthValue.False,
                PythonNoneValue.Instance,
                PythonNoneValue.Instance,
                Category("Warning"),
                PythonWholeNumberValue.Create(0),
                PythonTruthValue.False,
            ]
        );
        type.Attributes["__repr__"] = Method(
            owner,
            "__repr__",
            (receiver, arguments) =>
            {
                var (self, _) = PythonUserTypes.Bound(receiver, arguments);
                var state = States.GetOrCreateValue((PythonManagedObjectValue)self);
                return new PythonTextValue(
                    $"catch_warnings{(state.Record ? "(record=True)" : "()")}"
                );
            }
        );
        type.Attributes["__enter__"] = Method(owner, "__enter__", CatchWarningsEnter);
        type.Attributes["__exit__"] = Method(owner, "__exit__", CatchWarningsExit);
        PythonAbc.Refresh(type);
        return type;
    }

    private static PythonNoneValue CatchWarningsInit(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, positional);
        var state = States.GetOrCreateValue((PythonManagedObjectValue)self);
        if (rest.Count > 0)
            state.Record = ManagedObjectProtocols.IsTrue(rest[0]);
        var action = (PythonValue?)null;
        PythonValue? category = null;
        var lineno = 0;
        var append = false;
        for (var index = 0; index < names.Count; index++)
        {
            switch (names[index])
            {
                case "record":
                    state.Record = ManagedObjectProtocols.IsTrue(values[index]);
                    break;
                case "module":
                    break;
                case "action":
                    action = values[index];
                    break;
                case "category":
                    category = values[index];
                    break;
                case "lineno":
                    lineno = (int)Lineno(values[index], default);
                    break;
                case "append":
                    append = ManagedObjectProtocols.IsTrue(values[index]);
                    break;
            }
        }
        state.SavedShowWarning = null;
        state.Log = null;
        if (action is not null && action is not PythonNoneValue)
        {
            SimpleFilterCall(
                [
                    action,
                    category ?? Category("Warning"),
                    PythonWholeNumberValue.Create(lineno),
                    append ? PythonTruthValue.True : PythonTruthValue.False,
                ],
                [],
                [],
                default
            );
        }
        return PythonNoneValue.Instance;
    }

    private static PythonValue CatchWarningsEnter(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        var instance = (PythonManagedObjectValue)self;
        var state = States.GetOrCreateValue(instance);
        if (state.Entered)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"Cannot enter {instance.ToDisplayString()} twice",
                default,
                "RuntimeError"
            );
        state.Entered = true;
        var context = new Context
        {
            Filters = new PythonListValue([.. CurrentFilters().Elements]),
            Log = Contexts.Count != 0 ? Contexts[^1].Log : null,
        };
        Contexts.Add(context);
        Mutated();
        if (
            _module is { } globals
            && globals.TryGetValue("showwarning", out var hook)
            && hook is not PythonNoneValue
        )
            state.SavedShowWarning = hook as PythonProtocolFunctionValue;
        if (state.Record)
        {
            context.Log = new PythonListValue([]);
            state.Log = context.Log;
            if (_module is { } module)
                module.SetValue("showwarning", ShowWarningOriginal);
        }
        return context.Log is { } log ? log : PythonNoneValue.Instance;
    }

    private static PythonValue CatchWarningsExit(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        var state = States.GetOrCreateValue((PythonManagedObjectValue)self);
        if (!state.Entered)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"Cannot exit {self.ToDisplayString()} without entering first",
                default,
                "RuntimeError"
            );
        if (Contexts.Count != 0)
            Contexts.RemoveAt(Contexts.Count - 1);
        Mutated();
        if (_module is { } globals && state.SavedShowWarning is { } saved)
        {
            globals.SetValue("showwarning", saved);
            state.SavedShowWarning = null;
        }
        return PythonNoneValue.Instance;
    }

    /// <summary>`deprecated(message, /, *, category=DeprecationWarning, stacklevel=1)`.</summary>
    private static PythonManagedTypeValue Deprecated => _deprecated ??= CreateDeprecated();

    private static PythonManagedTypeValue CreateDeprecated()
    {
        var owner = "deprecated";
        var type = new PythonManagedTypeValue(owner) { Module = "warnings", QualName = owner };
        type.SetDeclaredBases(new PythonTupleValue([PythonBuiltinFunctions.Object]));
        type.SetResolutionOrder(new PythonTupleValue([type, PythonBuiltinFunctions.Object]));
        type.Attributes["__module__"] = new PythonTextValue("warnings");
        type.Attributes["__doc__"] = new PythonTextValue(
            "Indicate that a class, function or overload is deprecated.\n"
        );
        type.Attributes["__init__"] = PythonUserTypes.Method(
            owner,
            "__init__",
            (receiver, arguments) =>
            {
                var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
                if (rest.Count == 0 || rest[0] is not PythonTextValue message)
                    throw ManagedObjectProtocols.Fault(
                        "DPY4003",
                        "Expected an object of type str for 'message', not "
                            + $"'{ManagedObjectProtocols.GetTypeName(rest.Count == 0 ? PythonNoneValue.Instance : rest[0])}'",
                        default,
                        "TypeError"
                    );
                ManagedObjectProtocols.SetAttribute(self, "message", message);
                ManagedObjectProtocols.SetAttribute(
                    self,
                    "category",
                    rest.Count > 1 ? rest[1] : Category("DeprecationWarning")
                );
                ManagedObjectProtocols.SetAttribute(
                    self,
                    "stacklevel",
                    PythonWholeNumberValue.Create(
                        rest.Count > 2 ? (long)Lineno(rest[2], default) : 1
                    )
                );
                return PythonNoneValue.Instance;
            },
            parameters: ["message", "category", "stacklevel"],
            defaults: [null, Category("DeprecationWarning"), PythonWholeNumberValue.Create(1)],
            positionalOnly: 1
        );
        type.Attributes["__call__"] = Method(owner, "__call__", DeprecatedCall);
        PythonAbc.Refresh(type);
        return type;
    }

    /// <summary>
    /// `deprecated.__call__(arg)`: the callable wrapped so every call warns at the caller
    /// and still answers what it answered.
    /// </summary>
    private static PythonValue DeprecatedCall(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        var target = rest.Count != 0 ? rest[0] : PythonNoneValue.Instance;
        var message = Attribute(self, "message");
        var category = Attribute(self, "category");
        var stacklevel = (int)(
            Attribute(self, "stacklevel") is PythonWholeNumberValue level ? (long)level.Value : 1
        );
        if (category is PythonNoneValue)
        {
            SetDeprecated(target, message);
            return target;
        }
        if (!ManagedObjectProtocols.IsCallable(target))
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "@deprecated decorator with non-None category must be applied to "
                    + $"a class or callable, not {target.ToRepresentationString()}",
                default,
                "TypeError"
            );
        var wrapper = new PythonProtocolFunctionValue(
            ManagedObjectProtocols.GetAttribute(target, "__name__") is PythonTextValue name
                ? name.Value
                : "wrapper",
            (_, callArguments) =>
            {
                // The wrapper is a native callable and pushes no frame of its own, so the
                // caller's stack level is the decorator's own — the source adds one
                // because its wrapper is a function the interpreter runs.
                WarnCall(
                    [message, category, PythonWholeNumberValue.Create(stacklevel)],
                    [],
                    [],
                    default
                );
                return UserObjectProtocols.Dispatcher!.Invoke(target, [.. callArguments], default);
            }
        )
        {
            IsPythonMethod = true,
            Module = "warnings",
            Doc = ManagedObjectProtocols.GetAttribute(target, "__doc__") is PythonTextValue doc
                ? doc.Value
                : null,
        };
        SetDeprecated(target, message);
        SetDeprecated(wrapper, message);
        return wrapper;
    }

    /// <summary>`arg.__deprecated__ = msg`.</summary>
    private static void SetDeprecated(PythonValue target, PythonValue message)
    {
        if (target is PythonProtocolFunctionValue protocol)
        {
            protocol.Attributes["__deprecated__"] = message;
            return;
        }
        // A function the interpreter runs has no writable attributes through the object
        // protocol, so the marker goes into the dictionary it reads, the way
        // `abc.abstractmethod` writes its own.
        if (target is PythonFunctionValue function)
        {
            function.Attributes["__deprecated__"] = message;
            return;
        }
        if (target is PythonBuiltinFunctionValue)
            return;
        try
        {
            ManagedObjectProtocols.SetAttribute(target, "__deprecated__", message);
        }
        catch (Exception error)
            when (PythonNamespaceMapping.IsPythonException(error, "AttributeError")) { }
    }

    // -------------------------------------------------------------------------
    // Shared helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Binds a call against a declared parameter list, filling the defaults and refusing
    /// what CPython's own binder refuses.
    /// </summary>
    private static PythonValue[] BindCall(
        string name,
        string[] parameters,
        PythonValue?[] defaults,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values,
        TextSpan span
    )
    {
        if (positional.Count > parameters.Length)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{name}() takes from {Required(parameters, defaults)} to {parameters.Length} "
                    + $"positional arguments but {positional.Count} were given",
                span,
                "TypeError"
            );
        }
        var slots = new PythonValue?[parameters.Length];
        Array.Copy(defaults, slots, Math.Min(defaults.Length, parameters.Length));
        for (var index = 0; index < positional.Count; index++)
            slots[index] = positional[index];
        for (var index = 0; index < names.Count; index++)
        {
            var slot = Array.IndexOf(parameters, names[index]);
            if (slot < 0)
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{name}() got an unexpected keyword argument '{names[index]}'",
                    span,
                    "TypeError"
                );
            if (slot < positional.Count)
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{name}() got multiple values for argument '{names[index]}'",
                    span,
                    "TypeError"
                );
            slots[slot] = values[index];
        }
        for (var index = 0; index < slots.Length; index++)
        {
            if (slots[index] is null)
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{name}() missing required positional argument: '{parameters[index]}'",
                    span,
                    "TypeError"
                );
        }
        return Array.ConvertAll(slots, slot => slot!);
    }

    private static int Required(string[] parameters, PythonValue?[] defaults)
    {
        var count = 0;
        for (var index = 0; index < parameters.Length; index++)
        {
            if (index < defaults.Length && defaults[index] is null)
                count++;
        }
        return count;
    }

    private static PythonProtocolFunctionValue Method(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body
    ) => PythonUserTypes.Method(owner, name, body);

    private static PythonValue RequireArgument(PythonValue?[] bound, int index, string name) =>
        bound[index]
        ?? throw ManagedObjectProtocols.Fault(
            "DPY4003",
            $"missing required argument '{name}'",
            default,
            "TypeError"
        );

    private static string RequireText(PythonValue value, string name, TextSpan span) =>
        value is PythonTextValue text
            ? text.Value
            : throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{name} must be a string",
                span,
                "TypeError"
            );

    private static string RequireText(IReadOnlyList<PythonValue> arguments, int index, string name)
    {
        if (index >= arguments.Count || arguments[index] is not PythonTextValue text)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{name}() argument must be a string",
                default,
                "TypeError"
            );
        return text.Value;
    }

    /// <summary>`lineno`, which must be an int and must not be negative.</summary>
    private static int Lineno(PythonValue value, string name, TextSpan span)
    {
        if (value is not PythonWholeNumberValue whole)
        {
            if (value is PythonTruthValue truth)
                return truth.Value ? 1 : 0;
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{name} must be an int",
                span,
                "TypeError"
            );
        }
        var number = (long)whole.Value;
        if (number < 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{name} must be an int >= 0",
                span,
                "ValueError"
            );
        return (int)number;
    }

    /// <summary>`int(value)`: a whole number, a float truncated, or a numeric string.</summary>
    private static int Lineno(PythonValue value, TextSpan span) =>
        value switch
        {
            PythonWholeNumberValue whole => (int)(long)whole.Value,
            PythonTruthValue truth => truth.Value ? 1 : 0,
            PythonFloatingPointValue floating => (int)floating.Value,
            PythonTextValue text
                when int.TryParse(
                    text.Value,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed
                ) => parsed,
            _ => throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"an integer is required (got type {ManagedObjectProtocols.GetTypeName(value)})",
                span,
                "TypeError"
            ),
        };

    private static void RequireAction(string action)
    {
        if (!Actions.Contains(action, StringComparer.Ordinal))
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"invalid action: '{action}'",
                default,
                "ValueError"
            );
    }

    private static string Value(PythonValue value) => ManagedObjectProtocols.GetTypeName(value);

    /// <summary>Whether a class is a `Warning` category.</summary>
    private static bool IsWarningSubclass(PythonValue value)
    {
        if (value is not (PythonExceptionTypeValue or PythonManagedTypeValue))
            return false;
        var warning = PythonBuiltinTypes.GetExceptionType("Warning");
        return ReferenceEquals(value, warning)
            || PythonBuiltinTypes
                .GetMro(value)
                .Elements.Any(entry => ReferenceEquals(entry, warning));
    }

    /// <summary>`issubclass(category, filter category)`.</summary>
    private static bool IsSubclass(PythonValue category, PythonValue filterCategory) =>
        ReferenceEquals(category, filterCategory)
        || PythonBuiltinTypes
            .GetMro(category)
            .Elements.Any(entry => ReferenceEquals(entry, filterCategory));

    private static bool IsInternalFileName(string fileName) =>
        fileName.Contains("importlib", StringComparison.Ordinal)
        && fileName.Contains("_bootstrap", StringComparison.Ordinal);

    private static PythonTruthValue IsFileNameToSkip(IReadOnlyList<PythonValue> arguments)
    {
        if (
            arguments.Count < 2
            || arguments[0] is not PythonTextValue file
            || arguments[1] is not PythonTupleValue prefixes
        )
            return PythonTruthValue.False;
        return IsFileNameToSkip(file.Value, prefixes)
            ? PythonTruthValue.True
            : PythonTruthValue.False;
    }

    private static bool IsFileNameToSkip(string fileName, PythonTupleValue prefixes) =>
        prefixes.Elements.Any(prefix =>
            prefix is PythonTextValue text
            && fileName.StartsWith(text.Value, StringComparison.Ordinal)
        );

    private static readonly Dictionary<
        (string Pattern, bool IgnoreCase),
        WarningPatternValue
    > Patterns = [];

    /// <summary>
    /// A compiled pattern, handed out one instance per pattern the way `re.compile` caches
    /// them — which is what lets two identical filters compare equal.
    /// </summary>
    private static WarningPatternValue Pattern(string pattern, bool ignoreCase = false)
    {
        if (Patterns.TryGetValue((pattern, ignoreCase), out var cached))
            return cached;
        try
        {
            var compiled = new WarningPatternValue(
                pattern,
                new Regex(pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None)
            );
            Patterns[(pattern, ignoreCase)] = compiled;
            return compiled;
        }
        catch (ArgumentException)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"unterminated regular expression at end of string: {pattern}",
                default,
                "re.error"
            );
        }
    }

    /// <summary>
    /// Whether a pattern matches: a pattern the interpreter compiled is anchored at the
    /// start, the way `re.match` is, while a plain string — which is what the default
    /// filters carry — is compared as it stands.
    /// </summary>
    private static bool Matches(PythonValue pattern, string text) =>
        pattern switch
        {
            PythonTextValue literal => string.Equals(literal.Value, text, StringComparison.Ordinal),
            WarningPatternValue compiled => compiled.Compiled.Match(text)
                is { Success: true, Index: 0 },
            _ => false,
        };

    private static readonly PythonManagedTypeValue ReentrantLockType = CreateLockType();

    private static PythonManagedTypeValue CreateLockType()
    {
        var type = new PythonManagedTypeValue("_lock") { Module = "warnings", QualName = "_lock" };
        type.SetDeclaredBases(new PythonTupleValue([PythonBuiltinFunctions.Object]));
        type.SetResolutionOrder(new PythonTupleValue([type, PythonBuiltinFunctions.Object]));
        type.Attributes["__module__"] = new PythonTextValue("warnings");
        type.Attributes["__enter__"] = Method(
            "_lock",
            "__enter__",
            (receiver, _) => receiver ?? PythonNoneValue.Instance
        );
        type.Attributes["__exit__"] = Method(
            "_lock",
            "__exit__",
            (_, _) => PythonNoneValue.Instance
        );
        PythonAbc.Refresh(type);
        return type;
    }

    private const string ModuleDoc = "Python part of the warnings subsystem.\n";
}

/// <summary>
/// A compiled warning filter pattern. It matches the way `re.match` does — anchored at the
/// start — and reports itself the way the `re` module's own pattern does, because that text
/// is what `warnings.filters` prints.
/// </summary>
internal sealed record WarningPatternValue(string Pattern, Regex Compiled) : PythonValue
{
    internal override string ToDisplayString() =>
        Compiled.Options.HasFlag(RegexOptions.IgnoreCase)
            ? $"re.compile('{Pattern}', re.IGNORECASE)"
            : $"re.compile('{Pattern}')";
}
