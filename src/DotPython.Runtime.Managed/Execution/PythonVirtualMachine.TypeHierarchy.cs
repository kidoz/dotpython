using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal sealed partial class PythonVirtualMachine
{
    private sealed record MroUpdate(
        PythonManagedTypeValue Type,
        PythonTupleValue? Previous,
        PythonTupleValue Installed,
        bool WasPending
    );

    void IUserObjectDispatcher.SetTypeBases(
        PythonManagedTypeValue type,
        PythonTupleValue bases,
        TextSpan span
    )
    {
        PythonTypeLayout.ValidateSupportedLayout(type, span);
        var previousBases = PythonBuiltinTypes.GetBases(type);
        var previousBase = type.LayoutBase ?? previousBases.Elements[0];
        var newBase = PythonTypeLayout.BestBase(bases.Elements, span);
        PythonTypeLayout.ValidateAssignment(previousBase, newBase, span);

        type.SetDeclaredBases(bases);
        type.LayoutBase = newBase;
        var updates = new List<MroUpdate>();
        try
        {
            var pending = new Stack<PythonManagedTypeValue>();
            pending.Push(type);
            while (pending.TryPop(out var current))
            {
                if (_deferredControlFlowCount == 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    if (_instructionsExecuted++ >= _instructionLimit)
                        throw Fault("DPY4001", "The managed instruction limit was exceeded.", span);
                }
                var previous = current.MroTuple;
                var wasPending = current.IsMroPending;
                if (!InitializeMethodResolutionOrder(current, span))
                    continue;
                updates.Add(new(current, previous, current.MroTuple!, wasPending));

                // Snapshot after the callback: newly created subclasses are visible.
                // Depth-first traversal deliberately revisits diamonds through each
                // direct parent, matching CPython's callback ordering.
                var children = PythonTypeHierarchy.Snapshot(current);
                for (var index = children.Count - 1; index >= 0; index--)
                    pending.Push(children[index]);
            }
            if (ReferenceEquals(type.BasesTuple, bases))
                PythonTypeHierarchy.ReplaceBases(type, previousBases, bases);
        }
        catch
        {
            // Undo only this transaction's still-current installations. Successful
            // nested assignments and arbitrary callback side effects survive.
            for (var index = updates.Count - 1; index >= 0; index--)
            {
                var update = updates[index];
                if (!ReferenceEquals(update.Type.MroTuple, update.Installed))
                    continue;
                update.Type.SetResolutionOrder(update.Previous);
                update.Type.IsMroPending = update.WasPending;
            }
            if (ReferenceEquals(type.BasesTuple, bases))
            {
                type.SetDeclaredBases(previousBases);
                type.LayoutBase = previousBase;
            }
            throw;
        }
    }
}
