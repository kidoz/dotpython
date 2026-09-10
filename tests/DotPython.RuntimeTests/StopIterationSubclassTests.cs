using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class StopIterationSubclassTests
{
    [Fact]
    public void Value_IsInheritedThroughManagedExceptionAncestry()
    {
        var stopType = new PythonManagedTypeValue("End", exceptionBaseName: "StopIteration");
        var childType = new PythonManagedTypeValue("Child", stopType, exceptionBaseName: "End");
        var value = new PythonListValue([]);
        var exception = new PythonExceptionValue("Child", "value")
        {
            ManagedType = childType,
            Arguments = [value, new PythonTextValue("ignored")],
        };

        Assert.Same(value, ManagedObjectProtocols.GetAttribute(exception, "value"));
        var empty = new PythonExceptionValue("Child", string.Empty) { ManagedType = childType };
        Assert.Same(PythonNoneValue.Instance, ManagedObjectProtocols.GetAttribute(empty, "value"));
        var unrelated = new PythonExceptionValue("StopIteration", "value")
        {
            ManagedType = new PythonManagedTypeValue(
                "StopIteration",
                exceptionBaseName: "ValueError"
            ),
        };
        Assert.Throws<PythonRuntimeException>(() =>
            ManagedObjectProtocols.GetAttribute(unrelated, "value")
        );
    }

    [Fact]
    public void ConstructedAndRaisedSubclassInstances_ExposeInheritedValue()
    {
        using var output = new StringWriter();
        var result = new ManagedPythonEngine().Execute(
            """
            class End(StopIteration):
                pass
            class Child(End):
                pass
            for cls in [StopIteration, End, Child]:
                print(cls().value, cls('first', 'second').value)
            try:
                raise Child
            except Child as error:
                print(error.value)
            """,
            "stop_iteration_subclass.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(
            $"None first{Environment.NewLine}None first{Environment.NewLine}None first{Environment.NewLine}None{Environment.NewLine}",
            output.ToString()
        );
    }
}
