using Xunit;

namespace DotPython.DifferentialTests;

public sealed class SlotStorageCompatibilityTests
{
    [Fact]
    public Task UndeclaredAttributesAreRejectedOnSlottedClasses() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class C:
                __slots__ = ()
            instance = C()
            try:
                instance.x = 1
            except AttributeError as error:
                print('AttributeError:', error)
            print(hasattr(instance, 'x'))
            """
        );

    [Fact]
    public Task DeclaredMembersRoundTripAcrossBasesAndSubclasses() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Plain: pass
            class Slotted(Plain):
                __slots__ = ('y',)
            class Strict(object):
                __slots__ = ()
            class StrictChild(Strict):
                __slots__ = ('z',)
            class DictSlots(object):
                __slots__ = ('w', '__dict__')
            def attempt(label, instance, name):
                try:
                    setattr(instance, name, 1)
                    print(label, 'accepts', name)
                except AttributeError:
                    print(label, 'rejects', name)
            attempt('plain', Plain(), 'free')
            attempt('slotted', Slotted(), 'free')
            attempt('strict', Strict(), 'free')
            attempt('strict child', StrictChild(), 'z')
            attempt('strict child', StrictChild(), 'free')
            attempt('dict slots', DictSlots(), 'free')
            print('slotted dict:', hasattr(Slotted(), '__dict__'))
            print('strict dict:', hasattr(Strict(), '__dict__'))
            print('slotted member:', hasattr(Slotted(), 'y'), 'strict member:', hasattr(Strict(), 'z'))
            """
        );

    [Fact]
    public Task SlotDeclarationFormsAndManglingFollowCpython() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class Single:
                __slots__ = 'xy'
            single = Single()
            single.xy = 1
            print(single.xy)
            try:
                single.x = 1
            except AttributeError:
                print('rejects x')

            class Pair:
                __slots__ = ['ab', 'c']
            pair = Pair()
            pair.ab = 1
            pair.c = 2
            print(pair.ab, pair.c)

            class Mapping:
                __slots__ = {'m': 'doc'}
            mapping = Mapping()
            mapping.m = 3
            print(mapping.m)

            class Private:
                __slots__ = ('__hidden',)
            private = Private()
            private._Private__hidden = 4
            print(private._Private__hidden)

            class Repeated:
                __slots__ = ('dup', 'dup')
            repeated = Repeated()
            repeated.dup = 5
            print(repeated.dup)
            """
        );

    [Fact]
    public Task SlotClassesHideAndDeleteTheInstanceDictionary() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            class C:
                __slots__ = ('x',)
            instance = C()
            for label, action in (
                ('read', lambda: instance.__dict__),
                ('write', lambda: setattr(instance, '__dict__', {})),
                ('delete', lambda: delattr(instance, '__dict__')),
            ):
                try:
                    action()
                    print(label, 'allowed')
                except AttributeError as error:
                    print(label, 'AttributeError:', error)
            try:
                vars(instance)
            except TypeError as error:
                print('TypeError:', error)
            instance.x = 1
            del instance.x
            try:
                instance.x
            except AttributeError as error:
                print('deleted:', error)
            """
        );

    [Fact]
    public Task InvalidSlotDeclarationsRaiseTheSameExceptionTypes() =>
        CompatibilityOracle.AssertMatchesAsync(
            """
            for declaration in ((1,), 5, ('not an identifier',), ('__slots__',)):
                try:
                    class C:
                        __slots__ = declaration
                    print('accepted', declaration)
                except Exception as error:
                    print(type(error).__name__, error)
            try:
                class Conflict:
                    __slots__ = ('x',)
                    x = 1
            except Exception as error:
                print(type(error).__name__, error)
            """
        );
}
