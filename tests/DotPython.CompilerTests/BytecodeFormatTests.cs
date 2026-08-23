using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksCoroutineInstructions()
    {
        Assert.Equal(23, DotPythonBytecodeFormat.CurrentVersion);
    }
}
