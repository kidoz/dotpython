namespace DotPython.Runtime.Managed.Execution;

internal sealed partial class PythonVirtualMachine
{
    // Host diagnostics are rendered after dispatch has unwound. Keep formatting
    // callbacks attached to the original execution's streams and work limits.
    internal string FormatExceptionMessage(PythonExceptionValue exception)
    {
        var previousDispatcher = UserObjectProtocols.Dispatcher;
        UserObjectProtocols.Dispatcher = this;
        try
        {
            return exception.ToDisplayString();
        }
        finally
        {
            UserObjectProtocols.Dispatcher = previousDispatcher;
        }
    }
}
