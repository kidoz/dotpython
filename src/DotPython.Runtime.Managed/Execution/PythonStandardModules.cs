using System.Numerics;
using DotPython.Language;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// Runtime-native standard-library modules seeded into every module catalog. These are
/// the layer-1 stdlib substrate: pure managed implementations with CPython 3.14 error
/// surfaces, no I/O, and no VM dependency (external objects reconstruct through their
/// Stable-ABI protocol, never through interpreter frames).
/// </summary>
internal static class PythonStandardModules
{
    private enum DeepCopyMode
    {
        Copy,
        PickleSnapshot,
        PickleRestore,
    }

    private const int MaximumFactorialInput = 100_000;
    private const int MaximumOpenFileLength = 8 * 1024 * 1024;

    internal static readonly PythonBuiltinTypeValue TemplateType = new(
        "Template",
        ConstructTemplate,
        (positional, names, _, span) =>
            names.Count == 0
                ? ConstructTemplate(positional, span)
                : throw TemplateArgumentError("Template.__new__ only accepts *args arguments", span)
    )
    {
        ModuleName = "string.templatelib",
        MatchArguments = new PythonTupleValue([]),
    };

    internal static readonly PythonBuiltinTypeValue InterpolationType = new(
        "Interpolation",
        ConstructInterpolation,
        PythonKeywordArguments.Adapt(
            "Interpolation",
            ["value", "expression", "conversion", "format_spec"],
            [
                null,
                new PythonTextValue(string.Empty),
                PythonNoneValue.Instance,
                new PythonTextValue(string.Empty),
            ],
            ConstructInterpolation,
            typeStyleErrors: true
        )
    )
    {
        ModuleName = "string.templatelib",
        MatchArguments = new PythonTupleValue([
            new PythonTextValue("value"),
            new PythonTextValue("expression"),
            new PythonTextValue("conversion"),
            new PythonTextValue("format_spec"),
        ]),
    };

    private static void InitializeTemplateLibrary(PythonGlobalNamespace globals)
    {
        globals.SetValue("Template", TemplateType);
        globals.SetValue("Interpolation", InterpolationType);
        globals.SetValue(
            "convert",
            new PythonBuiltinFunctionValue("convert", ConvertInterpolation).WithSignature(
                ["obj", "conversion"],
                [null, null],
                positionalOnly: 1
            )
        );
    }

    private static PythonTemplateValue ConstructTemplate(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        var strings = new List<string> { string.Empty };
        var interpolations = new List<PythonInterpolationValue>();
        foreach (var argument in arguments)
        {
            switch (argument)
            {
                case PythonTextValue text:
                    strings[^1] += text.Value;
                    break;
                case PythonInterpolationValue interpolation:
                    interpolations.Add(interpolation);
                    strings.Add(string.Empty);
                    break;
                default:
                    throw TemplateArgumentError(
                        $"Template.__new__ *args need to be of type 'str' or 'Interpolation', got {ManagedObjectProtocols.GetTypeName(argument)}",
                        span
                    );
            }
        }

        return new PythonTemplateValue([.. strings], [.. interpolations]);
    }

    private static PythonValue ConstructInterpolation(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count == 0)
        {
            throw TemplateArgumentError(
                "Interpolation() missing required argument 'value' (pos 1)",
                span
            );
        }

        if (arguments.Count > 4)
        {
            throw TemplateArgumentError(
                $"Interpolation() takes at most 4 arguments ({arguments.Count} given)",
                span
            );
        }

        var expression =
            arguments.Count > 1
                ? RequireInterpolationText("expression", arguments[1], span)
                : string.Empty;
        char? conversion = null;
        if (arguments.Count > 2 && arguments[2] is not PythonNoneValue)
        {
            var text = RequireInterpolationText("conversion", arguments[2], span);
            if (text is not ("s" or "r" or "a"))
            {
                throw new PythonRuntimeException(
                    "DPY4028",
                    "Interpolation() argument 'conversion' must be one of 's', 'a' or 'r'",
                    span,
                    "ValueError"
                );
            }

            conversion = text[0];
        }

        var format =
            arguments.Count > 3
                ? RequireInterpolationText("format_spec", arguments[3], span)
                : string.Empty;
        return new PythonInterpolationValue(arguments[0], expression, conversion, format);
    }

    private static string RequireInterpolationText(string name, PythonValue value, TextSpan span) =>
        value is PythonTextValue text
            ? text.Value
            : throw TemplateArgumentError(
                $"Interpolation() argument '{name}' must be str, not {ManagedObjectProtocols.GetTypeName(value)}",
                span
            );

    private static PythonValue ConvertInterpolation(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count != 2)
        {
            throw TemplateArgumentError("convert() requires an object and a conversion", span);
        }

        return arguments[1] switch
        {
            PythonNoneValue => arguments[0],
            PythonTextValue { Value: "s" } => new PythonTextValue(arguments[0].ToDisplayString()),
            PythonTextValue { Value: "r" } => new PythonTextValue(
                arguments[0].ToRepresentationString()
            ),
            PythonTextValue { Value: "a" } => PythonBuiltinFunctions.Ascii([arguments[0]], span),
            var invalid => throw new PythonRuntimeException(
                "DPY4028",
                $"invalid conversion specifier: {invalid.ToDisplayString()}",
                span,
                "ValueError"
            ),
        };
    }

    private static PythonRuntimeException TemplateArgumentError(string message, TextSpan span) =>
        new("DPY4028", message, span, "TypeError");

    private static bool IsWithinSearchRoots(string fullPath, IReadOnlyList<string> searchRoots) =>
        searchRoots.Any(root =>
        {
            var fullRoot = Path.GetFullPath(root);
            return fullPath.StartsWith(fullRoot, StringComparison.Ordinal)
                && (
                    fullPath.Length == fullRoot.Length
                    || fullRoot.EndsWith('/')
                    || fullPath[fullRoot.Length] == '/'
                );
        });

    /// <summary>
    /// Creates the `open` builtin: read-only text mode, a capability scoped to the
    /// registered module search roots like the os.path probes.
    /// </summary>
    internal static PythonBuiltinFunctionValue CreateOpenBuiltin(
        IReadOnlyList<string> searchRoots
    ) =>
        new(
            "open",
            (arguments, span) =>
            {
                if (arguments.Count == 0)
                {
                    throw new PythonRuntimeException(
                        "DPY4037",
                        "open() missing required argument 'file' (pos 1)",
                        span,
                        "TypeError"
                    );
                }

                if (arguments.Count > 2)
                {
                    throw new PythonRuntimeException(
                        "DPY4037",
                        "open() arguments beyond file and mode are not supported in this runtime slice.",
                        span
                    );
                }

                if (arguments[0] is not PythonTextValue pathText)
                {
                    throw new PythonRuntimeException(
                        "DPY4037",
                        $"expected str, bytes or os.PathLike object, not {ManagedObjectProtocols.GetTypeName(arguments[0])}",
                        span,
                        "TypeError"
                    );
                }

                var mode = "r";
                if (arguments.Count == 2)
                {
                    if (arguments[1] is not PythonTextValue modeText)
                    {
                        throw new PythonRuntimeException(
                            "DPY4037",
                            $"open() argument 'mode' must be str, not {ManagedObjectProtocols.GetTypeName(arguments[1])}",
                            span,
                            "TypeError"
                        );
                    }

                    mode = modeText.Value;
                }

                ValidateOpenMode(mode, span);
                var fullPath = Path.GetFullPath(pathText.Value);
                if (!IsWithinSearchRoots(fullPath, searchRoots))
                {
                    throw new PythonRuntimeException(
                        "DPY4037",
                        "open() outside the registered module search roots is not permitted in this runtime slice.",
                        span,
                        "PermissionError"
                    );
                }

                if (Directory.Exists(fullPath))
                {
                    throw new PythonRuntimeException(
                        "DPY4037",
                        $"[Errno 21] Is a directory: '{pathText.Value}'",
                        span,
                        "IsADirectoryError"
                    );
                }

                if (!File.Exists(fullPath))
                {
                    throw new PythonRuntimeException(
                        "DPY4037",
                        $"[Errno 2] No such file or directory: '{pathText.Value}'",
                        span,
                        "FileNotFoundError"
                    );
                }

                string content;
                try
                {
                    if (new FileInfo(fullPath).Length > MaximumOpenFileLength)
                    {
                        throw new PythonRuntimeException(
                            "DPY4037",
                            $"open() beyond the {MaximumOpenFileLength} byte limit is not supported in this runtime slice.",
                            span
                        );
                    }

                    content = File.ReadAllText(fullPath);
                }
                catch (IOException exception)
                {
                    throw new PythonRuntimeException("DPY4037", exception.Message, span, "OSError");
                }

                // Universal newlines: text mode translates both CRLF and lone CR.
                content = content
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n');
                return new PythonFileValue(pathText.Value, mode, content);
            }
        );

    private static void ValidateOpenMode(string mode, TextSpan span)
    {
        // CPython-invalid modes get CPython's ValueError; CPython-valid modes beyond
        // the read-only text subset are a restriction of this slice.
        var seen = new HashSet<char>();
        foreach (var character in mode)
        {
            if (!"rwxab+t".Contains(character, StringComparison.Ordinal) || !seen.Add(character))
            {
                throw new PythonRuntimeException(
                    "DPY4037",
                    $"invalid mode: '{mode}'",
                    span,
                    "ValueError"
                );
            }
        }

        if (mode is not ("r" or "rt" or "tr"))
        {
            throw new PythonRuntimeException(
                "DPY4037",
                $"open() mode '{mode}' is not supported in this runtime slice.",
                span
            );
        }
    }

    internal static void AddTo(
        Dictionary<string, PythonModuleDefinition> modules,
        IReadOnlyList<string> searchRoots
    )
    {
        modules["types"] = PythonModuleDefinition.Native(
            "<dotpython types>",
            false,
            globals => globals.SetValue("MappingProxyType", PythonMappingProxies.Type)
        );
        modules["math"] = PythonModuleDefinition.Native(
            "<dotpython math>",
            isPackage: false,
            InitializeMath
        );
        modules["copy"] = PythonModuleDefinition.Native(
            "<dotpython copy>",
            isPackage: false,
            InitializeCopy
        );
        modules["pickle"] = PythonModuleDefinition.Native(
            "<dotpython pickle>",
            isPackage: false,
            InitializePickle
        );
        modules["os"] = PythonModuleDefinition.Native(
            "<dotpython os>",
            isPackage: true,
            globals => InitializeOs(globals, searchRoots)
        );
        modules["os.path"] = PythonModuleDefinition.Native(
            "<dotpython os.path>",
            isPackage: false,
            globals => InitializeOsPath(globals, searchRoots)
        );
        modules["sys"] = PythonModuleDefinition.Native(
            "<dotpython sys>",
            isPackage: false,
            InitializeSys
        );
        modules["__future__"] = PythonModuleDefinition.Native(
            "<dotpython __future__>",
            isPackage: false,
            InitializeFuture
        );
        // Only the template-string API is provided; the rest of string's API is
        // not part of the managed standard-library slice yet.
        modules["string"] = PythonModuleDefinition.Native(
            "<dotpython string>",
            isPackage: true,
            _ => { }
        );
        modules["string.templatelib"] = PythonModuleDefinition.Native(
            "<dotpython string.templatelib>",
            isPackage: false,
            InitializeTemplateLibrary
        );
    }

    /// <summary>`sys.exit` and the `exit`/`quit` conveniences: raise SystemExit.</summary>
    internal static PythonBuiltinFunctionValue CreateExitBuiltin(string name) =>
        new(
            name,
            (arguments, span) =>
            {
                if (arguments.Count > 1)
                {
                    throw new PythonRuntimeException(
                        "DPY4003",
                        $"{name} expected at most 1 argument, got {arguments.Count}",
                        span,
                        "TypeError"
                    );
                }

                var code = arguments.Count == 0 ? PythonNoneValue.Instance : arguments[0];
                throw new PythonRaisedException(
                    new PythonExceptionValue(
                        "SystemExit",
                        code is PythonNoneValue ? string.Empty : code.ToDisplayString()
                    )
                    {
                        Arguments = arguments.Count == 0 ? [] : [code],
                    }
                );
            }
        );

    private static void InitializeSys(PythonGlobalNamespace globals)
    {
        var dispatcher = UserObjectProtocols.Dispatcher;
        globals.SetValue(
            "argv",
            new PythonListValue([
                .. (dispatcher?.Arguments ?? []).Select(argument =>
                    (PythonValue)new PythonTextValue(argument)
                ),
            ])
        );
        globals.SetValue(
            "path",
            new PythonListValue([
                .. (dispatcher?.SearchRoots ?? []).Select(root =>
                    (PythonValue)new PythonTextValue(root)
                ),
            ])
        );
        globals.SetValue("stdout", new PythonStreamValue(PythonStreamKind.StandardOutput));
        globals.SetValue("stderr", new PythonStreamValue(PythonStreamKind.StandardError));
        globals.SetValue("stdin", new PythonStreamValue(PythonStreamKind.StandardInput));
        globals.SetValue("exit", CreateExitBuiltin("exit"));
        var version = PythonLanguageVersion.Current;
        globals.SetValue(
            "version",
            new PythonTextValue($"{version.Major}.{version.Minor}.0 (DotPython managed runtime)")
        );
        globals.SetValue(
            "version_info",
            new PythonTupleValue([
                PythonWholeNumberValue.Create(version.Major),
                PythonWholeNumberValue.Create(version.Minor),
                PythonWholeNumberValue.Create(0),
                new PythonTextValue("final"),
                PythonWholeNumberValue.Create(0),
            ])
        );
        globals.SetValue(
            "platform",
            new PythonTextValue(
                OperatingSystem.IsWindows() ? "win32"
                : OperatingSystem.IsMacOS() ? "darwin"
                : OperatingSystem.IsLinux() ? "linux"
                : "unknown"
            )
        );
        globals.SetValue("maxsize", PythonWholeNumberValue.Create(long.MaxValue));
        globals.SetValue(
            "byteorder",
            new PythonTextValue(BitConverter.IsLittleEndian ? "little" : "big")
        );
        globals.SetValue("implementation", new PythonTextValue("dotpython"));
        var recursionLimit = PythonWholeNumberValue.Create(1000);
        globals.SetValue(
            "getrecursionlimit",
            new PythonBuiltinFunctionValue("getrecursionlimit", (_, _) => recursionLimit)
        );
        globals.SetValue(
            "setrecursionlimit",
            new PythonBuiltinFunctionValue(
                "setrecursionlimit",
                (arguments, span) =>
                {
                    // Recorded for introspection only; the managed VM bounds recursion
                    // through its instruction budget and frame growth instead.
                    if (arguments.Count != 1 || arguments[0] is not PythonWholeNumberValue limit)
                    {
                        throw new PythonRuntimeException(
                            "DPY4003",
                            "setrecursionlimit() argument must be an integer",
                            span,
                            "TypeError"
                        );
                    }

                    if (limit.Value.Sign <= 0)
                    {
                        throw new PythonRuntimeException(
                            "DPY4003",
                            "recursion limit must be greater or equal than 1",
                            span,
                            "ValueError"
                        );
                    }

                    recursionLimit = limit;
                    return PythonNoneValue.Instance;
                }
            )
        );
        globals.SetValue(
            "intern",
            new PythonBuiltinFunctionValue(
                "intern",
                (arguments, span) =>
                    arguments.Count == 1 && arguments[0] is PythonTextValue text
                        ? text
                        : throw new PythonRuntimeException(
                            "DPY4003",
                            "intern() argument must be str",
                            span,
                            "TypeError"
                        )
            )
        );
    }

    private static readonly string[] FutureFeatures =
    [
        "nested_scopes",
        "generators",
        "division",
        "absolute_import",
        "with_statement",
        "print_function",
        "unicode_literals",
        "barry_as_FLUFL",
        "generator_stop",
        "annotations",
    ];

    private static void InitializeFuture(PythonGlobalNamespace globals)
    {
        // Every feature is already the language default, so the names exist for
        // `from __future__ import ...` and carry no behaviour.
        var featureType = new PythonManagedTypeValue("_Feature") { Module = "__future__" };
        foreach (var feature in FutureFeatures)
        {
            globals.SetValue(feature, new PythonManagedObjectValue(featureType));
        }

        globals.SetValue(
            "all_feature_names",
            new PythonListValue([
                .. FutureFeatures.Select(name => (PythonValue)new PythonTextValue(name)),
            ])
        );
    }

    private static void InitializeOs(
        PythonGlobalNamespace globals,
        IReadOnlyList<string> searchRoots
    )
    {
        var pathGlobals = new PythonGlobalNamespace();
        pathGlobals.SetValue("__name__", new PythonTextValue("os.path"));
        pathGlobals.SetValue("__package__", new PythonTextValue("os"));
        InitializeOsPath(pathGlobals, searchRoots);
        globals.SetValue("path", new PythonModuleValue("os.path", pathGlobals));
        globals.SetValue("sep", new PythonTextValue("/"));
    }

    private static void InitializeOsPath(
        PythonGlobalNamespace globals,
        IReadOnlyList<string> searchRoots
    )
    {
        globals.SetValue(
            "join",
            new PythonBuiltinFunctionValue(
                "join",
                (arguments, span) =>
                {
                    if (arguments.Count == 0)
                    {
                        throw new PythonRuntimeException(
                            "DPY4028",
                            "os.path.join() requires at least one argument.",
                            span,
                            "TypeError"
                        );
                    }

                    var joined = RequirePathText("join", arguments[0], span);
                    for (var index = 1; index < arguments.Count; index++)
                    {
                        var part = RequirePathText("join", arguments[index], span);
                        if (part.StartsWith('/'))
                        {
                            joined = part;
                        }
                        else if (joined.Length == 0 || joined.EndsWith('/'))
                        {
                            joined += part;
                        }
                        else
                        {
                            joined = joined + "/" + part;
                        }
                    }

                    return new PythonTextValue(joined);
                }
            )
        );
        globals.SetValue(
            "dirname",
            new PythonBuiltinFunctionValue(
                "dirname",
                (arguments, span) =>
                {
                    var path = RequireSinglePath("dirname", arguments, span);
                    var separator = path.LastIndexOf('/');
                    return new PythonTextValue(
                        separator < 0 ? string.Empty
                        : separator == 0 ? "/"
                        : path[..separator]
                    );
                }
            )
        );
        globals.SetValue(
            "basename",
            new PythonBuiltinFunctionValue(
                "basename",
                (arguments, span) =>
                {
                    var path = RequireSinglePath("basename", arguments, span);
                    return new PythonTextValue(path[(path.LastIndexOf('/') + 1)..]);
                }
            )
        );
        globals.SetValue(
            "exists",
            PathProbe("exists", searchRoots, path => File.Exists(path) || Directory.Exists(path))
        );
        globals.SetValue("isfile", PathProbe("isfile", searchRoots, File.Exists));
        globals.SetValue("isdir", PathProbe("isdir", searchRoots, Directory.Exists));
    }

    private static PythonBuiltinFunctionValue PathProbe(
        string name,
        IReadOnlyList<string> searchRoots,
        Func<string, bool> probe
    ) =>
        new(
            name,
            (arguments, span) =>
            {
                var path = RequireSinglePath(name, arguments, span);
                // Filesystem probes are a capability scoped to the registered module
                // search roots; nothing outside them is observable.
                var fullPath = Path.GetFullPath(path);
                if (!IsWithinSearchRoots(fullPath, searchRoots))
                {
                    throw new PythonRuntimeException(
                        "DPY4028",
                        $"os.path.{name}() outside the registered module search roots is not permitted in this runtime slice.",
                        span,
                        "PermissionError"
                    );
                }

                return PythonTruthValue.FromBoolean(probe(fullPath));
            }
        );

    private static string RequireSinglePath(
        string name,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count != 1)
        {
            throw new PythonRuntimeException(
                "DPY4028",
                $"os.path.{name}() takes exactly one argument ({arguments.Count} given).",
                span,
                "TypeError"
            );
        }

        return RequirePathText(name, arguments[0], span);
    }

    private static string RequirePathText(string name, PythonValue value, TextSpan span) =>
        value is PythonTextValue text
            ? text.Value
            : throw new PythonRuntimeException(
                "DPY4028",
                $"os.path.{name}() arguments must be str, not {ManagedObjectProtocols.GetTypeName(value)}.",
                span,
                "TypeError"
            );

    private static void InitializePickle(PythonGlobalNamespace globals)
    {
        // In-process round-trip pickling: dumps returns an opaque token that only
        // this module instance's loads understands; dumps snapshots the object
        // graph and loads reconstructs a fresh deep copy of that snapshot
        // through the same machinery as copy.deepcopy (native objects via
        // __reduce__). The byte payload is deliberately not CPython's wire format.
        var stash = new Dictionary<long, PythonValue>();
        var tokenPrefix = $"DPYPKL:{Guid.NewGuid():N}:";
        var nextToken = 0L;
        globals.SetValue("HIGHEST_PROTOCOL", PythonWholeNumberValue.Create(5));
        globals.SetValue(
            "dumps",
            new PythonBuiltinFunctionValue(
                "dumps",
                (arguments, span) =>
                {
                    if (arguments.Count is not (1 or 2))
                    {
                        throw new PythonRuntimeException(
                            "DPY4028",
                            $"pickle.dumps() takes 1 to 2 arguments ({arguments.Count} given).",
                            span,
                            "TypeError"
                        );
                    }

                    if (stash.Count >= 1024)
                    {
                        throw new PythonRuntimeException(
                            "DPY4028",
                            "pickle.dumps() exceeded the 1024 live-token limit of this runtime slice.",
                            span,
                            "RuntimeError"
                        );
                    }

                    var snapshot = DeepCopy(
                        arguments[0],
                        new Dictionary<PythonValue, PythonValue>(
                            ReferenceEqualityComparer.Instance
                        ),
                        span,
                        mode: DeepCopyMode.PickleSnapshot
                    );
                    var token = nextToken++;
                    stash[token] = snapshot;
                    return new PythonByteSequenceValue(
                        System.Text.Encoding.ASCII.GetBytes(
                            $"{tokenPrefix}{token.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                        )
                    );
                }
            )
        );
        globals.SetValue(
            "loads",
            new PythonBuiltinFunctionValue(
                "loads",
                (arguments, span) =>
                {
                    if (arguments.Count != 1 || arguments[0] is not PythonByteSequenceValue payload)
                    {
                        throw new PythonRuntimeException(
                            "DPY4028",
                            "pickle.loads() requires one bytes argument.",
                            span,
                            "TypeError"
                        );
                    }

                    var text = System.Text.Encoding.ASCII.GetString(payload.Value);
                    if (
                        !text.StartsWith(tokenPrefix, StringComparison.Ordinal)
                        || !long.TryParse(
                            text[tokenPrefix.Length..],
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var token
                        )
                        || !stash.TryGetValue(token, out var value)
                    )
                    {
                        throw new PythonRuntimeException(
                            "DPY4028",
                            "pickle.loads() only accepts tokens produced by this runtime's pickle.dumps().",
                            span,
                            "ValueError"
                        );
                    }

                    return DeepCopy(
                        value,
                        new Dictionary<PythonValue, PythonValue>(
                            ReferenceEqualityComparer.Instance
                        ),
                        span,
                        mode: DeepCopyMode.PickleRestore
                    );
                }
            )
        );
    }

    private static void InitializeMath(PythonGlobalNamespace globals)
    {
        globals.SetValue("pi", new PythonFloatingPointValue(Math.PI));
        globals.SetValue("e", new PythonFloatingPointValue(Math.E));
        globals.SetValue("tau", new PythonFloatingPointValue(Math.Tau));
        globals.SetValue("inf", new PythonFloatingPointValue(double.PositiveInfinity));
        globals.SetValue("nan", new PythonFloatingPointValue(double.NaN));

        MathFunction(globals, "sqrt", 1, 1, (arguments, span) => Sqrt(arguments[0], span));
        MathFunction(
            globals,
            "floor",
            1,
            1,
            (arguments, span) => RealToInteger(arguments[0], Math.Floor, span)
        );
        MathFunction(
            globals,
            "ceil",
            1,
            1,
            (arguments, span) => RealToInteger(arguments[0], Math.Ceiling, span)
        );
        MathFunction(
            globals,
            "trunc",
            1,
            1,
            (arguments, span) => RealToInteger(arguments[0], Math.Truncate, span)
        );
        MathFunction(
            globals,
            "fabs",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(Math.Abs(RequireReal(arguments[0], span)))
        );
        MathFunction(
            globals,
            "copysign",
            2,
            2,
            (arguments, span) =>
                new PythonFloatingPointValue(
                    Math.CopySign(RequireReal(arguments[0], span), RequireReal(arguments[1], span))
                )
        );
        MathFunction(globals, "gcd", 0, int.MaxValue, GreatestCommonDivisor);
        MathFunction(globals, "lcm", 0, int.MaxValue, LeastCommonMultiple);
        MathFunction(globals, "factorial", 1, 1, Factorial);
        MathFunction(globals, "comb", 2, 2, Combinations);
        MathFunction(globals, "perm", 2, 2, Permutations);
        MathFunction(globals, "log", 1, 2, Logarithm);
        MathFunction(
            globals,
            "log2",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(Math.Log2(RequirePositive(arguments[0], span)))
        );
        MathFunction(
            globals,
            "log10",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(Math.Log10(RequirePositive(arguments[0], span)))
        );
        MathFunction(
            globals,
            "exp",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(Math.Exp(RequireReal(arguments[0], span)))
        );
        MathFunction(
            globals,
            "pow",
            2,
            2,
            (arguments, span) =>
                new PythonFloatingPointValue(
                    Math.Pow(RequireReal(arguments[0], span), RequireReal(arguments[1], span))
                )
        );
        MathFunction(globals, "hypot", 0, int.MaxValue, Hypotenuse);
        MathFunction(
            globals,
            "degrees",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(RequireReal(arguments[0], span) * (180.0 / Math.PI))
        );
        MathFunction(
            globals,
            "radians",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(RequireReal(arguments[0], span) * (Math.PI / 180.0))
        );
        MathFunction(
            globals,
            "sin",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(Math.Sin(RequireReal(arguments[0], span)))
        );
        MathFunction(
            globals,
            "cos",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(Math.Cos(RequireReal(arguments[0], span)))
        );
        MathFunction(
            globals,
            "tan",
            1,
            1,
            (arguments, span) =>
                new PythonFloatingPointValue(Math.Tan(RequireReal(arguments[0], span)))
        );
        MathFunction(
            globals,
            "fmod",
            2,
            2,
            (arguments, span) =>
                new PythonFloatingPointValue(
                    RequireReal(arguments[0], span) % RequireReal(arguments[1], span)
                )
        );
        MathFunction(
            globals,
            "isnan",
            1,
            1,
            (arguments, span) =>
                PythonTruthValue.FromBoolean(double.IsNaN(RequireReal(arguments[0], span)))
        );
        MathFunction(
            globals,
            "isinf",
            1,
            1,
            (arguments, span) =>
                PythonTruthValue.FromBoolean(double.IsInfinity(RequireReal(arguments[0], span)))
        );
        MathFunction(
            globals,
            "isfinite",
            1,
            1,
            (arguments, span) =>
                PythonTruthValue.FromBoolean(double.IsFinite(RequireReal(arguments[0], span)))
        );
        MathFunction(globals, "isclose", 2, 4, IsClose);
        globals.SetValue(
            "isclose",
            ((PythonBuiltinFunctionValue)GetGlobal(globals, "isclose")).WithSignature(
                ["a", "b", "rel_tol", "abs_tol"],
                [null, null, new PythonFloatingPointValue(1e-09), new PythonFloatingPointValue(0)],
                positionalOnly: 2
            )
        );
    }

    private static PythonValue GetGlobal(PythonGlobalNamespace globals, string name) =>
        globals.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException($"Global '{name}' was not seeded.");

    private static void MathFunction(
        PythonGlobalNamespace globals,
        string name,
        int minimumArguments,
        int maximumArguments,
        Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> implementation
    ) =>
        globals.SetValue(
            name,
            new PythonBuiltinFunctionValue(
                name,
                (arguments, span) =>
                {
                    if (arguments.Count < minimumArguments || arguments.Count > maximumArguments)
                    {
                        throw new PythonRuntimeException(
                            "DPY4028",
                            $"math.{name}() takes {minimumArguments} to {maximumArguments} arguments ({arguments.Count} given).",
                            span,
                            "TypeError"
                        );
                    }

                    return implementation(arguments, span);
                }
            )
        );

    private static PythonFloatingPointValue Sqrt(PythonValue value, TextSpan span)
    {
        var real = RequireReal(value, span);
        if (real < 0)
        {
            throw new PythonRuntimeException(
                "DPY4028",
                $"expected a nonnegative input, got {new PythonFloatingPointValue(real).ToDisplayString()}",
                span,
                "ValueError"
            );
        }

        return new PythonFloatingPointValue(Math.Sqrt(real));
    }

    private static PythonWholeNumberValue RealToInteger(
        PythonValue value,
        Func<double, double> round,
        TextSpan span
    )
    {
        var promoted = PromoteTruthValue(value);
        if (promoted is PythonWholeNumberValue whole)
        {
            return whole;
        }

        var real = RequireReal(promoted, span);
        if (double.IsInfinity(real))
        {
            throw new PythonRuntimeException(
                "DPY4028",
                "cannot convert float infinity to integer",
                span,
                "OverflowError"
            );
        }

        if (double.IsNaN(real))
        {
            throw new PythonRuntimeException(
                "DPY4028",
                "cannot convert float NaN to integer",
                span,
                "ValueError"
            );
        }

        return PythonWholeNumberValue.Create(new BigInteger(round(real)));
    }

    private static PythonValue GreatestCommonDivisor(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        var result = BigInteger.Zero;
        foreach (var argument in arguments)
        {
            result = BigInteger.GreatestCommonDivisor(result, RequireInteger(argument, span));
        }

        return PythonWholeNumberValue.Create(BigInteger.Abs(result));
    }

    private static PythonValue LeastCommonMultiple(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        var result = BigInteger.One;
        foreach (var argument in arguments)
        {
            var value = BigInteger.Abs(RequireInteger(argument, span));
            if (value.IsZero)
            {
                return PythonWholeNumberValue.Create(BigInteger.Zero);
            }

            result = result / BigInteger.GreatestCommonDivisor(result, value) * value;
        }

        return PythonWholeNumberValue.Create(result);
    }

    private static PythonValue Factorial(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var value = RequireInteger(arguments[0], span);
        if (value.Sign < 0)
        {
            throw new PythonRuntimeException(
                "DPY4028",
                "factorial() not defined for negative values",
                span,
                "ValueError"
            );
        }

        if (value > MaximumFactorialInput)
        {
            throw new PythonRuntimeException(
                "DPY4028",
                $"factorial() argument should not exceed {MaximumFactorialInput} in this runtime slice.",
                span,
                "OverflowError"
            );
        }

        var result = BigInteger.One;
        for (var factor = new BigInteger(2); factor <= value; factor++)
        {
            result *= factor;
        }

        return PythonWholeNumberValue.Create(result);
    }

    private static PythonValue Combinations(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var total = RequireNonNegativeInteger("comb", arguments[0], span);
        var chosen = RequireNonNegativeInteger("comb", arguments[1], span);
        if (chosen > total)
        {
            return PythonWholeNumberValue.Create(BigInteger.Zero);
        }

        if (chosen > total - chosen)
        {
            chosen = total - chosen;
        }

        GuardIterationCount("comb", chosen, span);
        var result = BigInteger.One;
        for (var index = BigInteger.One; index <= chosen; index++)
        {
            result = result * (total - chosen + index) / index;
        }

        return PythonWholeNumberValue.Create(result);
    }

    private static PythonValue Permutations(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var total = RequireNonNegativeInteger("perm", arguments[0], span);
        var chosen = RequireNonNegativeInteger("perm", arguments[1], span);
        if (chosen > total)
        {
            return PythonWholeNumberValue.Create(BigInteger.Zero);
        }

        GuardIterationCount("perm", chosen, span);
        var result = BigInteger.One;
        for (var index = BigInteger.Zero; index < chosen; index++)
        {
            result *= total - index;
        }

        return PythonWholeNumberValue.Create(result);
    }

    private static void GuardIterationCount(string name, BigInteger count, TextSpan span)
    {
        if (count > MaximumFactorialInput)
        {
            throw new PythonRuntimeException(
                "DPY4028",
                $"math.{name}() computation is too large for this runtime slice.",
                span,
                "OverflowError"
            );
        }
    }

    private static PythonValue Logarithm(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var value = RequirePositive(arguments[0], span);
        if (arguments.Count == 1)
        {
            return new PythonFloatingPointValue(Math.Log(value));
        }

        var logBase = RequirePositive(arguments[1], span);
        return new PythonFloatingPointValue(Math.Log(value) / Math.Log(logBase));
    }

    private static PythonValue Hypotenuse(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var sum = 0.0;
        foreach (var argument in arguments)
        {
            var real = RequireReal(argument, span);
            sum += real * real;
        }

        return new PythonFloatingPointValue(Math.Sqrt(sum));
    }

    private static PythonValue IsClose(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        var left = RequireReal(arguments[0], span);
        var right = RequireReal(arguments[1], span);
        if (left == right)
        {
            return PythonTruthValue.True;
        }

        if (double.IsInfinity(left) || double.IsInfinity(right))
        {
            return PythonTruthValue.False;
        }

        var relativeTolerance = arguments.Count > 2 ? RequireReal(arguments[2], span) : 1e-09;
        var absoluteTolerance = arguments.Count > 3 ? RequireReal(arguments[3], span) : 0.0;
        if (relativeTolerance < 0 || absoluteTolerance < 0)
        {
            throw new PythonRuntimeException(
                "DPY4028",
                "tolerances must be non-negative",
                span,
                "ValueError"
            );
        }

        var difference = Math.Abs(left - right);
        return PythonTruthValue.FromBoolean(
            difference
                <= Math.Max(
                    relativeTolerance * Math.Max(Math.Abs(left), Math.Abs(right)),
                    absoluteTolerance
                )
        );
    }

    private static double RequireReal(PythonValue value, TextSpan span) =>
        PromoteTruthValue(value) switch
        {
            PythonWholeNumberValue whole => (double)whole.Value,
            PythonFloatingPointValue floatingPoint => floatingPoint.Value,
            var other => throw new PythonRuntimeException(
                "DPY4028",
                $"must be real number, not {ManagedObjectProtocols.GetTypeName(other)}",
                span,
                "TypeError"
            ),
        };

    private static double RequirePositive(PythonValue value, TextSpan span)
    {
        var real = RequireReal(value, span);
        if (real <= 0)
        {
            throw new PythonRuntimeException(
                "DPY4028",
                "expected a positive input",
                span,
                "ValueError"
            );
        }

        return real;
    }

    private static BigInteger RequireInteger(PythonValue value, TextSpan span) =>
        PromoteTruthValue(value) switch
        {
            PythonWholeNumberValue whole => whole.Value,
            var other => throw new PythonRuntimeException(
                "DPY4028",
                $"'{ManagedObjectProtocols.GetTypeName(other)}' object cannot be interpreted as an integer",
                span,
                "TypeError"
            ),
        };

    private static BigInteger RequireNonNegativeInteger(
        string name,
        PythonValue value,
        TextSpan span
    )
    {
        var integer = RequireInteger(value, span);
        if (integer.Sign < 0)
        {
            throw new PythonRuntimeException(
                "DPY4028",
                $"{name}() requires non-negative arguments.",
                span,
                "ValueError"
            );
        }

        return integer;
    }

    private static PythonValue PromoteTruthValue(PythonValue value) =>
        value is PythonTruthValue truth
            ? PythonWholeNumberValue.Create(truth.Value ? BigInteger.One : BigInteger.Zero)
            : value;

    private static void InitializeCopy(PythonGlobalNamespace globals)
    {
        globals.SetValue(
            "copy",
            new PythonBuiltinFunctionValue(
                "copy",
                (arguments, span) =>
                {
                    RequireCopyArguments("copy", arguments, span);
                    return ShallowCopy(arguments[0], span);
                }
            )
        );
        globals.SetValue(
            "deepcopy",
            new PythonBuiltinFunctionValue(
                "deepcopy",
                (arguments, span) =>
                {
                    RequireCopyArguments("deepcopy", arguments, span);
                    return DeepCopy(
                        arguments[0],
                        new Dictionary<PythonValue, PythonValue>(
                            ReferenceEqualityComparer.Instance
                        ),
                        span
                    );
                }
            )
        );
    }

    private static void RequireCopyArguments(
        string name,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count != 1)
        {
            throw new PythonRuntimeException(
                "DPY4028",
                $"copy.{name}() takes exactly one argument ({arguments.Count} given).",
                span,
                "TypeError"
            );
        }
    }

    private static PythonValue ShallowCopy(PythonValue value, TextSpan span) =>
        value switch
        {
            PythonListValue list => new PythonListValue([.. list.Elements]),
            PythonDictionaryValue dictionary => new PythonDictionaryValue([
                .. dictionary.Items.Select(item => new PythonDictionaryItemValue(
                    item.Key,
                    item.Value,
                    item.KeyHash
                )),
            ]),
            PythonSetValue { IsFrozen: false } set => new PythonSetValue([.. set.Elements]),
            PythonTemplateValue template => new PythonTemplateValue(
                template.Strings,
                template.Interpolations
            ),
            PythonInterpolationValue interpolation => interpolation with { },
            PythonManagedObjectValue instance => CopyInstance(instance, deep: false, null, span),
            PythonExceptionValue exception => CopyException(exception, deep: false, null, span),
            PythonExternalObjectValue external => ReconstructExternal(external, null, span),
            _ => value,
        };

    private static PythonValue DeepCopy(
        PythonValue value,
        Dictionary<PythonValue, PythonValue> memo,
        TextSpan span,
        DeepCopyMode mode = DeepCopyMode.Copy
    )
    {
        if (memo.TryGetValue(value, out var existing))
        {
            if (mode != DeepCopyMode.PickleSnapshot && existing is PythonPickleReductionValue)
            {
                throw new PythonRuntimeException(
                    "DPY4028",
                    "pickle.loads() does not support recursive native reduction arguments.",
                    span,
                    "ValueError"
                );
            }

            return existing;
        }

        switch (value)
        {
            case PythonListValue list:
            {
                var copy = new PythonListValue([]);
                memo[value] = copy;
                foreach (var element in list.Elements)
                {
                    copy.Elements.Add(DeepCopy(element, memo, span, mode));
                }

                return copy;
            }
            case PythonDictionaryValue dictionary:
            {
                var copy = new PythonDictionaryValue([]);
                memo[value] = copy;
                foreach (var item in dictionary.Items)
                {
                    copy.Items.Add(
                        new PythonDictionaryItemValue(
                            DeepCopy(item.Key, memo, span, mode),
                            DeepCopy(item.Value, memo, span, mode)
                        )
                    );
                }

                return copy;
            }
            case PythonSetValue set:
            {
                var copy = new PythonSetValue([]) { IsFrozen = set.IsFrozen };
                memo[value] = copy;
                foreach (var element in set.Elements)
                {
                    copy.Elements.Add(DeepCopy(element, memo, span, mode));
                }

                return copy;
            }
            case PythonTupleValue tuple:
            {
                var elements = new PythonValue[tuple.Elements.Length];
                var unchanged = true;
                for (var index = 0; index < tuple.Elements.Length; index++)
                {
                    elements[index] = DeepCopy(tuple.Elements[index], memo, span, mode);
                    unchanged &= ReferenceEquals(elements[index], tuple.Elements[index]);
                }

                // A mutable descendant may have recursively copied this tuple
                // already. Reuse that copy so tuple/list cycles retain identity.
                if (memo.TryGetValue(value, out var recursiveCopy))
                {
                    return recursiveCopy;
                }

                if (unchanged)
                {
                    return tuple;
                }

                var copy = new PythonTupleValue(elements);
                memo[value] = copy;
                return copy;
            }
            case PythonManagedObjectValue instance:
                return CopyInstance(instance, deep: true, memo, span, mode);
            case PythonExceptionValue exception:
                return CopyException(exception, deep: true, memo, span, mode);
            case PythonInterpolationValue interpolation:
            {
                var copiedValue = DeepCopy(interpolation.Value, memo, span, mode);
                if (memo.TryGetValue(value, out var recursiveCopy))
                {
                    return recursiveCopy;
                }

                var copy = interpolation with { Value = copiedValue };
                memo[value] = copy;
                return copy;
            }
            case PythonTemplateValue template:
            {
                var interpolations = template
                    .Interpolations.Select(item =>
                        (PythonInterpolationValue)DeepCopy(item, memo, span, mode)
                    )
                    .ToArray();
                if (memo.TryGetValue(value, out var recursiveCopy))
                {
                    return recursiveCopy;
                }

                var copy = new PythonTemplateValue(template.Strings, interpolations);
                memo[value] = copy;
                return copy;
            }
            case PythonExternalObjectValue external when mode == DeepCopyMode.PickleSnapshot:
            {
                var (factory, arguments) = GetExternalReduction(external, span);
                var snapshot = new PythonPickleReductionValue(factory);
                memo[value] = snapshot;
                snapshot.Arguments = arguments
                    .Elements.Select(argument =>
                        DeepCopy(argument, memo, span, mode: DeepCopyMode.PickleSnapshot)
                    )
                    .ToArray();
                return snapshot;
            }
            case PythonPickleReductionValue reduction:
            {
                // Mark an in-progress constructor so cycles through its arguments
                // fail explicitly: there is no instance to memoize before the call.
                memo[value] = reduction;
                var arguments = reduction
                    .Arguments.Select(argument => DeepCopy(argument, memo, span, mode))
                    .ToArray();
                var restored = ManagedObjectProtocols.Call(reduction.Factory, arguments, span);
                memo[value] = restored;
                return restored;
            }
            case PythonExternalObjectValue external:
            {
                var copy = ReconstructExternal(external, memo, span);
                memo[value] = copy;
                return copy;
            }
            default:
                return value;
        }
    }

    private static PythonManagedObjectValue CopyInstance(
        PythonManagedObjectValue instance,
        bool deep,
        Dictionary<PythonValue, PythonValue>? memo,
        TextSpan span,
        DeepCopyMode mode = DeepCopyMode.Copy
    )
    {
        var copy = new PythonManagedObjectValue(instance.Type);
        if (deep)
        {
            memo![instance] = copy;
        }

        CopyAttributes(
            instance.Attributes.Dictionary,
            copy.Attributes.Dictionary,
            deep,
            memo,
            span,
            mode
        );

        return copy;
    }

    private static PythonExceptionValue CopyException(
        PythonExceptionValue exception,
        bool deep,
        Dictionary<PythonValue, PythonValue>? memo,
        TextSpan span,
        DeepCopyMode mode = DeepCopyMode.Copy
    )
    {
        if (deep)
        {
            ValidateExceptionConstructorGraph(exception, span);
        }
        PythonExceptionValue copy;
        if (mode == DeepCopyMode.PickleSnapshot)
        {
            // Capturing the graph must not execute the exception constructor.
            // Register the shell before args/state to freeze recursive graphs.
            var nested = exception.GroupExceptions is null
                ? null
                : new List<PythonExceptionValue>();
            copy = new PythonExceptionValue(exception.TypeName, exception.Message)
            {
                ManagedType = exception.ManagedType,
                GroupExceptions = nested,
            };
            memo![exception] = copy;
            copy.Arguments = GetExceptionArguments(exception)
                .Select(argument => DeepCopy(argument, memo, span, mode))
                .ToArray();
            if (nested is not null)
            {
                foreach (var child in exception.GroupExceptions!)
                {
                    nested.Add((PythonExceptionValue)DeepCopy(child, memo, span, mode));
                }
            }
        }
        else
        {
            var arguments = GetExceptionArguments(exception)
                .Select(argument => deep ? DeepCopy(argument, memo!, span, mode) : argument)
                .ToArray();
            // A mutable argument can recursively reconstruct this exception.
            // Pickle reuses that instance once its constructor graph is complete.
            if (
                mode == DeepCopyMode.PickleRestore
                && memo!.TryGetValue(exception, out var recursiveCopy)
            )
            {
                return (PythonExceptionValue)recursiveCopy;
            }

            copy = ReconstructException(exception, arguments, span);
            if (deep)
            {
                memo![exception] = copy;
            }
        }

        // BaseException's default reduction contains args and custom state, not
        // the active traceback, cause/context, or suppression flag.
        CopyAttributes(
            exception.Attributes.Dictionary,
            copy.Attributes.Dictionary,
            deep,
            memo,
            span,
            mode,
            exceptionState: true
        );
        return copy;
    }

    private static IReadOnlyList<PythonValue> GetExceptionArguments(
        PythonExceptionValue exception
    ) =>
        exception.GroupExceptions is { } nested && exception.EffectiveArguments.Count != 2
            ?
            [
                new PythonTextValue(exception.Message),
                new PythonListValue([.. nested.Cast<PythonValue>()]),
            ]
            : exception.EffectiveArguments;

    private static void ValidateExceptionConstructorGraph(
        PythonExceptionValue exception,
        TextSpan span
    )
    {
        var active = new HashSet<PythonValue>(ReferenceEqualityComparer.Instance);
        var complete = new HashSet<PythonValue>(ReferenceEqualityComparer.Instance);
        if (HasCycle(exception))
        {
            throw new PythonRuntimeException(
                "DPY4028",
                "maximum recursion depth exceeded while reconstructing an exception",
                span,
                "RecursionError"
            );
        }

        bool HasCycle(PythonValue value)
        {
            if (complete.Contains(value))
            {
                return false;
            }

            IReadOnlyList<PythonValue> arguments;
            switch (value)
            {
                case PythonExceptionValue nestedException:
                    arguments = GetExceptionArguments(nestedException);
                    break;
                case PythonTupleValue tuple:
                    arguments = tuple.Elements;
                    break;
                default:
                    // Mutable containers are memoized before their children and
                    // allow a recursive constructor graph to make progress.
                    return false;
            }

            if (!active.Add(value))
            {
                return true;
            }

            foreach (var argument in arguments)
            {
                if (HasCycle(argument))
                {
                    return true;
                }
            }

            active.Remove(value);
            complete.Add(value);
            return false;
        }
    }

    private static PythonExceptionValue ReconstructException(
        PythonExceptionValue exception,
        PythonValue[] arguments,
        TextSpan span
    )
    {
        if (UserObjectProtocols.Dispatcher is { } dispatcher)
        {
            var type =
                (PythonValue?)exception.ManagedType
                ?? new PythonExceptionTypeValue(exception.TypeName);
            return dispatcher.Invoke(type, arguments, span) is PythonExceptionValue restored
                ? restored
                : throw new PythonRuntimeException(
                    "DPY4028",
                    "Exception reconstruction did not return an exception.",
                    span,
                    "TypeError"
                );
        }

        // Static protocol callers have no interpreter frames. Builtin exception
        // data can still be reconstructed without executing Python callbacks.
        var nested = exception.GroupExceptions is null
            ? null
            : ManagedObjectProtocols
                .MaterializeValues(arguments[1], span)
                .Cast<PythonExceptionValue>()
                .ToArray();
        return new PythonExceptionValue(exception.TypeName, exception.Message)
        {
            ManagedType = exception.ManagedType,
            Arguments = arguments,
            GroupExceptions = nested,
        };
    }

    private static void CopyAttributes(
        PythonDictionaryValue source,
        PythonDictionaryValue target,
        bool deep,
        Dictionary<PythonValue, PythonValue>? memo,
        TextSpan span,
        DeepCopyMode mode,
        bool exceptionState = false
    )
    {
        // Reconstruct the reduction state and then update the fresh namespace,
        // matching Python's handling of __dict__ (including non-string keys).
        var state = deep ? (PythonDictionaryValue)DeepCopy(source, memo!, span, mode) : source;
        foreach (var item in state.Items)
        {
            if (
                exceptionState
                && mode != DeepCopyMode.PickleSnapshot
                && item.Key is not PythonTextValue
            )
            {
                throw new PythonRuntimeException(
                    "DPY4028",
                    $"attribute name must be string, not '{ManagedObjectProtocols.GetTypeName(item.Key)}'",
                    span,
                    "TypeError"
                );
            }

            ManagedObjectProtocols.SetDictionaryItem(target, item.Key, item.Value, span);
        }
    }

    private static PythonValue ReconstructExternal(
        PythonExternalObjectValue external,
        Dictionary<PythonValue, PythonValue>? memo,
        TextSpan span
    )
    {
        var (factory, factoryArguments) = GetExternalReduction(external, span);
        var arguments = new PythonValue[factoryArguments.Elements.Length];
        for (var index = 0; index < arguments.Length; index++)
        {
            arguments[index] = memo is null
                ? factoryArguments.Elements[index]
                : DeepCopy(factoryArguments.Elements[index], memo, span);
        }

        return ManagedObjectProtocols.Call(factory, arguments, span);
    }

    private static (PythonValue Factory, PythonTupleValue Arguments) GetExternalReduction(
        PythonExternalObjectValue external,
        TextSpan span
    )
    {
        // Native Stable-ABI objects copy through the reduce protocol:
        // `__reduce__()` returns `(callable, args)`, both crossing the boundary as
        // managed-visible values, and calling the callable rebuilds the object.
        var reduce = external.Protocol.GetAttribute("__reduce__", span);
        var reduced = ManagedObjectProtocols.Call(reduce, [], span);
        if (
            reduced
            is not PythonTupleValue { Elements: [var factory, PythonTupleValue factoryArguments] }
        )
        {
            throw new PythonRuntimeException(
                "DPY4028",
                $"cannot copy '{ManagedObjectProtocols.GetTypeName(external)}' object: __reduce__ did not return (callable, args).",
                span,
                "TypeError"
            );
        }

        return (factory, factoryArguments);
    }
}
