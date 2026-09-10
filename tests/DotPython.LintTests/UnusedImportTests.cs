using DotPython.Language.Text;
using DotPython.Lint;
using Xunit;

namespace DotPython.LintTests;

public sealed class UnusedImportTests
{
    [Theory]
    [InlineData("import os\n", "os")]
    [InlineData("import os as operating_system\n", "os as operating_system")]
    [InlineData("import os.path\n", "os.path")]
    [InlineData("from os import path\n", "path")]
    [InlineData("from .helpers import value as result\n", "value as result")]
    [InlineData("import os, sys\nprint(os.name)\n", "sys")]
    [InlineData("import os\ndef f(os): return os\n", "os")]
    [InlineData("import os\ndef f():\n    os = 1\n    return os\n", "os")]
    [InlineData("import os\nf = lambda os: os\n", "os")]
    [InlineData("import os\nvalues = [os for os in []]\n", "os")]
    [InlineData("import os\nvalues = (os for os in [])\n", "os")]
    [InlineData("def f():\n    import os\n    return 1\n", "os")]
    [InlineData("if flag:\n    import os\n", "os")]
    [InlineData("from helpers import A\n__all__ = ['B']\n", "A")]
    [InlineData("import os\ntext = 'os'\n", "os")]
    [InlineData("import os\nx = object()\nx.os = 1\n", "os")]
    [InlineData("import os\ndef f():\n    match value:\n        case os: return os\n", "os")]
    [InlineData("import os\ndef f():\n    global os\n", "os")]
    public void Analyze_ReportsUnusedImportWithAliasSpan(string text, string fragment)
    {
        var source = new SourceText(text, "module.py");
        var result = Analyze(source);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DPYL004", diagnostic.Code);
        Assert.Equal(fragment, source.GetText(diagnostic.Span));
    }

    [Theory]
    [InlineData("import os\nprint(os.name)\n")]
    [InlineData("import os.path\nprint(os.path)\n")]
    [InlineData("from os import path as p\nprint(p)\n")]
    [InlineData("import os\ndef f(): return os.name\n")]
    [InlineData("def outer():\n    import os\n    def inner(): return os.name\n    return inner\n")]
    [InlineData(
        "def outer():\n    import os\n    def inner():\n        nonlocal os\n        return os.name\n    return inner\n"
    )]
    [InlineData("import os\ndef f():\n    global os\n    return os.name\n")]
    [InlineData(
        "import os\ndef outer():\n    global os\n    def inner(): return os\n    return inner\n"
    )]
    [InlineData("import os\nclass C:\n    os = 1\n    def f(self): return os\n")]
    [InlineData("import os\nclass C:\n    os = os\n")]
    [InlineData("import os\nvalues = [os for os in os.items]\n")]
    [InlineData("import os\nvalues = [os.name for x in []]\n")]
    [InlineData("import os\nvalues = [x for x in [] if os.name]\n")]
    [InlineData("import os\ndef f(x=os): return x\n")]
    [InlineData("from types import Item\ndef f(x: Item) -> Item: return x\n")]
    [InlineData("from types import Item\ndef f(x: 'Item') -> 'list[Item]': return x\n")]
    [InlineData("from types import Item\nx: \"list['Item']\"\n")]
    [InlineData("import os\nx = f'{os.name}'\n")]
    [InlineData("import os\nx = t'{os.name}'\n")]
    [InlineData("import os\nx = t'{(lambda: os)()}'\n")]
    [InlineData("import os as os\n")]
    [InlineData("from os import path as path\n")]
    [InlineData("from __future__ import annotations\n")]
    [InlineData("class C:\n    import os\n")]
    [InlineData("try:\n    import optional_package\nexcept ImportError:\n    pass\n")]
    [InlineData(
        "if flag:\n    import first as backend\nelse:\n    import second as backend\nbackend.run()\n"
    )]
    [InlineData("from helpers import A\n__all__ = ['A']\n")]
    [InlineData("from helpers import A\n__all__ = []\n__all__.append('A')\n")]
    [InlineData("from helpers import A\n__all__ = []\n__all__.extend(('A',))\n")]
    [InlineData("from helpers import A\n__all__ = []\n__all__ += ['A']\n")]
    [InlineData("from helpers import A\n__all__ = ('other',) + ('\\x41',)\n")]
    [InlineData("from helpers import A\n__all__ = make_exports()\n")]
    [InlineData("from helpers import A\n__all__, other = make_exports()\n")]
    [InlineData("from helpers import A\nfrom exports import __all__\n")]
    [InlineData("from helpers import A\nfor __all__ in export_sets: pass\n")]
    [InlineData(
        "from typing import TYPE_CHECKING\nif TYPE_CHECKING:\n    from helpers import A\ndef f(value: 'A'): pass\n"
    )]
    [InlineData("from helpers import A\n__all__ = []\nexports = __all__\nexports.append('A')\n")]
    [InlineData("from helpers import A\ndef exports():\n    global __all__\n    __all__ = ['A']\n")]
    [InlineData("import os\nexec('print(os)')\n")]
    [InlineData("import os\nnamespace = globals()\n")]
    [InlineData("def f():\n    import os\n    return locals()\n")]
    [InlineData("import os\ndel os\n")]
    [InlineData("import os\nos.name = 'changed'\n")]
    [InlineData("import os\nassert os\n")]
    public void Analyze_PreservesUsedOrIntentionallyExposedImports(string text)
    {
        Assert.Empty(Analyze(new SourceText(text)).Diagnostics);
    }

    [Fact]
    public void Analyze_ShadowedImportDoesNotUseOuterBinding()
    {
        var source = new SourceText("import os\ndef f():\n    import os\n    return os.name\n");
        var diagnostic = Assert.Single(Analyze(source).Diagnostics);

        Assert.Equal(new TextSpan(7, 2), diagnostic.Span);
    }

    [Fact]
    public void Analyze_UsesExistingSelectionAndSuppressionContract()
    {
        var source = new SourceText("import os # dotpython: ignore[DPYL004]\nimport sys\n");
        var diagnostic = Assert.Single(Analyze(source).Diagnostics);

        Assert.Equal("sys", source.GetText(diagnostic.Span));
        Assert.Empty(
            PythonLinter
                .Analyze(
                    source,
                    new PythonLintOptions { Ignore = ["DPYL004"] },
                    TestContext.Current.CancellationToken
                )
                .Diagnostics
        );
    }

    [Fact]
    public void Analyze_DoesNotReportUnusedImportsWhenBindingIsInvalid()
    {
        Assert.Empty(
            Analyze(new SourceText("import os\ndef f():\n    nonlocal missing\n")).Diagnostics
        );
    }

    private static PythonLintResult Analyze(SourceText source) =>
        PythonLinter.Analyze(
            source,
            new PythonLintOptions { Select = ["DPYL004"] },
            TestContext.Current.CancellationToken
        );
}
