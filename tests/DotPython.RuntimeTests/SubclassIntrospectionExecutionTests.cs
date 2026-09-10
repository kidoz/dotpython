using System.Runtime.CompilerServices;
using DotPython.Language.Text;
using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class SubclassIntrospectionExecutionTests
{
    [Fact]
    public void TemplateTypesExistAtStartupWhileFutureFeatureVisibilityIsEngineLocal()
    {
        var first = new ManagedPythonEngine();
        var second = new ManagedPythonEngine();
        const string inspect = """
            print([cls.__name__ for cls in object.__subclasses__()
                   if cls.__name__ in ('Template', 'Interpolation', '_Feature')])
            """;
        using var firstOutput = new StringWriter();
        var before = first.Execute(
            inspect,
            "before_imports.py",
            firstOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(before.Success);
        Assert.Equal($"['Interpolation', 'Template']{Environment.NewLine}", firstOutput.ToString());
        var imported = first.Execute(
            """
            from string.templatelib import Template, Interpolation
            import __future__
            feature_type = type(__future__.annotations)
            print(Template in object.__subclasses__(), Interpolation in object.__subclasses__(),
                  feature_type in object.__subclasses__())
            """,
            "import_types.py",
            firstOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(imported.Success);
        Assert.Equal(
            $"['Interpolation', 'Template']{Environment.NewLine}True True True{Environment.NewLine}",
            firstOutput.ToString()
        );

        using var secondOutput = new StringWriter();
        var isolated = second.Execute(
            inspect,
            "other_engine.py",
            secondOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(isolated.Success);
        Assert.Equal(
            $"['Interpolation', 'Template']{Environment.NewLine}",
            secondOutput.ToString()
        );
        var ownImport = second.Execute(
            "import __future__\n" + inspect,
            "second_import.py",
            secondOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(ownImport.Success);
        Assert.Equal(
            $"['Interpolation', 'Template']{Environment.NewLine}['Interpolation', 'Template', '_Feature']{Environment.NewLine}",
            secondOutput.ToString()
        );
    }

    [Fact]
    public void SharedManagedParentsExposeIntrinsicLinksFromEitherQueryEngine()
    {
        var first = new ManagedPythonEngine();
        var second = new ManagedPythonEngine();
        using var output = new StringWriter();
        var firstSetup = first.Execute(
            """
            class SharedRoot: pass
            class FirstChild(SharedRoot): pass
            def get_root(): return SharedRoot
            def get_children(parent): return type.__subclasses__(parent)
            """,
            "first.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var secondSetup = second.Execute(
            """
            def add_child(parent):
                class SecondChild(parent): pass
                return SecondChild
            def get_children(parent): return type.__subclasses__(parent)
            """,
            "second.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(firstSetup.Success);
        Assert.True(secondSetup.Success);
        var options = new ManagedExecutionOptions();
        var root = first.Invoke(
            "get_root",
            [],
            output,
            options,
            TestContext.Current.CancellationToken
        );
        var child = second.Invoke(
            "add_child",
            [root],
            output,
            options,
            TestContext.Current.CancellationToken
        );
        var firstSnapshot = Assert.IsType<PythonListValue>(
            first.Invoke(
                "get_children",
                [root],
                output,
                options,
                TestContext.Current.CancellationToken
            )
        );
        var secondSnapshot = Assert.IsType<PythonListValue>(
            second.Invoke(
                "get_children",
                [root],
                output,
                options,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(2, firstSnapshot.Elements.Count);
        Assert.Equal(2, secondSnapshot.Elements.Count);
        Assert.Same(firstSnapshot.Elements[0], secondSnapshot.Elements[0]);
        Assert.Same(child, firstSnapshot.Elements[1]);
        Assert.Same(child, secondSnapshot.Elements[1]);
        Assert.NotSame(firstSnapshot, secondSnapshot);
    }

    [Fact]
    public void BuiltinSubclassRegistrationsRemainPrivateToEachEngine()
    {
        var first = new ManagedPythonEngine();
        var second = new ManagedPythonEngine();
        using var firstOutput = new StringWriter();
        using var secondOutput = new StringWriter();
        var created = first.Execute(
            """
            class SubclassIsolationOrdinary: pass
            class SubclassIsolationMeta(type): pass
            print(SubclassIsolationOrdinary in object.__subclasses__())
            print(SubclassIsolationMeta in type.__subclasses__(type))
            """,
            "first.py",
            firstOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(created.Success);
        Assert.Equal($"True{Environment.NewLine}True{Environment.NewLine}", firstOutput.ToString());

        var isolated = second.Execute(
            """
            print([cls.__name__ for cls in object.__subclasses__()
                   if cls.__name__ == 'SubclassIsolationOrdinary'])
            print([cls.__name__ for cls in type.__subclasses__(type)
                   if cls.__name__ == 'SubclassIsolationMeta'])
            """,
            "second.py",
            secondOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(isolated.Success);
        Assert.Equal($"[]{Environment.NewLine}[]{Environment.NewLine}", secondOutput.ToString());
    }

    [Fact]
    public void SavedSubclassMethodsUseCurrentExecutionAndObserveNewClasses()
    {
        var engine = new ManagedPythonEngine();
        using var firstOutput = new StringWriter();
        var first = engine.Execute(
            """
            class Meta(type):
                def __subclasses__(cls):
                    print('list', cls.__name__)
                    return super().__subclasses__()
            class Root(metaclass=Meta): pass
            class First(Root): pass
            saved_override = Root.__subclasses__
            saved_builtin = type.__dict__['__subclasses__'].__get__(Root, type)
            saved_object = object.__subclasses__
            print(saved_override() == [First])
            """,
            "before.py",
            firstOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(first.Success);
        Assert.Equal(
            $"list Root{Environment.NewLine}True{Environment.NewLine}",
            firstOutput.ToString()
        );
        var previousOutput = firstOutput.ToString();
        using var secondOutput = new StringWriter();
        var second = engine.Execute(
            """
            class Second(Root): pass
            class NewOrdinary: pass
            print(saved_override() == [First, Second])
            print(saved_builtin() == [First, Second])
            print(NewOrdinary in saved_object())
            """,
            "after.py",
            secondOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(second.Success);
        Assert.Equal(previousOutput, firstOutput.ToString());
        Assert.Equal(
            $"list Root{Environment.NewLine}True{Environment.NewLine}True{Environment.NewLine}True{Environment.NewLine}",
            secondOutput.ToString()
        );
    }

    [Fact]
    public void ImportedModuleClassesShareTheEngineGraphWithoutDuplicateRegistrations()
    {
        var sources = new Dictionary<string, SourceText>(StringComparer.Ordinal)
        {
            ["family"] = new("class Root: pass\nclass ImportedChild(Root): pass", "family.py"),
        };
        var first = new ManagedPythonEngine(sources);
        var second = new ManagedPythonEngine(sources);
        using var firstOutput = new StringWriter();
        using var secondOutput = new StringWriter();
        var created = first.Execute(
            """
            import family
            class LocalChild(family.Root): pass
            import family as again
            print(family is again)
            print(family.Root.__subclasses__() == [family.ImportedChild, LocalChild])
            print(family.Root in object.__subclasses__())
            """,
            "first.py",
            firstOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(created.Success);
        Assert.Equal(
            $"True{Environment.NewLine}True{Environment.NewLine}True{Environment.NewLine}",
            firstOutput.ToString()
        );

        var isolated = second.Execute(
            """
            import family
            print(family.Root.__subclasses__() == [family.ImportedChild])
            print([cls.__name__ for cls in object.__subclasses__() if cls.__module__ == 'family'])
            """,
            "second.py",
            secondOutput,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(isolated.Success);
        Assert.Equal(
            $"True{Environment.NewLine}['Root']{Environment.NewLine}",
            secondOutput.ToString()
        );
    }

    [Fact]
    public void SubclassRegistryDoesNotKeepOtherwiseUnreachableClassesAlive()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var setup = engine.Execute(
            """
            class Root: pass
            def make_child():
                class Temporary(Root): pass
                return Temporary
            """,
            "weak_subclass.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        var reference = CreateWeakChild(engine);

        CollectUnreachableClasses();

        Assert.False(reference.IsAlive);
        var result = engine.Execute(
            "print(Root.__subclasses__() == [])",
            "after_collection.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal($"True{Environment.NewLine}", output.ToString());
        GC.KeepAlive(engine);
    }

    [Fact]
    public void ReturnedSubclassSnapshotsKeepClassesAliveUntilTheSnapshotIsReleased()
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var setup = engine.Execute(
            """
            class Root: pass
            snapshot = None
            def make_child():
                global snapshot
                class Temporary(Root): pass
                snapshot = Root.__subclasses__()
                return Temporary
            """,
            "retained_subclass.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(setup.Success);
        var reference = CreateWeakChild(engine);
        CollectUnreachableClasses();
        Assert.True(reference.IsAlive);
        var released = engine.Execute(
            "snapshot = None",
            "release_snapshot.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(released.Success);

        CollectUnreachableClasses();

        Assert.False(reference.IsAlive);
        var result = engine.Execute(
            "print(Root.__subclasses__() == [])",
            "after_collection.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(result.Success);
        Assert.Equal($"True{Environment.NewLine}", output.ToString());
        GC.KeepAlive(engine);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateWeakChild(ManagedPythonEngine engine)
    {
        var child = engine.Invoke(
            "make_child",
            [],
            TextWriter.Null,
            new ManagedExecutionOptions(),
            TestContext.Current.CancellationToken
        );
        return new WeakReference(child);
    }

    private static void CollectUnreachableClasses()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
