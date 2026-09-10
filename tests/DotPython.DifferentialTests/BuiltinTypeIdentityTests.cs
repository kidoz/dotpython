using Xunit;

namespace DotPython.DifferentialTests;

public sealed class BuiltinTypeIdentityTests
{
    [Fact]
    public Task BuiltinAndUserClassesHaveCanonicalTypeIdentityAndHierarchy() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Example: pass
            for cls in [type, object, bool, int, str, Example, Exception, ValueError]:
                print(cls.__name__, type(cls) is type, isinstance(cls, type), isinstance(cls, object))
            print(type(type) is type, type(object()) is object)
            print(isinstance(True, int), isinstance(1, bool), issubclass(bool, int), issubclass(int, bool))
            print(issubclass(type, object), issubclass(object, type), issubclass(Example, object))
            print(tuple(cls.__name__ for cls in bool.__mro__))
            print(tuple(cls.__name__ for cls in type.__mro__))
            print(Example.__bases__ == (object,), Example.__mro__ == (Example, object))
            """
        );

    [Fact]
    public Task ExistingConstructorBuiltinsExposeTypesAndKeepKeywordCalls() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for cls in [range, slice, complex, property, staticmethod, classmethod, enumerate, zip, map, filter, super, reversed]:
                print(cls.__name__, type(cls) is type, isinstance(cls, type), issubclass(cls, object))
            for value, cls in [(range(3), range), (slice(2), slice), (1j, complex),
                    (property(), property), (staticmethod(lambda: 1), staticmethod),
                    (classmethod(lambda cls: 1), classmethod), (enumerate([]), enumerate),
                    (zip(), zip), (map(str, []), map), (filter(None, []), filter)]:
                print(type(value) is cls, isinstance(value, cls))
            print(list(enumerate(['x'], start=4)))
            print(list(zip([1], [2], strict=True)))
            print(list(map(str, [3], strict=True)))
            class Example:
                value = property(fget=lambda self: 7)
            print(Example().value)
            print(type(super(Example, Example())) is super)
            """
        );

    [Fact]
    public Task RuntimeTypeObjectsAreStableAndUsableAsClassInfo() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            def first(): pass
            def second(): pass
            values = [None, Ellipsis, NotImplemented, first, len, iter([]), iter(()), iter(''), iter(range(0))]
            for value in values:
                cls = type(value)
                print(cls.__name__, cls is type(value), type(cls) is type, isinstance(value, cls), issubclass(cls, object))
            print(type(first) is type(second), type(len) is type(print))
            print(type(iter([])) is type(iter(())))
            """
        );

    [Fact]
    public Task DynamicTypesCopyNamespacesAndInitializeDescriptorsAndInstances() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Field:
                def __set_name__(self, owner, name): events.append((owner.__name__, name))
                def __get__(self, obj, owner): return obj._value
            def initialize(self, value): self._value = value
            def read(self): return self.value
            namespace = {'__init__': initialize, 'value': Field(), 'read': read, '__qualname__': 'Outer.Dynamic'}
            Dynamic = type('Dynamic', (), namespace)
            instance = Dynamic(42)
            namespace['read'] = lambda self: -1
            namespace['later'] = 7
            print(type(Dynamic) is type, type(instance) is Dynamic, isinstance(instance, Dynamic))
            print(instance.read(), events, hasattr(Dynamic, 'later'))
            print(Dynamic.__module__, Dynamic.__qualname__, Dynamic.__doc__)
            print('__qualname__' in namespace, '__qualname__' in Dynamic.__dict__)
            print(Dynamic.__bases__ == (object,), Dynamic.__mro__ == (Dynamic, object))
            """
        );

    [Fact]
    public Task DynamicClassesShareInheritanceAndDescriptorFailureBehavior() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Root:
                def answer(self): return 42
            class Left(Root): pass
            class Right(Root): pass
            Dynamic = type('Dynamic', (Left, Right), {})
            print(Dynamic().answer(), issubclass(Dynamic, Root), isinstance(Dynamic(), Right))
            print(tuple(cls.__name__ for cls in Dynamic.__mro__))
            Error = type('Error', (ValueError,), {'kind': 'dynamic'})
            error = Error('message')
            print(type(error) is Error, isinstance(error, ValueError), error.kind, str(error))
            print(tuple(cls.__name__ for cls in Error.__mro__))
            failure = ValueError('hook failed')
            class Field:
                def __set_name__(self, owner, name): raise failure
            try:
                type('Broken', (), {'value': Field()})
            except ValueError as error:
                print(error is failure, error.__notes__)
            print(type('After', (), {})().__class__.__name__)
            """
        );

    [Fact]
    public Task DynamicTypeValidatesArgumentsAndAllowsEmptyNames() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for arguments in [(), (1, 2), (1, (), {}), ('C', [], {}), ('C', (), []),
                    ('C', (object, object), {}), ('C', (), {'__qualname__': 12})]:
                try:
                    type(*arguments)
                except TypeError as error:
                    print(str(error))
            print(repr(type('', (), {}).__name__), repr(type('  ', (), {}).__name__))
            """
        );

    [Fact]
    public Task DynamicTypesPreserveExplicitObjectBasesAndRejectInconsistentOrders() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Base: pass
            Dynamic = type('Dynamic', (Base, object), {})
            print(Dynamic.__bases__ == (Base, object))
            print(Dynamic.__mro__ == (Dynamic, Base, object))
            try:
                type('Broken', (object, Base), {})
            except TypeError:
                print('inconsistent order')
            class Declared(Base, object): pass
            print(Declared.__bases__ == (Base, object))
            print(Declared.__mro__ == (Declared, Base, object))
            """
        );

    [Fact]
    public Task TypeNamesRemainMetadataEvenWhenNamespacesContainNameEntries() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            Dynamic = type('Dynamic', (), {'__name__': 'namespace value', '__qualname__': 'Outer.Dynamic'})
            Child = type('Child', (Dynamic,), {})
            print(Dynamic.__name__, Dynamic.__qualname__, Dynamic.__dict__['__name__'])
            print(Child.__name__, Child.__qualname__)
            Dynamic.__name__ = 'Renamed'
            print(Dynamic.__qualname__, repr(Dynamic))
            Dynamic.__qualname__ = 'Outer.Renamed'
            Dynamic.__module__ = 'example'
            print(Dynamic.__name__, Dynamic.__dict__['__name__'], repr(Dynamic))
            """
        );
}
