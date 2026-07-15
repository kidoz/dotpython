using DotPython.Compiler;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class BytecodeFormatTests
{
    [Fact]
    public void CurrentVersion_TracksManagedImportInstructions()
    {
        Assert.Equal(8, DotPythonBytecodeFormat.CurrentVersion);
    }
}
