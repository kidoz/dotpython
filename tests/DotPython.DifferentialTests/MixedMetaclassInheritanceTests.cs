using Xunit;

namespace DotPython.DifferentialTests;

public sealed class MixedMetaclassInheritanceTests
{
    [Fact]
    public Task BothBaseOrdersPreserveMroButSelectTypeAsTheSolidBase() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Mixin: pass
            class Before(Mixin, type): pass
            class After(type, Mixin): pass
            for meta in (Before, After):
                print(meta.__name__, [base.__name__ for base in meta.__bases__])
                print([base.__name__ for base in meta.__mro__], meta.__base__ is type)
                print(type(meta) is type, issubclass(meta, type), issubclass(meta, Mixin))
                Example = meta('Example', (), {'value': 7})
                print(type(Example) is meta, isinstance(Example, type), isinstance(Example, Mixin))
                print(isinstance(Example, meta), isinstance(Example(), meta), issubclass(Example, Mixin))
                print(Example().value, Example.__base__ is object)
            """
        );

    [Fact]
    public Task DerivedMetaclassesSelectTheInheritedSolidBase() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class First: pass
            class Second: pass
            class Meta(First, type): pass
            class Left(Second, Meta): pass
            class Right(Meta, Second): pass
            for candidate in (Left, Right):
                print([base.__name__ for base in candidate.__mro__])
                print(candidate.__base__ is Meta)
                print([base.__name__ for base in candidate.__bases__])
                class Example(metaclass=candidate): pass
                print(type(Example) is candidate, isinstance(Example(), Example))
            """
        );

    [Fact]
    public Task MixedMetaclassDiamondUsesC3AndVisitsSharedMixinOnce() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Root:
                def __call__(cls):
                    events.append('root')
                    return super().__call__()
            class Left(Root):
                def __call__(cls):
                    events.append('left')
                    return super().__call__()
            class Right(Root):
                def __call__(cls):
                    events.append('right')
                    return super().__call__()
            class Meta(Left, Right, type): pass
            class Example(metaclass=Meta): pass
            print([base.__name__ for base in Meta.__mro__])
            print(type(Example()) is Example, events, Meta.__base__ is type)
            """
        );

    [Fact]
    public Task ConflictingMixinOrdersRejectClassAndDynamicConstruction() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class X: pass
            class Y: pass
            class Left(X, Y): pass
            class Right(Y, X): pass
            try:
                class Broken(Left, Right, type): pass
            except TypeError:
                print('class conflict', 'Broken' in globals())
            try:
                type('Dynamic', (Left, Right, type), {})
            except TypeError:
                print('dynamic conflict')
            class Good(Left, type): pass
            print(Good.__base__ is type)
            """
        );

    [Fact]
    public Task MostDerivedMixedMetaclassWinsSelection() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Mixin: pass
            class Meta(Mixin, type): pass
            class Derived(Meta): pass
            class Left(metaclass=Meta): pass
            class Right(metaclass=Derived): pass
            class Child(Left, Right, metaclass=type): pass
            print(type(Child) is Derived, Child.__bases__ == (Left, Right))
            print(isinstance(Child, Meta), isinstance(Child, Derived), isinstance(Left, Derived))
            print(issubclass(Derived, Meta), issubclass(Meta, Derived), isinstance(Derived, Meta))
            print([base.__name__ for base in Child.__mro__])
            class Other(type, Mixin): pass
            class Foreign(metaclass=Other): pass
            try:
                class Broken(Left, Foreign): pass
            except TypeError:
                print('metaclass conflict', 'Broken' in globals())
            """
        );

    [Fact]
    public Task LeadingMixinCooperativelyHandlesAllFourConstructionHooks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Mixin:
                @classmethod
                def __prepare__(meta, name, bases, **kwargs):
                    events.append(('prepare', meta.__name__, name, kwargs))
                    return super().__prepare__(name, bases, **kwargs)
                def __new__(meta, name, bases, namespace, **kwargs):
                    events.append(('new', meta.__name__, name, kwargs))
                    return super().__new__(meta, name, bases, namespace, **kwargs)
                def __init__(cls, name, bases, namespace, **kwargs):
                    events.append(('init', cls.__name__, name, kwargs))
                    super().__init__(name, bases, namespace, **kwargs)
                def __call__(cls, *args, **kwargs):
                    events.append(('call', cls.__name__, args, kwargs))
                    return super().__call__(*args, **kwargs)
            class Meta(Mixin, type): pass
            class Example(metaclass=Meta):
                def __init__(self, value, *, extra=0): self.value = value + extra
            value = Example(3, extra=4)
            print(value.value, type(value) is Example, events)
            """
        );

    [Fact]
    public Task LeadingBuiltinTypeTakesPriorityOverTrailingConstructionHooks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Mixin:
                @classmethod
                def __prepare__(meta, name, bases):
                    events.append('prepare')
                    return {}
                def __new__(meta, name, bases, namespace):
                    events.append('new')
                    return type.__new__(meta, name, bases, namespace)
                def __init__(cls, name, bases, namespace): events.append('init')
                def __call__(cls):
                    events.append('call')
                    return 42
                def label(cls): return cls.__name__
            class Meta(type, Mixin): pass
            class Example(metaclass=Meta): pass
            print(type(Example()) is Example, events, Example.label())
            print(Meta.__base__ is type)
            """
        );

    [Fact]
    public Task DerivedPrepareAndNewCooperateThroughMixinAndBuiltinType() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Mixin:
                @classmethod
                def __prepare__(meta, name, bases):
                    events.append(('mixin prepare', meta.__name__))
                    return super().__prepare__(name, bases)
                def __new__(meta, name, bases, namespace):
                    events.append(('mixin new', meta.__name__))
                    return super().__new__(meta, name, bases, namespace)
            class Meta(Mixin, type): pass
            class Derived(Meta):
                @classmethod
                def __prepare__(meta, name, bases):
                    events.append(('derived prepare', meta.__name__))
                    return super().__prepare__(name, bases)
                def __new__(meta, name, bases, namespace):
                    events.append(('derived new', meta.__name__))
                    return super().__new__(meta, name, bases, namespace)
            class Example(metaclass=Derived): pass
            print(events, type(Example) is Derived)
            """
        );

    [Fact]
    public Task MixinDataDescriptorsOverrideClassAttributesAndAllowMutation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Mixin:
                @property
                def value(cls): return cls._value
                @value.setter
                def value(cls, value): cls._value = value
                @value.deleter
                def value(cls): del cls._value
            class Before(Mixin, type): pass
            class After(type, Mixin): pass
            for meta in (Before, After):
                class Example(metaclass=meta):
                    value = 'namespace'
                    _value = 1
                print(Example.value, Example.__dict__['value'])
                Example.value = 7
                print(Example.value, Example.__dict__['value'])
                del Example.value
                print(hasattr(Example, '_value'))
            """
        );

    [Fact]
    public Task ExplicitSuperCanCrossBuiltinTypeToReachTrailingMixin() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Mixin:
                @classmethod
                def owner(cls): return cls
                def label(cls): return cls.__name__
            class Meta(type, Mixin): pass
            class Example(metaclass=Meta): pass
            print(super(type, Meta).owner() is Meta)
            print(super(type, Example).label())
            print(super(type, Example).owner() is Meta)
            print(hasattr(super(object, Meta), '__call__'))
            print(hasattr(super(object, Example), '__call__'))
            """
        );

    [Fact]
    public Task InitSubclassTraversesBuiltinTypeToBothMixinPositions() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Mixin:
                def __init_subclass__(cls, flag=0, **kwargs):
                    events.append((cls.__name__, flag))
                    super().__init_subclass__(**kwargs)
            class Before(Mixin, type, flag=7): pass
            class After(type, Mixin, flag=8): pass
            class Derived(After, flag=9): pass
            print(events)
            print(Before.__base__ is type, After.__base__ is type, Derived.__base__ is After)
            """
        );

    [Fact]
    public Task DynamicTypeConstructsMixedMetaclassAndSelectsItsNewHook() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Mixin:
                @classmethod
                def __prepare__(meta, name, bases):
                    events.append('prepare')
                    return {}
                def __new__(meta, name, bases, namespace):
                    events.append(('new', name))
                    return super().__new__(meta, name, bases, namespace)
                def __init__(cls, name, bases, namespace):
                    events.append(('init', name))
                    super().__init__(name, bases, namespace)
            Meta = type('Meta', (Mixin, type), {})
            class Base(metaclass=Meta): pass
            events.clear()
            namespace = {'value': 7}
            Dynamic = type('Dynamic', (Base,), namespace)
            print(events, type(Dynamic) is Meta, Dynamic.value, Meta.__base__ is type)
            namespace['value'] = 9
            print(Dynamic.value)
            """
        );

    [Fact]
    public Task DuplicateBuiltinAndManagedBasesAreRejected() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Mixin: pass
            for bases in ((type, type), (Mixin, Mixin, type), (Mixin, type, Mixin)):
                try:
                    type('Broken', bases, {})
                except TypeError as error:
                    print(str(error))
            try:
                class Broken(type, type): pass
            except TypeError as error:
                print(str(error), 'Broken' in globals())
            """
        );

    [Fact]
    public Task TypeAndStorageBearingBuiltinBasesHaveLayoutConflicts() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for bases in ((type, int), (int, type), (type, list), (list, type),
                          (type, Exception), (Exception, type)):
                try:
                    type('Broken', bases, {})
                except TypeError as error:
                    print(str(error))
            try:
                class Broken(type, Exception): pass
            except TypeError as error:
                print(str(error), 'Broken' in globals())
            """
        );

    [Fact]
    public Task ExplicitObjectBaseMustRespectBuiltinTypeOrdering() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Mixin: pass
            for bases in ((type, object), (Mixin, type, object), (type, Mixin, object)):
                meta = type('Meta', bases, {})
                print([base.__name__ for base in meta.__mro__], meta.__base__ is type)
            try:
                class Broken(object, type): pass
            except TypeError as error:
                print(str(error), 'Broken' in globals())
            try:
                type('Broken', (object, type), {})
            except TypeError as error:
                print(str(error))
            """
        );

    [Fact]
    public Task MixinConstructionHookFailuresPreserveIdentityAndAllowRecovery() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            failure = ValueError('construction failed')
            events = []
            class Mixin:
                def __new__(meta, name, bases, namespace):
                    try:
                        if name == 'Broken': raise failure
                        return super().__new__(meta, name, bases, namespace)
                    finally:
                        events.append(name)
            class Meta(Mixin, type): pass
            try:
                class Broken(metaclass=Meta): pass
            except ValueError as error:
                print(error is failure, 'Broken' in globals())
            class Recovered(metaclass=Meta): pass
            print(events, type(Recovered) is Meta, type(Recovered()) is Recovered)
            """
        );
}
