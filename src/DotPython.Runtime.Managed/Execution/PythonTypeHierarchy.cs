using System.Runtime.CompilerServices;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// Weak direct-subclass links. Managed parents own their intrinsic links; links
/// from process-shared builtins belong to the executing engine's module registry.
/// This object deliberately holds no engine, VM, module, or output references.
/// </summary>
internal sealed class PythonTypeHierarchy
{
    private sealed class Children
    {
        internal List<WeakReference<PythonValue>> Entries { get; } = [];
    }

    private static readonly ConditionalWeakTable<
        PythonManagedTypeValue,
        Children
    > ManagedSubclasses = new();
    private readonly ConditionalWeakTable<PythonValue, Children> _builtinSubclasses = new();
    private bool _initialized;

    internal void Initialize(IReadOnlyDictionary<string, PythonValue> builtins)
    {
        if (_initialized)
            return;
        foreach (var type in PythonBuiltinSubclassInventory.GetStartupTypes(builtins))
        {
            if (type is PythonManagedTypeValue managed)
                managed.OwnerHierarchy ??= this;
            RegisterValue(type, PythonBuiltinTypes.GetBases(type));
        }
        // Template literal types are initialized by CPython before any module import.
        RegisterValue(
            PythonStandardModules.InterpolationType,
            PythonBuiltinTypes.GetBases(PythonStandardModules.InterpolationType)
        );
        RegisterValue(
            PythonStandardModules.TemplateType,
            PythonBuiltinTypes.GetBases(PythonStandardModules.TemplateType)
        );
        _initialized = true;
    }

    internal void RegisterModuleTypes(PythonGlobalNamespace globals)
    {
        foreach (var (_, value) in globals.Entries)
        {
            var type = value is PythonManagedObjectValue instance ? instance.Type : value;
            if (type is PythonManagedTypeValue managed)
            {
                managed.OwnerHierarchy ??= this;
                Register(managed, PythonBuiltinTypes.GetBases(managed));
            }
            else if (type is PythonBuiltinTypeValue or PythonExceptionTypeValue)
                RegisterValue(type, PythonBuiltinTypes.GetBases(type));
        }
    }

    internal static void Register(PythonManagedTypeValue type, PythonTupleValue bases) =>
        type.OwnerHierarchy!.RegisterValue(type, bases);

    private void RegisterValue(PythonValue type, PythonTupleValue bases)
    {
        foreach (var parent in bases.Elements)
        {
            var children = GetChildren(parent, create: true)!;
            lock (children)
            {
                children.Entries.RemoveAll(entry => !entry.TryGetTarget(out _));
                if (
                    !children.Entries.Any(entry =>
                        entry.TryGetTarget(out var target) && ReferenceEquals(target, type)
                    )
                )
                    children.Entries.Add(new(type));
            }
        }
    }

    internal static void ReplaceBases(
        PythonManagedTypeValue type,
        PythonTupleValue oldBases,
        PythonTupleValue newBases
    )
    {
        var hierarchy = type.OwnerHierarchy!;
        foreach (var parent in oldBases.Elements)
        {
            var children = hierarchy.GetChildren(parent, create: false);
            if (children is null)
                continue;
            lock (children)
                children.Entries.RemoveAll(entry =>
                    !entry.TryGetTarget(out var target) || ReferenceEquals(target, type)
                );
        }
        Register(type, newBases);
    }

    internal static List<PythonManagedTypeValue> Snapshot(PythonManagedTypeValue type) =>
        SnapshotChildren(ManagedSubclasses.TryGetValue(type, out var children) ? children : null)
            .OfType<PythonManagedTypeValue>()
            .ToList();

    internal PythonListValue GetSubclasses(PythonValue type) =>
        new(SnapshotChildren(GetChildren(type, create: false)));

    private Children? GetChildren(PythonValue type, bool create)
    {
        // The internal instance-layout marker and Python's object are one parent.
        if (ReferenceEquals(type, PythonBuiltinFunctions.ObjectType))
            type = PythonBuiltinFunctions.Object;
        if (type is PythonManagedTypeValue managed)
            return create ? ManagedSubclasses.GetValue(managed, static _ => new())
                : ManagedSubclasses.TryGetValue(managed, out var children) ? children
                : null;
        return create ? _builtinSubclasses.GetValue(type, static _ => new())
            : _builtinSubclasses.TryGetValue(type, out var builtinChildren) ? builtinChildren
            : null;
    }

    private static List<PythonValue> SnapshotChildren(Children? children)
    {
        var result = new List<PythonValue>();
        if (children is null)
            return result;
        lock (children)
        {
            children.Entries.RemoveAll(entry => !entry.TryGetTarget(out _));
            foreach (var entry in children.Entries)
                if (entry.TryGetTarget(out var child))
                    result.Add(child);
        }
        return result;
    }
}
