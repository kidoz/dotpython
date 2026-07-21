using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksKeywordCallAndDefaultInstructions()
    {
        Assert.Equal(10, DotPythonBytecodeFormat.CurrentVersion);
    }
}
