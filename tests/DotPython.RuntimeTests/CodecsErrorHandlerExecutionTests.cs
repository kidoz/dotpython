using System.Reflection;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class CodecsErrorHandlerExecutionTests
{
    [Theory]
    [InlineData("ignore", false)]
    [InlineData("replace", false)]
    [InlineData("backslashreplace", false)]
    [InlineData("xmlcharrefreplace", false)]
    [InlineData("namereplace", false)]
    [InlineData("surrogatepass", false)]
    [InlineData("surrogateescape", false)]
    [InlineData("backslashreplace", true)]
    public void SavedHandlersUseCurrentBudgetsAndRecoverWithoutPublishingResults(
        string name,
        bool decode
    )
    {
        var engine = new ManagedPythonEngine();
        using var previousOutput = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var source = decode ? "b'\\xff' * 100000" : "chr(0xdc80) * 100000";
        Assert.True(
            engine
                .Execute(
                    "import codecs\nhandler = codecs.lookup_error('"
                        + name
                        + "')\nerror = "
                        + (decode ? "UnicodeDecodeError" : "UnicodeEncodeError")
                        + "('utf8',"
                        + source
                        + ",0,100000,'bad')\ntarget = 'unchanged'",
                    "codecs_setup.py",
                    previousOutput,
                    cancellationToken: cancellation.Token
                )
                .Success
        );
        cancellation.Cancel();
        var original = Global(engine, "target");
        var error = Assert.IsType<PythonExceptionValue>(Global(engine, "error"));
        var originalSource = error.UnicodeErrorState!.Object;
        using var output = new StringWriter();
        var limited = engine.Execute(
            "try:\n    target = handler(error)\nexcept BaseException:\n    print('swallowed')\nfinally:\n    print('cleanup')",
            "codecs_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 100 },
            TestContext.Current.CancellationToken
        );
        Assert.Equal("DPY4001", Assert.Single(limited.Diagnostics).Code);
        Assert.Equal("cleanup" + Environment.NewLine, output.ToString());
        Assert.Same(original, Global(engine, "target"));
        Assert.Same(originalSource, error.UnicodeErrorState.Object);
        Assert.Empty(previousOutput.ToString());
        var recovered = engine.Execute(
            "target = handler(error)",
            "codecs_recovery.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(recovered.Success);
        Assert.IsType<PythonTupleValue>(Global(engine, "target"));
        Assert.Same(originalSource, error.UnicodeErrorState.Object);
    }

    [Theory]
    [InlineData("backslashreplace", "'😀' * 1000001", false, 1000000)]
    [InlineData("xmlcharrefreplace", "'😀' * 1111112", false, 1111111)]
    [InlineData("namereplace", "chr(0xfbf9) * 114943", false, 114942)]
    [InlineData("surrogatepass", "chr(0xd800) * 3333334", false, 3333333)]
    [InlineData("backslashreplace", "b'\\xff' * 2500001", true, 2500000)]
    public void ExpansionCapPreservesStateAndAcceptsLargestFittingRange(
        string name,
        string source,
        bool decode,
        int fittingEnd
    )
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        Assert.True(
            engine
                .Execute(
                    "import codecs\nhandler = codecs.lookup_error('"
                        + name
                        + "')\nsource = "
                        + source
                        + "\nerror = "
                        + (decode ? "UnicodeDecodeError" : "UnicodeEncodeError")
                        + "('utf8',source,0,len(source),'bad')\ntarget = 'unchanged'",
                    "codecs_cap_setup.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        var original = Global(engine, "target");
        var error = Assert.IsType<PythonExceptionValue>(Global(engine, "error"));
        var originalSource = error.UnicodeErrorState!.Object;
        Assert.True(
            engine
                .Execute(
                    "try: target = handler(error)\nexcept OverflowError as e: print(str(e))",
                    "codecs_cap.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.Contains(
            "managed materialization limit",
            output.ToString(),
            StringComparison.Ordinal
        );
        Assert.Same(original, Global(engine, "target"));
        Assert.Same(originalSource, error.UnicodeErrorState.Object);
        Assert.True(
            engine
                .Execute(
                    "error.end = " + fittingEnd + "\ntarget = handler(error)\nprint(target[1])",
                    "codecs_cap_recovery.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.EndsWith(
            fittingEnd + Environment.NewLine,
            output.ToString(),
            StringComparison.Ordinal
        );
        Assert.Same(originalSource, error.UnicodeErrorState.Object);
    }

    [Fact]
    public void ModuleExportsAndLookupTablesAreOwnedByEachEngine()
    {
        var first = new ManagedPythonEngine();
        var second = new ManagedPythonEngine();
        using var output = new StringWriter();
        Assert.True(
            first
                .Execute(
                    "import codecs\nhandler = codecs.lookup_error('replace')\ncodecs.replace_errors = None",
                    "codecs_first.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.True(
            second
                .Execute(
                    "import codecs\nhandler = codecs.replace_errors\nprint(handler is codecs.lookup_error('replace'))",
                    "codecs_second.py",
                    output,
                    cancellationToken: TestContext.Current.CancellationToken
                )
                .Success
        );
        Assert.Equal("True" + Environment.NewLine, output.ToString());
        Assert.NotSame(Global(first, "handler"), Global(second, "handler"));
    }

    private static PythonValue Global(ManagedPythonEngine engine, string name)
    {
        var globals = Assert.IsType<PythonGlobalNamespace>(
            typeof(ManagedPythonEngine)
                .GetField("_globals", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(engine)
        );
        Assert.True(globals.TryGetValue(name, out var value));
        return value;
    }
}
