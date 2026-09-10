using Xunit;

namespace DotPython.DifferentialTests;

public sealed class MetaclassCompatibilityTests
{
    [Fact]
    public Task HeaderKeywordUnpackingUsesMappingProtocolsAndRejectsDuplicates() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from types import MappingProxyType
            events = []
            class Keywords:
                def keys(self):
                    events.append('keys')
                    return ['flag']
                def __getitem__(self, key):
                    events.append(('get', key))
                    return 7
            class Base:
                def __init_subclass__(cls, flag): print(cls.__name__, flag)
            class First(Base, **Keywords()): pass
            class Second(Base, **MappingProxyType({'flag': 8})): pass
            print(events)
            events.clear()
            try:
                class Broken(Base, flag=1, **Keywords()): pass
            except TypeError:
                print('duplicate rejected', events)
            try:
                class BadKey(Base, **{1: 2}): pass
            except TypeError:
                print('nonstring rejected')
            """
        );

    [Fact]
    public Task DirectTypeConstructionForwardsKeywordsToSubclassHooks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Base:
                def __init_subclass__(cls, flag=0): print(cls.__name__, flag)
            Dynamic = type('Dynamic', (Base,), {}, flag=7)
            class Meta(type):
                def __init__(cls, name, bases, namespace, **kwargs):
                    print('meta init', name, kwargs)
                    super().__init__(name, bases, namespace, **kwargs)
            Other = Meta('Other', (Base,), {}, flag=8)
            try:
                type(1, flag=1)
            except TypeError as error:
                print(str(error))
            """
        );

    [Fact]
    public Task MetaclassCallDelegatesThroughSuperAndPreservesArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Meta(type):
                def __call__(cls, *args, **kwargs):
                    events.append(('call', cls.__name__, args, kwargs))
                    instance = super().__call__(*args, **kwargs)
                    instance.called = True
                    return instance
            class Example(metaclass=Meta):
                def __new__(cls, value, *, extra=0):
                    events.append(('new', value, extra))
                    return super().__new__(cls)
                def __init__(self, value, *, extra=0):
                    events.append(('init', value, extra))
                    self.value = value + extra
            instance = Example(3, extra=4)
            print(events, instance.value, instance.called, type(instance) is Example)
            """
        );

    [Fact]
    public Task MetaclassPropertiesOverrideClassNamespaceAndHandleMutation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Meta(type):
                @property
                def value(cls): return cls._stored
                @value.setter
                def value(cls, value): cls._stored = value
                @value.deleter
                def value(cls): del cls._stored
            class Example(metaclass=Meta):
                value = 'class namespace'
                _stored = 1
            print(Example.value, Example.__dict__['value'])
            Example.value = 7
            print(Example.value, Example.__dict__['value'])
            del Example.value
            print(hasattr(Example, '_stored'), Example.__dict__['value'])
            Meta.value = 'metaclass plain'
            print(Example.value)
            del Example.value
            print(Example.value)
            """
        );

    [Fact]
    public Task InheritedPrepareBindsTheSelectedDerivedMetaclass() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases):
                    events.append((meta.__name__, name))
                    return {'prepared_by': meta}
            class DerivedMeta(Meta): pass
            class Base(metaclass=DerivedMeta): pass
            class Child(Base): pass
            print(events)
            print(Base.prepared_by is DerivedMeta, Child.prepared_by is DerivedMeta)
            print(type(Child) is DerivedMeta)
            """
        );

    [Fact]
    public Task MetaclassInstanceAndSubclassChecksParticipateInClassInformation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Meta(type):
                def __instancecheck__(cls, instance):
                    events.append(('instance', cls.__name__))
                    return getattr(instance, 'accepted', False)
                def __subclasscheck__(cls, candidate):
                    events.append(('subclass', cls.__name__, candidate.__name__))
                    return getattr(candidate, 'accepted', False)
            class Contract(metaclass=Meta): pass
            class Accepted:
                accepted = True
            class Rejected: pass
            print(type(Contract) is Meta, isinstance(Contract, type), issubclass(Meta, type))
            print(isinstance(Accepted(), Contract), isinstance(Rejected(), Contract))
            print(issubclass(Accepted, Contract), issubclass(Rejected, Contract))
            print(events)
            """
        );

    [Fact]
    public Task ThreeArgumentTypeSelectsInheritedMetaclassWithoutPreparingANamespace() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases):
                    events.append('prepare')
                    return {}
                def __new__(meta, name, bases, namespace):
                    events.append(('new', name))
                    return super().__new__(meta, name, bases, namespace)
                def __init__(cls, name, bases, namespace):
                    events.append(('init', name))
            class Base(metaclass=Meta): pass
            events.clear()
            namespace = {'value': 7}
            Dynamic = type('Dynamic', (Base,), namespace)
            print(events, type(Dynamic) is Meta, Dynamic.value)
            namespace['value'] = 8
            print(Dynamic.value)
            """
        );

    [Fact]
    public Task MostDerivedMetaclassIsSelectedFromExplicitAndInheritedCandidates() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Meta(type): pass
            class DerivedMeta(Meta): pass
            class Left(metaclass=Meta): pass
            class Right(metaclass=DerivedMeta): pass
            class Child(Left, Right): pass
            class Explicit(Left, Right, metaclass=Meta): pass
            print(type(Left) is Meta, type(Right) is DerivedMeta)
            print(type(Child) is DerivedMeta, type(Explicit) is DerivedMeta)
            print(isinstance(Child, DerivedMeta), isinstance(Child, Meta), isinstance(Child, type))
            print(issubclass(DerivedMeta, Meta), issubclass(Meta, type))
            print(Child.__bases__ == (Left, Right))
            """
        );

    [Fact]
    public Task MetaclassConflictsFailBeforePreparingOrExecutingTheBody() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class FirstMeta(type):
                @classmethod
                def __prepare__(meta, name, bases):
                    events.append(name)
                    return {}
            class SecondMeta(type): pass
            class First(metaclass=FirstMeta): pass
            class Second(metaclass=SecondMeta): pass
            events.clear()
            try:
                class Conflict(First, Second):
                    events.append('body')
            except TypeError:
                print('incompatible metaclasses')
            try:
                class ExplicitConflict(First, metaclass=SecondMeta):
                    events.append('explicit body')
            except TypeError:
                print('incompatible explicit metaclass')
            print(events, 'Conflict' in globals(), 'ExplicitConflict' in globals())
            """
        );

    [Fact]
    public Task PreparedDictionaryIsTheClassBodyNamespaceAndIsCopiedAtConstruction() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            prepared = []
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases):
                    namespace = {'seed': 10}
                    prepared.append(namespace)
                    return namespace
                def __new__(meta, name, bases, namespace):
                    print(namespace is prepared[0], namespace['answer'])
                    return type.__new__(meta, name, bases, namespace)
            class Example(metaclass=Meta):
                answer = seed + 2
                print(locals() is prepared[0])
            prepared[0]['answer'] = 99
            prepared[0]['late'] = 1
            print(Example.answer, Example.seed, hasattr(Example, 'late'))
            print(type(Example) is Meta, type(Example.__dict__).__name__)
            """
        );

    [Fact]
    public Task CreationHooksRunInOrderWithClassCellsReadyAndKeywordsForwarded() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Field:
                def __set_name__(self, owner, name):
                    events.append(('set_name', name, owner().owner() is owner))
            class Base:
                def __init_subclass__(cls, flag, **kwargs):
                    events.append(('init_subclass', cls.__name__, flag, cls().owner() is cls))
                    super().__init_subclass__(**kwargs)
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases, **kwargs):
                    events.append(('prepare', name, kwargs['flag'], 'metaclass' in kwargs))
                    return {}
                def __new__(meta, name, bases, namespace, **kwargs):
                    events.append(('new', name, kwargs['flag']))
                    return super().__new__(meta, name, bases, namespace, **kwargs)
                def __init__(cls, name, bases, namespace, **kwargs):
                    events.append(('init', name, kwargs['flag']))
                    super().__init__(name, bases, namespace, **kwargs)
            def decorate(cls):
                events.append(('decorate', cls.__name__))
                return cls
            @decorate
            class Example(Base, metaclass=Meta, flag=9):
                events.append(('body', 'Example'))
                field = Field()
                def owner(self): return __class__
            print(events)
            print(type(Example) is Meta)
            """
        );

    [Fact]
    public Task ClassHeaderExpressionsAndKeywordUnpackingEvaluateOnceBeforePreparation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Base:
                def __init_subclass__(cls, flag=0): events.append(('subclass', flag))
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases, flag=0):
                    events.append(('prepare', flag))
                    return {}
            def select(label, value):
                events.append(label)
                return value
            class Example(select('base', Base), metaclass=select('meta', Meta), **select('keywords', {'flag': 7})):
                events.append('body')
            print(events)
            print(type(Example) is Meta)
            """
        );

    [Fact]
    public Task InitSubclassIsImplicitlyClassBoundAndCooperatesThroughTheMro() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Root:
                def __init_subclass__(cls, **kwargs):
                    events.append(('root', cls.__name__, kwargs))
                    super().__init_subclass__(**kwargs)
            class Left(Root):
                def __init_subclass__(cls, left=0, **kwargs):
                    events.append(('left', cls.__name__, left))
                    super().__init_subclass__(**kwargs)
                    return 123
            class Right(Root):
                def __init_subclass__(cls, right=0, **kwargs):
                    events.append(('right', cls.__name__, right))
                    super().__init_subclass__(**kwargs)
            events.clear()
            class Child(Left, Right, left=1, right=2): pass
            print(events)
            events.clear()
            Dynamic = type('Dynamic', (Left, Right), {})
            print(events, Dynamic.__bases__ == (Left, Right))
            try:
                class Invalid(Root, unexpected=3): pass
            except TypeError:
                print('object rejects unused keyword')
            """
        );

    [Fact]
    public Task MetaclassNewAndInitCanDelegateThroughSuperAndDirectTypeMethods() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Meta(type):
                def __new__(meta, name, bases, namespace):
                    events.append(('base new', meta.__name__, name))
                    return type.__new__(meta, name, bases, namespace)
                def __init__(cls, name, bases, namespace):
                    events.append(('base init', cls.__name__))
                    print(type.__init__(cls, name, bases, namespace))
            class DerivedMeta(Meta):
                def __new__(meta, name, bases, namespace):
                    events.append(('derived new', meta.__name__))
                    return super().__new__(meta, name, bases, namespace)
                def __init__(cls, name, bases, namespace):
                    events.append(('derived init', cls.__name__))
                    super().__init__(name, bases, namespace)
            Dynamic = DerivedMeta('Dynamic', (), {'answer': 42})
            print(events, Dynamic.answer, type(Dynamic) is DerivedMeta)
            print(type.__init__(Dynamic, 'Ignored', (), {'answer': 99}))
            print(Dynamic.__name__, Dynamic.answer)
            """
        );

    [Fact]
    public Task NonClassNewResultsSkipInitAndStillReachClassDecorators() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Meta(type):
                def __new__(meta, name, bases, namespace):
                    events.append(('new', name, '__classcell__' in namespace))
                    return 42
                def __init__(cls, *args): events.append('unexpected init')
            def decorate(value):
                events.append(('decorate', value))
                return value + 1
            @decorate
            class Example(metaclass=Meta):
                def owner(self): return __class__
            print(Example, events)
            """
        );

    [Fact]
    public Task TypeNewRequiresClassCellPropagationAndValidatesItsType() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class DropCell(type):
                def __new__(meta, name, bases, namespace):
                    namespace.pop('__classcell__', None)
                    return super().__new__(meta, name, bases, namespace)
            try:
                class Missing(metaclass=DropCell):
                    def owner(self): return __class__
            except RuntimeError:
                print('missing class cell rejected')
            class InvalidCell(type):
                def __new__(meta, name, bases, namespace):
                    namespace['__classcell__'] = 42
                    return super().__new__(meta, name, bases, namespace)
            try:
                class Invalid(metaclass=InvalidCell): pass
            except TypeError:
                print('noncell rejected')
            print('Missing' in globals(), 'Invalid' in globals())
            """
        );

    [Fact]
    public Task PrepareMustReturnAMappingAndRunsBeforeTheClassBody() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases):
                    events.append('prepare')
                    return []
            try:
                class Example(metaclass=Meta): events.append('body')
            except TypeError:
                print('invalid namespace')
            print(events, 'Example' in globals())
            """
        );

    [Fact]
    public Task MetaclassInitMustReturnNoneAndFailureLeavesClassNameUnbound() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Meta(type):
                def __new__(meta, name, bases, namespace):
                    events.append('new')
                    return super().__new__(meta, name, bases, namespace)
                def __init__(cls, name, bases, namespace):
                    events.append('init')
                    return 123
            def decorate(cls):
                events.append('decorator')
                return cls
            try:
                @decorate
                class Example(metaclass=Meta): pass
            except TypeError:
                print('invalid init result')
            print(events, 'Example' in globals())
            """
        );

    [Fact]
    public Task MetaclassHookFailuresPreserveOriginalExceptionIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            failure = ValueError('metaclass failed')
            events = []
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases):
                    events.append('prepare')
                    if stage == 'prepare': raise failure
                    return {}
                def __new__(meta, name, bases, namespace):
                    events.append('new')
                    if stage == 'new': raise failure
                    return super().__new__(meta, name, bases, namespace)
                def __init__(cls, name, bases, namespace):
                    events.append('init')
                    if stage == 'init': raise failure
            for stage in ('prepare', 'new', 'init'):
                events.clear()
                try:
                    class Example(metaclass=Meta): events.append('body')
                except ValueError as error:
                    print(stage, error is failure, events, 'Example' in globals())
            """
        );
}
