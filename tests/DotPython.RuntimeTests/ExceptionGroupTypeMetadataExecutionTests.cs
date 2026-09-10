using DotPython.Language.Text;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ExceptionGroupTypeMetadataExecutionTests
{
    [Fact]
    public void CanonicalExceptionGroupTypeAndItsMutationsAreEngineLocal()
    {
        var first = new ManagedPythonEngine();
        var second = new ManagedPythonEngine();
        using var firstOutput = new StringWriter();
        using var secondOutput = new StringWriter();
        var firstResult = first.Execute(
            """
            def canonical(): return ExceptionGroup
            ExceptionGroup.__name__ = 'Renamed'
            ExceptionGroup.__module__ = 'custom'
            ExceptionGroup.custom = 7
            ExceptionGroup.__bases__ = (BaseExceptionGroup,)
            print(ExceptionGroup.__name__, ExceptionGroup.custom, issubclass(ExceptionGroup, Exception))
            """,
            "first.py",
            firstOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var secondResult = second.Execute(
            """
            def canonical(): return ExceptionGroup
            print(ExceptionGroup.__name__, ExceptionGroup.__module__, hasattr(ExceptionGroup, 'custom'))
            print(ExceptionGroup.__bases__ == (BaseExceptionGroup, Exception), ExceptionGroup.__doc__ is None)
            """,
            "second.py",
            secondOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(firstResult.Success);
        Assert.True(secondResult.Success);
        Assert.Equal($"Renamed 7 False{Environment.NewLine}", firstOutput.ToString());
        Assert.Equal(
            $"ExceptionGroup builtins False{Environment.NewLine}True True{Environment.NewLine}",
            secondOutput.ToString()
        );
        Assert.NotSame(
            first.Invoke(
                "canonical",
                [],
                TextWriter.Null,
                new(),
                TestContext.Current.CancellationToken
            ),
            second.Invoke(
                "canonical",
                [],
                TextWriter.Null,
                new(),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Fact]
    public void SavedInstancesAndFactoriesObserveCanonicalMetadataAcrossExecutions()
    {
        var engine = new ManagedPythonEngine();
        using var firstOutput = new StringWriter();
        var first = engine.Execute(
            """
            canonical = ExceptionGroup
            existing = ExceptionGroup('message', [ValueError('value')])
            saved_factory = BaseExceptionGroup
            namespace = ExceptionGroup.__dict__
            """,
            "before.py",
            firstOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(first.Success);
        using var secondOutput = new StringWriter();
        var second = engine.Execute(
            """
            ExceptionGroup.__name__ = 'Renamed'
            ExceptionGroup.__qualname__ = 'Stored'
            ExceptionGroup.__module__ = 'custom'
            ExceptionGroup.__doc__ = 'documentation'
            ExceptionGroup.__bases__ = (BaseExceptionGroup,)
            print(repr(ExceptionGroup), repr(existing))
            print(type(existing) is canonical, isinstance(existing, Exception))
            print(type(saved_factory('promoted', [ValueError()])) is canonical)
            print(namespace['__doc__'], namespace['__module__'])
            """,
            "after.py",
            secondOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(second.Success);
        Assert.Equal(string.Empty, firstOutput.ToString());
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "<class 'custom.Stored'> Renamed('message', [ValueError('value')])",
                "True False",
                "True",
                "documentation custom",
                ""
            ),
            secondOutput.ToString()
        );
    }

    [Fact]
    public void ImportedModulesUseTheirEngineCanonicalExceptionGroup()
    {
        var sources = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["groups"] = new(
                "canonical = ExceptionGroup\ngroup = ExceptionGroup('module', [ValueError('value')])",
                "groups.py"
            ),
        };
        var first = new ManagedPythonEngine(sources);
        var second = new ManagedPythonEngine(sources);
        using var firstOutput = new StringWriter();
        using var secondOutput = new StringWriter();
        var firstResult = first.Execute(
            """
            ExceptionGroup.__name__ = 'Renamed'
            import groups
            print(groups.canonical is ExceptionGroup, type(groups.group) is ExceptionGroup)
            print(repr(groups.group))
            """,
            "first.py",
            firstOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var secondResult = second.Execute(
            """
            import groups
            print(groups.canonical is ExceptionGroup, type(groups.group) is ExceptionGroup)
            print(repr(groups.group))
            """,
            "second.py",
            secondOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(firstResult.Success);
        Assert.True(secondResult.Success);
        Assert.Equal(
            $"True True{Environment.NewLine}Renamed('module', [ValueError('value')]){Environment.NewLine}",
            firstOutput.ToString()
        );
        Assert.Equal(
            $"True True{Environment.NewLine}ExceptionGroup('module', [ValueError('value')]){Environment.NewLine}",
            secondOutput.ToString()
        );
    }

    [Fact]
    public void DescendantBudgetFailureRestoresTheCanonicalGroupForLaterExecution()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            """
            active = False
            class Meta(type):
                def mro(cls):
                    if active:
                        try:
                            while True: pass
                        finally:
                            print('inner cleanup')
                    return type.mro(cls)
            class Child(ExceptionGroup, metaclass=Meta): pass
            old_bases = ExceptionGroup.__bases__
            old_mro = ExceptionGroup.__mro__
            old_child = Child.__mro__
            active = True
            try:
                ExceptionGroup.__bases__ = (BaseExceptionGroup,)
            finally:
                print('outer cleanup')
            """,
            "group_metadata_budget.py",
            output,
            new ManagedExecutionOptions { InstructionLimit = 1000 },
            TestContext.Current.CancellationToken
        );
        Assert.Equal("DPY4001", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(
            $"inner cleanup{Environment.NewLine}outer cleanup{Environment.NewLine}",
            output.ToString()
        );
        using var subsequentOutput = new StringWriter();
        var subsequent = engine.Execute(
            """
            active = False
            print(ExceptionGroup.__bases__ is old_bases, ExceptionGroup.__mro__ is old_mro,
                  Child.__mro__ is old_child)
            print(type(BaseExceptionGroup('message', [ValueError()])) is ExceptionGroup)
            print(isinstance(Child('message', [ValueError()]), Exception))
            """,
            "after.py",
            subsequentOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(subsequent.Success);
        Assert.Equal(
            $"True True True{Environment.NewLine}True{Environment.NewLine}True{Environment.NewLine}",
            subsequentOutput.ToString()
        );
    }
}
