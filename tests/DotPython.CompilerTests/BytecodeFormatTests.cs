using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksMultipleInheritanceClassBases()
    {
        Assert.Equal(27, DotPythonBytecodeFormat.CurrentVersion);
    }
}
