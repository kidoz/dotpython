// The `dir()` builtin, `object.__dir__` and `object.__sizeof__` follow CPython 3.14.7
// Objects/object.c and Python/bltinmodule.c:
// https://github.com/python/cpython/tree/v3.14.7
// Copyright (c) 2001-2026 Python Software Foundation. All rights reserved.
// Used under the Python Software Foundation License Version 2:
// https://docs.python.org/3/license.html#psf-license-agreement-for-python-release

using System.Numerics;
using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// What a value knows about itself: the names it answers to, and — for the values whose size
/// follows from their contents — how many bytes it reports.
/// </summary>
/// <remarks>
/// The name list is what this runtime actually answers, so it is shorter than CPython's
/// wherever a member is documented as absent. A size is reported only where CPython's own
/// number follows from the contents: a container's depends on its allocation capacity and a
/// string's on the kind it was built as, neither of which is modelled here.
/// </remarks>
internal static class PythonIntrospection
{
    /// <summary>
    /// The names a value answers to, in the order `__dir__` reports them. `dir()` sorts what
    /// this returns; `__dir__` does not.
    /// </summary>
    internal static PythonListValue Names(PythonValue value)
    {
        var names = new List<string>();
        switch (value)
        {
            case PythonModuleValue module:
                foreach (var (name, _) in module.Globals.Entries)
                    AddName(names, name);
                break;
            case PythonManagedObjectValue instance:
                // The class namespace along the MRO, then whatever the instance carries.
                foreach (var type in instance.Type.Mro)
                foreach (var attribute in type.Attributes)
                    AddName(names, attribute.Key);
                foreach (var attribute in instance.Attributes)
                    AddName(names, attribute.Key);
                AddObjectNames(names);
                break;
            case PythonManagedTypeValue type:
                foreach (var entry in type.Mro)
                foreach (var attribute in entry.Attributes)
                    AddName(names, attribute.Key);
                AddObjectNames(names);
                break;
            default:
                AddBuiltinNames(value, names);
                break;
        }
        return new PythonListValue([.. names.Select(name => new PythonTextValue(name))]);
    }

    /// <summary>
    /// The names a builtin value answers: its method table, its slots, the members it computes
    /// and the ones it inherits from `object`.
    /// </summary>
    private static void AddBuiltinNames(PythonValue value, List<string> names)
    {
        var typeName = PythonBuiltinTypes.GetRuntimeTypeName(value);
        PythonBuiltinMethods.AddMethodNames(typeName, names);
        PythonSlotMethods.AddSlotNames(typeName, names);
        PythonIntMethods.AddMemberNames(value, names);
        PythonFloatMethods.AddMemberNames(value, names);
        AddObjectNames(names);
    }

    private static void AddObjectNames(List<string> names)
    {
        foreach (var name in ObjectMemberNames)
            AddName(names, name);
    }

    /// <summary>The same names, sorted, which is what `dir()` reports.</summary>
    internal static PythonListValue Sorted(PythonValue value)
    {
        var names = Names(value);
        var ordered = names.Elements.Select(element => element.ToDisplayString()).ToList();
        ordered.Sort(StringComparer.Ordinal);
        return new PythonListValue([.. ordered.Select(name => new PythonTextValue(name))]);
    }

    /// <summary>The text keys of a namespace, sorted: the names in scope.</summary>
    internal static List<string> NamesOf(PythonDictionaryValue scope)
    {
        var names = new List<string>();
        foreach (var item in scope.Items)
        {
            if (item.Key is PythonTextValue key)
                AddName(names, key.Value);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static void AddName(List<string> names, string name)
    {
        if (!names.Contains(name))
            names.Add(name);
    }

    /// <summary>
    /// `object.__sizeof__` for the values whose size follows from their contents: an integer's
    /// digits, and the fixed sizes of a float, a bool and `None`.
    /// </summary>
    internal static bool TryGetSize(PythonValue value, out BigInteger size)
    {
        switch (value)
        {
            // A long is a fixed header plus four bytes per 30-bit digit; zero still has one.
            case PythonWholeNumberValue whole:
                var bits = BigInteger.Abs(whole.Value).GetBitLength();
                size = 40 + 4 * BigInteger.Max(1, (bits + 29) / 30);
                return true;
            case PythonTruthValue:
                size = 44;
                return true;
            case PythonFloatingPointValue:
                size = 40;
                return true;
            case PythonNoneValue:
                size = 32;
                return true;
            default:
                size = default;
                return false;
        }
    }

    /// <summary>Whether a type publishes `__sizeof__` at all, given the sizes modelled.</summary>
    internal static bool HasSize(string typeName) =>
        typeName is "int" or "bool" or "float" or "NoneType";

    private static readonly string[] ObjectMemberNames =
    [
        "__class__",
        "__dir__",
        "__doc__",
        "__format__",
        "__getattribute__",
        "__getstate__",
        "__init__",
        "__str__",
    ];
}
