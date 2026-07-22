using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksCollectionProtocolInstructions()
    {
        Assert.Equal(11, DotPythonBytecodeFormat.CurrentVersion);
    }
}
