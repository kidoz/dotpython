using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksAsyncStatementInstructions()
    {
        Assert.Equal(24, DotPythonBytecodeFormat.CurrentVersion);
    }
}
