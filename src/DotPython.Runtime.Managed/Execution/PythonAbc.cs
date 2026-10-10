// The `abc` module follows CPython 3.14.7 Lib/abc.py and Modules/_abc.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The <c>abc</c> module: the <c>ABCMeta</c> metaclass the abstract base classes are built on,
/// the <c>abstractmethod</c> marker a class declares its abstract methods with, and the two
/// helpers CPython publishes beside them.
/// </summary>
/// <remarks>
/// A class whose metaclass is <c>ABCMeta</c> carries an <c>__abstractmethods__</c> set: the
/// names its own class body marked abstract, plus the names its bases report that it has not
/// made concrete. A class that is not built on the metaclass carries none, and a marker on its
/// methods means nothing — which is CPython's own rule, and the reason `class C:` with an
/// `@abstractmethod` still instantiates.
/// </remarks>
internal static class PythonAbc
{
    private const string MarkerName = "__isabstractmethod__";

    /// <summary>
    /// The token `get_cache_token` reports: it changes whenever the abstraction of a class
    /// changes, which is what a dispatching cache invalidates on.
    /// </summary>
    private static int _cacheToken;

    /// <summary>`abc.ABC`, built when the module is imported.</summary>
    private static PythonManagedTypeValue? _abcClass;

    /// <summary>`abc.ABCMeta`, the same object `collections.abc` publishes.</summary>
    internal static PythonManagedTypeValue Meta => PythonCollectionsAbc.AbcMeta;

    /// <summary>The module's names, as `abc` publishes them.</summary>
    internal static void Initialize(PythonGlobalNamespace globals)
    {
        globals.SetValue("ABCMeta", Meta);
        globals.SetValue("ABC", AbcClass());
        globals.SetValue(
            "abstractmethod",
            Function("abstractmethod", AbstractMethod, AbstractMethodDoc)
        );
        // The three deprecated forms answer the marker the same way; CPython writes them as a
        // property subclass and two classmethod wrappers, and the runtime answers them as the
        // callables that produce the same marked members.
        globals.SetValue(
            "abstractproperty",
            Function(
                "abstractproperty",
                (arguments, span) => AbstractProperty(arguments),
                AbstractPropertyDoc
            )
        );
        globals.SetValue(
            "abstractclassmethod",
            Function(
                "abstractclassmethod",
                (arguments, span) =>
                    new PythonClassMethodValue(
                        Marked(RequireSingle("abstractclassmethod", arguments))
                    ),
                AbstractClassMethodDoc
            )
        );
        globals.SetValue(
            "abstractstaticmethod",
            Function(
                "abstractstaticmethod",
                (arguments, span) =>
                    new PythonStaticMethodValue(
                        Marked(RequireSingle("abstractstaticmethod", arguments))
                    ),
                AbstractStaticMethodDoc
            )
        );
        globals.SetValue(
            "get_cache_token",
            new PythonBuiltinFunctionValue(
                "get_cache_token",
                (arguments, span) =>
                {
                    if (arguments.Count != 0)
                        throw ManagedObjectProtocols.Fault(
                            "DPY4003",
                            $"get_cache_token() takes no arguments ({arguments.Count} given)",
                            span,
                            "TypeError"
                        );
                    return PythonWholeNumberValue.Create(_cacheToken);
                }
            )
        );
        globals.SetValue(
            "update_abstractmethods",
            Function("update_abstractmethods", UpdateAbstractMethods, UpdateDoc)
        );
    }

    /// <summary>
    /// `class ABC: pass` with `ABCMeta` as its metaclass — a class that declares nothing, so a
    /// subclass of it answers `__abstractmethods__` from its own body.
    /// </summary>
    private static PythonManagedTypeValue AbcClass()
    {
        if (_abcClass is { } built)
            return built;
        var members = new PythonDictionaryValue([]);
        void Member(string name, PythonValue value) =>
            ManagedObjectProtocols.SetItem(members, new PythonTextValue(name), value);
        Member("__module__", new PythonTextValue("abc"));
        Member(
            "__doc__",
            new PythonTextValue(
                "Helper class that provides a standard way to create an ABC using\n"
                    + "inheritance.\n"
            )
        );
        Member("__slots__", new PythonTupleValue([]));
        _abcClass = (PythonManagedTypeValue)
            UserObjectProtocols.Dispatcher!.CallType(
                Meta,
                [
                    new PythonTextValue("ABC"),
                    new PythonTupleValue([PythonBuiltinFunctions.Object]),
                    members,
                ],
                [],
                [],
                default
            );
        return _abcClass;
    }

    /// <summary>`abc.abstractmethod(funcobj)`: the same object, marked abstract.</summary>
    private static PythonValue AbstractMethod(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    ) => Marked(RequireSingle("abstractmethod", arguments));

    /// <summary>`abc.abstractproperty`: the deprecated form, a property whose getter carries
    /// the marker.</summary>
    private static PythonPropertyValue AbstractProperty(IReadOnlyList<PythonValue> arguments)
    {
        if (arguments.Count is 0 or > 2 || arguments[0] is not { } getter)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "abstractproperty() requires a getter",
                default,
                "TypeError"
            );
        Marked(getter);
        return new PythonPropertyValue(getter, arguments.Count > 1 ? arguments[1] : null, null);
    }

    /// <summary>`update_abstractmethods(cls)`: recompute the set the class carries.</summary>
    private static PythonValue UpdateAbstractMethods(
        IReadOnlyList<PythonValue> arguments,
        TextSpan span
    )
    {
        if (arguments.Count != 1 || arguments[0] is not PythonManagedTypeValue type)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "update_abstractmethods() requires a class",
                span,
                "TypeError"
            );
        Refresh(type);
        return type;
    }

    /// <summary>A function written in Python, which is how the module's own names report.</summary>
    private static PythonProtocolFunctionValue Function(
        string name,
        Func<IReadOnlyList<PythonValue>, TextSpan, PythonValue> body,
        string doc
    ) =>
        new(name, (_, arguments) => body(arguments, default))
        {
            IsPythonMethod = true,
            Module = "abc",
            Doc = doc,
        };

    private const string AbstractMethodDoc =
        "A decorator indicating abstract methods.\n\n"
        + "    Requires that the metaclass is ABCMeta or derived from it.  A\n"
        + "    class that has a metaclass derived from ABCMeta cannot be\n"
        + "    instantiated unless all of its abstract methods are overridden.\n"
        + "    The abstract methods can be called using any of the normal\n"
        + "    'super' call mechanisms.  abstractmethod() may be used to declare\n"
        + "    abstract methods for properties and descriptors.\n\n"
        + "    Usage:\n\n"
        + "        class C(metaclass=ABCMeta):\n"
        + "            @abstractmethod\n"
        + "            def my_abstract_method(self, arg1, arg2, argN):\n"
        + "                ...\n"
        + "    ";

    private const string AbstractClassMethodDoc =
        "A decorator indicating abstract classmethods.\n\n"
        + "    Deprecated, use 'classmethod' with 'abstractmethod' instead.\n"
        + "    ";

    private const string AbstractStaticMethodDoc =
        "A decorator indicating abstract staticmethods.\n\n"
        + "    Deprecated, use 'staticmethod' with 'abstractmethod' instead.\n"
        + "    ";

    private const string AbstractPropertyDoc =
        "A decorator indicating abstract properties.\n\n"
        + "    Deprecated, use 'property' with 'abstractmethod' instead.\n"
        + "    ";

    private const string UpdateDoc =
        "Recalculate the set of abstract methods of an abstract class.";

    private static PythonValue RequireSingle(string name, IReadOnlyList<PythonValue> arguments)
    {
        if (arguments.Count != 1)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"{name}() takes exactly one argument ({arguments.Count} given)",
                default,
                "TypeError"
            );
        return arguments[0];
    }

    /// <summary>
    /// Whether a value a class body declares is abstract. A property and a class method answer
    /// with the function they wrap, which is the rule CPython's own descriptor types follow; a
    /// plain function answers for itself.
    /// </summary>
    internal static bool IsAbstract(PythonValue value) =>
        value switch
        {
            PythonFunctionValue function => Marker(name =>
                function.Attributes.TryGetValue(name, out var marker) ? marker : null
            ),
            PythonProtocolFunctionValue protocol => Marker(name =>
                protocol.Attributes.TryGetValue(name, out var marker) ? marker : null
            ),
            PythonPropertyValue property => property.Getter is { } getter && IsAbstract(getter),
            PythonClassMethodValue classMethod => IsAbstract(classMethod.Function),
            PythonStaticMethodValue staticMethod => IsAbstract(staticMethod.Function),
            _ => false,
        };

    private static bool Marker(Func<string, PythonValue?> read) =>
        read(MarkerName) is { } marker && ManagedObjectProtocols.IsTrue(marker);

    /// <summary>
    /// Mark a value abstract, which is what every form of the decorator does: the marker is
    /// written onto the getter or the function itself.
    /// </summary>
    internal static PythonValue Marked(PythonValue value)
    {
        switch (value)
        {
            case PythonFunctionValue function:
                function.Attributes[MarkerName] = PythonTruthValue.True;
                return value;
            case PythonProtocolFunctionValue protocol:
                protocol.Attributes[MarkerName] = PythonTruthValue.True;
                return value;
            case PythonPropertyValue property when property.Getter is { } getter:
                Marked(getter);
                return value;
            default:
                ManagedObjectProtocols.SetAttribute(value, MarkerName, PythonTruthValue.True);
                return value;
        }
    }

    /// <summary>
    /// The abstract names a class reports. A class not built on `ABCMeta` reports none and
    /// carries no set, as CPython leaves it.
    /// </summary>
    internal static void Refresh(PythonManagedTypeValue type)
    {
        if (!IsAbstractMetaClass(type))
            return;
        var names = new List<string>();
        foreach (var name in type.Attributes.Select(attribute => attribute.Key).ToList())
        {
            if (type.Attributes.TryGetValue(name, out var member) && IsAbstract(member))
                names.Add(name);
        }
        foreach (var entry in type.Mro.Skip(1))
        {
            if (
                entry.Attributes.TryGetValue("__abstractmethods__", out var inherited)
                && inherited is PythonSetValue reported
            )
            {
                foreach (var name in reported.Elements)
                {
                    if (
                        name is PythonTextValue text
                        && !names.Contains(text.Value)
                        && IsStillAbstract(type, text.Value)
                    )
                        names.Add(text.Value);
                }
            }
        }
        names.Sort(StringComparer.Ordinal);
        type.Attributes["__abstractmethods__"] = new PythonSetValue(
            names.Select(name => (PythonValue)new PythonTextValue(name))
        )
        {
            IsFrozen = true,
        };
        _cacheToken++;
    }

    /// <summary>
    /// Whether a name a base reports as abstract is still answered by something abstract,
    /// which is what keeps `class C(Sequence)` abstract until `__getitem__` and `__len__` are
    /// written and stops keeping it abstract once they are.
    /// </summary>
    internal static bool IsStillAbstract(PythonManagedTypeValue type, string name) =>
        PythonCollectionsAbc.ResolvesToAbstractStub(type, name) || ResolvesToMarked(type, name);

    private static bool ResolvesToMarked(PythonManagedTypeValue type, string name)
    {
        foreach (var entry in type.Mro)
        {
            if (entry.Attributes.TryGetValue(name, out var value))
                return IsAbstract(value);
        }
        return false;
    }

    /// <summary>Whether a class is built on `ABCMeta`, which is what gives it the set.</summary>
    private static bool IsAbstractMetaClass(PythonManagedTypeValue type) =>
        type.Metaclass is PythonManagedTypeValue metaclass
        && metaclass.Mro.Any(entry => ReferenceEquals(entry, Meta));
}
