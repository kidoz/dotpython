using DotPython.Language.Diagnostics;
using DotPython.Language.Text;
using DotPython.Lint;
using Xunit;

namespace DotPython.LintTests;

public sealed class UndefinedNameTests
{
    [Theory]
    [InlineData("print(missing)\n", "missing")]
    [InlineData("missing()\n", "missing")]
    [InlineData("def f(): return missing\n", "missing")]
    [InlineData("f = lambda: missing\n", "missing")]
    [InlineData("def f(value=missing): return value\n", "missing")]
    [InlineData("@missing\ndef f(): pass\n", "missing")]
    [InlineData("class C(missing): pass\n", "missing")]
    [InlineData("class C(metaclass=missing): pass\n", "missing")]
    [InlineData("def f(value: missing): return value\n", "missing")]
    [InlineData("def f() -> missing: pass\n", "missing")]
    [InlineData("value: missing\n", "missing")]
    [InlineData("value = [missing for item in []]\n", "missing")]
    [InlineData("value = [item for item in missing]\n", "missing")]
    [InlineData("value = [item for item in [] if missing]\n", "missing")]
    [InlineData("value = [item for item in []]\nprint(item)\n", "item")]
    [InlineData("def f(value): return value\nprint(value)\n", "value")]
    [InlineData("class C:\n    value = 1\n    def f(self): return value\n", "value")]
    [InlineData("class C:\n    value = 1\n    values = [value for item in []]\n", "value")]
    [InlineData("class C:\n    def f(self): return __private\n", "__private")]
    [InlineData("def f():\n    global missing\n    return missing\n", "missing")]
    [InlineData("missing.attribute = 1\n", "missing")]
    [InlineData("missing[0] = 1\n", "missing")]
    [InlineData("del missing.attribute\n", "missing")]
    [InlineData("def f():\n    try: pass\n    except missing: pass\n", "missing")]
    [InlineData("with missing as resource: pass\n", "missing")]
    [InlineData("__all__ = missing\n", "missing")]
    [InlineData("text = f'{missing}'\n", "missing")]
    [InlineData("text = t'{missing}'\n", "missing")]
    [InlineData("text = f\"{f'{missing}'}\"\n", "missing")]
    [InlineData("text = t'{(lambda: missing)()}'\n", "missing")]
    [InlineData("# 😀\r\nprint(ошибка)\r\n", "ошибка")]
    public void Analyze_ReportsUnresolvedReadAtIdentifier(string text, string name)
    {
        var source = new SourceText(text);
        var diagnostic = Assert.Single(Analyze(source).Diagnostics);

        Assert.Equal("DPYL005", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(name, source.GetText(diagnostic.Span));
        Assert.Equal($"Name '{name}' has no visible definition.", diagnostic.Message);
    }

    [Theory]
    [InlineData("value = 1\nprint(value)\n")]
    [InlineData("print(value)\nvalue = 1\n")]
    [InlineData("if False: value = 1\nprint(value)\n")]
    [InlineData("value = 1\ndel value\nprint(value)\n")]
    [InlineData("def f(value): return value\n")]
    [InlineData("def f(*args, **kwargs): return args, kwargs\n")]
    [InlineData("f = lambda value: value\n")]
    [InlineData("import missing_package\nmissing_package.run()\n")]
    [InlineData("from missing_package import Item as Value\nValue()\n")]
    [InlineData("import os.path\nprint(os.path)\n")]
    [InlineData("def f(): return later\nlater = 1\n")]
    [InlineData("def f():\n    global value\n    value = 1\nprint(value)\n")]
    [InlineData(
        "def outer(value):\n    def inner():\n        nonlocal value\n        return value\n    return inner\n"
    )]
    [InlineData("value = 1\nclass C:\n    value = value\n    def f(self): return value\n")]
    [InlineData("class C:\n    value = 1\n    def f(self, x=value) -> value: return x\n")]
    [InlineData("class C:\n    def f(self): return __class__\n")]
    [InlineData("class C:\n    __private = 1\n    print(__private)\n")]
    [InlineData("values = [item for item in []]\n")]
    [InlineData("values = [(left, right) for left in [] for right in left]\n")]
    [InlineData("values = [(last := item) for item in []]\nprint(last)\n")]
    [InlineData("for item in []: print(item)\n")]
    [InlineData("with open('file') as resource: print(resource)\n")]
    [InlineData("try: pass\nexcept Exception as error: print(error)\n")]
    [InlineData("match 1:\n    case captured: print(captured)\n")]
    [InlineData("value: int\nprint(value)\n")]
    [InlineData(
        "from typing import Literal, Annotated\nx: Literal['ready']\ny: Annotated[int, 'metadata']\n"
    )]
    [InlineData("def f(value: 'Unknown') -> 'list[Other]': return value\n")]
    [InlineData("class C:\n    print(__module__, __qualname__)\n")]
    [InlineData(
        "print(__name__, __file__, __package__, __spec__, __loader__, __builtins__, __doc__, __cached__, __annotations__, __path__)\n"
    )]
    [InlineData(
        "print(abs, aiter, anext, bool, breakpoint, bytearray, bytes, compile, complex, dict, filter, float, frozenset, help, input, int, iter, len, list, map, memoryview, next, object, open, range, set, slice, str, super, tuple, type, zip)\n"
    )]
    [InlineData(
        "print(BaseException, BaseExceptionGroup, ExceptionGroup, PythonFinalizationError, EncodingWarning, WindowsError, IOError, EnvironmentError, NotImplemented, Ellipsis, __debug__, __import__, __build_class__, exit, quit)\n"
    )]
    [InlineData("def f():\n    nonlocal missing\n    return unknown\n")]
    public void Analyze_RespectsLexicalBindingsAndConservativeExemptions(string text)
    {
        Assert.Empty(Analyze(new SourceText(text)).Diagnostics);
    }

    [Theory]
    [InlineData("exec('provided = 1')\nprint(provided)\n")]
    [InlineData("exec('provided = 1')\ndef f(): return provided\n")]
    [InlineData("globals()['provided'] = 1\nprint(provided)\n")]
    [InlineData("def f():\n    locals()['provided'] = 1\n    return provided\n")]
    [InlineData("vars()['provided'] = 1\nprint(provided)\n")]
    [InlineData("eval('register()')\nprint(provided)\n")]
    [InlineData("def f(): return globals()\nprint(provided)\n")]
    public void Analyze_SkipsUndefinedNamesInDynamicallyObservedModules(string text)
    {
        Assert.Empty(Analyze(new SourceText(text)).Diagnostics);
    }

    [Fact]
    public void Analyze_PreservesUnsupportedFormatExpressionParserDiagnostic()
    {
        var result = Analyze(new SourceText("text = f'{1:{missing}}'\n"));
        Assert.Equal("DPY2019", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Analyze_KnownGlobalsUseExactIdentifiersAndMangledLookupNames()
    {
        var source = new SourceText(
            "def f(): return host, контекст\nclass C:\n    def f(self): return __host\nprint(Host)\n"
        );
        var result = Analyze(source, ["host", "контекст", "_C__host"]);

        Assert.Equal("Host", source.GetText(Assert.Single(result.Diagnostics).Span));
    }

    [Theory]
    [InlineData("")]
    [InlineData("host.value")]
    [InlineData("1host")]
    [InlineData("host name")]
    [InlineData("host\n")]
    [InlineData(" host")]
    [InlineData("host#comment")]
    [InlineData("class")]
    [InlineData("True")]
    [InlineData(null)]
    public void Analyze_RejectsInvalidKnownGlobals(string? name)
    {
        Assert.Throws<ArgumentException>(() => Analyze(new SourceText("pass"), [name!]));
    }

    [Fact]
    public void Analyze_DefaultSelectionSuppressionIgnoreAndOrderingApply()
    {
        var source = new SourceText(
            "print(first, second)\nprint(ignored) # dotpython: ignore[DPYL005]\n"
        );
        var result = PythonLinter.Analyze(
            source,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(["first", "second"], result.Diagnostics.Select(d => source.GetText(d.Span)));
        Assert.Empty(
            PythonLinter
                .Analyze(
                    source,
                    new PythonLintOptions { Ignore = ["DPYL005"] },
                    TestContext.Current.CancellationToken
                )
                .Diagnostics
        );
    }

    private static PythonLintResult Analyze(SourceText source, string[]? knownGlobals = null) =>
        PythonLinter.Analyze(
            source,
            new PythonLintOptions { Select = ["DPYL005"], KnownGlobals = knownGlobals ?? [] },
            TestContext.Current.CancellationToken
        );
}
