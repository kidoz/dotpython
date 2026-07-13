using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_StartsAtOne()
    {
        Assert.Equal(1, DotPythonBytecodeFormat.CurrentVersion);
    }
}
