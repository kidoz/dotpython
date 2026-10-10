// The `contextlib` module follows CPython 3.14.7 Lib/contextlib.py:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The <c>contextlib</c> module: the utilities the <c>with</c> statement is built with.
/// </summary>
/// <remarks>
/// The synchronous surface is here — <c>contextmanager</c> with the manager it builds,
/// <c>closing</c>, <c>nullcontext</c>, <c>suppress</c>, <c>ExitStack</c>,
/// <c>ContextDecorator</c> and <c>AbstractContextManager</c> — while the asynchronous half
/// (<c>asynccontextmanager</c>, <c>aclosing</c>, <c>AbstractAsyncContextManager</c>,
/// <c>AsyncExitStack</c>) and the stream and directory utilities
/// (<c>redirect_stdout</c>, <c>redirect_stderr</c>, <c>chdir</c>) are absent with the
/// machinery they need. The classes are built the way CPython's are: `closing`, `suppress`
/// and `nullcontext` are classes on <c>ABCMeta</c>, and the manager <c>contextmanager</c>
/// hands out is a subclass of both <c>AbstractContextManager</c> and
/// <c>ContextDecorator</c>.
/// </remarks>
internal static class PythonContextLib
{
    /// <summary>The names the module publishes, in CPython's own order.</summary>
    private static readonly string[] ExportedNames =
    [
        "contextmanager",
        "closing",
        "nullcontext",
        "AbstractContextManager",
        "ContextDecorator",
        "ExitStack",
        "suppress",
    ];

    private static PythonManagedTypeValue? _abstractManager;
    private static PythonManagedTypeValue? _decorator;
    private static PythonManagedTypeValue? _managerBase;
    private static PythonManagedTypeValue? _manager;
    private static PythonManagedTypeValue? _closing;
    private static PythonManagedTypeValue? _nullContext;
    private static PythonManagedTypeValue? _suppress;
    private static PythonManagedTypeValue? _stackBase;
    private static PythonManagedTypeValue? _exitStack;
    private static PythonProtocolFunctionValue? _contextManager;

    /// <summary>The module's names.</summary>
    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue(
            "__doc__",
            new PythonTextValue("Utilities for with-statement contexts.  See PEP 343.")
        );
        globals.SetValue(
            "__all__",
            new PythonListValue([
                .. ExportedNames.Select(name => (PythonValue)new PythonTextValue(name)),
            ])
        );
        globals.SetValue("AbstractContextManager", AbstractManager);
        globals.SetValue("ContextDecorator", Decorator);
        globals.SetValue("closing", Closing);
        globals.SetValue("nullcontext", NullContext);
        globals.SetValue("suppress", Suppress);
        globals.SetValue("ExitStack", ExitStack);
        globals.SetValue("contextmanager", ContextManager);
    }

    // -------------------------------------------------------------------------
    // AbstractContextManager
    // -------------------------------------------------------------------------

    /// <summary>`contextlib.AbstractContextManager`.</summary>
    private static PythonManagedTypeValue AbstractManager =>
        _abstractManager ??= CreateAbstractManager();

    private static PythonManagedTypeValue CreateAbstractManager()
    {
        var baseClass = PythonAbc.AbcClass();
        var type = HeapType(
            "AbstractContextManager",
            [baseClass],
            [baseClass, PythonBuiltinFunctions.Object]
        );
        type.Attributes["__doc__"] = new PythonTextValue(
            "An abstract base class for context managers."
        );
        type.Attributes["__slots__"] = new PythonTupleValue([]);
        type.Attributes["__enter__"] = Method(
            "AbstractContextManager",
            "__enter__",
            (receiver, _) => receiver ?? PythonNoneValue.Instance
        );
        var stub = Method("AbstractContextManager", "__exit__", (_, _) => PythonNoneValue.Instance);
        PythonAbc.Marked(stub);
        type.Attributes["__exit__"] = stub;
        type.Attributes["__subclasshook__"] = new PythonClassMethodValue(
            Method(
                "AbstractContextManager",
                "__subclasshook__",
                (_, arguments) =>
                    ClassCarries(arguments[0], "__enter__")
                    && ClassCarries(arguments[0], "__exit__")
                        ? PythonTruthValue.True
                        : PythonNotImplementedValue.Instance
            )
        );
        PythonAbc.Refresh(type);
        return type;
    }

    // -------------------------------------------------------------------------
    // ContextDecorator
    // -------------------------------------------------------------------------

    /// <summary>`contextlib.ContextDecorator`.</summary>
    private static PythonManagedTypeValue Decorator => _decorator ??= CreateDecorator();

    private static PythonManagedTypeValue CreateDecorator()
    {
        // A plain class, not one of the module's abstract ones: `type(ContextDecorator)` is
        // `type`, as the source leaves it.
        var type = HeapType(
            "ContextDecorator",
            [PythonBuiltinFunctions.Object],
            [PythonBuiltinFunctions.Object],
            abstractMeta: false
        );
        type.Attributes["__doc__"] = new PythonTextValue(
            "A base class or mixin that enables context managers to work as decorators."
        );
        type.Attributes["_recreate_cm"] = Method(
            "ContextDecorator",
            "_recreate_cm",
            (receiver, _) => receiver ?? PythonNoneValue.Instance
        );
        type.Attributes["__call__"] = Method("ContextDecorator", "__call__", Decorate);
        PythonAbc.Refresh(type);
        return type;
    }

    /// <summary>
    /// `ContextDecorator.__call__(func)`: the function wrapped so that every call runs
    /// inside a freshly recreated manager.
    /// </summary>
    private static PythonValue Decorate(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var self = receiver ?? PythonNoneValue.Instance;
        var function = arguments.Count != 0 ? arguments[0] : PythonNoneValue.Instance;
        return Wrapped(
            function,
            (positional, names, values) =>
            {
                var manager = Dispatched(
                    ManagedObjectProtocols.GetAttribute(self, "_recreate_cm"),
                    []
                );
                return Inside(
                    manager,
                    () =>
                        names.Count == 0
                            ? Dispatched(function, positional)
                            : UserObjectProtocols.Dispatcher!.InvokeWithKeywords(
                                function,
                                positional,
                                names,
                                values,
                                default
                            )
                );
            }
        );
    }

    /// <summary>
    /// The function `functools.wraps` would produce: the same name, docstring and module,
    /// with the wrapped callable kept as `__wrapped__`.
    /// </summary>
    private static PythonProtocolFunctionValue Wrapped(
        PythonValue function,
        Func<
            IReadOnlyList<PythonValue>,
            IReadOnlyList<string>,
            IReadOnlyList<PythonValue>,
            PythonValue
        > body
    )
    {
        var wrapper = new PythonProtocolFunctionValue(
            TextOf(function, "__name__") ?? "inner",
            (_, positional) => body(positional, [], []),
            (_, positional, names, values) => body(positional, names, values)
        )
        {
            IsPythonMethod = true,
            Doc = TextOf(function, "__doc__"),
            Module = TextOf(function, "__module__"),
        };
        wrapper.Attributes["__wrapped__"] = function;
        return wrapper;
    }

    /// <summary>`with manager: body()`, with the manager's `__exit__` deciding the
    /// exception that escapes the body.</summary>
    private static PythonValue Inside(PythonValue manager, Func<PythonValue> body)
    {
        var exit = ManagedObjectProtocols.GetAttribute(manager, "__exit__");
        Dispatched(ManagedObjectProtocols.GetAttribute(manager, "__enter__"), []);
        PythonValue result;
        try
        {
            result = body();
        }
        catch (PythonRaisedException raised)
        {
            var suppression = Dispatched(
                exit,
                [
                    PythonBuiltinTypes.GetRuntimeType(raised.Value),
                    raised.Value,
                    PythonNoneValue.Instance,
                ]
            );
            if (ManagedObjectProtocols.IsTrue(suppression))
                return PythonNoneValue.Instance;
            throw;
        }
        Dispatched(
            exit,
            [PythonNoneValue.Instance, PythonNoneValue.Instance, PythonNoneValue.Instance]
        );
        return result;
    }

    // -------------------------------------------------------------------------
    // contextmanager
    // -------------------------------------------------------------------------

    /// <summary>`contextlib.contextmanager`.</summary>
    private static PythonProtocolFunctionValue ContextManager =>
        _contextManager ??= new PythonProtocolFunctionValue(
            "contextmanager",
            (_, arguments) => ContextManagerCall(arguments),
            (_, arguments, _, _) => ContextManagerCall(arguments)
        )
        {
            IsPythonMethod = true,
            Module = "contextlib",
            Doc = ContextManagerDoc,
        };

    /// <summary>
    /// `contextmanager(func)`: the helper that builds a manager per call, carrying the name,
    /// the docstring and the module of the generator function it was given.
    /// </summary>
    private static PythonProtocolFunctionValue ContextManagerCall(
        IReadOnlyList<PythonValue> arguments
    )
    {
        if (arguments.Count == 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "contextmanager() missing required argument 'func' (pos 1)",
                default,
                "TypeError"
            );
        var function = arguments[0];
        return Wrapped(
            function,
            (positional, names, values) => BuildManager(function, positional, names, values)
        );
    }

    /// <summary>`_GeneratorContextManager(func, args, kwds)`.</summary>
    private static PythonValue BuildManager(
        PythonValue function,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> names,
        IReadOnlyList<PythonValue> values
    ) =>
        UserObjectProtocols.Dispatcher!.CallType(
            Manager,
            [
                function,
                new PythonTupleValue([.. positional]),
                PythonUserTypes.Keywords(names, values),
            ],
            [],
            [],
            default
        );

    /// <summary>`contextlib._GeneratorContextManagerBase`.</summary>
    private static PythonManagedTypeValue ManagerBase => _managerBase ??= CreateManagerBase();

    private static PythonManagedTypeValue CreateManagerBase()
    {
        var owner = "_GeneratorContextManagerBase";
        var type = HeapType(
            owner,
            [PythonBuiltinFunctions.Object],
            [PythonBuiltinFunctions.Object],
            abstractMeta: false
        );
        type.Attributes["__doc__"] = new PythonTextValue(
            "Shared functionality for @contextmanager and @asynccontextmanager."
        );
        type.Attributes["__init__"] = PythonUserTypes.Method(
            owner,
            "__init__",
            Initialize,
            parameters: ["func", "args", "kwds"]
        );
        type.Attributes["_recreate_cm"] = Method(owner, "_recreate_cm", Recreate);
        return type;
    }

    /// <summary>`contextlib._GeneratorContextManager`.</summary>
    private static PythonManagedTypeValue Manager => _manager ??= CreateManager();

    private static PythonManagedTypeValue CreateManager()
    {
        var owner = "_GeneratorContextManager";
        var type = HeapType(
            owner,
            [ManagerBase, AbstractManager, Decorator],
            [
                ManagerBase,
                AbstractManager,
                PythonAbc.AbcClass(),
                Decorator,
                PythonBuiltinFunctions.Object,
            ]
        );
        type.Attributes["__doc__"] = new PythonTextValue(ManagerDoc);
        type.Attributes["__enter__"] = Method(owner, "__enter__", Enter);
        type.Attributes["__exit__"] = PythonUserTypes.Method(
            owner,
            "__exit__",
            Exit,
            parameters: ["typ", "value", "traceback"]
        );
        PythonAbc.Refresh(type);
        return type;
    }

    /// <summary>
    /// `_GeneratorContextManager(func, args, kwds)`: the generator the manager drives, the
    /// call it was made with — kept for `_recreate_cm` — and the docstring the manager
    /// answers with, which is the generator's own when it has one.
    /// </summary>
    private static PythonValue Initialize(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 3)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__init__() takes exactly 3 arguments ({rest.Count + 1} given)",
                default,
                "TypeError"
            );
        var (function, args, kwds) = (rest[0], rest[1], rest[2]);
        var (keywordNames, keywordValues) = Keywords(kwds);
        ManagedObjectProtocols.SetAttribute(
            self,
            "gen",
            UserObjectProtocols.Dispatcher!.InvokeWithKeywords(
                function,
                PythonUserTypes.Elements(args),
                keywordNames,
                keywordValues,
                default
            )
        );
        ManagedObjectProtocols.SetAttribute(self, "func", function);
        ManagedObjectProtocols.SetAttribute(self, "args", args);
        ManagedObjectProtocols.SetAttribute(self, "kwds", kwds);
        ManagedObjectProtocols.SetAttribute(
            self,
            "__doc__",
            new PythonTextValue(TextOf(function, "__doc__") ?? ManagerDoc)
        );
        return PythonNoneValue.Instance;
    }

    /// <summary>`_recreate_cm()`: a manager for the same call, because this one is spent.</summary>
    private static PythonValue Recreate(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        // The function first, as the source's argument list reads: `__enter__` drops the
        // three, and the one that is reported missing is the first one read.
        var function = ManagedObjectProtocols.GetAttribute(self, "func");
        var (names, values) = Keywords(ManagedObjectProtocols.GetAttribute(self, "kwds"));
        return BuildManager(
            function,
            PythonUserTypes.Elements(ManagedObjectProtocols.GetAttribute(self, "args")),
            names,
            values
        );
    }

    /// <summary>The keyword arguments a call carried, as the two parallel lists a call takes.</summary>
    private static (List<string> Names, List<PythonValue> Values) Keywords(PythonValue kwds)
    {
        List<string> names = [];
        List<PythonValue> values = [];
        if (kwds is not PythonDictionaryValue dictionary)
            return (names, values);
        foreach (var item in dictionary.Items)
        {
            if (item.Key is PythonTextValue key)
            {
                names.Add(key.Value);
                values.Add(item.Value);
            }
        }
        return (names, values);
    }

    private const string ManagerDoc = "Helper for @contextmanager decorator.";

    private const string ClosingDoc =
        "Context to automatically close something at the end of a block.\n\n"
        + "Code like this:\n\n"
        + "    with closing(<module>.open(<arguments>)) as f:\n"
        + "        <block>\n\n"
        + "is equivalent to this:\n\n"
        + "    f = <module>.open(<arguments>)\n"
        + "    try:\n"
        + "        <block>\n"
        + "    finally:\n"
        + "        f.close()\n\n";

    private const string NullContextDoc =
        "Context manager that does no additional processing.\n\n"
        + "Used as a stand-in for a normal context manager, when a particular\n"
        + "block of code is only sometimes used with a normal context manager:\n\n"
        + "cm = optional_cm if condition else nullcontext()\n"
        + "with cm:\n"
        + "    # Perform operation, using optional_cm if condition is True\n";

    private const string SuppressDoc =
        "Context manager to suppress specified exceptions\n\n"
        + "After the exception is suppressed, execution proceeds with the next\n"
        + "statement following the with statement.\n\n"
        + "     with suppress(FileNotFoundError):\n"
        + "         os.remove(somefile)\n"
        + "     # Execution still resumes here if the file was already removed\n";

    private const string ExitStackDoc =
        "Context manager for dynamic management of a stack of exit callbacks.\n\n"
        + "For example:\n"
        + "    with ExitStack() as stack:\n"
        + "        files = [stack.enter_context(open(fname)) for fname in filenames]\n"
        + "        # All opened files will automatically be closed at the end of\n"
        + "        # the with statement, even if attempts to open files later\n"
        + "        # in the list raise an exception.\n";

    /// <summary>
    /// `__enter__()`: the first value the generator yields, with the arguments dropped
    /// because recreation is no longer possible.
    /// </summary>
    private static PythonValue Enter(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        var generator = ManagedObjectProtocols.GetAttribute(self, "gen");
        ManagedObjectProtocols.DeleteAttribute(self, "args");
        ManagedObjectProtocols.DeleteAttribute(self, "kwds");
        ManagedObjectProtocols.DeleteAttribute(self, "func");
        if (Next(generator) is { } yielded)
            return yielded;
        throw Runtime("generator didn't yield");
    }

    /// <summary>
    /// `__exit__(typ, value, traceback)`: the generator resumed, thrown into, or closed —
    /// with a generator that yields again refusing with `generator didn't stop`, and one
    /// that raises the very exception it was given refusing to hide it.
    /// </summary>
    private static PythonValue Exit(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        if (rest.Count != 3)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"__exit__() takes exactly 3 arguments ({rest.Count + 1} given)",
                default,
                "TypeError"
            );
        var generator = ManagedObjectProtocols.GetAttribute(self, "gen");
        var type = rest[0];
        if (type is PythonNoneValue)
        {
            if (Next(generator) is null)
                return PythonTruthValue.False;
            throw Stopped(generator, "generator didn't stop");
        }

        var value = rest[1];
        if (value is PythonNoneValue)
        {
            // Forcing instantiation is what lets a generator tell whether the exception it
            // raised is the one it was handed.
            value = Dispatched(type, []);
        }

        (bool HasValue, PythonValue Value) advanced;
        try
        {
            advanced = ThrowIn(generator, value);
        }
        catch (PythonRaisedException raised)
        {
            // The same exception object came back out, or the RuntimeError a generator
            // raises around reinjected StopIteration: neither may be suppressed here.
            if (ReferenceEquals(raised.Value, value))
                return PythonTruthValue.False;
            if (
                raised.Value.TypeName == "RuntimeError"
                && ReferenceEquals(raised.Value.Cause, value)
            )
                return PythonTruthValue.False;
            throw;
        }
        if (!advanced.HasValue)
        {
            // StopIteration: suppressed unless it is the exception that was thrown in.
            return ReferenceEquals(advanced.Value, value)
                ? PythonTruthValue.False
                : PythonTruthValue.True;
        }
        throw Stopped(generator, "generator didn't stop after throw()");
    }

    /// <summary>`gen.throw(value)`: the yielded value, or the exception the generator
    /// stopped with when it did not yield again.</summary>
    private static (bool HasValue, PythonValue Value) ThrowIn(
        PythonValue generator,
        PythonValue exception
    )
    {
        try
        {
            return (
                true,
                Dispatched(ManagedObjectProtocols.GetAttribute(generator, "throw"), [exception])
            );
        }
        catch (PythonRaisedException raised) when (raised.Value.TypeName == "StopIteration")
        {
            return (false, raised.Value);
        }
    }

    /// <summary>
    /// The refusal the source raises before closing the generator in a `finally`, so a
    /// generator that fails to close reports that failure instead.
    /// </summary>
    private static PythonRaisedException Stopped(PythonValue generator, string message)
    {
        var refusal = Runtime(message);
        try
        {
            Dispatched(ManagedObjectProtocols.GetAttribute(generator, "close"), []);
        }
        catch (PythonRaisedException raised)
        {
            return raised;
        }
        return refusal;
    }

    // -------------------------------------------------------------------------
    // closing, nullcontext and suppress
    // -------------------------------------------------------------------------

    /// <summary>`contextlib.closing`.</summary>
    private static PythonManagedTypeValue Closing => _closing ??= CreateClosing();

    private static PythonManagedTypeValue CreateClosing()
    {
        var type = ContextManagerClass("closing");
        type.Attributes["__doc__"] = new PythonTextValue(ClosingDoc);
        type.Attributes["__init__"] = PythonUserTypes.Method(
            "closing",
            "__init__",
            (receiver, arguments) =>
            {
                var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
                ManagedObjectProtocols.SetAttribute(self, "thing", rest[0]);
                return PythonNoneValue.Instance;
            },
            parameters: ["thing"]
        );
        type.Attributes["__enter__"] = Method(
            "closing",
            "__enter__",
            (receiver, _) =>
            {
                var (self, _) = PythonUserTypes.Bound(receiver, []);
                return ManagedObjectProtocols.GetAttribute(self, "thing");
            }
        );
        type.Attributes["__exit__"] = Method(
            "closing",
            "__exit__",
            (receiver, _) =>
            {
                var (self, _) = PythonUserTypes.Bound(receiver, []);
                var thing = ManagedObjectProtocols.GetAttribute(self, "thing");
                Dispatched(ManagedObjectProtocols.GetAttribute(thing, "close"), []);
                return PythonNoneValue.Instance;
            }
        );
        PythonAbc.Refresh(type);
        return type;
    }

    /// <summary>`contextlib.nullcontext`.</summary>
    private static PythonManagedTypeValue NullContext => _nullContext ??= CreateNullContext();

    private static PythonManagedTypeValue CreateNullContext()
    {
        var type = ContextManagerClass("nullcontext");
        type.Attributes["__doc__"] = new PythonTextValue(NullContextDoc);
        type.Attributes["__init__"] = PythonUserTypes.Method(
            "nullcontext",
            "__init__",
            (receiver, arguments) =>
            {
                var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
                ManagedObjectProtocols.SetAttribute(
                    self,
                    "enter_result",
                    rest.Count != 0 ? rest[0] : PythonNoneValue.Instance
                );
                return PythonNoneValue.Instance;
            },
            parameters: ["enter_result"],
            defaults: [PythonNoneValue.Instance]
        );
        type.Attributes["__enter__"] = Method(
            "nullcontext",
            "__enter__",
            (receiver, _) =>
            {
                var (self, _) = PythonUserTypes.Bound(receiver, []);
                return ManagedObjectProtocols.GetAttribute(self, "enter_result");
            }
        );
        type.Attributes["__exit__"] = Method(
            "nullcontext",
            "__exit__",
            (_, _) => PythonNoneValue.Instance
        );
        PythonAbc.Refresh(type);
        return type;
    }

    /// <summary>`contextlib.suppress`.</summary>
    private static PythonManagedTypeValue Suppress => _suppress ??= CreateSuppress();

    private static PythonManagedTypeValue CreateSuppress()
    {
        var type = ContextManagerClass("suppress");
        type.Attributes["__doc__"] = new PythonTextValue(SuppressDoc);
        type.Attributes["__init__"] = PythonUserTypes.Method(
            "suppress",
            "__init__",
            (receiver, arguments) =>
            {
                var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
                ManagedObjectProtocols.SetAttribute(
                    self,
                    "_exceptions",
                    new PythonTupleValue([.. rest])
                );
                return PythonNoneValue.Instance;
            }
        );
        type.Attributes["__enter__"] = Method(
            "suppress",
            "__enter__",
            (_, _) => PythonNoneValue.Instance
        );
        type.Attributes["__exit__"] = PythonUserTypes.Method(
            "suppress",
            "__exit__",
            (receiver, arguments) =>
            {
                var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
                if (rest.Count == 0 || rest[0] is PythonNoneValue)
                    return PythonNoneValue.Instance;
                var exceptions = ManagedObjectProtocols.GetAttribute(self, "_exceptions");
                return IsSubclassOf(rest[0], exceptions)
                    ? PythonTruthValue.True
                    : PythonTruthValue.False;
            },
            parameters: ["exctype", "excinst", "exctb"]
        );
        PythonAbc.Refresh(type);
        return type;
    }

    /// <summary>
    /// `issubclass(exctype, self._exceptions)` — which, unlike an `except` clause, is the
    /// plain subclass test and refuses anything that is not a class.
    /// </summary>
    private static bool IsSubclassOf(PythonValue type, PythonValue candidates)
    {
        if (candidates is PythonTupleValue tuple)
        {
            foreach (var candidate in tuple.Elements)
            {
                if (IsSubclassOf(type, candidate))
                    return true;
            }
            return false;
        }
        if (!IsTypeObject(candidates))
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "issubclass() arg 2 must be a class, a tuple of classes, or a union",
                default,
                "TypeError"
            );
        return ReferenceEquals(type, candidates)
            || PythonBuiltinTypes
                .GetMro(type)
                .Elements.Any(entry => ReferenceEquals(entry, candidates));
    }

    private static bool IsTypeObject(PythonValue value) =>
        value is PythonManagedTypeValue or PythonBuiltinTypeValue or PythonExceptionTypeValue;

    // -------------------------------------------------------------------------
    // ExitStack
    // -------------------------------------------------------------------------

    /// <summary>`contextlib._BaseExitStack`.</summary>
    private static PythonManagedTypeValue StackBase => _stackBase ??= CreateStackBase();

    private static PythonManagedTypeValue CreateStackBase()
    {
        var owner = "_BaseExitStack";
        var type = HeapType(
            owner,
            [PythonBuiltinFunctions.Object],
            [PythonBuiltinFunctions.Object],
            abstractMeta: false
        );
        type.Attributes["__doc__"] = new PythonTextValue(
            "A base class for ExitStack and AsyncExitStack."
        );
        type.Attributes["__init__"] = Method(
            owner,
            "__init__",
            (receiver, arguments) =>
            {
                var (self, _) = PythonUserTypes.Bound(receiver, arguments);
                ManagedObjectProtocols.SetAttribute(
                    self,
                    "_exit_callbacks",
                    new PythonListValue([])
                );
                return PythonNoneValue.Instance;
            }
        );
        type.Attributes["pop_all"] = Method(owner, "pop_all", PopAll);
        type.Attributes["push"] = PythonUserTypes.Method(
            owner,
            "push",
            Push,
            parameters: ["exit"],
            positionalOnly: 1
        );
        type.Attributes["enter_context"] = PythonUserTypes.Method(
            owner,
            "enter_context",
            EnterContext,
            parameters: ["cm"],
            positionalOnly: 1
        );
        // No declared signature: everything after the callable belongs to it, however many
        // arguments and keywords there are.
        type.Attributes["callback"] = PythonUserTypes.Method(
            owner,
            "callback",
            (receiver, arguments) => Callback(receiver, arguments, [], []),
            (receiver, positional, names, values) => Callback(receiver, positional, names, values)
        );
        return type;
    }

    /// <summary>`contextlib.ExitStack`.</summary>
    private static PythonManagedTypeValue ExitStack => _exitStack ??= CreateExitStack();

    private static PythonManagedTypeValue CreateExitStack()
    {
        var owner = "ExitStack";
        var type = ContextManagerClass(owner, StackBase);
        type.Attributes["__doc__"] = new PythonTextValue(ExitStackDoc);
        type.Attributes["close"] = Method(owner, "close", Close);
        type.Attributes["__enter__"] = Method(
            owner,
            "__enter__",
            (receiver, _) => receiver ?? PythonNoneValue.Instance
        );
        type.Attributes["__exit__"] = PythonUserTypes.Method(
            owner,
            "__exit__",
            (receiver, arguments) => Unwind(receiver, arguments),
            parameters: ["exc_type", "exc", "tb"]
        );
        PythonAbc.Refresh(type);
        return type;
    }

    /// <summary>`pop_all()`: the callbacks moved onto a new stack.</summary>
    private static PythonValue PopAll(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        var callbacks = ManagedObjectProtocols.GetAttribute(self, "_exit_callbacks");
        var moved = UserObjectProtocols.Dispatcher!.CallType(
            self is PythonManagedObjectValue instance
                ? instance.Type
                : PythonBuiltinTypes.GetRuntimeType(self),
            [],
            [],
            [],
            default
        );
        ManagedObjectProtocols.SetAttribute(moved, "_exit_callbacks", callbacks);
        ManagedObjectProtocols.SetAttribute(self, "_exit_callbacks", new PythonListValue([]));
        return moved;
    }

    /// <summary>`push(exit)`: a context manager's `__exit__` or a plain callable, in both
    /// cases as the callback the unwind will call.</summary>
    private static PythonValue Push(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        var exit = rest[0];
        var callback = ClassCarries(exit, "__exit__")
            ? new PythonProtocolFunctionValue(
                "_exit_wrapper",
                (_, details) =>
                    Dispatched(ManagedObjectProtocols.GetAttribute(exit, "__exit__"), details)
            )
            : exit;
        Callbacks(self).Elements.Add(callback);
        return exit;
    }

    /// <summary>`enter_context(cm)`: the manager entered, and its exit pushed.</summary>
    private static PythonValue EnterContext(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, arguments);
        var manager = rest[0];
        if (!ClassCarries(manager, "__enter__") || !ClassCarries(manager, "__exit__"))
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"'{Description(manager)}' object does not support the context manager protocol",
                default,
                "TypeError"
            );
        var result = Dispatched(ManagedObjectProtocols.GetAttribute(manager, "__enter__"), []);
        Callbacks(self)
            .Elements.Add(
                new PythonProtocolFunctionValue(
                    "_exit_wrapper",
                    (_, details) =>
                        Dispatched(
                            ManagedObjectProtocols.GetAttribute(manager, "__exit__"),
                            details
                        )
                )
            );
        return result;
    }

    /// <summary>
    /// `callback(callback, *args, **kwds)`: the callable registered so that the arguments it
    /// was given are the only ones it ever sees.
    /// </summary>
    private static PythonValue Callback(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var (self, rest) = PythonUserTypes.Bound(receiver, positional);
        if (rest.Count == 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "callback() missing required argument 'callback' (pos 1)",
                default,
                "TypeError"
            );
        var function = rest[0];
        var arguments = rest.Skip(1).ToList();
        // The wrapper answers None whatever the callback returns, which is what keeps a
        // callback from suppressing the exception it was called for.
        var wrapper = new PythonProtocolFunctionValue(
            "_exit_wrapper",
            (_, _) =>
            {
                if (keywordNames.Count == 0)
                    Dispatched(function, arguments);
                else
                    UserObjectProtocols.Dispatcher!.InvokeWithKeywords(
                        function,
                        arguments,
                        keywordNames,
                        keywordValues,
                        default
                    );
                return PythonNoneValue.Instance;
            }
        )
        {
            IsPythonMethod = true,
        };
        wrapper.Attributes["__wrapped__"] = function;
        Callbacks(self).Elements.Add(wrapper);
        return function;
    }

    /// <summary>`close()`: the stack unwound with no exception of its own.</summary>
    private static PythonValue Close(PythonValue? receiver, IReadOnlyList<PythonValue> arguments)
    {
        var (self, _) = PythonUserTypes.Bound(receiver, arguments);
        Unwind(
            self,
            [PythonNoneValue.Instance, PythonNoneValue.Instance, PythonNoneValue.Instance]
        );
        return PythonNoneValue.Instance;
    }

    /// <summary>
    /// `__exit__(*exc_details)`: the callbacks in LIFO order, each seeing the exception the
    /// one before it left behind, and the last failure on the way out.
    /// </summary>
    private static PythonTruthValue Unwind(
        PythonValue? receiver,
        IReadOnlyList<PythonValue> arguments
    )
    {
        var self = receiver ?? PythonNoneValue.Instance;
        var callbacks = Callbacks(self).Elements;
        PythonValue exception = arguments.Count > 1 ? arguments[1] : PythonNoneValue.Instance;
        var received = exception is not PythonNoneValue;
        var suppressed = false;
        PythonExceptionValue? pending = null;
        while (callbacks.Count != 0)
        {
            var callback = callbacks[^1];
            callbacks.RemoveAt(callbacks.Count - 1);
            try
            {
                var answer = Dispatched(
                    callback,
                    exception is PythonNoneValue
                        ?
                        [
                            PythonNoneValue.Instance,
                            PythonNoneValue.Instance,
                            PythonNoneValue.Instance,
                        ]
                        :
                        [
                            PythonBuiltinTypes.GetRuntimeType(exception),
                            exception,
                            PythonNoneValue.Instance,
                        ]
                );
                if (ManagedObjectProtocols.IsTrue(answer))
                {
                    suppressed = true;
                    pending = null;
                    exception = PythonNoneValue.Instance;
                }
            }
            catch (PythonRaisedException raised)
            {
                // The callbacks see the same chain of exceptions the nested `with`
                // statements they stand for would leave behind.
                if (pending is not null && raised.Value.Context is null)
                    raised.Value.Context = pending;
                pending = raised.Value;
                exception = raised.Value;
            }
        }
        if (pending is not null)
            throw new PythonRaisedException(pending);
        return PythonTruthValue.FromBoolean(received && suppressed);
    }

    private static PythonListValue Callbacks(PythonValue self) =>
        ManagedObjectProtocols.GetAttribute(self, "_exit_callbacks") as PythonListValue
        ?? throw ManagedObjectProtocols.Fault(
            "DPY4003",
            "ExitStack() was not initialized",
            default,
            "AttributeError"
        );

    /// <summary>`'{module}.{qualname}'` for the refusal an object that is no context manager
    /// gets.</summary>
    private static string Description(PythonValue manager)
    {
        var type = PythonBuiltinTypes.GetRuntimeType(manager);
        var module = TextOf(type, "__module__") ?? "builtins";
        var qualname = TextOf(type, "__qualname__") ?? TextOf(type, "__name__") ?? "object";
        return $"{module}.{qualname}";
    }

    // -------------------------------------------------------------------------
    // Shared helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// A class of the module: a heap type on `ABCMeta` over `AbstractContextManager`, built
    /// where `closing` and its neighbours are defined, so its instances are instances of the
    /// abstract base class too.
    /// </summary>
    private static PythonManagedTypeValue ContextManagerClass(
        string name,
        PythonManagedTypeValue? mixedIn = null
    )
    {
        var declared = new List<PythonValue>();
        if (mixedIn is { } baseClass)
            declared.Add(baseClass);
        declared.Add(AbstractManager);
        return HeapType(
            name,
            [.. declared],
            [.. declared, PythonAbc.AbcClass(), PythonBuiltinFunctions.Object]
        );
    }

    /// <summary>
    /// A class of the module: a heap type declared over the given bases, with the
    /// resolution order the source's own linearization gives it.
    /// </summary>
    private static PythonManagedTypeValue HeapType(
        string name,
        PythonValue[] declaredBases,
        PythonValue[] bases,
        bool abstractMeta = true
    )
    {
        var type = new PythonManagedTypeValue(name) { Module = "contextlib", QualName = name };
        if (abstractMeta)
            type.Metaclass = PythonAbc.Meta;
        type.SetDeclaredBases(new PythonTupleValue(declaredBases));
        type.SetResolutionOrder(new PythonTupleValue([type, .. bases]));
        type.Attributes["__module__"] = new PythonTextValue("contextlib");
        return type;
    }

    private static PythonProtocolFunctionValue Method(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body
    ) => PythonUserTypes.Method(owner, name, body);

    /// <summary>A callable of the module called the way a Python call reaches it.</summary>
    private static PythonValue Dispatched(
        PythonValue callable,
        IReadOnlyList<PythonValue> arguments
    ) => UserObjectProtocols.Dispatcher!.Invoke(callable, [.. arguments], default);

    /// <summary>`next(iterator)`, or null when the iterator is exhausted.</summary>
    private static PythonValue? Next(PythonValue iterator)
    {
        var walker = ManagedObjectProtocols.GetIterator(iterator);
        return ManagedObjectProtocols.TryGetNext(walker, out var value, default) ? value : null;
    }

    /// <summary>Whether the value's type carries the named attribute.</summary>
    private static bool ClassCarries(PythonValue value, string name)
    {
        try
        {
            ManagedObjectProtocols.GetAttribute(
                value
                    is PythonManagedTypeValue
                        or PythonBuiltinTypeValue
                        or PythonExceptionTypeValue
                    ? value
                    : PythonBuiltinTypes.GetRuntimeType(value),
                name
            );
            return true;
        }
        catch (Exception error)
            when (PythonNamespaceMapping.IsPythonException(error, "AttributeError"))
        {
            return false;
        }
    }

    /// <summary>The text of an attribute, or null when there is none.</summary>
    private static string? TextOf(PythonValue value, string name)
    {
        try
        {
            return ManagedObjectProtocols.GetAttribute(value, name) is PythonTextValue text
                ? text.Value
                : null;
        }
        catch (Exception error)
            when (PythonNamespaceMapping.IsPythonException(error, "AttributeError"))
        {
            return null;
        }
    }

    /// <summary>A `RuntimeError` the source raises with `from None`.</summary>
    private static PythonRaisedException Runtime(string message) =>
        new(new PythonExceptionValue("RuntimeError", message) { SuppressContext = true });

    private const string ContextManagerDoc =
        "@contextmanager decorator.\n\n"
        + "Typical usage:\n\n"
        + "    @contextmanager\n"
        + "    def some_generator(<arguments>):\n"
        + "        <setup>\n"
        + "        try:\n"
        + "            yield <value>\n"
        + "        finally:\n"
        + "            <cleanup>\n\n"
        + "This makes this:\n\n"
        + "    with some_generator(<arguments>) as <variable>:\n"
        + "        <body>\n\n"
        + "equivalent to this:\n\n"
        + "    <setup>\n"
        + "    try:\n"
        + "        <variable> = <value>\n"
        + "        <body>\n"
        + "    finally:\n"
        + "        <cleanup>\n";
}
