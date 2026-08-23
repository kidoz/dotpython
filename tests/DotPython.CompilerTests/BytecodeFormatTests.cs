using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksAsyncGeneratorInstructions()
    {
        Assert.Equal(25, DotPythonBytecodeFormat.CurrentVersion);
    }
}
