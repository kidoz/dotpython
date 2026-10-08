using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// The attribute names instances of a class accept, derived from the `__slots__`
/// declarations along its physical base chain.
/// </summary>
internal sealed class PythonSlotLayout
{
    private PythonSlotLayout(bool allowsInstanceDictionary, HashSet<string> memberNames)
    {
        AllowsInstanceDictionary = allowsInstanceDictionary;
        MemberNames = memberNames;
    }

    /// <summary>
    /// Whether instances accept arbitrary names because the class can hold an
    /// instance dictionary.
    /// </summary>
    internal bool AllowsInstanceDictionary { get; }

    /// <summary>The declared member names, mangled, in this class and its bases.</summary>
    internal IReadOnlySet<string> MemberNames { get; }

    /// <summary>
    /// Builds the layout for a class that declares `__slots__`. A null result means no
    /// `__slots__` was declared, so instances accept any attribute name.
    /// </summary>
    internal static PythonSlotLayout? Create(
        PythonManagedTypeValue type,
        PythonValue declared,
        TextSpan span
    )
    {
        // A bare string declares a single member; CPython wraps it rather than
        // iterating it, so `__slots__ = "xy"` is the member `xy`, not `x` and `y`.
        IEnumerable<PythonValue> items = declared is PythonTextValue single
            ? [single]
            : ManagedObjectProtocols.MaterializeValues(declared, span);

        var members = new HashSet<string>(StringComparer.Ordinal);
        var allowsDictionary = false;
        foreach (var item in items)
        {
            if (item is not PythonTextValue text)
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4029",
                    $"__slots__ items must be strings, not '{ManagedObjectProtocols.GetTypeName(item)}'",
                    span,
                    "TypeError"
                );
            }

            if (!PythonIdentifier.IsIdentifier(text.Value))
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4029",
                    "__slots__ must be identifiers",
                    span,
                    "TypeError"
                );
            }

            var name = Mangle(text.Value, type.Name);
            if (name == "__dict__")
            {
                allowsDictionary = true;
                continue;
            }

            if (type.Attributes.TryGetValue(name, out _))
            {
                throw ManagedObjectProtocols.Fault(
                    "DPY4029",
                    $"'{text.Value}' in __slots__ conflicts with class variable",
                    span,
                    "ValueError"
                );
            }

            members.Add(name);
        }

        // A base that declares no `__slots__` gives its instances a dictionary, and a
        // slotted subclass still inherits it. Builtin bases contribute none, so
        // `class C(object): __slots__ = ()` rejects new attributes.
        if ((type.LayoutBase as PythonManagedTypeValue ?? type.BaseType) is { } parent)
        {
            if (parent.Slots is { } inherited)
            {
                allowsDictionary |= inherited.AllowsInstanceDictionary;
                members.UnionWith(inherited.MemberNames);
            }
            else
            {
                allowsDictionary = true;
            }
        }

        return new PythonSlotLayout(allowsDictionary, members);
    }

    /// <summary>Whether a name may be stored on an instance of the layout's class.</summary>
    internal bool Accepts(string name) => AllowsInstanceDictionary || MemberNames.Contains(name);

    /// <summary>
    /// Applies the private-name mangling CPython uses for slot members, so
    /// `__slots__ = ('__x',)` in class `C` declares the member `_C__x`.
    /// </summary>
    private static string Mangle(string name, string className)
    {
        if (
            !name.StartsWith("__", StringComparison.Ordinal)
            || name.EndsWith("__", StringComparison.Ordinal)
        )
        {
            return name;
        }

        var trimmed = className.TrimStart('_');
        return trimmed.Length == 0 ? name : $"_{trimmed}{name}";
    }
}
