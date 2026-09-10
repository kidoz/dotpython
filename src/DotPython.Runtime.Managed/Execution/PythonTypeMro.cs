using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>The default type.mro implementation; it never invokes a metaclass override.</summary>
internal static class PythonTypeMro
{
    internal static PythonListValue Compute(PythonValue type, TextSpan span) =>
        new([type, .. LinearizeBases(PythonBuiltinTypes.GetBases(type).Elements, span)]);

    /// <summary>Merge installed base MROs, excluding the new class itself.</summary>
    internal static List<PythonValue> LinearizeBases(
        IReadOnlyList<PythonValue> bases,
        TextSpan span
    )
    {
        foreach (var baseType in bases)
            if (baseType is PythonManagedTypeValue { IsMroPending: true })
                throw ManagedObjectProtocols.Fault(
                    "DPY4034",
                    $"Cannot extend an incomplete type '{TypeName(baseType)}'",
                    span,
                    "TypeError"
                );

        // CPython's single-base path copies the installed MRO verbatim, including
        // duplicates or omitted ancestors deliberately returned by a custom hook.
        if (bases.Count == 1)
            return [.. PythonBuiltinTypes.GetMro(bases[0]).Elements];

        for (var index = 0; index < bases.Count; index++)
        for (var previous = 0; previous < index; previous++)
            if (ReferenceEquals(bases[index], bases[previous]))
                throw ManagedObjectProtocols.Fault(
                    "DPY4034",
                    $"duplicate base class {TypeName(bases[index])}",
                    span,
                    "TypeError"
                );

        var sequences = bases
            .Select(baseType => new List<PythonValue>(PythonBuiltinTypes.GetMro(baseType).Elements))
            .ToList();
        sequences.Add([.. bases]);
        var result = new List<PythonValue>();
        while (sequences.Any(sequence => sequence.Count != 0))
        {
            PythonValue? selected = null;
            foreach (var sequence in sequences)
            {
                if (sequence.Count == 0)
                    continue;
                var head = sequence[0];
                if (
                    !sequences.Any(other =>
                        other.Skip(1).Any(entry => ReferenceEquals(entry, head))
                    )
                )
                {
                    selected = head;
                    break;
                }
            }
            if (selected is null)
                throw ManagedObjectProtocols.Fault(
                    "DPY4034",
                    "Cannot create a consistent method resolution order (MRO) for bases "
                        + string.Join(
                            ", ",
                            sequences
                                .Where(sequence => sequence.Count != 0)
                                .Select(sequence => sequence[0])
                                .Distinct<PythonValue>(ReferenceEqualityComparer.Instance)
                                .Select(TypeName)
                        ),
                    span,
                    "TypeError"
                );
            result.Add(selected);
            foreach (var sequence in sequences)
                if (sequence.Count != 0 && ReferenceEquals(sequence[0], selected))
                    sequence.RemoveAt(0);
        }
        return result;
    }

    private static string TypeName(PythonValue type) =>
        type switch
        {
            PythonManagedTypeValue managed => managed.Name,
            PythonBuiltinTypeValue builtin => builtin.Name,
            PythonExceptionTypeValue exception => exception.Name,
            _ => ManagedObjectProtocols.GetTypeName(type),
        };
}
