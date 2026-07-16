using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksManagedExceptionInstructions()
    {
        Assert.Equal(9, DotPythonBytecodeFormat.CurrentVersion);
    }
}
