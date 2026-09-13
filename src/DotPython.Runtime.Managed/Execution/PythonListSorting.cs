using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Owns list.sort argument conversion, detached storage and failure recovery.</summary>
internal static class PythonListSorting
{
    internal static readonly PythonProtocolFunctionValue Method = new(
        "sort",
        (target, positional) => Invoke((PythonListValue)target!, positional, [], [], default),
        (target, positional, names, values) =>
            Invoke((PythonListValue)target!, positional, names, values, default)
    );

    internal static PythonValue Invoke(
        PythonListValue list,
        IReadOnlyList<PythonValue> positional,
        IReadOnlyList<string> keywordNames,
        IReadOnlyList<PythonValue> keywordValues,
        TextSpan span
    )
    {
        if (positional.Count != 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                "sort() takes no positional arguments",
                span,
                "TypeError"
            );
        var slots = PythonKeywordArguments.Bind(
            "sort",
            ["key", "reverse"],
            0,
            positional,
            keywordNames,
            keywordValues,
            span
        );
        Sort(list, slots[0] ?? PythonNoneValue.Instance, slots[1] ?? PythonTruthValue.False, span);
        return PythonNoneValue.Instance;
    }

    internal static void Sort(
        PythonListValue list,
        PythonValue key,
        PythonValue reverseValue,
        TextSpan span
    )
    {
        // Argument conversion runs while the original receiver is still visible.
        var reverse = ManagedObjectProtocols.IsTrue(reverseValue);
        var values = list.Elements.ToArray();
        var capacity = list.Elements.Capacity;
        var parentActive = list.SortActive;
        var parentAllocation = list.SortAllocationObserved;
        list.Elements.Clear();
        list.Elements.Capacity = 0;
        list.SortActive = true;
        list.SortAllocationObserved = false;
        var reversed = false;
        try
        {
            var keys = values;
            if (key is not PythonNoneValue)
            {
                keys = new PythonValue[values.Length];
                for (var index = 0; index < values.Length; index++)
                {
                    UserObjectProtocols.Dispatcher?.CheckIterationWork(span);
                    keys[index] = UserObjectProtocols.Dispatcher is { } dispatcher
                        ? dispatcher.Invoke(key, [values[index]], span)
                        : ManagedObjectProtocols.Call(key, [values[index]], span);
                }
            }

            if (reverse)
            {
                Array.Reverse(values);
                if (!ReferenceEquals(keys, values))
                    Array.Reverse(keys);
                reversed = true;
            }
            PythonStableSort.Sort(values, keys, span);
            if (list.SortAllocationObserved || list.Elements.Capacity != 0)
                throw ManagedObjectProtocols.Fault(
                    "DPY4003",
                    "list modified during sort",
                    span,
                    "ValueError"
                );
        }
        finally
        {
            if (reversed)
                Array.Reverse(values);
            // Keep the receiver and its backing List identity for saved aliases/cursors.
            // Callback exceptions take precedence over mutation errors, but both paths
            // discard callback-created items and restore the working permutation.
            list.Elements.Clear();
            list.Elements.Capacity = capacity;
            list.Elements.AddRange(values);
            list.SortActive = parentActive;
            list.SortAllocationObserved = parentAllocation;
        }
    }
}
