using DotPython.Language.Text;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class PythonErrorIndicatorTests
{
    [Fact]
    public void GetRaisedException_ReturnsAndClearsTheTranslatedFault()
    {
        var indicator = new PythonErrorIndicator();
        var fault = new PythonRuntimeException("DPY4004", "Division by zero.", new TextSpan(3, 5));

        Assert.True(indicator.TrySetFromRuntimeFault(fault));
        Assert.True(indicator.IsSet);
        Assert.Equal("ZeroDivisionError", indicator.Occurred!.TypeName);

        var raised = Assert.IsType<PythonRaisedException>(indicator.GetRaisedException());

        Assert.False(indicator.IsSet);
        Assert.Null(indicator.Occurred);
        Assert.Equal("ZeroDivisionError", raised.Value.TypeName);
        Assert.Equal("Division by zero.", raised.Value.Message);
        Assert.Same(fault, raised.OriginatingFault);
    }

    [Fact]
    public void SetRaisedException_ReplacesExistingStateAndSupportsCallbackSaveRestore()
    {
        var indicator = new PythonErrorIndicator();
        var outer = new PythonRaisedException(new PythonExceptionValue("ValueError", "outer"));
        var callback = new PythonRaisedException(new PythonExceptionValue("TypeError", "callback"));

        indicator.SetRaisedException(outer);
        var savedOuter = Assert.IsType<PythonRaisedException>(indicator.GetRaisedException());
        indicator.SetRaisedException(callback);
        Assert.Same(callback, indicator.GetRaisedException());
        indicator.SetRaisedException(savedOuter);

        Assert.Same(outer, indicator.GetRaisedException());
        Assert.False(indicator.IsSet);
    }

    [Fact]
    public void TrySetFromRuntimeFault_LeavesExistingStateForAnUncatchableHostLimit()
    {
        var indicator = new PythonErrorIndicator();
        var existing = new PythonRaisedException(
            new PythonExceptionValue("RuntimeError", "existing")
        );
        indicator.SetRaisedException(existing);

        var translated = indicator.TrySetFromRuntimeFault(
            new PythonRuntimeException(
                "DPY4001",
                "The managed instruction limit was exceeded.",
                new TextSpan(0, 1)
            )
        );

        Assert.False(translated);
        Assert.Same(existing, indicator.GetRaisedException());
    }
}
