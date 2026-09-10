using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksImplicitClassCellProtocol()
    {
        Assert.Equal(32, DotPythonBytecodeFormat.CurrentVersion);
    }
}
