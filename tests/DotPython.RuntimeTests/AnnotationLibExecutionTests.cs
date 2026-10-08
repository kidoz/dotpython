using DotPython.Runtime.Managed.Execution;
using Xunit;

namespace DotPython.RuntimeTests;

public sealed class AnnotationLibExecutionTests
{
    [Fact]
    public void GetAnnotationsCoversFunctionsClassesAndModules()
    {
        var output = Run(
            """
            import annotationlib

            def f(x: int) -> str:
                pass
            class C:
                y: str
            print(annotationlib.get_annotations(f))
            print(annotationlib.get_annotations(C))

            z: bytes
            print(__annotate__(annotationlib.Format.VALUE))
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "{'x': <class 'int'>, 'return': <class 'str'>}",
                "{'y': <class 'str'>}",
                "{'z': <class 'bytes'>}",
                ""
            ),
            output
        );
    }

    [Fact]
    public void GetAnnotationsOfAnUnannotatedDefinitionIsEmpty()
    {
        var output = Run(
            """
            import annotationlib

            def f(x):
                pass
            class C:
                pass
            print(annotationlib.get_annotations(f), annotationlib.get_annotations(C))
            """
        );

        Assert.Equal($"{{}} {{}}{Environment.NewLine}", output);
    }

    [Fact]
    public void TheFakeGlobalsFormatIsRejected()
    {
        var output = Run(
            """
            import annotationlib

            def f(x: int):
                pass
            try:
                annotationlib.get_annotations(
                    f, format=annotationlib.Format.VALUE_WITH_FAKE_GLOBALS
                )
            except ValueError as error:
                print('ValueError:', error)
            """
        );

        Assert.Equal(
            $"ValueError: The VALUE_WITH_FAKE_GLOBALS format is for internal use only{Environment.NewLine}",
            output
        );
    }

    [Fact]
    public void TheForwardRefAndStringFormatsAreAnExplicitGap()
    {
        // CPython implements these two on top of the annotate function, in Python.
        // Here the call reaches the compiled body, which rejects them the same way
        // CPython's own compiled bodies do.
        var output = Run(
            """
            import annotationlib

            def f(x: int):
                pass
            for format in (annotationlib.Format.FORWARDREF, annotationlib.Format.STRING):
                try:
                    annotationlib.get_annotations(f, format=format)
                    print('computed')
                except NotImplementedError:
                    print(format, 'NotImplementedError')
            """
        );

        Assert.Equal(
            string.Join(Environment.NewLine, "3 NotImplementedError", "4 NotImplementedError", ""),
            output
        );
    }

    [Fact]
    public void RenderingAndNamespaceHelpers()
    {
        var output = Run(
            """
            import annotationlib

            print(annotationlib.type_repr(int), '|', annotationlib.type_repr(3), '|', annotationlib.type_repr(None))
            print(annotationlib.annotations_to_string({'a': int, 'b': 'q', 'c': None}))

            class C:
                y: str
            print(annotationlib.get_annotate_from_class_namespace(C.__dict__) is not None)
            print(annotationlib.get_annotate_from_class_namespace({}))
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "int | 3 | None",
                "{'a': 'int', 'b': 'q', 'c': 'None'}",
                "True",
                "None",
                ""
            ),
            output
        );
    }

    [Fact]
    public void CallAnnotateFunctionRunsTheBodyDirectly()
    {
        var output = Run(
            """
            import annotationlib

            def f(x: int) -> str:
                pass
            print(annotationlib.call_annotate_function(f.__annotate__, annotationlib.Format.VALUE))
            """
        );

        Assert.Equal(
            $@"{{'x': <class 'int'>, 'return': <class 'str'>}}{Environment.NewLine}",
            output
        );
    }

    private static string Run(string source)
    {
        var engine = new ManagedPythonEngine();
        using var output = new StringWriter();
        var result = engine.Execute(
            source,
            "annotation_lib_execution.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        return output.ToString();
    }
}
