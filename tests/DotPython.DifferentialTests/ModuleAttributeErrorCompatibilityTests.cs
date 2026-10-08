using Xunit;

namespace DotPython.DifferentialTests;

public sealed class ModuleAttributeErrorCompatibilityTests
{
    [Fact]
    public Task MissingModuleAttributesReportCPythonsWording() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            import itertools

            def probe(label, thunk):
                try:
                    print(label, '->', repr(thunk()))
                except Exception as error:
                    print(label, '->', type(error).__name__, error)

            probe('read', lambda: itertools.nope)
            probe('call', lambda: itertools.nope())
            probe('known', lambda: itertools.chain is not None)

            try:
                del itertools.nope
            except AttributeError as error:
                print('del ->', type(error).__name__, error)

            try:
                from itertools import nope
            except ImportError as error:
                print('from import ->', type(error).__name__, error)

            try:
                import itertools.nope
            except ImportError as error:
                print('import child ->', type(error).__name__, error)

            try:
                import totally_absent
            except ImportError as error:
                print('import root ->', type(error).__name__, error)
            """
        );
}
