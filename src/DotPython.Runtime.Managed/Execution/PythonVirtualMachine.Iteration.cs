using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal sealed partial class PythonVirtualMachine
{
    PythonIteratorValue IUserObjectDispatcher.GetUserIterator(
        PythonManagedObjectValue instance,
        TextSpan span
    ) => ResolveUserIterator(instance, span);

    private PythonValue GetUserIteratorResult(PythonManagedObjectValue instance, TextSpan span)
    {
        PythonValue? method = null;
        var found = false;
        try
        {
            found = ManagedObjectProtocols.TryGetSpecialMethod(instance, "__iter__", out method);
        }
        catch (Exception error)
            when (PythonNamespaceMapping.IsPythonException(error, "AttributeError")) { }
        if (found)
        {
            if (method is not PythonNoneValue)
                return InvokeCallableNested(method!, [], span);
        }
        else if (UserObjectProtocols.DefinesSpecialMethod(instance, "__getitem__"))
            return new PythonIteratorValue(
                new PythonSequenceIteratorSourceValue { Sequence = instance },
                -1
            );
        throw Fault("DPY4015", $"'{instance.Type.Name}' object is not iterable", span, "TypeError");
    }

    (bool HasValue, PythonValue Value) IUserObjectDispatcher.StepSequenceIterator(
        PythonSequenceIteratorSourceValue source,
        TextSpan span
    )
    {
        if (source.Sequence is not { } sequence)
            return (false, PythonNoneValue.Instance);
        CheckProtocolWork(span);
        if (source.NextIndex == long.MaxValue)
            throw Fault("DPY4003", "iter index too large", span, "OverflowError");
        try
        {
            var value = ManagedObjectProtocols.GetItem(
                sequence,
                PythonWholeNumberValue.Create(source.NextIndex),
                span
            );
            source.NextIndex++;
            return (true, value);
        }
        catch (Exception error)
            when (PythonNamespaceMapping.IsPythonException(error, "IndexError")
                || PythonNamespaceMapping.IsPythonException(error, "StopIteration")
            )
        {
            source.Sequence = null;
            return (false, PythonNoneValue.Instance);
        }
    }

    void IUserObjectDispatcher.CheckIterationWork(TextSpan span) => CheckProtocolWork(span);

    private void CheckProtocolWork(TextSpan span)
    {
        if (_deferredControlFlowCount == 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_instructionsExecuted++ >= _instructionLimit)
                throw Fault("DPY4001", "The managed instruction limit was exceeded.", span);
        }
        else if (_deferredCleanupInstructions++ >= MaximumDeferredCleanupInstructions)
            throw Fault(
                "DPY4032",
                $"Deferred cleanup exceeded the {MaximumDeferredCleanupInstructions} instruction limit.",
                span
            );
    }
}
