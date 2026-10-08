using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class ClassModuleAnnotationExecutionTests
{
    [Fact]
    public void ClassAnnotationsAreRecordedPerClass()
    {
        var output = Run(
            """
            class A:
                x: int
            class B(A):
                y: str
            class U(A):
                pass
            print(A.__annotations__)
            print(B.__annotations__)
            print(U.__annotations__)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "{'x': <class 'int'>}",
                "{'y': <class 'str'>}",
                "{}",
                ""
            ),
            output
        );
    }

    [Fact]
    public void AClassWithoutAnnotationsReportsAnEmptyMappingAndNoAnnotate()
    {
        var output = Run(
            """
            class C:
                pass
            print(C.__annotations__, C.__annotate__ is None, hasattr(C, "__annotations__"))
            """
        );

        Assert.Equal($"{{}} True True{Environment.NewLine}", output);
    }

    [Fact]
    public void ClassAnnotationsSeeClassBodyAndEnclosingNames()
    {
        var output = Run(
            """
            class C:
                T = int
                y: T
            print(C.__annotations__)

            def outer():
                Q = bytes
                class D:
                    z: Q
                return D
            print(outer().__annotations__)

            class E:
                def m(self, a: Later): pass
                Later = str
            print(E.m.__annotations__)
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "{'y': <class 'int'>}",
                "{'z': <class 'bytes'>}",
                "{'a': <class 'str'>}",
                ""
            ),
            output
        );
    }

    [Fact]
    public void ClassAnnotationsAreCachedAndMutable()
    {
        var output = Run(
            """
            class C:
                y: str
            first = C.__annotations__
            print(first is C.__annotations__)
            first['extra'] = 1
            print(C.__annotations__)
            """
        );

        Assert.Equal(
            string.Join(Environment.NewLine, "True", "{'y': <class 'str'>, 'extra': 1}", ""),
            output
        );
    }

    [Fact]
    public void ModuleAnnotationsAreReachableThroughTheAnnotateGlobal()
    {
        var output = Run(
            """
            x: int = 1
            y: str
            print(x)
            print(__annotate__(1))
            """
        );

        Assert.Equal(
            string.Join(Environment.NewLine, "1", "{'x': <class 'int'>, 'y': <class 'str'>}", ""),
            output
        );
    }

    private static string Run(string source)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "class_module_annotation_execution.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        return output.ToString();
    }
}
