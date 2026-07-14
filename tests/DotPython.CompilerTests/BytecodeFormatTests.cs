using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksMutableCollectionAndIterationInstructions()
    {
        Assert.Equal(4, DotPythonBytecodeFormat.CurrentVersion);
    }
}
