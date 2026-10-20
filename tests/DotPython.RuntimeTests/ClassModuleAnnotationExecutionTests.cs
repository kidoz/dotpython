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

    /// <summary>
    /// Classes and modules answer `__annotations__` and `__annotate__` assignments with
    /// CPython's rules: the annotate body is cleared, the cache is kept when it is set
    /// to None, and a module whose stored value is not a dict is refused by the two
    /// introspection entry points.
    /// </summary>
    [Fact]
    public void ClassesAndModulesExposeTheSameWritableAnnotations()
    {
        var output = Run(
            """"
            import math, annotationlib, typing
            class C:
                y: int
            print(C.__annotations__, C.__annotate__ is None)
            C.__annotations__ = {"z": 2}
            print(C.__annotations__, C.__annotate__)
            C.__annotations__ = None
            print(C.__annotations__, type(C.__annotations__).__name__)
            del C.__annotations__
            print(C.__annotations__, C.__annotate__)
            class D: pass
            D.__annotate__ = lambda fmt: {"w": 1}
            print(D.__annotations__)
            D.__annotate__ = None
            print(D.__annotations__, D.__annotate__)
            try:
                D.__annotate__ = 5
            except TypeError as e:
                print(e)
            try:
                del D.__annotate__
            except TypeError as e:
                print(e)
            m = math
            print(hasattr(m, "__annotations__"), hasattr(m, "__annotate__"))
            print(m.__annotations__)
            m.__annotate__ = lambda fmt: {"a": 1}
            print(m.__annotations__)
            m.__annotations__ = {"b": 2}
            print(m.__annotations__, m.__annotate__)
            del m.__annotations__
            print(m.__annotations__, m.__annotate__)
            try:
                m.__annotate__ = 5
            except TypeError as e:
                print(e)
            try:
                del m.__annotate__
            except TypeError as e:
                print(e)
            m.__annotations__ = 5
            for label, fn in (("alib", annotationlib.get_annotations), ("hints", typing.get_type_hints)):
                try:
                    fn(m)
                except Exception as e:
                    print(label, type(e).__name__, e)
            class E: pass
            E.__annotations__ = 5
            for label, fn in (("alib", annotationlib.get_annotations), ("hints", typing.get_type_hints)):
                try:
                    fn(E)
                except Exception as e:
                    print(label, type(e).__name__, e)
            class F: pass
            F.__annotations__ = None
            try:
                annotationlib.get_annotations(F)
            except Exception as e:
                print("alib-none", type(e).__name__, e)
            """"
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "{'y': <class 'int'>} False",
                "{'z': 2} None",
                "None NoneType",
                "{} None",
                "{'w': 1}",
                "{'w': 1} None",
                "__annotate__ must be callable or None",
                "cannot delete __annotate__ attribute",
                "True True",
                "{}",
                "{'a': 1}",
                "{'b': 2} None",
                "{} None",
                "__annotate__ must be callable or None",
                "cannot delete __annotate__ attribute",
                "alib ValueError <module 'math'>.__annotations__ is neither a dict nor None",
                "hints ValueError <module 'math'>.__annotations__ is neither a dict nor None",
                "alib ValueError <class '__main__.E'>.__annotations__ is neither a dict nor None",
                "hints ValueError <class '__main__.E'>.__annotations__ is neither a dict nor None",
                "alib-none ValueError <class '__main__.F'>.__annotations__ is neither a dict nor None",
                ""
            ),
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
