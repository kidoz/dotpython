using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksContextManagerInstructions()
    {
        Assert.Equal(15, DotPythonBytecodeFormat.CurrentVersion);
    }
}
