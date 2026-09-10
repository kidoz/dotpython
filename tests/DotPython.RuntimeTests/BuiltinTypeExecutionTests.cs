using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class BuiltinTypeExecutionTests
{
    [Fact]
    public void SavedBuiltinTypesRetainIdentityAndUseCurrentExecution()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var first = engine.Execute(
            "saved = [range, enumerate, type, dict, ValueError]\nmake_range = range\nmake_type = type",
            "before.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(first.Success);
        var second = engine.Execute(
            """
            print(all(before is after for before, after in zip(saved, [range, enumerate, type, dict, ValueError])))
            print(list(make_range(3)))
            class Field:
                def __set_name__(self, owner, name): print(owner.__name__, name)
            C = make_type('C', (), {'value': Field()})
            print(C.__module__)
            """,
            "after.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(second.Success);
        Assert.Equal(
            string.Join(Environment.NewLine, "True", "[0, 1, 2]", "C value", "__main__", ""),
            output.ToString()
        );
    }
}
