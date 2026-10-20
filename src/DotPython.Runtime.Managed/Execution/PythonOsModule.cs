using System.Globalization;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The `os` module and its `os.path` companion over the host filesystem, held to the
/// runtime's capability boundary: every read must resolve beneath the registered module
/// search roots, and anything outside them reports a `PermissionError` in this runtime's
/// words. The slice carries the read-only surface — `getcwd`/`getcwdb`/`getpid`,
/// `listdir`, `scandir` with its name, path and type probes, and `os.path`'s POSIX string
/// helpers and existence probes — while mutations, metadata (`stat`/`lstat`/`readlink`),
/// `chdir` and the process environment refuse with `PermissionError` or
/// `NotImplementedError` instead of widening the host access the runtime provides.
/// `DirEntry` iteration is a snapshot.
/// </summary>
internal static class PythonOsModule
{
    internal static void Initialize(
        PythonGlobalNamespace globals,
        IReadOnlyList<string> searchRoots
    )
    {
        var context = new OsContext(searchRoots);
        var pathGlobals = new PythonGlobalNamespace();
        pathGlobals.SetValue("__name__", new PythonTextValue("os.path"));
        pathGlobals.SetValue("__package__", new PythonTextValue("os"));
        InitializePath(pathGlobals, context);
        globals.SetValue("path", new PythonModuleValue("os.path", pathGlobals));
        globals.SetValue("sep", new PythonTextValue("/"));
        globals.SetValue("altsep", PythonNoneValue.Instance);
        globals.SetValue("pathsep", new PythonTextValue(":"));
        globals.SetValue("curdir", new PythonTextValue("."));
        globals.SetValue("pardir", new PythonTextValue(".."));
        globals.SetValue("linesep", new PythonTextValue("\n"));
        globals.SetValue("extsep", new PythonTextValue("."));
        globals.SetValue("devnull", new PythonTextValue("/dev/null"));
        globals.SetValue("name", new PythonTextValue("posix"));
        globals.SetValue(
            "fspath",
            new PythonBuiltinFunctionValue(
                "fspath",
                (arguments, span) => PythonPathlib.Fspath(arguments, span)
            )
        );
        globals.SetValue("getcwd", Function("getcwd", (_, span) => Text(Getcwd()), 0));
        globals.SetValue("getcwdb", Function("getcwdb", (_, _) => Bytes(Getcwd()), 0));
        globals.SetValue(
            "chdir",
            Function("chdir", (arguments, span) => Chdir(arguments, context, span), 1)
        );
        globals.SetValue(
            "listdir",
            Function("listdir", (arguments, span) => ListDir(arguments, context, span), 0, 1)
        );
        globals.SetValue(
            "scandir",
            Function("scandir", (arguments, span) => ScanDir(arguments, context, span), 0, 1)
        );
        globals.SetValue(
            "stat",
            Function(
                "stat",
                (arguments, span) => Stat(arguments, context, span, follow: true),
                1,
                2
            )
        );
        globals.SetValue(
            "lstat",
            Function(
                "lstat",
                (arguments, span) => Stat(arguments, context, span, follow: false),
                1,
                2
            )
        );
        globals.SetValue(
            "mkdir",
            Function("mkdir", (arguments, span) => MkDir(arguments, context, span), 1, 2)
        );
        globals.SetValue(
            "makedirs",
            Function("makedirs", (arguments, span) => MakeDirs(arguments, context, span), 1, 3)
        );
        globals.SetValue(
            "rmdir",
            Function("rmdir", (arguments, span) => RmDir(arguments, context, span), 1)
        );
        globals.SetValue(
            "remove",
            Function("remove", (arguments, span) => Remove(arguments, context, span), 1)
        );
        globals.SetValue(
            "unlink",
            Function("unlink", (arguments, span) => Remove(arguments, context, span), 1)
        );
        globals.SetValue(
            "rename",
            Function("rename", (arguments, span) => Rename(arguments, context, span, false), 2)
        );
        globals.SetValue(
            "replace",
            Function("replace", (arguments, span) => Rename(arguments, context, span, true), 2)
        );
        globals.SetValue(
            "getenv",
            Function("getenv", (_, span) => throw RefuseProcess("getenv", span), 1, 2)
        );
        globals.SetValue(
            "putenv",
            Function("putenv", (_, span) => throw RefuseProcess("putenv", span), 2)
        );
        globals.SetValue(
            "unsetenv",
            Function("unsetenv", (_, span) => throw RefuseProcess("unsetenv", span), 1)
        );
        globals.SetValue("environ", new PythonManagedObjectValue(EnvironType));
        globals.SetValue(
            "getpid",
            Function("getpid", (_, _) => PythonWholeNumberValue.Create(Environment.ProcessId), 0)
        );
        globals.SetValue(
            "symlink",
            Function(
                "symlink",
                (arguments, span) =>
                {
                    _ = arguments;
                    _ = context;
                    throw RefuseWrite("symlink", span);
                },
                2,
                3
            )
        );
        globals.SetValue(
            "readlink",
            Function(
                "readlink",
                (arguments, span) =>
                {
                    _ = arguments;
                    _ = context;
                    throw RefuseMetadata("readlink", span);
                },
                1
            )
        );
        globals.SetValue("stat_result", StatResultType);
        globals.SetValue("DirEntry", DirEntryType);
    }

    private sealed class OsContext(IReadOnlyList<string> searchRoots)
    {
        internal IReadOnlyList<string> SearchRoots { get; } = searchRoots;

        internal string Full(string caller, string path, TextSpan span)
        {
            var full = Path.GetFullPath(path);
            if (!PythonStandardModules.IsWithinSearchRoots(full, SearchRoots))
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4061",
                    $"os.{caller}() outside the registered module search roots is not "
                        + "permitted in this runtime slice.",
                    span,
                    "PermissionError"
                );
            }
            return full;
        }
    }

    private static PythonTextValue Text(string value) => new(value);

    private static PythonByteSequenceValue Bytes(string value) =>
        new(System.Text.Encoding.UTF8.GetBytes(value));

    private static PythonRuntimeException Fault(
        string message,
        TextSpan span,
        string typeName = "OSError"
    ) => ManagedObjectProtocols.Fault("DPY4061", message, span, typeName);

    private static PythonRaisedException Errno(
        int number,
        string text,
        string path,
        TextSpan span,
        string? typeName = null,
        string? second = null
    ) =>
        new(
            new PythonExceptionValue(
                typeName ?? "OSError",
                $"[Errno {number}] {text}: '{path}'"
                    + (second is null ? string.Empty : $" -> '{second}'")
            )
            {
                OSErrorSlots = new OSErrorSlots(
                    PythonWholeNumberValue.Create(number),
                    Text(text),
                    Text(path),
                    second is null ? null : Text(second)
                ),
            }
        );

    private static PythonBuiltinFunctionValue Function(
        string name,
        Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> body,
        int minimum,
        int maximum = -1
    ) =>
        new(
            name,
            (arguments, span) =>
            {
                var high = maximum < 0 ? minimum : maximum;
                if (arguments.Count < minimum || arguments.Count > high)
                {
                    throw ManagedObjectProtocols.Fault(
                        "DPY4061",
                        $"{name}() takes {minimum} to {high} arguments "
                            + $"({arguments.Count} given)",
                        span,
                        "TypeError"
                    );
                }
                return body(arguments, span);
            }
        );

    private static string RequirePath(PythonValue value, TextSpan span)
    {
        var text = PythonPathlib.Fspath([value], span);
        return text is PythonTextValue path
            ? path.Value
            : throw ManagedObjectProtocols.Fault(
                "DPY4061",
                "bytes paths are not supported in this runtime slice.",
                span,
                "NotImplementedError"
            );
    }

    private static string OptionalPath(IReadOnlyList<PythonValue> arguments, TextSpan span) =>
        arguments.Count == 0 ? "." : RequirePath(arguments[0], span);

    private static PythonRuntimeException RefuseWrite(string name, TextSpan span) =>
        ManagedObjectProtocols.Fault(
            "DPY4061",
            $"os.{name}() requires a filesystem write capability that this runtime slice "
                + "does not provide.",
            span,
            "PermissionError"
        );

    private static PythonRuntimeException RefuseMetadata(string name, TextSpan span) =>
        ManagedObjectProtocols.Fault(
            "DPY4061",
            $"os.{name}() is not supported in this runtime slice (file metadata, ownership "
                + "and symlinks are out of scope).",
            span,
            "NotImplementedError"
        );

    private static PythonRuntimeException RefuseEnvironment(TextSpan span) =>
        ManagedObjectProtocols.Fault(
            "DPY4061",
            "os.environ requires a host-process capability that this runtime slice does "
                + "not provide.",
            span,
            "PermissionError"
        );

    private static PythonRuntimeException RefuseProcess(string name, TextSpan span) =>
        ManagedObjectProtocols.Fault(
            "DPY4061",
            $"os.{name}() requires a host-process capability that this runtime slice does "
                + "not provide.",
            span,
            "PermissionError"
        );

    private static readonly PythonManagedTypeValue EnvironType = CreateEnvironType();

    private static PythonManagedTypeValue CreateEnvironType()
    {
        var type = new PythonManagedTypeValue("_Environ") { Module = "os" };
        type.Attributes["__module__"] = Text("os");
        foreach (
            var name in new[]
            {
                "__getitem__",
                "__setitem__",
                "__delitem__",
                "__contains__",
                "__iter__",
                "__len__",
                "__repr__",
                "get",
                "keys",
                "values",
                "items",
                "copy",
                "setdefault",
                "pop",
                "popitem",
                "update",
                "clear",
            }
        )
        {
            type.Attributes[name] = Method(
                "_Environ",
                name,
                (_, _) => throw RefuseEnvironment(default)
            );
        }
        return type;
    }

    private static string Getcwd()
    {
        var current = Directory.GetCurrentDirectory();
        return current.Length > 1 ? current.TrimEnd('/') : current;
    }

    private static PythonNoneValue Chdir(
        IReadOnlyList<PythonValue> arguments,
        OsContext context,
        TextSpan span
    )
    {
        _ = RequirePath(arguments[0], span);
        _ = context;
        throw RefuseProcess("chdir", span);
    }

    private static PythonListValue ListDir(
        IReadOnlyList<PythonValue> arguments,
        OsContext context,
        TextSpan span
    )
    {
        var path = OptionalPath(arguments, span);
        if (path.Length == 0)
        {
            throw Errno(2, "No such file or directory", path, span, "FileNotFoundError");
        }
        var full = context.Full("listdir", path, span);
        var kind = KindOf(full);
        if (kind != 2)
        {
            throw kind == 1
                ? Errno(20, "Not a directory", path, span, "NotADirectoryError")
                : Errno(2, "No such file or directory", path, span, "FileNotFoundError");
        }
        var names = new List<PythonValue>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(full))
        {
            names.Add(Text(Path.GetFileName(entry)));
        }
        return new PythonListValue(names);
    }

    private static readonly PythonManagedTypeValue DirEntryType = CreateDirEntryType();

    private sealed record DirEntryState(string Name, string Path, string FullPath);

    private static PythonManagedTypeValue CreateDirEntryType()
    {
        var type = new PythonManagedTypeValue("DirEntry") { Module = "os" };
        type.Attributes["__module__"] = Text("os");
        type.Attributes["__repr__"] = Method(
            "DirEntry",
            "__repr__",
            (self, _) => Text($"<DirEntry '{DirEntryOf(self!).Name}'>")
        );
        type.Attributes["name"] = Property("name", state => Text(state.Name));
        type.Attributes["path"] = Property("path", state => Text(state.Path));
        type.Attributes["is_dir"] = Method(
            "DirEntry",
            "is_dir",
            (self, _) => PythonTruthValue.FromBoolean(KindOf(DirEntryOf(self!).FullPath) == 2)
        );
        type.Attributes["is_file"] = Method(
            "DirEntry",
            "is_file",
            (self, _) => PythonTruthValue.FromBoolean(KindOf(DirEntryOf(self!).FullPath) == 1)
        );
        type.Attributes["is_symlink"] = Method(
            "DirEntry",
            "is_symlink",
            (self, _) => PythonTruthValue.FromBoolean(IsSymlink(DirEntryOf(self!).FullPath))
        );
        type.Attributes["stat"] = Method(
            "DirEntry",
            "stat",
            (_, _) => throw RefuseMetadata("DirEntry.stat", default)
        );
        type.Attributes["inode"] = Method(
            "DirEntry",
            "inode",
            (_, _) => throw RefuseMetadata("DirEntry.inode", default)
        );
        return type;
    }

    private static PythonPropertyValue Property(
        string name,
        Func<DirEntryState, PythonValue> getter
    ) =>
        new(
            new PythonProtocolFunctionValue(
                name,
                (_, arguments) => getter(DirEntryOf(arguments[0]))
            ),
            null,
            null
        );

    private static DirEntryState DirEntryOf(PythonValue value) =>
        value is PythonManagedObjectValue { Payload: DirEntryState state }
            ? state
            : throw Fault("the value is not a DirEntry", default, "TypeError");

    private static PythonIteratorValue ScanDir(
        IReadOnlyList<PythonValue> arguments,
        OsContext context,
        TextSpan span
    )
    {
        var path = OptionalPath(arguments, span);
        if (path.Length == 0)
        {
            throw Errno(2, "No such file or directory", path, span, "FileNotFoundError");
        }
        var full = context.Full("scandir", path, span);
        var kind = KindOf(full);
        if (kind != 2)
        {
            throw kind == 1
                ? Errno(20, "Not a directory", path, span, "NotADirectoryError")
                : Errno(2, "No such file or directory", path, span, "FileNotFoundError");
        }
        var entries = new List<DirEntryState>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(full))
        {
            var name = Path.GetFileName(entry);
            entries.Add(new DirEntryState(name, JoinPath(path, name), entry));
        }
        return ManagedObjectProtocols.GetIterator(
            new PythonListValue([
                .. entries.Select(entry =>
                    (PythonValue)new PythonManagedObjectValue(DirEntryType, entry)
                ),
            ]),
            span
        );
    }

    private static string JoinPath(string left, string right) =>
        right.StartsWith('/') ? right
        : left.Length == 0 ? right
        : left.EndsWith('/') ? left + right
        : left + "/" + right;

    // -----------------------------------------------------------------------------
    // stat
    // -----------------------------------------------------------------------------

    private static readonly PythonManagedTypeValue StatResultType = CreateStatResultType();

    private static PythonManagedTypeValue CreateStatResultType()
    {
        var factory = (PythonBuiltinFunctionValue)PythonNamedTuple.Function;
        var created = factory.Invoke(
            [
                Text("stat_result"),
                Text(
                    "st_mode st_ino st_dev st_nlink st_uid st_gid st_size "
                        + "st_atime st_mtime st_ctime"
                ),
            ],
            default
        );
        if (created is PythonManagedTypeValue type)
        {
            type.Module = "os";
            type.Attributes["__module__"] = Text("os");
            return type;
        }
        throw Fault("the stat_result type could not be built", default);
    }

    /// <summary>
    /// The target's kind: 1 for a regular file, 2 for a directory, 0 otherwise.
    /// `File.Exists` and `Directory.Exists` report the link itself on Unix, so the
    /// link chain is resolved here the way `stat` follows it; a dangling link is 0.
    /// </summary>
    private static int KindOf(string path, int depth = 0)
    {
        if (depth > 40 || path.Length == 0)
        {
            return 0;
        }
        string? target;
        try
        {
            target = new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
        if (target is null)
        {
            return File.Exists(path) ? 1
                : Directory.Exists(path) ? 2
                : 0;
        }
        if (target.Length == 0)
        {
            return 0;
        }
        var resolved = Path.IsPathRooted(target)
            ? target
            : Path.Combine(Path.GetDirectoryName(path) ?? ".", target);
        return KindOf(Path.GetFullPath(resolved), depth + 1);
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null
                || new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static PythonValue Stat(
        IReadOnlyList<PythonValue> arguments,
        OsContext context,
        TextSpan span,
        bool follow
    )
    {
        _ = RequirePath(arguments[0], span);
        _ = context;
        throw RefuseMetadata(follow ? "stat" : "lstat", span);
    }

    // -----------------------------------------------------------------------------
    // Mutations and the process environment
    // -----------------------------------------------------------------------------

    private static PythonNoneValue MkDir(
        IReadOnlyList<PythonValue> arguments,
        OsContext context,
        TextSpan span
    )
    {
        _ = arguments;
        _ = context;
        throw RefuseWrite("mkdir", span);
    }

    private static PythonNoneValue MakeDirs(
        IReadOnlyList<PythonValue> arguments,
        OsContext context,
        TextSpan span
    )
    {
        _ = arguments;
        _ = context;
        throw RefuseWrite("makedirs", span);
    }

    private static PythonNoneValue RmDir(
        IReadOnlyList<PythonValue> arguments,
        OsContext context,
        TextSpan span
    )
    {
        _ = arguments;
        _ = context;
        throw RefuseWrite("rmdir", span);
    }

    private static PythonNoneValue Remove(
        IReadOnlyList<PythonValue> arguments,
        OsContext context,
        TextSpan span
    )
    {
        _ = arguments;
        _ = context;
        throw RefuseWrite("remove", span);
    }

    private static PythonNoneValue Rename(
        IReadOnlyList<PythonValue> arguments,
        OsContext context,
        TextSpan span,
        bool replace
    )
    {
        _ = arguments;
        _ = context;
        _ = replace;
        throw RefuseWrite("rename", span);
    }

    private static PythonProtocolFunctionValue Method(
        string owner,
        string name,
        Func<PythonValue?, IReadOnlyList<PythonValue>, PythonValue> body
    ) => PythonUserTypes.Method(owner, name, body);

    // -----------------------------------------------------------------------------
    // os.path
    // -----------------------------------------------------------------------------

    internal static void InitializePathModule(
        PythonGlobalNamespace globals,
        IReadOnlyList<string> searchRoots
    ) => InitializePath(globals, new OsContext(searchRoots));

    private static void InitializePath(PythonGlobalNamespace globals, OsContext context)
    {
        globals.SetValue("sep", Text("/"));
        globals.SetValue("altsep", PythonNoneValue.Instance);
        globals.SetValue("pathsep", Text(":"));
        globals.SetValue("curdir", Text("."));
        globals.SetValue("pardir", Text(".."));
        globals.SetValue("extsep", Text("."));
        globals.SetValue("devnull", Text("/dev/null"));
        globals.SetValue(
            "join",
            new PythonBuiltinFunctionValue(
                "join",
                (arguments, span) =>
                {
                    if (arguments.Count == 0)
                    {
                        throw Fault(
                            "join() missing 1 required positional argument: 'a'",
                            span,
                            "TypeError"
                        );
                    }
                    var joined = RequirePath(arguments[0], span);
                    for (var index = 1; index < arguments.Count; index++)
                    {
                        joined = JoinPath(joined, RequirePath(arguments[index], span));
                    }
                    return Text(joined);
                }
            )
        );
        globals.SetValue(
            "split",
            Function(
                "split",
                (arguments, span) =>
                {
                    var (head, tail) = Split(RequirePath(arguments[0], span));
                    return Tuple(Text(head), Text(tail));
                },
                1
            )
        );
        globals.SetValue(
            "dirname",
            Function(
                "dirname",
                (arguments, span) => Text(Split(RequirePath(arguments[0], span)).Head),
                1
            )
        );
        globals.SetValue(
            "basename",
            Function(
                "basename",
                (arguments, span) => Text(Split(RequirePath(arguments[0], span)).Tail),
                1
            )
        );
        globals.SetValue(
            "splitdrive",
            Function(
                "splitdrive",
                (arguments, span) =>
                    Tuple(Text(string.Empty), Text(RequirePath(arguments[0], span))),
                1
            )
        );
        globals.SetValue(
            "splitext",
            Function(
                "splitext",
                (arguments, span) =>
                {
                    var path = RequirePath(arguments[0], span);
                    var separator = path.LastIndexOf('/');
                    var dot = path.LastIndexOf('.');
                    if (dot > separator)
                    {
                        var nameStart = separator + 1;
                        while (nameStart < dot && path[nameStart] == '.')
                        {
                            nameStart++;
                        }
                        if (nameStart < dot)
                        {
                            return Tuple(Text(path[..dot]), Text(path[dot..]));
                        }
                    }
                    return Tuple(Text(path), Text(string.Empty));
                },
                1
            )
        );
        globals.SetValue(
            "normcase",
            Function("normcase", (arguments, span) => Text(RequirePath(arguments[0], span)), 1)
        );
        globals.SetValue(
            "normpath",
            Function(
                "normpath",
                (arguments, span) => Text(NormalizePath(RequirePath(arguments[0], span))),
                1
            )
        );
        globals.SetValue(
            "isabs",
            Function(
                "isabs",
                (arguments, span) =>
                    PythonTruthValue.FromBoolean(RequirePath(arguments[0], span).StartsWith('/')),
                1
            )
        );
        globals.SetValue(
            "abspath",
            Function(
                "abspath",
                (arguments, span) =>
                {
                    var path = RequirePath(arguments[0], span);
                    var full = path.Length == 0 ? Getcwd() : Path.GetFullPath(path);
                    return Text(full.Length > 1 ? full.TrimEnd('/') : full);
                },
                1
            )
        );
        globals.SetValue("exists", Probe("exists", context, full => KindOf(full) != 0));
        globals.SetValue(
            "lexists",
            Probe("lexists", context, full => KindOf(full) != 0 || IsSymlink(full))
        );
        globals.SetValue("isfile", Probe("isfile", context, full => KindOf(full) == 1));
        globals.SetValue("isdir", Probe("isdir", context, full => KindOf(full) == 2));
        globals.SetValue("islink", Probe("islink", context, IsSymlink));
        globals.SetValue(
            "getsize",
            Function(
                "getsize",
                (arguments, span) =>
                {
                    _ = RequirePath(arguments[0], span);
                    _ = context;
                    throw RefuseMetadata("path.getsize", span);
                },
                1
            )
        );
        globals.SetValue(
            "getmtime",
            Function(
                "getmtime",
                (arguments, span) =>
                {
                    _ = RequirePath(arguments[0], span);
                    _ = context;
                    throw RefuseMetadata("path.getmtime", span);
                },
                1
            )
        );
        globals.SetValue(
            "commonprefix",
            Function(
                "commonprefix",
                (arguments, span) =>
                {
                    var values = ManagedObjectProtocols
                        .MaterializeValues(arguments[0], span)
                        .Select(value => RequirePath(value, span))
                        .ToList();
                    if (values.Count == 0)
                    {
                        return Text(string.Empty);
                    }
                    var prefix = values[0];
                    foreach (var value in values.Skip(1))
                    {
                        var limit = Math.Min(prefix.Length, value.Length);
                        var index = 0;
                        while (index < limit && prefix[index] == value[index])
                        {
                            index++;
                        }
                        prefix = prefix[..index];
                    }
                    return Text(prefix);
                },
                1
            )
        );
    }

    private static PythonBuiltinFunctionValue Probe(
        string name,
        OsContext context,
        Func<string, bool> probe
    ) =>
        Function(
            name,
            (arguments, span) =>
            {
                var path = RequirePath(arguments[0], span);
                if (path.Length == 0)
                {
                    return PythonTruthValue.False;
                }
                var full = context.Full($"path.{name}", path, span);
                return PythonTruthValue.FromBoolean(probe(full));
            },
            1,
            1
        );

    private static PythonTupleValue Tuple(PythonValue left, PythonValue right) =>
        new([left, right]);

    private static (string Head, string Tail) Split(string path)
    {
        var index = path.LastIndexOf('/') + 1;
        var head = path[..index];
        var tail = path[index..];
        if (head.Length != 0 && head.Any(c => c != '/'))
        {
            head = head.TrimEnd('/');
        }
        return (head, tail);
    }

    private static string NormalizePath(string path)
    {
        if (path.Length == 0)
        {
            return ".";
        }
        var absolute = path.StartsWith('/');
        var doubleSlash =
            path.StartsWith("//", StringComparison.Ordinal)
            && !path.StartsWith("///", StringComparison.Ordinal);
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }
            if (part == "..")
            {
                if (parts.Count != 0 && parts[^1] != "..")
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                else if (!absolute)
                {
                    parts.Add("..");
                }
                continue;
            }
            parts.Add(part);
        }
        var body = string.Join('/', parts);
        if (absolute)
        {
            return (doubleSlash ? "//" : "/") + body;
        }
        return body.Length == 0 ? "." : body;
    }
}
