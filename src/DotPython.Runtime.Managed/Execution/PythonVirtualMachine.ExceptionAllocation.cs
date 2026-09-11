using DotPython.Language.Text;

namespace DotPython.Runtime.Managed.Execution;

internal sealed partial class PythonVirtualMachine
{
    PythonExceptionValue IUserObjectDispatcher.AllocateException(
        string allocator,
        PythonValue type,
        PythonValue[] arguments,
        TextSpan span
    ) => AllocateException(allocator, type, arguments, span);

    private PythonExceptionValue AllocateException(
        string allocator,
        PythonValue type,
        PythonValue[] arguments,
        TextSpan span
    )
    {
        var layout = PythonTypeLayout.GetSolidBase(type);
        if (
            layout is not PythonExceptionTypeValue
            || layout is PythonExceptionTypeValue { Name: "BaseExceptionGroup" }
                && allocator != "BaseExceptionGroup"
            || layout is PythonExceptionTypeValue { Name: "OSError" } && allocator != "OSError"
        )
        {
            var name = PythonTypeLayout.Name(type);
            throw Fault(
                "DPY4003",
                $"{allocator}.__new__({name}) is not safe, use {name}.__new__()",
                span,
                "TypeError"
            );
        }
        if (allocator == "BaseExceptionGroup")
            return CreateExceptionGroupValue(arguments, type as PythonManagedTypeValue);

        var typeName = PythonTypeLayout.Name(type);
        var exception = new PythonExceptionValue(
            typeName,
            arguments.Length == 1
            && IsSubclassOf(type, PythonBuiltinTypes.GetExceptionType("KeyError"), span)
                ? arguments[0].ToRepresentationString()
                : ComposeExceptionMessage(arguments)
        )
        {
            Arguments = [.. arguments],
            ManagedType = type as PythonManagedTypeValue,
        };
        // BaseException allocation initializes only args. The additional builtin
        // value/code fields are populated later by their own initializer slots.
        exception.InitializeSpecializedArguments([]);
        return exception;
    }
}
