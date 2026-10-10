using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksBodyDocstrings()
    {
        Assert.Equal(36, DotPythonBytecodeFormat.CurrentVersion);
    }
}
