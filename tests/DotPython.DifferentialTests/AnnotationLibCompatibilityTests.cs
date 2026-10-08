using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// The `annotationlib` surface that is implemented. The FORWARDREF and STRING formats
/// are a documented gap and are covered by the paired execution tests instead.
/// </summary>
public sealed class AnnotationLibCompatibilityTests
{
    [Fact]
    public Task GetAnnotationsReturnsTheValueMappingForEverySurface() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import annotationlib

            def f(x: int) -> str:
                pass
            print(annotationlib.get_annotations(f))

            def bare(x):
                pass
            class Empty:
                pass
            print(annotationlib.get_annotations(bare), annotationlib.get_annotations(Empty))

            class C:
                y: str
            print(annotationlib.get_annotations(C))

            print(annotationlib.get_annotations(f, format=annotationlib.Format.VALUE))
            print(annotationlib.get_annotations(f, format=1))
            """
        );

    [Fact]
    public Task FormatMembersCarryCpythonValues() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import annotationlib
            print(
                annotationlib.Format.VALUE,
                annotationlib.Format.VALUE_WITH_FAKE_GLOBALS,
                annotationlib.Format.FORWARDREF,
                annotationlib.Format.STRING,
            )
            print(annotationlib.Format.__name__, annotationlib.Format.__module__)
            """
        );

    [Fact]
    public Task TheFakeGlobalsFormatIsRejectedForInternalUseOnly() =>
        CompatibilityOracle.AssertMatchesAsync(
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

    [Fact]
    public Task CallAnnotateFunctionAndClassNamespaceHelpers() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import annotationlib

            def f(x: int) -> str:
                pass
            print(annotationlib.call_annotate_function(f.__annotate__, annotationlib.Format.VALUE))
            print(annotationlib.call_annotate_function(f.__annotate__, 1))

            class C:
                y: str
            print(annotationlib.get_annotate_from_class_namespace(C.__dict__) is not None)
            print(annotationlib.get_annotate_from_class_namespace({}))

            def bare(x):
                pass
            print(annotationlib.get_annotate_from_class_namespace(bare.__dict__) is None)
            """
        );

    [Fact]
    public Task RenderingHelpersMatchCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import annotationlib
            print(annotationlib.type_repr(int), '|', annotationlib.type_repr(3), '|', annotationlib.type_repr(None))
            print(annotationlib.type_repr(str), '|', annotationlib.type_repr('q'))
            print(annotationlib.annotations_to_string({'a': int, 'b': 'q', 'c': None}))
            """
        );
}
