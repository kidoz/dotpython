using Xunit;

namespace DotPython.DifferentialTests;

public sealed class CustomMroCompatibilityTests
{
    [Fact]
    public Task CustomMroControlsInstanceSuperAndReflectedOperatorPriority() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Left:
                def chain(self): return ['left']
                def __add__(self, other): return 'left add'
            class Right:
                def chain(self): return ['right'] + super().chain()
            class Meta(type):
                def mro(cls): return [cls, Right, Left, object]
            class Combined(Left, Right, metaclass=Meta): pass
            print(Combined().chain())
            class InsertMeta(type):
                def mro(cls): return [cls, Left, object]
            class Inserted(metaclass=InsertMeta):
                def __radd__(self, other): return 'inserted reflected'
            print(Left() + Inserted())
            print(issubclass(Inserted, Left), Inserted.__bases__ == (object,))
            """
        );

    [Fact]
    public Task ExceptionLayoutValidationAndMatchingUseDistinctStructuralAndMroRules() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for inserted in (ValueError, OSError, StopIteration):
                class Meta(type):
                    def mro(cls): return [cls, inserted, Exception, BaseException, object]
                try:
                    class Error(Exception, metaclass=Meta): pass
                    print('accepted', inserted.__name__, isinstance(Error(), inserted))
                except TypeError as error:
                    print(str(error))
            class OsMeta(type):
                def mro(cls): return [cls, ValueError, OSError, Exception, BaseException, object]
            class OsError(OSError, metaclass=OsMeta): pass
            print(issubclass(OsError, ValueError), isinstance(OsError('message'), ValueError))
            class ExitMeta(type):
                def mro(cls): return [cls, Exception, BaseException, object]
            class Exit(SystemExit, metaclass=ExitMeta): pass
            value = Exit('message')
            print(issubclass(Exit, SystemExit), issubclass(Exit, Exception))
            print(isinstance(value, SystemExit), isinstance(value, Exception))
            try: raise value
            except SystemExit: print('SystemExit caught')
            except Exception as error: print('Exception caught', error is value)
            """
        );

    [Fact]
    public Task DefaultMroSupportsBoundUnboundBuiltinAndDictionaryCalls() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Example: pass
            raw = type.__dict__['mro']
            print(Example.mro() == [Example, object])
            print(type.mro(Example) == [Example, object], raw(Example) == [Example, object])
            print(int.mro() == [int, object], type.mro(int) == [int, object])
            print(raw(Exception) == [Exception, BaseException, object])
            for call in (lambda: type.mro(), lambda: type.mro(1), lambda: Example.mro(1),
                         lambda: type.mro(Example, 1), lambda: raw(Example, 1),
                         lambda: type.mro(Example, extra=1)):
                try: call()
                except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task PendingClassHidesOrdinaryAttributesButHasNamespaceAndFilledClassCell() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            namespaces = {}
            class Base:
                inherited = 1
            class Meta(type):
                def __getattribute__(cls, name):
                    if name == 'mro': events.append('ordinary mro lookup')
                    return super().__getattribute__(name)
                def __new__(meta, name, bases, namespace):
                    namespaces[name] = namespace
                    return super().__new__(meta, name, bases, namespace)
                def mro(cls):
                    print(cls.__mro__ is None, hasattr(cls, 'own'), hasattr(cls, 'inherited'))
                    print(cls.__dict__['own'], '__classcell__' in cls.__dict__)
                    print(namespaces[cls.__name__]['owner'](None) is cls)
                    return type.mro(cls)
            class Example(Base, metaclass=Meta):
                own = 2
                def owner(self): return __class__
            print(Example.own, Example.inherited, Example().owner() is Example, events)
            """
        );

    [Fact]
    public Task ExtendingPendingClassFailsAfterTheDerivedMroCallback() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Meta(type):
                def mro(cls):
                    events.append(cls.__name__)
                    if cls.__name__ == 'Example':
                        try: type('Inner', (cls,), {})
                        except TypeError as error: print(str(error))
                    return type.mro(cls)
            class Example(metaclass=Meta): pass
            print(events, Example.__mro__ == (Example, object))
            """
        );

    [Fact]
    public Task MissingClassInCustomMroRunsSetNameBeforeSubclassHookFails() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Descriptor:
                def __set_name__(self, owner, name): events.append((owner.__name__, name))
            class Meta(type):
                def mro(cls): return [object]
            try:
                class Missing(metaclass=Meta): value = Descriptor()
            except TypeError as error:
                print(str(error), 'Missing' in globals())
            print(events)
            """
        );

    [Fact]
    public Task DefaultSingleBaseMroPreservesDuplicatesInItsParentMro() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Meta(type):
                def mro(cls):
                    if cls.__name__ == 'Base': return [cls, object, object]
                    return type.mro(cls)
            class Base(metaclass=Meta): pass
            class Child(Base): pass
            print(Child.__mro__ == (Child, Base, object, object))
            print(type.mro(Child) == [Child, Base, object, object])
            print(type(Child()) is Child)
            """
        );

    [Fact]
    public Task SelfOnlyMroCannotInstantiateAndUsesSuperFallbackForClassKeywords() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Meta(type):
                def mro(cls): return [cls]
            class Alone(metaclass=Meta): pass
            print(Alone.__new__ is type.__new__)
            try: Alone()
            except TypeError as error: print(str(error))
            try:
                class WithKeyword(metaclass=Meta, flag=1): pass
            except TypeError as error:
                print(str(error), 'WithKeyword' in globals())
            class MetaMeta(type):
                def mro(cls): return [cls, object]
            class MissingType(type, metaclass=MetaMeta): pass
            try: type.__new__(MissingType, 'Created', (), {})
            except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task CompatibleExceptionInsertedIntoCustomMroControlsChecksAndCatching() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Meta(type):
                def mro(cls): return [cls, ValueError, Exception, BaseException, object]
            class Error(Exception, metaclass=Meta): pass
            value = Error('message')
            print(Error.__bases__ == (Exception,), Error.__base__ is Exception)
            print(issubclass(Error, ValueError), isinstance(value, ValueError))
            print(issubclass(Error, TypeError), isinstance(value, TypeError))
            print(value.args, str(value), type(value) is Error)
            try: raise value
            except ValueError as caught: print('caught ValueError', caught is value)
            """
        );

    [Fact]
    public Task CustomMroReordersLookupWhileDefaultTypeMroRecomputesFromBases() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Left:
                value = 'left'
            class Right:
                value = 'right'
            class Meta(type):
                def mro(cls): return [cls, Right, Left, object]
            class Example(Left, Right, metaclass=Meta): pass
            print([base.__name__ for base in Example.__mro__])
            print([base.__name__ for base in type.mro(Example)])
            print(Example.value, Example().value)
            print(Example.__bases__ == (Left, Right), Example.__base__ is Left)
            print(type(Example.mro()).__name__, type(type.mro(Example)).__name__)
            """
        );

    [Fact]
    public Task InheritedMroOverridesBindTheNewClassAndDelegateThroughSuper() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Meta(type):
                def mro(cls):
                    events.append(('base', cls.__name__))
                    return super().mro()
            class Derived(Meta):
                def mro(cls):
                    events.append(('derived', cls.__name__))
                    return super().mro()
            class Base(metaclass=Derived): pass
            class Child(Base): pass
            print(events)
            print(Child.__mro__ == (Child, Base, object), type(Child) is Derived)
            """
        );

    [Fact]
    public Task MroCallbackSeesNamespaceAndBasesBeforeSetNameAndSubclassHooks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Descriptor:
                def __set_name__(self, owner, name): events.append(('set name', name))
            class Base:
                def __init_subclass__(cls): events.append(('subclass', cls.__name__))
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases):
                    events.append('prepare')
                    return {}
                def __new__(meta, name, bases, namespace):
                    events.append('new before')
                    result = super().__new__(meta, name, bases, namespace)
                    events.append('new after')
                    return result
                def mro(cls):
                    events.append(('mro', cls.__name__, cls.__mro__ is None,
                                   cls.__bases__ == (Base,), cls.__base__ is Base,
                                   cls.__dict__['value']))
                    return type.mro(cls)
                def __init__(cls, name, bases, namespace): events.append('init')
            class Example(Base, metaclass=Meta):
                value = 7
                descriptor = Descriptor()
            print(events)
            """
        );

    [Fact]
    public Task TupleAndGeneratorMroResultsAreMaterialized() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class TupleMeta(type):
                def mro(cls): return (cls, object)
            class GeneratorMeta(type):
                def mro(cls):
                    try:
                        events.append('start')
                        yield cls
                        events.append('between')
                        yield object
                    finally:
                        events.append('end')
            class First(metaclass=TupleMeta): pass
            class Second(metaclass=GeneratorMeta): pass
            print(First.__mro__ == (First, object), Second.__mro__ == (Second, object))
            print(type(First.__mro__).__name__, type(Second.__mro__).__name__, events)
            """
        );

    [Fact]
    public Task MaterializedMroDoesNotAliasTheReturnedMutableList() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            returned = []
            class Meta(type):
                def mro(cls):
                    returned.extend([cls, object])
                    return returned
            class Example(metaclass=Meta): pass
            returned.clear()
            print(Example.__mro__ == (Example, object), returned)
            print(type(Example()) is Example)
            """
        );

    [Fact]
    public Task EmptyAndNonIterableMroResultsFailBeforeClassBinding() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for returned in ([], (), None, 42):
                class Meta(type):
                    def mro(cls): return returned
                try:
                    class Broken(metaclass=Meta): pass
                except TypeError as error:
                    print(str(error), 'Broken' in globals())
            """
        );

    [Fact]
    public Task MroValidationRejectsNonClassEntries() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Ordinary: pass
            for invalid in (42, 'bad', None, Ordinary()):
                class Meta(type):
                    def mro(cls): return [cls, invalid, object]
                try:
                    class Broken(metaclass=Meta): pass
                except TypeError as error:
                    print(str(error), 'Broken' in globals())
            """
        );

    [Fact]
    public Task MroValidationRejectsIncompatibleBuiltinLayouts() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for invalid in (int, list, type, Exception):
                class Meta(type):
                    def mro(cls): return [cls, invalid, object]
                try:
                    class Broken(metaclass=Meta): pass
                except TypeError as error:
                    print(str(error), 'Broken' in globals())
            """
        );

    [Fact]
    public Task CustomMroAllowsDuplicatesAndUnrelatedOrdinaryClasses() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Other:
                value = 7
            class Base: pass
            class Meta(type):
                def mro(cls): return [cls, Other, object, object]
            class Example(Base, metaclass=Meta): pass
            print([base.__name__ for base in Example.__mro__])
            print(Example.value, Example().value)
            print(issubclass(Example, Other), issubclass(Example, Base))
            print(isinstance(Example(), Other), isinstance(Example(), Base))
            print(Example.__bases__ == (Base,), Example.__base__ is Base)
            """
        );

    [Fact]
    public Task CustomMroCanOmitObjectOrPlaceTheNewClassAfterAnotherClass() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Other:
                value = 'other'
            class AloneMeta(type):
                def mro(cls): return [cls]
            class Alone(metaclass=AloneMeta): pass
            print(Alone.__mro__ == (Alone,), Alone.__bases__ == (object,))
            print(issubclass(Alone, object))
            class OtherFirstMeta(type):
                def mro(cls): return [Other, cls, object]
            class Example(metaclass=OtherFirstMeta):
                value = 'own'
            print(Example.value, Example().value)
            print([base.__name__ for base in Example.__mro__])
            """
        );

    [Fact]
    public Task CustomMroControlsInitSubclassLookup() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Declared:
                def __init_subclass__(cls): events.append('declared')
            class Inserted:
                def __init_subclass__(cls): events.append(('inserted', cls.__name__))
            class Meta(type):
                def mro(cls): return [cls, Inserted, object]
            class Example(Declared, metaclass=Meta): pass
            print(events, Example.__base__ is Declared, issubclass(Example, Inserted))
            """
        );

    [Fact]
    public Task CustomMroCanBypassDefaultC3AndDuplicateBaseChecks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class X: pass
            class Y: pass
            class Left(X, Y): pass
            class Right(Y, X): pass
            class Meta(type):
                def mro(cls): return [cls, X, Y, object]
            for bases in ((Left, Right), (X, X)):
                Example = Meta('Example', bases, {})
                print(Example.__bases__ == bases, [base.__name__ for base in Example.__mro__])
                try: type.mro(Example)
                except TypeError: print('default rejects bases')
            """
        );

    [Fact]
    public Task MetaclassMroDescriptorBindsClassAndIgnoresItsOwnMroMethod() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class MroDescriptor:
                def __get__(self, instance, owner):
                    events.append((instance.__name__, owner.__name__))
                    return lambda: [instance, object]
            class Meta(type): mro = MroDescriptor()
            class Example(metaclass=Meta):
                def mro(self): return 'instance method'
            print(events, Example().mro(), Example.__mro__ == (Example, object))
            """
        );

    [Fact]
    public Task IterationFailuresPreserveIdentityAndPreventSubsequentHooks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            failure = ValueError('iteration failed')
            events = []
            class Descriptor:
                def __set_name__(self, owner, name): events.append('set name')
            class Meta(type):
                def mro(cls):
                    if cls.__name__ == 'Broken':
                        def result():
                            try:
                                yield cls
                                raise failure
                            finally:
                                events.append('cleanup')
                        return result()
                    return type.mro(cls)
            try:
                class Broken(metaclass=Meta): value = Descriptor()
            except ValueError as error:
                print(error is failure, 'Broken' in globals())
            class Recovered(metaclass=Meta): value = Descriptor()
            print(events, Recovered.__mro__ == (Recovered, object))
            """
        );

    [Fact]
    public Task CallbackExceptionsPreserveIdentityAndAllowLaterConstruction() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            failure = ValueError('mro failed')
            events = []
            class Meta(type):
                def mro(cls):
                    try:
                        if cls.__name__ == 'Broken': raise failure
                        return super().mro()
                    finally:
                        events.append(cls.__name__)
            try:
                class Broken(metaclass=Meta): pass
            except ValueError as error: print(error is failure, 'Broken' in globals())
            class Recovered(metaclass=Meta): pass
            print(events, type(Recovered()) is Recovered)
            """
        );

    [Fact]
    public Task MroCallbacksRunForDynamicConstructionAndMayMutateTheNamespace() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Descriptor:
                def __set_name__(self, owner, name): events.append(('set name', name))
            class Meta(type):
                def mro(cls):
                    events.append(('mro', cls.__name__))
                    cls.added = Descriptor()
                    return type.mro(cls)
            First = Meta('First', (), {})
            events.clear()
            Second = type('Second', (First,), {})
            print(events, type(Second) is Meta)
            print('added' in First.__dict__, 'added' in Second.__dict__)
            """
        );
}
