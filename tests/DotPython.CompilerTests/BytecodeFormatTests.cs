using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksCallLocalInstruction()
    {
        Assert.Equal(6, DotPythonBytecodeFormat.CurrentVersion);
    }
}
