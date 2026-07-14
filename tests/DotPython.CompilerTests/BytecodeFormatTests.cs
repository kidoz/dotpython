using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksCollectionInstructions()
    {
        Assert.Equal(3, DotPythonBytecodeFormat.CurrentVersion);
    }
}
