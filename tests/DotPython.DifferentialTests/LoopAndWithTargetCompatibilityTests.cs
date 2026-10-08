using Xunit;

namespace DotPython.DifferentialTests;

/// <summary>
/// Two target forms ordinary modern source uses: a parenthesized list of context
/// managers, and attribute or subscript loop targets.
/// </summary>
public sealed class LoopAndWithTargetCompatibilityTests
{
    [Fact]
    public Task ParenthesizedContextManagersBindEveryItem() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class C:
                def __init__(self, n): self.n = n
                def __enter__(self): print('enter', self.n); return self
                def __exit__(self, *a): print('exit', self.n)

            with (C(1) as a, C(2) as b):
                print('body', a.n, b.n)

            with (C(3), C(4)):
                print('tuple form')

            with (C(5)):
                print('single item')

            with (C(6) as x,):
                print('trailing comma', x.n)

            with ((C(7))):
                print('nested parens')

            with C(8) as y, C(9) as z:
                print('unparenthesized still works', y.n, z.n)
            """
        );

    [Fact]
    public Task ParenthesizedContextManagersPreserveExitOrder() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class C:
                def __init__(self, n): self.n = n
                def __enter__(self):
                    events.append(('enter', self.n))
                    return self
                def __exit__(self, *a):
                    events.append(('exit', self.n))

            with (C(1) as a, C(2) as b):
                events.append(('body',))

            print(events)
            """
        );

    [Fact]
    public Task AttributeAndSubscriptLoopTargetsAssign() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class C: pass
            class D: pass

            holder = C()
            for holder.x in [1, 2]:
                pass
            print(holder.x)

            sequence = [0, 0]
            for sequence[0] in [7]:
                pass
            print(sequence)

            outer = D()
            outer.inner = C()
            for outer.inner.x in [5]:
                pass
            print(outer.inner.x)

            for outer.inner.x, sequence[1] in [(8, 9)]:
                pass
            print(outer.inner.x, sequence)

            print([0 for holder.x in [1, 2]])
            print(holder.x)
            """
        );
}
