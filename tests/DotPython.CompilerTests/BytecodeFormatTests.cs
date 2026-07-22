using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksDeletionInstructions()
    {
        Assert.Equal(14, DotPythonBytecodeFormat.CurrentVersion);
    }
}
