using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksClosureMetadataLayout()
    {
        Assert.Equal(2, DotPythonBytecodeFormat.CurrentVersion);
    }
}
