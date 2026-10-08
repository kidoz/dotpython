using DotPython.Compiler.Binding;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class WalrusScopeBindingTests
{
    [Fact]
    public void Bind_BindsAComprehensionWalrusInTheOwningFunction()
    {
        var result = Bind("def f():\n" + "    [(x := i) for i in range(3)]\n" + "    return x\n");

        Assert.Empty(result.Diagnostics);
        var function = Assert.Single(result.ModuleScope.Children);
        Assert.Contains("x", function.LocalNames);
        Assert.Equal(["x"], function.CellVariableNames);

        var comprehension = Assert.Single(function.Children);
        Assert.Equal("<listcomp>", comprehension.Name);
        Assert.DoesNotContain("x", comprehension.LocalNames);
        Assert.Equal(["x"], comprehension.FreeVariableNames);
    }

    [Fact]
    public void Bind_BindsAModuleComprehensionWalrusAsAGlobal()
    {
        var result = Bind("x = 0\n[(x := i) for i in range(3)]\nprint(x)\n");

        Assert.Empty(result.Diagnostics);
        var comprehension = Assert.Single(result.ModuleScope.Children);
        Assert.DoesNotContain("x", comprehension.LocalNames);
        // A module has no cells, so the name stays a global on both sides.
        Assert.Empty(comprehension.FreeVariableNames);
        Assert.Empty(result.ModuleScope.CellVariableNames);
    }

    [Fact]
    public void Bind_ThreadsANestedComprehensionWalrusThroughBothComprehensions()
    {
        var result = Bind(
            "def f():\n"
                + "    [[(z := j) for j in range(2)] for i in range(2)]\n"
                + "    return z\n"
        );

        Assert.Empty(result.Diagnostics);
        var function = Assert.Single(result.ModuleScope.Children);
        Assert.Equal(["z"], function.CellVariableNames);

        var outer = Assert.Single(function.Children);
        Assert.Equal(["z"], outer.FreeVariableNames);
        Assert.DoesNotContain("z", outer.LocalNames);

        var inner = Assert.Single(outer.Children);
        Assert.Equal(["z"], inner.FreeVariableNames);
        Assert.DoesNotContain("z", inner.LocalNames);
    }

    [Fact]
    public void Bind_KeepsADeclaredGlobalOutOfTheComprehensionScope()
    {
        var result = Bind(
            "def f():\n"
                + "    global gx\n"
                + "    [(gx := i) for i in range(3)]\n"
                + "    return gx\n"
        );

        Assert.Empty(result.Diagnostics);
        var function = Assert.Single(result.ModuleScope.Children);
        Assert.DoesNotContain("gx", function.LocalNames);
        Assert.Empty(function.CellVariableNames);

        var comprehension = Assert.Single(function.Children);
        Assert.DoesNotContain("gx", comprehension.LocalNames);
        Assert.Empty(comprehension.FreeVariableNames);
    }

    [Theory]
    [InlineData("[(i := 1) for i in range(2)]", "DPY3119")]
    [InlineData("[[(i := 1) for j in range(2)] for i in range(2)]", "DPY3119")]
    [InlineData("[x for x in (n := range(3))]", "DPY3120")]
    [InlineData("[x for x in [(q := 1) for _ in range(1)]]", "DPY3120")]
    [InlineData("[x for x in (lambda: (n := range(3)))()]", "DPY3120")]
    [InlineData("x := 1", "DPY3122")]
    public void Bind_RejectsPlacementsCpythonRefuses(string source, string code)
    {
        var result = Bind(source + "\n");

        Assert.Equal(code, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Bind_RejectsAComprehensionWalrusInAClassBody()
    {
        var result = Bind("class C:\n    [(y := 1) for i in range(2)]\n");

        Assert.Equal("DPY3121", Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData("(x := 1)\n")]
    [InlineData("print([x for x in range(3) if (n := x)])\n")]
    [InlineData("print([(lambda: (i := 1)) for i in range(1)][0]())\n")]
    [InlineData("print([(q := 1) for _ in range(1)] + [(q := 2) for _ in range(1)])\n")]
    [InlineData("class C:\n    f = lambda: [(q := 1) for _ in range(1)]\n")]
    public void Bind_AcceptsPlacementsCpythonAllows(string source)
    {
        Assert.Empty(Bind(source).Diagnostics);
    }

    private static PythonBindingResult Bind(string source) =>
        PythonSymbolBinder.Bind(PythonParser.Parse(new SourceText(source)).Module);
}
