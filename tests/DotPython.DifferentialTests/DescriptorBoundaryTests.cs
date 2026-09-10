using Xunit;

namespace DotPython.DifferentialTests;

public sealed class DescriptorBoundaryTests
{
    [Fact]
    public Task CallableDescriptorsBindForDirectKeywordAndNestedCalls() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Call:
                def __get__(self, obj, owner):
                    events.append(owner.__name__)
                    return lambda value=2: value + 40
            class C: __call__ = Call()
            c = C()
            c.__call__ = lambda: -1
            print(callable(c), events)
            print(c(), c(value=3), list(map(c, [4])))
            print(events)
            C.__call__ = None
            print(callable(c))
            try: c()
            except TypeError: print('not callable')
            """
        );

    [Fact]
    public Task ExceptionDescriptorAttributeErrorInvokesFallback() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class D:
                def __get__(self, obj, owner): raise AttributeError('hidden')
                def __set__(self, obj, value): pass
            class E(ValueError):
                value = D()
                def __getattr__(self, name): return 'fallback:' + name
            error = E('message')
            error.__dict__['value'] = 1
            print(error.value, error.other, error.args)
            """
        );

    [Fact]
    public Task ExceptionClassesAndExceptionDescriptorsUseTheSameProtocol() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Field(ValueError):
                def __set_name__(self, owner, name):
                    self.name = name
                    events.append((owner.__name__, name))
                def __get__(self, obj, owner):
                    if obj is None: return self
                    return obj.__dict__.get('_stored', 0)
                def __set__(self, obj, value): obj.__dict__['_stored'] = value
                def __delete__(self, obj): del obj.__dict__['_stored']
            class Error(ValueError): value = Field()
            class Derived(Error):
                def base_value(self): return super().value
            error = Derived('message')
            error.__dict__['value'] = 100
            print(events, error.value, Error.value.name)
            error.value = 7
            print(error.value, error.base_value(), error.args)
            del error.value
            print(error.value, error.__dict__['value'])
            """
        );

    [Fact]
    public Task RecursiveDescriptorFailsWithPythonRecursionErrorAndUnwinds() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class D:
                def __get__(self, obj, owner): return obj.value
            class C: value = D()
            for i in range(2):
                try: C().value
                except RecursionError: print('recursion caught')
            print('still running')
            """
        );
}
