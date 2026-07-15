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
        Assert.Empty(function.CellVariableNames);
        Assert.Empty(function.FreeVariableNames);
    }

    [Fact]
    public void Bind_ClassifiesDirectClosureCellsAndFreeVariables()
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

        Assert.Empty(result.Diagnostics);
        var outer = Assert.Single(result.ModuleScope.Children);
        var inner = Assert.Single(outer.Children);
        Assert.Equal(["value"], outer.CellVariableNames);
        Assert.Empty(outer.FreeVariableNames);
        Assert.Empty(inner.CellVariableNames);
        Assert.Equal(["value"], inner.FreeVariableNames);
    }

    [Fact]
    public void Bind_PropagatesFreeVariablesThroughIntermediateFunctions()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "def outer(value):\n"
                    + "    def middle():\n"
                    + "        def inner(): return value\n"
                    + "        return inner\n"
                    + "    return middle\n"
            )
        );

        var result = PythonSymbolBinder.Bind(parseResult.Module);

        Assert.Empty(result.Diagnostics);
        var outer = Assert.Single(result.ModuleScope.Children);
        var middle = Assert.Single(outer.Children);
        var inner = Assert.Single(middle.Children);
        Assert.Equal(["value"], outer.CellVariableNames);
        Assert.Equal(["value"], middle.FreeVariableNames);
        Assert.Equal(["value"], inner.FreeVariableNames);
    }

    [Fact]
    public void Bind_LeavesModuleBindingsGlobalAndHonorsLocalShadowing()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "value = 40\n"
                    + "def outer(value):\n"
                    + "    def inner(value): return value\n"
                    + "    return inner(value)\n"
            )
        );

        var result = PythonSymbolBinder.Bind(parseResult.Module);

        Assert.Empty(result.Diagnostics);
        var outer = Assert.Single(result.ModuleScope.Children);
        var inner = Assert.Single(outer.Children);
        Assert.Empty(outer.CellVariableNames);
        Assert.Empty(outer.FreeVariableNames);
        Assert.Empty(inner.CellVariableNames);
        Assert.Empty(inner.FreeVariableNames);
    }

    [Fact]
    public void Bind_CollectsReferencesNestedInsideCollectionDisplays()
    {
        var parseResult = PythonParser.Parse(
            new SourceText("def collect(first, second): return [first, (second,)]")
        );

        var result = PythonSymbolBinder.Bind(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            ["first", "second"],
            Assert.Single(result.ModuleScope.Children).ReferencedNames
        );
    }

    [Fact]
    public void Bind_ClassifiesForTargetsAndSubscriptionReferences()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "def update(values, mapping):\n"
                    + "    for key in mapping:\n"
                    + "        values[key] = mapping[key]\n"
                    + "    return values\n"
            )
        );

        var result = PythonSymbolBinder.Bind(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        var function = Assert.Single(result.ModuleScope.Children);
        Assert.Equal(["values", "mapping", "key"], function.LocalNames);
        Assert.Equal(["mapping", "key", "values"], function.ReferencedNames);
    }

    [Fact]
    public void Bind_TreatsImportedNamesAsAssignmentsInTheirScope()
    {
        var parseResult = PythonParser.Parse(
            new SourceText(
                "def load():\n"
                    + "    import helper as module\n"
                    + "    from values import answer as result\n"
                    + "    return module.answer() + result"
            )
        );

        var result = PythonSymbolBinder.Bind(parseResult.Module);

        Assert.Empty(parseResult.Diagnostics);
        Assert.Empty(result.Diagnostics);
        var function = Assert.Single(result.ModuleScope.Children);
        Assert.Equal(["module", "result"], function.LocalNames);
        Assert.Equal(["module", "result"], function.ReferencedNames);
    }
}
