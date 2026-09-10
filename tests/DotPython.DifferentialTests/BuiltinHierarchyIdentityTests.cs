using Xunit;

namespace DotPython.DifferentialTests;

public sealed class BuiltinHierarchyIdentityTests
{
    [Fact]
    public Task BuiltinAndExceptionHierarchyReadsRetainTupleIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            represented = (object, type, int, bool, str, bytes, complex, float,
                list, tuple, dict, set, frozenset, range, enumerate, map, filter,
                zip, reversed, slice, property, staticmethod, classmethod, super,
                BaseException, Exception, ArithmeticError, LookupError, ValueError,
                TypeError, KeyError, StopIteration, BaseExceptionGroup, ExceptionGroup)
            for cls in represented:
                bases, mro = cls.__bases__, cls.__mro__
                print(cls.__name__, type(bases).__name__, type(mro).__name__,
                      bases is cls.__bases__, mro is cls.__mro__)
                print(type.__dict__['__bases__'].__get__(cls, type) is bases,
                      type.__dict__['__mro__'].__get__(cls, type) is mro)
            print(bool.__bases__ == (int,))
            print(ExceptionGroup.__bases__ == (BaseExceptionGroup, Exception))
            print(ExceptionGroup.__mro__ == (ExceptionGroup, BaseExceptionGroup, Exception, BaseException, object))
            print(int.__bases__ == str.__bases__, int.__bases__ is str.__bases__)
            """
        );

    [Fact]
    public Task OpaqueAndExportedModuleTypesRetainHierarchyTupleIdentity() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            from string.templatelib import Template, Interpolation
            from types import MappingProxyType
            def example(): pass
            values = (None, NotImplemented, Ellipsis, example, len, iter([]),
                      iter(()), iter('a'), iter(range(2)), iter({}), {}.keys(),
                      type.__dict__['__mro__'], type.__dict__['mro'])
            for value in values:
                cls = type(value)
                print(cls.__name__, cls.__bases__ is cls.__bases__, cls.__mro__ is cls.__mro__)
            for cls in (Template, Interpolation, MappingProxyType):
                print(cls.__name__, cls.__bases__ is cls.__bases__, cls.__mro__ is cls.__mro__)
            """
        );

    [Fact]
    public Task DefaultMroListsRemainIndependentOfStoredTuples() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for cls in (object, int, bool, ExceptionGroup):
                stored = cls.__mro__
                first = type.mro(cls)
                second = type.mro(cls)
                print(first == list(stored), first is second)
                first.clear()
                second.append(str)
                print(cls.__mro__ is stored, type.mro(cls) == list(stored))
            """
        );

    [Fact]
    public Task FailedBuiltinHierarchyMutationPreservesStoredTuples() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for cls in (object, int, bool, BaseExceptionGroup):
                bases, mro = cls.__bases__, cls.__mro__
                for name in ('__bases__', '__mro__'):
                    try: setattr(cls, name, (object,))
                    except (TypeError, AttributeError) as error: print(type(error).__name__)
                    try: type.__dict__[name].__set__(cls, (object,))
                    except (TypeError, AttributeError) as error: print(type(error).__name__)
                try: bases[0] = str
                except TypeError as error: print(type(error).__name__)
                try: mro[0] = str
                except TypeError as error: print(type(error).__name__)
                print(cls.__bases__ is bases, cls.__mro__ is mro)
            """
        );

    [Fact]
    public Task ManagedRebasingReplacesOnlyManagedHierarchyTuples() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Before: pass
            class After: pass
            class Child(Before): pass
            class Grandchild(Child): pass
            root_bases, root_mro = object.__bases__, object.__mro__
            old_bases, old_mro = Child.__bases__, Child.__mro__
            old_grand = Grandchild.__mro__
            Child.__bases__ = (After,)
            print(Child.__bases__ is old_bases, Child.__mro__ is old_mro,
                  Grandchild.__mro__ is old_grand)
            print(old_bases == (Before,), old_mro == (Child, Before, object))
            print(Child.__mro__ == (Child, After, object))
            print(Grandchild.__mro__ == (Grandchild, Child, After, object))
            print(object.__bases__ is root_bases, object.__mro__ is root_mro)
            """
        );
}
