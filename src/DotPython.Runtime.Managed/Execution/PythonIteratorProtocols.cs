using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

/// <summary>Shared explicit methods for represented iterator cursors.</summary>
internal static class PythonIteratorProtocols
{
    internal static readonly PythonProtocolFunctionValue IterMethod = CreateMethod("__iter__");
    internal static readonly PythonProtocolFunctionValue NextMethod = CreateMethod("__next__");

    private static PythonProtocolFunctionValue CreateMethod(string name) =>
        new(
            name,
            (receiver, arguments) => Invoke((PythonIteratorValue)receiver!, name, arguments),
            (receiver, arguments, keywordNames, _) =>
                keywordNames.Count == 0
                    ? Invoke((PythonIteratorValue)receiver!, name, arguments)
                    : throw ManagedObjectProtocols.Fault(
                        "DPY4003",
                        $"wrapper {name}() takes no keyword arguments",
                        default,
                        "TypeError"
                    )
        );

    internal static PythonValue Invoke(
        PythonIteratorValue iterator,
        string name,
        IReadOnlyList<PythonValue> arguments,
        TextSpan span = default
    )
    {
        if (arguments.Count != 0)
            throw ManagedObjectProtocols.Fault(
                "DPY4003",
                $"expected 0 arguments, got {arguments.Count}",
                span,
                "TypeError"
            );
        if (name == "__iter__")
            return iterator;
        if (ManagedObjectProtocols.TryGetNext(iterator, out var value, span))
            return value;
        throw new PythonRaisedException(
            iterator.StopIteration
                ?? ManagedObjectProtocols.CreateStopIteration(PythonNoneValue.Instance)
        );
    }
}
