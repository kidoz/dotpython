using Xunit;

namespace DotPython.DifferentialTests;

public sealed class NamespaceCompatibilityTests
{
    [Fact]
    public async Task Vars_UsesTheDictionaryAttributeProtocol()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            class C:
                __dict__ = 42
            print(vars(C()))
            class D:
                @property
                def __dict__(self): return 'custom'
            print(vars(D()))
            class E:
                @property
                def __dict__(self): raise AttributeError('hidden')
            try: vars(E())
            except TypeError as error: print(str(error))
            try: vars(1)
            except TypeError as error: print(str(error))
            """
        );
    }

    [Fact]
    public async Task ModuleNamespaces_AreLiveAndInvalidateCachedLoads()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            state = vars()
            state['injected'] = 42
            print(injected, state is globals(), state is locals())
            def read(): return injected
            for i in range(20): read()
            state.update(injected=99)
            print(read())
            state.pop('injected')
            try: read()
            except NameError: print('deleted')
            state.setdefault('injected', 100)
            print(read())
            state[17] = 'non-string'
            print(globals()[17])
            state['len'] = lambda x: 123
            print(len([]))
            del state['len']
            print(len([]))
            """
        );
    }

    [Fact]
    public async Task InstanceNamespaces_ShareMutationsAndSupportReplacement()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            class C: pass
            c = C()
            c.x = 1
            state = vars(c)
            print(state is vars(c), state is c.__dict__)
            state['x'] = 2
            print(c.x)
            c.x = 3
            print(state['x'])
            state.update(y=4)
            print(c.y, state.pop('y'), hasattr(c, 'y'))
            state[17] = 'extra'
            print(c.__dict__[17])
            c.__dict__ = {'z': 9}
            print(c.z, hasattr(c, 'x'), state['x'], vars(c) is state)
            c.__dict__.clear()
            print(hasattr(c, 'z'))
            c.retained = 11
            previous = vars(c)
            del c.__dict__
            print(vars(c), previous['retained'], hasattr(c, 'retained'))
            """
        );
    }

    [Fact]
    public async Task FunctionNamespaces_AreSnapshotsAndIncludeFreeVariables()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            outer_value = 5
            def empty(): return len(locals())
            print(empty())
            def outer():
                captured = 8
                def inner(arg):
                    print(captured)
                    first = locals()
                    first['arg'] = 90
                    second = locals()
                    print(arg, second['arg'], first is second, second['captured'])
                    print(globals()['outer_value'])
                    return vars()['arg']
                return inner(7)
            print(outer())
            class C:
                def method(self):
                    print(__class__ is C, locals()['__class__'] is C)
            C().method()
            """
        );
    }

    [Fact]
    public async Task ClassBodyNamespaces_AreLiveAndDistinctFromGlobals()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            class C:
                state = locals()
                state['value'] = 8
                print(value, state is vars(), state is globals())
                value = 9
                print(state['value'])
            print(C.value)
            """
        );
    }

    [Fact]
    public async Task ModuleAndExceptionDictionaries_AreLive()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            import math
            state = vars(math)
            print(state is math.__dict__)
            state['custom_value'] = 7
            print(math.custom_value)
            math.custom_value = 8
            print(state['custom_value'])
            del state['custom_value']
            print(hasattr(math, 'custom_value'))
            error = ValueError('bad')
            state = vars(error)
            state['tag'] = 4
            print(error.tag, state is error.__dict__)
            error.__dict__ = {'replacement': 9}
            print(error.replacement, hasattr(error, 'tag'), state['tag'])
            try: del error.__dict__
            except TypeError as caught: print(str(caught))
            try: math.__dict__ = {}
            except AttributeError as caught: print(str(caught))
            try: del math.__dict__
            except AttributeError as caught: print(str(caught))
            """
        );
    }

    [Fact]
    public async Task SuspendedFrameLocals_KeepFunctionSnapshotSemantics()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            def generate():
                x = 1
                state = locals()
                yield state['x']
                state['x'] = 9
                x = 2
                yield (x, locals()['x'], state['x'])
            print(list(generate()))
            async def run():
                x = 3
                snapshot = locals()
                snapshot['x'] = 10
                return x
            c = run()
            try: c.send(None)
            except StopIteration as stop: print(stop.value)
            """
        );
    }

    [Fact]
    public async Task NamespaceIteration_DetectsAttributeSizeChanges()
    {
        await CompatibilityOracle.AssertMatchesAsync(
            """
            class C: pass
            c = C()
            c.a = 1
            c.b = 2
            iterator = iter(vars(c))
            print(next(iterator))
            c.c = 3
            try: next(iterator)
            except RuntimeError as error: print(type(error).__name__)
            """
        );
    }
}
