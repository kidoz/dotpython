using Xunit;

namespace DotPython.DifferentialTests;

public sealed class Python314FrontendDifferentialTests
{
    [Fact]
    public Task BareAnnotationsEvaluateTargetsWithoutReadingOrWriting() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Target:
                def __getattr__(self, name):
                    events.append('get:' + name)
                    return self
                def __setattr__(self, name, value):
                    events.append('set:' + name)
                def __getitem__(self, key):
                    events.append('getitem')
                    return self
                def __setitem__(self, key, value):
                    events.append('setitem')
            target = Target()
            def mark(name):
                events.append(name)
                return 1
            def receiver():
                events.append('receiver')
                return target
            receiver().field: mark('annotation')
            receiver()[mark('index')]: mark('annotation')
            receiver()[mark('start'):mark('stop'):mark('step')]: mark('annotation')
            receiver()[mark('first'), mark('second')]: mark('annotation')
            receiver().child.field: mark('annotation')
            receiver()[mark('inner')].field: mark('annotation')
            (missing): mark('annotation')
            def local():
                receiver().field: mark('annotation')
                receiver()[mark('local')]: mark('annotation')
            local()
            class Annotated:
                receiver().field: mark('annotation')
            print(events)
            """
        );

    [Fact]
    public Task ParenthesizedAnnotationsPreserveBindingAndAssignmentSemantics() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            value = 10
            def bare():
                (value): missing_annotation
                return value
            def assigned():
                ((value)): missing_annotation = 20
                return value
            print(bare(), assigned(), value)
            class Target:
                pass
            target = Target()
            (target.field): missing_annotation = 30
            values = [0]
            (values[0]): missing_annotation = 40
            print(target.field, values)
            """
        );

    [Fact]
    public Task BareAnnotationTargetsPropagateErrorsAndPreserveClosureBinding() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            def fail():
                events.append('failed')
                raise ValueError('target failed')
            def outer():
                values = []
                def inner():
                    values[len(values)]: int
                    try:
                        fail()[events.append('index')]: int
                    except ValueError as error:
                        print(str(error))
                return inner
            outer()()
            print(events)
            """
        );

    [Fact]
    public Task UnparenthesizedExceptionListsMatchTheirParenthesizedForms() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for kind in [ValueError, TypeError, KeyError]:
                try:
                    raise kind('example')
                except ValueError, TypeError:
                    print('first handler')
                except KeyError,:
                    print('second handler')
            try:
                raise ExceptionGroup('group', [ValueError('v'), TypeError('t'), KeyError('k')])
            except* ValueError, TypeError:
                print('first subgroup')
            except* KeyError,:
                print('second subgroup')
            """
        );
}
