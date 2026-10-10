using DotPython.Compiler;
using DotPython.Compiler.Bytecode;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.CompilerTests;

/// <summary>
/// Adjacent string literals, which the parser folds into one expression: the compiler sees
/// a single constant, and the mixes CPython refuses are refused with its wording.
/// </summary>
public sealed class StringConcatenationTests
{
    [Theory]
    [InlineData("\"a\" \"b\"", "ab")]
    [InlineData("\"a\\n\" \"b\"", "a\nb")]
    [InlineData("r\"a\\n\" \"b\"", "a\\nb")]
    [InlineData("\"\" \"\" \"x\"", "x")]
    [InlineData("(\"one \"\n\"two\")", "one two")]
    public void AdjacentStringsFoldIntoOneConstant(string source, string expected)
    {
        var result = PythonCompiler.Compile(PythonParser.Parse(new SourceText(source)).Module);

        Assert.Empty(result.Diagnostics);
        var constant = Assert.Single(
            result.Code.Constants,
            constant => constant.Type == PythonConstantType.TextValue
        );
        Assert.Equal(expected, constant.Value);
    }

    [Fact]
    public void AdjacentBytesFoldIntoOneByteSequence()
    {
        var result = PythonCompiler.Compile(
            PythonParser.Parse(new SourceText("b\"\\x41\" b\"B\"")).Module
        );

        Assert.Empty(result.Diagnostics);
        var constant = Assert.Single(
            result.Code.Constants,
            constant => constant.Type == PythonConstantType.ByteSequence
        );
        Assert.Equal("AB"u8.ToArray(), constant.Value);
    }

    [Theory]
    [InlineData("b\"a\" \"b\"", "cannot mix bytes and nonbytes literals")]
    [InlineData("f\"a\" b\"b\"", "cannot mix bytes and nonbytes literals")]
    [InlineData("\"a\" t\"b\"", "cannot mix t-string literals with string or bytes literals")]
    [InlineData("t\"a\" f\"b\"", "cannot mix t-string literals with string or bytes literals")]
    public void MixingLiteralKindsIsRefused(string source, string message)
    {
        var parseResult = PythonParser.Parse(new SourceText(source));

        var diagnostic = Assert.Single(parseResult.Diagnostics);
        Assert.Equal("DPY2001", diagnostic.Code);
        Assert.Equal(message, diagnostic.Message);
    }
}
