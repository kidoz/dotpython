using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksExceptionGroupInstructions()
    {
        Assert.Equal(26, DotPythonBytecodeFormat.CurrentVersion);
    }
}
