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
    public void TheForwardRefAndStringFormatsAreComputed()
    {
        // Compiled bodies answer the value format only; the library falls back to the
        // value mapping for FORWARDREF and stringifies it for STRING.
        var output = Run(
            """
            import annotationlib

            def f(x: int) -> str:
                pass
            print(annotationlib.get_annotations(f, format=annotationlib.Format.FORWARDREF))
            print(annotationlib.get_annotations(f, format=annotationlib.Format.STRING))
            """
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "{'x': <class 'int'>, 'return': <class 'str'>}",
                "{'x': 'int', 'return': 'str'}",
                ""
            ),
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

    /// <summary>
    /// `get_annotations` copies the stored mapping and refuses a class whose stored
    /// value is neither a dict nor None, matching CPython's descriptor-path wording.
    /// </summary>
    [Fact]
    public void GetAnnotationsReturnsAFreshMapping()
    {
        var output = Run(
            """"
            import annotationlib
            def f(x: int): pass
            a = annotationlib.get_annotations(f)
            print(a is f.__annotations__, a == f.__annotations__)
            class C: pass
            C.__annotations__ = 5
            try:
                annotationlib.get_annotations(C)
            except ValueError as e:
                print(type(e).__name__, e)
            C.__annotations__ = None
            try:
                annotationlib.get_annotations(C)
            except ValueError as e:
                print(type(e).__name__, e)
            class D: pass
            d = annotationlib.get_annotations(D)
            print(d is D.__annotations__, d)
            """"
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "False True",
                "ValueError <class '__main__.C'>.__annotations__ is neither a dict nor None",
                "ValueError <class '__main__.C'>.__annotations__ is neither a dict nor None",
                "False {}",
                ""
            ),
            output
        );
    }

    /// <summary>
    /// `annotationlib.get_annotations`/`call_annotate_function` with the FORWARDREF and
    /// STRING formats, `eval_str`, and the `ForwardRef` class - the compiled bodies only
    /// answer VALUE, so the runtime takes CPython's own fallback path.
    /// </summary>
    [Fact]
    public void TheAnnotationFormatsMatchPython()
    {
        var output = Run(
            """"
            import annotationlib as al, typing
            def show(l, f):
                try: print(l, "ok", repr(f()))
                except Exception as e: print(l, type(e).__name__, e)
            def f(x: int, y: "Und", z: list[int]) -> str: pass
            for fmt in (1, 2, 3, 4):
                show("ann-%d" % fmt, lambda fmt=fmt: f.__annotate__(fmt))
                show("get-%d" % fmt, lambda fmt=fmt: al.get_annotations(f, format=fmt))
            class C:
                a: int
                b: "Und"
            show("class-str", lambda: al.get_annotations(C, format=al.Format.STRING))
            show("class-fwd", lambda: al.get_annotations(C, format=al.Format.FORWARDREF))
            def g(x: int): pass
            show("call-3", lambda: al.call_annotate_function(g.__annotate__, al.Format.FORWARDREF))
            show("call-4", lambda: al.call_annotate_function(g.__annotate__, al.Format.STRING))
            show("eval-str-4", lambda: al.get_annotations(f, format=al.Format.STRING, eval_str=True))
            def k(x: "int") -> "str": pass
            show("eval-str-1", lambda: al.get_annotations(k, format=al.Format.VALUE, eval_str=True))
            fr = al.ForwardRef("X.Y")
            print("fr", repr(fr), fr.__forward_arg__, fr == al.ForwardRef("X.Y"), fr == al.ForwardRef("Z"))
            show("fr-int", lambda: al.ForwardRef(5))
            show("fr-missing", lambda: al.ForwardRef())
            show("fr-eval-g", lambda: al.ForwardRef("X").evaluate(globals={"X": int}))
            show("fr-eval-bad", lambda: al.ForwardRef("X.Y").evaluate(globals={"X": typing}))
            show("fr-eval-pos", lambda: al.ForwardRef("X").evaluate({}))
            show("annstr", lambda: al.annotations_to_string({"x": int, "y": list[int], "z": "Und", "w": 3, "u": None}))
            """"
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "ann-1 ok {'x': <class 'int'>, 'y': 'Und', 'z': list[int], 'return': <class 'str'>}",
                "get-1 ok {'x': <class 'int'>, 'y': 'Und', 'z': list[int], 'return': <class 'str'>}",
                "ann-2 ok {'x': <class 'int'>, 'y': 'Und', 'z': list[int], 'return': <class 'str'>}",
                "get-2 ValueError The VALUE_WITH_FAKE_GLOBALS format is for internal use only",
                "ann-3 NotImplementedError ",
                "get-3 ok {'x': <class 'int'>, 'y': 'Und', 'z': list[int], 'return': <class 'str'>}",
                "ann-4 NotImplementedError ",
                "get-4 ok {'x': 'int', 'y': 'Und', 'z': 'list[int]', 'return': 'str'}",
                "class-str ok {'a': 'int', 'b': 'Und'}",
                "class-fwd ok {'a': <class 'int'>, 'b': 'Und'}",
                "call-3 ok {'x': <class 'int'>}",
                "call-4 ok {'x': 'int'}",
                "eval-str-4 ValueError eval_str=True is only supported with format=Format.VALUE",
                "eval-str-1 ok {'x': <class 'int'>, 'return': <class 'str'>}",
                "fr ForwardRef('X.Y') X.Y True False",
                "fr-int TypeError Forward reference must be a string -- got 5",
                "fr-missing TypeError ForwardRef.__init__() missing 1 required positional argument: 'arg'",
                "fr-eval-g ok <class 'int'>",
                "fr-eval-bad AttributeError module 'typing' has no attribute 'Y'",
                "fr-eval-pos TypeError ForwardRef.evaluate() takes 1 positional argument but 2 were given",
                "annstr ok {'x': 'int', 'y': 'list[int]', 'z': 'Und', 'w': '3', 'u': 'None'}",
                ""
            ),
            output
        );
    }

    /// <summary>
    /// The recorded gap: an annotation the value body cannot evaluate still raises
    /// NameError, where CPython's re-execution trick builds a ForwardRef or a source
    /// string; the runtime's eval also words NameError its own way.
    /// </summary>
    [Fact]
    public void TheRuntimePinsItsAnnotationFormatGaps()
    {
        var output = Run(
            """"
            import annotationlib as al
            def show(l, f):
                try: print(l, "ok", repr(f()))
                except Exception as e: print(l, type(e).__name__, e)
            def f(x: "Und"): pass
            show("eval-str-undefined", lambda: al.get_annotations(f, format=al.Format.VALUE, eval_str=True))
            def h(x: Undefined): pass
            show("fwd-undefined", lambda: al.get_annotations(h, format=al.Format.FORWARDREF))
            show("str-undefined", lambda: al.get_annotations(h, format=al.Format.STRING))
            show("fr-eval-none", lambda: al.ForwardRef("X").evaluate())
            """"
        );

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "eval-str-undefined NameError Name 'Und' is not defined.",
                "fwd-undefined NameError Name 'Undefined' is not defined.",
                "str-undefined NameError Name 'Undefined' is not defined.",
                "fr-eval-none NameError Name 'X' is not defined.",
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
            "annotation_lib_execution.py",
            output,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(result.Success);
        return output.ToString();
    }
}
