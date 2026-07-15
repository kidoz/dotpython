using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksReturnLocalInstruction()
    {
        Assert.Equal(5, DotPythonBytecodeFormat.CurrentVersion);
    }
}
