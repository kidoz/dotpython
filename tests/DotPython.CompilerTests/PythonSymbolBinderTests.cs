using DotPython.Compiler.Binding;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class PythonSymbolBinderTests
{
    [Fact]
    public void Bind_ClassifiesParametersAssignmentsAndReferencesPerFunction()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "factor = 2\n"
                    + "def calculate(value):\n"
                    + "    print(result)\n"
                    + "    result = value * factor\n"
                    + "    return result\n"
            )
        );

        var result = PythonSymbolBinder.Bind(parseResult.Module);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(["factor", "calculate"], result.ModuleScope.LocalNames);
        var function = Assert.Single(result.ModuleScope.Children);
        Assert.Equal(PythonScopeKind.Function, function.Kind);
        Assert.Equal(["value"], function.Parameters);
        Assert.Equal(["value", "result"], function.LocalNames);
        Assert.Equal(["print", "result", "value", "factor"], function.ReferencedNames);
    }

    [Fact]
    public void Bind_RejectsClosureVariablesUntilCellsAreImplemented()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "def outer(value):\n"
                    + "    def inner():\n"
                    + "        return value\n"
                    + "    return inner()\n"
            )
        );

        var result = PythonSymbolBinder.Bind(parseResult.Module);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPY3101", diagnostic.Code);
    }
}
