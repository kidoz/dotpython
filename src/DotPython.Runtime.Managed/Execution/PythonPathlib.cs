using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// External values that report the Python-visible name of their class so that type
/// errors, <c>type(x).__name__</c> and messages can name them like CPython does.
/// </summary>
internal interface IPythonNamedExternalValue
{
    string TypeName { get; }
}

/// <summary>External objects that expose iteration items (pathlib's <c>parents</c>).</summary>
internal interface IPythonExternalIterable
{
    IReadOnlyList<PythonValue> IterationItems { get; }
}

/// <summary>
/// The posix <c>pathlib</c> slice: <c>PurePath</c>, <c>PurePosixPath</c>, <c>Path</c>
/// and <c>PosixPath</c>. Pure path objects are complete; filesystem access is limited
/// to reads beneath the registered module search roots.
/// </summary>
internal static class PythonPathlib
{
    internal static void Initialize(
        PythonGlobalNamespace globals,
        IReadOnlyList<string> searchRoots
    )
    {
        var context = new PathContext(searchRoots);
        var purePath = new PathClass(context, PathKind.PurePath, "PurePath");
        var purePosixPath = new PathClass(context, PathKind.PurePosixPath, "PurePosixPath");
        var pathClass = new PathClass(context, PathKind.Path, "Path");
        var posixPath = new PathClass(context, PathKind.PosixPath, "PosixPath");
        context.Link(purePath, purePosixPath, pathClass, posixPath);
        var objectType = PythonBuiltinFunctions.Object;
        purePath.SetHierarchy(
            [purePath.Value, objectType],
            [objectType],
            [purePosixPath, posixPath]
        );
        purePosixPath.SetHierarchy(
            [purePosixPath.Value, purePath.Value, objectType],
            [purePath.Value],
            [purePosixPath, posixPath]
        );
        pathClass.SetHierarchy(
            [pathClass.Value, purePath.Value, objectType],
            [purePath.Value],
            [posixPath]
        );
        posixPath.SetHierarchy(
            [posixPath.Value, pathClass.Value, purePosixPath.Value, purePath.Value, objectType],
            [pathClass.Value, purePosixPath.Value],
            [posixPath]
        );
        globals.SetValue("PurePath", purePath.Value);
        globals.SetValue("PurePosixPath", purePosixPath.Value);
        globals.SetValue("Path", pathClass.Value);
        globals.SetValue("PosixPath", posixPath.Value);
    }

    /// <summary>`os.fspath(obj)`: `str` and `bytes` pass through, paths convert.</summary>
    internal static PythonValue Fspath(IReadOnlyList<PythonValue> arguments, TextSpan span)
    {
        if (arguments.Count != 1)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                arguments.Count == 0
                    ? "fspath() missing required argument 'path' (pos 1)"
                    : $"fspath() takes at most 1 argument ({arguments.Count} given)",
                span,
                "TypeError"
            );
        }

        var value = arguments[0];
        if (value is PythonTextValue or PythonByteSequenceValue)
        {
            return value;
        }

        if (value is PythonExternalObjectValue { Protocol: PathValue path })
        {
            return new PythonTextValue(path.Str);
        }

        if (
            value is PythonManagedObjectValue instance
            && UserObjectProtocols.Dispatcher is not null
            && UserObjectProtocols.TryGetSpecialMethod(value, "__fspath__", out var method, out _)
            && method is not PythonNoneValue
        )
        {
            // os.fspath() returns whatever `__fspath__` produces when it is str or bytes.
            var result = UserObjectProtocols.Dispatcher.Invoke(method, [], span);
            if (result is PythonTextValue or PythonByteSequenceValue)
            {
                return result;
            }

            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"expected {instance.Type.Name}.__fspath__() to return str or bytes, "
                    + $"not {ManagedObjectProtocols.GetTypeName(result)}",
                span,
                "TypeError"
            );
        }

        throw ManagedObjectProtocols.Fault(
            "DPY4003",
            "expected str, bytes or os.PathLike object, not "
                + $"{ManagedObjectProtocols.GetTypeName(value)}",
            span,
            "TypeError"
        );
    }

    /// <summary>
    /// `path / segment` and `segment / path`: <c>with_segments</c> semantics with the
    /// NotImplemented-to-operand-error fallback of the reference implementation.
    /// </summary>
    internal static bool TryApplyTrueDivide(
        PythonValue left,
        PythonValue right,
        TextSpan span,
        out PythonValue result
    )
    {
        result = null!;
        var leftPath = AsPath(left);
        var rightPath = AsPath(right);
        if (leftPath is null && rightPath is null)
        {
            return false;
        }

        if (leftPath is not null && TryBuildDivision(leftPath, right, span, out result))
        {
            return true;
        }

        if (rightPath is not null && TryBuildDivision(rightPath, left, span, out result))
        {
            return true;
        }

        throw ManagedObjectProtocols.Fault(
            "DPY4005",
            $"unsupported operand type(s) for /: "
                + $"'{ManagedObjectProtocols.GetTypeName(left)}' and "
                + $"'{ManagedObjectProtocols.GetTypeName(right)}'",
            span,
            "TypeError"
        );
    }

    private static PathValue? AsPath(PythonValue value) =>
        value is PythonExternalObjectValue { Protocol: PathValue path } ? path : null;

    private static bool TryBuildDivision(
        PathValue basis,
        PythonValue segment,
        TextSpan span,
        out PythonValue result
    )
    {
        result = null!;
        string text;
        try
        {
            text = ConvertSegment(segment, span);
        }
        catch (PythonRuntimeException error) when (error.PythonExceptionTypeName is "TypeError")
        {
            return false;
        }

        result = PathValue.Create(basis.Class, basis.Kind, [basis.Str, text]).Wrapped;
        return true;
    }

    /// <summary>
    /// `os.fspath`-style conversion used by `open()` and by path construction:
    /// accepts `str`, pathlib paths and objects defining `__fspath__`.
    /// </summary>
    internal static bool TryConvertPathArgument(PythonValue value, TextSpan span, out string text)
    {
        switch (value)
        {
            case PythonTextValue pathText:
                text = pathText.Value;
                return true;
            case PythonExternalObjectValue { Protocol: PathValue path }:
                text = path.Str;
                return true;
            case PythonManagedObjectValue instance:
            {
                if (
                    UserObjectProtocols.Dispatcher is not null
                    && UserObjectProtocols.TryGetSpecialMethod(
                        value,
                        "__fspath__",
                        out var method,
                        out _
                    )
                    && method is not PythonNoneValue
                )
                {
                    var result = UserObjectProtocols.Dispatcher.Invoke(method, [], span);
                    if (result is PythonTextValue converted)
                    {
                        text = converted.Value;
                        return true;
                    }

                    // The reference reports the same message as for a missing `__fspath__`.
                    // It names the converted value when `__fspath__` returned bytes and the
                    // original argument otherwise (it goes through os.fspath() first).
                    var named = result is PythonByteSequenceValue ? "bytes" : instance.Type.Name;
                    throw ManagedObjectProtocols.Fault(
                        "DPY4003",
                        "argument should be a str or an os.PathLike object where __fspath__ "
                            + $"returns a str, not '{named}'",
                        span,
                        "TypeError"
                    );
                }

                text = string.Empty;
                return false;
            }
            default:
                text = string.Empty;
                return false;
        }
    }

    /// <summary>Converts one constructor/join argument or reports CPython's message.</summary>
    internal static string ConvertSegment(PythonValue value, TextSpan span)
    {
        if (TryConvertPathArgument(value, span, out var text))
        {
            return text;
        }

        throw ManagedObjectProtocols.Fault(
            "DPY4003",
            "argument should be a str or an os.PathLike object where __fspath__ returns "
                + $"a str, not '{ManagedObjectProtocols.GetTypeName(value)}'",
            span,
            "TypeError"
        );
    }

    /// <summary>`posixpath.join`: an absolute component resets the accumulated path.</summary>
    internal static string Join(string first, IEnumerable<string> rest)
    {
        var path = first;
        foreach (var component in rest)
        {
            if (component.StartsWith('/'))
            {
                path = component;
            }
            else if (path.Length == 0 || path.EndsWith('/'))
            {
                path += component;
            }
            else
            {
                path += "/" + component;
            }
        }

        return path;
    }

    /// <summary>`posixpath.splitroot`: (drive, root, rest) with no drive on posix.</summary>
    internal static (string Root, string Remainder) SplitRoot(string path)
    {
        if (path.Length == 0 || path[0] != '/')
        {
            return (string.Empty, path);
        }

        if (path.Length < 2 || path[1] != '/' || (path.Length > 2 && path[2] == '/'))
        {
            return ("/", path[1..]);
        }

        return ("//", path[2..]);
    }
}

internal enum PathKind
{
    PurePath,
    PurePosixPath,
    Path,
    PosixPath,
}

/// <summary>Shared state for one `pathlib` module instance.</summary>
internal sealed class PathContext
{
    internal PathContext(IReadOnlyList<string> searchRoots)
    {
        SearchRoots = searchRoots;
        OpenBuiltin = PythonStandardModules.CreateOpenBuiltin(searchRoots);
    }

    internal IReadOnlyList<string> SearchRoots { get; }

    internal PythonBuiltinFunctionValue OpenBuiltin { get; }

    internal PathClass PurePathClass { get; private set; } = null!;

    internal PathClass PurePosixPathClass { get; private set; } = null!;

    internal PathClass PathClassObject { get; private set; } = null!;

    internal PathClass PosixPathClass { get; private set; } = null!;

    internal void Link(
        PathClass purePath,
        PathClass purePosixPath,
        PathClass pathClass,
        PathClass posixPath
    )
    {
        PurePathClass = purePath;
        PurePosixPathClass = purePosixPath;
        PathClassObject = pathClass;
        PosixPathClass = posixPath;
    }

    internal PathClass ClassFor(PathKind kind) =>
        kind switch
        {
            PathKind.PurePath or PathKind.PurePosixPath => PurePosixPathClass,
            _ => PosixPathClass,
        };

    internal bool IsWithinSearchRoots(string fullPath) =>
        PythonStandardModules.IsWithinSearchRoots(fullPath, SearchRoots);
}

/// <summary>One of the four `pathlib` class objects.</summary>
internal sealed class PathClass : PythonExternalObjectProtocol, IPythonNamedExternalValue
{
    private readonly PathContext _context;
    private PythonExternalObjectValue? _value;

    internal PathClass(PathContext context, PathKind kind, string name)
    {
        _context = context;
        Kind = kind;
        Name = name;
    }

    internal PathKind Kind { get; }

    internal string Name { get; }

    internal bool IsConcrete => Kind is PathKind.Path or PathKind.PosixPath;

    internal PythonExternalObjectValue Value => _value ??= new PythonExternalObjectValue(this);

    public string TypeName => "type";

    internal PathContext Context => _context;

    /// <summary>The transition's method resolution order, ending in `object`.</summary>
    internal PythonValue[] Mro { get; private set; } = [];

    internal PythonValue[] Bases { get; private set; } = [];

    /// <summary>The runtime classes whose instances satisfy <c>isinstance(x, this)</c>.</summary>
    internal PathClass[] InstanceClasses { get; private set; } = [];

    internal void SetHierarchy(PythonValue[] mro, PythonValue[] bases, PathClass[] instanceClasses)
    {
        Mro = mro;
        Bases = bases;
        InstanceClasses = instanceClasses;
    }

    internal PythonValue Call(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (keywordNames.Count > 0)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"PurePath.__init__() got an unexpected keyword argument '{keywordNames[0]}'",
                span,
                "TypeError"
            );
        }

        var segments = new string[arguments.Count];
        for (var index = 0; index < arguments.Count; index++)
        {
            segments[index] = PythonPathlib.ConvertSegment(arguments[index], span);
        }

        // PurePath and PurePosixPath both construct PurePosixPath on posix;
        // Path and PosixPath both construct PosixPath.
        var kind = Kind is PathKind.PurePath or PathKind.PurePosixPath
            ? PathKind.PurePosixPath
            : PathKind.PosixPath;
        return PathValue.Create(_context.ClassFor(kind), kind, segments).Wrapped;
    }

    PythonValue PythonExternalObjectProtocol.Call(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    ) => Call(arguments, [], [], span);

    PythonValue PythonExternalObjectProtocol.CallWithKeywords(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    ) => Call(arguments, keywordNames, keywordValues, span);

    public PythonValue GetAttribute(string name, TextSpan span)
    {
        switch (name)
        {
            case "__name__" or "__qualname__":
                return new PythonTextValue(Name);
            case "__module__":
                return new PythonTextValue("pathlib");
            case "__bases__":
                return new PythonTupleValue([.. Bases]);
            case "__mro__":
                return new PythonTupleValue([.. Mro]);
            case "__doc__":
                return PythonNoneValue.Instance;
            case "__new__" or "__init__":
                return Bound(name, (_, arguments) => Call(arguments, [], [], span));
            case "cwd" or "home" when IsConcrete:
                return Bound(name, (_, arguments) => ClassMethod(name, arguments, span));
            case "from_uri" when IsConcrete:
                return new PythonBoundMethodValue(
                    name,
                    Value,
                    new PythonProtocolFunctionValue(
                        name,
                        (_, arguments) => ClassMethod(name, arguments, span),
                        (_, positional, keywordNames, keywordValues) =>
                            ClassMethod(
                                name,
                                MergeKeywordArguments(
                                    Name,
                                    name,
                                    "uri",
                                    positional,
                                    keywordNames,
                                    keywordValues
                                ),
                                default
                            )
                    )
                );
        }

        if (PathValue.DefinedOn(name, pureOnly: !IsConcrete, out var definingClass))
        {
            return Unbound(name, definingClass);
        }

        throw ManagedObjectProtocols.Fault(
            "DPY4022",
            $"type object '{Name}' has no attribute '{name}'",
            span,
            "AttributeError"
        );
    }

    /// <summary>A classmethod-style value bound to this class object.</summary>
    private PythonBoundMethodValue Bound(
        string name,
        Func<PathClass, IReadOnlyList<PythonValue>, PythonValue> body
    ) =>
        new(
            name,
            Value,
            new PythonProtocolFunctionValue(name, (_, arguments) => body(this, arguments))
        );

    /// <summary>
    /// An unbound method value, as seen through the class; the first argument is the
    /// receiver, mirroring CPython's plain functions.
    /// </summary>
    private static PythonProtocolFunctionValue Unbound(string name, string definingClass) =>
        new PythonProtocolFunctionValue(
            name,
            (_, arguments) =>
            {
                if (arguments.Count == 0)
                {
                    throw ManagedObjectProtocols.Fault(
                        "DPY4003",
                        $"{name}() missing 1 required positional argument: 'self'",
                        default,
                        "TypeError"
                    );
                }

                if (
                    arguments[0]
                    is not PythonExternalObjectValue { Protocol: PathValue receiverPath }
                )
                {
                    throw ManagedObjectProtocols.MissingAttribute(
                        ManagedObjectProtocols.GetTypeName(arguments[0]),
                        name,
                        default
                    );
                }

                return receiverPath.InvokeMethod(
                    name,
                    definingClass,
                    [.. arguments.Skip(1)],
                    [],
                    [],
                    span: default
                );
            }
        );

    internal PythonValue ClassMethod(
        string name,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        switch (name)
        {
            case "cwd":
                RequireNoArguments("Path", name, arguments, span);
                return PathValue
                    .Create(
                        _context.PosixPathClass,
                        PathKind.PosixPath,
                        [Directory.GetCurrentDirectory()]
                    )
                    .Wrapped;
            case "home":
                RequireNoArguments("Path", name, arguments, span);
                return PathValue
                    .Create(
                        _context.PosixPathClass,
                        PathKind.PosixPath,
                        [PathValue.RequireHomeDirectory(span)]
                    )
                    .Wrapped;
            default:
                return PathValue.FromUri(
                    _context.PosixPathClass,
                    PathKind.PosixPath,
                    arguments,
                    span
                );
        }
    }

    private static void RequireNoArguments(
        string className,
        string name,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count == 0)
        {
            return;
        }

        throw ManagedObjectProtocols.Fault(
            "DPY4003",
            $"{className}.{name}() takes 1 positional argument but {arguments.Count + 1} were given",
            span,
            "TypeError"
        );
    }

    /// <summary>
    /// Binds one positional-or-keyword classmethod parameter, keeping the positional form's
    /// arity messages for the callee.
    /// </summary>
    private static List<PythonValue> MergeKeywordArguments(
        string className,
        string name,
        string parameter,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues
    )
    {
        var merged = positional.ToList();
        for (var index = 0; index < keywordNames.Count; index++)
        {
            if (keywordNames[index] != parameter)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{className}.{name}() got an unexpected keyword argument "
                        + $"'{keywordNames[index]}'",
                    default,
                    "TypeError"
                );
            }

            if (merged.Count > 0)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{className}.{name}() got multiple values for argument '{parameter}'",
                    default,
                    "TypeError"
                );
            }

            merged.Add(keywordValues[index]);
        }

        return merged;
    }

    public PythonValue GetItem(PythonValue index, TextSpan span) =>
        throw ManagedObjectProtocols.Fault(
            "DPY4011",
            $"type '{Name}' is not subscriptable",
            span,
            "TypeError"
        );

    public long GetHash(TextSpan span) => RuntimeHelpers.GetHashCode(Value);

    public int GetLength(TextSpan span) =>
        throw ManagedObjectProtocols.Fault(
            "DPY4015",
            "object of type 'type' has no len()",
            span,
            "TypeError"
        );

    public PythonTruthValue RichCompare(
        PythonValue other,
        PythonRichComparison comparison,
        TextSpan span
    )
    {
        var equal =
            other is PythonExternalObjectValue { Protocol: PathClass otherClass }
            && ReferenceEquals(this, otherClass);
        return comparison switch
        {
            PythonRichComparison.Equal => PythonTruthValue.FromBoolean(equal),
            PythonRichComparison.NotEqual => PythonTruthValue.FromBoolean(!equal),
            _ => throw ManagedObjectProtocols.Fault(
                "DPY4005",
                $"'{PathValue.ComparisonSymbol(comparison)}' not supported between instances "
                    + $"of 'type' and '{ManagedObjectProtocols.GetTypeName(other)}'",
                span,
                "TypeError"
            ),
        };
    }

    public bool IsInstanceOf(PythonValue value, TextSpan span) =>
        value is PythonExternalObjectValue { Protocol: PathValue instance }
        && Array.IndexOf(InstanceClasses, instance.Class) >= 0;

    public string ToDisplayString() => $"<class 'pathlib.{Name}'>";

    public string ToRepresentationString() => ToDisplayString();
}

/// <summary>
/// The glob pattern language shared by `match`, `full_match`, `glob` and `rglob`,
/// ported from the reference implementation's `glob.translate` and `glob._GlobberBase`
/// with `/` as the only separator and hidden files always matched.
/// </summary>
internal static class PathGlob
{
    private static readonly Regex MagicCheck = new("([*?[])", RegexOptions.CultureInvariant);

    internal static bool HasMagic(string part) => MagicCheck.IsMatch(part);

    /// <summary>Compiles one pattern part or a full pattern into an anchored matcher.</summary>
    internal static Regex Compile(string pattern, bool recursive, bool caseSensitive)
    {
        var options = RegexOptions.CultureInvariant | RegexOptions.Singleline;
        if (!caseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(@"\A" + Translate(pattern, recursive) + @"\z", options);
    }

    /// <summary>
    /// `glob.translate` with `/` separators and `include_hidden=True`:
    /// `*` matches a non-empty run without separators, `?` one non-separator
    /// character, and a whole `**` part matches any number of path segments.
    /// </summary>
    internal static string Translate(string pattern, bool recursive)
    {
        var parts = pattern.Split('/');
        var results = new StringBuilder();
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            var last = index == parts.Length - 1;
            if (part == "*")
            {
                results.Append(last ? "[^/]+" : "[^/]+/");
            }
            else if (recursive && part == "**")
            {
                if (!last)
                {
                    if (parts[index + 1] != "**")
                    {
                        results.Append("(?:.+/)?");
                    }
                }
                else
                {
                    results.Append(".*");
                }
            }
            else
            {
                if (part.Length > 0)
                {
                    results.Append(TranslatePart(part, "[^/]*", "[^/]"));
                }

                if (!last)
                {
                    results.Append('/');
                }
            }
        }

        return results.ToString();
    }

    /// <summary>`fnmatch._translate` for one pattern part.</summary>
    private static string TranslatePart(string pattern, string star, string questionMark)
    {
        var results = new StringBuilder();
        var index = 0;
        while (index < pattern.Length)
        {
            var character = pattern[index++];
            switch (character)
            {
                case '*':
                    results.Append(star);
                    while (index < pattern.Length && pattern[index] == '*')
                    {
                        index++;
                    }

                    break;
                case '?':
                    results.Append(questionMark);
                    break;
                case '[':
                    TranslateBracket(pattern, ref index, results);
                    break;
                default:
                    results.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }

        return results.ToString();
    }

    private static void TranslateBracket(string pattern, ref int index, StringBuilder results)
    {
        var end = index;
        if (end < pattern.Length && pattern[end] == '!')
        {
            end++;
        }

        if (end < pattern.Length && pattern[end] == ']')
        {
            end++;
        }

        while (end < pattern.Length && pattern[end] != ']')
        {
            end++;
        }

        if (end >= pattern.Length)
        {
            results.Append("\\[");
            return;
        }

        var stuff = pattern[index..end];
        index = end + 1;
        if (stuff.Length == 0)
        {
            // Empty range: never match.
            results.Append("(?!)");
            return;
        }

        if (stuff == "!")
        {
            // Negated empty range: match any character.
            results.Append('.');
            return;
        }

        stuff = stuff.Replace("\\", "\\\\", StringComparison.Ordinal);
        stuff = Regex.Replace(stuff, "[&~|]", "\\$0", RegexOptions.CultureInvariant);
        if (stuff[0] == '!')
        {
            stuff = "^" + stuff[1..];
        }
        else if (stuff[0] is '^' or '[')
        {
            stuff = "\\" + stuff;
        }

        results.Append('[').Append(stuff).Append(']');
    }

    /// <summary>`_StringGlobber.selector` over a filesystem directory tree.</summary>
    internal sealed class Selector
    {
        private readonly bool _caseSensitive;
        private readonly bool _casePedantic;
        private readonly string[] _parts;
        private readonly int _index;

        internal Selector(string[] parts, int index, bool caseSensitive, bool casePedantic)
        {
            _parts = parts;
            _index = index;
            _caseSensitive = caseSensitive;
            _casePedantic = casePedantic;
        }

        /// <summary>Yields the display paths selected from <paramref name="path"/>.</summary>
        internal IEnumerable<string> Select(string path, bool exists)
        {
            if (_index >= _parts.Length)
            {
                if (exists || Exists(path))
                {
                    yield return path;
                }

                yield break;
            }

            var part = _parts[_index];
            if (part == "**")
            {
                foreach (var selected in SelectRecursive(path, exists))
                {
                    yield return selected;
                }

                yield break;
            }

            if (part is "" or "." or "..")
            {
                var special = _index + 1 < _parts.Length ? part + "/" : part;
                foreach (var selected in Next().Select(path + special, exists))
                {
                    yield return selected;
                }

                yield break;
            }

            if (!_casePedantic && !HasMagic(part))
            {
                // Consume and join any following literal parts in one step.
                var literal = part;
                var next = _index + 1;
                while (next < _parts.Length && !HasMagic(_parts[next]))
                {
                    literal += "/" + _parts[next];
                    next++;
                }

                if (next < _parts.Length)
                {
                    literal += "/";
                }

                foreach (
                    var selected in new Selector(
                        _parts,
                        next,
                        _caseSensitive,
                        _casePedantic
                    ).Select(path + literal, exists: false)
                )
                {
                    yield return selected;
                }

                yield break;
            }

            foreach (var selected in SelectWildcard(path, part))
            {
                yield return selected;
            }
        }

        private Selector Next() => new(_parts, _index + 1, _caseSensitive, _casePedantic);

        private IEnumerable<string> SelectWildcard(string path, string part)
        {
            var match = part == "*" ? null : Compile(part, recursive: true, _caseSensitive);
            var dirOnly = _index + 1 < _parts.Length;
            foreach (var (name, child) in Enumerate(path))
            {
                if (match is not null && !match.IsMatch(name))
                {
                    continue;
                }

                if (dirOnly)
                {
                    if (!Directory.Exists(child))
                    {
                        continue;
                    }

                    foreach (var selected in Next().Select(child + "/", exists: true))
                    {
                        yield return selected;
                    }
                }
                else
                {
                    yield return child;
                }
            }
        }

        private IEnumerable<string> SelectRecursive(string path, bool exists)
        {
            // With `recurse_symlinks=False` the reference implementation does not merge
            // following pattern parts into the recursive matcher; every entry passes and
            // the remaining parts are selected from each directory in turn.
            var dirOnly = _index + 1 < _parts.Length;
            foreach (var selected in Next().Select(path, exists))
            {
                yield return selected;
            }

            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                var directory = stack.Pop();
                foreach (var (name, child) in Enumerate(directory))
                {
                    _ = name;
                    var isDirectory = Directory.Exists(child);
                    if (!isDirectory && dirOnly)
                    {
                        continue;
                    }

                    var entry = dirOnly ? child + "/" : child;
                    if (dirOnly)
                    {
                        foreach (var selected in Next().Select(entry, exists: true))
                        {
                            yield return selected;
                        }
                    }
                    else
                    {
                        yield return entry;
                    }

                    if (isDirectory)
                    {
                        stack.Push(entry);
                    }
                }
            }
        }

        /// <summary>Enumerates direct children as (name, display path) pairs.</summary>
        private static IEnumerable<(string Name, string Path)> Enumerate(string directory)
        {
            var full = ScriptPath.Resolve(directory);
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(full);
            }
            catch (IOException)
            {
                yield break;
            }
            catch (UnauthorizedAccessException)
            {
                yield break;
            }

            foreach (var entry in entries)
            {
                var name = System.IO.Path.GetFileName(entry);
                yield return (
                    name,
                    directory.Length != 0 && directory.EndsWith('/')
                        ? directory + name
                        : directory + "/" + name
                );
            }
        }

        private static bool Exists(string path)
        {
            var full = ScriptPath.Resolve(path);
            return File.Exists(full) || Directory.Exists(full);
        }
    }
}

/// <summary>Filesystem helpers shared by the pathlib filesystem operations.</summary>
internal static class ScriptPath
{
    /// <summary>Resolves a display path against the process working directory.</summary>
    internal static string Resolve(string path) => System.IO.Path.GetFullPath(path);
}

/// <summary>
/// A `pathlib` path object. Pure path manipulation is complete; filesystem access is
/// limited to reads beneath the registered module search roots, and every operation
/// that would write or inspect metadata is refused explicitly.
/// </summary>
internal sealed class PathValue : PythonExternalObjectProtocol, IPythonNamedExternalValue
{
    private static readonly HashSet<string> PureMethodNames = new(StringComparer.Ordinal)
    {
        "as_posix",
        "as_uri",
        "is_absolute",
        "is_reserved",
        "joinpath",
        "with_segments",
        "with_name",
        "with_stem",
        "with_suffix",
        "relative_to",
        "is_relative_to",
        "match",
        "full_match",
        "__fspath__",
        "__str__",
        "__repr__",
        "__bytes__",
    };

    private static readonly HashSet<string> FilesystemMethodNames = new(StringComparer.Ordinal)
    {
        "absolute",
        "resolve",
        "expanduser",
        "stat",
        "lstat",
        "exists",
        "is_dir",
        "is_file",
        "is_junction",
        "is_mount",
        "is_symlink",
        "is_block_device",
        "is_char_device",
        "is_fifo",
        "is_socket",
        "iterdir",
        "glob",
        "rglob",
        "walk",
        "open",
        "read_bytes",
        "read_text",
        "write_bytes",
        "write_text",
        "mkdir",
        "touch",
        "unlink",
        "rmdir",
        "rename",
        "replace",
        "symlink_to",
        "hardlink_to",
        "chmod",
        "copy",
        "move",
        "owner",
        "group",
        "readlink",
        "samefile",
        "as_uri",
    };

    private readonly string[] _tail;
    private string? _str;
    private PythonValue? _wrapped;

    private PathValue(
        PathContext context,
        PathClass @class,
        PathKind kind,
        string[] raw,
        string? display
    )
    {
        Context = context;
        Class = @class;
        Kind = kind;
        RawPaths = raw;
        _str = display;
        var joined = raw.Length == 0 ? string.Empty : PythonPathlib.Join(raw[0], raw.Skip(1));
        var (root, rest) = PythonPathlib.SplitRoot(joined);
        Root = root;
        _tail =
            rest.Length == 0
                ? []
                : [.. rest.Split('/').Where(part => part.Length != 0 && part != ".")];
    }

    internal PathContext Context { get; }

    internal PathClass Class { get; }

    internal PathKind Kind { get; }

    internal string[] RawPaths { get; }

    /// <summary>The root ('', '/' or '//'); posix paths never have a drive.</summary>
    internal string Root { get; }

    internal string[] Tail => _tail;

    internal static PathValue Create(
        PathClass @class,
        PathKind kind,
        string[] raw,
        string? display = null
    ) => new(@class.Context, @class, kind, raw, display);

    /// <summary>`_from_parsed_parts`: a path whose display string is canonical.</summary>
    internal PathValue FromParsed(string root, string[] tail)
    {
        var formatted = root.Length > 0 ? root + string.Join('/', tail) : string.Join('/', tail);
        return new PathValue(
            Context,
            Class,
            Kind,
            [formatted],
            formatted.Length > 0 ? formatted : "."
        );
    }

    internal string Str
    {
        get
        {
            if (_str is not null)
            {
                return _str;
            }

            var body = string.Join('/', _tail);
            var formatted = Root.Length > 0 ? Root + body : body;
            return _str = formatted.Length > 0 ? formatted : ".";
        }
    }

    public string TypeName => Class.Name;

    /// <summary>The protocol object as a Python value, with a stable identity.</summary>
    internal PythonValue Wrapped => _wrapped ??= new PythonExternalObjectValue(this);

    internal string[] Parts => Root.Length > 0 ? [Root, .. _tail] : _tail;

    internal string Anchor => Root;

    internal string Name => _tail.Length > 0 ? _tail[^1] : string.Empty;

    internal string Suffix
    {
        get
        {
            var name = Name.TrimStart('.');
            var dot = name.LastIndexOf('.');
            return dot >= 1 ? name[dot..] : string.Empty;
        }
    }

    internal string Stem
    {
        get
        {
            var name = Name;
            var suffix = Suffix;
            return suffix.Length == 0 ? name : name[..^suffix.Length];
        }
    }

    internal List<string> SuffixList
    {
        get
        {
            var trimmed = Name.TrimStart('.').Split('.');
            return [.. trimmed.Skip(1).Select(extension => "." + extension)];
        }
    }

    internal PathValue Parent => _tail.Length == 0 ? this : FromParsed(Root, _tail[..^1]);

    internal PathParentsValue Parents => new(this);

    internal bool IsAbsolute => Root.Length > 0;

    internal static bool DefinedOn(string name, bool pureOnly, out string qualifier)
    {
        if (name == "as_uri")
        {
            qualifier = pureOnly ? "PurePath" : "Path";
            return true;
        }

        if (FilesystemMethodNames.Contains(name))
        {
            qualifier = "Path";
            return !pureOnly;
        }

        if (PureMethodNames.Contains(name))
        {
            qualifier = "PurePath";
            return true;
        }

        qualifier = "PurePath";
        return false;
    }

    public PythonValue GetAttribute(string name, TextSpan span)
    {
        switch (name)
        {
            case "name":
                return Text(Name);
            case "suffix":
                return Text(Suffix);
            case "suffixes":
                return new PythonListValue([.. SuffixList.Select(Text)]);
            case "stem":
                return Text(Stem);
            case "parent":
                return Parent.Wrapped;
            case "parents":
                return Parents.Wrapped;
            case "parts":
                return new PythonTupleValue([.. Parts.Select(Text)]);
            case "anchor":
                return Text(Anchor);
            case "drive":
                return Text(string.Empty);
            case "root":
                return Text(Root);
            case "__class__":
                return Class.Value;
            case "__doc__":
                return PythonNoneValue.Instance;
        }

        var pureOnly = Kind is PathKind.PurePath or PathKind.PurePosixPath;
        if (DefinedOn(name, pureOnly, out var qualifier))
        {
            return Bound(name, qualifier, span);
        }

        throw ManagedObjectProtocols.Fault(
            "DPY4022",
            $"'{Class.Name}' object has no attribute '{name}'",
            span,
            "AttributeError"
        );
    }

    private PythonBoundMethodValue Bound(string name, string qualifier, TextSpan span) =>
        new(
            name,
            Wrapped,
            new PythonProtocolFunctionValue(
                name,
                (_, arguments) => InvokeMethod(name, qualifier, arguments, [], [], span),
                (_, arguments, keywordNames, keywordValues) =>
                    InvokeMethod(name, qualifier, arguments, keywordNames, keywordValues, span)
            )
        );

    public PythonValue Call(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        throw ManagedObjectProtocols.Fault(
            "DPY4003",
            $"'{Class.Name}' object is not callable",
            span,
            "TypeError"
        );

    public PythonValue CallWithKeywords(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    ) => Call(arguments, span);

    public PythonValue GetItem(PythonValue index, TextSpan span) =>
        throw ManagedObjectProtocols.Fault(
            "DPY4011",
            $"'{Class.Name}' object is not subscriptable",
            span,
            "TypeError"
        );

    public long GetHash(TextSpan span) => StringComparer.Ordinal.GetHashCode(Str);

    public int GetLength(TextSpan span) =>
        throw ManagedObjectProtocols.Fault(
            "DPY4015",
            $"object of type '{Class.Name}' has no len()",
            span,
            "TypeError"
        );

    public PythonTruthValue RichCompare(
        PythonValue other,
        PythonRichComparison comparison,
        TextSpan span
    )
    {
        switch (comparison)
        {
            case PythonRichComparison.Equal or PythonRichComparison.NotEqual:
            {
                var equal =
                    other is PythonExternalObjectValue { Protocol: PathValue path }
                    && string.Equals(Str, path.Str, StringComparison.Ordinal);
                return PythonTruthValue.FromBoolean(
                    comparison == PythonRichComparison.Equal ? equal : !equal
                );
            }
        }

        if (other is not PythonExternalObjectValue { Protocol: PathValue otherPath })
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4005",
                $"'{ComparisonSymbol(comparison)}' not supported between instances of "
                    + $"'{Class.Name}' and '{ManagedObjectProtocols.GetTypeName(other)}'",
                span,
                "TypeError"
            );
        }

        var order = ComparePartLists(Str.Split('/'), otherPath.Str.Split('/'));
        var result = comparison switch
        {
            PythonRichComparison.LessThan => order < 0,
            PythonRichComparison.LessThanOrEqual => order <= 0,
            PythonRichComparison.GreaterThan => order > 0,
            PythonRichComparison.GreaterThanOrEqual => order >= 0,
            _ => false,
        };
        return PythonTruthValue.FromBoolean(result);
    }

    internal static string ComparisonSymbol(PythonRichComparison comparison) =>
        comparison switch
        {
            PythonRichComparison.Equal => "==",
            PythonRichComparison.NotEqual => "!=",
            PythonRichComparison.LessThan => "<",
            PythonRichComparison.LessThanOrEqual => "<=",
            PythonRichComparison.GreaterThan => ">",
            PythonRichComparison.GreaterThanOrEqual => ">=",
            _ => "?",
        };

    private static int ComparePartLists(string[] left, string[] right)
    {
        var shared = Math.Min(left.Length, right.Length);
        for (var index = 0; index < shared; index++)
        {
            var order = string.CompareOrdinal(left[index], right[index]);
            if (order != 0)
            {
                return order;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    public bool IsInstanceOf(PythonValue value, TextSpan span) => false;

    public string ToDisplayString() => Str;

    public string ToRepresentationString() =>
        $"{Class.Name}({new PythonTextValue(Str).ToRepresentationString()})";

    internal PythonValue InvokeMethod(
        string name,
        string qualifier,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        switch (name)
        {
            case "as_posix":
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                return Text(Str);
            case "is_absolute":
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                return PythonTruthValue.FromBoolean(IsAbsolute);
            case "is_reserved":
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                // Reserved names are a Windows concept; posix paths are never reserved.
                return PythonTruthValue.FromBoolean(false);
            case "as_uri":
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                return Text(AsUri(qualifier == "Path"));
            case "__fspath__":
            case "__str__":
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                return Text(Str);
            case "__repr__":
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                return Text(ToRepresentationString());
            case "__bytes__":
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                return new PythonByteSequenceValue(Encoding.UTF8.GetBytes(Str));
            case "joinpath":
            {
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    true,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out var segments,
                    span
                );
                return Create(
                    Class,
                    Kind,
                    [
                        .. RawPaths,
                        .. segments.Select(segment => PythonPathlib.ConvertSegment(segment, span)),
                    ]
                ).Wrapped;
            }
            case "with_segments":
            {
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    true,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out var segments,
                    span
                );
                return Create(
                    Class,
                    Kind,
                    [.. segments.Select(segment => PythonPathlib.ConvertSegment(segment, span))]
                ).Wrapped;
            }
            case "with_name":
            {
                Bind(
                    qualifier,
                    name,
                    ["name"],
                    1,
                    1,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                return WithName(slots[0]!, span).Wrapped;
            }
            case "with_stem":
            {
                Bind(
                    qualifier,
                    name,
                    ["stem"],
                    1,
                    1,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                return WithStem(slots[0]!, span).Wrapped;
            }
            case "with_suffix":
            {
                Bind(
                    qualifier,
                    name,
                    ["suffix"],
                    1,
                    1,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                return WithSuffix(slots[0]!, span).Wrapped;
            }
            case "relative_to":
            {
                Bind(
                    qualifier,
                    name,
                    ["other", "walk_up"],
                    1,
                    1,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                var other = ConvertPathArgument(slots[0]!, span);
                var walkUp = slots[1] is not null && ManagedObjectProtocols.IsTrue(slots[1]!);
                return RelativeTo(other, walkUp, span).Wrapped;
            }
            case "is_relative_to":
            {
                Bind(
                    qualifier,
                    name,
                    ["other"],
                    1,
                    1,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                return PythonTruthValue.FromBoolean(
                    IsRelativeTo(ConvertPathArgument(slots[0]!, span))
                );
            }
            case "match":
            {
                Bind(
                    qualifier,
                    name,
                    ["path_pattern", "case_sensitive"],
                    1,
                    1,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                var pattern = ConvertPathArgument(slots[0]!, span);
                return PythonTruthValue.FromBoolean(Match(pattern, CaseSensitive(slots[1]), span));
            }
            case "full_match":
            {
                Bind(
                    qualifier,
                    name,
                    ["pattern", "case_sensitive"],
                    1,
                    1,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                var pattern = ConvertPathArgument(slots[0]!, span);
                return PythonTruthValue.FromBoolean(FullMatch(pattern, CaseSensitive(slots[1])));
            }
            case "absolute":
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                return Absolute().Wrapped;
            case "resolve":
            {
                Bind(
                    qualifier,
                    name,
                    ["strict"],
                    1,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                var strict = slots[0] is not null && ManagedObjectProtocols.IsTrue(slots[0]!);
                return Resolve(strict, span).Wrapped;
            }
            case "expanduser":
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                return ExpandUser(span).Wrapped;
            case "exists":
            {
                Bind(
                    qualifier,
                    name,
                    ["follow_symlinks"],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                RequireFollowSymlinks(name, slots[0], span);
                var full = FullPath(name, span);
                return PythonTruthValue.FromBoolean(File.Exists(full) || Directory.Exists(full));
            }
            case "is_dir":
            case "is_file":
            {
                Bind(
                    qualifier,
                    name,
                    ["follow_symlinks"],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                RequireFollowSymlinks(name, slots[0], span);
                var full = FullPath(name, span);
                var result = name == "is_dir" ? Directory.Exists(full) : File.Exists(full);
                return PythonTruthValue.FromBoolean(result);
            }
            case "is_junction":
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                // Junctions are a Windows concept; posix paths are never junctions.
                return PythonTruthValue.FromBoolean(false);
            case "iterdir":
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                return Iterdir(span);
            case "glob":
            case "rglob":
            {
                Bind(
                    qualifier,
                    name,
                    ["pattern", "case_sensitive", "recurse_symlinks"],
                    1,
                    1,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                var recurseSymlinks =
                    slots[2] is not null && ManagedObjectProtocols.IsTrue(slots[2]!);
                return Glob(
                    name == "rglob",
                    name,
                    slots[0]!,
                    CaseSensitiveOptional(slots[1]),
                    recurseSymlinks,
                    span
                );
            }
            case "walk":
            {
                Bind(
                    qualifier,
                    name,
                    ["top_down", "on_error", "follow_symlinks"],
                    3,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                return Walk(
                    slots[0] is null || ManagedObjectProtocols.IsTrue(slots[0]!),
                    slots[1],
                    slots[2] is not null && ManagedObjectProtocols.IsTrue(slots[2]!),
                    span
                );
            }
            case "open":
            {
                Bind(
                    qualifier,
                    name,
                    ["mode", "buffering", "encoding", "errors", "newline"],
                    5,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                return Open(slots, span);
            }
            case "read_bytes":
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                return ReadBytes(span);
            case "read_text":
            {
                Bind(
                    qualifier,
                    name,
                    ["encoding", "errors", "newline"],
                    3,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                RequireTextOptions("read_text", slots[0], slots[1], slots[2], span);
                return ReadText(span);
            }
            case "write_text":
            {
                Bind(
                    qualifier,
                    name,
                    ["data", "encoding", "errors", "newline"],
                    4,
                    1,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                if (slots[0] is not PythonTextValue)
                {
                    throw ManagedObjectProtocols.Fault(
                        "DPY4003",
                        $"data must be str, not {ManagedObjectProtocols.GetTypeName(slots[0]!)}",
                        span,
                        "TypeError"
                    );
                }

                RefuseWrite(name, span);
                return PythonNoneValue.Instance;
            }
            case "write_bytes":
            {
                Bind(
                    qualifier,
                    name,
                    ["data"],
                    1,
                    1,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                if (slots[0] is not PythonByteSequenceValue)
                {
                    throw ManagedObjectProtocols.Fault(
                        "DPY4003",
                        $"memoryview: a bytes-like object is required, not "
                            + $"'{ManagedObjectProtocols.GetTypeName(slots[0]!)}'",
                        span,
                        "TypeError"
                    );
                }

                RefuseWrite(name, span);
                return PythonNoneValue.Instance;
            }
            case "mkdir":
                Bind(
                    qualifier,
                    name,
                    ["mode", "parents", "exist_ok"],
                    3,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                RefuseWrite(name, span);
                return PythonNoneValue.Instance;
            case "touch":
                Bind(
                    qualifier,
                    name,
                    ["mode", "exist_ok"],
                    2,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                RefuseWrite(name, span);
                return PythonNoneValue.Instance;
            case "unlink":
                Bind(
                    qualifier,
                    name,
                    ["missing_ok"],
                    1,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                RefuseWrite(name, span);
                return PythonNoneValue.Instance;
            case "rmdir":
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                RefuseWrite(name, span);
                return PythonNoneValue.Instance;
            case "rename":
            case "replace":
            case "move":
            {
                Bind(
                    qualifier,
                    name,
                    ["target"],
                    1,
                    1,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                PythonPathlib.ConvertSegment(slots[0]!, span);
                RefuseWrite(name, span);
                return PythonNoneValue.Instance;
            }
            case "hardlink_to":
            {
                Bind(
                    qualifier,
                    name,
                    ["target"],
                    1,
                    1,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                PythonPathlib.ConvertSegment(slots[0]!, span);
                RefuseWrite(name, span);
                return PythonNoneValue.Instance;
            }
            case "symlink_to":
            {
                Bind(
                    qualifier,
                    name,
                    ["target", "target_is_directory"],
                    2,
                    1,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var slots,
                    out _,
                    span
                );
                PythonPathlib.ConvertSegment(slots[0]!, span);
                RefuseWrite(name, span);
                return PythonNoneValue.Instance;
            }
            case "chmod":
                Bind(
                    qualifier,
                    name,
                    ["mode", "follow_symlinks"],
                    1,
                    1,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                RefuseWrite(name, span);
                return PythonNoneValue.Instance;
            case "copy":
                Bind(
                    qualifier,
                    name,
                    ["target", "follow_symlinks"],
                    1,
                    1,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out var copySlots,
                    out _,
                    span
                );
                PythonPathlib.ConvertSegment(copySlots[0]!, span);
                RefuseWrite(name, span);
                return PythonNoneValue.Instance;
            case "stat":
            case "owner":
            case "group":
                Bind(
                    qualifier,
                    name,
                    ["follow_symlinks"],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                throw RefuseMetadata(name, span);
            case "lstat":
            case "is_mount":
            case "is_symlink":
            case "is_block_device":
            case "is_char_device":
            case "is_fifo":
            case "is_socket":
            case "readlink":
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                throw RefuseMetadata(name, span);
            case "samefile":
                Bind(
                    qualifier,
                    name,
                    ["other_path"],
                    1,
                    1,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                throw RefuseMetadata(name, span);
            default:
                Bind(
                    qualifier,
                    name,
                    [],
                    0,
                    0,
                    false,
                    positional,
                    keywordNames,
                    keywordValues,
                    out _,
                    out _,
                    span
                );
                throw RefuseMetadata(name, span);
        }
    }

    /// <summary>CPython's function-style argument binder for the path methods.</summary>
    private static void Bind(
        string qualifier,
        string name,
        string[] parameters,
        int positionsCount,
        int requiredCount,
        bool variadic,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        out PythonValue?[] slots,
        out List<PythonValue> extras,
        TextSpan span
    )
    {
        var owner = $"{qualifier}.{name}";
        slots = new PythonValue?[parameters.Length];
        extras = [];
        if (!variadic && positional.Count > positionsCount)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{owner}() takes {positionsCount + 1} positional "
                    + $"argument{(positionsCount + 1 == 1 ? "" : "s")} but "
                    + $"{positional.Count + 1} were given",
                span,
                "TypeError"
            );
        }

        var filled = Math.Min(positional.Count, positionsCount);
        for (var index = 0; index < filled; index++)
        {
            slots[index] = positional[index];
        }

        for (var index = positionsCount; index < positional.Count; index++)
        {
            extras.Add(positional[index]);
        }

        for (var index = 0; index < keywordNames.Count; index++)
        {
            var keyword = keywordNames[index];
            var slot = Array.IndexOf(parameters, keyword);
            if (slot < 0)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{owner}() got an unexpected keyword argument '{keyword}'",
                    span,
                    "TypeError"
                );
            }

            if (slots[slot] is not null)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{owner}() got multiple values for argument '{keyword}'",
                    span,
                    "TypeError"
                );
            }

            slots[slot] = keywordValues[index];
        }

        var missing = new List<string>();
        for (var index = 0; index < requiredCount; index++)
        {
            if (slots[index] is null)
            {
                missing.Add(parameters[index]);
            }
        }

        if (missing.Count == 1)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{owner}() missing 1 required positional argument: '{missing[0]}'",
                span,
                "TypeError"
            );
        }

        if (missing.Count > 1)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{owner}() missing {missing.Count} required positional arguments: "
                    + $"{string.Join(", ", missing.Select(item => $"'{item}'"))}",
                span,
                "TypeError"
            );
        }
    }

    private static PythonTextValue Text(string value) => new(value);

    private static string Repr(string value) => new PythonTextValue(value).ToRepresentationString();

    private PathValue ConvertPathArgument(PythonValue value, TextSpan span) =>
        value is PythonExternalObjectValue { Protocol: PathValue path }
            ? path
            : Create(Class, Kind, [PythonPathlib.ConvertSegment(value, span)]);

    private static bool? CaseSensitiveOptional(PythonValue? value) =>
        value is null or PythonNoneValue ? null : ManagedObjectProtocols.IsTrue(value);

    private static bool CaseSensitive(PythonValue? value) =>
        value is null or PythonNoneValue || ManagedObjectProtocols.IsTrue(value);

    private static void RequireFollowSymlinks(string name, PythonValue? value, TextSpan span)
    {
        if (value is not null && !ManagedObjectProtocols.IsTrue(value))
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4037",
                $"{name}() does not support follow_symlinks=False in this runtime slice "
                    + "(symlinks are out of scope).",
                span,
                "NotImplementedError"
            );
        }
    }

    private PythonRuntimeException RefuseMetadata(string name, TextSpan span) =>
        ManagedObjectProtocols.Fault(
            "DPY4037",
            $"{Class.Name}.{name}() is not supported in this runtime slice "
                + "(file metadata, ownership and symlinks are out of scope).",
            span,
            "NotImplementedError"
        );

    private static void RefuseWrite(string name, TextSpan span) =>
        throw ManagedObjectProtocols.Fault(
            "DPY4037",
            $"Path.{name}() requires a filesystem write capability that this runtime slice "
                + "does not provide.",
            span,
            "PermissionError"
        );

    private string GetDisplayFullPath() => ScriptPath.Resolve(Str);

    /// <summary>Resolves the path and enforces the read capability boundary.</summary>
    private string FullPath(string name, TextSpan span)
    {
        var full = GetDisplayFullPath();
        if (!Context.IsWithinSearchRoots(full))
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4037",
                $"{Class.Name}.{name}() outside the registered module search roots is not "
                    + "permitted in this runtime slice.",
                span,
                "PermissionError"
            );
        }

        return full;
    }

    private string AsUri(bool concrete)
    {
        if (!IsAbsolute)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                concrete
                    ? "relative paths can't be expressed as file URIs"
                    : "relative path can't be expressed as a file URI",
                default,
                "ValueError"
            );
        }

        return "file://" + PercentEncode(Str);
    }

    private static string PercentEncode(string text)
    {
        var builder = new StringBuilder();
        foreach (var character in Encoding.UTF8.GetBytes(text))
        {
            if (
                (character >= 'A' && character <= 'Z')
                || (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9')
                || character is (byte)'_' or (byte)'.' or (byte)'-' or (byte)'~' or (byte)'/'
            )
            {
                builder.Append((char)character);
            }
            else
            {
                builder.Append('%').Append(character.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>`Path.from_uri`: percent-decoding and the localhost authority check.</summary>
    internal static PythonValue FromUri(
        PathClass @class,
        PathKind kind,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count == 0)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "Path.from_uri() missing 1 required positional argument: 'uri'",
                span,
                "TypeError"
            );
        }

        if (arguments.Count > 1)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"Path.from_uri() takes 2 positional arguments but {arguments.Count + 1} were given",
                span,
                "TypeError"
            );
        }

        if (arguments[0] is not PythonTextValue uriText)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4022",
                $"'{ManagedObjectProtocols.GetTypeName(arguments[0])}' object has no attribute 'decode'",
                span,
                "AttributeError"
            );
        }

        var uri = uriText.Value;
        var colon = uri.IndexOf(':', StringComparison.Ordinal);
        var slash = uri.IndexOf('/', StringComparison.Ordinal);
        var scheme =
            colon >= 0 && (slash < 0 || colon < slash)
                ? uri[..colon].ToUpperInvariant()
                : string.Empty;
        if (scheme != "FILE")
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "URL is missing a 'file:' scheme",
                span,
                "ValueError"
            );
        }

        var rest = uri[(colon + 1)..];
        var authority = string.Empty;
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            var end = rest.IndexOf('/', 2);
            if (end < 0)
            {
                authority = rest[2..];
                rest = string.Empty;
            }
            else
            {
                authority = rest[2..end];
                rest = rest[end..];
            }
        }

        if (authority is not ("" or "localhost"))
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "file:// scheme is supported only on localhost",
                span,
                "ValueError"
            );
        }

        // Discard the query and fragment.
        var cut = rest.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            rest = rest[..cut];
        }

        var decoded = PercentDecode(rest);
        if (!decoded.StartsWith('/'))
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"URI is not absolute: {Repr(uri)}",
                span,
                "ValueError"
            );
        }

        return Create(@class, kind, [decoded]).Wrapped;
    }

    private static string PercentDecode(string text)
    {
        if (!text.Contains('%', StringComparison.Ordinal))
        {
            return text;
        }

        var bytes = new List<byte>();
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (
                character == '%'
                && index + 2 < text.Length
                && IsHexDigit(text[index + 1])
                && IsHexDigit(text[index + 2])
            )
            {
                bytes.Add(
                    byte.Parse(
                        text.AsSpan(index + 1, 2),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture
                    )
                );
                index += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(character.ToString()));
            }
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static bool IsHexDigit(char character) =>
        (character >= '0' && character <= '9')
        || (character >= 'a' && character <= 'f')
        || (character >= 'A' && character <= 'F');

    private PathValue WithName(PythonValue name, TextSpan span)
    {
        if (!ManagedObjectProtocols.IsTrue(name))
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"Invalid name {name.ToRepresentationString()}",
                span,
                "ValueError"
            );
        }

        if (name is PythonTextValue text)
        {
            if (text.Value.Contains('/', StringComparison.Ordinal) || text.Value == ".")
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"Invalid name {Repr(text.Value)}",
                    span,
                    "ValueError"
                );
            }

            if (_tail.Length == 0)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{ToRepresentationString()} has an empty name",
                    span,
                    "ValueError"
                );
            }

            return FromParsed(Root, [.. _tail[..^1], text.Value]);
        }

        // CPython evaluates `sep in name`; a non-container raises its own message.
        bool contains;
        try
        {
            contains = ManagedObjectProtocols.Contains(name, Text("/"), span);
        }
        catch (PythonRuntimeException error) when (error.PythonExceptionTypeName is "TypeError")
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"argument of type '{ManagedObjectProtocols.GetTypeName(name)}' is not a "
                    + "container or iterable",
                span,
                "TypeError"
            );
        }

        if (contains)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"Invalid name {name.ToRepresentationString()}",
                span,
                "ValueError"
            );
        }

        if (_tail.Length == 0)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{ToRepresentationString()} has an empty name",
                span,
                "ValueError"
            );
        }

        return FromParsed(Root, [.. _tail[..^1], PythonPathlib.ConvertSegment(name, span)]);
    }

    private PathValue WithStem(PythonValue stem, TextSpan span)
    {
        if (stem is PythonTextValue text)
        {
            return WithName(new PythonTextValue(text.Value + Suffix), span);
        }

        if (stem is PythonByteSequenceValue)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4005",
                "can't concat str to bytes",
                span,
                "TypeError"
            );
        }

        throw ManagedObjectProtocols.Fault(
            "DPY4005",
            $"unsupported operand type(s) for +: '{ManagedObjectProtocols.GetTypeName(stem)}' and 'str'",
            span,
            "TypeError"
        );
    }

    private PathValue WithSuffix(PythonValue suffix, TextSpan span)
    {
        var stem = Stem;
        if (stem.Length == 0)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{ToRepresentationString()} has an empty name",
                span,
                "ValueError"
            );
        }

        if (suffix is PythonTextValue text)
        {
            if (text.Value.Length > 0 && !text.Value.StartsWith('.'))
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"Invalid suffix {Repr(text.Value)}",
                    span,
                    "ValueError"
                );
            }

            return WithName(new PythonTextValue(stem + text.Value), span);
        }

        if (ManagedObjectProtocols.IsTrue(suffix))
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4022",
                $"'{ManagedObjectProtocols.GetTypeName(suffix)}' object has no attribute 'startswith'",
                span,
                "AttributeError"
            );
        }

        throw ManagedObjectProtocols.Fault(
            "DPY4005",
            $"can only concatenate str (not \"{ManagedObjectProtocols.GetTypeName(suffix)}\") to str",
            span,
            "TypeError"
        );
    }

    private bool IsRelativeTo(PathValue other)
    {
        if (string.Equals(Str, other.Str, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var parent in Parents.ParentList)
        {
            if (string.Equals(parent.Str, other.Str, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private PathValue RelativeTo(PathValue other, bool walkUp, TextSpan span)
    {
        PathValue? matched = null;
        var step = 0;
        var candidates = new List<PathValue> { other };
        candidates.AddRange(other.Parents.ParentList);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (
                string.Equals(candidate.Str, Str, StringComparison.Ordinal) || IsParentOf(candidate)
            )
            {
                matched = candidate;
                step = index;
                break;
            }

            if (!walkUp)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"{Repr(Str)} is not in the subpath of {Repr(other.Str)}",
                    span,
                    "ValueError"
                );
            }

            if (candidate.Name == "..")
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    $"'..' segment in {Repr(other.Str)} cannot be walked",
                    span,
                    "ValueError"
                );
            }
        }

        if (matched is null)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{Repr(Str)} and {Repr(other.Str)} have different anchors",
                span,
                "ValueError"
            );
        }

        var parts = new List<string>();
        for (var index = 0; index < step; index++)
        {
            parts.Add("..");
        }

        parts.AddRange(_tail.Skip(matched.Tail.Length));
        return FromParsed(string.Empty, [.. parts]);
    }

    private bool IsParentOf(PathValue candidate)
    {
        foreach (var parent in Parents.ParentList)
        {
            if (string.Equals(parent.Str, candidate.Str, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool Match(PathValue pattern, bool caseSensitive, TextSpan span)
    {
        var pathParts = Parts;
        var patternParts = pattern.Parts;
        if (patternParts.Length == 0)
        {
            throw ManagedObjectProtocols.Fault("DPY4003", "empty pattern", span, "ValueError");
        }

        if (pathParts.Length < patternParts.Length)
        {
            return false;
        }

        if (pathParts.Length > patternParts.Length && pattern.Anchor.Length > 0)
        {
            return false;
        }

        for (var index = 0; index < patternParts.Length; index++)
        {
            var patternPart = patternParts[^(index + 1)];
            var pathPart = pathParts[^(index + 1)];
            if (!PathGlob.Compile(patternPart, recursive: true, caseSensitive).IsMatch(pathPart))
            {
                return false;
            }
        }

        return true;
    }

    private bool FullMatch(PathValue pattern, bool caseSensitive)
    {
        var pathText = Parts.Length > 0 ? Str : string.Empty;
        var patternText = pattern.Parts.Length > 0 ? pattern.Str : string.Empty;
        return PathGlob.Compile(patternText, recursive: true, caseSensitive).IsMatch(pathText);
    }

    private PathValue Absolute()
    {
        if (IsAbsolute)
        {
            return this;
        }

        var cwd = Directory.GetCurrentDirectory();
        if (_tail.Length == 0)
        {
            return Create(Class, Kind, [cwd], cwd);
        }

        var joined = cwd + "/" + string.Join('/', _tail);
        return Create(Class, Kind, [joined], joined);
    }

    private PathValue Resolve(bool strict, TextSpan span)
    {
        var full = GetDisplayFullPath();
        var within = Context.IsWithinSearchRoots(full);
        if (!within)
        {
            if (strict)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4037",
                    $"{Class.Name}.resolve() with strict=True outside the registered module "
                        + "search roots is not permitted in this runtime slice.",
                    span,
                    "PermissionError"
                );
            }

            // Purely lexical normalization; nothing outside the roots is inspected.
            return Create(Class, Kind, [full], full);
        }

        var components = new List<string>();
        if (!IsAbsolute)
        {
            components.AddRange(
                Directory.GetCurrentDirectory().Split('/').Where(part => part.Length != 0)
            );
        }

        var missing = string.Empty;
        foreach (var part in Str.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (components.Count > 0)
                {
                    components.RemoveAt(components.Count - 1);
                }

                continue;
            }

            components.Add(part);
            var candidate = "/" + string.Join('/', components);
            if (missing.Length == 0 && !File.Exists(candidate) && !Directory.Exists(candidate))
            {
                missing = candidate;
            }
        }

        if (strict && missing.Length > 0)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4037",
                $"[Errno 2] No such file or directory: '{missing}'",
                span,
                "FileNotFoundError"
            );
        }

        var resolved = "/" + string.Join('/', components);
        return Create(Class, Kind, [resolved], resolved);
    }

    private PathValue ExpandUser(TextSpan span)
    {
        if (Root.Length > 0 || _tail.Length == 0 || !_tail[0].StartsWith('~'))
        {
            return this;
        }

        var home = ExpandUserPath(_tail[0]);
        if (home.StartsWith('~'))
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4037",
                "Could not determine home directory.",
                span,
                "RuntimeError"
            );
        }

        var (root, rest) = PythonPathlib.SplitRoot(home);
        var tail = new List<string>();
        if (rest.Length > 0)
        {
            tail.AddRange(rest.Split('/').Where(part => part.Length != 0 && part != "."));
        }

        tail.AddRange(_tail.Skip(1));
        return FromParsed(root, [.. tail]);
    }

    internal static string RequireHomeDirectory(TextSpan span)
    {
        var home = ExpandUserPath("~");
        if (home.StartsWith('~'))
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4037",
                "Could not determine home directory.",
                span,
                "RuntimeError"
            );
        }

        return home;
    }

    /// <summary>`os.path.expanduser` on posix: `~` comes from HOME, `~user` is refused.</summary>
    private static string ExpandUserPath(string path)
    {
        if (!path.StartsWith('~'))
        {
            return path;
        }

        var slash = path.IndexOf('/', 1);
        var end = slash < 0 ? path.Length : slash;
        string home;
        if (end == 1)
        {
            home = Environment.GetEnvironmentVariable("HOME") ?? "~";
        }
        else
        {
            // There is no user database in this runtime slice.
            return path;
        }

        var expanded = home + path[end..];
        return expanded.Length > 0 ? expanded : "/";
    }

    private PythonListValue Iterdir(TextSpan span)
    {
        var full = FullPath("iterdir", span);
        if (!Directory.Exists(full))
        {
            // `NotADirectoryError` is not a defined builtin in this runtime slice, so the
            // nearest defined ancestor (`OSError`) carries CPython's message and errno.
            throw ManagedObjectProtocols.Fault(
                "DPY4037",
                File.Exists(full)
                    ? $"[Errno 20] Not a directory: '{Str}'"
                    : $"[Errno 2] No such file or directory: '{Str}'",
                span,
                File.Exists(full) ? "OSError" : "FileNotFoundError"
            );
        }

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(full);
        }
        catch (IOException)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4037",
                $"[Errno 2] No such file or directory: '{Str}'",
                span,
                "FileNotFoundError"
            );
        }

        Array.Sort(entries, StringComparer.Ordinal);
        var results = new List<PythonValue>(entries.Length);
        foreach (var entry in entries)
        {
            var name = System.IO.Path.GetFileName(entry);
            var display = Str == "." ? name : PythonPathlib.Join(Str, [name]);
            results.Add(Create(Class, Kind, [display], display).Wrapped);
        }

        return new PythonListValue(results);
    }

    private PythonListValue Glob(
        bool recursivePrefix,
        string name,
        PythonValue patternValue,
        bool? caseSensitive,
        bool recurseSymlinks,
        TextSpan span
    )
    {
        if (recurseSymlinks)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4037",
                $"{Class.Name}.{name}() does not support recurse_symlinks=True in this runtime "
                    + "slice (symlinks are out of scope).",
                span,
                "NotImplementedError"
            );
        }

        var pattern = patternValue is PythonExternalObjectValue { Protocol: PathValue patternPath }
            ? patternPath.Str
            : PythonPathlib.ConvertSegment(patternValue, span);
        if (recursivePrefix)
        {
            pattern = PythonPathlib.Join("**", [pattern]);
        }

        var parts = ParsePattern(pattern, span);
        var sensitivity = caseSensitive ?? true;
        var pedantic = caseSensitive.HasValue;
        var select = new PathGlob.Selector([.. parts], 0, sensitivity, pedantic);
        var root = Str;
        var rootFull = ScriptPath.Resolve(root);
        if (!Context.IsWithinSearchRoots(rootFull))
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4037",
                $"{Class.Name}.{name}() outside the registered module search roots is not "
                    + "permitted in this runtime slice.",
                span,
                "PermissionError"
            );
        }

        var paths = select.Select(PythonPathlib.Join(root, [string.Empty]), false).ToList();
        if (root == ".")
        {
            paths = [.. paths.Select(path => path.Length > 2 ? path[2..] : string.Empty)];
        }

        if (parts[^1].Length == 0)
        {
            paths = [.. paths.Select(path => path.Length > 0 ? path[..^1] : path)];
        }
        else if (parts[^1] == "**")
        {
            var anchorLength = Anchor.Length;
            paths =
            [
                .. paths.Select(path =>
                    path.Length > anchorLength && path.EndsWith('/') ? path[..^1] : path
                ),
            ];
        }

        return new PythonListValue([
            .. paths.Select(path =>
                Create(Class, Kind, [path], path.Length > 0 ? path : ".").Wrapped
            ),
        ]);
    }

    private static List<string> ParsePattern(string pattern, TextSpan span)
    {
        var (root, rest) = PythonPathlib.SplitRoot(pattern);
        if (root.Length > 0)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4037",
                "Non-relative patterns are unsupported",
                span,
                "NotImplementedError"
            );
        }

        var parts = rest.Split('/').Where(part => part.Length != 0 && part != ".").ToList();
        if (parts.Count == 0)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"Unacceptable pattern: {Repr(pattern)}",
                span,
                "ValueError"
            );
        }

        if (pattern.EndsWith('/'))
        {
            parts.Add(string.Empty);
        }

        return parts;
    }

    private PythonListValue Walk(
        bool topDown,
        PythonValue? onError,
        bool followSymlinks,
        TextSpan span
    )
    {
        if (followSymlinks)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4037",
                $"{Class.Name}.walk() does not support follow_symlinks=True in this runtime "
                    + "slice (symlinks are out of scope).",
                span,
                "NotImplementedError"
            );
        }

        if (onError is not null and not PythonNoneValue)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4037",
                $"{Class.Name}.walk() does not support on_error in this runtime slice.",
                span,
                "NotImplementedError"
            );
        }

        var full = FullPath("walk", span);
        var results = new List<PythonValue>();
        WalkDirectory(full, Str, topDown, results);
        return new PythonListValue(results);
    }

    private void WalkDirectory(string full, string display, bool topDown, List<PythonValue> results)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(full);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Array.Sort(entries, StringComparer.Ordinal);
        var dirnames = new List<PythonValue>();
        var filenames = new List<PythonValue>();
        var children = new List<(string Full, string Display)>();
        foreach (var entry in entries)
        {
            var name = System.IO.Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                dirnames.Add(Text(name));
                children.Add((entry, PythonPathlib.Join(display, [name])));
            }
            else
            {
                filenames.Add(Text(name));
            }
        }

        PythonValue Emit()
        {
            var shown = Str == "." ? (display.Length > 2 ? display[2..] : string.Empty) : display;
            var path = Create(Class, Kind, [shown], shown.Length > 0 ? shown : ".").Wrapped;
            return new PythonTupleValue([
                path,
                new PythonListValue(dirnames),
                new PythonListValue(filenames),
            ]);
        }

        if (topDown)
        {
            results.Add(Emit());
        }

        foreach (var (childFull, childDisplay) in children)
        {
            WalkDirectory(childFull, childDisplay, topDown, results);
        }

        if (!topDown)
        {
            results.Add(Emit());
        }
    }

    private static void RequireTextOptions(
        string name,
        PythonValue? encoding,
        PythonValue? errors,
        PythonValue? newline,
        TextSpan span
    )
    {
        if (encoding is not null and not PythonNoneValue && !IsUtf8(encoding))
        {
            throw UnsupportedTextOption(name, "encoding", span);
        }

        // 'strict' is the default and the only handler this slice implements.
        if (errors is not null and not PythonNoneValue)
        {
            if (errors is not PythonTextValue { Value: "strict" })
            {
                throw UnsupportedTextOption(name, "errors", span);
            }
        }

        if (newline is not null and not PythonNoneValue)
        {
            throw UnsupportedTextOption(name, "newline", span);
        }
    }

    private static bool IsUtf8(PythonValue value) =>
        value is PythonTextValue text
        && text.Value.ToUpperInvariant()
            is "UTF-8"
                or "UTF8"
                or "UTF_8"
                or "U8"
                or "UTF"
                or "CP65001";

    private static PythonRuntimeException UnsupportedTextOption(
        string name,
        string option,
        TextSpan span
    ) =>
        ManagedObjectProtocols.Fault(
            "DPY4037",
            $"{name}() does not support the '{option}' argument in this runtime slice "
                + "(decoding is always UTF-8).",
            span,
            "NotImplementedError"
        );

    private PythonFileValue Open(PythonValue?[] slots, TextSpan span)
    {
        var mode = "r";
        if (slots[0] is not null)
        {
            if (slots[0] is not PythonTextValue modeText)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4037",
                    $"open() argument 'mode' must be str, not "
                        + $"{ManagedObjectProtocols.GetTypeName(slots[0]!)}",
                    span,
                    "TypeError"
                );
            }

            mode = modeText.Value;
        }

        if (slots[1] is not null and not PythonNoneValue)
        {
            if (slots[1] is not PythonWholeNumberValue buffering || buffering.Value != -1)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4037",
                    "Path.open() does not support the 'buffering' argument beyond its "
                        + "default (-1) in this runtime slice.",
                    span,
                    "NotImplementedError"
                );
            }
        }

        RequireTextOptions("Path.open", slots[2], slots[3], slots[4], span);
        var file = ManagedObjectProtocols.Call(Context.OpenBuiltin, [Text(Str), Text(mode)], span);
        return file is PythonFileValue opened
            ? opened
            : throw new PythonRuntimeException(
                "DPY4037",
                "open() did not return a file object.",
                span
            );
    }

    private PythonTextValue ReadText(TextSpan span)
    {
        _ = FullPath("read_text", span);
        var file = ManagedObjectProtocols.Call(Context.OpenBuiltin, [Text(Str), Text("r")], span);
        return file is PythonFileValue opened
            ? new PythonTextValue(opened.Content)
            : throw new PythonRuntimeException(
                "DPY4037",
                "open() did not return a file object.",
                span
            );
    }

    private PythonByteSequenceValue ReadBytes(TextSpan span)
    {
        var full = FullPath("read_bytes", span);
        if (Directory.Exists(full))
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4037",
                $"[Errno 21] Is a directory: '{Str}'",
                span,
                "IsADirectoryError"
            );
        }

        if (!File.Exists(full))
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4037",
                $"[Errno 2] No such file or directory: '{Str}'",
                span,
                "FileNotFoundError"
            );
        }

        try
        {
            if (new FileInfo(full).Length > PythonStandardModules.MaximumOpenFileLength)
            {
                throw new PythonRuntimeException(
                    "DPY4037",
                    $"read_bytes() beyond the {PythonStandardModules.MaximumOpenFileLength} byte "
                        + "limit is not supported in this runtime slice.",
                    span
                );
            }

            return new PythonByteSequenceValue(File.ReadAllBytes(full));
        }
        catch (IOException exception)
        {
            throw ManagedObjectProtocols.Fault("DPY4037", exception.Message, span, "OSError");
        }
    }
}

/// <summary>The `Path.parents` sequence.</summary>
internal sealed class PathParentsValue : PythonExternalObjectProtocol, IPythonExternalIterable
{
    private readonly PathValue _owner;
    private List<PathValue>? _items;
    private PythonValue? _wrapped;

    internal PathParentsValue(PathValue owner) => _owner = owner;

    internal PythonValue Wrapped => _wrapped ??= new PythonExternalObjectValue(this);

    /// <summary>The logical ancestors, nearest first.</summary>
    internal List<PathValue> ParentList => _items ??= Build();

    IReadOnlyList<PythonValue> IPythonExternalIterable.IterationItems =>
        [.. ParentList.Select(parent => parent.Wrapped)];

    private List<PathValue> Build()
    {
        var tail = _owner.Tail;
        var items = new List<PathValue>(tail.Length);
        for (var count = 1; count <= tail.Length; count++)
        {
            items.Add(_owner.FromParsed(_owner.Root, tail[..^count]));
        }

        return items;
    }

    public PythonValue GetAttribute(string name, TextSpan span) =>
        throw ManagedObjectProtocols.MissingAttribute("_PathParents", name, span);

    public PythonValue Call(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        throw ManagedObjectProtocols.Fault(
            "DPY4003",
            "'_PathParents' object is not callable",
            span,
            "TypeError"
        );

    public PythonValue CallWithKeywords(
        IReadOnlyList<PythonValue> arguments,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    ) => Call(arguments, span);

    public PythonValue GetItem(PythonValue index, TextSpan span)
    {
        if (index is PythonSliceValue slice)
        {
            var selected = new List<PythonValue>();
            foreach (
                var elementIndex in ManagedObjectProtocols.EnumerateSliceIndices(
                    slice,
                    ParentList.Count,
                    span
                )
            )
            {
                selected.Add(ParentList[elementIndex].Wrapped);
            }

            return new PythonTupleValue([.. selected]);
        }

        if (index is not PythonWholeNumberValue number)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"sequence index must be integer, not "
                    + $"'{ManagedObjectProtocols.GetTypeName(index)}'",
                span,
                "TypeError"
            );
        }

        var position = (int)number.Value;
        if (position >= ParentList.Count || position < -ParentList.Count)
        {
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                position.ToString(CultureInfo.InvariantCulture),
                span,
                "IndexError"
            );
        }

        return ParentList[position < 0 ? position + ParentList.Count : position].Wrapped;
    }

    public long GetHash(TextSpan span) => RuntimeHelpers.GetHashCode(this);

    public int GetLength(TextSpan span) => ParentList.Count;

    public PythonTruthValue RichCompare(
        PythonValue other,
        PythonRichComparison comparison,
        TextSpan span
    )
    {
        var equal =
            other is PythonExternalObjectValue { Protocol: PathParentsValue parents }
            && ReferenceEquals(this, parents);
        return comparison switch
        {
            PythonRichComparison.Equal => PythonTruthValue.FromBoolean(equal),
            PythonRichComparison.NotEqual => PythonTruthValue.FromBoolean(!equal),
            _ => throw ManagedObjectProtocols.Fault(
                "DPY4005",
                $"'{PathValue.ComparisonSymbol(comparison)}' not supported between instances "
                    + $"of '_PathParents' and '{ManagedObjectProtocols.GetTypeName(other)}'",
                span,
                "TypeError"
            ),
        };
    }

    public bool IsInstanceOf(PythonValue value, TextSpan span) => false;

    public string ToDisplayString() => $"<{_owner.Class.Name}.parents>";

    public string ToRepresentationString() => ToDisplayString();
}
