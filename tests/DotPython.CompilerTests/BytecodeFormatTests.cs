using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksPositionalOnlySignatures()
    {
        Assert.Equal(28, DotPythonBytecodeFormat.CurrentVersion);
    }
}
