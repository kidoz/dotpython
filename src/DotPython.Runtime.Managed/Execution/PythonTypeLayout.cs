using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>
/// Physical layout rules for the qualified unslotted object, type, and exception
/// families. Physical ancestry is independent of a metaclass's custom MRO.
/// </summary>
internal static class PythonTypeLayout
{
    internal static PythonValue GetSolidBase(PythonValue type)
    {
        if (type is PythonManagedTypeValue managed)
            return GetPhysicalBase(managed) is { } managedParent
                ? GetSolidBase(managedParent)
                : PythonBuiltinFunctions.Object;
        if (type is PythonExceptionTypeValue exception)
        {
            if (
                exception.Name
                is "BaseException"
                    or "BaseExceptionGroup"
                    or "AttributeError"
                    or "ImportError"
                    or "NameError"
                    or "OSError"
                    or "StopIteration"
                    or "SyntaxError"
                    or "SystemExit"
                    or "UnicodeDecodeError"
                    or "UnicodeEncodeError"
                    or "UnicodeTranslateError"
            )
                return exception;
            return GetPhysicalBase(exception) is { } exceptionParent
                ? GetSolidBase(exceptionParent)
                : PythonBuiltinFunctions.Object;
        }
        return type;
    }

    internal static PythonValue BestBase(IReadOnlyList<PythonValue> bases, TextSpan span)
    {
        PythonValue? best = null;
        PythonValue? winner = null;
        foreach (var candidateBase in bases)
        {
            ValidateBaseAdmissibility(candidateBase, span);
            var candidate = GetSolidBase(candidateBase);
            if (winner is null || IsSubtype(candidate, winner))
            {
                // Equal solid roots preserve the first declared base.
                if (winner is null || !IsSubtype(winner, candidate))
                {
                    winner = candidate;
                    best = candidateBase;
                }
            }
            else if (!IsSubtype(winner, candidate))
                throw Error("multiple bases have instance lay-out conflict", span);
        }
        return best ?? PythonBuiltinFunctions.Object;
    }

    internal static void ValidateAssignment(PythonValue oldBase, PythonValue newBase, TextSpan span)
    {
        ValidateBaseAdmissibility(oldBase, span);
        ValidateBaseAdmissibility(newBase, span);
        if (ReferenceEquals(oldBase, newBase))
        {
            ValidateSupportedLayout(newBase, span);
            return;
        }

        // Diagnose physical incompatibility before rejecting an otherwise
        // admissible builtin whose subclass storage is not implemented.
        if (UsesGcFree(oldBase) != UsesGcFree(newBase))
            throw Error(
                $"__bases__ assignment: '{Name(newBase)}' deallocator differs from '{Name(oldBase)}'",
                span
            );

        var oldAnchor = CompatibilityAnchor(oldBase);
        var newAnchor = CompatibilityAnchor(newBase);
        if (ReferenceEquals(oldAnchor, newAnchor))
        {
            ValidateSupportedLayout(oldBase, span);
            ValidateSupportedLayout(newBase, span);
            return;
        }
        if (
            HasManagedHeader(oldAnchor)
            && HasManagedHeader(newAnchor)
            && ReferenceEquals(GetPhysicalBase(oldAnchor), GetPhysicalBase(newAnchor))
            && ReferenceEquals(GetSolidBase(oldAnchor), GetSolidBase(newAnchor))
        )
        {
            ValidateSupportedLayout(oldBase, span);
            ValidateSupportedLayout(newBase, span);
            return;
        }

        throw Error(
            $"__bases__ assignment: '{Name(newBase)}' object layout differs from '{Name(oldBase)}'",
            span
        );
    }

    internal static void ValidateSupportedLayout(PythonValue type, TextSpan span)
    {
        var seen = new HashSet<PythonValue>(ReferenceEqualityComparer.Instance);
        for (PythonValue? current = type; current is not null; current = GetPhysicalBase(current))
        {
            if (!seen.Add(current))
                throw Error("a __bases__ item causes an inheritance cycle", span);
            ValidateBaseAdmissibility(current, span);
            if (current is PythonManagedTypeValue managed)
            {
                if (managed.HasDeclaredSlots)
                    throw Error(
                        "Changing bases of classes with __slots__ is not supported in this runtime slice.",
                        span
                    );
                continue;
            }
            if (
                current is PythonExceptionTypeValue
                || ReferenceEquals(current, PythonBuiltinTypes.Type)
                || IsObject(current)
            )
                continue;
            if (current is PythonBuiltinTypeValue builtin)
            {
                throw Error(
                    $"Subclassing the builtin type '{builtin.Name}' is not supported in this runtime slice.",
                    span
                );
            }
            throw Error("bases must be types", span);
        }
    }

    private static void ValidateBaseAdmissibility(PythonValue type, TextSpan span)
    {
        if (!PythonTypeProtocols.IsType(type))
            throw Error("bases must be types", span);
        if (
            type is PythonBuiltinTypeValue builtin
            && builtin.Name
                is "bool"
                    or "range"
                    or "slice"
                    or "memoryview"
                    or "mappingproxy"
                    or "NoneType"
                    or "NotImplementedType"
                    or "ellipsis"
                    or "function"
                    or "builtin_function_or_method"
                    or "method"
                    or "generator"
                    or "coroutine"
                    or "async_generator"
                    or "code"
                    or "frame"
                    or "traceback"
        )
            throw Error($"type '{builtin.Name}' is not an acceptable base type", span);
    }

    private static bool UsesGcFree(PythonValue type) =>
        !IsObject(type)
        && type
            is not PythonBuiltinTypeValue
            {
                Name: "int" or "float" or "complex" or "str" or "bytes" or "bytearray"
            };

    private static PythonValue CompatibilityAnchor(PythonValue type)
    {
        while (GetPhysicalBase(type) is { } parent)
        {
            if (
                !ReferenceEquals(GetSolidBase(type), GetSolidBase(parent))
                || HasManagedHeader(type) != HasManagedHeader(parent)
            )
                break;
            type = parent;
        }
        return type;
    }

    private static bool HasManagedHeader(PythonValue type) =>
        type is PythonExceptionTypeValue { Name: "ExceptionGroup" }
        || (
            type is PythonManagedTypeValue
            && !IsObject(type)
            && !ReferenceEquals(GetSolidBase(type), PythonBuiltinTypes.Type)
        );

    private static PythonValue? GetPhysicalBase(PythonValue type) =>
        type switch
        {
            PythonManagedTypeValue managed when IsObject(managed) => null,
            PythonManagedTypeValue { LayoutBase: { } parent } => parent,
            PythonManagedTypeValue { Bases.Count: > 0 } managed => managed.Bases[0],
            PythonManagedTypeValue { IsMetaclass: true } => PythonBuiltinTypes.Type,
            PythonManagedTypeValue { ExceptionBaseName: { } name } =>
                PythonBuiltinTypes.GetExceptionType(name),
            PythonManagedTypeValue => PythonBuiltinFunctions.Object,
            PythonExceptionTypeValue exception
                when PythonVirtualMachine.GetBuiltinExceptionBase(exception.Name) is { } name =>
                PythonBuiltinTypes.GetExceptionType(name),
            PythonExceptionTypeValue => PythonBuiltinFunctions.Object,
            PythonBuiltinTypeValue builtin when IsObject(builtin) => null,
            PythonBuiltinTypeValue => PythonBuiltinFunctions.Object,
            _ => null,
        };

    private static bool IsObject(PythonValue value) =>
        ReferenceEquals(value, PythonBuiltinFunctions.Object)
        || ReferenceEquals(value, PythonBuiltinFunctions.ObjectType);

    private static bool IsSubtype(PythonValue value, PythonValue expected) =>
        ReferenceEquals(value, expected)
        || PythonBuiltinTypes.GetMro(value).Elements.Any(entry => ReferenceEquals(entry, expected));

    internal static string Name(PythonValue type) =>
        type switch
        {
            PythonManagedTypeValue managed => managed.Name,
            PythonBuiltinTypeValue builtin => builtin.Name,
            PythonExceptionTypeValue exception => exception.Name,
            _ => ManagedObjectProtocols.GetTypeName(type),
        };

    private static PythonRuntimeException Error(string message, TextSpan span) =>
        ManagedObjectProtocols.Fault("DPY4034", message, span, "TypeError");
}
