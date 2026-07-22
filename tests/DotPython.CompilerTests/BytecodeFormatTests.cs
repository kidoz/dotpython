using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksIterationAndClassInstructions()
    {
        Assert.Equal(13, DotPythonBytecodeFormat.CurrentVersion);
    }
}
