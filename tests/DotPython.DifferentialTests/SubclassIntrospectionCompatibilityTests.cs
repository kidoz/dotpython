using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SubclassIntrospectionCompatibilityTests
{
    [Fact]
    public Task TypeMethodDescriptorMetadataAndExplicitBindingFollowDescriptorRules() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Root: pass
            class Child(Root): pass
            for name in ('__subclasses__', 'mro'):
                raw = type.__dict__[name]
                print(raw.__name__, raw.__qualname__, raw.__objclass__ is type)
                print(raw.__get__(None, int) is raw)
                expected = [Child] if name == '__subclasses__' else [Root, object]
                bound = raw.__get__(Root, object)
                print(bound() == expected, type(bound).__name__)
                print(bound.__name__, bound.__qualname__, bound.__self__ is Root)
                print(hasattr(bound, '__objclass__'), hasattr(bound, '__get__'))
                for arguments in ((None,), (None, None), (42,), (42, type), (), (Root, type, object)):
                    try: raw.__get__(*arguments)
                    except TypeError as error: print(str(error))
                try: raw.__get__(Root, owner=type)
                except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task RepresentedBuiltinInventoryPreservesEveryDirectSubclassSubsequence() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            represented = (
                object, type, bytes, classmethod, complex, dict, enumerate, filter,
                float, frozenset, list, int, map, property, range, reversed, set,
                slice, staticmethod, super, tuple, str, zip, BaseException, bool,
                BaseExceptionGroup, Exception, GeneratorExit, KeyboardInterrupt,
                SystemExit, ArithmeticError, AssertionError, AttributeError, EOFError,
                ImportError, LookupError, NameError, OSError, RuntimeError,
                StopAsyncIteration, StopIteration, SyntaxError, TypeError, ValueError,
                ExceptionGroup, OverflowError, ZeroDivisionError, ModuleNotFoundError,
                IndexError, KeyError, UnboundLocalError, FileNotFoundError,
                IsADirectoryError, PermissionError, RecursionError, UnicodeError,
                UnicodeDecodeError, UnicodeEncodeError
            )
            print(len(represented))
            for parent in represented:
                print(parent.__name__, [child.__name__ for child in type.__subclasses__(parent)
                                        if child in represented])
            """
        );

    [Fact]
    public Task ImportedBuiltinModuleTypesAppearInTheObjectSubclassSubsequence() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            print([child.__name__ for child in object.__subclasses__()
                   if child.__name__ in ('Template', 'Interpolation')])
            from string.templatelib import Template, Interpolation
            import __future__
            Feature = type(__future__.annotations)
            represented = (Template, Interpolation, Feature)
            print([child.__name__ for child in object.__subclasses__() if child in represented])
            for parent in represented:
                print(parent.__name__, parent.__base__ is object,
                      parent in object.__subclasses__())
            from string.templatelib import Template as Again
            import __future__ as future_again
            print(Again is Template, type(future_again.annotations) is Feature)
            print([child.__name__ for child in object.__subclasses__() if child in represented])
            """
        );

    [Fact]
    public Task SubclassesAreDirectAndOrderedByCreation() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Root: pass
            class First(Root): pass
            class Second(Root): pass
            class Grandchild(First): pass
            class Diamond(First, Second): pass
            print(Root.__subclasses__() == [First, Second])
            print(First.__subclasses__() == [Grandchild, Diamond])
            print(Second.__subclasses__() == [Diamond], Diamond.__subclasses__() == [])
            """
        );

    [Fact]
    public Task EachSubclassListIsAnIndependentSnapshot() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Root: pass
            class First(Root): pass
            snapshot = Root.__subclasses__()
            another = Root.__subclasses__()
            print(type(snapshot).__name__, snapshot is another, snapshot == another)
            snapshot.clear()
            snapshot.append(object)
            print(Root.__subclasses__() == [First], snapshot == [object])
            class Second(Root): pass
            print(another == [First], Root.__subclasses__() == [First, Second])
            """
        );

    [Fact]
    public Task RawDescriptorAndBoundCallsValidateReceiverAndArguments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Root: pass
            class Child(Root): pass
            raw = type.__dict__['__subclasses__']
            print(type(raw).__name__, raw(Root) == [Child])
            print(type.__subclasses__(Root) == [Child], raw.__get__(Root, type)() == [Child])
            print(raw.__get__(None, type) is raw)
            for call in (lambda: raw(), lambda: raw(1), lambda: type.__subclasses__(),
                         lambda: Root.__subclasses__(1), lambda: type.__subclasses__(Root, 1),
                         lambda: raw(Root, flag=1)):
                try: call()
                except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task TypeSubclassesRequireExplicitBindingForTheirInheritedMethod() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Meta(type): pass
            class Derived(Meta): pass
            class Example(metaclass=Meta): pass
            try: Meta.__subclasses__()
            except TypeError as error: print(str(error))
            print(type.__subclasses__(Meta) == [Derived])
            print(type.__dict__['__subclasses__'].__get__(Meta, type)() == [Derived])
            print(Example not in type.__subclasses__(Meta))
            """
        );

    [Fact]
    public Task ClassAndMetaclassOverridesDoNotAffectDirectDescriptorIntrospection() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Meta(type):
                def __subclasses__(cls): return ['metaclass override']
            class Root(metaclass=Meta): pass
            class Child(Root): pass
            print(Root.__subclasses__(), type.__subclasses__(Root) == [Child])
            class Shadow:
                @classmethod
                def __subclasses__(cls): return ['class override', cls.__name__]
            class ShadowChild(Shadow): pass
            print(Shadow.__subclasses__(), type.__subclasses__(Shadow) == [ShadowChild])
            """
        );

    [Fact]
    public Task MixedMetaclassLookupHonorsBothPositionsAroundType() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Mixin:
                def __subclasses__(cls): return ['mixin override']
            class Before(Mixin, type): pass
            class After(type, Mixin): pass
            class First(metaclass=Before): pass
            class Second(metaclass=After): pass
            class FirstChild(First): pass
            class SecondChild(Second): pass
            print(First.__subclasses__())
            print(Second.__subclasses__() == [SecondChild])
            print(type.__subclasses__(First) == [FirstChild])
            """
        );

    [Fact]
    public Task CustomMroDoesNotChangeDeclaredBaseRegistration() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Declared: pass
            class Inserted: pass
            class Meta(type):
                def mro(cls): return [cls, Inserted, object]
            class Example(Declared, metaclass=Meta): pass
            print(Declared.__subclasses__() == [Example], Inserted.__subclasses__() == [])
            print(issubclass(Example, Declared), issubclass(Example, Inserted))
            """
        );

    [Fact]
    public Task DuplicateDeclaredBasesRegisterEachSubclassOnlyOnce() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Root: pass
            class Meta(type):
                def mro(cls): return [cls, Root, object]
            class First(Root, Root, metaclass=Meta): pass
            class Second(Root, metaclass=Meta): pass
            print(Root.__subclasses__() == [First, Second])
            First.__bases__ = (Root, Root)
            print(Root.__subclasses__() == [Second, First])
            """
        );

    [Fact]
    public Task RegistrationOccursAfterMroButBeforeDescriptorAndSubclassHooks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Root:
                def __init_subclass__(cls):
                    events.append(('init subclass', cls in Root.__subclasses__()))
            class Descriptor:
                def __set_name__(self, owner, name):
                    events.append(('set name', owner in Root.__subclasses__()))
            class Meta(type):
                def mro(cls):
                    events.append(('mro', cls in Root.__subclasses__()))
                    return type.mro(cls)
                def __new__(meta, name, bases, namespace):
                    result = super().__new__(meta, name, bases, namespace)
                    events.append(('new after', result in Root.__subclasses__()))
                    return result
            class Example(Root, metaclass=Meta): value = Descriptor()
            print(events, Root.__subclasses__() == [Example])
            """
        );

    [Fact]
    public Task FailedConstructionRegistrationDependsOnTheFailingStage() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            retained = []
            failure = ValueError('failure')
            class Root: pass
            class Meta(type):
                def mro(cls):
                    retained.append(cls)
                    if cls.__name__ == 'MroFailure': raise failure
                    return type.mro(cls)
            try:
                class MroFailure(Root, metaclass=Meta): pass
            except ValueError: pass
            print(retained[0] not in Root.__subclasses__())
            class Descriptor:
                def __set_name__(self, owner, name): raise failure
            try:
                class DescriptorFailure(Root, metaclass=Meta): value = Descriptor()
            except ValueError: pass
            print(Root.__subclasses__() == [retained[1]])
            print('MroFailure' in globals(), 'DescriptorFailure' in globals())
            """
        );

    [Fact]
    public Task SuccessfulRebasingRemovesAndAppendsSubclassEntries() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Before: pass
            class After: pass
            class First(Before): pass
            class Second(Before): pass
            class Existing(After): pass
            First.__bases__ = First.__bases__
            print(Before.__subclasses__() == [Second, First])
            First.__bases__ = (After,)
            print(Before.__subclasses__() == [Second], After.__subclasses__() == [Existing, First])
            First.__bases__ = (Before,)
            print(Before.__subclasses__() == [Second, First], After.__subclasses__() == [Existing])
            """
        );

    [Fact]
    public Task FailedBaseChangeRetainsOldRegistrationAndExposesItDuringCallbacks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            active = False
            events = []
            class Before: pass
            class After: pass
            class Meta(type):
                def mro(cls):
                    if active:
                        events.append((cls in Before.__subclasses__(), cls in After.__subclasses__()))
                        raise ValueError('failure')
                    return type.mro(cls)
            class Example(Before, metaclass=Meta): pass
            active = True
            try: Example.__bases__ = (After,)
            except ValueError: pass
            print(events, Before.__subclasses__() == [Example], After.__subclasses__() == [])
            """
        );

    [Fact]
    public Task ReentrantBaseChangePreservesTheOriginalRegistrationAlongsideTheInnerOne() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            active = False
            class Before: pass
            class Outer: pass
            class Inner: pass
            class Meta(type):
                def mro(cls):
                    global active
                    if active:
                        active = False
                        cls.__bases__ = (Inner,)
                    return type.mro(cls)
            class Example(Before, metaclass=Meta): pass
            active = True
            Example.__bases__ = (Outer,)
            print(Example.__bases__ == (Inner,))
            print(Before.__subclasses__() == [Example], Outer.__subclasses__() == [])
            print(Inner.__subclasses__() == [Example])
            """
        );

    [Fact]
    public Task BuiltinBasesExposeTheirDirectManagedSubclasses() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Ordinary: pass
            class Meta(type): pass
            class Error(ValueError): pass
            class DerivedError(Error): pass
            print(Ordinary in object.__subclasses__())
            print(Meta in type.__subclasses__(type))
            print(Error in ValueError.__subclasses__(), DerivedError in ValueError.__subclasses__())
            print(Error.__subclasses__() == [DerivedError])
            print(bool in int.__subclasses__(), Exception in BaseException.__subclasses__())
            """
        );
}
