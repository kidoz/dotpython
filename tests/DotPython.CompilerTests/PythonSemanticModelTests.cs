using DotPython.Compiler;
using DotPython.Compiler.Artifacts;
using DotPython.Compiler.Binding;
using DotPython.Language.Text;
using DotPython.ParserGenerator;
using Xunit;

namespace DotPython.CompilerTests;

public sealed class PythonSemanticModelTests
{
    [Fact]
    public void Analyze_ExposesMangledLookupSpellingForUnresolvedNames()
    {
        var model = Analyze(new SourceText("class C:\n    def f(self): return __missing\n"));
        var occurrence = Assert.Single(
            model.Occurrences,
            o => o.Kind == PythonNameOccurrenceKind.Read
        );
        Assert.Equal("__missing", occurrence.Name);
        Assert.Equal("_C__missing", occurrence.LookupName);
        Assert.Null(occurrence.Symbol);
        Assert.False(occurrence.IsQuotedAnnotation);
    }

    [Fact]
    public void Analyze_TracksDistinctImportsReadsAndWritesWithLexicalIdentity()
    {
        var source = new SourceText(
            "import os as system\nvalue = system.name\ndef f(system): return system\n"
        );
        var model = Analyze(source);
        var import = Assert.Single(model.Imports);
        var reads = model
            .Occurrences.Where(o => o.Name == "system" && o.Kind == PythonNameOccurrenceKind.Read)
            .ToArray();

        Assert.Equal("os as system", source.GetText(import.Occurrence.Span));
        Assert.Equal("os", import.ModuleName);
        Assert.Null(import.ImportedName);
        Assert.Equal(2, reads.Length);
        Assert.Same(import.Occurrence.Symbol, reads[0].Symbol);
        Assert.NotSame(import.Occurrence.Symbol, reads[1].Symbol);
        Assert.Equal(PythonScopeKind.Function, reads[1].Symbol!.Scope.Kind);
        Assert.Contains(
            model.Occurrences,
            o => o.Name == "value" && o.Kind == PythonNameOccurrenceKind.Definition
        );
        Assert.All(reads, read => Assert.Equal("system", source.GetText(read.Span)));
    }

    [Theory]
    [InlineData("import x\ndef f():\n    global x\n    return x\n")]
    [InlineData(
        "def outer():\n    import x\n    def inner():\n        nonlocal x\n        return x\n    return inner\n"
    )]
    [InlineData("import x\nclass C:\n    x = 1\n    def f(self): return x\n")]
    [InlineData("import x\nitems = [x.name for item in []]\n")]
    public void Analyze_ResolvesClosureGlobalAndClassBoundaries(string text)
    {
        var model = Analyze(new SourceText(text));
        var symbol = Assert.Single(model.Imports).Occurrence.Symbol;
        var read = Assert.Single(
            model.Occurrences,
            o => o.Name == "x" && o.Kind == PythonNameOccurrenceKind.Read
        );

        Assert.Same(symbol, read.Symbol);
    }

    [Fact]
    public void Analyze_RecordsClassBodyFallbackWithoutLeakingClassLocalsIntoMethods()
    {
        var model = Analyze(
            new SourceText("import x\nclass C:\n    x = x\n    def f(self): return x\n")
        );
        var imported = Assert.Single(model.Imports).Occurrence.Symbol;
        var reads = model
            .Occurrences.Where(o => o.Name == "x" && o.Kind == PythonNameOccurrenceKind.Read)
            .ToArray();

        Assert.NotSame(imported, reads[0].Symbol);
        Assert.Same(imported, reads[0].FallbackSymbol);
        Assert.Same(imported, reads[1].Symbol);
        Assert.Null(reads[1].FallbackSymbol);
    }

    [Fact]
    public void Analyze_ComprehensionFirstIterableUsesOuterScope()
    {
        var model = Analyze(new SourceText("import x\nitems = [x for x in x.items]\n"));
        var imported = Assert.Single(model.Imports).Occurrence.Symbol;
        var reads = model
            .Occurrences.Where(o => o.Name == "x" && o.Kind == PythonNameOccurrenceKind.Read)
            .ToArray();

        Assert.NotSame(imported, reads[0].Symbol);
        Assert.Same(imported, reads[1].Symbol);
    }

    [Fact]
    public void Analyze_KeepsUnresolvedNamesDistinctFromLocalSymbols()
    {
        var model = Analyze(new SourceText("x = missing\n"));
        var read = Assert.Single(model.Occurrences, o => o.Kind == PythonNameOccurrenceKind.Read);

        Assert.Equal("missing", read.Name);
        Assert.Null(read.Symbol);
        Assert.Null(read.FallbackSymbol);
    }

    [Fact]
    public void Analyze_ComprehensionAssignmentExpressionBindsInContainingScope()
    {
        var model = Analyze(
            new SourceText(
                "def outer():\n    items = [(captured := item) for item in [1]]\n    def inner(): return captured\n    return inner\n"
            )
        );
        var definition = Assert.Single(
            model.Occurrences,
            occurrence =>
                occurrence.Name == "captured"
                && occurrence.Kind == PythonNameOccurrenceKind.Definition
        );
        var read = Assert.Single(
            model.Occurrences,
            occurrence =>
                occurrence.Name == "captured" && occurrence.Kind == PythonNameOccurrenceKind.Read
        );

        Assert.Equal("outer", definition.Symbol!.Scope.Name);
        Assert.Same(definition.Symbol, read.Symbol);
    }

    [Fact]
    public void Analyze_ResolvesImplicitClassCell()
    {
        var model = Analyze(new SourceText("class C:\n    def f(self): return __class__\n"));
        var read = Assert.Single(
            model.Occurrences,
            occurrence =>
                occurrence.Name == "__class__" && occurrence.Kind == PythonNameOccurrenceKind.Read
        );

        Assert.Equal(PythonScopeKind.Class, read.Symbol!.Scope.Kind);
        Assert.Equal("C", read.Symbol.Scope.Name);
    }

    [Fact]
    public void Analyze_MapsNestedInterpolationReferencesToOriginalSource()
    {
        var source = new SourceText("import x\ntext = f\"{t'{(lambda: x)()}'}\"\n");
        var model = Analyze(source);
        var reference = Assert.Single(
            model.Occurrences,
            o => o.Name == "x" && o.Kind == PythonNameOccurrenceKind.Read
        );

        Assert.Same(Assert.Single(model.Imports).Occurrence.Symbol, reference.Symbol);
        Assert.Equal(source.Content.LastIndexOf('x'), reference.Span.Start);
        Assert.Equal("x", source.GetText(reference.Span));
    }

    [Fact]
    public void Analyze_TracksAnnotationsAndDecodedExportNames()
    {
        var source = new SourceText(
            "from types import Item\ndef f(x: 'list[Item]') -> Item: return x\n__all__ = ['\\x49tem']\n"
        );
        var model = Analyze(source);
        var imported = Assert.Single(model.Imports).Occurrence.Symbol;
        var annotations = model
            .Occurrences.Where(o =>
                o.Name == "Item" && o.Kind == PythonNameOccurrenceKind.Annotation
            )
            .ToArray();

        Assert.Equal(2, annotations.Length);
        Assert.All(annotations, occurrence => Assert.Same(imported, occurrence.Symbol));
        Assert.Equal("'list[Item]'", source.GetText(annotations[0].Span));
        Assert.True(annotations[0].IsQuotedAnnotation);
        Assert.False(annotations[1].IsQuotedAnnotation);
        Assert.Contains("Item", model.ExportedNames);
        Assert.False(model.HasDynamicExports);
    }

    [Fact]
    public void Analyze_RecordsInvalidBindingWithoutInventingOccurrences()
    {
        var parse = PythonParser.Parse(new SourceText("def f():\n    nonlocal missing\n"));
        var model = PythonSymbolBinder.Analyze(parse.Module, TestContext.Current.CancellationToken);

        Assert.Contains(model.Diagnostics, diagnostic => diagnostic.Code == "DPY3110");
        Assert.Empty(model.Occurrences);
    }

    [Fact]
    public void Analyze_DoesNotChangeSubsequentCompilation()
    {
        var parse = PythonParser.Parse(new SourceText("import os\ndef f(x): return x + 1\n"));
        var before = PythonCompiler.Compile(parse.Module);
        _ = PythonSymbolBinder.Analyze(parse.Module, TestContext.Current.CancellationToken);
        var after = PythonCompiler.Compile(parse.Module);

        Assert.Equal(before.Diagnostics, after.Diagnostics);
        var first = DotPythonModuleArtifact.Create("test", before.Code, []);
        var second = DotPythonModuleArtifact.Create("test", after.Code, []);
        Assert.Equal(
            DotPythonModuleArtifactSerializer.Serialize(first),
            DotPythonModuleArtifactSerializer.Serialize(second)
        );
    }

    [Fact]
    public void Analyze_ObservesCancellation()
    {
        var parse = PythonParser.Parse(new SourceText("import os\n"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            PythonSymbolBinder.Analyze(parse.Module, cancellation.Token)
        );
    }

    private static PythonSemanticModel Analyze(SourceText source)
    {
        var parse = PythonParser.Parse(source);
        Assert.True(parse.Success, string.Join("; ", parse.Diagnostics.Select(d => d.Message)));
        var model = PythonSymbolBinder.Analyze(parse.Module, TestContext.Current.CancellationToken);
        Assert.Empty(model.Diagnostics);
        return model;
    }
}
