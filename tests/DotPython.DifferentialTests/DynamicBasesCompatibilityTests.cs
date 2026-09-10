using Xunit;

namespace DotPython.DifferentialTests;

public sealed class DynamicBasesCompatibilityTests
{
    [Fact]
    public Task AdmittedBuiltinAndHeapLayoutMatrixMatchesAssignmentRules() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class A: pass
            class B: pass
            class A1(A): pass
            class MA(type): pass
            class MB(type): pass
            class EA(Exception): pass
            class EB(Exception): pass
            class EV(ValueError): pass
            class EA1(EA): pass
            class EG(BaseExceptionGroup): pass
            class EGG(ExceptionGroup): pass
            options = (object, A, B, A1, type, MA, MB, BaseException, Exception, ValueError,
                       EA, EB, EV, EA1, OSError, StopIteration, BaseExceptionGroup,
                       ExceptionGroup, EG, EGG)
            for before in options:
                for after in options:
                    Target = type('Target', (before,), {})
                    try:
                        Target.__bases__ = (after,)
                        print(before.__name__, after.__name__, 'ok')
                    except TypeError as error:
                        print(before.__name__, after.__name__, str(error))
            """
        );

    [Fact]
    public Task NewerNestedDescendantMroSurvivesLaterSiblingFailure() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            active = False
            moved = False
            inner_mro = None
            failure = ValueError('sibling failed')
            class Before: pass
            class After: pass
            class Independent: pass
            class Meta(type):
                def mro(cls):
                    global moved, inner_mro
                    if active:
                        events.append(cls.__name__)
                        if cls.__name__ == 'Left' and not moved:
                            moved = True
                            cls.__bases__ = (Independent,)
                            inner_mro = cls.__mro__
                        if cls.__name__ == 'Right': raise failure
                    return type.mro(cls)
            class Root(Before, metaclass=Meta): pass
            class Left(Root): pass
            class Right(Root): pass
            class LeftChild(Left): pass
            old_root = Root.__mro__
            old_right = Right.__mro__
            active = True
            try: Root.__bases__ = (After,)
            except ValueError as error: print(error is failure)
            print(events)
            print(Root.__mro__ is old_root, Right.__mro__ is old_right)
            print(Left.__mro__ is inner_mro, Left.__bases__ == (Independent,))
            print(LeftChild.__mro__ == (LeftChild, Left, Independent, object))
            """
        );

    [Fact]
    public Task ChildCreatedDuringFailingCallbackRetainsTheAttemptedAncestry() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            active = False
            new_child = None
            failure = ValueError('sibling failed')
            class Before: pass
            class After: pass
            class Meta(type):
                def mro(cls):
                    global new_child
                    if active:
                        events.append(cls.__name__)
                        if cls.__name__ == 'Right':
                            new_child = type('NewChild', (Root,), {})
                            raise failure
                    return type.mro(cls)
            class Root(Before, metaclass=Meta): pass
            class Left(Root): pass
            class Right(Root): pass
            old_root = Root.__mro__
            old_left = Left.__mro__
            active = True
            try: Root.__bases__ = (After,)
            except ValueError as error: print(error is failure)
            print(events, Root.__mro__ is old_root, Left.__mro__ is old_left)
            print(new_child.__mro__ == (new_child, Root, After, object))
            print(issubclass(new_child, After), issubclass(Root, After))
            active = False
            Root.__bases__ = (Before,)
            print(new_child.__mro__ == (new_child, Root, Before, object))
            """
        );

    [Fact]
    public Task CustomMroCanReuseTheExactTupleAcrossBaseAssignments() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            saved = None
            class Before: pass
            class After: pass
            creation_bases = (Before,)
            Created = type('Created', creation_bases, {})
            print(Created.__bases__ is creation_bases)
            class Meta(type):
                def mro(cls):
                    global saved
                    if saved is None: saved = (cls, Before, object)
                    return saved
            class Example(Before, metaclass=Meta): pass
            print(Example.__mro__ is saved)
            Example.__bases__ = (After,)
            print(Example.__mro__ is saved, Example.__bases__ == (After,))
            print(Example.__base__ is After, issubclass(Example, Before), issubclass(Example, After))
            print(type.mro(Example) == [Example, After, object])
            """
        );

    [Fact]
    public Task ChildrenCreatedDuringRootCallbackParticipateInTheUpdate() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            active = False
            new_child = None
            class Before: pass
            class After: pass
            class Meta(type):
                def mro(cls):
                    global active, new_child
                    events.append(cls.__name__)
                    if active and cls.__name__ == 'Example':
                        active = False
                        new_child = type('NewChild', (cls,), {})
                        print(new_child.__mro__ == (new_child, cls, Before, object))
                    return type.mro(cls)
            class Example(Before, metaclass=Meta): pass
            class Existing(Example): pass
            events.clear()
            active = True
            Example.__bases__ = (After,)
            print(events)
            print(new_child.__mro__ == (new_child, Example, After, object))
            print(Existing.__mro__ == (Existing, Example, After, object))
            """
        );

    [Fact]
    public Task ReentrantCycleValidationChecksTheAlreadyUpdatedPhysicalBaseChain() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            active = False
            class Root: pass
            class Candidate(Root): pass
            class Meta(type):
                def mro(cls):
                    if active:
                        try: Candidate.__bases__ = (cls,)
                        except TypeError as error: print(str(error))
                    return type.mro(cls)
            class Example(Root, metaclass=Meta): pass
            active = True
            Example.__bases__ = (Candidate,)
            print(Example.__mro__ == (Example, Candidate, Root, object))
            print(Candidate.__bases__ == (Root,), Candidate.__base__ is Root)
            """
        );

    [Fact]
    public Task WarmedAttributeAndSpecialMethodCallsObserveNewBasesWhileSavedMethodsRetainFunctions() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Before:
                def value(self): return 1
                def __len__(self): return 2
                def __add__(self, other): return 'before add'
            class After:
                def value(self): return 7
                def __len__(self): return 8
                def __add__(self, other): return 'after add'
            class Example(Before): pass
            class Child(Example): pass
            instance = Child()
            saved_method = instance.value
            def read_value(value): return value.value()
            def read_length(value): return len(value)
            for index in range(100):
                read_value(instance)
                read_length(instance)
                instance + 1
            print(read_value(instance), read_length(instance), instance + 1)
            Example.__bases__ = (After,)
            print(read_value(instance), read_length(instance), instance + 1)
            print(saved_method(), instance.value(), Child().value())
            """
        );

    [Fact]
    public Task RebasingAnExistingExceptionChangesPreparedMappingKeyErrorFallback() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Missing(Exception): pass
            failure = Missing('missing')
            class Namespace:
                def __getitem__(self, key): raise failure
                def __setitem__(self, key, value): pass
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases): return Namespace()
                def __new__(meta, name, bases, namespace):
                    return type.__new__(meta, name, bases, {})
            try:
                class Failed(metaclass=Meta): pass
            except Exception as error: print(error is failure, 'Failed' in globals())
            Missing.__bases__ = (KeyError,)
            class Recovered(metaclass=Meta): pass
            print(Recovered.__name__, isinstance(failure, KeyError))
            try: raise failure
            except KeyError as error: print(error is failure)
            """
        );

    [Fact]
    public Task ChangingBasesUpdatesExistingInstancesSuperAndDescendants() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Before:
                def value(self): return 'before'
            class After:
                def value(self): return 'after'
            class Example(Before):
                def value(self): return ['example', super().value()]
            class Child(Example): pass
            instance = Child()
            print(instance.value(), isinstance(instance, Before), isinstance(instance, After))
            bases = (After,)
            Example.__bases__ = bases
            print(instance.value(), isinstance(instance, Before), isinstance(instance, After))
            print(Example.__bases__ is bases, Example.__base__ is After)
            print(Child.__mro__ == (Child, Example, After, object))
            """
        );

    [Fact]
    public Task DirectBasesDescriptorBypassesMetaclassProperty() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Before: pass
            class After: pass
            class Meta(type):
                @property
                def __bases__(cls): return 'override'
                @__bases__.setter
                def __bases__(cls, value): events.append('property setter')
            class Example(Before, metaclass=Meta): pass
            Example.__bases__ = (After,)
            descriptor = type.__dict__['__bases__']
            print(events, descriptor.__get__(Example) == (Before,))
            descriptor.__set__(Example, (After,))
            print(Example.__bases__, descriptor.__get__(Example) == (After,))
            print(Example.__base__ is After, issubclass(Example, After))
            """
        );

    [Fact]
    public Task DescendantMroCallbacksVisitDiamondsDepthFirstWithRepeatedNodes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Before: pass
            class After: pass
            class Meta(type):
                def mro(cls):
                    events.append(cls.__name__)
                    return type.mro(cls)
            class Root(Before, metaclass=Meta): pass
            class Left(Root): pass
            class Right(Root): pass
            class Diamond(Left, Right): pass
            events.clear()
            Root.__bases__ = (After,)
            print(events)
            print(Diamond.__mro__ == (Diamond, Left, Right, Root, After, object))
            """
        );

    [Fact]
    public Task BaseChangesDoNotRepeatConstructionHooks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Before:
                def __init_subclass__(cls): events.append('before subclass')
            class After:
                def __init_subclass__(cls): events.append('after subclass')
            class Descriptor:
                def __set_name__(self, owner, name): events.append('set name')
            class Meta(type):
                @classmethod
                def __prepare__(meta, name, bases):
                    events.append('prepare')
                    return {}
                def __new__(meta, name, bases, namespace):
                    events.append('new')
                    return super().__new__(meta, name, bases, namespace)
                def __init__(cls, name, bases, namespace): events.append('init')
                def mro(cls):
                    events.append('mro')
                    return type.mro(cls)
            class Example(Before, metaclass=Meta): descriptor = Descriptor()
            events.clear()
            Example.__bases__ = (After,)
            print(events, issubclass(Example, After))
            """
        );

    [Fact]
    public Task ChangingBasesPreservesMetaclassDespiteNewBaseMetaclassConflict() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class FirstMeta(type): pass
            class SecondMeta(type): pass
            class Before(metaclass=FirstMeta): pass
            class After(metaclass=SecondMeta): pass
            class Example(Before): pass
            Example.__bases__ = (After,)
            print(type(Example) is FirstMeta, issubclass(Example, After))
            print(isinstance(Example, SecondMeta), Example.__mro__ == (Example, After, object))
            """
        );

    [Fact]
    public Task InvalidBasesAndCyclesLeaveTheOriginalTupleAndMroUntouched() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Base: pass
            class Example(Base): pass
            class Child(Example): pass
            original_bases = Example.__bases__
            original_mro = Example.__mro__
            for value in ([], (), (42,), (Base, Base), (Example,), (Child,)):
                try: Example.__bases__ = value
                except TypeError as error: print(str(error))
                print(Example.__bases__ is original_bases, Example.__mro__ is original_mro)
            """
        );

    [Fact]
    public Task OrdinaryClassesCannotCrossBuiltinObjectAllocationBoundaries() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Base: pass
            class Example(Base): pass
            for candidate in (object, int, list):
                try: Example.__bases__ = (candidate,)
                except TypeError as error: print(str(error))
            class Plain: pass
            try: Plain.__bases__ = (Base,)
            except TypeError as error: print(str(error))
            try: int.__bases__ = (Base,)
            except TypeError as error: print(str(error))
            print(Example.__base__ is Base, Plain.__base__ is object)
            """
        );

    [Fact]
    public Task MetaclassHeapBasesCanExchangeWithBuiltinType() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class First(type): pass
            class Second(type): pass
            class Meta(First): pass
            for candidate in (Second, type, First):
                Meta.__bases__ = (candidate,)
                print(Meta.__base__ is candidate, issubclass(Meta, type))
                Created = Meta('Created', (), {})
                print(type(Created) is Meta)
            for candidate in (object, Exception):
                try: Meta.__bases__ = (candidate,)
                except TypeError as error: print(str(error))
            """
        );

    [Fact]
    public Task ExceptionBaseAssignmentsRespectBuiltinAndHeapLayouts() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Error(Exception): pass
            instance = Error('message')
            Error.__bases__ = (ValueError,)
            print(isinstance(instance, ValueError), instance.args)
            for candidate in (OSError, StopIteration, SystemExit):
                try: Error.__bases__ = (candidate,)
                except TypeError as error: print(str(error))
            class First(Exception): pass
            class Second(Exception): pass
            class HeapError(First): pass
            HeapError.__bases__ = (Second,)
            print(HeapError.__base__ is Second)
            for candidate in (Exception, ValueError):
                try: HeapError.__bases__ = (candidate,)
                except TypeError as error: print(str(error))
            HeapError.__bases__ = (First,)
            print(HeapError.__base__ is First)
            """
        );

    [Fact]
    public Task DescendantC3FailureRollsBackEarlierMroUpdates() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class X: pass
            class Y: pass
            class Left(X, Y): pass
            class Right(X, Y): pass
            class Diamond(Left, Right): pass
            old_bases = Left.__bases__
            old_left = Left.__mro__
            old_diamond = Diamond.__mro__
            try: Left.__bases__ = (Y, X)
            except TypeError: print('C3 conflict')
            print(Left.__bases__ is old_bases, Left.__mro__ is old_left)
            print(Diamond.__mro__ is old_diamond, Left.__base__ is X)
            """
        );

    [Fact]
    public Task FailedCustomMroRestoresHierarchyButPreservesCallbackSideEffects() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            active = False
            failure = ValueError('mro failed')
            events = []
            class Before: pass
            class After: pass
            class Meta(type):
                def mro(cls):
                    if active:
                        events.append((cls.__name__, cls.__bases__[0].__name__,
                                       [base.__name__ for base in cls.__mro__]))
                        cls.touched = True
                        if cls.__name__ == 'Child': raise failure
                    return type.mro(cls)
            class Example(Before, metaclass=Meta): pass
            class Child(Example): pass
            old_bases = Example.__bases__
            old_mro = Example.__mro__
            old_child = Child.__mro__
            active = True
            try: Example.__bases__ = (After,)
            except ValueError as error: print(error is failure)
            print(events)
            print(Example.__bases__ is old_bases, Example.__mro__ is old_mro)
            print(Child.__mro__ is old_child, Example.touched, Child.touched)
            active = False
            Example.__bases__ = (After,)
            print(Child.__mro__ == (Child, Example, After, object))
            """
        );

    [Fact]
    public Task ReentrantBaseAssignmentWinsWithoutRepeatingDescendantCallbacks() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            active = False
            events = []
            class Before: pass
            class Outer: pass
            class Inner: pass
            class Meta(type):
                def mro(cls):
                    global active
                    if active and cls.__name__ == 'Example':
                        active = False
                        events.append(('outer', cls.__bases__[0].__name__))
                        cls.__bases__ = (Inner,)
                        events.append(('after inner', cls.__bases__[0].__name__))
                    else:
                        events.append(('mro', cls.__name__, cls.__bases__[0].__name__))
                    return type.mro(cls)
            class Example(Before, metaclass=Meta): pass
            class Child(Example): pass
            events.clear()
            active = True
            Example.__bases__ = (Outer,)
            print(events)
            print(Example.__bases__ == (Inner,), Example.__base__ is Inner)
            print(Child.__mro__ == (Child, Example, Inner, object))
            """
        );

    [Fact]
    public Task SuccessfulInnerAssignmentSurvivesAnOuterCallbackFailure() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            active = False
            failure = ValueError('outer failed')
            class Before: pass
            class Outer: pass
            class Inner: pass
            class Meta(type):
                def mro(cls):
                    global active
                    if active and cls.__name__ == 'Example':
                        active = False
                        cls.__bases__ = (Inner,)
                        raise failure
                    return type.mro(cls)
            class Example(Before, metaclass=Meta): pass
            class Child(Example): pass
            active = True
            try: Example.__bases__ = (Outer,)
            except ValueError as error: print(error is failure)
            print(Example.__bases__ == (Inner,), Example.__base__ is Inner)
            print(Child.__mro__ == (Child, Example, Inner, object))
            """
        );

    [Fact]
    public Task AssigningTheSameBasesTupleStillRecomputesDescendantMros() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Base: pass
            class Meta(type):
                def mro(cls):
                    events.append(cls.__name__)
                    return type.mro(cls)
            class Example(Base, metaclass=Meta): pass
            class Child(Example): pass
            original = Example.__bases__
            events.clear()
            Example.__bases__ = original
            print(events, Example.__bases__ is original)
            """
        );

    [Fact]
    public Task LaterBaseMutationsFollowUpdatedSubclassRelationships() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            events = []
            class Root: pass
            class Alternate: pass
            class Meta(type):
                def mro(cls):
                    events.append(cls.__name__)
                    return type.mro(cls)
            class Before(Root, metaclass=Meta): pass
            class After(Root, metaclass=Meta): pass
            class Example(Before): pass
            class Child(Example): pass
            Example.__bases__ = (After,)
            events.clear()
            Before.__bases__ = (Alternate,)
            print(events)
            events.clear()
            After.__bases__ = (Alternate,)
            print(events)
            print(Child.__mro__ == (Child, Example, After, Alternate, object))
            """
        );

    [Fact]
    public Task CustomMroMayAcceptDuplicateAssignedBases() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Before: pass
            class After: pass
            class Meta(type):
                def mro(cls): return [cls, cls.__bases__[0], object]
            class Example(Before, metaclass=Meta): pass
            assigned = (After, After)
            Example.__bases__ = assigned
            print(Example.__bases__ is assigned, Example.__base__ is After)
            print(Example.__mro__ == (Example, After, object))
            try: type.mro(Example)
            except TypeError as error: print(str(error))
            """
        );
}
